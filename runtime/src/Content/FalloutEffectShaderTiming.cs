namespace OpenNV.Runtime.Content;

// Per-instance EFSH clocks. One stepped envelope drives particle birth and the
// fill and edge membrane alphas; each owner keeps its own current value.
internal static class FalloutEffectShaderTiming
{
    // Particle slots per batch. Birth ratios at most 1 are fractions of it.
    internal const int ParticleBatchCapacity = 78;
    internal const float MinimumReleaseRate = .33f;

    // Incremental, not closed form: fade-out and release ramp down from the
    // value actually reached. Releasing never cuts into fade-in or full time.
    // The particle owner scales its release step by the batch capacity; the
    // membrane passes 1 (see the caller for that retail-unverified choice).
    internal static float Step(FalloutEffectShaderEnvelope envelope, float current, float delta, double age,
        bool releasing, float releaseScale)
    {
        var span = Math.Abs(envelope.Full - envelope.Persistent);
        if (releasing && age >= envelope.FadeIn + envelope.FullTime)
        {
            var rate = envelope.FadeOut > 0 && envelope.Full != envelope.Persistent ? span / envelope.FadeOut : 0;
            return Math.Max(0, current - releaseScale * delta * Math.Max(MinimumReleaseRate, rate));
        }
        if (age < envelope.FadeIn) return Math.Min(envelope.Full, current + delta / envelope.FadeIn * envelope.Full);
        if (age - envelope.FadeIn < envelope.FullTime) return envelope.Full;
        if (age - envelope.FadeIn - envelope.FullTime < envelope.FadeOut)
            return Math.Max(envelope.Persistent, current - delta / envelope.FadeOut * span);
        return envelope.Persistent;
    }

    // Birth ratios in (0, 1] are fractions of one batch; larger values are
    // already target live-particle counts.
    internal static FalloutEffectShaderEnvelope ParticleBirth(FalloutEffectShaderEnvelope source)
    {
        static float Scale(float value) => value is > 0 and <= 1 ? value * ParticleBatchCapacity : value;
        return source with { Full = Scale(source.Full), Persistent = Scale(source.Persistent) };
    }

    internal static int BatchCount(FalloutEffectShaderEnvelope birth) =>
        (int)Math.Ceiling(Math.Max(0, Math.Max(birth.Full, birth.Persistent)) / ParticleBatchCapacity);

    // A non-positive amplitude leaves the base alpha unchanged. Retail data
    // suggests a doubled base there; that reading is unverified, so the
    // conservative unchanged alpha is kept until a capture settles it.
    internal static float Displayed(float value, FalloutEffectShaderPulse pulse, double age) =>
        Math.Clamp(pulse.Amplitude > 0 ? value * (1 + pulse.Amplitude * MathF.Sin((float)(Math.Tau * pulse.Frequency * age))) : value, 0, 1);

    // Alpha-test reference (0-255) for the holes texture, or null without holes.
    internal static float? HolesThreshold(FalloutEffectShaderHoles holes, double age)
    {
        if (holes is not { StartTime: { } start, EndTime: { } end, StartValue: { } from, EndValue: { } to }) return null;
        var clamped = Math.Clamp(age, Math.Min(start, end), Math.Max(start, end));
        var weight = end > start ? (float)((clamped - start) / (end - start)) : age >= end ? 1 : 0;
        return from + (to - from) * weight;
    }
}

// Add-on (DEBR) fade alpha and scale progress; both are clamped integrators.
internal struct FalloutEffectShaderAddonClock
{
    internal float Alpha, Progress;

    internal void Step(FalloutEffectShaderAddons source, bool releasing, float delta)
    {
        Alpha = Integrate(Alpha, delta, releasing ? -(source.FadeOut ?? 0) : source.FadeIn ?? 0, releasing);
        Progress = Integrate(Progress, delta, releasing ? -(source.ScaleOutTime ?? 0) : source.ScaleInTime ?? 0, releasing);
    }

    internal readonly float Scale(FalloutEffectShaderAddons source, bool releasing)
    {
        var low = Math.Max(.01f, (releasing ? source.ScaleEnd : source.ScaleStart) ?? 0);
        return low + (1 - low) * Progress;
    }

    // A zero duration completes the ramp at once in its current direction.
    private static float Integrate(float value, float delta, float duration, bool releasing) =>
        duration == 0 ? releasing ? 0 : 1 : Math.Clamp(value + delta / duration, 0, 1);
}
