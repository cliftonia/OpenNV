using Godot;

namespace OpenNV.Runtime.Formats.Gamebryo;

// Velocity-only modifiers. Each particle advances by its own step: the time
// since it was last integrated (MotionSeconds), never the frame delta.
internal sealed partial class RuntimeNifParticleSystem
{
    private void ApplyGravity(FalloutNifParticleGravity gravity)
    {
        if (ActiveCount == 0) return;
        var transform = TransformOf(gravity.Object);
        // World-aligned gravity turns the axis by the object's own local rotation.
        var basis = gravity.WorldAligned ? NativeNifWorldAxes.Effective(_nodes[gravity.Object]).Basis : transform.Basis;
        var axis = (basis * Convert(gravity.Axis)).Normalized();
        var strength = _gravityStrengths[gravity.Name] * FalloutNifParticleForces.GravityStrengthScale * _units;
        var noise = gravity.Turbulence * gravity.TurbulenceScale * FalloutNifParticleForces.TurbulenceScale * _units;
        for (var i = 0; i < ActiveCount; i++)
        {
            ref var particle = ref _particles[i];
            var offset = transform.Origin - particle.Position;
            particle.Velocity += (FalloutNifParticleForces.Gravity(gravity.ForceType, gravity.Decay, noise > 0, axis, offset, strength, _units) +
                (noise > 0 ? WhiteNoise() * noise : Vector3.Zero)) * particle.MotionSeconds;
        }
    }

    private Vector3 WhiteNoise() => new(_random.NextSingle() * 2 - 1, _random.NextSingle() * 2 - 1, _random.NextSingle() * 2 - 1);

    private void ApplyBomb(FalloutNifParticleBomb bomb)
    {
        var transform = TransformOf(bomb.Object);
        var axis = (transform.Basis * Convert(bomb.Axis)).Normalized();
        for (var i = 0; i < ActiveCount; i++)
        {
            ref var particle = ref _particles[i];
            if (particle.MotionSeconds <= 0) continue;
            if (FalloutNifParticleForces.Bomb(bomb, axis, particle.Position - transform.Origin, _units) is { } impulse)
                particle.Velocity += impulse * particle.MotionSeconds;
        }
    }

    // Drag is not source-confirmed: full drag within Range, linear falloff
    // beyond it, removing a share of the along-axis velocity.
    private void ApplyDrag(FalloutNifParticleDrag drag)
    {
        var transform = TransformOf(drag.Object);
        var axis = (transform.Basis * Convert(drag.Axis)).Normalized();
        for (var i = 0; i < ActiveCount; i++)
        {
            ref var particle = ref _particles[i];
            var distance = particle.Position.DistanceTo(transform.Origin) / _units;
            var weight = distance <= drag.Range ? 1 : drag.Falloff == 0 ? 0 : Math.Clamp(1 - (distance - drag.Range) / drag.Falloff, 0, 1);
            var reduction = Math.Clamp(drag.Percentage * weight * particle.MotionSeconds, 0, 1);
            particle.Velocity -= axis * (particle.Velocity.Dot(axis) * reduction);
        }
    }

    private void ApplyWind(FalloutNifParticleWind wind)
    {
        var velocity = OpenNV.Runtime.World.Cells.RuntimeNativeWind.SampleFor(this) * (wind.Strength * _units);
        if (!_source.WorldSpace) velocity = _parentInverse.Basis * velocity;
        for (var i = 0; i < ActiveCount; i++) _particles[i].Velocity += velocity * _particles[i].MotionSeconds;
    }
}

// Pure force functions (metres). Strength is pre-multiplied by 1.6 and units.
internal static class FalloutNifParticleForces
{
    internal const float GravityStrengthScale = 1.6f;
    internal const float TurbulenceScale = 500;

    // `offset` is gravity centre minus particle (metres); `strength` is the
    // scaled source strength in metres. Decay distances are in source units.
    internal static Vector3 Gravity(uint forceType, float decay, bool turbulent, Vector3 axis, Vector3 offset, float strength, float units)
    {
        if (forceType == 0)
            return axis * (strength * (decay == 0 ? 1 : MathF.Exp(-decay * MathF.Abs(offset.Dot(axis)) / units)));
        if (decay == 0) return offset.Normalized() * strength;
        var falloff = MathF.Exp(-decay * offset.Length() / units);
        // Spherical decay pulls along the raw offset, so force grows with
        // distance until the exponential wins. With turbulence the source uses
        // the axis instead; that quirk is kept.
        return turbulent ? axis * (strength * falloff) : offset * (falloff * strength / units);
    }

    // Acceleration (metres) from a bomb at `offset` = particle minus centre.
    // Particles beyond Decay are untouched for linear and exponential decay.
    internal static Vector3? Bomb(FalloutNifParticleBomb bomb, Vector3 axis, Vector3 offset, float units)
    {
        Vector3 direction; float distance;
        switch (bomb.Symmetry)
        {
            case 0:
                distance = offset.Length();
                if (distance == 0) return null;
                direction = offset / distance;
                break;
            case 1:
                var radial = offset - axis * offset.Dot(axis);
                distance = radial.Length();
                if (distance == 0) return null;
                direction = radial / distance;
                break;
            default:
                var along = offset.Dot(axis);
                direction = along >= 0 ? axis : -axis;
                distance = MathF.Abs(along);
                break;
        }
        distance /= units;
        if (bomb.DecayType != 0 && (bomb.Decay <= 0 || distance > bomb.Decay)) return null;
        var falloff = bomb.DecayType switch { 1 => (bomb.Decay - distance) / bomb.Decay, 2 => MathF.Exp(-distance / bomb.Decay), _ => 1 };
        return direction * (bomb.DeltaVelocity * falloff * units);
    }
}
