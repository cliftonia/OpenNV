using Godot;

namespace OpenNV.Runtime.InputSystem;

/// <summary>Fallout control indices, in FalloutInputControls order.</summary>
internal static class FalloutControl
{
    internal const int Forward = 0, Back = 1, Left = 2, Right = 3, Use = 4, Activate = 5, Block = 6, ReadyItem = 7,
        Sneak = 8, Jump = 12, TogglePov = 13, MenuMode = 14, Rest = 15, Vats = 16, Hotkey1 = 17, Grab = 27;
}

/// <summary>
/// Presents a standard (Xbox-layout) gamepad, and on mobile the on-screen touch
/// controls, as the player's own keyboard and mouse bindings. Gameplay controls
/// press whichever key or mouse button the installation binds to that Fallout
/// control, so remapping, aliases and raw key consumers all observe ordinary
/// bound input. While the pointer is free (menus, dialogue, Pip-Boy) the sticks
/// drive a drawn cursor and A clicks.
/// </summary>
internal sealed partial class NativeGamepadInput : Node
{
    private const float StickDeadzone = .2f, MoveThreshold = .35f, TriggerThreshold = .4f;
    private const float LookPixelsPerSecond = 1100, CursorPixelsPerSecond = 900, TouchLookScale = 1.6f;
    private const bool InvertControllerLookY = true;

    private static readonly (JoyButton Button, int Control)[] GameplayButtons =
    [
        (JoyButton.A, FalloutControl.Activate), (JoyButton.B, FalloutControl.MenuMode), (JoyButton.X, FalloutControl.ReadyItem),
        (JoyButton.Y, FalloutControl.Jump), (JoyButton.RightShoulder, FalloutControl.Vats), (JoyButton.LeftShoulder, FalloutControl.Grab),
        (JoyButton.LeftStick, FalloutControl.Sneak), (JoyButton.RightStick, FalloutControl.TogglePov), (JoyButton.Back, FalloutControl.Rest),
        (JoyButton.DpadUp, FalloutControl.Hotkey1), (JoyButton.DpadRight, FalloutControl.Hotkey1 + 1),
        (JoyButton.DpadDown, FalloutControl.Hotkey1 + 2), (JoyButton.DpadLeft, FalloutControl.Hotkey1 + 3),
    ];
    private static readonly JoyButton[] TrackedButtons =
        [.. GameplayButtons.Select(pair => pair.Button), JoyButton.Start];

    internal static Func<int, InputEvent?>? BoundInput { get; set; }
    internal static Func<bool>? GameplayActive { get; set; }
    // Synthetic device id marking stick look; accepted without pointer capture.
    private const int LookDevice = -0x4F4E;
    internal static bool IsLookInput(InputEventMouseMotion motion) => motion.Device == LookDevice;
    private static readonly bool PointerCapture = DisplayServer.HasFeature(DisplayServer.Feature.MouseWarp);
    // On-screen controls are a mobile feature; desktop touchscreens keep the mouse.
    private static readonly bool TouchControls = (OperatingSystem.IsIOS() || OperatingSystem.IsAndroid()) &&
        DisplayServer.IsTouchscreenAvailable();

    // Each held slot remembers the exact event it pressed, so release never
    // depends on a binding owner that may have gone away meanwhile.
    private readonly Dictionary<string, InputEvent> _held = [];
    private readonly Dictionary<JoyButton, bool> _previous = [];
    private readonly HashSet<JoyButton> _suppressed = [];
    private bool? _lastGameplay;
    private Vector2 _cursor = new(-1, -1);
    private bool _cursorShown;
    private CanvasLayer? _cursorLayer;
    private Control? _cursorMark;
    private NativeTouchControls? _touch;

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
        if (TouchControls)
        {
            var touchLayer = new CanvasLayer { Layer = 127 };
            _touch = new NativeTouchControls { Name = "NativeTouchControls" };
            touchLayer.AddChild(_touch);
            AddChild(touchLayer);
        }
        foreach (var pad in Input.GetConnectedJoypads()) Report(pad, true);
        Input.Singleton.JoyConnectionChanged += Report;
    }

    public override void _ExitTree()
    {
        Input.Singleton.JoyConnectionChanged -= Report;
        ReleaseAll();
    }

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

        var pressed = TrackedButtons.ToDictionary(button => button, button => device >= 0 && Input.IsJoyButtonPressed(device, button));
        if (_lastGameplay != gameplay)
        {
            // A button that switched modes (e.g. B opening the Pip-Boy) stays
            // ignored until released, so it cannot act again in the new mode.
            ReleaseAll();
            _suppressed.Clear();
            foreach (var (button, down) in pressed) if (down) _suppressed.Add(button);
            _lastGameplay = gameplay;
            if (gameplay) _cursorShown = false;
        }
        foreach (var (button, down) in pressed) if (!down) _suppressed.Remove(button);
        bool Down(JoyButton button) => pressed[button] && !_suppressed.Contains(button);
        bool Tapped(JoyButton button) => Down(button) && !_previous.GetValueOrDefault(button);

        if (device < 0 && !touch) { FinishFrame(pressed); _cursorLayer!.Visible = false; return; }
        var leftStick = device < 0 ? Vector2.Zero : Stick(device, JoyAxis.LeftX, JoyAxis.LeftY);
        var rightStick = device < 0 ? Vector2.Zero : Stick(device, JoyAxis.RightX, JoyAxis.RightY);
        if (touch) leftStick += _touch!.Stick;
        if (gameplay)
        {
            _cursorLayer!.Visible = false;
            Hold("move_forward", FalloutControl.Forward, leftStick.Y < -MoveThreshold);
            Hold("move_back", FalloutControl.Back, leftStick.Y > MoveThreshold);
            Hold("move_left", FalloutControl.Left, leftStick.X < -MoveThreshold);
            Hold("move_right", FalloutControl.Right, leftStick.X > MoveThreshold);
            Hold("use", FalloutControl.Use, device >= 0 && Input.GetJoyAxis(device, JoyAxis.TriggerRight) > TriggerThreshold);
            Hold("block", FalloutControl.Block, device >= 0 && Input.GetJoyAxis(device, JoyAxis.TriggerLeft) > TriggerThreshold);
            foreach (var (button, control) in GameplayButtons) Hold("button_" + button, control, Down(button));
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
        else if (device >= 0)
        {
            MoveCursor(leftStick + rightStick, delta);
            if (Tapped(JoyButton.A) || !Down(JoyButton.A) && _previous.GetValueOrDefault(JoyButton.A))
            {
                ShowCursor();
                ParseMouseButton(Down(JoyButton.A));
            }
            if (Tapped(JoyButton.B)) TapKey(Key.Escape);
        }
        // Start pauses (gameplay) and backs out (menus); both are the cancel key.
        if (Tapped(JoyButton.Start)) TapKey(Key.Escape);
        FinishFrame(pressed);

        void FinishFrame(Dictionary<JoyButton, bool> state)
        {
            foreach (var (button, down) in state) _previous[button] = down && !_suppressed.Contains(button);
        }
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

    private void Hold(string slot, int control, bool pressed)
    {
        if (_held.TryGetValue(slot, out var held))
        {
            if (pressed) return;
            _held.Remove(slot);
            Emit(held, control, false);
            return;
        }
        if (!pressed || BoundInput?.Invoke(control) is not { } bound) return;
        _held[slot] = bound;
        Emit(bound, control, true);
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
        foreach (var (_, held) in _held.ToArray()) Emit(held, -1, false);
        _held.Clear();
    }

    private void ShowCursor()
    {
        var size = GetViewport().GetVisibleRect().Size;
        if (_cursor.X < 0) _cursor = size / 2;
        _cursorShown = true;
        _cursorLayer!.Visible = true;
        _cursorMark!.Position = _cursor - _cursorMark.Size / 2;
    }

    private void MoveCursor(Vector2 stick, double delta)
    {
        // The cursor appears only once the gamepad is actually used in a menu.
        if (stick == Vector2.Zero) { _cursorLayer!.Visible = _cursorShown; return; }
        ShowCursor();
        var size = GetViewport().GetVisibleRect().Size;
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
