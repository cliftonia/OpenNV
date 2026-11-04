using System.Globalization;
using Godot;
using OpenNV.Runtime.Content;
using OpenNV.Runtime.Gameplay.State;

namespace OpenNV.Runtime.Campaigns.NewVegas.Opening;

internal partial class RuntimeNativeOpeningStageDriver
{
    // EnableLoadingMenu 1 makes the next scripted player move show the loading
    // menu even inside one cell. Source callers move the player in the same
    // script block, so the request is frame-transient and never saved.
    internal bool LoadingMenuForced { get; private set; }

    internal bool ConsumeForcedLoadingMenu()
    {
        var forced = LoadingMenuForced;
        LoadingMenuForced = false;
        return forced;
    }

    // Character re-editing and perk rewards reuse the character-creation
    // owners: the SPECIAL tester for a full allocation, the SPECIAL book for
    // additive points, the tag menu for extra tags, and the shared XP owner.
    private bool TryApplyProgressionCommand(string[] parts, string operation, IReadOnlyList<string> arguments)
    {
        if (parts.Length != 1) return false;
        switch (operation)
        {
            case "setspecialpoints":
                OpenVigorMenu(PositiveCount(operation, arguments));
                return true;
            case "addspecialpoints":
                OpenSpecialBookMenu(PositiveCount(operation, arguments));
                return true;
            case "addtagskills":
                var added = PositiveCount(operation, arguments);
                if (_tagSkillEntry is not null) throw new InvalidOperationException("A tag menu already owns player input.");
                var total = Math.Min(FalloutPlayerTagSkills.SlotCount, _tagSkills.Selection.Count + added);
                if (total == _tagSkills.Selection.Count)
                {
                    GD.PushWarning($"OPENNV_ADD_TAG_SKILLS_FULL tagged={total}");
                    return true;
                }
                _tagMenuRequest = new(total, true);
                SynchronizeTagSkillEntry(sourceRequested: true);
                if (_tagSkillEntry is null) throw new NotSupportedException("Tag input has no active menu owner.");
                return true;
            case "enableloadingmenu":
                if (arguments.Count != 1 || arguments[0] is not ("0" or "1"))
                    throw new InvalidDataException("EnableLoadingMenu requires a zero/one flag.");
                LoadingMenuForced = arguments[0] == "1";
                return true;
            case "resetxp":
                if (arguments.Count != 0) throw new InvalidDataException("ResetXP takes no arguments.");
                _experience.Reset();
                GD.Print($"OPENNV_PLAYER_XP_RESET level={_vitals.State.Level}");
                return true;
            default:
                return false;
        }
    }

    private static int PositiveCount(string operation, IReadOnlyList<string> arguments)
    {
        if (arguments.Count != 1 || !int.TryParse(arguments[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var count) ||
            count < 1)
            throw new InvalidDataException($"{operation} requires one positive integer.");
        return count;
    }
}
