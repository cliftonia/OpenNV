using OpenNV.Runtime.Content;
using OpenNV.Runtime.Formats.Gamebryo;
using Numerics = System.Numerics;

// Synthetic contracts for EFSH timing and particles and for the NIF particle
// update: emitter-controller emission, its schedule, forces, colliders, spawn.
internal static class VisualEffectContracts
{
    internal static void Run()
    {
        FadeCurve();
        ParticleMaths();
        ParticleCloud();
        NifEmission();
        NifUpdateOrder();
        NifForcesAndColliders();
        NifSpawn();
        Console.WriteLine("OPENNV_EFFECT_SHADER_FADE_CURVE_PASS stepped=true release=true pulse=true holes=true addons=true");
        Console.WriteLine("OPENNV_EFFECT_SHADER_PARTICLE_PASS scale=true color=true rotation=true births=true deaths=true motion=true finish=true");
        Console.WriteLine("OPENNV_NIF_PARTICLE_UPDATE_PASS emission=true windows=true backlog=true loop=true order=true forces=true colliders=true spawn=true parity=unverified");
    }

    private static void FadeCurve()
    {
        var envelope = new FalloutEffectShaderEnvelope(FadeIn: 1, FullTime: 1, FadeOut: 2, Persistent: .2f, Full: 1);
        var value = 0f;
        for (var age = .25; age <= 1; age += .25) value = FalloutEffectShaderTiming.Step(envelope, value, .25f, age - .25, false, 1);
        Require(Near(value, 1), "fade-in steps to the full value");
        Require(FalloutEffectShaderTiming.Step(envelope, .3f, .1f, 1.5, false, 1) == 1, "full time holds the full value");
        Require(Near(FalloutEffectShaderTiming.Step(envelope, 1, .5f, 2.5, false, 1), .8f), "fade-out ramps from the current value");
        Require(Near(FalloutEffectShaderTiming.Step(envelope, .5f, .5f, 2.5, false, 1), .3f), "fade-out ramp is incremental");
        Require(FalloutEffectShaderTiming.Step(envelope, .9f, .1f, 9, false, 1) == .2f, "persistent value after fade-out");
        Require(Near(FalloutEffectShaderTiming.Step(envelope, .5f, .25f, .5, true, 1), .75f), "release never cuts fade-in");
        Require(Near(FalloutEffectShaderTiming.Step(envelope, .8f, .1f, 2.5, true, 1), .76f), "release uses the source ramp rate");
        Require(FalloutEffectShaderTiming.Step(envelope, .8f, .1f, 2.5, true, FalloutEffectShaderTiming.ParticleBatchCapacity) == 0,
            "particle release scales by the batch capacity");
        var flat = envelope with { Persistent = 1 };
        Require(Near(FalloutEffectShaderTiming.Step(flat, 1, .1f, 3, true, 1), 1 - .033f), "release has a minimum rate");
        var birth = FalloutEffectShaderTiming.ParticleBirth(new(0, 1, 0, .5f, 1200));
        Require(birth.Full == 1200 && birth.Persistent == 39 && FalloutEffectShaderTiming.BatchCount(birth) == 16, "birth ratio scaling");
        Require(FalloutEffectShaderTiming.Displayed(.6f, new(0, 2), .3) == .6f, "zero pulse keeps the base alpha");
        Require(Near(FalloutEffectShaderTiming.Displayed(.4f, new(.5f, 1), .25), .6f), "pulse modulates the base alpha");
        var holes = new FalloutEffectShaderHoles(0, 2, 0, 200, null, null);
        Require(FalloutEffectShaderTiming.HolesThreshold(holes, 1) == 100 && FalloutEffectShaderTiming.HolesThreshold(holes, 5) == 200 &&
            FalloutEffectShaderTiming.HolesThreshold(holes with { EndValue = null }, 1) is null, "holes threshold lerp");
        var addons = new FalloutEffectShaderAddons(null, .75f, 2, .5f, .2f, 1, 1);
        var clock = new FalloutEffectShaderAddonClock();
        Require(Near(clock.Scale(addons, false), .5f), "add-on starts at its start scale");
        clock.Step(addons, false, .375f);
        Require(Near(clock.Alpha, .5f) && Near(clock.Scale(addons, false), .6875f), "add-on fade and scale in");
        clock.Step(addons, true, 1);
        Require(clock.Alpha == 0 && Near(clock.Scale(addons, true), .2f), "add-on fades and scales out");
    }

    private static FalloutEffectShader Effect(float full, float lifetime)
    {
        var membrane = new FalloutEffectShaderMembrane(new(5, 1, 3, 6), default, new(0, 0, 0, 0, 0), new(0, 0), default, 1,
            default, new(0, 0, 0, 0, 0), new(0, 0));
        var particles = new FalloutEffectShaderParticles(new(5, 1, 4, 6), new(0, 100, 0, 0, full), lifetime, 0, 2, 0,
            new(1, 0, 0), new(0, 0, -2), [new(2, .25f), new(1, .75f)],
            [new(new(255, 0, 0, 0), .5f, .25f), new(new(0, 255, 0, 0), .8f, .5f), new(new(0, 0, 255, 0), .4f, .75f)],
            0, 0, 0, 0, 0);
        return new(new("Synthetic.esm", 0x800), 1, null, "textures\\p.dds", null, membrane, particles,
            new(null, null, null, null, null, null), new(null, null, null, null, null, null, null), 0, 1, 1, 308);
    }

    private static void ParticleMaths()
    {
        var source = Effect(.5f, 1).Particles;
        Require(FalloutEffectShaderParticleCloud.Scale(source, 0) == 0 && Near(FalloutEffectShaderParticleCloud.Scale(source, .125f), 1) &&
            Near(FalloutEffectShaderParticleCloud.Scale(source, .5f), 1.5f) && Near(FalloutEffectShaderParticleCloud.Scale(source, .875f), .5f) &&
            Near(FalloutEffectShaderParticleCloud.Scale(source, 1), 0), "piecewise size grows from and shrinks to zero");
        var start = FalloutEffectShaderParticleCloud.Color(source, 0);
        var key = FalloutEffectShaderParticleCloud.Color(source, .25f);
        var middle = FalloutEffectShaderParticleCloud.Color(source, .375f);
        var end = FalloutEffectShaderParticleCloud.Color(source, 1);
        Require(start == new Numerics.Vector4(1, 0, 0, 0) && Near(key.W, .5f) && key.X == 1 &&
            Near(middle.X, .5f) && Near(middle.Y, .5f) && Near(middle.W, .65f) && end == new Numerics.Vector4(0, 0, 1, 0),
            "colour keys with zero alpha at both ends");
        Require(Near(FalloutEffectShaderParticleCloud.Angle(0, MathF.PI, 1.5), -MathF.PI / 2), "rotation uses the wrapped absolute clock");
    }

    private static void ParticleCloud()
    {
        var cloud = new FalloutEffectShaderParticleCloud(Effect(.5f, 1), new(1, 0, 0), new(0, 0, -2), 1, new Random(7));
        void Emit(Random random, out Numerics.Vector3 position, out Numerics.Vector3 direction) { position = default; direction = Numerics.Vector3.UnitY; }
        Require(cloud.BatchCount == 1, "a half birth ratio fits one batch");
        cloud.Update(0, 0, false, Emit);
        Require(cloud.LiveCount == 0 && Near(cloud.Ratio(0), .5f), "first update hands out the ratio without births");
        cloud.Update(.1, .1f, false, Emit);
        Require(cloud.LiveCount == 3, "births are the population limit per lifetime with fractional carry");
        var sampled = 0;
        for (var slot = 0; slot < FalloutEffectShaderTiming.ParticleBatchCapacity; slot++)
        {
            if (!cloud.Sample(0, slot, .1, out var sample)) continue;
            sampled++;
            // Born at the previous clock: centre = P + (V + n Sn) t + a t^2 / 2.
            Require(Near(sample.Centre.X, .1f) && Near(sample.Centre.Y, .2f) && Near(sample.Centre.Z, -.01f), "particle motion");
            Require(Near(sample.HalfSize, FalloutEffectShaderParticleCloud.Scale(Effect(.5f, 1).Particles, .1f) * FalloutEffectShaderParticleCloud.ViewHalfSize),
                "FOV-independent half size");
        }
        Require(sampled == 3, "live slots sample");
        for (var age = .2; age <= 2.001; age += .1) cloud.Update(age, .1f, false, Emit);
        Require(cloud.LiveCount is >= 35 and <= 39, $"steady population approaches its limit ({cloud.LiveCount})");
        var age2 = 101.0;
        for (var frame = 0; frame < 40; frame++, age2 += .1) cloud.Update(age2, .1f, true, Emit);
        Require(cloud.Finished, "released particles stop being born and die out");
    }

    private static FalloutNifParticleEmitterClock Clock(Func<float, bool>? active = null, float[]? keys = null, bool keyed = false) =>
        new() { Rate = _ => 10, RateKeyed = keyed, Active = active ?? (_ => true), ActiveKeys = keys, LoopStart = 0, LoopStop = 1 };

    private static void NifEmission()
    {
        var ages = new float[FalloutNifParticleEmitterClock.MaximumPerAdvance];
        Require(FalloutNifParticleEmitterClock.Interval(10, .05, .35, 0, double.PositiveInfinity, ages) == 3 &&
            Near(ages[0], .25f) && Near(ages[1], .15f) && Near(ages[2], .05f), "interval births at exact multiples");
        Require(FalloutNifParticleEmitterClock.Interval(1000, 0, 1, 0, double.PositiveInfinity, ages) == 15, "interval cap");
        var constant = Clock();
        Require(constant.Advance(0, 1, ages) == 0, "first controller update emits nothing");
        var total = 0;
        for (var frame = 1; frame <= 640; frame++) total += constant.Advance(frame / 64.0, 1, ages);
        Require(total == 100, $"floor-from-start emission has no drift ({total})");
        Require(constant.Advance(15, 1, ages) == 0 && constant.Stored == 15, "a long gap discards the backlog");
        float[] keys = [0, .1f, .2f, .5f];
        var windows = Clock(time => time is >= .1f and < .2f || time >= .5f, keys);
        windows.Rate = _ => 100;
        windows.Advance(0, 10, ages);
        Require(windows.Advance(.3, 10, ages) == 10 && Near(ages[0], .19f) && Near(ages[9], .1f), "closed window ages measured at now");
        Require(windows.Advance(.625, 10, ages) == 12 && Near(ages[0], .115f) && Near(ages[11], .005f), "open window emits up to now");
        var looping = Clock(keyed: true);
        looping.Advance(.9, 1, ages);
        Require(looping.Advance(.1, 1, ages) == 2 && Near(ages[0], .1f) && Near(ages[1], 0), "a looped controller emits its tail and head");
        Require(Near(FalloutNifParticleEmission.Sin(MathF.PI / 2), 1) && Near(FalloutNifParticleEmission.Sin(-MathF.PI / 2), -1) &&
            Near(FalloutNifParticleEmission.Cos(0), 1), "table sine and cosine");
    }

    private static FalloutNifParticleModifier Header(int index, string type, uint order) =>
        new(new FalloutNifBlock(index, type, 0, 0), $"M{index}", order, 0, true);

    private static void NifUpdateOrder()
    {
        var emitter = new FalloutNifParticleBoxEmitter(new FalloutNifParticleEmitter(Header(1, "NiPSysBoxEmitter", 1000),
            0, 0, 0, 0, 0, 0, default, 1, 0, 1, 0), 0, default);
        FalloutNifParticleModifier[] modifiers = [Header(5, "NiPSysPositionModifier", 6000), emitter, Header(2, "NiPSysColorModifier", 3000),
            Header(0, "NiPSysAgeDeathModifier", 0), Header(3, "BSPSysSimpleColorModifier", 3000), Header(4, "NiPSysGravityModifier", 4000)];
        var (emitters, update) = FalloutNifParticleUpdate.Schedule(modifiers);
        Require(emitters.Single() == emitter && update.Select(value => value.Block.Index).SequenceEqual([0, 2, 3, 4, 5]),
            "emitters run first, then modifiers by order with ties in file order");
    }

    private static void NifForcesAndColliders()
    {
        Require(FalloutNifParticleForces.Gravity(0, 0, false, Godot.Vector3.Down, new(5, 0, 0), 2 * FalloutNifParticleForces.GravityStrengthScale, 1)
            == new Godot.Vector3(0, -3.2f, 0), "planar gravity carries the 1.6 strength scale");
        Require(FalloutNifParticleForces.Gravity(0, 1, false, Godot.Vector3.Down, new(5, 2, 0), 1, 1).IsEqualApprox(Godot.Vector3.Down * MathF.Exp(-2)),
            "planar decay uses the distance along the axis");
        Require(FalloutNifParticleForces.Gravity(1, .5f, false, Godot.Vector3.Up, new(2, 0, 0), 1, 1).IsEqualApprox(new Godot.Vector3(2, 0, 0) * MathF.Exp(-1)),
            "spherical decay uses the raw offset");
        Require(FalloutNifParticleForces.Gravity(1, .5f, true, Godot.Vector3.Up, new(2, 0, 0), 1, 1).IsEqualApprox(Godot.Vector3.Up * MathF.Exp(-1)),
            "spherical decay with turbulence uses the axis");
        var bomb = new FalloutNifParticleBomb(Header(6, "NiPSysBombModifier", 4000), 0, default, 1, 3, 0, 2);
        Require(FalloutNifParticleForces.Bomb(bomb, Godot.Vector3.Up, new(0, -2, 0), 1) == new Godot.Vector3(0, -3, 0),
            "planar bomb pushes away on either side");
        Require(FalloutNifParticleForces.Bomb(bomb with { DecayType = 2 }, Godot.Vector3.Up, new(0, -2, 0), 1) is null &&
            FalloutNifParticleForces.Bomb(bomb with { Symmetry = 0 }, Godot.Vector3.Up, default, 1) is null, "bomb skips beyond decay and at its centre");
        var outside = FalloutNifParticleSphere.Sweep(new(-3, 0, 0), Numerics.Vector3.UnitX, 5, 1);
        var inside = FalloutNifParticleSphere.Sweep(default, Numerics.Vector3.UnitX, 2, 1);
        Require(outside is { } entry && Near(entry.Fraction, .4f) && entry.Point == new Numerics.Vector3(-1, 0, 0) &&
            inside is { } exit && Near(exit.Fraction, .5f) && exit.Point == Numerics.Vector3.UnitX &&
            FalloutNifParticleSphere.Sweep(new(-3, 0, 0), -Numerics.Vector3.UnitX, 5, 1) is null, "sphere entry, exit and receding tests");
        Require(FalloutNifParticleSphere.Respond(-Numerics.Vector3.UnitX, -Numerics.Vector3.UnitX, .5f) == new Numerics.Vector3(.5f, 0, 0) &&
            FalloutNifParticlePlane.Respond(new(1, -2, 0), Numerics.Vector3.UnitY, .5f, false) == new Numerics.Vector3(.5f, 1, 0) &&
            FalloutNifParticlePlane.Respond(new(1, -2, 0), Numerics.Vector3.UnitY, .5f, true) == Numerics.Vector3.UnitX,
            "collider responses scale the whole reflection; immediate planar contact slides");
    }

    private static void NifSpawn()
    {
        var spawn = new FalloutNifParticleSpawn(Header(7, "NiPSysSpawnModifier", 2000), 1, .5f, 1, 3, .5f, 0, 2, 1);
        Require(FalloutNifParticleSpawnRules.Copies(spawn, 0, .1, .25) == 1 && FalloutNifParticleSpawnRules.Copies(spawn, 0, .1, .3) == 2 &&
            FalloutNifParticleSpawnRules.Copies(spawn, 1, .1, .3) == 0 && FalloutNifParticleSpawnRules.Copies(spawn, 0, .6, .3) == 0 &&
            FalloutNifParticleSpawnRules.Copies(spawn with { Minimum = 0, Maximum = 0 }, 0, 0, 0) == 1, "spawn eligibility and rounding");
        var child = FalloutNifParticleSpawnRules.ChildVelocity(spawn, new(0, 0, -2), 1, .7f, .3f);
        Require(Near(child.Z, -3) && Near(child.X, 0) && Near(child.Y, 0), "zero direction chaos keeps the parent direction; speed chaos only grows");
        var cone = FalloutNifParticleSpawnRules.ChildVelocity(spawn with { DirectionVariation = .5f }, new(0, 4, 0), 0, 1, 0);
        Require(Near(cone.Length(), 4) && Near(Numerics.Vector3.Dot(Numerics.Vector3.Normalize(cone), Numerics.Vector3.UnitY), 0),
            "the cone half-angle is direction variation times pi");
        Require(Near(FalloutNifParticleSpawnRules.ChildLife(spawn, .75f), 2.25f), "child life varies by half its range");
    }

    private static bool Near(float value, float expected) => MathF.Abs(value - expected) < 1e-4f;

    private static void Require(bool condition, string contract)
    {
        if (!condition) throw new InvalidOperationException($"Visual effect contract failed: {contract}.");
    }
}
