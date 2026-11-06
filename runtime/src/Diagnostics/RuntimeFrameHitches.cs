using System.Diagnostics;
using Godot;

namespace OpenNV.Runtime.Diagnostics;

// Opt-in with OPENNV_FRAME_SPIKE_MS: whole-frame hitches (render included),
// joined to the renderer's pipeline compilations in the same frame. Draw and
// specialization compilations stall the frame; mesh/surface ones are the
// background precompiles Godot starts when an instance enters the scene.
internal partial class RuntimeFrameHitches : Node
{
    private static readonly RenderingServer.RenderingInfo[] Sources =
    [
        RenderingServer.RenderingInfo.PipelineCompilationsDraw,
        RenderingServer.RenderingInfo.PipelineCompilationsSpecialization,
        RenderingServer.RenderingInfo.PipelineCompilationsSurface,
        RenderingServer.RenderingInfo.PipelineCompilationsMesh,
        RenderingServer.RenderingInfo.PipelineCompilationsCanvas,
    ];
    private readonly ulong[] _previous = new ulong[Sources.Length];
    private readonly ulong[] _total = new ulong[Sources.Length];
    private long _threshold, _last;
    private ulong _hitches;

    // Shares the owner spike threshold so one variable enables both logs.
    internal static RuntimeFrameHitches? FromEnvironment() => RuntimeFrameWork.SpikeTicks == long.MaxValue
        ? null
        : new RuntimeFrameHitches { Name = "FrameHitches", ProcessMode = ProcessModeEnum.Always, _threshold = RuntimeFrameWork.SpikeTicks };

    public override void _Process(double delta)
    {
        var now = Stopwatch.GetTimestamp();
        var elapsed = _last == 0 ? 0 : now - _last;
        _last = now;
        var changed = false;
        Span<ulong> step = stackalloc ulong[Sources.Length];
        for (var index = 0; index < Sources.Length; index++)
        {
            var value = RenderingServer.GetRenderingInfo(Sources[index]);
            step[index] = value >= _previous[index] ? value - _previous[index] : value;
            _previous[index] = value; _total[index] += step[index];
            changed |= step[index] != 0;
        }
        if (elapsed < _threshold) return;
        ++_hitches;
        GD.Print($"OPENNV_FRAME_HITCH ms={elapsed * 1000.0 / Stopwatch.Frequency:F1} frame={Engine.GetFramesDrawn()} " +
            $"pipelinesDraw={step[0]} specialization={step[1]} surface={step[2]} mesh={step[3]} canvas={step[4]} " +
            $"totalDraw={_total[0]} totalSpecialization={_total[1]} hitches={_hitches} compiled={changed}");
    }
}
