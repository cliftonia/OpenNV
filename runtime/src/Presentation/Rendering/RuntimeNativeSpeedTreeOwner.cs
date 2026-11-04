using Godot;
using OpenNV.Runtime.Content;

namespace OpenNV.Runtime.Presentation.Rendering;

// A placed TREE reference. It keeps the ordinary reference identity, enable
// state and transform so scripts, residency and save state see a normal node;
// drawing is batched by the exterior SpeedTree owner.
internal partial class RuntimeNativeSpeedTreeReference : Node3D
{
    internal FalloutSpeedTree Definition { get; init; } = null!;
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

// One MultiMesh per TREE base across the resident exterior: a handful of
// draw calls for every shrub and tree. Batches rebuild only on residency,
// enable or transform changes; per frame only the shared source frame moves.
internal partial class RuntimeNativeSpeedTreeOwner : Node3D
{
    internal const string NodeName = "OwnedSpeedTrees";
    private sealed class Batch(FalloutSpeedTree tree, ShaderMaterial material, MultiMesh instances, MultiMeshInstance3D visual)
    {
        internal FalloutSpeedTree Tree { get; } = tree;
        internal ShaderMaterial Material { get; } = material;
        internal MultiMesh Instances { get; } = instances;
        internal MultiMeshInstance3D Visual { get; } = visual;
        internal HashSet<RuntimeNativeSpeedTreeReference> Members { get; } = [];
        internal bool Dirty { get; set; } = true;
    }

    private readonly Dictionary<FalloutFormKey, Batch> _batches = [];
    private RuntimeLiveContentSource _content = null!;
    private RuntimeNativeExteriorEnvironment _environment = null!;
    private FalloutInstallationSettings _ini = null!;
    private Shader _program = null!;
    private ArrayMesh _quad = null!;
    private float _units, _alphaCutoff;
    private double _seconds;
    private bool _disposed;
    internal string? Error { get; private set; }
    internal object State => new
    {
        error = Error,
        batches = _batches.Values.Select(batch => new
        {
            form = batch.Tree.Form.ToString(),
            model = batch.Tree.Model,
            billboard = batch.Tree.Billboard,
            size = new[] { batch.Tree.BillboardWidth, batch.Tree.BillboardHeight },
            members = batch.Members.Count,
            drawn = batch.Instances.InstanceCount,
            sample = batch.Members.Take(8).Select(member => member.GetMeta("opennv_reference_form_key", "").AsString()).ToArray()
        }).ToArray(),
        boundary = "authored-billboard;procedural-spt-geometry-unbound"
    };

    internal void Configure(RuntimeLiveContentSource content, RuntimeNativeExteriorEnvironment environment, float units, float alphaCutoff)
    {
        if (!float.IsFinite(units) || units <= 0) throw new ArgumentOutOfRangeException(nameof(units));
        Name = NodeName;
        _content = content; _environment = environment; _units = units; _alphaCutoff = alphaCutoff;
        _ini = FalloutInstallationSettings.Read(content);
        _program = new() { Code = RuntimeNativeSpeedTreeShader.Code };
        _quad = new ArrayMesh();
        using var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = new Vector3[] { new(-.5f, 0, 0), new(.5f, 0, 0), new(.5f, 1, 0), new(-.5f, 1, 0) };
        arrays[(int)Mesh.ArrayType.Normal] = new Vector3[] { Vector3.Back, Vector3.Back, Vector3.Back, Vector3.Back };
        arrays[(int)Mesh.ArrayType.TexUV] = new Vector2[] { new(0, 1), new(1, 1), new(1, 0), new(0, 0) };
        arrays[(int)Mesh.ArrayType.Index] = new[] { 0, 2, 1, 0, 3, 2 };
        _quad.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
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
        if (!_content.TryRead(tree.Model, null, out var spt, out _))
            throw new FileNotFoundException("SpeedTree source is missing.", tree.Model);
        var bark = FalloutSpeedTree.ReadBarkTexture(spt);
        if (!_content.TryRead(tree.Billboard, null, out var bytes, out var source))
            throw new FileNotFoundException("SpeedTree billboard texture is missing.", tree.Billboard);
        var texture = NativeDdsTexture.LoadCached(_content, bytes, source, logicalPath: tree.Billboard);
        var material = new ShaderMaterial { Shader = _program };
        material.SetShaderParameter("source_billboard", texture);
        material.SetShaderParameter("source_alpha_cutoff", _alphaCutoff);
        material.SetShaderParameter("source_units", _units);
        material.SetShaderParameter("source_size", new Vector2(tree.BillboardWidth, tree.BillboardHeight));
        material.SetShaderParameter("source_motion", new Vector3(tree.RockSpeed, tree.RustleSpeed, tree.LeafDimming));
        var instances = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, Mesh = _quad };
        var visual = new MultiMeshInstance3D
        {
            Name = $"SpeedTree_{tree.Form}",
            Multimesh = instances,
            MaterialOverride = material,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            Visible = false // The first authoritative frame binds lighting before drawing.
        };
        AddChild(visual);
        GD.Print($"OPENNV_SPEEDTREE_READY tree={tree.Form} model={tree.Model} bark={bark} billboard={tree.Billboard} " +
            $"size={tree.BillboardWidth}x{tree.BillboardHeight} rock={tree.RockSpeed} rustle={tree.RustleSpeed}");
        return new(tree, material, instances, visual);
    }

    private void Rebuild(Batch batch)
    {
        batch.Dirty = false;
        var drawn = batch.Members.Where(member => member.IsInsideTree() && member.IsVisibleInTree()).ToArray();
        batch.Instances.InstanceCount = drawn.Length;
        if (drawn.Length == 0) return;
        // Wind and the camera-facing turn stay inside the authored
        // billboard square scaled by the reference; the margin covers sway.
        var half = Math.Max(batch.Tree.BillboardWidth, batch.Tree.BillboardHeight) * _units;
        Aabb? bounds = null;
        for (var index = 0; index < drawn.Length; index++)
        {
            var transform = drawn[index].Transform;
            batch.Instances.SetInstanceTransform(index, transform);
            var extent = Vector3.One * (half * transform.Basis.Scale.X);
            var next = new Aabb(transform.Origin - extent, extent * 2);
            bounds = bounds is { } prior ? prior.Merge(next) : next;
        }
        batch.Instances.CustomAabb = bounds!.Value;
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
            var wind = new Vector4(frame.WindDirection.X, frame.WindDirection.Y, frame.WindMagnitude, (float)(_seconds % 3600));
            foreach (var batch in _batches.Values)
            {
                if (batch.Dirty) Rebuild(batch);
                var material = batch.Material;
                material.SetShaderParameter("source_wind", wind);
                material.SetShaderParameter("source_ambient", frame.Ambient);
                material.SetShaderParameter("source_diffuse_direction", frame.DiffuseDirection);
                material.SetShaderParameter("source_diffuse_color", frame.DiffuseColor);
                material.SetShaderParameter("source_directional_scale", frame.DirectionalScale);
                material.SetShaderParameter("source_fog_color", frame.FogColor);
                material.SetShaderParameter("source_fog", frame.Fog);
                batch.Visual.Visible = batch.Instances.InstanceCount > 0;
            }
        }
        catch (Exception error)
        {
            Error = error.Message; SetMeta("opennv_speedtree_error", Error);
            GD.PushError($"OPENNV_SPEEDTREE_DIVERGENCE {error}");
        }
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
        }
        _batches.Clear();
        _quad?.Dispose(); _program?.Dispose();
    }
}
