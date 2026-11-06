using System.Buffers.Binary;
using System.Globalization;
using OpenNV.Runtime.Content;
using OpenNV.Runtime.Gameplay.State;
using OpenNV.Runtime.World.Actors;

namespace OpenNV.Runtime.World.Cells;

// Magic, effect, visual, weather and clock commands shared by quest and object
// scripts. World-owned state is applied here; the returned arguments, already
// resolved to literals, are the remainder owned by the native session. A
// receiver of the wrong kind, or without loaded 3D where the engine requires
// it, makes the command a silent no-op that still succeeds.
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

    // PlayMagicShaderVisuals and SetActorAlpha without a calling reference
    // (a quest script, for example) act on the player.
    internal static FalloutFormKey Receiver(FalloutPluginStack records, FalloutFormKey target, string operation, bool global) =>
        global && operation is "pms" or "playmagicshadervisuals" or "saa" or "setactoralpha" &&
        records.RuntimeFormId(target) != 0x14 && records.GetEffective(target).Signature is not ("REFR" or "ACHR" or "ACRE")
            ? records.RuntimeFormKey(0x14) : target;

    internal static IReadOnlyList<string>? Execute(FalloutPluginStack records, FalloutReferenceWorld world, FalloutFormKey target,
        string operation, bool global, IReadOnlyList<string> arguments, FalloutScriptEffectResolvers resolve)
    {
        var player = records.RuntimeFormId(target) == 0x14;
        switch (operation)
        {
            // Casting on self needs no aim or animation. Abilities are ignored;
            // other spells reach the caster's active-effect owner at once.
            case "cios" or "castimmediateonself" when arguments.Count == 1:
                var cast = Require(resolve.Form(arguments[0]), "SPEL");
                if (SpellType(records, cast) == AbilitySpell || !world.IsScriptActor(target)) return null;
                return [arguments[0]];
            case "addspell" or "removespell" when arguments.Count == 1:
                _ = ChangeSpell(records, world, target, operation == "addspell", Require(resolve.Form(arguments[0]), "SPEL"));
                return null;
            case "dispel" when arguments.Count == 1:
                var dispelled = resolve.Form(arguments[0]);
                if (dispelled.Signature is not ("SPEL" or "ALCH" or "INGR"))
                    throw new InvalidDataException("Dispel source is not an actor effect or ingestible.");
                if (!world.IsScriptActor(target)) return null;
                if (player) return [arguments[0]];
                _ = world.DispelActorEffects(target, item => item == dispelled.FormKey);
                return null;
            case "dispelallspells" when arguments.Count == 0:
                if (!world.IsScriptActor(target)) return null;
                if (player) return [];
                _ = world.DispelActorEffects(target, item => DispelledByDispelAll(records, item));
                return null;
            // The duration defaults to -1: play until stopped or unloaded.
            case "pms" or "playmagicshadervisuals" when arguments.Count is 1 or 2:
                var shader = Require(resolve.Form(arguments[0]), "EFSH");
                var duration = arguments.Count == 2 ? resolve.Number(arguments[1]) : -1;
                if (!double.IsFinite(duration)) throw new InvalidDataException("Magic shader duration is not finite.");
                if (!world.SetMagicShader(target, shader, true)) return null;
                return duration < 0 ? [arguments[0]] : [arguments[0], Literal(duration)];
            case "sms" or "stopmagicshadervisuals" when arguments.Count == 1:
                world.SetMagicShader(target, Require(resolve.Form(arguments[0]), "EFSH"), false);
                return [arguments[0]];
            case "saa" or "setactoralpha" when arguments.Count == 1:
                var alpha = resolve.Number(arguments[0]);
                if (!double.IsFinite(alpha)) throw new InvalidDataException("Actor alpha is not finite.");
                return world.SetActorAlpha(target, (float)alpha) is { } applied
                    ? [applied.ToString("R", CultureInfo.InvariantCulture)] : null;
            // Preloading only warms the editor's resource cache. Owned records
            // and assets are already resolved on first use.
            case "preloadmagiceffect" when global && arguments.Count == 1:
                _ = Require(resolve.Form(arguments[0]), "MGEF");
                return null;
            // Count, then optional alpha (<= 0 keeps the default opacity) and
            // decal size range; drawn by the shared screen blood overlay.
            case "triggerscreensplatter" when global && arguments.Count is >= 1 and <= 4:
                var splatter = arguments.Select(resolve.Number).ToArray();
                if (splatter.Any(value => !double.IsFinite(value))) throw new InvalidDataException("Screen splatter arguments are not finite.");
                world.ScreenBlood.Trigger(target, FalloutScreenBlood.Count(splatter[0]), new(
                    splatter.Length >= 2 && splatter[1] > 0 ? (float)splatter[1] : null,
                    splatter.Length >= 3 ? (float)splatter[2] : null, splatter.Length == 4 ? (float)splatter[3] : null));
                return null;
            case "setscreensplatterfade" when global && arguments.Count is 1 or 2:
                if (arguments.Select(resolve.Number).Any(value => !double.IsFinite(value)))
                    throw new InvalidDataException("Screen splatter arguments are not finite.");
                FalloutScriptCommandDeferral.Warn(operation, "splatter-fade-flag-semantics-unresolved");
                return null;
            case "setweather" or "sw" when global && arguments.Count is 1 or 2:
                _ = Require(resolve.Form(arguments[0]), "WTHR");
                return arguments.Count == 1 ? [arguments[0]] : [arguments[0], Literal(resolve.Number(arguments[1]))];
            // The cell's image space changes at once; a presented current cell
            // composes its next frame from it.
            case "setcellimagespace" when global && arguments.Count == 2:
                world.SetCellImageSpace(Require(resolve.Form(arguments[0]), "CELL"), Require(resolve.Form(arguments[1]), "IMGS"));
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
            // A reference, then personality and emotion as two free strings
            // (up to 511 characters each). Nothing happens without loaded 3D.
            case "setsecuritronexpression" or "setsecexp" when global && arguments.Count == 3:
                var face = resolve.Reference(arguments[0]);
                _ = PlainString(arguments[1]); _ = PlainString(arguments[2]);
                if (world.IsResident(face))
                    FalloutScriptCommandDeferral.Warn("setsecuritronexpression", "securitron-face-presentation-unbound");
                return null;
            default:
                throw new InvalidDataException($"{operation} has an invalid target or argument count ({arguments.Count}).");
        }
    }

    // AddSpell returns 1 when the spell was added and 0 when it was already
    // known; RemoveSpell returns 1 when it was found and removed. A receiver
    // that is not an actor does nothing.
    internal static bool ChangeSpell(FalloutPluginStack records, FalloutReferenceWorld world, FalloutFormKey target,
        bool add, FalloutFormKey spell)
    {
        if (!world.IsScriptActor(target)) return false;
        var changed = world.ChangeScriptSpell(target, spell, add);
        if (add && changed && records.RuntimeFormId(target) == 0x14)
            FalloutScriptCommandDeferral.Warn("addspell", "player-spell-added-notice-setting-unidentified");
        FalloutScriptCommandDeferral.Warn(add ? "addspell" : "removespell", "spell-list-retained;ability-consumers-read-source-SPLO-only");
        return changed;
    }

    private const uint AbilitySpell = 4;

    internal static uint SpellType(FalloutPluginStack records, FalloutFormKey spell)
    {
        var header = records.GetEffective(spell).ReadSubrecords().Where(field => field.Signature == "SPIT").ToArray();
        if (header.Length != 1 || header[0].Data.Length < 4) throw new InvalidDataException("Actor effect needs one SPIT declaration.");
        return BinaryPrimitives.ReadUInt32LittleEndian(header[0].Data.Span);
    }

    // DispelAllSpells ends actor effects, powers and lesser powers, ingestibles,
    // ingredients and plain enchantments. Abilities, diseases, poisons,
    // addictions and weapon or apparel enchantments remain.
    internal static bool DispelledByDispelAll(FalloutPluginStack records, FalloutFormKey item)
    {
        var record = records.GetEffective(item);
        return record.Signature switch
        {
            "SPEL" => SpellType(records, item) is 0 or 2 or 3,
            "ALCH" or "INGR" => true,
            "ENCH" => record.ReadSubrecords().Where(field => field.Signature == "ENIT").ToArray() is [var header] &&
                header.Data.Length >= 4 && BinaryPrimitives.ReadUInt32LittleEndian(header.Data.Span) is not (2 or 3),
            _ => false,
        };
    }

    private static string PlainString(string token)
    {
        var text = token.Length >= 2 && token[0] == '"' && token[^1] == '"' ? token[1..^1] : token;
        return text.Length is > 0 and <= 511 ? text : throw new InvalidDataException("Script string argument is empty or exceeds 511 characters.");
    }

    private static FalloutFormKey Require(FalloutPluginRecord record, string signature) => record.Signature == signature
        ? record.FormKey : throw new InvalidDataException($"Script effect argument is {record.Signature}, not {signature}.");

    private static string Literal(double value) => value.ToString("R", CultureInfo.InvariantCulture);
}

internal sealed record FalloutScriptEffectResolvers(Func<string, FalloutPluginRecord> Form,
    Func<string, double> Number, Func<string, FalloutFormKey> Reference);

internal sealed partial class FalloutReferenceWorld
{
    private readonly Dictionary<FalloutFormKey, float> _actorAlpha = [];
    private readonly HashSet<(FalloutFormKey Reference, FalloutFormKey Shader)> _magicShaders = [];
    private readonly Dictionary<FalloutFormKey, FalloutFormKey> _cellImageSpaces = [];
    private readonly Dictionary<FalloutFormKey, Action<FalloutImageSpace>> _cellImageSpacePresenters = [];

    // AddSpell/RemoveSpell are retained beside perks: one is a grant, zero
    // revokes a source SPLO entry. Order of later changes wins. Returns whether
    // the actor's spell list changed.
    internal bool ChangeScriptSpell(FalloutFormKey reference, FalloutFormKey spell, bool add)
    {
        var current = Overrides(reference);
        var entry = FormOverride(spell, "SPEL", add ? 1 : 0);
        var known = ScriptSpell(reference, spell) ?? SourceSpells(reference).Contains(spell);
        _actorOverrides[reference] = current with { Spells = (current.Spells ?? []).Where(item => item.Form != spell).Append(entry).ToArray() };
        return known != add;
    }

    internal bool? ScriptSpell(FalloutFormKey reference, FalloutFormKey spell) =>
        _actorOverrides.GetValueOrDefault(reference)?.Spells?.SingleOrDefault(item => item.Form == spell) is { } entry ? entry.Value == 1 : null;

    private IEnumerable<FalloutFormKey> SourceSpells(FalloutFormKey reference)
    {
        var placed = ActorOverrideSource(reference);
        var actorBase = placed.Signature is "NPC_" or "CREA" ? placed : records.GetEffective(placed.ReadSubrecords()
            .Where(field => field.Signature == "NAME" && field.Data.Length == 4)
            .Select(field => placed.Plugin.AdjustOptionalFormId(BinaryPrimitives.ReadUInt32LittleEndian(field.Data.Span)))
            .Single() ?? throw new InvalidDataException("Placed actor has no base."));
        return actorBase.ReadSubrecords().Where(field => field.Signature == "SPLO" && field.Data.Length == 4)
            .Select(field => actorBase.Plugin.AdjustOptionalFormId(BinaryPrimitives.ReadUInt32LittleEndian(field.Data.Span)))
            .OfType<FalloutFormKey>();
    }

    // Ends matching timed actor effects early. Reversible modifiers are
    // returned exactly as at natural expiry; applied increments remain.
    internal int DispelActorEffects(FalloutFormKey reference, Func<FalloutFormKey, bool> source)
    {
        if (records.RuntimeFormId(reference) == 0x14) throw new InvalidOperationException("The engine player has its own active-effect owner.");
        var actor = Actor(reference);
        if (actor.Ingestibles is not { } before) return 0;
        if (before.Failure is not null) throw new NotSupportedException($"Actor active effects already failed: {before.Failure}");
        var removed = before.Effects.Where(timer => source(timer.Item)).ToArray();
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
        actor.Ingestibles = new(before.Effects.Where(timer => !source(timer.Item)).ToArray());
        return removed.Length;
    }

    // Script alpha lives with the resident actor: clamped to [0, 1], never
    // saved, and dropped when the actor unloads. Null means no loaded 3D.
    internal float? SetActorAlpha(FalloutFormKey reference, float alpha)
    {
        if (!IsScriptActor(reference) || reference != _enginePlayer && !IsResident(reference)) return null;
        var clamped = Math.Clamp(alpha, 0, 1);
        if (reference == _enginePlayer) return clamped;
        if (clamped == 1) _actorAlpha.Remove(reference); else _actorAlpha[reference] = clamped;
        ++PresentationRevision;
        return clamped;
    }

    internal float ActorAlpha(FalloutFormKey reference) => _actorAlpha.GetValueOrDefault(reference, 1);

    // Shader instances are transient presentation of a loaded receiver.
    // Returns false when the receiver has no loaded 3D.
    internal bool SetMagicShader(FalloutFormKey reference, FalloutFormKey shader, bool play)
    {
        if (records.RuntimeFormId(reference) != 0x14 && records.GetEffective(reference).Signature is not ("REFR" or "ACHR" or "ACRE"))
            throw new InvalidDataException("Magic shader target is not a placed reference.");
        if (reference != _enginePlayer && !IsResident(reference)) return false;
        if (play) _magicShaders.Add((reference, shader)); else _magicShaders.Remove((reference, shader));
        return true;
    }

    internal IReadOnlyList<FalloutFormKey> MagicShaders(FalloutFormKey reference) =>
        _magicShaders.Where(item => item.Reference == reference).Select(item => item.Shader).ToArray();

    private void RetireTransientPresentation(FalloutFormKey reference)
    {
        _actorAlpha.Remove(reference);
        _magicShaders.RemoveWhere(item => item.Reference == reference);
    }

    internal void SetCellImageSpace(FalloutFormKey cell, FalloutFormKey imageSpace)
    {
        _cellImageSpaces[cell] = imageSpace;
        if (_cellImageSpacePresenters.TryGetValue(cell, out var present)) present(CellImageSpace(cell)!);
    }

    internal FalloutImageSpace? CellImageSpace(FalloutFormKey cell) => _cellImageSpaces.TryGetValue(cell, out var image)
        ? FalloutImageSpaceReader.Read(records.GetEffective(image)) : FalloutImageSpaceReader.ForCell(records, cell);

    internal void BindCellImageSpace(FalloutFormKey cell, Action<FalloutImageSpace> present) => _cellImageSpacePresenters[cell] = present;

    internal void UnbindCellImageSpace(FalloutFormKey cell, Action<FalloutImageSpace> present)
    {
        if (_cellImageSpacePresenters.TryGetValue(cell, out var current) && current.Equals(present)) _cellImageSpacePresenters.Remove(cell);
    }

    // SetCellImageSpace replacements are treated as campaign state.
    internal IReadOnlyList<FalloutCellImageSpaceChange>? CaptureCellImageSpaces() => _cellImageSpaces.Count == 0 ? null :
        _cellImageSpaces.OrderBy(pair => records.RuntimeFormId(pair.Key)).Select(pair => new FalloutCellImageSpaceChange(pair.Key, pair.Value)).ToArray();

    internal void RestoreCellImageSpaces(IReadOnlyList<FalloutCellImageSpaceChange>? changes)
    {
        if (_cellImageSpaces.Count != 0) throw new InvalidOperationException("Cell image space restore requires a fresh world.");
        if (changes is null) return;
        if (changes.Any(change => change is null || records.GetEffective(change.Cell).Signature != "CELL" ||
                records.GetEffective(change.ImageSpace).Signature != "IMGS") || changes.Select(change => change.Cell).Distinct().Count() != changes.Count)
            throw new InvalidDataException("Saved cell image spaces are invalid or duplicated.");
        foreach (var change in changes) _cellImageSpaces.Add(change.Cell, change.ImageSpace);
    }
}

internal sealed record FalloutCellImageSpaceChange(FalloutFormKey Cell, FalloutFormKey ImageSpace);
