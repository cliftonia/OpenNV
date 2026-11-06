using System.Globalization;
using OpenNV.Runtime.Content;
using OpenNV.Runtime.Gameplay.State;
using OpenNV.Runtime.World.Actors;

namespace OpenNV.Runtime.World.Cells;

// A reached command whose state is retained but whose presentation or consumer
// owner does not exist yet. The script continues; the gap stays visible once.
// Magic, effect, visual, weather and clock commands shared by quest and object
// scripts. World-owned state is applied here; the returned arguments, already
// resolved to literals, are the remainder owned by the native session.
internal static class FalloutScriptEffectCommands
{
    private static readonly HashSet<string> Operations =
    [
        "cios", "castimmediateonself", "addspell", "removespell", "dispel", "dispelallspells",
        "pms", "playmagicshadervisuals", "sms", "stopmagicshadervisuals", "saa", "setactoralpha",
        "preloadmagiceffect", "triggerscreensplatter", "setscreensplatterfade", "setweather", "sw",
        "setcellimagespace", "setglobaltimemultiplier", "sgtm", "setrumble", "setsecexp", "setsecuritronexpression",
    ];

    internal static bool Supports(string operation) => Operations.Contains(operation);

    internal static IReadOnlyList<string>? Execute(FalloutPluginStack records, FalloutReferenceWorld world, FalloutFormKey target,
        string operation, bool global, IReadOnlyList<string> arguments, FalloutScriptEffectResolvers resolve)
    {
        var player = records.RuntimeFormId(target) == 0x14;
        switch (operation)
        {
            // Casting on self needs no aim or animation; the active-effect owner
            // of the caster receives the spell's effects at once.
            case "cios" or "castimmediateonself" when arguments.Count == 1:
                _ = Require(resolve.Form(arguments[0]), "SPEL");
                return [arguments[0]];
            case "addspell" or "removespell" when arguments.Count == 1:
                world.ChangeScriptSpell(target, Require(resolve.Form(arguments[0]), "SPEL"), operation == "addspell");
                FalloutScriptCommandDeferral.Warn(operation, "spell-list-retained;ability-consumers-read-source-SPLO-only");
                return null;
            case "dispel" when arguments.Count == 1:
                var dispelled = resolve.Form(arguments[0]);
                if (dispelled.Signature is not ("SPEL" or "ALCH" or "INGR"))
                    throw new InvalidDataException("Dispel source is not an actor effect or ingestible.");
                if (player) return [arguments[0]];
                _ = world.DispelActorEffects(target, dispelled.FormKey);
                return null;
            case "dispelallspells" when arguments.Count == 0:
                if (player) return [];
                _ = world.DispelActorEffects(target, null);
                return null;
            case "pms" or "playmagicshadervisuals" when arguments.Count is 1 or 2:
                var shader = Require(resolve.Form(arguments[0]), "EFSH");
                if (arguments.Count == 2 && !double.IsFinite(resolve.Number(arguments[1])))
                    throw new InvalidDataException("Magic shader duration is not finite.");
                world.SetMagicShader(target, shader, true);
                return arguments.Count == 1 ? [arguments[0]] : [arguments[0], Literal(resolve.Number(arguments[1]))];
            case "sms" or "stopmagicshadervisuals" when arguments.Count == 1:
                world.SetMagicShader(target, Require(resolve.Form(arguments[0]), "EFSH"), false);
                return [arguments[0]];
            case "saa" or "setactoralpha" when arguments.Count == 1:
                var alpha = resolve.Number(arguments[0]);
                if (!double.IsFinite(alpha)) throw new InvalidDataException("Actor alpha is not finite.");
                return [world.SetActorAlpha(target, (float)alpha).ToString("R", CultureInfo.InvariantCulture)];
            // Preloading only warms the editor's resource cache. Owned records
            // and assets are already resolved on first use.
            case "preloadmagiceffect" when global && arguments.Count == 1:
                _ = Require(resolve.Form(arguments[0]), "MGEF");
                return null;
            case "triggerscreensplatter" when global && arguments.Count <= 4:
            case "setscreensplatterfade" when global && arguments.Count is 1 or 2:
                if (arguments.Select(resolve.Number).Any(value => !double.IsFinite(value)))
                    throw new InvalidDataException("Screen splatter arguments are not finite.");
                FalloutScriptCommandDeferral.Warn(operation, "screen-splatter-presentation-unbound");
                return null;
            case "setweather" or "sw" when global && arguments.Count is 1 or 2:
                _ = Require(resolve.Form(arguments[0]), "WTHR");
                return arguments.Count == 1 ? [arguments[0]] : [arguments[0], Literal(resolve.Number(arguments[1]))];
            case "setcellimagespace" when global && arguments.Count == 2:
                world.SetCellImageSpace(Require(resolve.Form(arguments[0]), "CELL"), Require(resolve.Form(arguments[1]), "IMGS"));
                FalloutScriptCommandDeferral.Warn(operation, "override-applies-on-next-cell-load");
                return null;
            case "setglobaltimemultiplier" or "sgtm" when global && arguments.Count is 1 or 2:
                var multiplier = resolve.Number(arguments[0]);
                if (!double.IsFinite(multiplier) || multiplier <= 0)
                    throw new InvalidDataException("Global time multiplier must be finite and positive.");
                if (arguments.Count == 2 && resolve.Number(arguments[1]) is not (0 or 1))
                    throw new InvalidDataException("Global time multiplier player flag must be zero or one.");
                return [Literal(multiplier)];
            case "setrumble" when global && arguments.Count == 3:
                var rumble = arguments.Select(resolve.Number).ToArray();
                if (rumble.Any(value => !double.IsFinite(value) || value < 0))
                    throw new InvalidDataException("Rumble motor strengths and duration must be finite and non-negative.");
                return rumble.Select(Literal).ToArray();
            // Personality and emotion are plain strings, not forms. A reference
            // without loaded 3D ignores the command; otherwise the pair is kept
            // on the reference and its resident screen re-applies it.
            case "setsecuritronexpression" or "setsecexp" when global && arguments.Count == 3:
                var securitron = resolve.Reference(arguments[0]);
                if (records.GetEffective(securitron).Signature is not ("ACHR" or "ACRE"))
                    throw new InvalidDataException("Securitron expression target is not a placed actor.");
                var personality = FalloutSecuritronFace.Word(PlainString(arguments[1]));
                var emotion = FalloutSecuritronFace.Word(PlainString(arguments[2]));
                if (world.IsResident(securitron)) world.SetSecuritronFace(securitron, personality, emotion);
                return null;
            default:
                throw new InvalidDataException($"{operation} has an invalid target or argument count ({arguments.Count}).");
        }
    }

    private static FalloutFormKey Require(FalloutPluginRecord record, string signature) => record.Signature == signature
        ? record.FormKey : throw new InvalidDataException($"Script effect argument is {record.Signature}, not {signature}.");

    private static string PlainString(string argument) =>
        argument.Length >= 2 && argument[0] == '"' && argument[^1] == '"' ? argument[1..^1] : argument;

    private static string Literal(double value) => value.ToString("R", CultureInfo.InvariantCulture);
}

internal sealed record FalloutScriptEffectResolvers(Func<string, FalloutPluginRecord> Form,
    Func<string, double> Number, Func<string, FalloutFormKey> Reference);

internal sealed partial class FalloutReferenceWorld
{
    private readonly Dictionary<FalloutFormKey, float> _actorAlpha = [];
    private readonly HashSet<(FalloutFormKey Reference, FalloutFormKey Shader)> _magicShaders = [];
    private readonly Dictionary<FalloutFormKey, FalloutFormKey> _cellImageSpaces = [];

    // AddSpell/RemoveSpell are retained beside perks: one is a grant, zero
    // revokes a source SPLO entry. Order of later changes wins.
    internal void ChangeScriptSpell(FalloutFormKey reference, FalloutFormKey spell, bool add)
    {
        var current = Overrides(reference);
        var entry = FormOverride(spell, "SPEL", add ? 1 : 0);
        _actorOverrides[reference] = current with { Spells = (current.Spells ?? []).Where(item => item.Form != spell).Append(entry).ToArray() };
    }

    internal bool? ScriptSpell(FalloutFormKey reference, FalloutFormKey spell) =>
        _actorOverrides.GetValueOrDefault(reference)?.Spells?.SingleOrDefault(item => item.Form == spell) is { } entry ? entry.Value == 1 : null;

    // Ends matching timed actor effects early. Reversible modifiers are
    // returned exactly as at natural expiry; applied increments remain.
    internal int DispelActorEffects(FalloutFormKey reference, FalloutFormKey? source)
    {
        if (records.RuntimeFormId(reference) == 0x14) throw new InvalidOperationException("The engine player has its own active-effect owner.");
        var actor = Actor(reference);
        if (actor.Ingestibles is not { } before) return 0;
        if (before.Failure is not null) throw new NotSupportedException($"Actor active effects already failed: {before.Failure}");
        var removed = before.Effects.Where(timer => source is null || timer.Item == source).ToArray();
        if (removed.Length == 0) return 0;
        var values = new Dictionary<string, FalloutActorValue>(actor.ActorValues);
        foreach (var timer in removed)
        {
            var effect = ActorIngestible(timer.Item).Effects[timer.Index];
            if (!FalloutIngestibleApplications.Reversible(effect)) continue;
            var slot = FalloutActorValue.SourceSlot(effect.ActorValue); var value = values[slot];
            values[slot] = value with { Temporary = value.Temporary - timer.Magnitude };
            if (!values[slot].IsFinite) throw new InvalidDataException("Dispelled actor effect is not finite.");
        }
        PublishActorIngestibleValues(actor, values);
        actor.Ingestibles = new(before.Effects.Where(timer => source is not null && timer.Item != source).ToArray());
        return removed.Length;
    }

    // Session state only: presentation reapplies it while the actor is resident.
    internal float SetActorAlpha(FalloutFormKey reference, float alpha)
    {
        _ = Overrides(reference);
        var clamped = Math.Clamp(alpha, 0, 1);
        if (clamped == 1) _actorAlpha.Remove(reference); else _actorAlpha[reference] = clamped;
        return clamped;
    }

    internal float ActorAlpha(FalloutFormKey reference) => _actorAlpha.GetValueOrDefault(reference, 1);

    internal void SetMagicShader(FalloutFormKey reference, FalloutFormKey shader, bool play)
    {
        if (records.RuntimeFormId(reference) != 0x14 && records.GetEffective(reference).Signature is not ("REFR" or "ACHR" or "ACRE"))
            throw new InvalidDataException("Magic shader target is not a placed reference.");
        if (play) _magicShaders.Add((reference, shader)); else _magicShaders.Remove((reference, shader));
    }

    internal IReadOnlyList<FalloutFormKey> MagicShaders(FalloutFormKey reference) =>
        _magicShaders.Where(item => item.Reference == reference).Select(item => item.Shader).ToArray();

    internal void SetCellImageSpace(FalloutFormKey cell, FalloutFormKey imageSpace) => _cellImageSpaces[cell] = imageSpace;

    internal FalloutImageSpace? CellImageSpace(FalloutFormKey cell) => _cellImageSpaces.TryGetValue(cell, out var image)
        ? FalloutImageSpaceReader.Read(records.GetEffective(image)) : FalloutImageSpaceReader.ForCell(records, cell);
}
