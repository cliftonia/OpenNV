using System.Globalization;

namespace OpenNV.Runtime.Content;

internal enum FalloutAudioCompletionStep { Idle, Ended, Unbounded, Armed, Waiting, Refused, Expired }

// One frame's view of a player: whether it plays, whether its stream has a
// finite end, which stream instance it plays and where its cursor is.
internal readonly record struct FalloutAudioObservation(bool Playing, bool Paused, bool Finite, ulong Stream,
    double LengthSeconds, double Pitch, double PositionSeconds);

// A finite clip ends when the device reports Finished. Output is not gameplay
// authority, so a clip whose cursor stops moving cannot hold its owners
// forever: the watch anchors on the last observed cursor progress and expires
// only after the cursor has been frozen for the remaining clip time (at the
// playback pitch) plus a small slack. A device that merely hiccups (a Bluetooth
// route change, dropped IO cycles on a loaded device) moves the cursor again
// and re-anchors, so a working device is never cut short. Pause time, frame
// hitches and gaps are capped per step and can only delay expiry.
internal sealed class FalloutAudioCompletionWatch
{
    internal const double SlackSeconds = 0.5;
    internal const double MaximumStepSeconds = 0.25;
    private ulong _stream, _refused;
    internal double LengthSeconds { get; private set; }
    internal double Pitch { get; private set; }
    internal double AnchorSeconds { get; private set; }
    internal double FrozenSeconds { get; private set; }
    internal double RemainingSeconds => (LengthSeconds - AnchorSeconds) / Pitch;
    internal double BoundSeconds => RemainingSeconds + SlackSeconds;
    internal bool Armed => _stream != 0;

    internal FalloutAudioCompletionStep Observe(in FalloutAudioObservation observation, double unscaledDelta)
    {
        if (!double.IsFinite(unscaledDelta) || unscaledDelta < 0)
            throw new InvalidDataException("Finite audio tick must be finite and nonnegative.");
        if (!observation.Playing || observation.Stream == 0)
        {
            var ended = Armed;
            _stream = 0;
            return ended ? FalloutAudioCompletionStep.Ended : FalloutAudioCompletionStep.Idle;
        }
        if (!observation.Finite) { _stream = 0; return FalloutAudioCompletionStep.Unbounded; }
        if (observation.Stream == _refused) return FalloutAudioCompletionStep.Waiting;
        if (observation.Stream != _stream)
        {
            // A clip without a positive finite length or pitch is a decoding
            // failure: it gets no fabricated end and is reported once.
            if (!double.IsFinite(observation.LengthSeconds) || observation.LengthSeconds <= 0 ||
                !double.IsFinite(observation.Pitch) || observation.Pitch <= 0)
            {
                _stream = 0; _refused = observation.Stream;
                return FalloutAudioCompletionStep.Refused;
            }
            _stream = observation.Stream; LengthSeconds = observation.LengthSeconds; Pitch = observation.Pitch;
            Anchor(observation.PositionSeconds);
            return FalloutAudioCompletionStep.Armed;
        }
        if (observation.Paused) return FalloutAudioCompletionStep.Waiting;
        var position = Cursor(observation.PositionSeconds);
        // Any cursor movement, forwards or backwards (a seek or restart), is
        // device progress and re-anchors the remaining time.
        if (position != AnchorSeconds) { Anchor(position); return FalloutAudioCompletionStep.Waiting; }
        FrozenSeconds += Math.Min(unscaledDelta, MaximumStepSeconds);
        if (FrozenSeconds < BoundSeconds) return FalloutAudioCompletionStep.Waiting;
        _stream = 0;
        return FalloutAudioCompletionStep.Expired;
    }

    // The owner settled or restarted the player: the next playback is a new clip.
    internal void Reset() => _stream = 0;

    private void Anchor(double position) { AnchorSeconds = Cursor(position); FrozenSeconds = 0; }

    private double Cursor(double position) => double.IsFinite(position) && position >= 0
        ? Math.Min(position, LengthSeconds)
        : throw new InvalidDataException("Finite audio cursor must be finite and nonnegative.");

    // One divergence line for every owner whose device never confirmed a clip.
    internal string Divergence(string lane, string identity, double sinceLastMixSeconds) =>
        string.Create(CultureInfo.InvariantCulture,
            $"OPENNV_AUDIO_UNCONFIRMED lane={lane} {identity} lengthSeconds={LengthSeconds:0.###} pitch={Pitch:0.###} " +
            $"frozenAtSeconds={AnchorSeconds:0.###} frozenSeconds={FrozenSeconds:0.###} " +
            $"sinceLastMixSeconds={sinceLastMixSeconds:0.###} owner=clip-length-bound parity=unmeasured");

    internal object State => new
    {
        armed = Armed,
        lengthSeconds = LengthSeconds,
        pitch = Pitch,
        anchorSeconds = AnchorSeconds,
        frozenSeconds = FrozenSeconds,
        boundSeconds = BoundSeconds,
        owner = "frozen-cursor-remaining-clip-bound"
    };
}
