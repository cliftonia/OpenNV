namespace OpenNV.Runtime.Gameplay.State;

// GetVATSValue action-type numbering (function parameter 6).
internal enum FalloutVatsAction : uint
{
    Unarmed = 0, OneHandMelee = 1, TwoHandMelee = 2, Pistol = 3, Rifle = 4, Handle = 5, Launcher = 6
}

internal static class FalloutVatsActions
{
    // Special/power actions have separate producers, not inferred categories.
    internal static FalloutVatsAction OrdinaryRanged(uint weaponType) => weaponType switch
    {
        3 or 4 => FalloutVatsAction.Pistol,
        5 or 6 or 7 => FalloutVatsAction.Rifle,
        8 => FalloutVatsAction.Handle,
        9 => FalloutVatsAction.Launcher,
        _ => throw new NotSupportedException("VATS action requires its separate melee/thrown/special producer.")
    };

    // WEAP animation types 0-2 are hand-to-hand, one-hand and two-hand melee.
    internal static FalloutVatsAction OrdinaryMelee(uint weaponType) => weaponType switch
    {
        0 => FalloutVatsAction.Unarmed,
        1 => FalloutVatsAction.OneHandMelee,
        2 => FalloutVatsAction.TwoHandMelee,
        _ => throw new NotSupportedException("VATS melee action requires a hand-to-hand or melee weapon type.")
    };
}
