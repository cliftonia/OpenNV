using Godot;
using System.Diagnostics;

namespace OpenNV.Runtime.Formats.Gamebryo;

// Source declarations and clocks are instance-owned. The source NIF supplies
// emission geometry, capacity, texture, curves and forces; the renderer supplies
// camera-facing particle quads, never an authored replacement effect.
internal sealed partial class RuntimeNifParticleSystem : Node3D
{
    private FalloutNifParticleSystem _source = null!;
    private FalloutNifParticleData _data = null!;
    private FalloutNifParticleModifier[] _modifiers = [], _emitterModifiers = [], _updateModifiers = [];
    private FalloutNifParticleModifier[] _drawModifiers = [], _activeDrawModifiers = [];
    private readonly Dictionary<string, bool> _active = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FalloutNifParticleEmitterClock> _emitters = new(StringComparer.Ordinal);
    private readonly float[] _emissionAges = new float[FalloutNifParticleEmitterClock.MaximumPerAdvance];
    private readonly Dictionary<string, float> _speeds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, float> _lifespans = new(StringComparer.Ordinal);
    private readonly Dictionary<string, float> _initialRadii = new(StringComparer.Ordinal);
    private readonly Dictionary<string, float> _gravityStrengths = new(StringComparer.Ordinal);
    private readonly Dictionary<int, FalloutNifMeshData> _meshes = [];
    private readonly Dictionary<int, FalloutNifParticleSystem> _particleMeshSources = [];
    private string? _emissionError;
    private IReadOnlyDictionary<int, Node3D> _nodes = null!;
    private readonly Dictionary<int, Transform3D> _stepTransforms = [];
    private readonly Random _random = new();
    private Particle[] _particles = [];
    private float[] _drawBuffer = [];
    private NativeParticleDrawOrder[] _drawOrder = [];
    private MultiMesh _draw = null!;
    private MultiMeshInstance3D _visual = null!;
    private Aabb _publishedBounds;
    private double _nextBoundsRefresh;
    private int _visibleCount;
    private static long _publicationCalls, _publishedParticles, _uploadedFloats, _sortTicks, _packTicks, _uploadTicks;
    internal static object PublicationTiming => new
    {
        scope = "process-cumulative-main-thread;upload-includes-native-call-not-GPU-execution",
        calls = _publicationCalls,
        particles = _publishedParticles,
        uploadedFloats = _uploadedFloats,
        sortMilliseconds = _sortTicks * (1000.0 / Stopwatch.Frequency),
        packMilliseconds = _packTicks * (1000.0 / Stopwatch.Frequency),
        uploadMilliseconds = _uploadTicks * (1000.0 / Stopwatch.Frequency)
    };
    private float _units;
    private Transform3D _previousParent, _parent, _parentInverse;
    private float _parentSeconds;
    private FalloutNifParticleParentVelocity? _parentVelocity;
    internal void ResetParentMotion() { _previousParent = GlobalTransform; _parentSeconds = 0; }
    internal void BeginParentMotion(double seconds)
    {
        NativeNifWorldAxes.RefreshAncestors(this);
        _parent = GlobalTransform; _parentInverse = _parent.AffineInverse();
        _parentSeconds = (float)seconds;
    }
    internal void EndParentMotion() => _previousParent = _parent;
    internal long BoundsPublications { get; private set; }
    internal int ActiveCount { get; private set; }
    internal long BirthCount { get; private set; }
    internal long DeathCount { get; private set; }
    internal double SimulatedSeconds { get; private set; }
    internal bool EmissionEnabled { get; set; } = true;
    internal void SetOutputEncoding(bool encoded)
    {
        RequireConfigured();
        ((ShaderMaterial)_draw.Mesh.SurfaceGetMaterial(0)).SetShaderParameter("source_store_encoded", encoded);
    }
    internal void ResetCompleted()
    {
        RequireConfigured();
        if (_advancing) throw new InvalidOperationException("Particle reset cannot reenter an advancing lifetime.");
        if (ActiveCount != 0) throw new InvalidOperationException("Live particles cannot be recycled.");
        foreach (var modifier in _modifiers)
        {
            _active[modifier.Name] = modifier.Active;
            if (modifier is FalloutNifParticleVolumeEmitter volume)
            { _speeds[modifier.Name] = volume.Emitter.Speed; _lifespans[modifier.Name] = volume.Emitter.Life; _initialRadii[modifier.Name] = volume.Emitter.Radius; }
            else if (modifier is FalloutNifParticleMeshEmitter mesh)
            { _speeds[modifier.Name] = mesh.Emitter.Speed; _lifespans[modifier.Name] = mesh.Emitter.Life; _initialRadii[modifier.Name] = mesh.Emitter.Radius; }
            else if (modifier is FalloutNifParticleGravity gravity) _gravityStrengths[modifier.Name] = gravity.Strength;
        }
        foreach (var clock in _emitters.Values) clock.Reset();
        ResetLifecycle();
        BirthCount = DeathCount = CollisionCount = 0; SimulatedSeconds = 0; EmissionEnabled = false;
        _nextBoundsRefresh = 0; _publishedBounds = default;
    }
    internal IReadOnlyList<Vector3> Positions => _particles.Take(ActiveCount).Select(value => value.Position).ToArray();
    internal object Observation => new
    {
        block = _source.Block.Index,
        worldSpace = _source.WorldSpace,
        capacity = _particles.Length,
        emissionError = _emissionError,
        deferredMeshSources = _particleMeshSources.Values.Select(source => new
        {
            block = source.Block.Index,
            type = source.Block.TypeName,
            data = source.Geometry.Data,
            unbound = "source-particle-buffer-emission-sampling"
        }).ToArray(),
        active = ActiveCount,
        births = BirthCount,
        deaths = DeathCount,
        collisions = CollisionCount,
        seconds = SimulatedSeconds,
        modifiers = _modifiers.Select(modifier => new
        {
            block = modifier.Block.Index,
            modifier.Name,
            modifier.Order,
            active = _active[modifier.Name],
            emitterTime = _emitters.TryGetValue(modifier.Name, out var clock) && double.IsFinite(clock.Stored) ? clock.Stored : (double?)null,
            speed = _speeds.GetValueOrDefault(modifier.Name),
            lifespan = _lifespans.GetValueOrDefault(modifier.Name),
            initialRadius = _initialRadii.GetValueOrDefault(modifier.Name),
            gravityStrength = modifier is FalloutNifParticleGravity ? (float?)_gravityStrengths[modifier.Name] : null,
        }).ToArray(),
        // Packed fields preserve float32 values, including signed zero; the
        // render trace stores these exact bytes alongside the MultiMesh buffer.
        lifecycle = LifecycleObservation,
        keyColorOwners = _keyColors.Select(pair => new
        {
            modifier = pair.Key,
            data = pair.Value.Source.Block.Index,
            keys = pair.Value.Source.Keys.Length,
            interpolation = pair.Value.Source.Interpolation,
            clock = "particle-age / birth-lifespan",
            nativeRetailParity = "unverified"
        }).ToArray(),
        encoding = "particle:position3,velocity3,age,life,radius,angle,spin,color4:f32-le;texture:i32-le",
        state = System.Convert.ToBase64String(ObservationBytes()),
        missing = new[] { "retail-particle-identity-and-random-sequence-join", "retail-modifier-motion-parity", "retail-plane-sidedness-and-moving-plane-response",
            "retail-sphere-initial-overlap-and-moving-collider-response", "retail-color-curve-clock-and-pixels", "retail-spawn-variation-equations",
            "retail-particle-birth-event-time-and-order", "complete-particle-lifecycle-event-sink" },
    };

    private byte[] ObservationBytes()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        foreach (var particle in _particles.AsSpan(0, ActiveCount))
        {
            writer.Write(particle.Position.X); writer.Write(particle.Position.Y); writer.Write(particle.Position.Z);
            writer.Write(particle.Velocity.X); writer.Write(particle.Velocity.Y); writer.Write(particle.Velocity.Z);
            writer.Write(particle.Age); writer.Write(particle.Life); writer.Write(particle.Radius);
            writer.Write(particle.Angle); writer.Write(particle.Spin);
            writer.Write(particle.InitialColor.R); writer.Write(particle.InitialColor.G);
            writer.Write(particle.InitialColor.B); writer.Write(particle.InitialColor.A); writer.Write(particle.Texture);
        }
        return stream.ToArray();
    }

    private struct Particle
    {
        internal Vector3 Position, Velocity;
        internal float Age, Life, Radius, Angle, Spin, MotionSeconds;
        internal int Texture, SourceBirth;
        internal long Identity, ParentIdentity;
        internal ushort Generation;
        internal Color InitialColor;
        internal NativeParticleAngleCache DrawAngle;
    }

    internal static Action<string, double>? ConfigureObserver { get; set; }

    private ShaderMaterial ConfigureSource(FalloutNifFile file, FalloutNifParticleSystem source,
        IReadOnlyDictionary<int, Node3D> nodes, Material material, float units)
    {
        var observer = ConfigureObserver;
        var started = observer is null ? 0 : System.Diagnostics.Stopwatch.GetTimestamp();
        void Mark(string stage)
        {
            if (observer is null) return;
            var now = System.Diagnostics.Stopwatch.GetTimestamp();
            observer(stage, System.Diagnostics.Stopwatch.GetElapsedTime(started, now).TotalMilliseconds);
            started = now;
        }
        _source = source; _nodes = nodes; _units = units;
        _data = file.ReadObject(source.Geometry.Data) as FalloutNifParticleData ??
            throw new InvalidDataException("Particle system has no particle data.");
        // The six source keep bits request CPU attribute retention, independently
        // of compression. This owner retains its source data and particle state;
        // requesting retention does not introduce another simulation mode.
        if (_data.Maximum == 0 || _data.Additional != -1 || _data.Active != 0 || _data.HasNormals || _data.HasRotations ||
            _data.HasAxes || (_data.Keep & ~0x3f) != 0 || _data.Compress != 0 || _data.Flags != 0 || _data.Consistency != 0 ||
            _data.HasTextureIndices != (_data.Subtextures.Length != 0))
            throw new NotSupportedException($"Particle data {_data.Block.Index} has unsupported runtime fields.");
        _modifiers = source.Modifiers.Select(index => file.ReadObject(index) as FalloutNifParticleModifier ??
            throw new InvalidDataException("Particle modifier reference is not a modifier.")).OrderBy(value => value.Order).ToArray();
        (_emitterModifiers, _updateModifiers) = FalloutNifParticleUpdate.Schedule(_modifiers);
        Mark("source-and-modifier-read");
        _parentVelocity = _modifiers.OfType<FalloutNifParticleParentVelocity>().SingleOrDefault();
        if (_parentVelocity?.Damping < 0) throw new InvalidDataException("Parent velocity damping is negative.");
        foreach (var modifier in _modifiers)
        {
            if (modifier.Target != source.Block.Index || !_active.TryAdd(modifier.Name, modifier.Active))
                throw new InvalidDataException("Particle modifier has a wrong target or duplicate name.");
            switch (modifier)
            {
                case FalloutNifParticleColorKeys colors:
                    ConfigureColorKeys(file, colors);
                    break;
                case FalloutNifParticleColliderManager manager:
                    ConfigureColliders(file, manager);
                    break;
                case FalloutNifParticleVolumeEmitter volume:
                    _speeds.Add(modifier.Name, volume.Emitter.Speed);
                    RequireNode(volume.Object); ValidateEmitter(volume.Emitter);
                    if (volume switch
                    {
                        FalloutNifParticleBoxEmitter box => box.Dimensions.X < 0 || box.Dimensions.Y < 0 || box.Dimensions.Z < 0,
                        FalloutNifParticleCylinderEmitter cylinder => cylinder.CylinderRadius < 0 || cylinder.Height < 0,
                        FalloutNifParticleSphereEmitter sphere => sphere.SphereRadius < 0,
                        _ => true,
                    }) throw new InvalidDataException("Particle volume has invalid dimensions.");
                    break;
                case FalloutNifParticleMeshEmitter mesh:
                    _speeds.Add(modifier.Name, mesh.Emitter.Speed);
                    ValidateEmitter(mesh.Emitter);
                    if (mesh.Meshes.Length == 0 || mesh.VelocityType > 2 || mesh.EmissionType is not (0 or 3))
                        throw new NotSupportedException("Particle mesh emission mode has no sampler.");
                    foreach (var index in mesh.Meshes)
                    {
                        RequireNode(index);
                        if (file.ReadObject(index) is FalloutNifParticleSystem particleSource)
                        {
                            if (file.ReadObject(particleSource.Geometry.Data) is not FalloutNifParticleData)
                                throw new InvalidDataException("Particle emitter source has no declared particle data.");
                            _particleMeshSources.TryAdd(index, particleSource);
                            continue;
                        }
                        var data = file.ReadMeshData(file.ReadGeometry(index).Data);
                        if (data.Vertices.Length == 0 || mesh.VelocityType == 0 && data.Normals.Length != data.Vertices.Length ||
                            mesh.EmissionType == 3 && data.Triangles.Length == 0)
                            throw new InvalidDataException("Particle emitter mesh is incomplete.");
                        _meshes.TryAdd(index, data);
                    }
                    break;
                case FalloutNifParticleSpawn spawn:
                    FalloutNifParticleSpawnRules.Validate(spawn);
                    break;
                case FalloutNifParticleGrowFade grow when grow.Grow < 0 || grow.Fade < 0 || grow.Scale < 0:
                    throw new InvalidDataException("Particle growth contains a negative duration or scale.");
                case FalloutNifParticleGravity gravity:
                    _gravityStrengths.Add(modifier.Name, gravity.Strength);
                    RequireNode(gravity.Object);
                    if (gravity.ForceType > 1 || gravity.Decay < 0 || gravity.Turbulence < 0 || gravity.TurbulenceScale < 0)
                        throw new NotSupportedException("Particle gravity has an invalid force, decay or turbulence declaration.");
                    break;
                case FalloutNifParticleDrag drag:
                    RequireNode(drag.Object);
                    if (drag.Percentage < 0 || drag.Range < 0 || drag.Falloff < 0 || Convert(drag.Axis).LengthSquared() == 0)
                        throw new InvalidDataException("Particle drag has invalid strength, extent or axis.");
                    break;
                case FalloutNifParticleBomb bomb:
                    RequireNode(bomb.Object);
                    if (bomb.DecayType > 2 || bomb.Symmetry > 2 || bomb.Decay < 0 || Convert(bomb.Axis).LengthSquared() == 0)
                        throw new NotSupportedException("Particle bomb force is outside the declared symmetry/decay contract.");
                    break;
            }
        }
        ConfigureSpawning(file);
        Mark("modifier-and-spawn-binding");
        if (!_modifiers.OfType<FalloutNifParticleAgeDeath>().Any() ||
            !_modifiers.Any(value => value.Block.TypeName == "NiPSysPositionModifier") ||
            !_modifiers.OfType<FalloutNifParticleBounds>().Any())
            throw new NotSupportedException("Particle system lacks lifetime, position or bounds ownership.");
        _drawModifiers = _modifiers.Where(modifier => modifier is
            FalloutNifParticleGrowFade or FalloutNifParticleColor or FalloutNifParticleColorKeys).ToArray();
        _activeDrawModifiers = new FalloutNifParticleModifier[_drawModifiers.Length];
        _particles = new Particle[_data.Maximum];
        _drawOrder = new NativeParticleDrawOrder[_data.Maximum];
        _drawBuffer = new float[checked(_data.Maximum * NativeParticleDrawBuffer.Stride)];
        if (material is not ShaderMaterial shader || shader.ResourceName != NativeNifEffectMaterial.ResourceIdentity)
            throw new NotSupportedException("Particle material has no admitted source shader.");
        Mark("simulation-buffer-allocation");
        return shader;
    }

    private void ValidateEmitter(FalloutNifParticleEmitter emitter)
    {
        if (emitter.Radius < 0 || emitter.RadiusVariation < 0 || emitter.Life <= 0 || emitter.LifeVariation < 0 ||
            emitter.SpeedVariation < 0 || emitter.DeclinationVariation < 0 || emitter.PlanarVariation < 0)
            throw new InvalidDataException("Particle emitter has an invalid range.");
        _lifespans.Add(emitter.Name, emitter.Life);
        _initialRadii.Add(emitter.Name, emitter.Radius);
    }

    private void RequireNode(int index)
    {
        if (!_nodes.ContainsKey(index)) throw new InvalidDataException($"Particle transform {index} is not reachable.");
    }

    internal RuntimeNifControllerChannel Bind(FalloutNifFile file, FalloutNifControllerLink link)
    {
        RequireConfigured();
        if (link.ControllerType == "NiPSysGravityStrengthCtlr")
        {
            if (!ReferenceEquals(file, _sourceFile) || link.PropertyType.Length != 0 || link.Variable2.Length != 0 ||
                link.Priority != 0 || !_gravityStrengths.ContainsKey(link.Variable1) ||
                file.ReadObject(link.Controller) is not FalloutNifParticleController controller ||
                controller.Block.TypeName != link.ControllerType || controller.Time.Target != _source.Block.Index ||
                controller.Modifier != link.Variable1 || (controller.Time.Flags & 0x40) == 0)
                throw new NotSupportedException("Particle gravity-strength channel has no declared gravity owner.");
            var sampler = new FalloutNifFloatAnimation(file, link.Interpolator);
            return new(time =>
            {
                RequireConfigured();
                var strength = sampler.Sample(time);
                if (!float.IsFinite(strength)) throw new InvalidDataException("Particle gravity strength is not finite.");
                _gravityStrengths[link.Variable1] = strength;
            });
        }
        if (link.ControllerType == "NiPSysEmitterInitialRadiusCtlr")
        {
            if (link.PropertyType.Length != 0 || link.Variable2.Length != 0 || link.Priority != 0 ||
                !_initialRadii.ContainsKey(link.Variable1))
                throw new NotSupportedException("Particle initial-radius channel has no declared emitter owner.");
            var sampler = new FalloutNifFloatAnimation(file, link.Interpolator);
            return new(time =>
            {
                RequireConfigured();
                var radius = sampler.Sample(time);
                if (!float.IsFinite(radius) || radius < 0)
                    throw new InvalidDataException("Particle initial radius is negative or not finite.");
                _initialRadii[link.Variable1] = radius;
            });
        }
        if (link.ControllerType == "NiPSysEmitterLifeSpanCtlr" && link.Variable2.Length == 0 && _lifespans.ContainsKey(link.Variable1))
        {
            var sampler = new FalloutNifFloatAnimation(file, link.Interpolator);
            return new(time =>
            {
                RequireConfigured();
                var value = sampler.Sample(time);
                if (!float.IsFinite(value) || value < 0) throw new InvalidDataException("Particle emitter lifespan is not finite/nonnegative.");
                _lifespans[link.Variable1] = value;
            });
        }
        if (link.ControllerType == "NiPSysEmitterSpeedCtlr" && link.Variable2.Length == 0 && _speeds.ContainsKey(link.Variable1))
        {
            var sampler = new FalloutNifFloatAnimation(file, link.Interpolator);
            return new(time =>
            {
                RequireConfigured();
                var speed = sampler.Sample(time);
                if (!float.IsFinite(speed)) throw new InvalidDataException("Particle emitter speed is not finite.");
                _speeds[link.Variable1] = speed;
            });
        }
        if (!_active.ContainsKey(link.Variable1)) throw new InvalidDataException("Particle controller targets a missing modifier.");
        // The emitter controller only records its time here; the system update
        // emits from it before any modifier runs.
        if (link.ControllerType == "NiPSysEmitterCtlr" && link.Variable2 == "BirthRate" && _lifespans.ContainsKey(link.Variable1))
        {
            var sampler = new FalloutNifFloatAnimation(file, link.Interpolator);
            var clock = EmitterClock(file, link);
            clock.Rate = time => Math.Max(0, sampler.Sample(time));
            clock.RateBegin = sampler.FirstKeyTime; clock.RateKeyed = sampler.Keyed;
            return new(time => { RequireConfigured(); clock.Now = time; });
        }
        if (link.ControllerType == "NiPSysEmitterCtlr" && link.Variable2 == "EmitterActive" && _lifespans.ContainsKey(link.Variable1))
        {
            var sampler = new FalloutNifBoolAnimation(file, link.Interpolator);
            var clock = EmitterClock(file, link);
            if (sampler.ConstantValue is { } constant) { clock.Active = _ => constant; clock.ActiveKeys = null; }
            else { clock.Active = sampler.Sample; clock.ActiveKeys = sampler.BoundaryTimes.Order().ToArray(); }
            return new(time => { RequireConfigured(); clock.Now = time; });
        }
        if (link.ControllerType == "NiPSysModifierActiveCtlr" && link.Variable2.Length == 0)
        {
            var sampler = new FalloutNifBoolAnimation(file, link.Interpolator);
            return new(time =>
            {
                RequireConfigured();
                _active[link.Variable1] = sampler.Sample(time);
            }, sampler.BoundaryTimes);
        }
        throw new NotSupportedException($"Particle channel {link.Variable1}/{link.Variable2} is unsupported.");
    }

    private FalloutNifParticleEmitterClock EmitterClock(FalloutNifFile file, FalloutNifControllerLink link)
    {
        if (_emitters.TryGetValue(link.Variable1, out var clock)) return clock;
        var controller = file.ReadObject(link.Controller) as FalloutNifParticleController ??
            throw new InvalidDataException("Particle emitter channel has no source controller.");
        clock = new() { Rate = _ => 0, Active = _ => true, LoopStart = controller.Time.StartTime, LoopStop = controller.Time.StopTime };
        _emitters.Add(link.Variable1, clock);
        return clock;
    }

    public override void _Ready()
    {
        RequireConfigured();
        if (_source.WorldSpace) { _visual.TopLevel = true; _visual.GlobalTransform = Transform3D.Identity; }
        ResetParentMotion();
    }

    public override void _Process(double delta)
    {
        using var frameWork = OpenNV.Runtime.Diagnostics.RuntimeFrameWork.Measure(OpenNV.Runtime.Diagnostics.RuntimeFrameWork.Owner.Particle);
        BeginParentMotion(delta); Advance((float)delta); EndParentMotion();
        Publish();
    }

    internal void Advance(float delta)
    {
        RequireConfigured();
        if (!float.IsFinite(delta) || delta < 0) throw new ArgumentOutOfRangeException(nameof(delta));
        if (_advancing) throw new InvalidOperationException("Particle lifetime cannot reenter an advance.");
        var end = SimulatedSeconds + delta;
        if (!double.IsFinite(end)) throw new InvalidDataException("Particle lifetime clock overflowed.");
        _advancing = true;
        try
        {
            // One variable step per update; each particle then advances by
            // the time since its own last integration.
            _stepOrdinal = checked(_stepOrdinal + 1);
            Step(delta);
            SimulatedSeconds = end;
        }
        finally { _advancing = false; }
    }

    private void Step(float delta)
    {
        // Source node poses stay fixed during this synchronous modifier pass,
        // unless a lifecycle observer changes them. Never retain a pose across
        // simulation steps or reuse one while callbacks own that possibility.
        _stepTransforms.Clear();
        for (var i = 0; i < ActiveCount; i++) _particles[i].MotionSeconds = delta;
        EmitFromControllers();
        foreach (var modifier in _updateModifiers)
        {
            if (!_active[modifier.Name]) continue;
            switch (modifier)
            {
                case FalloutNifParticleAgeDeath age: AgeAndRetire(age); break;
                case FalloutNifParticleColliderManager manager: Collide(manager); break;
                case FalloutNifParticleGravity gravity: ApplyGravity(gravity); break;
                case FalloutNifParticleWind wind: ApplyWind(wind); break;
                case FalloutNifParticleBomb bomb: ApplyBomb(bomb); break;
                case FalloutNifParticleDrag drag: ApplyDrag(drag); break;
                case FalloutNifParticleRotation:
                    for (var i = 0; i < ActiveCount; i++) _particles[i].Angle += _particles[i].Spin * _particles[i].MotionSeconds;
                    break;
                case { Block.TypeName: "NiPSysPositionModifier" }:
                    for (var i = 0; i < ActiveCount; i++)
                    {
                        _particles[i].Position += _particles[i].Velocity * _particles[i].MotionSeconds;
                        _particles[i].MotionSeconds = 0;
                    }
                    break;
            }
        }
    }

    // Emission belongs to the emitter controllers and precedes every modifier.
    // Each age is the time since that particle's exact birth.
    private void EmitFromControllers()
    {
        foreach (var modifier in _emitterModifiers)
        {
            if (!_emitters.TryGetValue(modifier.Name, out var clock) || clock.Now is not { } now) continue;
            clock.Now = null;
            var count = clock.Advance(now, _lifespans[modifier.Name], _emissionAges);
            if (!EmissionEnabled || !_active[modifier.Name]) continue;
            for (var index = 0; index < count; index++)
                if (!Emit(modifier, _emissionAges[index])) break;
        }
    }

    // One birth of the given age; false when capacity stops this emission call.
    private bool Emit(FalloutNifParticleModifier modifier, float age)
    {
        var emitter = modifier switch
        {
            FalloutNifParticleVolumeEmitter source => source.Emitter,
            FalloutNifParticleMeshEmitter source => source.Emitter,
            _ => throw new InvalidOperationException("Particle source is not an emitter."),
        };
        // Life and speed vary by half their range each way; angles and radius by the full range.
        var life = Vary(_lifespans[emitter.Name], emitter.LifeVariation);
        if (age > life) return true;
        if (ActiveCount >= _particles.Length) { EmitterCapacityRefusals = checked(EmitterCapacityRefusals + 1); return false; }
        var speed = Vary(_speeds[modifier.Name], emitter.SpeedVariation) * _units;
        var direction = Convert(FalloutNifParticleEmission.Direction(emitter.Declination + emitter.DeclinationVariation * Signed(),
            emitter.PlanarAngle + emitter.PlanarVariation * Signed()));
        Transform3D transform;
        Vector3 point, velocity;
        if (modifier is FalloutNifParticleVolumeEmitter volume)
        {
            transform = TransformOf(volume.Object);
            point = VolumePoint(volume) * _units;
            velocity = (transform.Basis * direction).Normalized() * speed;
        }
        else
        {
            var mesh = (FalloutNifParticleMeshEmitter)modifier;
            var index = mesh.Meshes[_random.Next(mesh.Meshes.Length)];
            if (_particleMeshSources.TryGetValue(index, out var particleSource))
            {
                _emissionError = $"Particle system {_source.Block.Index} emitter {mesh.Block.Index} reached source {index} " +
                    $"({particleSource.Block.TypeName}), emission {mesh.EmissionType}, velocity {mesh.VelocityType}: " +
                    "source particle-buffer emission sampling and retained position-slot semantics are unbound.";
                SetProcess(false);
                throw new NotSupportedException(_emissionError);
            }
            transform = TransformOf(index);
            var geometry = _meshes[index];
            Vector3 normal;
            if (mesh.EmissionType == 3)
            {
                // Face surface: s = sqrt(u1) towards a point on the opposite edge.
                var triangle = geometry.Triangles[_random.Next(geometry.Triangles.Length)];
                Vector3 v0 = Convert(geometry.Vertices[triangle.A]), v1 = Convert(geometry.Vertices[triangle.B]), v2 = Convert(geometry.Vertices[triangle.C]);
                var reach = MathF.Sqrt(_random.NextSingle());
                point = v1 + (v0 + (v2 - v0) * _random.NextSingle() - v1) * reach;
                normal = mesh.VelocityType == 0 ? (Convert(geometry.Normals[triangle.A]) + Convert(geometry.Normals[triangle.B]) +
                    Convert(geometry.Normals[triangle.C])).Normalized() : default;
            }
            else
            {
                var vertex = _random.Next(geometry.Vertices.Length);
                point = Convert(geometry.Vertices[vertex]);
                normal = mesh.VelocityType == 0 ? Convert(geometry.Normals[vertex]) : default;
            }
            point *= _units;
            velocity = mesh.VelocityType switch
            {
                0 => (transform.Basis * normal).Normalized() * MathF.Abs(speed),
                // A random direction is not turned by the mesh transform.
                1 => new Vector3(Signed(), Signed(), Signed()).Normalized() * speed,
                _ => (transform.Basis * Convert(mesh.Axis)).Normalized() * speed,
            };
        }
        var particle = new Particle
        {
            Position = transform * point,
            Velocity = velocity,
            // Age/death adds the birth age through MotionSeconds, so it is counted once.
            MotionSeconds = age,
            Life = Math.Max(float.Epsilon, life),
            Radius = Math.Max(0, _initialRadii[emitter.Name] + emitter.RadiusVariation * Signed()) * _units * transform.Basis.Scale.Abs().X,
            InitialColor = ToColor(emitter.Color),
            SourceBirth = emitter.Block.Index,
            Texture = _data.Subtextures.Length == 0 ? 0 : _random.Next(_data.Subtextures.Length),
        };
        if (_source.WorldSpace && _parentSeconds > 0 && _parentVelocity is { } inherit && _active[inherit.Name])
            particle.Velocity += (particle.Position - _previousParent * (_parentInverse * particle.Position)) * (inherit.Damping / _parentSeconds);
        foreach (var update in _updateModifiers)
        {
            if (update is not FalloutNifParticleRotation rotation || !_active[rotation.Name]) continue;
            particle.Angle = rotation.Angle + rotation.AngleVariation * Signed();
            particle.Spin = rotation.Speed + rotation.SpeedVariation * Signed();
            if (rotation.RandomSign && _random.Next(2) == 0) particle.Spin = -particle.Spin;
        }
        AppendParticle(particle, false, modifier.Block.Index, "emitter");
        return true;
    }

    // Sphere and cylinder radii are linear in u, so births favour the centre;
    // the sphere's two angles both span a full turn.
    private Vector3 VolumePoint(FalloutNifParticleVolumeEmitter volume)
    {
        if (volume is FalloutNifParticleBoxEmitter box)
            return Convert(new FalloutNifVector3(box.Dimensions.X * Centered(), box.Dimensions.Y * Centered(), box.Dimensions.Z * Centered()));
        if (volume is FalloutNifParticleSphereEmitter sphere)
        {
            var radius = sphere.SphereRadius * _random.NextSingle();
            var azimuth = _random.NextSingle() * MathF.Tau; var polar = _random.NextSingle() * MathF.Tau;
            return Convert(new FalloutNifVector3(radius * MathF.Sin(polar) * MathF.Cos(azimuth), radius * MathF.Sin(polar) * MathF.Sin(azimuth), radius * MathF.Cos(polar)));
        }
        if (volume is FalloutNifParticleCylinderEmitter cylinder)
        {
            var angle = _random.NextSingle() * MathF.Tau; var radius = _random.NextSingle() * cylinder.CylinderRadius;
            return Convert(new FalloutNifVector3(radius * MathF.Cos(angle), radius * MathF.Sin(angle), Centered() * cylinder.Height));
        }
        throw new NotSupportedException("Particle volume has no source sampler.");
    }

    private Transform3D TransformOf(int index)
    {
        var cacheable = _advancing && LifecycleEvent is null;
        if (cacheable && _stepTransforms.TryGetValue(index, out var retained)) return retained;
        var source = NativeNifWorldAxes.Effective(_nodes[index]);
        var transform = _source.WorldSpace ? source.GlobalTransform :
            GlobalTransform.AffineInverse() * source.GlobalTransform;
        if (cacheable) _stepTransforms.Add(index, transform);
        return transform;
    }
    private float Centered() => (float)_random.NextDouble() - .5f;
    private float Vary(float value, float variation) => value + variation * Centered();
    private float Signed() => _random.NextSingle() * 2 - 1;

    internal void Publish()
    {
        RequireConfigured();
        if (_advancing) throw new InvalidOperationException("Particle publication cannot reenter an advancing lifetime.");
        if (_visibleCount != ActiveCount) { _draw.VisibleInstanceCount = ActiveCount; _visibleCount = ActiveCount; }
        if (ActiveCount == 0) return;
        var sortStarted = Stopwatch.GetTimestamp();
        var camera = GetViewport().GetCamera3D();
        var basis = camera?.GlobalBasis.Orthonormalized() ?? Basis.Identity;
        if (!_source.WorldSpace) basis = GlobalBasis.Inverse() * basis;
        // Alpha quads are sorted within this system; authored material depth
        // and blend state still determine their relationship to the world.
        if (camera is not null)
        {
            var origin = _source.WorldSpace ? camera.GlobalPosition : ToLocal(camera.GlobalPosition);
            // Rendering never reorders simulation storage or its identity map.
            // Compute each key once and sort reusable distance/index values.
            for (var index = 0; index < ActiveCount; index++)
            {
                _drawOrder[index] = new(_particles[index].Position.DistanceSquaredTo(origin), index);
            }
            Array.Sort(_drawOrder, 0, ActiveCount);
        }
        var packStarted = Stopwatch.GetTimestamp();
        var minimum = new Vector3(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
        var maximum = -minimum;
        // Controller activity is stable during this synchronous publication.
        // Resolve only draw modifiers once, retaining their source order.
        var drawModifierCount = 0;
        foreach (var modifier in _drawModifiers)
            if (_active[modifier.Name]) _activeDrawModifiers[drawModifierCount++] = modifier;
        for (var i = 0; i < ActiveCount; i++)
        {
            ref var particle = ref _particles[camera is null ? i : _drawOrder[i].Index];
            var radius = particle.Radius;
            var color = particle.InitialColor;
            for (var modifierIndex = 0; modifierIndex < drawModifierCount; modifierIndex++)
            {
                var modifier = _activeDrawModifiers[modifierIndex];
                if (modifier is FalloutNifParticleGrowFade grow)
                {
                    radius *= GrowFadeScale(grow, particle.Age, particle.Life, particle.Generation);
                }
                else if (modifier is FalloutNifParticleColor colors) color = ParticleColor(colors, particle.Age / particle.Life);
                else if (modifier is FalloutNifParticleColorKeys keys)
                    color = ToColor(_keyColors[keys.Block.Index].Sample(particle.Age / particle.Life));
            }
            var (sin, cos) = particle.DrawAngle.Get(particle.Angle);
            var transform = NativeParticleDrawBuffer.Billboard(basis, sin, cos, radius, particle.Position);
            var extent = transform.Basis.X.Abs() + transform.Basis.Y.Abs();
            minimum = minimum.Min(particle.Position - extent); maximum = maximum.Max(particle.Position + extent);
            var uv = _data.Subtextures.Length == 0 ? new FalloutNifVector4(0, 1, 0, 1) : _data.Subtextures[particle.Texture];
            NativeParticleDrawBuffer.Write(_drawBuffer.AsSpan(i * NativeParticleDrawBuffer.Stride), transform, color, ToColor(uv));
        }
        var bounds = new Aabb(minimum, maximum - minimum);
        if (!_publishedBounds.Encloses(bounds) || SimulatedSeconds >= _nextBoundsRefresh)
        {
            // Conservative slack avoids a renderer bounds update for every
            // moving quad. Escaping particles expand it immediately; periodic
            // refits keep dead particles from leaving an ever-growing box.
            _publishedBounds = bounds.Grow(Math.Max(_units, bounds.Size.Length() * .125f));
            _draw.CustomAabb = _publishedBounds;
            _nextBoundsRefresh = SimulatedSeconds + 1;
            BoundsPublications++;
        }
        var uploadStarted = Stopwatch.GetTimestamp();
        _draw.Buffer = _drawBuffer;
        _sortTicks += packStarted - sortStarted;
        _packTicks += uploadStarted - packStarted;
        _uploadTicks += Stopwatch.GetTimestamp() - uploadStarted;
        ++_publicationCalls;
        _publishedParticles += ActiveCount;
        _uploadedFloats += _drawBuffer.Length;
    }

    internal static Color ParticleColor(FalloutNifParticleColor source, float fraction)
    {
        var first = ToColor(source.First); var second = ToColor(source.Second); var third = ToColor(source.Third);
        var color = fraction < source.FirstEnd ? first : fraction < source.FirstStart ?
            first.Lerp(second, (fraction - source.FirstEnd) / (source.FirstStart - source.FirstEnd)) :
            fraction <= source.SecondEnd || source.SecondStart == 0 ? second : fraction < source.SecondStart ?
            second.Lerp(third, (fraction - source.SecondEnd) / (source.SecondStart - source.SecondEnd)) : third;
        color.A = fraction < source.FadeIn ? Mathf.Lerp(first.A, second.A, fraction / source.FadeIn) :
            fraction > source.FadeOut ? Mathf.Lerp(second.A, third.A, (fraction - source.FadeOut) / (1 - source.FadeOut)) : second.A;
        return color;
    }

    internal static float GrowFadeScale(FalloutNifParticleGrowFade source, float age, float life, ushort generation = 0)
    {
        // Base Scale is the endpoint of the growth/shrink envelope, not a
        // multiplier on the entire particle. Overlapping envelopes use their
        // minimum. A zero endpoint therefore still permits full-sized blood,
        // smoke and other particles during their authored lifetime.
        var grow = source.GrowGeneration == generation && source.Grow > 0 ? Math.Min(1, age / source.Grow) : 1;
        var fade = source.FadeGeneration == generation && source.Fade > 0 ? Math.Min(1, (life - age) / source.Fade) : 1;
        var range = 1 - source.Scale;
        return Math.Max(.0001f, (float)((double)Math.Min(grow, fade) * range + source.Scale));
    }

    private static Vector3 Convert(FalloutNifVector3 value) => GamebryoCoordinate.ConvertVector(new Vector3(value.X, value.Y, value.Z));
    private static Vector3 Convert(System.Numerics.Vector3 value) => GamebryoCoordinate.ConvertVector(new Vector3(value.X, value.Y, value.Z));
    private static Color ToColor(FalloutNifVector4 value) => new(value.X, value.Y, value.Z, value.W);
}
