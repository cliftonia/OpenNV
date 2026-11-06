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
            case "castimmediateonself" when arguments.Count == 1:
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
            case "setactoralpha" when arguments.Count == 1:
                if (player) _player.SetScriptAlpha(float.Parse(arguments[0], NumberStyles.Float, CultureInfo.InvariantCulture));
                return true;
            // Effect shaders attach to the resident presentation only; a
            // reference that streams in later does not replay a retained shader yet.
            case "playmagicshadervisuals" when arguments.Count is 1 or 2:
                var effect = FalloutEffectShader.Read(_pluginStack, bindings.Form(arguments[0]).FormKey);
                float? duration = arguments.Count == 2 ? float.Parse(arguments[1], NumberStyles.Float, CultureInfo.InvariantCulture) : null;
                var content = RuntimeLiveContentSource.Current ?? throw new InvalidOperationException("Owned source is absent.");
                var mode = EffectShaderEmitMode(target);
                int resident = 0, attached = 0, playing = 0;
                foreach (var node in ResidentPresentation(target))
                {
                    resident++;
                    if (node.GetChildren().OfType<NativeEffectShader>().Any(existing => existing.Shader == effect.Form && !existing.Releasing))
                    {
                        playing++;
                        continue;
                    }
                    NativeEffectShader? shader;
                    try { shader = NativeEffectShader.Attach(node, effect, content, _pluginStack, duration, mode, _player.UnitsToMeters); }
                    catch (NotSupportedException error)
                    {
                        FalloutScriptCommandDeferral.Warn($"playmagicshadervisuals:{effect.Form}", error.Message);
                        continue;
                    }
                    if (shader is null) continue;
                    attached++;
                    foreach (var reason in shader.Deferrals) FalloutScriptCommandDeferral.Warn($"playmagicshadervisuals:{reason}", reason);
                }
                // Two source fire shaders also own a looping fire sound.
                if (attached != 0 && effect.Form.OwnerPlugin.Equals("FalloutNV.esm", StringComparison.OrdinalIgnoreCase) && effect.Form.ObjectId is 0x0852FE or 0x084A51)
                    FalloutScriptCommandDeferral.Warn("playmagicshadervisuals:fire-loop", "effect-shader-fire-loop-sound-unbound");
                if (resident == 0) FalloutScriptCommandDeferral.Warn("playmagicshadervisuals", "target-not-resident");
                else if (attached + playing == 0) FalloutScriptCommandDeferral.Warn("playmagicshadervisuals", "effect-shader-attachment-produced-no-geometry");
                return true;
            case "stopmagicshadervisuals" when arguments.Count == 1:
                var stopped = bindings.Form(arguments[0]).FormKey;
                foreach (var node in ResidentPresentation(target))
                    foreach (var shader in node.GetChildren().OfType<NativeEffectShader>().Where(item => item.Shader == stopped))
                        shader.Stop();
                return true;
            // The sky owner commits weather without a transition clock, so the
            // ordinary transition request currently matches ForceWeather.
            case "setweather" when arguments.Count is 1 or 2:
                (_skyLighting ?? throw new InvalidOperationException("Sky state owner is absent."))
                    .ForceWeather(bindings.Form(arguments[0]).FormKey, FalloutSkyLightingState.ReadForceWeatherOverride(arguments));
                return true;
            case "setglobaltimemultiplier" when arguments.Count == 1:
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

    // Weapons emit along a line, actors and activators from their nodes, and
    // everything else from its mesh surfaces.
    private NativeEffectShaderEmitMode EffectShaderEmitMode(FalloutFormKey reference)
    {
        // A first-person player emits from its view-model surfaces.
        if (_pluginStack.RuntimeFormId(reference) == 0x14)
            return _player.ThirdPersonView ? NativeEffectShaderEmitMode.Skeleton : NativeEffectShaderEmitMode.Surface;
        if (_pluginStack.GetEffective(reference).Signature is "ACHR" or "ACRE") return NativeEffectShaderEmitMode.Skeleton;
        return _pluginStack.GetEffective(FalloutReferenceIdentity.Base(_pluginStack, reference)).Signature switch
        {
            "WEAP" => NativeEffectShaderEmitMode.Line,
            "ACTI" => NativeEffectShaderEmitMode.Skeleton,
            _ => NativeEffectShaderEmitMode.Surface,
        };
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
