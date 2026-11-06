namespace OpenNV.Runtime.Formats.Gamebryo;

// One sequence's emitter-controller curves: birth rate, emitter activity and
// the clock range it loops over. Every sequence binds its own instance.
internal sealed class FalloutNifParticleEmitterCurves
{
    // Birth rate at a controller time, and the first birth-rate key time.
    internal Func<float, float> Rate { get; set; } = _ => 0;
    internal float RateBegin { get; set; }
    internal bool RateKeyed { get; set; }
    // Emitter-active samples and its key times; null keys mean constant.
    internal Func<float, bool> Active { get; set; } = _ => true;
    internal float[]? ActiveKeys { get; set; }
    internal required double LoopStart { get; init; }
    internal required double LoopStop { get; init; }
    internal required bool Loops { get; init; }
}

// NiPSysEmitterCtlr emission. Counts are floors measured from the start of the
// active window, so there is no fractional accumulator; ages are the times
// since each particle's exact birth, measured at the controller's `now`.
internal sealed class FalloutNifParticleEmitterClock
{
    internal const int MaximumPerInterval = 15;
    internal const int MaximumWindows = 20;
    // Ages one Advance can produce: every window, plus a loop tail.
    internal const int MaximumPerAdvance = MaximumPerInterval * (MaximumWindows + 1) * 2;
    // Short-lived emitters must still emit across slow frames; the backlog
    // discard never triggers below this gap.
    internal const float MinimumBacklogSeconds = .5f;
    private double _stored = double.NaN;
    private FalloutNifParticleEmitterCurves? _curves;

    internal double? Now { get; private set; }
    internal double Stored => _stored;
    internal FalloutNifParticleEmitterCurves? Curves => _curves;

    internal void Reset() { _stored = double.NaN; Now = null; _curves = null; }

    // A controller channel sampled its sequence at `time`. The first sample
    // (or one from a newly active sequence) primes the stored time, so an
    // authored burst at the first key still emits on the next update.
    internal void Apply(FalloutNifParticleEmitterCurves curves, double time)
    {
        if (!double.IsFinite(time)) throw new InvalidDataException("Particle emitter controller time is not finite.");
        if (!ReferenceEquals(curves, _curves)) { _curves = curves; _stored = time; }
        else if (double.IsNaN(_stored)) _stored = time;
        Now = time;
    }

    // Emit(rate, last, current, start, stop): births at whole multiples of
    // 1/rate after `start`, inside (last, min(current, stop)], at most 15.
    internal static int Interval(float rate, double last, double current, double start, double stop, Span<float> ages)
    {
        if (!float.IsFinite(rate)) throw new InvalidDataException("Particle emitter birth rate is not finite.");
        if (!(rate > 0) || start >= stop || start >= current || last >= stop) return 0;
        var duration = current <= stop ? current - start : stop - start;
        var first = (long)((start <= last ? last - start : 0) * rate);
        var count = (int)Math.Min(MaximumPerInterval, (long)(duration * rate) - first);
        for (var k = 1; k <= count; k++) ages[k - 1] = (float)(duration - (first + k) / (double)rate);
        return Math.Max(0, count);
    }

    // Consumes the pending controller time. A looping (persistent) emitter
    // whose gap exceeds max(backlogLimit, MinimumBacklogSeconds) discards
    // that backlog; one-shot emitters keep their bursts, bounded by the caps.
    internal int Advance(float backlogLimit, Span<float> ages)
    {
        if (Now is not { } now || _curves is not { } curves) return 0;
        Now = null;
        if (curves.Loops && now >= _stored && now - _stored > Math.Max(backlogLimit, MinimumBacklogSeconds)) { _stored = now; return 0; }
        int count;
        if (now >= _stored) count = Walk(curves, _stored, now, 0, ages);
        else
        {
            // The controller looped: finish the old cycle, then start the new one.
            // A constant rate and constant activity restart from the loop start.
            count = curves.ActiveKeys is null && !curves.RateKeyed ? 0 : Walk(curves, _stored, curves.LoopStop, now - curves.LoopStart, ages);
            count += Walk(curves, curves.LoopStart, now, 0, ages[count..]);
        }
        _stored = now;
        return count;
    }

    private static int Walk(FalloutNifParticleEmitterCurves curves, double from, double to, double extra, Span<float> ages)
    {
        if (to <= from) return 0;
        if (curves.ActiveKeys is null)
        {
            if (!curves.Active((float)from)) return 0;
            var constant = Interval(curves.Rate((float)(from + .5 * (to - from))), from, to, curves.RateBegin, double.PositiveInfinity, ages);
            for (var index = 0; index < constant; index++) ages[index] += (float)extra;
            return constant;
        }
        var count = 0;
        var time = from;
        for (var window = 0; window < MaximumWindows; window++)
        {
            double start;
            if (curves.Active((float)time)) start = WindowStart(curves, time);
            else if (FirstActiveKey(curves, time, to) is { } opened) start = opened;
            else break;
            var stop = WindowEnd(curves, start);
            var rate = curves.Rate((float)(stop < to ? start + .5 * (stop - start) : to));
            var emitted = Interval(rate, time, to, start, stop, ages[count..]);
            // Ages from a closed window were measured at its end.
            var later = extra + (stop < to ? to - stop : 0);
            for (var index = count; index < count + emitted; index++) ages[index] += (float)later;
            count += emitted;
            if (stop >= to) break;
            time = stop;
        }
        return count;
    }

    // The latest false-to-true key at or before `time` (or the first key).
    private static double WindowStart(FalloutNifParticleEmitterCurves curves, double time)
    {
        var keys = curves.ActiveKeys!;
        var start = keys[0];
        for (var index = 0; index < keys.Length && keys[index] <= time; index++)
            if (curves.Active(keys[index]) && (index == 0 || !curves.Active(keys[index - 1]))) start = keys[index];
        return start;
    }

    // The first false key after `start`; an unclosed window stays open.
    private static double WindowEnd(FalloutNifParticleEmitterCurves curves, double start)
    {
        foreach (var key in curves.ActiveKeys!)
            if (key > start && !curves.Active(key)) return key;
        return double.PositiveInfinity;
    }

    private static double? FirstActiveKey(FalloutNifParticleEmitterCurves curves, double after, double until)
    {
        foreach (var key in curves.ActiveKeys!)
            if (key > after && key <= until && curves.Active(key)) return key;
        return null;
    }
}

// System update order: emitters are driven by their controllers before any
// modifier; the rest run in ascending Order, ties keeping file order.
internal static class FalloutNifParticleUpdate
{
    internal static (FalloutNifParticleModifier[] Emitters, FalloutNifParticleModifier[] Modifiers) Schedule(
        IEnumerable<FalloutNifParticleModifier> source)
    {
        var ordered = source.OrderBy(modifier => modifier.Order).ToArray();
        return (ordered.Where(IsEmitter).ToArray(), ordered.Where(modifier => !IsEmitter(modifier)).ToArray());
    }

    internal static bool IsEmitter(FalloutNifParticleModifier modifier) =>
        modifier is FalloutNifParticleVolumeEmitter or FalloutNifParticleMeshEmitter;
}

// Emitter-side source quirks shared by every emitter type.
internal static class FalloutNifParticleEmission
{
    private const int TableSize = 512;
    private static readonly float[] SineTable = Enumerable.Range(0, TableSize).Select(index => MathF.Sin(index * MathF.Tau / TableSize)).ToArray();

    // Declination and planar angles use 512-entry tables over a full turn.
    internal static float Sin(float angle) => SineTable[(int)(angle * (TableSize / MathF.Tau)) & (TableSize - 1)];
    internal static float Cos(float angle) => Sin(angle + MathF.PI / 2);

    // Emitter-frame direction from declination and planar angles (Z is the axis).
    internal static System.Numerics.Vector3 Direction(float declination, float planar) =>
        new(Cos(planar) * Sin(declination), Sin(planar) * Sin(declination), Cos(declination));
}
