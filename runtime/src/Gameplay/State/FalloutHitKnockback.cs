using System.Buffers.Binary;
using System.Numerics;
using System.Text.Json.Serialization;
using OpenNV.Runtime.Content;
using OpenNV.Runtime.Content.Definitions;

namespace OpenNV.Runtime.Gameplay.State;

// The retained controller contribution is a velocity rate and a remaining
// lifetime. Collision constrains the resulting motion; hits never set position.
internal sealed record FalloutHitKnockback(float RateX, float RateY, float RateZ, float RemainingSeconds)
{
    [JsonIgnore] internal Vector3 Rate => new(RateX, RateY, RateZ);
    [JsonIgnore] internal Vector3 Velocity => Rate * RemainingSeconds;

    internal static void Validate(FalloutHitKnockback? state)
    {
        if (state is not null && (!float.IsFinite(state.RateX) || !float.IsFinite(state.RateY) ||
            !float.IsFinite(state.RateZ) || !float.IsFinite(state.RemainingSeconds) || state.RemainingSeconds <= 0 ||
            !float.IsFinite(state.Rate.LengthSquared()) || state.Rate.LengthSquared() <= 0))
            throw new InvalidDataException("Hit knockback has invalid retained motion.");
    }

    internal static bool NotPushable(FalloutPluginRecord actor)
    {
        var acbs = actor.ReadSubrecords().Single(field => field.Signature == "ACBS").Data.Span;
        if (acbs.Length != 24) throw new InvalidDataException("Actor knockback has invalid ACBS data.");
        return ((FalloutActorBaseFlags)BinaryPrimitives.ReadUInt32LittleEndian(acbs) & FalloutActorBaseFlags.NotPushable) != 0;
    }

    private static float Setting(FalloutPluginStack records, string name)
    {
        var value = FalloutGameSettingFloats.Read(records, name);
        return float.IsFinite(value) ? value : throw new InvalidDataException($"Knockback setting {name} is not finite.");
    }

    // The Agility knockback force for an integer damage/force term. Agility
    // lowers it; a sufficiently negative term reverses its direction.
    internal static float Force(FalloutPluginStack records, float agility, int amount)
    {
        if (!float.IsFinite(agility)) throw new InvalidDataException("Knockback Agility is not finite.");
        var force = (Setting(records, "fKnockbackAgilBase") + 10 * agility * Setting(records, "fKnockbackAgilMult")) *
            (Setting(records, "fKnockbackDamageBase") + amount * Setting(records, "fKnockbackDamageMult"));
        return float.IsFinite(force) ? force : throw new InvalidDataException("Knockback force is not finite.");
    }

    internal static FalloutHitKnockback? Request(FalloutPluginStack records, float agility, float weaponBaseDamage,
        Vector3 direction, float unitsToMeters, FalloutHitKnockback? current)
    {
        // Native hit-payload weapon damage already includes skill/condition
        // and precedes armor mitigation; it is not raw WEAP DATA damage.
        if (!float.IsFinite(weaponBaseDamage) || weaponBaseDamage < 0 || weaponBaseDamage >= int.MaxValue)
            throw new InvalidDataException("Hit knockback inputs are not finite and source-valid.");
        var maximum = Setting(records, "fKnockbackForceMax");
        if (maximum < 0) throw new NotSupportedException("Negative knockback force limit is unbound.");
        return Push(records, Math.Min(Force(records, agility, (int)weaponBaseDamage), maximum), direction, unitsToMeters, current);
    }

    // Retained controller motion of the given force along the direction.
    internal static FalloutHitKnockback? Push(FalloutPluginStack records, float force, Vector3 direction, float unitsToMeters,
        FalloutHitKnockback? current)
    {
        Validate(current);
        if (!float.IsFinite(force) || !float.IsFinite(direction.LengthSquared()) || !float.IsFinite(unitsToMeters) || unitsToMeters <= 0)
            throw new InvalidDataException("Hit knockback inputs are not finite and source-valid.");
        var duration = Setting(records, "fKnockbackTime");
        if (duration <= 0) throw new NotSupportedException("Nonpositive knockback time is unbound.");
        if (direction.LengthSquared() == 0) return current;
        var rate = Vector3.Normalize(direction) * (force * unitsToMeters / duration);
        if (rate.LengthSquared() <= (current?.Rate.LengthSquared() ?? 0)) return current;
        var result = new FalloutHitKnockback(rate.X, rate.Y, rate.Z, duration);
        Validate(result);
        return result;
    }

    internal FalloutHitKnockback? Advance(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds < 0) throw new InvalidDataException("Knockback elapsed time is invalid.");
        var remaining = RemainingSeconds - (float)seconds;
        return remaining > 0 ? this with { RemainingSeconds = remaining } : null;
    }
}
