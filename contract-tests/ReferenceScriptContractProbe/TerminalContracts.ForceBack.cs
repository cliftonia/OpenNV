using OpenNV.Runtime.Content;
using OpenNV.Runtime.Gameplay.State;
using OpenNV.Runtime.World.Cells;

internal static partial class TerminalContracts
{
    // ForceTerminalBack requested inside a result fragment returns after that
    // selection completes; outside a fragment it applies to the active page.
    internal static void RunForceBack()
    {
        var directory = Path.Combine(Path.GetTempPath(), "opennv-terminal-back-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllBytes(Path.Combine(directory, "TerminalBase.esm"), Base());
            File.WriteAllBytes(Path.Combine(directory, "TerminalNotes.esm"), Notes());
            File.WriteAllBytes(Path.Combine(directory, "TerminalPatch.esp"), Patch());
            using var records = FalloutPluginStack.Load(directory, ["TerminalBase.esm", "TerminalNotes.esm", "TerminalPatch.esp"]);
            using var world = World(records);
            var quests = new FalloutQuestState(records);
            var effects = new List<FalloutReferenceScriptEffect>();
            var states = new Dictionary<FalloutFormKey, int> { [A(0x91)] = 3, [A(0x92)] = 3, [A(0x90)] = 0 };
            var scripts = Scripts(records, world, quests, effects, states);
            var conditions = new FalloutTerminalConditions(records, quests, world, reference => states[reference]);
            var forceInside = false;
            FalloutTerminalMenu? menu = null;
            menu = new(records, A(0x90), reference =>
            {
                if (!world.IsResident(reference) || !world.CanActivate(reference) || world.GetLocked(reference) != 0)
                    throw new InvalidOperationException("Synthetic terminal is not resident, available and unlocked.");
            }, conditions.Evaluate, selection =>
            {
                scripts.ExecuteTerminalResult(selection);
                if (forceInside) menu!.ForceBack();
            });

            forceInside = true;
            menu.Select(5, menu.Generation);
            Require(!menu.HasResult && menu.DisplayText.Length == 0 && menu.CurrentPage.Record.FormKey == A(0x20) &&
                menu.LastReceipt?.State == FalloutTerminalSelectionState.Succeeded,
                "ForceTerminalBack inside a result did not return to its page after the selection.");
            menu.Select(1, menu.Generation);
            Require(menu.CurrentPage.Record.FormKey == A(0x20) && !menu.HasResult,
                "ForceTerminalBack inside a submenu selection entered the submenu.");

            forceInside = false;
            menu.Select(5, menu.Generation);
            Require(menu.HasResult, "Ordinary result display was suppressed without ForceTerminalBack.");
            menu.ForceBack();
            Require(!menu.HasResult && menu.CurrentPage.Record.FormKey == A(0x20),
                "ForceTerminalBack outside a result did not leave the result display.");
            menu.Close();
            Reject(menu.ForceBack);
        }
        finally { Directory.Delete(directory, true); }
    }
}
