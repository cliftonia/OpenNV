using Godot;
using OpenNV.Runtime.Content;
using OpenNV.Runtime.Formats.Gamebryo;

namespace OpenNV.Runtime.Presentation.Rendering;

// EFSH add-on models: one weighted DEBR model on every emitter node, faded and
// scaled by the instance's add-on clock.
internal sealed partial class NativeEffectShader
{
    private readonly List<(Node3D Root, GeometryInstance3D[] Geometry)> _addons = [];
    private FalloutEffectShaderAddonClock _addonClock;

    private void AttachAddons(RuntimeLiveContentSource content, FalloutPluginStack records, FalloutFormKey form,
        List<(Node3D Parent, int Bone)> nodes, float units)
    {
        var debris = FalloutDebris.Read(records, form);
        var prototypes = new Dictionary<string, RuntimeNativeNifPrototype>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var initial = Math.Max(.01f, _effect.Addons.ScaleStart ?? 0);
            foreach (var (parent, bone) in nodes)
            {
                var model = debris.Pick(_random).Model;
                if (!prototypes.TryGetValue(model, out var prototype))
                {
                    if (!content.TryRead(model, null, out var bytes, out _)) throw new FileNotFoundException("Effect shader add-on model is missing.", model);
                    prototypes.Add(model, prototype = new RuntimeNativeNifPrototype(bytes, units));
                }
                var root = prototype.InstantiatePlaced(Transform3D.Identity);
                root.Scale = Vector3.One * initial;
                Node3D host = parent;
                if (bone >= 0) parent.AddChild(host = new BoneAttachment3D { Name = $"EffectShaderAddon{bone}", BoneIdx = bone });
                host.AddChild(root);
                _addons.Add((bone >= 0 ? host : root, SceneGraph.NodeTraversal.SelfAndDescendants<GeometryInstance3D>(root).ToArray()));
                foreach (var geometry in _addons[^1].Geometry) geometry.Transparency = 1;
            }
        }
        finally { foreach (var prototype in prototypes.Values) prototype.Scene.Root.Free(); }
    }

    private bool UpdateAddons(float delta)
    {
        if (_addons.Count == 0) return false;
        var source = _effect.Addons;
        _addonClock.Step(source, Releasing, delta);
        var scale = Vector3.One * _addonClock.Scale(source, Releasing);
        foreach (var (root, geometry) in _addons)
        {
            (root is BoneAttachment3D ? root.GetChild<Node3D>(0) : root).Scale = scale;
            foreach (var instance in geometry) instance.Transparency = 1 - _addonClock.Alpha;
        }
        return _addonClock.Alpha > 1e-4f;
    }

    private void ReleaseAddons()
    {
        foreach (var (root, _) in _addons) if (IsInstanceValid(root)) root.QueueFree();
        _addons.Clear();
    }
}
