using System.Globalization;
using Godot;
using OpenNV.Runtime.Content;
using OpenNV.Runtime.Gameplay.State;
using OpenNV.Runtime.World.Actors;
using OpenNV.Runtime.World.Cells;

namespace OpenNV.Runtime.Campaigns.NewVegas.Opening;

internal partial class RuntimeNativeOpeningStageDriver
{
    // The native remainder of FalloutScriptEffectCommands. Arguments arrive as
    // validated form names and invariant literals; target is the resolved caller.
    private bool ApplyNativeEffectCommand(FalloutFormKey target, FalloutScriptBindings bindings, string operation,
        IReadOnlyList<string> arguments)
    {
        var player = _pluginStack.RuntimeFormId(target) == 0x14;
        switch (operation)
        {
            case "cios" or "castimmediateonself" when arguments.Count == 1:
                if (!player)
                {
                    FalloutScriptCommandDeferral.Warn("castimmediateonself", "npc-actor-effect-owner-unbound");
                    return true;
                }
                try { _ = _ingestibles.PrepareActorEffect(bindings.Form(arguments[0]).FormKey).Commit(); }
                catch (NotSupportedException error)
                {
                    // Spell types, scripted and presentation effects have no
                    // staged actor lifecycle yet; value effects already applied.
                    FalloutScriptCommandDeferral.Warn("castimmediateonself", $"player-effect-unbound:{error.Message}");
                }
                return true;
            case "dispel" when player && arguments.Count == 1:
                var dispelled = bindings.Form(arguments[0]).FormKey;
                _ = _ingestibles.Dispel(item => item == dispelled);
                return true;
            case "dispelallspells" when player && arguments.Count == 0:
                _ = _ingestibles.Dispel(_ => true);
                return true;
            case "saa" or "setactoralpha" when arguments.Count == 1:
                var alpha = float.Parse(arguments[0], NumberStyles.Float, CultureInfo.InvariantCulture);
                if (player)
                {
                    FalloutScriptCommandDeferral.Warn("setactoralpha", "player-self-visibility-owner-unbound");
                    return true;
                }
                // Residency rebuilds presentation from source; the world retains
                // the alpha for the session. A non-resident actor has no geometry.
                foreach (var actor in SceneGraph.NodeTraversal.SelfAndDescendants<Node3D>(GetTree().Root).Where(node =>
                    node.IsInsideTree() && !node.IsQueuedForDeletion() && node switch
                    {
                        RuntimeNativeNpc npc => npc.Appearance.Reference == target,
                        RuntimeNativeCreature creature => creature.Appearance.Reference == target,
                        _ => false,
                    }))
                    foreach (var geometry in SceneGraph.NodeTraversal.SelfAndDescendants<GeometryInstance3D>(actor))
                        geometry.Transparency = 1 - alpha;
                return true;
            // The sky owner commits weather without a transition clock, so the
            // ordinary transition request currently matches ForceWeather.
            case "setweather" or "sw" when arguments.Count is 1 or 2:
                (_skyLighting ?? throw new InvalidOperationException("Sky state owner is absent."))
                    .ForceWeather(bindings.Form(arguments[0]).FormKey, FalloutSkyLightingState.ReadForceWeatherOverride(arguments));
                return true;
            case "setglobaltimemultiplier" or "sgtm" when arguments.Count == 1:
                Engine.TimeScale = double.Parse(arguments[0], NumberStyles.Float, CultureInfo.InvariantCulture);
                return true;
            // Left is the low-frequency motor; each call replaces the last.
            case "setrumble" when arguments.Count == 3:
                var rumble = arguments.Select(value => float.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture)).ToArray();
                foreach (var device in Input.GetConnectedJoypads())
                    Input.StartJoyVibration(device, Math.Min(rumble[1], 1), Math.Min(rumble[0], 1), rumble[2]);
                return true;
            default:
                return false;
        }
    }
}
