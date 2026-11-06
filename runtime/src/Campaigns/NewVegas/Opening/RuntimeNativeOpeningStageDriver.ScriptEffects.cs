using System.Globalization;
using Godot;
using OpenNV.Runtime.Content;
using OpenNV.Runtime.Gameplay.State;
using OpenNV.Runtime.World.Actors;
using OpenNV.Runtime.Presentation.Rendering;
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
                var spell = bindings.Form(arguments[0]).FormKey;
                try
                {
                    if (player) _ = _ingestibles.PrepareActorEffect(spell).Commit();
                    else _ = _scripts.References!.CastActorEffectOnSelf(target, spell, SourcePlayerLevel, _globals, _scripts.Session.Hardcore);
                }
                catch (NotSupportedException error)
                {
                    // Spell types, scripted and presentation effects have no
                    // staged actor lifecycle yet; nothing was applied.
                    FalloutScriptCommandDeferral.Warn("castimmediateonself", $"actor-effect-unbound:{error.Message}");
                }
                return true;
            case "dispel" when player && arguments.Count == 1:
                var dispelled = bindings.Form(arguments[0]).FormKey;
                _ = _ingestibles.Dispel(item => item == dispelled);
                return true;
            case "dispelallspells" when player && arguments.Count == 0:
                _ = _ingestibles.Dispel(item => FalloutScriptEffectCommands.DispelledByDispelAll(_pluginStack, item));
                return true;
            // NPC alpha is published by the reference presentation from the
            // world; only the player's own 3D is applied here.
            case "saa" or "setactoralpha" when arguments.Count == 1:
                if (player) _player.SetScriptAlpha(float.Parse(arguments[0], NumberStyles.Float, CultureInfo.InvariantCulture));
                return true;
            // Membranes attach to the resident presentation only; a reference
            // that streams in later does not replay a retained shader yet.
            case "pms" or "playmagicshadervisuals" when arguments.Count is 1 or 2:
                var effect = FalloutEffectShader.Read(_pluginStack, bindings.Form(arguments[0]).FormKey);
                float? duration = arguments.Count == 2 ? float.Parse(arguments[1], NumberStyles.Float, CultureInfo.InvariantCulture) : null;
                var content = RuntimeLiveContentSource.Current ?? throw new InvalidOperationException("Owned source is absent.");
                var attached = 0;
                foreach (var node in ResidentPresentation(target))
                {
                    if (node.GetChildren().OfType<NativeEffectShaderMembrane>().Any(existing => existing.Shader == effect.Form && !existing.Stopping))
                        continue;
                    if (NativeEffectShaderMembrane.Attach(node, effect, content, duration) is not null) attached++;
                }
                if (!effect.HasMembrane) FalloutScriptCommandDeferral.Warn("playmagicshadervisuals", "effect-shader-particles-unbound");
                else if (attached == 0) FalloutScriptCommandDeferral.Warn("playmagicshadervisuals", "membrane-attachment-produced-no-geometry");
                return true;
            case "sms" or "stopmagicshadervisuals" when arguments.Count == 1:
                var stopped = bindings.Form(arguments[0]).FormKey;
                foreach (var node in ResidentPresentation(target))
                    foreach (var membrane in node.GetChildren().OfType<NativeEffectShaderMembrane>().Where(item => item.Shader == stopped))
                        membrane.Stop();
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

    // Resident presentation roots of a placed actor or object reference.
    private IEnumerable<Node3D> ResidentPresentation(FalloutFormKey reference)
    {
        if (_pluginStack.RuntimeFormId(reference) == 0x14) return _player.SelfPresentation.ToArray();
        var key = reference.ToString();
        return SceneGraph.NodeTraversal.SelfAndDescendants<Node3D>(GetTree().Root).Where(node =>
            node.IsInsideTree() && !node.IsQueuedForDeletion() && node switch
            {
                RuntimeNativeNpc npc => npc.Appearance.Reference == reference,
                RuntimeNativeCreature creature => creature.Appearance.Reference == reference,
                _ => node.HasMeta("opennv_reference_form_key") && node.GetMeta("opennv_reference_form_key").AsString() == key,
            }).ToArray();
    }
}
