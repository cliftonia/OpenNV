using Godot;

namespace OpenNV.Runtime.InputSystem;

/// <summary>
/// Presents a standard (Xbox-layout) gamepad as the player's own keyboard and
/// mouse bindings. Gameplay controls press whichever key or mouse button the
/// installation binds to that Fallout control, so remapping, aliases and raw
/// key consumers all observe ordinary bound input. While the pointer is free
/// (menus, dialogue, Pip-Boy) the sticks drive a drawn cursor and A clicks.
/// </summary>
internal sealed partial class NativeGamepadInput : Node
{
    // Fallout control indices (FalloutInputControls order).
    private const int Forward = 0, Back = 1, Left = 2, Right = 3, Use = 4, Activate = 5, Block = 6, ReadyItem = 7,
        Sneak = 8, Jump = 12, TogglePov = 13, MenuMode = 14, Rest = 15, Vats = 16, Hotkey1 = 17, Grab = 27;
    private const float StickDeadzone = .2f, MoveThreshold = .35f, TriggerThreshold = .4f;
    private const float LookPixelsPerSecond = 1100, CursorPixelsPerSecond = 900;
    private const bool InvertControllerLookY = true;

    private static readonly (JoyButton Button, int Control)[] GameplayButtons =
    [
        (JoyButton.A, Activate), (JoyButton.B, MenuMode), (JoyButton.X, ReadyItem), (JoyButton.Y, Jump),
        (JoyButton.RightShoulder, Vats), (JoyButton.LeftShoulder, Grab), (JoyButton.LeftStick, Sneak),
        (JoyButton.RightStick, TogglePov), (JoyButton.Back, Rest),
        (JoyButton.DpadUp, Hotkey1), (JoyButton.DpadRight, Hotkey1 + 1), (JoyButton.DpadDown, Hotkey1 + 2),
        (JoyButton.DpadLeft, Hotkey1 + 3),
    ];

    internal static Func<int, InputEvent?>? BoundInput { get; set; }
    internal static Func<bool>? GameplayActive { get; set; }
    // Synthetic device id marking stick look; accepted without pointer capture.
    private const int LookDevice = -0x4F4E;
    internal static bool IsLookInput(InputEventMouseMotion motion) => motion.Device == LookDevice;
    private static readonly bool PointerCapture = DisplayServer.HasFeature(DisplayServer.Feature.MouseWarp);

    private readonly Dictionary<string, bool> _held = [];
    private readonly Dictionary<JoyButton, bool> _buttons = [];
    private Vector2 _cursor = new(-1, -1);
    private CanvasLayer? _cursorLayer;
    private Control? _cursorMark;
    private NativeTouchControls? _touch;
    private static readonly bool Touchscreen = DisplayServer.IsTouchscreenAvailable();
    private const float TouchLookScale = 1.6f;

    public override void _Ready()
    {
        Name = "NativeGamepadInput";
        ProcessMode = ProcessModeEnum.Always;
        ProcessPriority = -100;
        _cursorLayer = new CanvasLayer { Layer = 128, Visible = false };
        _cursorMark = new ColorRect
        {
            Color = new Color(0.25f, 1f, 0.5f, 0.9f), Size = new Vector2(10, 10),
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        _cursorLayer.AddChild(_cursorMark);
        AddChild(_cursorLayer);
        if (Touchscreen)
        {
            var touchLayer = new CanvasLayer { Layer = 127 };
            _touch = new NativeTouchControls { Name = "NativeTouchControls" };
            touchLayer.AddChild(_touch);
            AddChild(touchLayer);
        }
        foreach (var pad in Input.GetConnectedJoypads()) Report(pad, true);
        Input.Singleton.JoyConnectionChanged += Report;
    }

    public override void _ExitTree() => Input.Singleton.JoyConnectionChanged -= Report;

    private static void Report(long device, bool connected) =>
        GD.Print($"OPENNV_GAMEPAD device={device} connected={connected} name={Input.GetJoyName((int)device)}");

    public override void _Process(double delta)
    {
        var pads = Input.GetConnectedJoypads();
        var device = pads.Count == 0 ? -1 : pads[0];
        var gameplay = BoundInput is not null && (PointerCapture
            ? Input.MouseMode == Input.MouseModeEnum.Captured
            : GameplayActive?.Invoke() == true);
        // On-screen controls stand in for a gamepad only during gameplay; menus
        // keep ordinary touch-to-mouse taps. While shown, touches must not also
        // arrive as emulated mouse clicks, which the game binds to Fire.
        var touch = _touch is not null && gameplay && device < 0;
        _touch?.SetActive(touch);
        if (_touch is not null) Input.EmulateMouseFromTouch = !touch;
        if (device < 0 && !touch) { ReleaseAll(); _cursorLayer!.Visible = false; return; }
        var leftStick = device < 0 ? Vector2.Zero : Stick(device, JoyAxis.LeftX, JoyAxis.LeftY);
        var rightStick = device < 0 ? Vector2.Zero : Stick(device, JoyAxis.RightX, JoyAxis.RightY);
        if (touch) leftStick += _touch!.Stick;
        if (gameplay)
        {
            _cursorLayer!.Visible = false;
            Hold("move_forward", Forward, leftStick.Y < -MoveThreshold);
            Hold("move_back", Back, leftStick.Y > MoveThreshold);
            Hold("move_left", Left, leftStick.X < -MoveThreshold);
            Hold("move_right", Right, leftStick.X > MoveThreshold);
            if (device >= 0)
            {
                Hold("use", Use, Input.GetJoyAxis(device, JoyAxis.TriggerRight) > TriggerThreshold);
                Hold("block", Block, Input.GetJoyAxis(device, JoyAxis.TriggerLeft) > TriggerThreshold);
                foreach (var (button, control) in GameplayButtons)
                    Hold("button_" + button, control, Input.IsJoyButtonPressed(device, button));
            }
            // Controller look is inverted on the vertical axis (push up to look down).
            var look = new Vector2(rightStick.X, InvertControllerLookY ? -rightStick.Y : rightStick.Y) *
                LookPixelsPerSecond * (float)delta;
            if (touch)
            {
                foreach (var control in NativeTouchControls.Controls)
                    Hold("touch_" + control, control, _touch!.Pressed(control));
                look += _touch!.ConsumeLook() * TouchLookScale;
                if (_touch.ConsumePause()) TapKey(Key.Escape);
            }
            if (look != Vector2.Zero)
                Input.ParseInputEvent(new InputEventMouseMotion { Device = LookDevice, Relative = look });
        }
        else
        {
            ReleaseAll();
            MoveCursor(leftStick + rightStick, delta);
            var click = Input.IsJoyButtonPressed(device, JoyButton.A);
            if (Edge(JoyButton.A, click)) ParseMouseButton(click);
        }
        if (device < 0) return;
        // Start pauses and B backs out of menus; both are the cancel key.
        if (Edge(JoyButton.Start, Input.IsJoyButtonPressed(device, JoyButton.Start)) && _buttons[JoyButton.Start])
            TapKey(Key.Escape);
        if (!gameplay && Edge(JoyButton.B, Input.IsJoyButtonPressed(device, JoyButton.B)) && _buttons[JoyButton.B])
            TapKey(Key.Escape);
    }

    private static Vector2 Stick(int device, JoyAxis x, JoyAxis y)
    {
        var value = new Vector2(Input.GetJoyAxis(device, x), Input.GetJoyAxis(device, y));
        var length = value.Length();
        if (length < StickDeadzone) return Vector2.Zero;
        // Rescale past the deadzone and square for fine aim near centre.
        var scaled = Math.Min(1, (length - StickDeadzone) / (1 - StickDeadzone));
        return value / length * scaled * scaled;
    }

    private bool Edge(JoyButton button, bool pressed)
    {
        var previous = _buttons.GetValueOrDefault(button);
        _buttons[button] = pressed;
        return previous != pressed;
    }

    private void Hold(string slot, int control, bool pressed)
    {
        if (_held.GetValueOrDefault(slot) == pressed) return;
        if (BoundInput?.Invoke(control) is not { } bound) return;
        _held[slot] = pressed;
        Emit(bound, control, pressed);
    }

    private static void Emit(InputEvent bound, int control, bool pressed)
    {
        var copy = (InputEvent)bound.Duplicate();
        switch (copy)
        {
            case InputEventKey key: key.Pressed = pressed; break;
            case InputEventMouseButton button: button.Pressed = pressed; break;
            default: throw new NotSupportedException($"Fallout control {control} has no gamepad-presentable binding.");
        }
        Input.ParseInputEvent(copy);
    }

    private void ReleaseAll()
    {
        if (_held.Count == 0) return;
        foreach (var slot in _held.Where(pair => pair.Value).Select(pair => pair.Key).ToArray())
        {
            var control = slot switch
            {
                "move_forward" => Forward, "move_back" => Back, "move_left" => Left, "move_right" => Right,
                "use" => Use, "block" => Block,
                _ when slot.StartsWith("touch_", StringComparison.Ordinal) => int.Parse(slot["touch_".Length..]),
                _ => GameplayButtons.First(pair => "button_" + pair.Button == slot).Control,
            };
            if (BoundInput?.Invoke(control) is { } bound) Emit(bound, control, false);
        }
        _held.Clear();
    }

    private void MoveCursor(Vector2 stick, double delta)
    {
        var size = GetViewport().GetVisibleRect().Size;
        if (_cursor.X < 0) _cursor = size / 2;
        _cursorLayer!.Visible = true;
        if (stick == Vector2.Zero) return;
        _cursor = (_cursor + stick * CursorPixelsPerSecond * (float)delta).Clamp(Vector2.Zero, size);
        _cursorMark!.Position = _cursor - _cursorMark.Size / 2;
        Input.ParseInputEvent(new InputEventMouseMotion { Position = _cursor, GlobalPosition = _cursor, Relative = stick });
    }

    private void ParseMouseButton(bool pressed) => Input.ParseInputEvent(new InputEventMouseButton
    {
        ButtonIndex = MouseButton.Left, Pressed = pressed, Position = _cursor, GlobalPosition = _cursor,
        ButtonMask = pressed ? MouseButtonMask.Left : 0,
    });

    private static void TapKey(Key key)
    {
        Input.ParseInputEvent(new InputEventKey { Keycode = key, PhysicalKeycode = key, Pressed = true });
        Input.ParseInputEvent(new InputEventKey { Keycode = key, PhysicalKeycode = key, Pressed = false });
    }
}
