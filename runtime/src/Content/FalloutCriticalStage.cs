namespace OpenNV.Runtime.Content;

// The CriticalStage script parameter shared by SetCriticalStage and the
// IsInCriticalStage condition. Critical effect scripts drive goo/disintegration.
internal static class FalloutCriticalStage
{
    internal const byte None = 0, GooStart = 1, GooEnd = 2, DisintegrateStart = 3, DisintegrateEnd = 4;

    internal static byte Parse(string identifier) => identifier.ToLowerInvariant() switch
    {
        "none" => None,
        "goostart" => GooStart,
        "gooend" => GooEnd,
        "disintegratestart" => DisintegrateStart,
        "disintegrateend" => DisintegrateEnd,
        _ => throw new InvalidDataException($"Invalid CriticalStage '{identifier}'."),
    };

    internal static byte Require(uint stage) => stage <= DisintegrateEnd ? (byte)stage
        : throw new InvalidDataException($"Critical stage {stage} is outside the source CriticalStage values.");
}
