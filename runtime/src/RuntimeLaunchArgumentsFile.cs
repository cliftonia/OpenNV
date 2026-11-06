namespace OpenNV.Runtime;

/// <summary>
/// Platforms without a command line (iOS) read the same launch arguments, one
/// per line, from Documents/OpenNV/launch-args.txt. {documents} expands to the
/// sandboxed Documents folder, whose absolute path changes per install.
/// </summary>
internal static class RuntimeLaunchArgumentsFile
{
    internal static string PathIn(string documents) => Path.Combine(documents, "OpenNV", "launch-args.txt");

    // Blank lines and # comments are ignored. An option line may also carry
    // its value ("--render-scale 0.6").
    internal static string[] Parse(IEnumerable<string> lines, string documents) => lines
        .Select(line => line.Trim())
        .Where(line => line.Length != 0 && !line.StartsWith('#'))
        .SelectMany(line => line.StartsWith("--", StringComparison.Ordinal) && line.IndexOfAny([' ', '\t']) is > 0 and var split
            ? new[] { line[..split], line[(split + 1)..].Trim() } : [line])
        .Select(line => line.Replace("{documents}", documents, StringComparison.Ordinal))
        .ToArray();
}
