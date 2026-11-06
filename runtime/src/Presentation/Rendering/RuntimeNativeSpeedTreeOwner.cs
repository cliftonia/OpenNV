using Godot;
using OpenNV.Runtime.Content;

namespace OpenNV.Runtime.Presentation.Rendering;

// A placed TREE reference. It keeps the ordinary reference identity, enable
// state and transform so scripts, residency and save state see a normal node;
// drawing is batched by the exterior SpeedTree owner. Seed is the reference's
// generation seed selected from the TREE seed list.
internal partial class RuntimeNativeSpeedTreeReference : Node3D
{
    internal FalloutSpeedTree Definition { get; init; } = null!;
    internal byte? SeedIndex { get; init; }
    private RuntimeNativeSpeedTreeOwner? _owner;

    public override void _EnterTree()
    {
        SetNotifyTransform(true);
        _owner = GetParent()?.GetNodeOrNull<RuntimeNativeSpeedTreeOwner>(RuntimeNativeSpeedTreeOwner.NodeName);
        _owner?.Add(this);
    }

    public override void _ExitTree() { _owner?.Remove(this); _owner = null; }

    public override void _Notification(int what)
    {
        if (what == NotificationVisibilityChanged || what == NotificationTransformChanged) _owner?.Invalidate(Definition.Form);
    }

    internal void Attach(RuntimeNativeSpeedTreeOwner owner) { _owner = owner; owner.Add(this); }
}

// One billboard MultiMesh per TREE base and one generated-geometry MultiMesh
// per (TREE, seed) across the resident exterior. References inside the LOD
// far distance draw generated branches and leaf cards; beyond it, the
// authored billboard. Between near and far both draw with complementary
// dither. Membership repartitions on residency, enable or transform changes
// and when the camera has moved; per frame only shared uniforms change.
internal partial class RuntimeNativeSpeedTreeOwner : Node3D
{
    internal const string NodeName = "OwnedSpeedTrees";
    private const int WindMatrixCount = 4;

    private sealed class Model(FalloutSpeedTreeGeometry geometry, ArrayMesh mesh, MultiMesh instances, MultiMeshInstance3D visual)
    {
        internal FalloutSpeedTreeGeometry Geometry { get; } = geometry;
        internal ArrayMesh Mesh { get; } = mesh;
        internal MultiMesh Instances { get; } = instances;
        internal MultiMeshInstance3D Visual { get; } = visual;
    }

    private sealed class Batch(FalloutSpeedTree tree, FalloutSpeedTreeSource source, ShaderMaterial material, MultiMesh instances, MultiMeshInstance3D visual)
    {
        internal FalloutSpeedTree Tree { get; } = tree;
        internal FalloutSpeedTreeSource Source { get; } = source;
        internal ShaderMaterial Material { get; } = material;
        internal MultiMesh Instances { get; } = instances;
        internal MultiMeshInstance3D Visual { get; } = visual;
        internal HashSet<RuntimeNativeSpeedTreeReference> Members { get; } = [];
        internal Dictionary<int, Model> Models { get; } = [];
        internal ShaderMaterial? BranchMaterial { get; set; }
        internal ShaderMaterial? LeafMaterial { get; set; }
        internal string? GeometryError { get; set; }
        internal bool Dirty { get; set; } = true;
    }

    private readonly Dictionary<FalloutFormKey, Batch> _batches = [];
    private RuntimeLiveContentSource _content = null!;
    private RuntimeNativeExteriorEnvironment _environment = null!;
    private FalloutInstallationSettings _ini = null!;
    private Shader _billboardProgram = null!, _branchProgram = null!, _leafProgram = null!;
    private ArrayMesh _quad = null!;
    private float _units, _alphaCutoff, _sizeConversion, _near, _far, _maximumWind;
    private float? _forcedLeafDimming, _forcedBranchDimming, _forcedCurvature;
    private bool _forceFullLod;
    private string _branchDirectory = null!;
    private double _seconds, _rockTime, _rustleTime;
    private Vector3? _partitionCamera;
    private bool _disposed;
    internal string? Error { get; private set; }
    private bool GeometryEnabled => _far > 0;

    internal object State => new
    {
        error = Error,
        lod = new { near = _near, far = _far, forceFull = _forceFullLod, sizeConversion = _sizeConversion },
        batches = _batches.Values.Select(batch => new
        {
            form = batch.Tree.Form.ToString(),
            model = batch.Tree.Model,
            billboard = batch.Tree.Billboard,
            leaves = batch.Tree.LeafTexture,
            bark = batch.Source.BarkTexture,
            seeds = batch.Tree.Seeds,
            shadowRadius = batch.Tree.ShadowRadius,
            size = new[] { batch.Tree.BillboardWidth, batch.Tree.BillboardHeight },
            members = batch.Members.Count,
            billboards = batch.Instances.InstanceCount,
            generated = batch.Models.Values.Select(model => new
            {
                seed = model.Geometry.Seed,
                drawn = model.Instances.InstanceCount,
                branchVertices = model.Geometry.BranchVertices.Count,
                leaves = model.Geometry.Leaves.Count,
            }).ToArray(),
            geometryError = batch.GeometryError,
            sample = batch.Members.Take(8).Select(member => member.GetMeta("opennv_reference_form_key", "").AsString()).ToArray(),
            nearest = _partitionCamera is not { } camera ? null : batch.Members.Where(member => member.IsInsideTree())
                .OrderBy(member => member.GlobalPosition.DistanceTo(camera)).Take(3).Select(member => new
                {
                    reference = member.GetMeta("opennv_reference_form_key", "").AsString(),
                    position = new[] { member.GlobalPosition.X, member.GlobalPosition.Y, member.GlobalPosition.Z },
                }).ToArray(),
        }).ToArray(),
        boundary = "generated-branches-and-leaf-cards;billboard-beyond-lod;fronds-flares-collision-canopy-shadow-unbound"
    };

    internal void Configure(RuntimeLiveContentSource content, FalloutPluginStack records, RuntimeNativeExteriorEnvironment environment, float units,
        SpeedTreeCompilerConfiguration configuration, float geometryDistanceScale)
    {
        if (!float.IsFinite(units) || units <= 0) throw new ArgumentOutOfRangeException(nameof(units));
        if (geometryDistanceScale is not (>= 0 and <= 1)) throw new ArgumentOutOfRangeException(nameof(geometryDistanceScale));
        Name = NodeName;
        _content = content; _environment = environment; _units = units; _alphaCutoff = configuration.BillboardAlphaCutoff;
        _branchDirectory = configuration.BranchDirectory;
        _ini = FalloutInstallationSettings.Read(content);
        // Retail LOD limits come from the game settings, not SPT 2001/2003.
        var multiplier = _ini.Number("LOD", "fLODMultTrees");
        _near = FalloutGameSettingFloats.Read(records, "fTreeNearDistanceBase") * multiplier * geometryDistanceScale;
        _far = FalloutGameSettingFloats.Read(records, "fTreeFarDistanceBase") * multiplier * geometryDistanceScale;
        if (!float.IsFinite(_near) || !float.IsFinite(_far) || _near < 0 || _far < _near)
            throw new InvalidDataException("SpeedTree LOD distances are invalid.");
        _sizeConversion = FalloutGameSettingFloats.Read(records, "fTreeSizeConversion");
        _forceFullLod = _ini.Boolean("SpeedTree", "bForceFullLOD") && GeometryEnabled;
        float? Forced(string key, Func<float, bool> valid) => _ini.Number("SpeedTree", key) is var value && valid(value) ? value : null;
        _forcedLeafDimming = Forced("fTreeForceLeafDimming", value => value is >= 0 and <= 1);
        _forcedBranchDimming = Forced("fTreeForceBranchDimming", value => value is >= 0 and <= 1);
        _forcedCurvature = Forced("fTreeForceCS", value => value >= 0);
        _maximumWind = Math.Max(MathF.Abs(_ini.Number("Grass", "fGrassWindMagnitudeMin")), MathF.Abs(_ini.Number("Grass", "fGrassWindMagnitudeMax")));
        _billboardProgram = new() { Code = RuntimeNativeSpeedTreeShader.Billboard };
        _branchProgram = new() { Code = RuntimeNativeSpeedTreeShader.Branch };
        _leafProgram = new() { Code = RuntimeNativeSpeedTreeShader.Leaf };
        _quad = new ArrayMesh();
        using var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = new Vector3[] { new(-.5f, 0, 0), new(.5f, 0, 0), new(.5f, 1, 0), new(-.5f, 1, 0) };
        arrays[(int)Mesh.ArrayType.Normal] = new Vector3[] { Vector3.Back, Vector3.Back, Vector3.Back, Vector3.Back };
        arrays[(int)Mesh.ArrayType.TexUV] = new Vector2[] { new(0, 1), new(1, 1), new(1, 0), new(0, 0) };
        arrays[(int)Mesh.ArrayType.Index] = new[] { 0, 2, 1, 0, 3, 2 };
        _quad.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        GD.Print($"OPENNV_SPEEDTREE_LOD near={_near} far={_far} forceFull={_forceFullLod} sizeConversion={_sizeConversion} geometryScale={geometryDistanceScale}");
    }

    public override void _EnterTree()
    {
        // Markers staged before this owner joined the root register now.
        foreach (var reference in GetParent().GetChildren().OfType<RuntimeNativeSpeedTreeReference>())
            reference.Attach(this);
    }

    internal void Add(RuntimeNativeSpeedTreeReference reference)
    {
        if (_disposed || Error is not null) return;
        try
        {
            if (!_batches.TryGetValue(reference.Definition.Form, out var batch))
                _batches.Add(reference.Definition.Form, batch = CreateBatch(reference.Definition));
            batch.Members.Add(reference); batch.Dirty = true;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or NotSupportedException)
        {
            Error = error.Message; SetMeta("opennv_speedtree_error", Error);
            GD.PushError($"OPENNV_SPEEDTREE_DIVERGENCE tree={reference.Definition.Form}: {error.Message}");
        }
    }

    internal void Remove(RuntimeNativeSpeedTreeReference reference)
    {
        if (_batches.TryGetValue(reference.Definition.Form, out var batch) && batch.Members.Remove(reference)) batch.Dirty = true;
    }

    internal void Invalidate(FalloutFormKey tree) { if (_batches.TryGetValue(tree, out var batch)) batch.Dirty = true; }

    private Batch CreateBatch(FalloutSpeedTree tree)
    {
        var source = FalloutSpeedTreeSource.Read(ReadRequired(tree.Model, "SpeedTree source"));
        var material = new ShaderMaterial { Shader = _billboardProgram };
        material.SetShaderParameter("source_billboard", Texture(tree.Billboard, "SpeedTree billboard texture"));
        material.SetShaderParameter("source_alpha_cutoff", _alphaCutoff);
        material.SetShaderParameter("source_units", _units);
        material.SetShaderParameter("source_size", new Vector2(tree.BillboardWidth, tree.BillboardHeight));
        material.SetShaderParameter("source_motion", new Vector3(tree.RockSpeed, tree.RustleSpeed, _forcedLeafDimming ?? tree.LeafDimming));
        var instances = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, UseCustomData = true, Mesh = _quad };
        var visual = new MultiMeshInstance3D
        {
            Name = $"SpeedTree_{tree.Form}",
            Multimesh = instances,
            MaterialOverride = material,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            Visible = false // The first authoritative frame binds lighting before drawing.
        };
        AddChild(visual);
        GD.Print($"OPENNV_SPEEDTREE_READY tree={tree.Form} model={tree.Model} bark={source.BarkTexture} billboard={tree.Billboard} " +
            $"leaves={tree.LeafTexture} seeds=[{string.Join(',', tree.Seeds)}] levels={source.Levels.Count} leafMaps={source.LeafMaps.Count} " +
            $"size={tree.BillboardWidth}x{tree.BillboardHeight} rock={tree.RockSpeed} rustle={tree.RustleSpeed}");
        return new(tree, source, material, instances, visual);
    }

    // Generated shapes are cached per (TREE, seed): placed copies share one mesh.
    private Model? GeneratedModel(Batch batch, int seed)
    {
        if (batch.Models.TryGetValue(seed, out var cached)) return cached;
        if (batch.GeometryError is not null) return null;
        try
        {
            var started = Time.GetTicksUsec();
            var geometry = FalloutSpeedTreeGenerator.Generate(batch.Source, seed, _sizeConversion, _forcedBranchDimming ?? batch.Tree.BranchDimming);
            batch.BranchMaterial ??= GeneratedMaterial(_branchProgram,
                Texture(_branchDirectory.TrimEnd('\\', '/') + "\\" + Path.ChangeExtension(batch.Source.BarkTexture, ".dds").ToLowerInvariant(), "SpeedTree bark texture"));
            batch.LeafMaterial ??= GeneratedMaterial(_leafProgram, Texture(batch.Tree.LeafTexture, "SpeedTree leaf texture"));
            var mesh = RuntimeNativeSpeedTreeMesh.Build(geometry, _units, batch.BranchMaterial, batch.LeafMaterial);
            var instances = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, UseCustomData = true, Mesh = mesh };
            var visual = new MultiMeshInstance3D
            {
                Name = $"SpeedTree_{batch.Tree.Form}_{seed}",
                Multimesh = instances,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                Visible = false,
            };
            AddChild(visual);
            GD.Print($"OPENNV_SPEEDTREE_GENERATED tree={batch.Tree.Form} seed={seed} size={geometry.Size:0.##} " +
                $"branchVertices={geometry.BranchVertices.Count} leaves={geometry.Leaves.Count} microseconds={Time.GetTicksUsec() - started}");
            return batch.Models[seed] = new(geometry, mesh, instances, visual);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or NotSupportedException)
        {
            // The billboard keeps drawing; the generated-geometry gap stays visible.
            batch.GeometryError = error.Message;
            GD.PushError($"OPENNV_SPEEDTREE_GEOMETRY_DIVERGENCE tree={batch.Tree.Form} seed={seed}: {error.Message}");
            return null;
        }
    }

    private ShaderMaterial GeneratedMaterial(Shader program, Texture2D texture)
    {
        var material = new ShaderMaterial { Shader = program };
        material.SetShaderParameter("source_texture", texture);
        material.SetShaderParameter("source_alpha_cutoff", _alphaCutoff);
        material.SetShaderParameter("source_units", _units);
        return material;
    }

    private byte[] ReadRequired(string path, string label) =>
        _content.TryRead(path, null, out var bytes, out _) ? bytes : throw new FileNotFoundException($"{label} is missing.", path);

    private Texture2D Texture(string path, string label)
    {
        if (!_content.TryRead(path, null, out var bytes, out var source)) throw new FileNotFoundException($"{label} is missing.", path);
        return NativeDdsTexture.LoadCached(_content, bytes, source, logicalPath: path);
    }

    private void Rebuild(Batch batch, Vector3 camera)
    {
        batch.Dirty = false;
        var drawn = batch.Members.Where(member => member.IsInsideTree() && member.IsVisibleInTree()).ToArray();
        var billboards = new List<(Transform3D Transform, float Lod)>();
        var generated = new Dictionary<Model, List<(Transform3D Transform, float Lod)>>();
        foreach (var member in drawn)
        {
            var lod = !GeometryEnabled ? 0 : _forceFullLod ? 1 :
                Math.Clamp(1 - (member.GlobalPosition.DistanceTo(camera) / _units - _near) / Math.Max(_far - _near, 1e-3f), 0, 1);
            var seed = member.Definition.ReferenceSeed(member.SeedIndex, batch.Source.Seed);
            var model = lod > 0 ? GeneratedModel(batch, seed) : null;
            if (model is null) lod = 0;
            else
            {
                if (!generated.TryGetValue(model, out var list)) generated.Add(model, list = []);
                list.Add((member.Transform, lod));
            }
            if (lod < 1) billboards.Add((member.Transform, lod));
        }
        // Wind and the camera-facing turn stay inside the authored
        // billboard square scaled by the reference; the margin covers sway.
        var half = Math.Max(batch.Tree.BillboardWidth, batch.Tree.BillboardHeight) * _units;
        Publish(batch.Instances, billboards, half);
        foreach (var model in batch.Models.Values)
            Publish(model.Instances, generated.GetValueOrDefault(model) ?? [], half);
    }

    private static void Publish(MultiMesh multimesh, List<(Transform3D Transform, float Lod)> members, float half)
    {
        multimesh.InstanceCount = members.Count;
        if (members.Count == 0) return;
        Aabb? bounds = null;
        for (var index = 0; index < members.Count; index++)
        {
            var (transform, lod) = members[index];
            multimesh.SetInstanceTransform(index, transform);
            multimesh.SetInstanceCustomData(index, new Color(lod, 0, 0, 0));
            var extent = Vector3.One * (half * transform.Basis.Scale.X);
            var next = new Aabb(transform.Origin - extent, extent * 2);
            bounds = bounds is { } prior ? prior.Merge(next) : next;
        }
        multimesh.CustomAabb = bounds!.Value;
    }

    public override void _Process(double delta)
    {
        if (Error is not null || _disposed || _batches.Count == 0) return;
        try
        {
            _seconds += delta;
            var camera = GetViewport().GetCamera3D();
            if (camera is null) return;
            var frame = _environment.GrassFrame(camera, _ini, 0, 1);
            var strength = _maximumWind > 0 ? Math.Clamp(MathF.Abs(frame.WindMagnitude) / _maximumWind, 0, 1) : 0;
            var wind = new Vector4(frame.WindDirection.X, frame.WindDirection.Y, frame.WindMagnitude, (float)(_seconds % 3600));
            // Wind frequency follows 8 x strength; the accumulated rock and
            // rustle times scale each tree's CNAM speeds into phases.
            var rate = delta * 8 * Math.Max(strength, .1f);
            _rockTime = (_rockTime + rate) % 3600; _rustleTime = (_rustleTime + rate) % 3600;
            var matrices = WindMatrices(frame.WindDirection, strength);
            var position = camera.GlobalPosition;
            var moved = _partitionCamera is not { } last || last.DistanceTo(position) > Math.Max(1f, (_far - _near) * _units / 64);
            if (moved) _partitionCamera = position;
            foreach (var batch in _batches.Values)
            {
                if (batch.Dirty || (moved && GeometryEnabled && !_forceFullLod)) Rebuild(batch, position);
                Bind(batch.Material, frame);
                batch.Material.SetShaderParameter("source_wind", wind);
                batch.Visual.Visible = batch.Instances.InstanceCount > 0;
                if (batch.Models.Count == 0) continue;
                foreach (var material in new[] { batch.BranchMaterial!, batch.LeafMaterial! })
                {
                    Bind(material, frame);
                    material.SetShaderParameter("tree_wind", matrices);
                }
                var tree = batch.Tree;
                batch.LeafMaterial!.SetShaderParameter("tree_rock", new Vector3(.25f * strength, (float)(tree.RockSpeed * _rockTime % 2), 1));
                batch.LeafMaterial.SetShaderParameter("tree_rustle", new Vector3(.15f * strength, (float)(tree.RustleSpeed * _rustleTime % 2), 1));
                batch.LeafMaterial.SetShaderParameter("tree_curvature", _forcedCurvature ?? tree.LeafCurvature);
                batch.LeafMaterial.SetShaderParameter("tree_leaf_dimming", _forcedLeafDimming ?? tree.LeafDimming);
                foreach (var model in batch.Models.Values) model.Visual.Visible = model.Instances.InstanceCount > 0;
            }
        }
        catch (Exception error)
        {
            Error = error.Message; SetMeta("opennv_speedtree_error", Error);
            GD.PushError($"OPENNV_SPEEDTREE_DIVERGENCE {error}");
        }
    }

    private static void Bind(ShaderMaterial material, RuntimeNativeGrassFrame frame)
    {
        material.SetShaderParameter("source_ambient", frame.Ambient);
        material.SetShaderParameter("source_diffuse_direction", frame.DiffuseDirection);
        material.SetShaderParameter("source_diffuse_color", frame.DiffuseColor);
        material.SetShaderParameter("source_directional_scale", frame.DirectionalScale);
        material.SetShaderParameter("source_fog_color", frame.FogColor);
        material.SetShaderParameter("source_fog", frame.Fog);
    }

    // The source wind blocks are empty, so the four matrices are small
    // rotations about horizontal axes near the wind's crosswind, scaled by
    // the weather wind strength and offset in phase per matrix.
    private Godot.Collections.Array<Basis> WindMatrices(Vector2 direction, float strength)
    {
        var crosswind = Vector3.Up.Cross(new Vector3(direction.X, 0, -direction.Y));
        crosswind = crosswind.LengthSquared() < 1e-6f ? Vector3.Right : crosswind.Normalized();
        var matrices = new Godot.Collections.Array<Basis>();
        for (var index = 0; index < WindMatrixCount; index++)
        {
            var axis = crosswind.Rotated(Vector3.Up, index * .35f);
            var angle = strength * Mathf.DegToRad(2.5f) * MathF.Sin((float)(_seconds * (.35 + .12 * index) * Math.Tau) + index * 1.7f);
            matrices.Add(new Basis(axis, angle));
        }
        return matrices;
    }

    public override void _ExitTree()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var batch in _batches.Values)
        {
            batch.Visual.Multimesh = null; batch.Visual.MaterialOverride = null;
            batch.Instances.Mesh = null; batch.Instances.Dispose();
            batch.Material.Shader = null; batch.Material.Dispose();
            foreach (var model in batch.Models.Values)
            {
                model.Visual.Multimesh = null;
                model.Instances.Mesh = null; model.Instances.Dispose();
                model.Mesh.Dispose();
            }
            batch.BranchMaterial?.Dispose(); batch.LeafMaterial?.Dispose();
        }
        _batches.Clear();
        _quad?.Dispose(); _billboardProgram?.Dispose(); _branchProgram?.Dispose(); _leafProgram?.Dispose();
    }
}
