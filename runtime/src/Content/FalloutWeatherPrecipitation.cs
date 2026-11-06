namespace OpenNV.Runtime.Content;

// The sky's weather change: the weather it moves to, the one it leaves and the
// transition progress from 0 to 1.
internal readonly record struct FalloutWeatherTransition(FalloutFormKey Current, FalloutFormKey? Previous, float Blend);

// IsRaining: rain is visibly falling while a rainy incoming weather has begun
// its precipitation fade-in, or a rainy outgoing weather has not finished its
// fade-out. Snow weathers do not count.
internal static class FalloutWeatherPrecipitation
{
    private const byte RainyClassification = 0x04;

    internal static bool IsRaining(FalloutPluginStack records, FalloutWeatherTransition transition)
    {
        if (!float.IsFinite(transition.Blend) || transition.Blend is < 0 or > 1)
            throw new InvalidDataException("Weather transition progress is outside 0..1.");
        var current = Data(records, transition.Current);
        if (Rainy(current) && transition.Blend > current[6] / 255f * .999f) return true;
        return transition.Previous is { } previous && Data(records, previous) is var outgoing &&
            Rainy(outgoing) && transition.Blend < outgoing[7] / 255f * (1 - .001f) + .001f;
    }

    private static bool Rainy(byte[] data) => (data[11] & RainyClassification) != 0;

    // WTHR DATA: winds and cloud speeds, glare, damage, precipitation fade
    // in (6) and out (7), thunder, classification (11) and lightning colour.
    private static byte[] Data(FalloutPluginStack records, FalloutFormKey weather)
    {
        var record = records.GetEffective(weather);
        if (record.Signature != "WTHR") throw new InvalidDataException("Weather transition does not resolve to WTHR.");
        var data = record.ReadSubrecords().Single(field => field.Signature == "DATA").Data;
        return data.Length == 15 ? data.ToArray() :
            throw new NotSupportedException($"Weather {weather} DATA has unbound size {data.Length}.");
    }
}
