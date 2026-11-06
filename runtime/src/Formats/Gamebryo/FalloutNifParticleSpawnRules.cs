using System.Numerics;

namespace OpenNV.Runtime.Formats.Gamebryo;

// NiPSysSpawnModifier: eligibility, copy count, child speed/direction and life.
internal static class FalloutNifParticleSpawnRules
{
    internal static void Validate(FalloutNifParticleSpawn source)
    {
        if (!float.IsFinite(source.Probability) || source.Probability is < 0 or > 1 || source.Minimum > source.Maximum ||
            !float.IsFinite(source.SpeedVariation) || source.SpeedVariation < 0 ||
            !float.IsFinite(source.DirectionVariation) || source.DirectionVariation < 0 ||
            !float.IsFinite(source.Life) || source.Life < 0 || !float.IsFinite(source.LifeVariation) || source.LifeVariation < 0)
            throw new InvalidDataException($"Particle spawn modifier {source.Block.Index} has invalid source ranges.");
    }

    // Eligible below the generation limit when the sample is within the
    // spawn percentage; the count rounds half up and is at least one.
    internal static int Copies(FalloutNifParticleSpawn source, ushort generation, double probabilitySample, double countSample)
    {
        if (!double.IsFinite(probabilitySample) || probabilitySample is < 0 or > 1 ||
            !double.IsFinite(countSample) || countSample is < 0 or > 1)
            throw new InvalidDataException("Particle spawn random sample is outside its unit interval.");
        if (generation >= source.Generations || probabilitySample > source.Probability) return 0;
        var extra = (source.Maximum - source.Minimum) * countSample;
        var whole = Math.Floor(extra);
        return Math.Max(1, source.Minimum + (int)whole + (extra - whole > .5 ? 1 : 0));
    }

    // Speed chaos only ever increases speed. The direction is a cone of
    // half-angle DirectionVariation * pi around the parent's direction.
    internal static Vector3 ChildVelocity(FalloutNifParticleSpawn source, Vector3 parent, float speedSample,
        float declinationSample, float planarSample)
    {
        var speed = parent.Length();
        if (speed == 0) return Vector3.Zero;
        var axis = parent / speed;
        var declination = source.DirectionVariation * MathF.PI * declinationSample;
        var planar = MathF.Tau * planarSample;
        var local = new Vector3(MathF.Sin(declination) * MathF.Cos(planar), MathF.Sin(declination) * MathF.Sin(planar), MathF.Cos(declination));
        Quaternion rotation;
        var dot = Vector3.Dot(Vector3.UnitZ, axis);
        if (dot >= 1 - 1e-6f) rotation = Quaternion.Identity;
        else if (dot <= -1 + 1e-6f) rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI);
        else rotation = Quaternion.CreateFromAxisAngle(Vector3.Normalize(Vector3.Cross(Vector3.UnitZ, axis)), MathF.Acos(dot));
        return Vector3.Transform(local, rotation) * (speed * (1 + source.SpeedVariation * speedSample));
    }

    internal static float ChildLife(FalloutNifParticleSpawn source, float sample) => source.Life + (sample - .5f) * source.LifeVariation;
}
