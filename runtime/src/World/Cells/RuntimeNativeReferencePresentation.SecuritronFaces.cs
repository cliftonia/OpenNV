using Godot;
using OpenNV.Runtime.Content;

namespace OpenNV.Runtime.World.Cells;

// A retained Securitron face is applied whenever the reference's 3D attaches
// and whenever the world changes a face. Only the base texture of the
// Screen01:0 shape changes: its material is updated in place, so the screen
// NIF's own texture-transform and material-colour controllers keep animating it.
internal partial class RuntimeNativeReferencePresentation
{
    private void PublishSecuritronFaces()
    {
        var revision = _world.SecuritronFaceRevision;
        if (_publishedFaceRevision == revision) return;
        _publishedFaceRevision = revision;
        foreach (var (key, node) in _nodes)
            if (GodotObject.IsInstanceValid(node) && !node.IsQueuedForDeletion()) ApplySecuritronFace(key, node);
    }

    private void ApplySecuritronFace(FalloutFormKey key, Node3D node)
    {
        if (_world.SecuritronFace(key) is not { } face) return;
        var screens = SceneGraph.NodeTraversal.SelfAndDescendants<MeshInstance3D>(node)
            .Where(mesh => mesh.GetMeta("opennv_nif_source_name", "").AsString() == FalloutSecuritronFace.ScreenNode).ToArray();
        if (screens.Length == 0) return; // No screen shape: retail does nothing.
        try
        {
            var texture = face.TexturePath is { } path ? Texture(path) : null;
            foreach (var screen in screens)
                for (var surface = 0; surface < screen.Mesh.GetSurfaceCount(); surface++)
                    SetDiffuse(screen.GetActiveMaterial(surface) ?? throw new InvalidDataException("Securitron screen has no source material."), texture);
            GD.Print($"OPENNV_SECURITRON_FACE reference={key} personality={face.Personality} emotion={face.Emotion} " +
                $"texture={face.TexturePath ?? "none"} screens={screens.Length}");
        }
        catch (Exception error) when (error is FileNotFoundException or NotSupportedException or InvalidDataException)
        {
            GD.PushError($"OPENNV_SECURITRON_FACE_DIVERGENCE reference={key} personality={face.Personality} emotion={face.Emotion}: {error.Message}");
        }
    }
}
