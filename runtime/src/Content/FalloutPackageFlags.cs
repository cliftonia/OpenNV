using OpenNV.Runtime.World.Cells;

namespace OpenNV.Runtime.Content;

// PKDT general flags are shared by every procedure. A procedure owner admits
// all of them; bits whose behaviour has no runtime owner yet are reported once
// as a visible deferral instead of refusing the whole actor package.
internal static class FalloutPackageFlags
{
    internal const uint OffersServices = 0x1;
    internal const uint MustReachLocation = 0x2;
    internal const uint MustComplete = 0x4;
    internal const uint DoorFlags = 0x1f8;
    internal const uint ContinueIfPlayerNear = 0x200;
    internal const uint OncePerDay = 0x400;
    internal const uint SkipFalloutBehavior = 0x1000;
    internal const uint AlwaysRun = 0x2000;
    internal const uint AlwaysSneak = 0x20000;
    internal const uint AllowSwimming = 0x40000;
    internal const uint DefensiveCombat = 0x400000;
    internal const uint WeaponDrawn = 0x800000;
    internal const uint NoIdleAnimations = 0x1000000;
    internal const uint PretendInCombat = 0x2000000;
    internal const uint ContinueDuringCombat = 0x4000000;
    internal const uint NoCombatAlert = 0x8000000;
    internal const uint NoWarnAttack = 0x10000000;

    // Generic semantics owned by the shared package lifecycle: preemption
    // retention (Must Reach/Complete), selector day bookkeeping, package door
    // actions, the inert behaviour lane switch, and falling-route permission.
    internal const uint SharedOwned = MustReachLocation | MustComplete | DoorFlags | OncePerDay | SkipFalloutBehavior |
        FalloutScriptPackage.AllowFallsFlag;

    private static readonly (uint Bit, string Name)[] Names =
    [
        (OffersServices, "offers-services"), (ContinueIfPlayerNear, "continue-if-pc-near"),
        (0x800, "unused-0x800"), (AlwaysRun, "always-run"), (0x4000, "unused-0x4000"), (0x8000, "unused-0x8000"),
        (0x10000, "unused-0x10000"), (AlwaysSneak, "always-sneak"), (AllowSwimming, "allow-swimming"),
        (FalloutScriptPackage.HeadTrackingOffFlag, "head-tracking-off"), (FalloutScriptPackage.WeaponsUnequippedFlag, "weapons-unequipped"),
        (DefensiveCombat, "defensive-combat"), (WeaponDrawn, "weapon-drawn"), (NoIdleAnimations, "no-idle-animations"),
        (PretendInCombat, "pretend-in-combat"), (ContinueDuringCombat, "continue-during-combat"), (NoCombatAlert, "no-combat-alert"),
        (NoWarnAttack, "no-warn-attack"), (0x20000000, "unused-0x20000000"), (0x40000000, "unused-0x40000000"),
        (0x80000000, "unused-0x80000000"),
    ];

    // Type-specific flags are one editor union; records keep stale bits from
    // other procedure types. Only the bits defined for this procedure exist.
    internal static ushort TypeSpecificMask(byte procedure) => procedure switch
    {
        0 or 2 or 3 => 0x700,
        8 => 0x702,
        9 => 0x1,
        12 => 0x3f,
        _ => 0,
    };

    // Returns the general flags unchanged. Bits outside the procedure's own
    // owner and the shared lifecycle are accepted with a once-per-process
    // deferral. Behaviour flags are inert: OpenNV has no Fallout behaviour lane.
    internal static uint Admit(FalloutPluginRecord record, FalloutPackageData data, uint owned)
    {
        var unowned = data.Flags & ~(owned | SharedOwned);
        foreach (var (bit, name) in Names)
            if ((unowned & bit) != 0)
                FalloutPackageDeferral.Warn($"pkdt-flag:{name}", $"PKDT general flag {name} has no runtime owner for procedure {data.Procedure} (first {record.FormKey}).");
        if ((data.Flags & SkipFalloutBehavior) == 0 && data.BehaviorFlags != 0)
            FalloutPackageDeferral.Warn("pkdt-behavior-flags", $"PKDT behaviour flags are inert; no Fallout behaviour lane owner (first {record.FormKey}).");
        return data.Flags;
    }

    // Defined type-specific bits (already masked by FalloutPackageData) whose
    // behaviour the procedure owner does not implement are deferred visibly.
    internal static ushort AdmitSpecific(FalloutPluginRecord record, FalloutPackageData data, ushort owned)
    {
        var specific = data.SpecificFlags ?? 0;
        for (var bit = 1; bit <= 0x8000; bit <<= 1)
            if ((specific & ~owned & bit) != 0)
                FalloutPackageDeferral.Warn($"pkdt-specific:{data.Procedure}:0x{bit:x4}",
                    $"PKDT type-specific flag 0x{bit:x4} has no runtime owner for procedure {data.Procedure} (first {record.FormKey}).");
        return specific;
    }
}
