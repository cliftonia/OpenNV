namespace OpenNV.Runtime.Content;

// Interval is present only when SetQuestDelay replaced the source interval.
internal sealed record FalloutQuestScriptClockSnapshot(float Remaining, float Elapsed, long Invocations, float? Interval = null)
{
    internal bool HasSameBits(FalloutQuestScriptClockSnapshot other) =>
        BitConverter.SingleToInt32Bits(Remaining) == BitConverter.SingleToInt32Bits(other.Remaining) &&
        BitConverter.SingleToInt32Bits(Elapsed) == BitConverter.SingleToInt32Bits(other.Elapsed) &&
        Invocations == other.Invocations && Interval == other.Interval;

    internal void Validate()
    {
        if (!float.IsFinite(Remaining) || !float.IsFinite(Elapsed) || Elapsed < 0 || Invocations < 0 ||
            Interval is { } interval && (!float.IsFinite(interval) || interval <= 0))
            throw new InvalidDataException("Saved quest script clock is invalid.");
    }
}

// Recurrence is separate from initial script linking and block admission.
// This owner receives its initial phase; it does not infer an initialization
// counter from a selected subset of scripts or from a reference game's clock.
internal sealed class FalloutQuestScriptClock
{
    private readonly float _defaultDelay, _sourceInterval;
    internal float Interval { get; private set; }
    internal float Remaining { get; private set; }
    internal float Elapsed { get; private set; }
    internal long Invocations { get; private set; }

    internal FalloutQuestScriptClock(float defaultDelay, float? authoredDelay, float initialPhase)
    {
        if (!float.IsFinite(defaultDelay) || authoredDelay is { } delay && !float.IsFinite(delay) ||
            !float.IsFinite(initialPhase) || initialPhase < 0)
            throw new InvalidDataException("Quest script clock input is invalid.");
        _defaultDelay = defaultDelay;
        Interval = _sourceInterval = defaultDelay <= 0 ? 0 : authoredDelay is > 0 ? authoredDelay.Value : defaultDelay;
        Remaining = initialPhase;
    }

    internal bool Advance(float seconds)
    {
        if (!float.IsFinite(seconds) || seconds < 0) throw new ArgumentOutOfRangeException(nameof(seconds));
        // Initial linking and globally disabled recurrence retain their zero
        // elapsed dispatch. Later overdue calls still receive each new frame's
        // time: countdown debt and GetSecondsPassed are independent owners.
        if (Remaining <= 0 && (Invocations == 0 || _defaultDelay <= 0)) return true;
        var remaining = Remaining > 0 ? (float)((double)Remaining - seconds) : Remaining;
        var elapsed = (float)((double)Elapsed + seconds);
        if (!float.IsFinite(remaining) || !float.IsFinite(elapsed))
            throw new InvalidDataException("Quest script clock exceeds Float32 storage.");
        Remaining = remaining;
        Elapsed = elapsed;
        return Remaining <= 0;
    }

    internal void CompleteInvocation()
    {
        if (Remaining > 0) throw new InvalidOperationException("Quest script is not due.");
        var invocations = checked(Invocations + 1);
        if (_defaultDelay > 0)
        {
            // Retain a late frame's overshoot. Do not drift by restarting a
            // whole interval, or execute an artificial catch-up loop this frame.
            Remaining = (float)((double)Remaining + Interval);
            Elapsed = 0;
        }
        Invocations = invocations;
    }

    // SetQuestDelay replaces the recurrence interval; zero or less restores the
    // installation default. A pending countdown never exceeds the new interval.
    internal void SetInterval(float seconds)
    {
        if (!float.IsFinite(seconds)) throw new InvalidDataException("Quest delay is not finite.");
        if (_defaultDelay <= 0) return;
        Interval = seconds > 0 ? seconds : _defaultDelay;
        if (Remaining > Interval) Remaining = Interval;
    }

    internal FalloutQuestScriptClockSnapshot Capture() =>
        new(Remaining, Elapsed, Invocations, Interval == _sourceInterval ? null : Interval);

    internal void Validate(FalloutQuestScriptClockSnapshot snapshot)
    {
        snapshot.Validate();
        if (snapshot.Invocations > 0 && snapshot.Remaining > (snapshot.Interval ?? Interval))
            throw new InvalidDataException("Saved recurrence exceeds its source interval.");
    }

    internal void Restore(FalloutQuestScriptClockSnapshot snapshot)
    {
        Validate(snapshot);
        if (snapshot.Interval is { } interval) Interval = interval;
        Remaining = snapshot.Remaining;
        Elapsed = snapshot.Elapsed;
        Invocations = snapshot.Invocations;
    }
}
