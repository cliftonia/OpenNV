using Godot;
using OpenNV.Runtime.Content;
using OpenNV.Runtime.Formats.Gamebryo;
using Numerics = System.Numerics;

namespace OpenNV.Runtime.Presentation.Rendering;

// EFSH particle drawing. The C# cloud owns slots, timing and the per-particle
// functions; this half resolves emission points on the live target and draws
// world-space camera-facing quads. The record's world-space flag is assumed
// on; the "actor particle effects" local-space variant is not bound.
internal sealed partial class NativeEffectShader
{
    private const string ParticleCode = """
        shader_type spatial;
        render_mode unshaded, depth_draw_never, cull_disabled, fog_disabled, {0};
        uniform sampler2D particle_map : filter_linear_mipmap, repeat_disable;
        void vertex() { UV = INSTANCE_CUSTOM.xz + UV * INSTANCE_CUSTOM.yw; }
        void fragment() {
            vec4 sampled = texture(particle_map, UV) * COLOR;
            ALBEDO = sampled.rgb;
            ALPHA = sampled.a;
        }
        """;
    // Gamebryo local axes in presentation coordinates.
    private static readonly Vector3 SourceX = GamebryoCoordinate.ConvertVector(Vector3.Right),
        SourceY = GamebryoCoordinate.ConvertVector(Vector3.Up), SourceZ = GamebryoCoordinate.ConvertVector(Vector3.Back);
    private readonly Random _random = new();
    private FalloutEffectShaderParticleCloud? _cloud;
    private FalloutEffectShaderEmitter _emitter = null!;
    private Node3D _target = null!;
    private float _boundsExtent;
    private bool _sorted;
    private MultiMesh? _draw;
    private MultiMeshInstance3D? _visual;
    private QuadMesh? _quad;
    private float[] _drawBuffer = [];
    private FalloutEffectShaderParticleSample[] _samples = [];
    private NativeParticleDrawOrder[] _drawOrder = [];
    private int _visibleCount;
    private bool _sourceLost;
    private Vector3 _boundsCentre = new(float.NaN, 0, 0);

    private void AttachParticles(Node3D target, RuntimeLiveContentSource content, FalloutPluginStack records,
        NativeEffectShaderEmitMode mode, float units)
    {
        var source = _effect.Particles;
        // Explosion wind is supplied by an explosion owner that never reaches
        // this path; a nonzero speed stays visible rather than guessing a point.
        if (_effect.ExplosionWindSpeed is not null and not 0) _deferrals.Add("effect-shader-explosion-wind-point-unbound");
        var cloud = new FalloutEffectShaderParticleCloud(_effect, ToNumerics(Convert(source.Velocity)),
            ToNumerics(Convert(source.Acceleration)), units, _random);
        if (cloud.Capacity == 0) return;
        var (centre, radius) = LocalBounds(target);
        _boundsExtent = Math.Max(512 * units, 2 * radius);
        var nodes = new List<(Node3D Parent, int Bone)>();
        FalloutEffectShaderEmitter? emitter = mode switch
        {
            NativeEffectShaderEmitMode.Line => LineEmitter(target, centre.Length() + radius),
            NativeEffectShaderEmitMode.Skeleton => SkeletonEmitter(target, nodes),
            _ => SurfaceEmitter(target),
        };
        if (emitter is null) { _deferrals.Add($"effect-shader-{mode.ToString().ToLowerInvariant()}-emission-source-empty"); return; }
        var particles = _effect.Particles.Blend;
        var shader = CachedShader(ParticleCode, BlendMode(particles, "particle") + DepthMode(particles, "particle"));
        var material = new ShaderMaterial { Shader = shader, ResourceName = $"EFSH particles {_effect.Form}" };
        material.SetShaderParameter("particle_map", LoadTexture(content, _effect.ParticleTexture!));
        _sorted = particles.Destination != 2;
        _quad = new QuadMesh { Size = Vector2.One * 2, Material = material };
        _draw = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            UseColors = true,
            UseCustomData = true,
            Mesh = _quad,
            InstanceCount = cloud.Capacity,
            VisibleInstanceCount = 0,
        };
        _visual = new MultiMeshInstance3D { Name = "EffectShaderParticles", Multimesh = _draw, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        AddChild(_visual);
        _drawBuffer = new float[cloud.Capacity * NativeParticleDrawBuffer.Stride];
        _samples = new FalloutEffectShaderParticleSample[cloud.Capacity];
        _drawOrder = new NativeParticleDrawOrder[cloud.Capacity];
        _cloud = cloud; _emitter = emitter; _target = target;
        if (_effect.Addons.Debris is { } debris && nodes.Count != 0) AttachAddons(content, records, debris, nodes, units);
    }

    private bool UpdateParticles(float delta)
    {
        if (_cloud is not { } cloud) return false;
        cloud.Update(_age, delta, Releasing, _emitter);
        if (_sourceLost && !Releasing)
        {
            // A freed bone, node or mesh ends emission; the instance releases.
            GD.PushWarning($"OPENNV_EFFECT_SHADER_DEFERRED shader={_effect.Form} reason=effect-shader-emission-source-retired");
            Stop();
        }
        PublishParticles(cloud);
        return !cloud.Finished;
    }

    private void PublishParticles(FalloutEffectShaderParticleCloud cloud)
    {
        var count = 0;
        for (var batch = 0; batch < cloud.BatchCount; batch++)
            for (var slot = 0; slot < FalloutEffectShaderTiming.ParticleBatchCapacity; slot++)
                if (cloud.Sample(batch, slot, _age, out var sample)) _samples[count++] = sample;
        if (_visibleCount != count) { _draw!.VisibleInstanceCount = count; _visibleCount = count; }
        var visible = _target.IsVisibleInTree();
        if (_visual!.Visible != visible) _visual.Visible = visible;
        // The batch bound follows the target; refit only after it moves a quarter extent.
        var centre = _target.GlobalPosition;
        if (!(centre.DistanceTo(_boundsCentre) < _boundsExtent / 4))
        {
            _boundsCentre = centre;
            _draw!.CustomAabb = new Aabb(centre - Vector3.One * (1.25f * _boundsExtent), Vector3.One * (2.5f * _boundsExtent));
        }
        if (count == 0) return;
        var camera = GetViewport().GetCamera3D();
        var basis = camera?.GlobalBasis.Orthonormalized() ?? Basis.Identity;
        // Source-alpha quads are drawn back to front; additive ones need no order.
        var ordered = _sorted && camera is not null;
        if (ordered)
        {
            var eye = ToNumerics(camera!.GlobalPosition);
            for (var index = 0; index < count; index++)
                _drawOrder[index] = new(Numerics.Vector3.DistanceSquared(_samples[index].Centre, eye), index);
            Array.Sort(_drawOrder, 0, count);
        }
        var scale = cloud.TileScale;
        for (var index = 0; index < count; index++)
        {
            ref readonly var sample = ref _samples[ordered ? _drawOrder[index].Index : index];
            var transform = NativeParticleDrawBuffer.Billboard(basis, sample.Angle, sample.HalfSize,
                new Vector3(sample.Centre.X, sample.Centre.Y, sample.Centre.Z));
            NativeParticleDrawBuffer.Write(_drawBuffer.AsSpan(index * NativeParticleDrawBuffer.Stride), transform,
                new Color(sample.Color.X, sample.Color.Y, sample.Color.Z, sample.Color.W),
                new Color(sample.TileOffset.X, scale.X, sample.TileOffset.Y, scale.Y));
        }
        _draw!.Buffer = _drawBuffer;
    }

    // Weapon: a point along local +Y up to the bound reach, moving in local XZ.
    private FalloutEffectShaderEmitter LineEmitter(Node3D target, float length) =>
        (Random random, out Numerics.Vector3 position, out Numerics.Vector3 direction) =>
        {
            position = direction = default;
            if (!Alive(target)) return false;
            var transform = target.GlobalTransform;
            position = ToNumerics(transform * (SourceY * (length * random.NextSingle())));
            direction = ToNumerics((transform.Basis * PlanarDirection(random)).Normalized());
            return true;
        };

    // Actor or activator: a random bone or helper node; a node with children
    // emits on the segment towards its parent, a leaf from its own origin.
    private FalloutEffectShaderEmitter? SkeletonEmitter(Node3D target, List<(Node3D Parent, int Bone)> nodes)
    {
        var bones = new List<(Skeleton3D Skeleton, int Bone, int Parent, bool Branch)>();
        foreach (var skeleton in SceneGraph.NodeTraversal.SelfAndDescendants<Skeleton3D>(target))
            for (var bone = 0; bone < skeleton.GetBoneCount(); bone++)
            {
                var parent = skeleton.GetBoneParent(bone);
                if (parent < 0) continue;
                bones.Add((skeleton, bone, parent, skeleton.GetBoneChildren(bone).Length != 0));
                nodes.Add((skeleton, bone));
            }
        var helpers = new List<(Node3D Node, Node3D Parent, bool Branch)>();
        foreach (var node in SceneGraph.NodeTraversal.SelfAndDescendants<Node3D>(target))
        {
            // Only source NIF nodes: not sound emitters, attachments or other effects.
            if (node == target || !node.HasMeta(SourceNodeMeta) || node is VisualInstance3D or Skeleton3D ||
                node.GetParent() is not Node3D parent) continue;
            helpers.Add((node, parent, node.GetChildren().Any(child => child is Node3D and not VisualInstance3D)));
            nodes.Add((node, -1));
        }
        if (bones.Count + helpers.Count == 0) return null;
        return (Random random, out Numerics.Vector3 position, out Numerics.Vector3 direction) =>
        {
            position = direction = default;
            var pick = random.Next(bones.Count + helpers.Count);
            Transform3D self, parent;
            bool branch;
            if (pick < bones.Count)
            {
                var (skeleton, bone, parentBone, isBranch) = bones[pick];
                if (!Alive(skeleton)) return false;
                self = skeleton.GlobalTransform * skeleton.GetBoneGlobalPose(bone);
                parent = skeleton.GlobalTransform * skeleton.GetBoneGlobalPose(parentBone);
                branch = isBranch;
            }
            else
            {
                var (node, parentNode, isBranch) = helpers[pick - bones.Count];
                if (!Alive(node) || !Alive(parentNode)) return false;
                self = node.GlobalTransform; parent = parentNode.GlobalTransform; branch = isBranch;
            }
            if (branch)
            {
                var weight = random.NextSingle();
                position = ToNumerics(self.Origin * weight + parent.Origin * (1 - weight));
                direction = ToNumerics((self.Basis * PlanarDirection(random)).Normalized());
            }
            else
            {
                position = ToNumerics(self.Origin);
                direction = ToNumerics(CubeDirection(random));
            }
            return true;
        };
    }

    // Other references: a random shaded geometry outside Bip subtrees; a
    // skinned one emits along a bone segment, a rigid one from a vertex.
    private FalloutEffectShaderEmitter? SurfaceEmitter(Node3D target)
    {
        var rigid = new List<(MeshInstance3D Mesh, Vector3[] Vertices, Vector3[] Normals)>();
        var skinned = new List<(MeshInstance3D Mesh, Skeleton3D Skeleton, int[] Bones)>();
        foreach (var mesh in SceneGraph.NodeTraversal.SelfAndDescendants<MeshInstance3D>(target))
        {
            if (mesh.Mesh is null || BelowBip(target, mesh)) continue;
            if (mesh.Skin is { } skin && mesh.GetNodeOrNull<Skeleton3D>(mesh.Skeleton) is { } skeleton)
            {
                var bones = Enumerable.Range(0, skin.GetBindCount())
                    .Select(bind => skin.GetBindBone(bind) is >= 0 and var bone ? bone : skeleton.FindBone(skin.GetBindName(bind)))
                    .Where(bone => bone >= 0).ToArray();
                if (bones.Length != 0) skinned.Add((mesh, skeleton, bones));
                continue;
            }
            var vertices = new List<Vector3>(); var normals = new List<Vector3>();
            for (var surface = 0; surface < mesh.Mesh.GetSurfaceCount(); surface++)
            {
                var arrays = mesh.Mesh.SurfaceGetArrays(surface);
                var points = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
                var directions = arrays[(int)Mesh.ArrayType.Normal].AsVector3Array();
                if (points.Length == 0 || directions.Length != points.Length) continue;
                vertices.AddRange(points); normals.AddRange(directions);
            }
            if (vertices.Count != 0) rigid.Add((mesh, vertices.ToArray(), normals.ToArray()));
        }
        if (rigid.Count + skinned.Count == 0) return null;
        return (Random random, out Numerics.Vector3 position, out Numerics.Vector3 direction) =>
        {
            position = direction = default;
            var pick = random.Next(rigid.Count + skinned.Count);
            if (pick < rigid.Count)
            {
                var (mesh, vertices, normals) = rigid[pick];
                if (!Alive(mesh)) return false;
                var vertex = random.Next(vertices.Length);
                var transform = mesh.GlobalTransform;
                position = ToNumerics(transform * vertices[vertex]);
                direction = ToNumerics((transform.Basis * normals[vertex]).Normalized());
                return true;
            }
            var (skinnedMesh, skeleton, bones) = skinned[pick - rigid.Count];
            if (!Alive(skinnedMesh) || !Alive(skeleton)) return false;
            var bone = bones[random.Next(bones.Length)];
            var paired = skeleton.GetBoneParent(bone) is >= 0 and var parent ? parent : bone;
            var weight = random.NextSingle();
            var origin = skeleton.GlobalTransform * skeleton.GetBoneGlobalPose(bone).Origin;
            var other = skeleton.GlobalTransform * skeleton.GetBoneGlobalPose(paired).Origin;
            position = ToNumerics(origin * weight + other * (1 - weight));
            direction = ToNumerics((skinnedMesh.GlobalBasis * PlanarDirection(random)).Normalized());
            return true;
        };
    }

    private const string SourceNodeMeta = "opennv_nif_source_node";

    // A retired source flags the instance once instead of failing every frame.
    private bool Alive(Node node)
    {
        if (IsInstanceValid(node) && node.IsInsideTree()) return true;
        _sourceLost = true;
        return false;
    }

    private static bool BelowBip(Node3D root, Node node)
    {
        for (var current = node.GetParent(); current is not null && current != root; current = current.GetParent())
            if (current.Name.ToString().StartsWith("Bip", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    // Target-local bound centre and radius over its resident geometry.
    private static (Vector3 Centre, float Radius) LocalBounds(Node3D target)
    {
        var inverse = target.GlobalTransform.AffineInverse();
        Aabb? bounds = null;
        foreach (var visual in SceneGraph.NodeTraversal.SelfAndDescendants<VisualInstance3D>(target))
        {
            var local = inverse * visual.GlobalTransform * visual.GetAabb();
            bounds = bounds is { } merged ? merged.Merge(local) : local;
        }
        return bounds is { } value ? (value.GetCenter(), value.Size.Length() / 2) : (Vector3.Zero, 0);
    }

    private static Vector3 PlanarDirection(Random random) =>
        (SourceX * (random.NextSingle() * 2 - 1) + SourceZ * (random.NextSingle() * 2 - 1)).Normalized();
    private static Vector3 CubeDirection(Random random) =>
        new Vector3(random.NextSingle() * 2 - 1, random.NextSingle() * 2 - 1, random.NextSingle() * 2 - 1).Normalized();
    private static Vector3 Convert(System.Numerics.Vector3 value) => GamebryoCoordinate.ConvertVector(new(value.X, value.Y, value.Z));
    private static Numerics.Vector3 ToNumerics(Vector3 value) => new(value.X, value.Y, value.Z);

    private void ReleaseParticleDraw()
    {
        if (_visual is not null && IsInstanceValid(_visual)) _visual.Multimesh = null;
        _draw?.Dispose();
        if (_quad is not null) { _quad.Material = null; _quad.Dispose(); }
        _draw = null; _quad = null; _visual = null;
    }
}
