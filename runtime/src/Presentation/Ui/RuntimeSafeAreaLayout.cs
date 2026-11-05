using Godot;

namespace OpenNV.Runtime.Presentation.Ui;

/// <summary>
/// Keeps 2D presentation inside the display's safe area on devices with a
/// notch or Dynamic Island. Every canvas layer of the root viewport, and the
/// root canvas itself, is uniformly scaled and centred into the safe rect; the
/// 3D view still fills the screen. A layer that must stay in raw screen
/// coordinates (a cursor drawn at input-event positions) sets
/// <see cref="FullScreenMeta"/>.
/// </summary>
internal sealed partial class RuntimeSafeAreaLayout : Node
{
    internal const string FullScreenMeta = "opennv_full_screen";
    private Transform2D _transform = Transform2D.Identity;

    public override void _Ready()
    {
        Name = "RuntimeSafeAreaLayout";
        GetTree().NodeAdded += OnNodeAdded;
        GetTree().Root.SizeChanged += Refresh;
        Refresh();
    }

    public override void _ExitTree()
    {
        GetTree().NodeAdded -= OnNodeAdded;
        GetTree().Root.SizeChanged -= Refresh;
    }

    private void Refresh()
    {
        var root = GetTree().Root;
        var size = (Vector2)root.Size;
        var safe = DisplayServer.GetDisplaySafeArea();
        _transform = Transform2D.Identity;
        if (size.X > 0 && size.Y > 0 && safe.Size.X > 0 && safe.Size.Y > 0)
        {
            var scale = Math.Min(1f, Math.Min(safe.Size.X / size.X, safe.Size.Y / size.Y));
            var offset = (Vector2)safe.Position + ((Vector2)safe.Size - size * scale) / 2;
            _transform = new Transform2D(0, new Vector2(scale, scale), 0, offset);
        }
        root.CanvasTransform = _transform;
        foreach (var layer in root.FindChildren("*", nameof(CanvasLayer), true, false).OfType<CanvasLayer>()) Apply(layer);
        GD.Print($"OPENNV_SAFE_AREA screen={root.Size} safe={safe} scale={_transform.Scale.X:0.000} offset={_transform.Origin}");
    }

    private void OnNodeAdded(Node node)
    {
        if (node is CanvasLayer layer) Apply(layer);
    }

    private void Apply(CanvasLayer layer)
    {
        if (layer.GetViewport() != GetTree().Root || layer.HasMeta(FullScreenMeta)) return;
        layer.Transform = _transform;
    }
}
