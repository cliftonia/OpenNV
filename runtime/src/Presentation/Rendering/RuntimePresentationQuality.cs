using System.Text.RegularExpressions;
using Godot;

namespace OpenNV.Runtime.Presentation.Rendering;

internal enum RuntimePresentationQualityKind
{
    High,
    Medium,
    Low,
}

// Selects one of the configured renderer quality profiles. --quality wins;
// otherwise mobile is low and a desktop that looks low-power is medium.
internal sealed partial record RuntimePresentationQuality(RuntimePresentationQualityKind Kind, string Source, RendererQualityProfile Profile)
{
    internal static RuntimePresentationQuality Resolve(IReadOnlyDictionary<string, string> options, RendererQualityProfiles profiles)
    {
        var (kind, source) = options.TryGetValue("quality", out var requested)
            ? (Parse(requested), "option")
            : Detect();
        var profile = kind switch
        {
            RuntimePresentationQualityKind.High => profiles.High,
            RuntimePresentationQualityKind.Medium => profiles.Medium,
            RuntimePresentationQualityKind.Low => profiles.Low,
            _ => throw new ArgumentOutOfRangeException(nameof(options)),
        };
        return new(kind, source, profile);
    }

    private static RuntimePresentationQualityKind Parse(string value) => value.ToLowerInvariant() switch
    {
        "high" => RuntimePresentationQualityKind.High,
        "medium" => RuntimePresentationQualityKind.Medium,
        "low" => RuntimePresentationQualityKind.Low,
        _ => throw new ArgumentException($"Unknown --quality {value}; use high, medium or low."),
    };

    // Conservative: only a base Apple M-series chip (MacBook Air, base
    // MacBook Pro/iMac/mini: no Pro/Max/Ultra GPU) or a non-Apple integrated
    // GPU is treated as low-power. Apple Silicon always reports unified-memory
    // GPUs as integrated, so its adapter type cannot separate those tiers.
    private static (RuntimePresentationQualityKind Kind, string Source) Detect()
    {
        if (OperatingSystem.IsIOS() || OperatingSystem.IsAndroid())
            return (RuntimePresentationQualityKind.Low, "auto-mobile");
        var processor = OS.GetProcessorName();
        if (processor.StartsWith("Apple", StringComparison.Ordinal))
            return BaseAppleChip().IsMatch(processor)
                ? (RuntimePresentationQualityKind.Medium, "auto-base-apple-silicon")
                : (RuntimePresentationQualityKind.High, "auto-desktop");
        return RenderingServer.GetVideoAdapterType() == RenderingDevice.DeviceType.IntegratedGpu
            ? (RuntimePresentationQualityKind.Medium, "auto-integrated-gpu")
            : (RuntimePresentationQualityKind.High, "auto-desktop");
    }

    [GeneratedRegex(@"^Apple M\d+$", RegexOptions.CultureInvariant)]
    private static partial Regex BaseAppleChip();
}
