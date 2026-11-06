namespace OpenNV.Runtime.World.Cells;

// Accepted AI package inputs whose engine behaviour is not implemented keep
// the actor's AI running. Each deferral key is reported once per process on
// the same warning channel as script command deferrals.
internal static class FalloutPackageDeferral
{
    private static readonly HashSet<string> Reported = [];
    private static readonly Lock Gate = new();

    internal static void Warn(string key, string reason)
    {
        lock (Gate)
            if (!Reported.Add(key)) return;
        FalloutScriptCommandDeferral.Sink($"OPENNV_PACKAGE_DEFERRED key={key} reason={reason}");
    }
}
