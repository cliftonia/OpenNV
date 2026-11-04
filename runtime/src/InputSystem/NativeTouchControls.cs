using Godot;

namespace OpenNV.Runtime.InputSystem;

/// <summary>
/// On-screen gameplay controls for touch devices without a gamepad. A floating
/// stick on the left half moves, dragging elsewhere looks, and the right-edge
/// buttons press Fallout controls. <see cref="NativeGamepadInput"/> turns these
/// into the player's bound keys exactly as it does for a physical gamepad.
/// </summary>
internal sealed partial class NativeTouchControls : Control
{
    internal const int PauseControl = -1;
    private const float StickRadiusFraction = .12f, LookPixelScale = 1f;

    private sealed record TouchButton(string Label, int Control, Vector2 Fraction, float Radius);

    // Positions are (x from right edge, y from bottom edge) in screen heights.
    private static readonly TouchButton[] Buttons =
    [
        new("FIRE", FalloutControl.Use, new(.14f, .17f), .095f), new("AIM", FalloutControl.Block, new(.34f, .11f), .06f),
        new("USE", FalloutControl.Activate, new(.31f, .31f), .06f), new("JUMP", FalloutControl.Jump, new(.11f, .37f), .06f),
        new("RELOAD", FalloutControl.ReadyItem, new(.47f, .22f), .055f), new("SNEAK", FalloutControl.Sneak, new(.11f, .53f), .055f),
        new("VATS", FalloutControl.Vats, new(.27f, .48f), .055f), new("PIP-BOY", FalloutControl.MenuMode, new(.11f, .88f), .055f),
        new("PAUSE", PauseControl, new(.27f, .88f), .05f),
    ];

    private readonly Dictionary<int, int> _buttonFingers = [];
    private int _stickFinger = -1, _lookFinger = -1;
    private Vector2 _stickOrigin, _stickPosition, _lookDelta;
    private bool _pauseTapped;

    internal Vector2 Stick { get; private set; }
    internal bool Pressed(int control) => _buttonFingers.ContainsValue(control);
    internal static IEnumerable<int> Controls => Buttons.Select(button => button.Control).Where(c => c != PauseControl);

    internal Vector2 ConsumeLook()
    {
        var delta = _lookDelta * LookPixelScale;
        _lookDelta = Vector2.Zero;
        return delta;
    }

    internal bool ConsumePause()
    {
        var tapped = _pauseTapped;
        _pauseTapped = false;
        return tapped;
    }

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        Visible = false;
    }

    internal void SetActive(bool active)
    {
        if (Visible == active) return;
        Visible = active;
        if (!active) Reset();
        QueueRedraw();
    }

    private void Reset()
    {
        _buttonFingers.Clear();
        _stickFinger = _lookFinger = -1;
        Stick = Vector2.Zero;
        _lookDelta = Vector2.Zero;
    }

    public override void _Input(InputEvent inputEvent)
    {
        if (!Visible) return;
        var size = GetViewportRect().Size;
        switch (inputEvent)
        {
            case InputEventScreenTouch { Pressed: true } touch:
                if (Hit(touch.Position, size) is { } button)
                {
                    if (button.Control == PauseControl) _pauseTapped = true;
                    else _buttonFingers[touch.Index] = button.Control;
                }
                else if (touch.Position.X < size.X * .45f && _stickFinger < 0)
                {
                    _stickFinger = touch.Index;
                    _stickOrigin = _stickPosition = touch.Position;
                    Stick = Vector2.Zero;
                }
                else if (_lookFinger < 0) _lookFinger = touch.Index;
                break;
            case InputEventScreenTouch { Pressed: false } release:
                _buttonFingers.Remove(release.Index);
                if (release.Index == _stickFinger) { _stickFinger = -1; Stick = Vector2.Zero; }
                if (release.Index == _lookFinger) _lookFinger = -1;
                break;
            case InputEventScreenDrag drag when drag.Index == _stickFinger:
                _stickPosition = drag.Position;
                var radius = size.Y * StickRadiusFraction;
                var offset = (_stickPosition - _stickOrigin) / radius;
                Stick = offset.Length() > 1 ? offset.Normalized() : offset;
                break;
            case InputEventScreenDrag look when look.Index == _lookFinger:
                _lookDelta += look.Relative;
                break;
            default:
                return;
        }
        GetViewport().SetInputAsHandled();
        QueueRedraw();
    }

    private static TouchButton? Hit(Vector2 position, Vector2 size) =>
        Buttons.FirstOrDefault(button => position.DistanceTo(Center(button, size)) <= button.Radius * size.Y * 1.15f);

    private static Vector2 Center(TouchButton button, Vector2 size) =>
        new(size.X - button.Fraction.X * size.Y, size.Y - button.Fraction.Y * size.Y);

    public override void _Draw()
    {
        var size = GetViewportRect().Size;
        var font = ThemeDB.FallbackFont;
        var green = new Color(.35f, 1f, .55f);
        foreach (var button in Buttons)
        {
            var center = Center(button, size);
            var radius = button.Radius * size.Y;
            var held = Pressed(button.Control);
            DrawCircle(center, radius, new Color(green, held ? .45f : .18f));
            DrawArc(center, radius, 0, Mathf.Tau, 48, new Color(green, .8f), 2, true);
            var fontSize = (int)(radius * .42f);
            var text = font.GetStringSize(button.Label, HorizontalAlignment.Center, -1, fontSize);
            DrawString(font, center + new Vector2(-text.X / 2, fontSize * .35f), button.Label,
                HorizontalAlignment.Center, -1, fontSize, new Color(green, .95f));
        }
        if (_stickFinger >= 0)
        {
            var radius = size.Y * StickRadiusFraction;
            DrawCircle(_stickOrigin, radius, new Color(green, .12f));
            DrawArc(_stickOrigin, radius, 0, Mathf.Tau, 48, new Color(green, .7f), 2, true);
            DrawCircle(_stickOrigin + Stick * radius, radius * .4f, new Color(green, .5f));
        }
    }
}
