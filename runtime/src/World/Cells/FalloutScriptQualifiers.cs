using OpenNV.Runtime.Content;

namespace OpenNV.Runtime.World.Cells;

// The three outcomes of a reference-variable qualifier that holds no usable
// reference: the run continues (a fallback receiver or a zero term), the rest
// of this run is abandoned (pending events survive), or the script form is
// disabled for the session. Every degraded site is counted and reported once.
internal static class FalloutScriptQualifiers
{
    private static readonly Dictionary<(FalloutFormKey Script, int Statement, string Operation, string Outcome), long> Sites = [];
    private static readonly Lock Gate = new();

    internal static long Count(string outcome)
    {
        lock (Gate) return Sites.Where(site => site.Key.Outcome == outcome).Sum(site => site.Value);
    }

    internal static void Report(FalloutFormKey script, int statement, string operation, string outcome)
    {
        long count;
        lock (Gate)
        {
            var site = (script, statement, operation, outcome);
            Sites[site] = count = Sites.GetValueOrDefault(site) + 1;
        }
        if (count == 1)
            FalloutScriptCommandDeferral.Sink($"OPENNV_SCRIPT_QUALIFIER outcome={outcome} script={script} line={statement} function={operation}");
    }
}

// "Function requires a reference" or a missing SET target: the rest of this
// run stops without retaining a fault; the next run starts from the top.
internal sealed class FalloutScriptRunAbandonedException(string message) : Exception(message);

// An expression whose qualifier resolved to a non-reference form: the script
// form never runs again this session, for any object that uses it.
internal sealed class FalloutScriptFormDisabledException(FalloutFormKey script, string message) : Exception(message)
{
    internal FalloutFormKey Script { get; } = script;
}
