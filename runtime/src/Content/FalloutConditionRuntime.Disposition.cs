using System.Buffers.Binary;
using Function = OpenNV.Runtime.Content.Definitions.FalloutConditionFunction;

namespace OpenNV.Runtime.Content;

// GetDisposition: how the subject regards a target actor, 0-100.
internal sealed partial class FalloutConditionRuntime
{
    private const int CharismaValue = 8, KarmaValue = 23, ResponsibilityValue = 3;
    private const byte DispositionEntryPoint = 14;

    private float Disposition(FalloutConditionQuery condition, FalloutFormKey subject, FalloutFormKey target)
    {
        if (!IsActor(subject) || !IsActor(target)) return 0;
        var traits = Traits(subject);
        if (traits.Signature == "NPC_" && Base(target) == Base(subject)) return 100;
        // Owned, mastered and reanimated creatures answer through a proxy actor.
        if (traits.Signature != "NPC_")
            throw new NotSupportedException("Creature disposition has no owner, process-master or reanimation owner.");
        var acbs = traits.ReadSubrecords().Where(field => field.Signature == "ACBS").ToArray();
        if (acbs.Length != 1 || acbs[0].Data.Length != 24) throw new InvalidDataException("Disposition ACBS extent is invalid.");
        // Setting slots follow their names; the executable binds names to slots at run time.
        var total = BinaryPrimitives.ReadInt16LittleEndian(acbs[0].Data.Span[20..]) +
            Trunc(Setting("fDispActorCharBase") + Setting("fDispActorCharMult") *
                ((int)CurrentActorValue(condition, target, CharismaValue) - Setting("fDispTargetCharOffset"))) +
            Trunc(Setting("fDispTargetKarmaMult") * (int)CurrentActorValue(condition, target, KarmaValue) *
                (Setting("fDispActorRespBase") + Setting("fDispActorRespMult") * (int)CurrentActorValue(condition, subject, ResponsibilityValue))) +
            RaceDisposition(subject, target) + FactionDisposition(subject, target) +
            (IsWeaponDrawn(condition, target) ? Trunc(Setting("fDispTargetWeaponBase")) : 0);
        // ModDisposition has no runtime owner, so no modifier toward the player exists.
        RequireNoDispositionPerks(subject);
        return Math.Clamp(total, 0, 100);
    }

    // Only the subject's own race relation counts. Retail races declare none.
    private int RaceDisposition(FalloutFormKey subject, FalloutFormKey target)
    {
        if (target != records.RuntimeFormKey(0x14) && records.GetEffective(Base(target)).Signature != "NPC_") return 0;
        if (ActorRace(subject) is { } race && records.GetEffective(race).ReadSubrecords().Any(field => field.Signature == "XNAM"))
            throw new NotSupportedException("Race disposition relations have no qualified source layout.");
        return 0;
    }

    // A shared faction's highest self modifier wins; otherwise the lowest
    // modifier any subject faction holds toward any target faction.
    private int FactionDisposition(FalloutFormKey subject, FalloutFormKey target)
    {
        var mine = ActorFactions(subject).Where(pair => pair.Value >= 0).Select(pair => pair.Key).ToArray();
        var theirs = ActorFactions(target).Where(pair => pair.Value >= 0).Select(pair => pair.Key).ToHashSet();
        if (mine.Length == 0 || theirs.Count == 0) return 0;
        var factions = Required(world, "faction relations");
        var shared = mine.Where(theirs.Contains).ToArray();
        var reaction = shared.Length != 0 ? shared.Max(faction => factions.FactionReactionModifier(faction, faction)) :
            mine.SelectMany(from => theirs.Select(to => factions.FactionReactionModifier(from, to))).Min();
        var multiplier = Setting("fDispTargetFactionMult");
        return Trunc(reaction * multiplier * (Setting("fDispTargetFactionRankBase") + multiplier));
    }

    // Perk entry point Get Disposition, held by the subject or, for a
    // teammate, by the player.
    private void RequireNoDispositionPerks(FalloutFormKey subject)
    {
        var owner = Required(world, "acquired perks");
        if (subject == records.RuntimeFormKey(0x14) || owner.Get(subject).PlayerTeammate)
            throw new NotSupportedException("Player-held disposition perks have no condition owner.");
        var perks = _perkEntries ??= new FalloutAbilityModifiers(records);
        if (owner.AcquiredPerks(subject).Any(perk => perks.Perk(perk).Entries.Any(entry => entry.Entry == DispositionEntryPoint)))
            throw new NotSupportedException("Disposition perk entries have no owner/target condition scope.");
    }

    private FalloutAbilityModifiers? _perkEntries;
    private float Setting(string name) => records.NumericSettings.Float(name);
    private static int Trunc(float value) => checked((int)value);

    private float CurrentActorValue(FalloutConditionQuery condition, FalloutFormKey actor, int index) =>
        ActorValue is { } value ? value(actor, index) :
        actor == records.RuntimeFormKey(0x14) ? NativeValue(actor, condition with
        {
            Declaration = condition.Declaration with { Function = (ushort)Function.GetActorValue, Argument1 = checked((uint)index), Argument2 = 0 },
            BoundForm = null, BoundSecondForm = null,
        }) : Required(world, "actor values").ActorValue(actor, ActorValueName(checked((uint)index)));

    private bool IsWeaponDrawn(FalloutConditionQuery condition, FalloutFormKey actor) => WeaponOut is { } drawn ? drawn(actor) :
        NativeValue(actor, condition with
        {
            Declaration = condition.Declaration with { Function = (ushort)Function.IsWeaponOut, Argument1 = 0, Argument2 = 0 },
            BoundForm = null, BoundSecondForm = null,
        }) == 1;
}
