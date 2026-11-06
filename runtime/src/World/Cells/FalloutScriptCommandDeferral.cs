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

    internal static void Warn(string operation, string reason) => Report("SCRIPT_COMMAND", operation, reason);

    // A combat rule reached in play whose consumer owner is absent.
    internal static void WarnCombat(string rule, string reason) => Report("COMBAT", rule, reason);

    private static void Report(string channel, string name, string reason)
    {
        lock (Gate)
            if (!Reported.Add(channel + ":" + name)) return;
        Sink($"OPENNV_{channel}_DEFERRED name={name} reason={reason}");
    }
}
