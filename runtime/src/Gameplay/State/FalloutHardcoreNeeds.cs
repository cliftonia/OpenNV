using OpenNV.Runtime.Content;
using OpenNV.Runtime.Content.Definitions;

namespace OpenNV.Runtime.Gameplay.State;

internal readonly record struct FalloutNeedStageChange(int ActorValue, bool Increased,
    FalloutActorValueStage? Previous, FalloutActorValueStage? Current);

/// <summary>
/// Hardcore Dehydration, Hunger and SleepDeprivation: real-time accrual from
/// the effective rate settings and DEHY/HUNG/SLPD stage transitions. Need
/// values live in the player vitals; the time anchors are deliberately not
/// saved, so a loaded or new game never catches up time from before it.
/// </summary>
internal sealed class FalloutHardcoreNeeds(FalloutPluginStack records)
{
    private sealed record Need(int ActorValue, string Stage, string Rate, string Notice);

    // Accrual order is Dehydration, SleepDeprivation, Hunger.
    private static readonly Need[] Needs =
    [
        new((int)FalloutActorValueId.Dehydration, "DEHY", "fHCDehydrationRate", "sDehydration"),
        new((int)FalloutActorValueId.SleepDeprivation, "SLPD", "fHCSleepDeprivationRate", "sSleepDeprevation"),
        new((int)FalloutActorValueId.Hunger, "HUNG", "fHCStarvationRate", "sHunger"),
    ];
    internal const string SickNotice = "sRadiationSick";
    internal const float NoticeSeconds = 2;
    private const string InPainIcon = "Interface/Icons/Message Icons/glow_message_vaultboy_in_pain.dds";
    private const string VeryHappyIcon = "Interface/Icons/Message Icons/glow_message_vaultboy_very_happy.dds";
    private static readonly HashSet<string> NoticeSettings = Needs
        .SelectMany(need => new[] { need.Notice + "Increase", need.Notice + "Decrease", need.Notice + "NotSick" })
        .Append(SickNotice).ToHashSet(StringComparer.Ordinal);
    private readonly FalloutAbilityModifiers _spells = new(records);
    private readonly double[] _anchors = new double[Needs.Length];
    private bool _anchored;
    private ((float, float, float) Key, IReadOnlyList<FalloutFormKey> Spells)? _selected;

    internal static int? ActorValue(string name) => FalloutActorValueNames.TryParse(name, out var id) &&
        id is FalloutActorValueId.Dehydration or FalloutActorValueId.Hunger or FalloutActorValueId.SleepDeprivation ? (int)id : null;

    // One point per `rate` real seconds at any timescale: the interval in game
    // minutes is rate * TimeScale / 60. Whole points are added, the leftover
    // interval is discarded and the anchor moves to now. Sleeping still moves
    // the SleepDeprivation anchor without adding to it.
    internal GameplayVitals Accrue(GameplayVitals state, float daysPassed, float timeScale, bool sleeping)
    {
        if (!float.IsFinite(daysPassed) || !float.IsFinite(timeScale) || timeScale < 0)
            throw new InvalidDataException("Hardcore needs require a finite game clock.");
        var now = (double)daysPassed * 24 * 60;
        if (!_anchored)
        {
            Array.Fill(_anchors, now);
            _anchored = true;
            return state;
        }
        for (var index = 0; index < Needs.Length; index++)
        {
            var need = Needs[index];
            var rate = FalloutGameSettingFloats.Read(records, need.Rate);
            if (!float.IsFinite(rate) || rate <= 0) throw new InvalidDataException($"{need.Rate} is not a positive rate.");
            var interval = (double)rate * timeScale / 60;
            var elapsed = now - _anchors[index];
            if (interval <= 0 || elapsed < interval) continue;
            _anchors[index] = now;
            if (sleeping && need.ActorValue == (int)FalloutActorValueId.SleepDeprivation) continue;
            state = state.WithNeed(need.ActorValue, (float)(state.Need(need.ActorValue) + Math.Floor(elapsed / interval)));
        }
        return state;
    }

    // Each slept Hardcore hour removes up to fHCSleepRestorationMod points.
    internal GameplayVitals RestoreSleep(GameplayVitals state)
    {
        var restoration = FalloutGameSettingFloats.Read(records, "fHCSleepRestorationMod");
        if (!float.IsFinite(restoration) || restoration < 0) throw new InvalidDataException("fHCSleepRestorationMod is invalid.");
        var current = state.SleepDeprivation;
        return state.WithNeed((int)FalloutActorValueId.SleepDeprivation, current - Math.Min(restoration, current));
    }

    internal static GameplayVitals Reset(GameplayVitals state) => state with { Dehydration = 0, Hunger = 0, SleepDeprivation = 0 };

    internal FalloutActorValueStage? Stage(int actorValue, float value) =>
        FalloutActorValueStage.Select(FalloutActorValueStage.Read(records, Spec(actorValue).Stage), value);

    // Stage spells that remain while their stage lasts (the disease spells).
    // Entry-only spells, such as the fatal health damage, are not selected.
    internal IReadOnlyList<FalloutFormKey> ConstantSpells(Func<int, float> need)
    {
        var key = (need(73), need(74), need(75));
        if (_selected is { } cached && cached.Key == key) return cached.Spells;
        var result = new List<FalloutFormKey>();
        foreach (var spec in Needs)
            if (Stage(spec.ActorValue, need(spec.ActorValue))?.Spell is { } spell && EntryHealthDamage(spell) is null)
                result.Add(spell);
        _selected = (key, result);
        return result;
    }

    // Stage changes between two need states, in accrual order. A change that
    // stays within one stage produces nothing, so there is no notice per point.
    internal IReadOnlyList<FalloutNeedStageChange> Changes(GameplayVitals before, GameplayVitals after)
    {
        var result = new List<FalloutNeedStageChange>();
        foreach (var spec in Needs)
        {
            var previous = before.Need(spec.ActorValue);
            var current = after.Need(spec.ActorValue);
            if (previous == current) continue;
            var oldStage = Stage(spec.ActorValue, previous);
            var newStage = Stage(spec.ActorValue, current);
            if (oldStage != newStage) result.Add(new(spec.ActorValue, current > previous, oldStage, newStage));
        }
        return result;
    }

    // A stage spell is either a constant effect or an ability whose every
    // effect is instant, unconditional Health damage (the Fatal stages). That
    // damage is applied once on entering the stage, through the health owner.
    internal float? EntryHealthDamage(FalloutFormKey spell)
    {
        if (_spells.TryGetConstantModifiers(spell, out var modifiers) && (modifiers.Count != 0 || _spells.Scripts(spell).Count != 0))
            return null;
        var (source, _, _) = FalloutIngestible.ReadSpell(records, spell);
        if (source.Effects.Any(effect => effect.Archetype != 0 || effect.ActorValue != (int)FalloutActorValueId.Health ||
            (effect.Flags & 2) != 0 || (effect.Flags & 4) == 0 || effect.EffectiveDuration != 0 || effect.Conditions.Count != 0 ||
            effect.Area != 0 || !float.IsFinite(effect.Magnitude)))
            throw new NotSupportedException($"Hardcore stage spell {spell} has no constant-effect or instant-damage owner.");
        return source.Effects.Sum(effect => effect.Magnitude);
    }

    internal static IReadOnlyList<FalloutHudEvent> Notices(FalloutNeedStageChange change, FalloutFormKey player)
    {
        var prefix = Spec(change.ActorValue).Notice;
        return
        [
            new(FalloutHudEventKind.ExecutableNotice, player, 0, Text: prefix + (change.Increased ? "Increase" : "Decrease")),
            change.Current is { } stage
                ? new(FalloutHudEventKind.ExecutableNotice,
                    stage.Spell ?? throw new InvalidDataException($"Stage {stage.Form} has no spell to name."), 0, Text: SickNotice)
                : new(FalloutHudEventKind.ExecutableNotice, player, 0, Text: prefix + "NotSick"),
        ];
    }

    internal static bool IsNotice(FalloutHudEvent value) =>
        value.Kind == FalloutHudEventKind.ExecutableNotice && value.Text is { } text && NoticeSettings.Contains(text);

    // "<sRadiationSick> <stage spell name>" names the new stage; increases use
    // the in-pain Vault Boy and recoveries the very-happy one, for 2 seconds.
    internal static (string Text, string Icon, float Seconds) Notice(FalloutPluginStack records, FalloutHudEvent value)
    {
        if (!IsNotice(value)) throw new InvalidDataException("HUD event is not a Hardcore need notice.");
        var text = value.Text!;
        if (text == SickNotice)
            return (FalloutGameSettingStrings.Read(records, SickNotice) + " " +
                FalloutRecordDisplayName.Read(records.GetEffective(value.Source)), InPainIcon, NoticeSeconds);
        return (FalloutGameSettingStrings.Read(records, text), text.EndsWith("Increase", StringComparison.Ordinal) ? InPainIcon : VeryHappyIcon,
            NoticeSeconds);
    }

    private static Need Spec(int actorValue) => Needs.SingleOrDefault(need => need.ActorValue == actorValue) ??
        throw new ArgumentOutOfRangeException(nameof(actorValue));
}
