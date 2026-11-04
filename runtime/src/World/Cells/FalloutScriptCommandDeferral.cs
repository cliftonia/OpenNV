namespace OpenNV.Runtime.World.Cells;

// A reached command whose engine effect OpenNV only approximates completes
// without stopping its script. Each name is reported once per process.
internal static class FalloutScriptCommandDeferral
{
    private static readonly HashSet<string> Reported = [];
    private static readonly Lock Gate = new();

    // Contract probes run without the engine. The Godot host replaces this with
    // its warning channel before any session can execute source scripts.
    internal static Action<string> Sink { get; set; } = message => Console.Error.WriteLine(message);

    internal static void Warn(string operation, string reason)
    {
        lock (Gate)
            if (!Reported.Add(operation)) return;
        Sink($"OPENNV_SCRIPT_COMMAND_DEFERRED name={operation} reason={reason}");
    }
}
