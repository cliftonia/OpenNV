using OpenNV.Runtime.Content;

namespace OpenNV.Runtime.Gameplay.State;

internal readonly record struct FalloutCriticalOutcome(bool Critical, bool SneakAttack, float Damage, bool OnDeath,
    FalloutFormKey? Effect)
{
    // A critical's effect is cast unless it is reserved for a killing hit.
    internal FalloutFormKey? EffectFor(bool killed) => Critical && (!OnDeath || killed) ? Effect : null;
}

// Target side of the per-hit critical roll. The attacker's actor value, weapon
// multiplier and Calculate My Critical Hit Chance/Damage entries arrive in the
// weapon damage; the target's Modify Enemy Critical Hit Chance entries, the
// VATS bonus, the per-mille conversion and the sneak-attack multiplier follow.
internal static class FalloutCriticalHits
{
    internal static FalloutCriticalOutcome Roll(FalloutPluginStack records, FalloutCriticalData? critical,
        Func<float, float> enemyChanceEntries, bool sneakAttack, Func<float>? random)
    {
        if (critical is null) return default;
        var chance = enemyChanceEntries(critical.Chance);
        if (critical.Vats) chance += FalloutGameSettingFloats.Read(records, "fVATSCriticalChanceBonus");
        chance *= 10;
        if (sneakAttack) chance *= FalloutGameSettingFloats.Read(records, "fCombatSneakAttackBonusMult");
        if (!float.IsFinite(chance)) throw new InvalidDataException("Critical chance is not finite.");
        if (chance <= 0) return default;
        var unit = (random ?? throw new NotSupportedException("Critical roll has no persistent random owner."))();
        if (!float.IsFinite(unit) || unit is < 0 or >= 1) throw new InvalidDataException("Critical roll random unit is invalid.");
        if ((int)(unit * 1000) >= chance) return default;
        return new(true, sneakAttack, Math.Max(critical.Damage, 0), critical.OnDeath, critical.Effect);
    }
}
