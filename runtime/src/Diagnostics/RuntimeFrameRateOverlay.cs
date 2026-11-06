using Godot;

namespace OpenNV.Runtime.Diagnostics;

// --show-fps: frame rate with GPU against CPU frame time, which tells whether a
// scene is fill- or script-bound. The periodic log line lets a device run be
// read back afterwards.
internal sealed partial class RuntimeFrameRateOverlay : CanvasLayer
{
    private readonly float _renderScale;
    private Label? _label;
    private Rid _viewport;
    private int _ticks;

    internal RuntimeFrameRateOverlay(float renderScale)
    {
        _renderScale = renderScale;
        Layer = 126;
    }

    public override void _Ready()
    {
        _label = new Label { Position = new Vector2(12, 8), MouseFilter = Control.MouseFilterEnum.Ignore };
        _label.AddThemeColorOverride("font_color", new Color(.35f, 1f, .55f));
        AddChild(_label);
        _viewport = GetViewport().GetViewportRid();
        RenderingServer.ViewportSetMeasureRenderTime(_viewport, true);
        var timer = new Godot.Timer { WaitTime = .5, Autostart = true, ProcessMode = ProcessModeEnum.Always };
        timer.Timeout += Sample;
        AddChild(timer);
    }

    private void Sample()
    {
        var gpu = RenderingServer.ViewportGetMeasuredRenderTimeGpu(_viewport);
        var cpu = Godot.Performance.GetMonitor(Godot.Performance.Monitor.TimeProcess) * 1000;
        _label!.Text = $"{Engine.GetFramesPerSecond():0} FPS  gpu {gpu:0.0}ms  cpu {cpu:0.0}ms  {_renderScale:0.00}x";
        if (++_ticks % 10 == 0)
            GD.Print($"OPENNV_FPS fps={Engine.GetFramesPerSecond():0} gpuMs={gpu:0.0} processMs={cpu:0.0} " +
                $"drawCalls={Godot.Performance.GetMonitor(Godot.Performance.Monitor.RenderTotalDrawCallsInFrame):0} " +
                $"primitives={Godot.Performance.GetMonitor(Godot.Performance.Monitor.RenderTotalPrimitivesInFrame):0}");
    }
}
