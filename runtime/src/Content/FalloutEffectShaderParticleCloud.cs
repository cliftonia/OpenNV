using System.Numerics;

namespace OpenNV.Runtime.Content;

// Presentation-space emission point: a position (metres) and a unit direction.
internal delegate void FalloutEffectShaderEmitter(Random random, out Vector3 position, out Vector3 direction);

internal readonly record struct FalloutEffectShaderParticleSample(Vector3 Centre, float HalfSize, float Angle,
    Vector4 Color, Vector2 TileOffset);

// The EFSH particle half: birth envelope, fixed-capacity batches and the
// per-particle motion, size, colour, rotation and tile functions. Vectors are
// in presentation axes; Velocity/Acceleration arrive already converted.
internal sealed class FalloutEffectShaderParticleCloud
{
    private const int N = FalloutEffectShaderTiming.ParticleBatchCapacity;
    // The on-screen half-size of a unit-scale particle is tan(37.5 degrees)
    // view-space units whatever the camera field of view.
    internal static readonly float ViewHalfSize = MathF.Tan(37.5f * MathF.PI / 180);
    private readonly FalloutEffectShaderParticles _source;
    private readonly FalloutEffectShaderEnvelope _birth;
    private readonly Vector3 _velocity, _acceleration;
    private readonly float _units;
    private readonly Vector2 _tileScale;
    private readonly Batch[] _batches;
    private readonly Random _random;
    private float _birthCurrent;

    internal FalloutEffectShaderParticleCloud(FalloutEffectShader effect, Vector3 velocity, Vector3 acceleration,
        float units, Random random)
    {
        if (!float.IsFinite(units) || units <= 0) throw new InvalidDataException("Effect shader particle units must be positive.");
        _source = effect.Particles;
        _birth = FalloutEffectShaderTiming.ParticleBirth(_source.Birth);
        _velocity = velocity * units; _acceleration = acceleration * units; _units = units; _random = random;
        uint columns = effect.TextureColumns is > 0 and var u ? u : 1, rows = effect.TextureRows is > 0 and var v ? v : 1;
        _tileScale = new(1f / columns, 1f / rows);
        _batches = new Batch[FalloutEffectShaderTiming.BatchCount(_birth)];
        for (var index = 0; index < _batches.Length; index++) _batches[index] = new(this, columns, rows);
    }

    internal int BatchCount => _batches.Length;
    internal int Capacity => _batches.Length * N;
    internal float BirthCurrent => _birthCurrent;
    internal float Ratio(int batch) => _batches[batch].Ratio;
    internal int LiveCount { get { var live = 0; foreach (var batch in _batches) live += batch.Live; return live; } }
    internal bool Finished => _birthCurrent <= 1e-4f && LiveCount == 0;
    internal Vector2 TileScale => _tileScale;

    // Steps the birth envelope from last frame's handed-out total, splits the
    // result across batches in order, then runs every batch at clock `age`.
    internal void Update(double age, float delta, bool releasing, FalloutEffectShaderEmitter emitter)
    {
        var current = 0f;
        foreach (var batch in _batches) current += batch.Ratio * N;
        var available = _birthCurrent = FalloutEffectShaderTiming.Step(_birth, current, delta, age, releasing, N);
        foreach (var batch in _batches)
        {
            var amount = Math.Min(available, N);
            available -= amount;
            batch.Ratio = amount / N;
            batch.Update(age, emitter);
        }
    }

    // Visits live slots; returns false when a slot is outside its life.
    internal bool Sample(int batch, int slot, double clock, out FalloutEffectShaderParticleSample sample) =>
        _batches[batch].Sample(slot, clock, out sample);

    internal static float Scale(FalloutEffectShaderParticles source, float fraction)
    {
        float s1 = source.ScaleKeys[0].Scale, s2 = source.ScaleKeys[1].Scale, t1 = source.ScaleKeys[0].Time, t2 = source.ScaleKeys[1].Time;
        if (fraction <= t1) return Lerp(0, s1, Ratio(fraction, 0, t1));
        if (fraction <= t2) return Lerp(s1, s2, Ratio(fraction, t1, t2));
        return Lerp(s2, 0, Ratio(fraction, t2, 1));
    }

    // Alpha starts and ends at zero; the end keys keep their colour.
    internal static Vector4 Color(FalloutEffectShaderParticles source, float fraction)
    {
        var keys = source.ColorKeys;
        static Vector4 Key(FalloutEffectShaderColorKey key, bool clear) =>
            new(key.Color.Red / 255f, key.Color.Green / 255f, key.Color.Blue / 255f, clear ? 0 : key.Alpha);
        if (fraction <= keys[0].Time) return Vector4.Lerp(Key(keys[0], true), Key(keys[0], false), Ratio(fraction, 0, keys[0].Time));
        if (fraction <= keys[1].Time) return Vector4.Lerp(Key(keys[0], false), Key(keys[1], false), Ratio(fraction, keys[0].Time, keys[1].Time));
        if (fraction <= keys[2].Time) return Vector4.Lerp(Key(keys[1], false), Key(keys[2], false), Ratio(fraction, keys[1].Time, keys[2].Time));
        return Vector4.Lerp(Key(keys[2], false), Key(keys[2], true), Ratio(fraction, keys[2].Time, 1));
    }

    // Rotation uses the absolute clock, not the particle age, wrapped to [-pi, pi].
    internal static float Angle(float initial, float spin, double clock) =>
        (float)Math.IEEERemainder(initial + spin * clock, Math.Tau);

    private static float Lerp(float from, float to, float weight) => from + (to - from) * weight;
    private static float Ratio(float value, float start, float end) => end > start ? (value - start) / (end - start) : 1;
    private static float VariationRatio(float variation, float value) => value == 0 ? 0 : variation / value;
    private float Uniform(float low, float high) => low + (high - low) * _random.NextSingle();

    private sealed class Batch
    {
        private readonly FalloutEffectShaderParticleCloud _owner;
        private readonly Vector3[] _position = new Vector3[N], _direction = new Vector3[N];
        private readonly double[] _birth = new double[N];
        private readonly float[] _life = new float[N], _angle = new float[N], _spin = new float[N], _speed = new float[N];
        private readonly Vector2[] _tile = new Vector2[N];
        private double _previous, _carry;
        internal float Ratio;
        internal int Live;

        internal Batch(FalloutEffectShaderParticleCloud owner, uint columns, uint rows)
        {
            _owner = owner;
            var source = owner._source;
            // Multipliers, angles and tiles are drawn once per slot, not per rebirth.
            var life = VariationRatio(source.LifetimeVariation, source.Lifetime);
            var speed = VariationRatio(source.NormalSpeedVariation, source.NormalSpeed);
            float r0 = source.Rotation ?? 0, rv = source.RotationVariation ?? 0, w0 = source.RotationSpeed ?? 0, wv = source.RotationSpeedVariation ?? 0;
            for (var slot = 0; slot < N; slot++)
            {
                Clear(slot);
                _life[slot] = owner.Uniform(1 - life, 1 + life);
                _angle[slot] = owner.Uniform(r0 - rv, r0 + rv) * MathF.PI / 180;
                _spin[slot] = owner.Uniform(w0 - wv, w0 + wv) * MathF.PI / 180;
                _speed[slot] = owner.Uniform(1 - speed, 1 + speed);
                _tile[slot] = new((float)owner._random.Next((int)columns) / columns, (float)owner._random.Next((int)rows) / rows);
            }
        }

        private bool Spawned(int slot) => _position[slot].X != float.MaxValue;

        private void Clear(int slot)
        {
            _position[slot] = new(float.MaxValue); _birth[slot] = -10000; _direction[slot] = Vector3.Zero;
        }

        internal void Update(double clock, FalloutEffectShaderEmitter emitter)
        {
            var lifetime = _owner._source.Lifetime;
            var limit = Math.Min(N, (int)(N * Ratio));
            var births = 0;
            if (lifetime > 0)
            {
                var due = Math.Abs(clock - _previous) / lifetime * limit + _carry;
                births = (int)due; _carry = due - births;
            }
            for (var slot = 0; slot < N; slot++)
            {
                var age = clock - _birth[slot];
                if (age >= 0 && age * _life[slot] <= lifetime) continue;
                if (Spawned(slot)) { Clear(slot); Live--; }
                if (births <= 0 || Live >= limit) continue;
                emitter(_owner._random, out _position[slot], out _direction[slot]);
                _birth[slot] = _previous;
                births--; Live++;
            }
            _previous = clock;
        }

        internal bool Sample(int slot, double clock, out FalloutEffectShaderParticleSample sample)
        {
            sample = default;
            var source = _owner._source;
            var scaledAge = (float)((clock - _birth[slot]) * _life[slot]);
            if (!Spawned(slot) || scaledAge < 0 || source.Lifetime <= 0 || scaledAge > source.Lifetime) return false;
            var fraction = scaledAge / source.Lifetime;
            var units = _owner._units;
            var direction = _direction[slot];
            var velocity = _owner._velocity + direction * (source.NormalSpeed * _speed[slot] * units);
            var acceleration = _owner._acceleration + direction * (source.NormalAcceleration * units);
            var centre = _position[slot] + velocity * scaledAge + acceleration * (.5f * scaledAge * scaledAge);
            sample = new(centre, Scale(source, fraction) * ViewHalfSize * units, Angle(_angle[slot], _spin[slot], clock),
                Color(source, fraction), _tile[slot]);
            return true;
        }
    }
}
