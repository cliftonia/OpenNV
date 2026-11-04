using Godot;
using OpenNV.Runtime.Content;

namespace OpenNV.Runtime.Presentation.Rendering;

// EFSH membrane on a placed reference: a material overlay on every resident
// geometry of the target, driven by the source fill/edge alpha envelopes,
// pulses and fill-texture scroll. Particle shaders stay a separate gap.
internal sealed partial class NativeEffectShaderMembrane : Node
{
    private const string Code = """
        shader_type spatial;
        render_mode unshaded, depth_draw_never, cull_back, fog_disabled, {0};
        uniform sampler2D fill_map : source_color, filter_linear_mipmap, repeat_enable;
        uniform bool use_fill_map;
        uniform vec4 fill_color; // rgb, current envelope alpha
        uniform vec4 edge_color; // rgb, current envelope alpha
        uniform float edge_falloff;
        uniform bool inverse_edge;
        uniform vec2 fill_offset;
        void fragment() {
            float facing = abs(dot(normalize(NORMAL), normalize(VIEW)));
            float edge = pow(clamp(1.0 - facing, 0.0, 1.0), max(edge_falloff, 0.01));
            if (inverse_edge) edge = 1.0 - edge;
            vec4 fill = use_fill_map ? texture(fill_map, UV + fill_offset) : vec4(1.0);
            vec3 color = fill.rgb * fill_color.rgb * fill_color.a * fill.a + edge_color.rgb * edge_color.a * edge;
            float alpha = clamp(fill_color.a * fill.a + edge_color.a * edge, 0.0, 1.0);
            ALBEDO = color;
            ALPHA = alpha;
        }
        """;
    private static readonly Dictionary<bool, Shader> Shaders = [];
    private readonly List<(GeometryInstance3D Geometry, Material? Previous)> _targets = [];
    private ShaderMaterial _material = null!;
    private FalloutEffectShaderMembrane _source = null!;
    private double _elapsed, _stopAt = double.PositiveInfinity, _stoppedAt = double.NaN;
    private float _stopFill, _stopEdge;
    internal FalloutFormKey Shader { get; private set; }

    internal static NativeEffectShaderMembrane? Attach(Node3D target, FalloutEffectShader effect, RuntimeLiveContentSource content, float? duration)
    {
        if (!effect.HasMembrane) return null;
        var membrane = effect.Membrane;
        // Blend factor 2 is D3D ONE: an additive membrane; otherwise source alpha over.
        var additive = membrane.Blend.Destination == 2;
        if (!Shaders.TryGetValue(additive, out var shader))
            Shaders.Add(additive, shader = new() { Code = string.Format(System.Globalization.CultureInfo.InvariantCulture, Code, additive ? "blend_add" : "blend_mix") });
        var material = new ShaderMaterial { Shader = shader, ResourceName = $"EFSH {effect.Form}", RenderPriority = 1 };
        Texture2D? fill = null;
        if (effect.FillTexture is { } path)
        {
            if (!content.TryRead(path, null, out var bytes, out var source))
                throw new FileNotFoundException("Effect shader fill texture is missing.", path);
            fill = NativeDdsTexture.LoadCached(content, bytes, source, logicalPath: path);
            material.SetShaderParameter("fill_map", fill);
        }
        material.SetShaderParameter("use_fill_map", fill is not null);
        material.SetShaderParameter("edge_falloff", membrane.EdgeFalloff);
        material.SetShaderParameter("inverse_edge", effect.InverseEdge);
        var node = new NativeEffectShaderMembrane { Name = $"EffectShader_{effect.Form.ObjectId:X6}", Shader = effect.Form, _material = material, _source = membrane };
        if (duration is > 0) node._stopAt = duration.Value;
        foreach (var geometry in SceneGraph.NodeTraversal.SelfAndDescendants<GeometryInstance3D>(target))
        {
            if (geometry is not MeshInstance3D) continue;
            node._targets.Add((geometry, geometry.MaterialOverlay));
            geometry.MaterialOverlay = material;
        }
        target.AddChild(node);
        GD.Print($"OPENNV_EFFECT_SHADER_MEMBRANE shader={effect.Form} target={target.Name} geometries={node._targets.Count} " +
            $"fill={effect.FillTexture ?? "none"} additive={additive} duration={(duration is > 0 ? duration.Value.ToString("R", System.Globalization.CultureInfo.InvariantCulture) : "persistent")}");
        return node;
    }

    internal bool Stopping => _elapsed >= _stopAt;
    internal void Stop() { if (double.IsPositiveInfinity(_stopAt) || _stopAt > _elapsed) _stopAt = _elapsed; }

    public override void _Process(double delta)
    {
        _elapsed += delta;
        float fill, edge;
        if (_elapsed < _stopAt)
        {
            fill = Envelope(_source.FillAlpha, _source.FillPulse, _elapsed);
            edge = Envelope(_source.EdgeAlpha, _source.EdgePulse, _elapsed);
        }
        else
        {
            if (double.IsNaN(_stoppedAt))
            {
                _stoppedAt = _elapsed;
                _stopFill = Envelope(_source.FillAlpha, _source.FillPulse, _stopAt);
                _stopEdge = Envelope(_source.EdgeAlpha, _source.EdgePulse, _stopAt);
            }
            var since = (float)(_elapsed - _stoppedAt);
            fill = Fade(_stopFill, since, _source.FillAlpha.FadeOut);
            edge = Fade(_stopEdge, since, _source.EdgeAlpha.FadeOut);
            if (fill <= 0 && edge <= 0) { QueueFree(); return; }
        }
        Vector4 Color(FalloutEffectShaderColor color, float alpha) => new(color.Red / 255f, color.Green / 255f, color.Blue / 255f, alpha);
        _material.SetShaderParameter("fill_color", Color(_source.FillColor, fill));
        _material.SetShaderParameter("edge_color", Color(_source.EdgeColor, edge));
        _material.SetShaderParameter("fill_offset", new Vector2(_source.TextureScroll.X, _source.TextureScroll.Y) * (float)_elapsed);
    }

    // Fade in to the full ratio, hold, then settle on the persistent ratio.
    private static float Envelope(FalloutEffectShaderEnvelope envelope, FalloutEffectShaderPulse pulse, double time)
    {
        float value;
        if (time < envelope.FadeIn) value = envelope.Full * (float)(time / envelope.FadeIn);
        else if (time < envelope.FadeIn + envelope.FullTime) value = envelope.Full;
        else value = envelope.Persistent;
        if (pulse.Amplitude != 0 && pulse.Frequency > 0)
            value *= 1 + pulse.Amplitude * MathF.Sin((float)(time * pulse.Frequency * Math.Tau));
        return Math.Clamp(value, 0, 1);
    }

    private static float Fade(float from, float since, float duration) =>
        duration <= 0 ? 0 : Math.Max(0, from * (1 - since / duration));

    public override void _ExitTree()
    {
        foreach (var (geometry, previous) in _targets)
            if (IsInstanceValid(geometry) && geometry.MaterialOverlay == _material) geometry.MaterialOverlay = previous;
        _targets.Clear();
    }

    public override void _Notification(int what) { if (what == NotificationPredelete) _material?.Dispose(); }
}
