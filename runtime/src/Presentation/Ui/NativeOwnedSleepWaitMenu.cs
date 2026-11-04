using System.Globalization;
using System.Xml.Linq;
using Godot;
using OpenNV.Runtime.Content;
using OpenNV.Runtime.Gameplay.State;

namespace OpenNV.Runtime.Presentation.Ui;

/// <summary>SleepWaitMenu (menus/sleep_wait_menu.xml): chooses hours, then counts them down one game hour at a time.</summary>
internal sealed partial class NativeOwnedSleepWaitMenu : Control
{
    // OpenNV pacing for the visible hour count; the retail interval is unverified.
    private const double SecondsPerHour = .12;
    private readonly FalloutPluginStack _records;
    private readonly NativeOwnedMenuTree _tiles;
    private readonly XElement _scrollbar, _hours, _time, _prompt;
    private readonly List<(XElement Tile, Control Control)> _targets = [];
    private readonly FalloutRestKind _kind;
    private readonly Func<string> _currentTime;
    private readonly Func<bool> _advanceHour;
    private readonly Action<int> _complete;
    private readonly string _verb;
    private double _elapsed;
    private bool _closed;
    private NativeViewportLayout? _viewportLayout;
    internal int Hours { get; private set; } = 1;
    internal int RestedHours { get; private set; }
    internal bool Resting { get; private set; }
    internal string? Error { get; private set; }
    internal object State => new { kind = _kind.ToString(), hours = Hours, resting = Resting, rested = RestedHours, error = Error };

    // advanceHour advances one game hour and returns false when the rest is
    // interrupted; complete receives the hours actually rested (0 = cancel).
    internal NativeOwnedSleepWaitMenu(FalloutPluginStack records, FalloutRestKind kind, Func<string> currentTime,
        Func<bool> advanceHour, Action<int> complete)
    {
        _records = records; _kind = kind; _currentTime = currentTime; _advanceHour = advanceHour; _complete = complete;
        Name = "OwnedSleepWaitMenu"; TopLevel = true; ZIndex = 100;
        ProcessMode = ProcessModeEnum.Always; MouseFilter = MouseFilterEnum.Stop;
        var menu = FalloutMenuXml.Expand(FalloutMenuXml.Read("menus/sleep_wait_menu.xml")).Elements("menu").Single();
        _tiles = new(menu, name => FalloutGameSettingStrings.Read(records, name));
        XElement Named(string name) => menu.DescendantsAndSelf().Single(tile => (string?)tile.Attribute("name") == name);
        _scrollbar = Named("SWM_Scrollbar"); _hours = Named("SWM_HoursChosen");
        _time = Named("SWM_CurrentTime"); _prompt = Named("SWM_HowManyText");
        _verb = Setting(kind == FalloutRestKind.Sleep ? "sSleep" : "sWait");
        _tiles.Text[_prompt] = $"{Setting("sHowManyWait")} {_verb}?";
        var begin = Named("SWM_WaitButton");
        _tiles.Text[begin] = CultureInfo.InvariantCulture.TextInfo.ToTitleCase(_verb);
        AddButton(begin, () => Begin());
        AddButton(Named("SWM_CancelButton"), Cancel);
        var slider = new Control { Name = "HourSlider", MouseFilter = MouseFilterEnum.Stop };
        slider.GuiInput += input =>
        {
            if (input is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true } button) Slide(button.Position.X);
            else if (input is InputEventMouseMotion { ButtonMask: MouseButtonMask.Left } motion) Slide(motion.Position.X);
        };
        void Slide(float x) => Select(1 + (int)Math.Round(Math.Clamp(x / slider.Size.X, 0, 1) * (FalloutPlayerRest.MaximumHours - 1)));
        AddChild(slider); _targets.Add((_scrollbar, slider));
        SetMeta("opennv_menu_id", (int)FalloutPlayerRest.MenuId);
        SetMeta("opennv_ui_source", "menus/sleep_wait_menu.xml; source-fonts-and-atlas");
        SetMeta("opennv_ui_unverified", "retail-hour-pacing,matched-pixels");
    }

    private string Setting(string name) => FalloutGameSettingStrings.Read(_records, name);

    private void AddButton(XElement tile, Action activate)
    {
        var font = _tiles.Font(tile.DescendantsAndSelf("text").FirstOrDefault() ?? tile);
        var button = new NativeBitmapMenuButton(font.Font, font.Atlas, _tiles.Color)
        { Name = (string)tile.Attribute("name")!, Text = _tiles.String(tile), DrawText = false, FocusMode = FocusModeEnum.All };
        button.Pressed += activate; AddChild(button); _targets.Add((tile, button));
    }

    internal void Select(int value)
    {
        if (_closed || Resting) return;
        Hours = Math.Clamp(value, 1, FalloutPlayerRest.MaximumHours);
        Refresh(Hours);
    }

    private void Refresh(int hours)
    {
        _tiles.Bind(_scrollbar, "_current_value", hours - 1);
        _tiles.Text[_hours] = $"{hours.ToString(CultureInfo.InvariantCulture)} {Setting(hours == 1 ? "sHour" : "sHours")}";
        _tiles.Text[_time] = _currentTime();
        QueueRedraw();
    }

    internal void Begin()
    {
        if (_closed || Resting || Error is not null) return;
        Resting = true; _elapsed = 0;
        foreach (var (_, control) in _targets) control.Visible = false;
        GD.Print($"OPENNV_REST_BEGIN kind={_kind} hours={Hours}");
    }

    internal void Cancel()
    {
        if (_closed) return;
        if (Resting) { Finish(); return; }
        _closed = true; _complete(0);
    }

    private void Finish()
    {
        if (_closed) return;
        _closed = true; Resting = false;
        _complete(RestedHours);
    }

    public override void _Process(double delta)
    {
        if (!Resting || _closed || Error is not null) return;
        _elapsed += delta;
        try
        {
            while (_elapsed >= SecondsPerHour && Resting && !_closed)
            {
                _elapsed -= SecondsPerHour;
                var continuing = _advanceHour();
                RestedHours++;
                Refresh(Math.Max(1, Hours - RestedHours));
                if (!continuing || RestedHours >= Hours) Finish();
            }
        }
        catch (Exception error)
        {
            Error = error.Message; GD.PushError($"OPENNV_SLEEP_WAIT_UI_FAIL {error.Message}");
            Finish();
        }
    }

    public override void _Ready() { Select(Hours); Layout(); }
    public override void _EnterTree() => _viewportLayout = new(this, Layout);
    public override void _ExitTree() => _viewportLayout?.Dispose();

    private void Layout()
    {
        if (!IsInsideTree()) return;
        try
        {
            var scale = GetViewportRect().Size.Y / 960;
            Scale = Vector2.One * scale; Position = Vector2.Zero;
            Size = _tiles.Screen = GetViewportRect().Size / scale; _tiles.ResolutionConverter = 1 / scale;
            foreach (var (tile, control) in _targets)
            {
                control.Position = _tiles.Position(tile);
                control.Size = new(_tiles.Number(tile, "width"), _tiles.Number(tile, "height"));
            }
            _tiles.ValidateDrawing(); QueueRedraw();
        }
        catch (Exception error) { Error = error.Message; GD.PushError($"OPENNV_SLEEP_WAIT_UI_FAIL {error.Message}"); }
    }

    public override void _Input(InputEvent inputEvent)
    {
        if (_closed) return;
        var viewport = GetViewport();
        if (inputEvent is InputEventKey { Pressed: true } key)
        {
            switch (key.PhysicalKeycode)
            {
                case Key.Left or Key.A when !Resting: Select(Hours - 1); break;
                case Key.Right or Key.D when !Resting: Select(Hours + 1); break;
                case Key.Pageup: Select(Hours - 6); break;
                case Key.Pagedown: Select(Hours + 6); break;
                case Key.Enter or Key.KpEnter or Key.W when !key.Echo: Begin(); break;
                case Key.Escape or Key.Tab or Key.E when !key.Echo: Cancel(); break;
                default: break;
            }
            viewport.SetInputAsHandled();
        }
        else if (inputEvent is InputEventMouseButton { Pressed: true } mouse && mouse.ButtonIndex is MouseButton.WheelUp or MouseButton.WheelDown)
        {
            Select(Hours + (mouse.ButtonIndex == MouseButton.WheelUp ? 1 : -1)); viewport.SetInputAsHandled();
        }
    }

    public override void _Draw()
    {
        if (Error is not null) return;
        try { _tiles.Draw(this); }
        catch (Exception error) { Error = error.Message; GD.PushError($"OPENNV_SLEEP_WAIT_UI_FAIL {error.Message}"); }
    }
}
