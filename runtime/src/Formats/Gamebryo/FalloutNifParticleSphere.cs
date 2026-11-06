using System.Numerics;

namespace OpenNV.Runtime.Formats.Gamebryo;

internal static class FalloutNifParticleSphere
{
    internal static void Validate(float radius)
    {
        if (!float.IsFinite(radius) || radius <= 0)
            throw new InvalidDataException("Particle collision sphere requires a finite positive radius.");
    }

    // Outside points hit on the way in, inside points on the way out; an
    // outside point moving away never hits. Drawing radius is independent.
    internal static FalloutNifParticleCollisionHit? Sweep(Vector3 start, Vector3 velocity, float seconds, float radius,
        float tolerance = 1e-4f)
    {
        Validate(radius);
        if (!float.IsFinite(seconds) || seconds < 0 || !Finite(start) || !Finite(velocity))
            throw new InvalidDataException("Particle sphere sweep has nonfinite motion.");
        var speedSquared = Dot(velocity, velocity);
        if (seconds == 0 || speedSquared == 0) return null;
        var squared = (double)radius * radius;
        var inside = squared - Dot(start, start) > tolerance;
        var approach = -Dot(start, velocity);
        if (!inside && approach <= 0) return null;
        var closest = approach / speedSquared;
        var nearest = start + velocity * (float)closest;
        var gap = Dot(nearest, nearest);
        if (!inside && gap >= squared) return null;
        var half = Math.Sqrt((squared - gap) / speedSquared);
        var time = inside ? closest + half : closest - half;
        if (time < 0 || time >= seconds) return null;
        var point = start + velocity * (float)time;
        var normal = point / radius;
        if (!Finite(point) || !Finite(normal)) throw new InvalidDataException("Particle sphere contact is not finite.");
        return new((float)(time / seconds), point, normal);
    }

    // The reflected velocity is scaled as a whole by the bounce factor.
    internal static Vector3 Respond(Vector3 velocity, Vector3 normal, float bounce) =>
        (velocity - 2 * Vector3.Dot(velocity, normal) * normal) * bounce;

    private static double Dot(Vector3 first, Vector3 second) =>
        (double)first.X * second.X + (double)first.Y * second.Y + (double)first.Z * second.Z;
    private static bool Finite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
