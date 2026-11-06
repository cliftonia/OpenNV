using OpenNV.Runtime.Content;

// A recorded line must end even when the audio device never reports the clip
// finished. Synthetic ticks stand in for frames; no device is opened.
internal static class RecordedSpeechClockContracts
{
    internal static void Run()
    {
        const double frame = 1.0 / 60.0;
        const double slack = FalloutRecordedSpeechClock.SlackSeconds;

        // The tick right after Play still carries the starting frame's time.
        var hitch = new FalloutRecordedSpeechClock(1.0, 0);
        Require(!hitch.Advance(5.0) && hitch.ElapsedSeconds == 0, "The starting frame's delta counted toward the clip.");

        // Ordinary frames end the line after its length plus slack, not before.
        var clock = new FalloutRecordedSpeechClock(2.0, 0);
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
        var gaps = new FalloutRecordedSpeechClock(1.0, 0);
        gaps.Advance(0);
        Require(!gaps.Advance(30.0) && gaps.ElapsedSeconds == FalloutRecordedSpeechClock.MaximumStepSeconds,
            "A long frame gap cut the line short.");

        // A cold continuation is bounded by what remains of the clip.
        var cold = new FalloutRecordedSpeechClock(3.0, 2.5);
        cold.Advance(0);
        var coldTicks = 0;
        while (!cold.Advance(frame)) Require(++coldTicks < 10_000, "A continued line never expired.");
        Require(cold.ElapsedSeconds >= 0.5 + slack && cold.ElapsedSeconds < 0.5 + slack + frame,
            "A continued line ignored its saved position.");

        // A decoding failure stays a failure: no zero-length or endless line.
        foreach (var length in new[] { 0.0, -1.0, double.NaN, double.PositiveInfinity })
            Require(Throws(() => _ = new FalloutRecordedSpeechClock(length, 0)), $"Decoded length {length} was accepted.");
        Require(Throws(() => _ = new FalloutRecordedSpeechClock(1.0, 1.5)), "A start past the clip was accepted.");
        Require(Throws(() => new FalloutRecordedSpeechClock(1.0, 0).Advance(double.NaN)), "A non-finite tick was accepted.");
        Console.WriteLine($"OPENNV_RECORDED_SPEECH_CLOCK_PASS firstTickSkipped=true bound=length+{slack}s " +
            $"stepCap={FalloutRecordedSpeechClock.MaximumStepSeconds}s coldRemainder=true decodeFailureRefused=true device=none");
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
