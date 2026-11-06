using System.Globalization;

namespace OpenNV.Runtime.Content;

// A finite clip lasts as long as its decoded length. The audio device's
// Finished signal is the ordinary end, but output is not gameplay authority:
// when the device never confirms (stalled mixer, sleeping hardware, an
// interrupted mobile audio session), the clip's own length plus a small slack
// ends it and the owner reports the unconfirmed completion. The clock starts
// on the first tick after playback starts, because that tick's delta still
// belongs to the frame that started the clip, and caps each step so a hitch or
// pause gap can only delay the bound.
internal sealed class FalloutAudioCompletionClock
{
    internal const double SlackSeconds = 0.5;
    internal const double MaximumStepSeconds = 0.25;
    private bool _started;
    internal double DurationSeconds { get; }
    internal double StartSeconds { get; }
    internal double ElapsedSeconds { get; private set; }
    internal double BoundSeconds => DurationSeconds - StartSeconds + SlackSeconds;
    internal bool Expired => ElapsedSeconds >= BoundSeconds;

    internal FalloutAudioCompletionClock(double durationSeconds, double startSeconds)
    {
        // A clip without a positive finite length is a decoding failure, not
        // an instant or endless clip.
        if (!double.IsFinite(durationSeconds) || durationSeconds <= 0)
            throw new InvalidDataException("Finite audio has no positive finite decoded length.");
        if (!double.IsFinite(startSeconds) || startSeconds < 0 || startSeconds > durationSeconds)
            throw new InvalidDataException("Finite audio starts outside its decoded length.");
        DurationSeconds = durationSeconds;
        StartSeconds = startSeconds;
    }

    internal bool Advance(double unscaledDelta)
    {
        if (!double.IsFinite(unscaledDelta) || unscaledDelta < 0)
            throw new InvalidDataException("Finite audio tick must be finite and nonnegative.");
        if (!_started)
        {
            _started = true;
            return false;
        }
        ElapsedSeconds += Math.Min(unscaledDelta, MaximumStepSeconds);
        return Expired;
    }

    // One divergence line for every owner whose device never confirmed a clip.
    internal string Divergence(string lane, string identity, double playbackSeconds, double sinceLastMixSeconds) =>
        string.Create(CultureInfo.InvariantCulture,
            $"OPENNV_AUDIO_UNCONFIRMED lane={lane} {identity} durationSeconds={DurationSeconds:0.###} " +
            $"startSeconds={StartSeconds:0.###} elapsedSeconds={ElapsedSeconds:0.###} playbackSeconds={playbackSeconds:0.###} " +
            $"sinceLastMixSeconds={sinceLastMixSeconds:0.###} owner=clip-length-bound parity=unmeasured");

    internal object State => new
    {
        durationSeconds = DurationSeconds,
        startSeconds = StartSeconds,
        elapsedSeconds = ElapsedSeconds,
        boundSeconds = BoundSeconds,
        owner = "decoded-clip-length-bound"
    };
}
