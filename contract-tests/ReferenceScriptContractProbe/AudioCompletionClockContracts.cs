using OpenNV.Runtime.Content;

// A recorded line must end even when the audio device never reports the clip
// finished. Synthetic ticks stand in for frames; no device is opened.
internal static class AudioCompletionClockContracts
{
    internal static void Run()
    {
        const double frame = 1.0 / 60.0;
        const double slack = FalloutAudioCompletionClock.SlackSeconds;

        // The tick right after Play still carries the starting frame's time.
        var hitch = new FalloutAudioCompletionClock(1.0, 0);
        Require(!hitch.Advance(5.0) && hitch.ElapsedSeconds == 0, "The starting frame's delta counted toward the clip.");

        // Ordinary frames end the line after its length plus slack, not before.
        var clock = new FalloutAudioCompletionClock(2.0, 0);
        var ticks = 0;
        clock.Advance(frame);
        while (!clock.Advance(frame))
        {
            Require(++ticks < 10_000, "A silent device kept the line open forever.");
            Require(clock.ElapsedSeconds < 2.0 + slack, "The clock passed its bound without expiring.");
        }
        Require(clock.ElapsedSeconds >= 2.0 + slack && clock.ElapsedSeconds < 2.0 + slack + frame,
            $"The line ended at {clock.ElapsedSeconds} instead of its length plus slack.");

        // A hitch or a paused gap can delay the bound but never shorten the clip.
        var gaps = new FalloutAudioCompletionClock(1.0, 0);
        gaps.Advance(0);
        Require(!gaps.Advance(30.0) && gaps.ElapsedSeconds == FalloutAudioCompletionClock.MaximumStepSeconds,
            "A long frame gap cut the line short.");

        // A cold continuation is bounded by what remains of the clip.
        var cold = new FalloutAudioCompletionClock(3.0, 2.5);
        cold.Advance(0);
        var coldTicks = 0;
        while (!cold.Advance(frame)) Require(++coldTicks < 10_000, "A continued line never expired.");
        Require(cold.ElapsedSeconds >= 0.5 + slack && cold.ElapsedSeconds < 0.5 + slack + frame,
            "A continued line ignored its saved position.");

        // Every owner reports the same visible, culture-invariant divergence line.
        var line = cold.Divergence("speech", "info=Fixture.esm:000801", 0.005, 12.5);
        Require(line.StartsWith("OPENNV_AUDIO_UNCONFIRMED lane=speech info=Fixture.esm:000801 durationSeconds=3 startSeconds=2.5 ",
            StringComparison.Ordinal) && line.Contains("sinceLastMixSeconds=12.5 owner=clip-length-bound", StringComparison.Ordinal),
            $"Unconfirmed audio divergence lost its identity or evidence: {line}");

        // A decoding failure stays a failure: no zero-length or endless line.
        foreach (var length in new[] { 0.0, -1.0, double.NaN, double.PositiveInfinity })
            Require(Throws(() => _ = new FalloutAudioCompletionClock(length, 0)), $"Decoded length {length} was accepted.");
        Require(Throws(() => _ = new FalloutAudioCompletionClock(1.0, 1.5)), "A start past the clip was accepted.");
        Require(Throws(() => new FalloutAudioCompletionClock(1.0, 0).Advance(double.NaN)), "A non-finite tick was accepted.");
        Console.WriteLine($"OPENNV_AUDIO_COMPLETION_CLOCK_PASS firstTickSkipped=true bound=length+{slack}s " +
            $"stepCap={FalloutAudioCompletionClock.MaximumStepSeconds}s coldRemainder=true divergenceLine=true decodeFailureRefused=true device=none");
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
