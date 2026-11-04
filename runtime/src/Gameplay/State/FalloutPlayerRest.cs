using OpenNV.Runtime.Content;
using OpenNV.Runtime.World.Cells;

namespace OpenNV.Runtime.Gameplay.State;

internal enum FalloutRestKind { Wait, Sleep }

// The player's live circumstances when a Sleep/Wait request is admitted.
internal readonly record struct FalloutRestCircumstances(bool InCombat, bool Airborne, bool BedOwnedByOther);

/// <summary>Sleep/Wait admission and recovery rules shared by beds, the wait control and script commands.</summary>
internal static class FalloutPlayerRest
{
    internal const uint MenuId = 1012;
    // sleep_wait_menu.xml declares a 24-item hour scrollbar beginning at one.
    internal const int MaximumHours = 24;
    // HUD lifetime for the executable's rest refusal notices. The retail
    // notices carry no icon; their exact queue timing is unverified.
    internal const float RefusalNoticeSeconds = 2;

    private static readonly HashSet<string> RefusalSettings = new(StringComparer.Ordinal)
    {
        "sNoSleepCombat", "sNoWaitInCombat", "sNoWaitInAir", "sNoSleepInOwnedBed",
    };

    internal static bool IsRefusalNotice(string? setting) => setting is not null && RefusalSettings.Contains(setting);

    // Returns the executable string setting naming why rest is refused, or
    // null when the request is admitted. Order follows the visible priority:
    // combat, then physical support, then bed ownership.
    internal static string? Refusal(FalloutRestKind kind, FalloutRestCircumstances circumstances)
    {
        if (circumstances.InCombat) return kind == FalloutRestKind.Sleep ? "sNoSleepCombat" : "sNoWaitInCombat";
        if (circumstances.Airborne) return "sNoWaitInAir";
        if (kind == FalloutRestKind.Sleep && circumstances.BedOwnedByOther) return "sNoSleepInOwnedBed";
        return null;
    }

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

    internal static int RequireHours(double value)
    {
        if (!double.IsFinite(value) || value != Math.Truncate(value) || value < 1 || value > MaximumHours)
            throw new InvalidDataException($"Rest duration must be a whole number of hours from 1 through {MaximumHours}.");
        return (int)value;
    }

    // Sleeping at least one hour restores health. Hardcore keeps crippled
    // limbs for a doctor; waiting restores nothing.
    internal static GameplayVitals Recover(GameplayVitals vitals, FalloutRestKind kind, int hours, bool hardcore)
    {
        if (hours < 0) throw new ArgumentOutOfRangeException(nameof(hours));
        if (kind != FalloutRestKind.Sleep || hours == 0 || vitals.ExactHitPoints <= 0) return vitals;
        var recovered = vitals with
        {
            HitPoints = vitals.MaximumHitPoints, HitPointFraction = 0,
            LimbDamage = hardcore ? vitals.LimbDamage : null,
        };
        recovered.Validate();
        return recovered;
    }
}
