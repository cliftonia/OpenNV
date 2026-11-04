namespace OpenNV.Runtime.Content;

internal static partial class FalloutExecutableStringTable
{
    // Declarations only: names, types and optional flags of the selected
    // executable's casino command parameters.
    internal static IReadOnlyList<FalloutExecutableCommandParameter> ReadCasinoCommandParameters(string path, string command)
    {
        var (_, image) = Load(path);
        return image.CommandParameters(command);
    }
}
