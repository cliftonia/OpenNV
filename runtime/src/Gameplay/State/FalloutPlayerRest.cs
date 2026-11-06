using OpenNV.Runtime.Content;
using OpenNV.Runtime.World.Cells;

namespace OpenNV.Runtime.Gameplay.State;

internal enum FalloutRestKind { Wait, Sleep }

// The player's live circumstances when a Sleep/Wait request is admitted.
// CantWaitHere is the cell/worldspace "Can't Wait" record flag; WaitDisabled
// is the EnableFastTravel wait argument, which beds do not consult.
internal readonly record struct FalloutRestCircumstances(bool Trespassing = false, bool AlarmSounding = false,
    bool Underwater = false, bool HostileActorsNear = false, bool Airborne = false, bool CantWaitHere = false,
    bool WaitDisabled = false, bool EnvironmentRadiation = false, bool RadiationRate = false,
    bool TakingHealthDamage = false, bool BedOwnedByOther = false);

/// <summary>Sleep/Wait admission and recovery rules shared by beds, the wait control and script commands.</summary>
internal static class FalloutPlayerRest
{
    internal const uint MenuId = 1012;
    // sleep_wait_menu.xml declares a 24-item hour scrollbar beginning at one.
    internal const int MaximumHours = 24;
    // Refusals show their setting with the sad Vault Boy for about 2 seconds.
    internal const float RefusalNoticeSeconds = 2;
    internal const string RefusalNoticeIcon = "Interface/Icons/Message Icons/glow_message_vaultboy_sad.dds";
    // One game hour is simulated per real second; the excess frame time is discarded.
    internal const double SecondsPerHour = 1;
    private static readonly HashSet<string> RefusalSettings = new(StringComparer.Ordinal)
    {
        "sNoWaitTrespass", "sNoWaitWhileAlarmSounding", "sNoWaitUnderWater", "sNoWaitHostilActorsNear", "sNoWaitInAir",
        "sNoWaitInCell", "sNoWaitInRadiation", "sNoWaitTakingHealthDamage", "sNoSleepInOwnedBed", "sNoSleepTrespass",
        "sNoSleepHostileActorsNear", "sNoSleepInRadiation", "sNoSleepTakingHealthDamage",
    };

    // Saves from before the original chains may still hold these pending
    // notices; they remain displayable but no admission produces them.
    private static readonly HashSet<string> LegacyRefusalSettings = new(StringComparer.Ordinal) { "sNoSleepCombat", "sNoWaitInCombat" };

    internal static bool IsRefusalNotice(string? setting) =>
        setting is not null && (RefusalSettings.Contains(setting) || LegacyRefusalSettings.Contains(setting));

    // The Wait control (and ShowSleepWaitMenu with its check flag). The first
    // matching circumstance names the refusal; null admits the request.
    internal static string? WaitRefusal(FalloutRestCircumstances circumstances) => circumstances switch
    {
        { Trespassing: true } => "sNoWaitTrespass",
        { AlarmSounding: true } => "sNoWaitWhileAlarmSounding",
        { Underwater: true } => "sNoWaitUnderWater",
        { HostileActorsNear: true } => "sNoWaitHostilActorsNear",
        { Airborne: true } => "sNoWaitInAir",
        { CantWaitHere: true } or { WaitDisabled: true } => "sNoWaitInCell",
        { EnvironmentRadiation: true } or { RadiationRate: true } => "sNoWaitInRadiation",
        { TakingHealthDamage: true } => "sNoWaitTakingHealthDamage",
        _ => null,
    };

    // Activating a bed. The Can't Wait refusal deliberately uses the wait
    // string, and only environmental radiation is checked.
    internal static string? SleepRefusal(FalloutRestCircumstances circumstances) => circumstances switch
    {
        { BedOwnedByOther: true } => "sNoSleepInOwnedBed",
        { CantWaitHere: true } => "sNoWaitInCell",
        { Trespassing: true } => "sNoSleepTrespass",
        { HostileActorsNear: true } => "sNoSleepHostileActorsNear",
        { EnvironmentRadiation: true } => "sNoSleepInRadiation",
        { TakingHealthDamage: true } => "sNoSleepTakingHealthDamage",
        _ => null,
    };

    // A bed is the player's to use when it has no owner, names the player,
    // admits the player's faction rank, or its ownership global is set
    // (rented rooms set that global for the stay).
    internal static bool OwnedByOther(FalloutPluginStack records, FalloutReferenceOwnership ownership,
        Func<FalloutFormKey, int?> playerFactionRank, Func<FalloutFormKey, float> global)
    {
        if (ownership.Owner is not { } owner) return false;
        if (owner == records.RuntimeFormKey(7) || owner == records.RuntimeFormKey(0x14)) return false;
        if (ownership.Global is { } condition && global(condition) != 0) return false;
        if (records.GetEffective(owner).Signature == "FACT" && playerFactionRank(owner) is { } rank &&
            rank >= (ownership.FactionRank ?? 0)) return false;
        return true;
    }

    // "Can't Wait" is a record-header flag of the interior cell, or of the
    // worldspace that owns an exterior cell.
    internal static bool CantWait(FalloutPluginStack records, FalloutFormKey cell)
    {
        const uint CantWaitFlag = 0x0008_0000;
        var owner = FalloutCellSceneReader.ParentWorldspace(records.GetEffective(cell)) ?? cell;
        return (records.GetEffective(owner).Flags & CantWaitFlag) != 0;
    }

    // Health regeneration for one rest hour of S real seconds. In the sleeping
    // furniture state the sleeping base and multiplier apply to the heal rate.
    internal static float HourHealing(FalloutPluginStack records, float healRate, bool sleepingFurniture, float seconds)
    {
        if (!float.IsFinite(healRate) || !float.IsFinite(seconds) || seconds < 0)
            throw new InvalidDataException("Rest healing requires a finite heal rate and hour length.");
        var perSecond = healRate / 3600.0;
        if (sleepingFurniture)
            perSecond = FalloutGameSettingFloats.Read(records, "fHealingRateSleepingBase") / 3600.0 +
                perSecond * FalloutGameSettingFloats.Read(records, "fHealingRateSleepingMult");
        var result = (float)(perSecond * seconds);
        return float.IsFinite(result) ? result : throw new InvalidDataException("Rest healing is not finite.");
    }

    // Only a Sleep that runs out naturally outside Hardcore ends with the full
    // heal: all health damage and every limb condition, including the brain.
    internal static GameplayVitals EndOfSleep(GameplayVitals vitals)
    {
        if (vitals.ExactHitPoints <= 0) return vitals;
        var recovered = vitals with { HitPoints = vitals.MaximumHitPoints, HitPointFraction = 0, LimbDamage = null };
        recovered.Validate();
        return recovered;
    }

    internal static GameplayVitals Heal(GameplayVitals vitals, float amount)
    {
        if (!float.IsFinite(amount) || amount < 0) throw new ArgumentOutOfRangeException(nameof(amount));
        if (vitals.ExactHitPoints <= 0 || amount == 0) return vitals;
        var health = Math.Min(vitals.MaximumHitPoints, vitals.ExactHitPoints + amount);
        var displayed = (int)MathF.Ceiling(health);
        return vitals with { HitPoints = displayed, HitPointFraction = displayed - health };
    }

    // "<day name>, <MM.DD.YY>, h:mm <AM/PM>"; hour 0 shows as 12.
    internal static string ClockText(FalloutPluginStack records, FalloutScheduleTime time, int year)
    {
        if (time.Weekday is < 0 or > 6 || time.Month is < 0 or > 11 || !float.IsFinite(time.Hour) || time.Hour is < 0 or >= 24)
            throw new InvalidDataException("Rest clock has an invalid calendar time.");
        string[] days = ["sDaySunday", "sDayMonday", "sDayTuesday", "sDayWednesday", "sDayThursday", "sDayFriday", "sDaySaturday"];
        var whole = (int)time.Hour;
        var minutes = Math.Clamp((int)((time.Hour - whole) * 60), 0, 59);
        var shown = whole < 1 ? 12 : whole >= 13 ? whole - 12 : whole;
        var suffix = FalloutGameSettingStrings.Read(records, whole < 12 ? "sAMTime" : "sPMTime");
        return string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"{FalloutGameSettingStrings.Read(records, days[time.Weekday])}, {time.Month + 1:00}.{time.Date:00}.{year % 100:00}, {shown}:{minutes:00} {suffix}");
    }
}
