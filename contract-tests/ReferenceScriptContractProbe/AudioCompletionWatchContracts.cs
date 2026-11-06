using OpenNV.Runtime.Content;

// A finite clip must end even when the audio device never reports Finished,
// without ever cutting a working device short. Synthetic frames and cursors
// stand in for the player; no device is opened.
internal static class AudioCompletionWatchContracts
{
    private const double Frame = 1.0 / 60.0;
    private const double Slack = FalloutAudioCompletionWatch.SlackSeconds;

    internal static void Run()
    {
        // A stalled device from the first buffer ends after the clip plus slack.
        var stalled = new FalloutAudioCompletionWatch();
        Require(stalled.Observe(Play(1, 2.0, 0.005), 5.0) == FalloutAudioCompletionStep.Armed && stalled.FrozenSeconds == 0,
            "A new clip was not armed at its first cursor, or the starting frame's delta counted.");
        var frozen = FramesUntilExpired(stalled, Play(1, 2.0, 0.005));
        Require(frozen >= 2.0 - 0.005 + Slack && frozen < 2.0 - 0.005 + Slack + Frame,
            $"A stalled clip ended after {frozen}s instead of its remaining length plus slack.");
        Require(stalled.Divergence("speech", "info=Fixture.esm:000801", 950).StartsWith(
            "OPENNV_AUDIO_UNCONFIRMED lane=speech info=Fixture.esm:000801 lengthSeconds=2 pitch=1 frozenAtSeconds=0.005 ",
            StringComparison.Ordinal), "Unconfirmed audio divergence lost its identity or evidence.");

        // A working device that hiccups (a Bluetooth route change stalls the
        // mixer for a second near the end) re-anchors and is never cut short.
        var hiccup = new FalloutAudioCompletionWatch();
        hiccup.Observe(Play(2, 3.0, 0), Frame);
        for (var position = Frame; position < 2.8; position += Frame)
            Require(hiccup.Observe(Play(2, 3.0, position), Frame) == FalloutAudioCompletionStep.Waiting, "A moving cursor expired.");
        for (var stall = 0.0; stall < 0.69; stall += Frame)
            Require(hiccup.Observe(Play(2, 3.0, 2.8), Frame) == FalloutAudioCompletionStep.Waiting,
                "A device stall shorter than the remaining clip plus slack cut the clip short.");
        Require(hiccup.Observe(Play(2, 3.0, 2.82), Frame) == FalloutAudioCompletionStep.Waiting && hiccup.FrozenSeconds == 0,
            "Resumed device progress did not re-anchor the remaining time.");

        // Dropped IO cycles slow the cursor on a long clip; any progress keeps it alive.
        var slow = new FalloutAudioCompletionWatch();
        slow.Observe(Play(3, 60.0, 0), Frame);
        var cursor = 0.0;
        for (var frame = 0; frame < 120 * 60; frame++)
        {
            if (frame % 30 == 0) cursor += 0.25;
            Require(slow.Observe(Play(3, 60.0, cursor), Frame) == FalloutAudioCompletionStep.Waiting, "A slow but moving clip expired.");
        }

        // A lost Finished at the end of the clip settles after the slack alone.
        var lost = new FalloutAudioCompletionWatch();
        lost.Observe(Play(4, 1.0, 1.0), Frame);
        var afterEnd = FramesUntilExpired(lost, Play(4, 1.0, 1.0));
        Require(afterEnd >= Slack && afterEnd < Slack + Frame, "A clip whose cursor reached its end did not settle after the slack.");

        // Pitch scales the remaining real time; a backwards cursor (seek) re-anchors.
        var pitched = new FalloutAudioCompletionWatch();
        pitched.Observe(Play(5, 2.0, 0, pitch: 2), Frame);
        Require(Math.Abs(pitched.RemainingSeconds - 1.0) < 1e-9, "Pitch did not scale the remaining clip time.");
        pitched.Observe(Play(5, 2.0, 1.5, pitch: 2), Frame);
        pitched.Observe(Play(5, 2.0, 0.5, pitch: 2), Frame);
        Require(pitched.AnchorSeconds == 0.5 && pitched.FrozenSeconds == 0, "A backwards cursor did not re-anchor.");

        // Pause time and frame gaps never count toward the bound beyond one capped step.
        var paused = new FalloutAudioCompletionWatch();
        paused.Observe(Play(6, 1.0, 0.2), Frame);
        for (var frame = 0; frame < 600; frame++)
            Require(paused.Observe(Play(6, 1.0, 0.2) with { Paused = true }, Frame) == FalloutAudioCompletionStep.Waiting,
                "A paused clip expired.");
        Require(paused.FrozenSeconds == 0, "Pause time counted toward the bound.");
        paused.Observe(Play(6, 1.0, 0.2), 30.0);
        Require(paused.FrozenSeconds == FalloutAudioCompletionWatch.MaximumStepSeconds, "A long frame gap was not capped.");

        // A new stream on the same player is a new clip; stopping ends the watch.
        var reused = new FalloutAudioCompletionWatch();
        reused.Observe(Play(7, 1.0, 0.4), Frame);
        Require(reused.Observe(Play(8, 4.0, 0), Frame) == FalloutAudioCompletionStep.Armed && reused.LengthSeconds == 4.0,
            "A replaced stream was not re-armed as a new clip.");
        Require(reused.Observe(Play(8, 4.0, 0) with { Playing = false }, Frame) == FalloutAudioCompletionStep.Ended &&
            reused.Observe(Play(8, 4.0, 0) with { Playing = false }, Frame) == FalloutAudioCompletionStep.Idle,
            "Stopping did not end the watch exactly once.");
        Require(reused.Observe(Play(8, 4.0, 0), Frame) == FalloutAudioCompletionStep.Armed,
            "Replaying the same stream after it ended was not a new clip.");
        reused.Reset();
        Require(reused.Observe(Play(8, 4.0, 1.0), Frame) == FalloutAudioCompletionStep.Armed, "Reset did not re-arm the next clip.");

        // Looping streams have no finite end; undecodable clips are refused once and never fabricated.
        var loop = new FalloutAudioCompletionWatch();
        Require(loop.Observe(Play(9, 1.0, 0) with { Finite = false }, Frame) == FalloutAudioCompletionStep.Unbounded,
            "A looping stream was bounded.");
        foreach (var length in new[] { 0.0, -1.0, double.NaN, double.PositiveInfinity })
        {
            var refused = new FalloutAudioCompletionWatch();
            Require(refused.Observe(Play(10, length, 0), Frame) == FalloutAudioCompletionStep.Refused &&
                Enumerable.Range(0, 1000).All(_ => refused.Observe(Play(10, length, 0), Frame) == FalloutAudioCompletionStep.Waiting),
                $"Decoded length {length} was given an end.");
        }
        Require(Throws(() => new FalloutAudioCompletionWatch().Observe(Play(11, 1.0, 0), double.NaN)) &&
            Throws(() => { var watch = new FalloutAudioCompletionWatch(); watch.Observe(Play(12, 1.0, 0), Frame); watch.Observe(Play(12, 1.0, double.NaN), Frame); }),
            "A non-finite tick or cursor was accepted.");
        Console.WriteLine($"OPENNV_AUDIO_COMPLETION_WATCH_PASS stalledBound=remaining+{Slack}s hiccupReanchored=true slowCursorAlive=true " +
            $"lostFinishedSettled=true pitch=true seekReanchored=true pauseExcluded=true stepCap={FalloutAudioCompletionWatch.MaximumStepSeconds}s " +
            "newClip=true loopUnbounded=true decodeFailureRefused=true divergenceLine=true device=none");
    }

    private static FalloutAudioObservation Play(ulong stream, double length, double position, double pitch = 1) =>
        new(true, false, true, stream, length, pitch, position);

    private static double FramesUntilExpired(FalloutAudioCompletionWatch watch, FalloutAudioObservation observation)
    {
        for (var frame = 0; frame < 100_000; frame++)
            if (watch.Observe(observation, Frame) == FalloutAudioCompletionStep.Expired) return watch.FrozenSeconds;
        throw new InvalidDataException("A frozen clip never expired.");
    }

    private static bool Throws(Action action)
    {
        try { action(); }
        catch (InvalidDataException) { return true; }
        return false;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
