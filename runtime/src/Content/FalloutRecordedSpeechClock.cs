namespace OpenNV.Runtime.Content;

// A recorded response lasts as long as its decoded clip. The audio device's
// Finished signal is the ordinary end, but output is not gameplay authority:
// when the device never confirms (stalled mixer, sleeping hardware), the
// clip's own length plus a small slack ends the line and the caller reports
// the unconfirmed completion. The clock starts on the first tick after Play,
// because that tick's delta still belongs to the frame that started the line,
// and caps each step so a hitch or pause gap can only delay the bound.
internal sealed class FalloutRecordedSpeechClock
{
    internal const double SlackSeconds = 0.5;
    internal const double MaximumStepSeconds = 0.25;
    private bool _started;
    internal double DurationSeconds { get; }
    internal double StartSeconds { get; }
    internal double ElapsedSeconds { get; private set; }
    internal double BoundSeconds => DurationSeconds - StartSeconds + SlackSeconds;
    internal bool Expired => ElapsedSeconds >= BoundSeconds;

    internal FalloutRecordedSpeechClock(double durationSeconds, double startSeconds)
    {
        // A clip without a positive finite length is a decoding failure, not
        // an instant or endless line.
        if (!double.IsFinite(durationSeconds) || durationSeconds <= 0)
            throw new InvalidDataException("Recorded speech has no positive finite decoded length.");
        if (!double.IsFinite(startSeconds) || startSeconds < 0 || startSeconds > durationSeconds)
            throw new InvalidDataException("Recorded speech starts outside its decoded length.");
        DurationSeconds = durationSeconds;
        StartSeconds = startSeconds;
    }

    internal bool Advance(double unscaledDelta)
    {
        if (!double.IsFinite(unscaledDelta) || unscaledDelta < 0)
            throw new InvalidDataException("Recorded speech tick must be finite and nonnegative.");
        if (!_started)
        {
            _started = true;
            return false;
        }
        ElapsedSeconds += Math.Min(unscaledDelta, MaximumStepSeconds);
        return Expired;
    }

    internal object State => new
    {
        durationSeconds = DurationSeconds,
        startSeconds = StartSeconds,
        elapsedSeconds = ElapsedSeconds,
        boundSeconds = BoundSeconds,
        owner = "decoded-clip-length-bound"
    };
}
