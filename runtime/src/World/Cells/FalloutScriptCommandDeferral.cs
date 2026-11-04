using Godot;

namespace OpenNV.Runtime.World.Cells;

// A reached command whose engine effect OpenNV only approximates completes
// without stopping its script. Each name is reported once per process.
internal static class FalloutScriptCommandDeferral
{
    private static readonly HashSet<string> Reported = [];
    private static readonly Lock Gate = new();

    internal static void Warn(string operation, string reason)
    {
        lock (Gate)
            if (!Reported.Add(operation)) return;
        var message = $"OPENNV_SCRIPT_COMMAND_DEFERRED name={operation} reason={reason}";
        // Headless contract hosts have no Godot engine to receive the warning.
        try { GD.PushWarning(message); }
        catch (Exception error) when (error is TypeInitializationException or DllNotFoundException or
            EntryPointNotFoundException or NullReferenceException or InvalidOperationException)
        { Console.Error.WriteLine(message); }
    }
}
