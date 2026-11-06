using Godot;
using OpenNV.Runtime.Content;

namespace OpenNV.Runtime.Presentation.Rendering;

// Where an EFSH instance births its particles: along a weapon's +Y line, on
// the bones and helper nodes of an actor or activator, or on mesh surfaces.
internal enum NativeEffectShaderEmitMode { Line, Skeleton, Surface }

// One EFSH instance on one resident reference. Membrane, particles and add-on
// models share the instance clock and releasing flag; the node frees itself
// once releasing and every part has faded out.
internal sealed partial class NativeEffectShader : Node
{
    private const string MembraneCode = """
        shader_type spatial;
        render_mode unshaded, depth_draw_never, cull_back, fog_disabled, {0};
        uniform sampler2D fill_map : source_color, filter_linear_mipmap, repeat_enable;
        uniform sampler2D holes_map : filter_linear_mipmap, repeat_enable;
        uniform bool use_fill_map;
        uniform bool use_holes;
        uniform float holes_threshold; // alpha-test reference, 0-1
        uniform vec4 fill_color; // rgb, displayed alpha
        uniform vec4 edge_color; // rgb (colour - 1 when inverse), displayed alpha
        uniform float edge_falloff;
        uniform vec2 fill_offset;
        void fragment() {
            if (use_holes && texture(holes_map, UV).a <= holes_threshold) discard;
            float facing = abs(dot(normalize(NORMAL), normalize(VIEW)));
            float edge = pow(clamp(1.0 - facing, 0.0, 1.0), max(edge_falloff, 0.01));
            vec4 fill = use_fill_map ? texture(fill_map, UV + fill_offset) : vec4(1.0);
            ALBEDO = fill.rgb * fill_color.rgb * fill_color.a * fill.a + edge_color.rgb * edge_color.a * edge;
            ALPHA = clamp(fill_color.a * fill.a + edge_color.a * edge, 0.0, 1.0);
        }
        """;
    private static readonly Dictionary<(string Code, string Modes), Shader> Shaders = [];
    private readonly List<(GeometryInstance3D Geometry, Material? Previous)> _targets = [];
    private readonly List<string> _deferrals = [];
    private FalloutEffectShader _effect = null!;
    private ShaderMaterial? _membrane;
    private double _age, _duration = double.PositiveInfinity;
    private float _fill, _edge;
    private Vector2 _scroll;
    internal FalloutFormKey Shader => _effect.Form;
    internal bool Releasing { get; private set; }
    // Visible deferrals for the caller's diagnostics channel.
    internal IReadOnlyList<string> Deferrals => _deferrals;

    internal static NativeEffectShader? Attach(Node3D target, FalloutEffectShader effect, RuntimeLiveContentSource content,
        FalloutPluginStack records, float? duration, NativeEffectShaderEmitMode mode, float units)
    {
        var node = new NativeEffectShader { Name = $"EffectShader_{effect.Form.ObjectId:X6}", _effect = effect };
        // A non-positive duration is infinite.
        if (duration is > 0) node._duration = duration.Value;
        try
        {
            if (effect.HasMembrane) node.AttachMembrane(target, content);
            if (effect.HasParticles && effect.ParticleTexture is not null) node.AttachParticles(target, content, records, mode, units);
        }
        catch { node.Restore(); node.ReleaseAddons(); node.Free(); throw; }
        if (node._membrane is null && node._cloud is null) { node.Free(); return null; }
        target.AddChild(node);
        GD.Print($"OPENNV_EFFECT_SHADER shader={effect.Form} target={target.Name} membraneGeometries={node._targets.Count} " +
            $"particleBatches={node._cloud?.BatchCount ?? 0} emit={mode} addons={node._addons.Count} " +
            $"duration={(duration is > 0 ? duration.Value.ToString("R", System.Globalization.CultureInfo.InvariantCulture) : "persistent")}");
        return node;
    }

    internal void Stop() => Releasing = true;

    private void AttachMembrane(Node3D target, RuntimeLiveContentSource content)
    {
        var source = _effect.Membrane;
        var blend = BlendMode(source.Blend, "membrane");
        var holes = _effect.HolesTexture is not null && FalloutEffectShaderTiming.HolesThreshold(_effect.Holes, 0) is not null;
        var material = new ShaderMaterial { Shader = CachedShader(MembraneCode, blend + DepthMode(source.Blend, "membrane")), ResourceName = $"EFSH {_effect.Form}", RenderPriority = 1 };
        var fill = _effect.FillTexture is { } path ? LoadTexture(content, path) : null;
        if (fill is not null) material.SetShaderParameter("fill_map", fill);
        material.SetShaderParameter("use_fill_map", fill is not null);
        if (holes) material.SetShaderParameter("holes_map", LoadTexture(content, _effect.HolesTexture!));
        material.SetShaderParameter("use_holes", holes);
        material.SetShaderParameter("edge_falloff", source.EdgeFalloff);
        _membrane = material;
        foreach (var geometry in SceneGraph.NodeTraversal.SelfAndDescendants<MeshInstance3D>(target))
        {
            // Skin-only shaders reach only geometry whose source material is named skin.
            if (_effect.SkinOnly && !string.Equals(geometry.GetMeta(SourceMaterialNameMeta, "").AsString(), "skin", StringComparison.OrdinalIgnoreCase))
                continue;
            _targets.Add((geometry, geometry.MaterialOverlay));
            geometry.MaterialOverlay = material;
        }
    }

    internal const string SourceMaterialNameMeta = "opennv_nif_material_name";

    public override void _Process(double delta)
    {
        _age += delta;
        Releasing |= _age > _duration;
        var step = (float)delta;
        var membraneVisible = false;
        if (_membrane is { } material)
        {
            var source = _effect.Membrane;
            // Release timing: the shared step multiplies its release rate by
            // the particle batch capacity. Taken literally on membrane alpha it
            // would vanish in a few frames; that factor is unverified for the
            // membrane, so it releases at the unscaled rate here.
            _fill = FalloutEffectShaderTiming.Step(source.FillAlpha, _fill, step, _age, Releasing, 1);
            _edge = FalloutEffectShaderTiming.Step(source.EdgeAlpha, _edge, step, _age, Releasing, 1);
            _scroll += new Vector2(source.TextureScroll.X, source.TextureScroll.Y) * step;
            static Vector4 Color(FalloutEffectShaderColor color, float alpha, bool inverse) =>
                new(color.Red / 255f - (inverse ? 1 : 0), color.Green / 255f - (inverse ? 1 : 0), color.Blue / 255f - (inverse ? 1 : 0), alpha);
            material.SetShaderParameter("fill_color", Color(source.FillColor, FalloutEffectShaderTiming.Displayed(_fill, source.FillPulse, _age), false));
            material.SetShaderParameter("edge_color", Color(source.EdgeColor, FalloutEffectShaderTiming.Displayed(_edge, source.EdgePulse, _age), _effect.InverseEdge));
            material.SetShaderParameter("fill_offset", _scroll);
            if (FalloutEffectShaderTiming.HolesThreshold(_effect.Holes, _age) is { } threshold)
                material.SetShaderParameter("holes_threshold", threshold / 255f);
            membraneVisible = _fill > 1e-4f || _edge > 1e-4f;
        }
        var particlesVisible = UpdateParticles(step);
        var addonsVisible = UpdateAddons(step);
        if (Releasing && !membraneVisible && !particlesVisible && !addonsVisible) QueueFree();
    }

    // D3D numbering: SrcAlpha (5) with One (2) adds; with InvSrcAlpha (6) mixes.
    // The owned corpus uses only these, with the Add (1) operation.
    private string BlendMode(FalloutEffectShaderBlend blend, string part) => (blend.Source, blend.Destination, blend.Operation) switch
    {
        (5, 2, 1) => "blend_add",
        (5, 6, 1) => "blend_mix",
        _ => throw new NotSupportedException($"Effect shader {_effect.Form} {part} blend {blend.Source}/{blend.Destination}/{blend.Operation} has no renderer owner."),
    };

    // Less, Equal and LessEqual all keep the ordinary depth test: the membrane
    // redraws already-laid geometry, so Equal and LessEqual coincide there.
    private string DepthMode(FalloutEffectShaderBlend blend, string part) => blend.DepthTest switch
    {
        2 or 3 or 4 => "",
        8 => ", depth_test_disabled",
        _ => throw new NotSupportedException($"Effect shader {_effect.Form} {part} depth test {blend.DepthTest} has no renderer owner."),
    };

    private static Shader CachedShader(string code, string modes)
    {
        if (!Shaders.TryGetValue((code, modes), out var shader))
            Shaders.Add((code, modes), shader = new() { Code = string.Format(System.Globalization.CultureInfo.InvariantCulture, code, modes) });
        return shader;
    }

    private static Texture2D LoadTexture(RuntimeLiveContentSource content, string path)
    {
        if (!content.TryRead(path, null, out var bytes, out var source))
            throw new FileNotFoundException("Effect shader texture is missing.", path);
        return NativeDdsTexture.LoadCached(content, bytes, source, logicalPath: path);
    }

    private void Restore()
    {
        foreach (var (geometry, previous) in _targets)
            if (IsInstanceValid(geometry) && geometry.MaterialOverlay == _membrane) geometry.MaterialOverlay = previous;
    }

    // Leaving the tree (a reparent or residency change) restores the target's
    // own overlays; re-entering reapplies the membrane to live targets.
    public override void _EnterTree()
    {
        if (_membrane is null) return;
        foreach (var (geometry, _) in _targets)
            if (IsInstanceValid(geometry)) geometry.MaterialOverlay = _membrane;
    }

    public override void _ExitTree() => Restore();

    public override void _Notification(int what)
    {
        if (what != NotificationPredelete) return;
        Restore(); _targets.Clear();
        ReleaseAddons();
        _membrane?.Dispose();
        ReleaseParticleDraw();
    }
}
