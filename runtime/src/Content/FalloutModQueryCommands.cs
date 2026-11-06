namespace OpenNV.Runtime.Content;

internal static class FalloutModQueryCommands
{
    internal static FalloutScriptFunction? Function(FalloutPluginStack records, string operation) => operation switch
    {
        "ismodloaded" => new([FalloutScriptArgumentKind.String],
            arguments => records.IsPluginLoaded(arguments[0].Text) ? 1 : 0)
        { ReadOnly = true },
        "isdlcinstalled" or "hasdlc" => new([FalloutScriptArgumentKind.String],
            arguments => IsContentInstalled(records, arguments[0].Text) ? 1 : 0)
        { ReadOnly = true },
        _ => null,
    };

    // A mounted content package matches its file name or its display name
    // ("Tribal Pack" is TribalPack.esm), compared case-insensitively.
    internal static bool IsContentInstalled(FalloutPluginStack records, string name)
    {
        static string Key(string value) => new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
        var wanted = Key(name);
        return wanted.Length != 0 && records.Plugins.Any(plugin =>
            Key(plugin.Plugin.Name) == wanted || Key(Path.GetFileNameWithoutExtension(plugin.Plugin.Name)) == wanted);
    }
}
