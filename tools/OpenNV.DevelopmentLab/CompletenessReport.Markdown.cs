using System.Globalization;
using System.Text;

internal static partial class CompletenessReport
{
    private static readonly (string Title, string[] Lanes)[] UseLanes =
    [
        ("Script statement commands", ["command"]),
        ("Script expression functions", ["function", "operand"]),
        ("Condition functions (CTDA)", ["condition"]),
        ("AI package procedures (PACK records)", ["package"]),
    ];

    internal sealed record Gap(Need Need, int Quests, int MainQuests, long Uses, string Evidence);

    internal static IReadOnlyList<Gap> Gaps(Result result) => result.Supply.Where(pair => pair.Value.Status == Status.Unsupported)
        .Select(pair => new Gap(pair.Key, result.Quests.Count(quest => quest.Needs.ContainsKey(pair.Key)),
            result.Quests.Count(quest => quest.Line is not null && quest.Needs.ContainsKey(pair.Key)),
            result.Uses.GetValueOrDefault(pair.Key), pair.Value.Evidence))
        .OrderByDescending(gap => gap.Quests).ThenByDescending(gap => gap.Uses).ThenBy(gap => gap.Need.Lane).ThenBy(gap => gap.Need.Name).ToArray();

    private static (long Executed, long Deferred, long Unsupported) Share(Result result, string[] lanes)
    {
        long executed = 0, deferred = 0, unsupported = 0;
        foreach (var (need, count) in result.Uses.Where(pair => lanes.Contains(pair.Key.Lane)))
        {
            switch (result.Supply[need].Status)
            {
                case Status.Executed: executed += count; break;
                case Status.Deferred: deferred += count; break;
                case Status.Unsupported: unsupported += count; break;
            }
        }
        return (executed, deferred, unsupported);
    }

    private static string Percent(long part, long total) => total == 0 ? "n/a" :
        (100.0 * part / total).ToString("0.0", CultureInfo.InvariantCulture) + "%";

    internal static string Headline(Result result)
    {
        var commands = Share(result, ["command"]);
        var conditions = Share(result, ["condition"]);
        var clean = result.Quests.Count(quest => quest.Blockers.Count == 0);
        return $"OPENNV_COMPLETENESS quests={clean}/{result.Quests.Count} " +
            $"commandUses={Percent(commands.Executed + commands.Deferred, commands.Executed + commands.Deferred + commands.Unsupported)} " +
            $"conditionUses={Percent(conditions.Executed + conditions.Deferred, conditions.Executed + conditions.Deferred + conditions.Unsupported)}";
    }

    internal static string Markdown(Result result)
    {
        var text = new StringBuilder();
        void Line(string value = "") => text.Append(value).Append('\n');
        string Cell(string value) => value.Replace("|", "\\|", StringComparison.Ordinal);
        var gaps = Gaps(result);
        var clean = result.Quests.Count(quest => quest.Blockers.Count == 0);
        var deferredOnly = result.Quests.Count(quest => quest.Blockers.Count == 0 && quest.Deferred.Count > 0);

        Line("# OpenNV completeness report");
        Line();
        Line("Private, generated from owned data. Do not commit or distribute.");
        Line();
        Line("Load order: " + string.Join(", ", result.Plugins) + $" ({result.LiveRecords:N0} live winning records).");
        Line();
        Line("## Headline");
        Line();
        Line("| Measure | Executed | Deferred | Unsupported | Supported (executed + deferred) |");
        Line("|---|---|---|---|---|");
        foreach (var (title, lanes) in UseLanes)
        {
            var share = Share(result, lanes);
            var total = share.Executed + share.Deferred + share.Unsupported;
            Line($"| {title}: uses | {share.Executed:N0} | {share.Deferred:N0} | {share.Unsupported:N0} | {Percent(share.Executed + share.Deferred, total)} of {total:N0} |");
        }
        Line($"| Quests with no unsupported dependency | {clean - deferredOnly:N0} | {deferredOnly:N0} (deferred effects only) | " +
            $"{result.Quests.Count - clean:N0} | {Percent(clean, result.Quests.Count)} of {result.Quests.Count:N0} |");
        Line();
        Line("Deferred means the runtime reaches the command but skips part of its engine effect with an " +
            "`OPENNV_SCRIPT_COMMAND_DEFERRED` warning on at least one path. A use is one statement, one resolved " +
            "expression call or unresolved operand, one CTDA field or one PACK record.");
        Line();

        Line("## Main quest lines");
        Line();
        Line("| Line | Quests | No unsupported dependency | Blocked |");
        Line("|---|---|---|---|");
        foreach (var line in result.Quests.Where(quest => quest.Line is not null).GroupBy(quest => quest.Line!).OrderBy(group => group.Key == "New Vegas" ? 0 : 1).ThenBy(group => group.Key))
            Line($"| {line.Key} | {line.Count()} | {line.Count(quest => quest.Blockers.Count == 0)} | {line.Count(quest => quest.Blockers.Count > 0)} |");
        Line();
        Line("| Quest | Name | Line | Status | Unsupported dependencies |");
        Line("|---|---|---|---|---|");
        foreach (var quest in result.Quests.Where(quest => quest.Line is not null).OrderBy(quest => quest.Line == "New Vegas" ? 0 : 1)
                     .ThenBy(quest => quest.Line).ThenBy(quest => quest.EditorId, StringComparer.OrdinalIgnoreCase))
            Line($"| {Cell(quest.EditorId)} | {Cell(quest.Name)} | {quest.Line} | {(quest.Blockers.Count == 0 ? quest.Deferred.Count == 0 ? "supported" : "deferred effects" : "blocked")} | " +
                $"{Cell(string.Join("; ", quest.Blockers.Select(Label)))} |");
        Line();

        Line("## Top gaps ranked by quests blocked");
        Line();
        Line("| # | Gap | Quests blocked | Main-line quests | Uses | Why unsupported |");
        Line("|---|---|---|---|---|---|");
        var rank = 0;
        foreach (var gap in gaps.Take(50))
            Line($"| {++rank} | {Cell(Label(gap.Need))} | {gap.Quests} | {gap.MainQuests} | {gap.Uses:N0} | {Cell(gap.Evidence)} |");
        Line();

        foreach (var (title, lanes) in UseLanes)
        {
            Line($"## {title}: unsupported and deferred");
            Line();
            Line("| Name | Status | Uses | Quests | Evidence |");
            Line("|---|---|---|---|---|");
            foreach (var pair in result.Supply.Where(pair => lanes.Contains(pair.Key.Lane) && pair.Value.Status != Status.Executed)
                         .OrderBy(pair => pair.Value.Status).ThenByDescending(pair => result.Uses.GetValueOrDefault(pair.Key)))
                Line($"| {Cell(Label(pair.Key))} | {pair.Value.Status} | {result.Uses.GetValueOrDefault(pair.Key):N0} | " +
                    $"{result.Quests.Count(quest => quest.Needs.ContainsKey(pair.Key))} | {Cell(pair.Value.Evidence)} |");
            Line();
        }

        var programs = result.Supply.Where(pair => pair.Key.Lane == "program").ToArray();
        Line("## Program-level refusals");
        Line();
        Line("| Refusal | Uses | Quests |");
        Line("|---|---|---|");
        foreach (var pair in programs.OrderByDescending(pair => result.Uses.GetValueOrDefault(pair.Key)))
            Line($"| {Cell(pair.Key.Name)} | {result.Uses.GetValueOrDefault(pair.Key):N0} | {result.Quests.Count(quest => quest.Needs.ContainsKey(pair.Key))} |");
        Line();
        Line("### Compiled (SCDA) lane");
        Line();
        Line($"{result.Compiled.Programs:N0} programs carry compiled bytecode. The runtime's SCDA statement framing reader decoded " +
            $"{result.Compiled.Decoded:N0} standalone scripts ({result.Compiled.FramingFailures:N0} refused; embedded result fragments are not framed), " +
            $"with {result.Compiled.Commands:N0} compiled command statements and {result.Compiled.DistinctOpcodes:N0} distinct opcodes. " +
            $"In {result.Compiled.PairedPrograms:N0} scripts the compiled command sequence matches the parsed source statement sequence one to one, " +
            $"naming {result.Compiled.NamedOpcodes:N0} opcodes unambiguously, so the source demand above stands in for the compiled demand there. " +
            "OpenNV executes parsed source, not bytecode: a program with compiled bytes and no source is unsupported.");
        Line();
        foreach (var program in result.Compiled.CompiledOnly) Line($"- compiled-only {Cell(program)}");
        Line();

        Line("## All quests");
        Line();
        Line("| Quest | Name | Records reached | Status | Unsupported dependencies |");
        Line("|---|---|---|---|---|");
        foreach (var quest in result.Quests.OrderBy(quest => quest.Blockers.Count).ThenBy(quest => quest.EditorId, StringComparer.OrdinalIgnoreCase))
            Line($"| {Cell(quest.EditorId)} | {Cell(quest.Name)} | {quest.Records} | " +
                $"{(quest.Blockers.Count == 0 ? quest.Deferred.Count == 0 ? "supported" : "deferred effects" : "blocked")} | {Cell(string.Join("; ", quest.Blockers.Select(Label)))} |");
        Line();

        Line("## How supply was derived");
        Line();
        Line("| Lane | Source of supply | Derived or inferred |");
        Line("|---|---|---|");
        Line("| Expression functions | The runtime's own function dispatch (`FalloutReferenceScripts` FunctionFor) was asked to resolve every call " +
            "while the runtime inspector walked each parsed program; an operand nothing resolves or declares is unsupported. | Derived (executed lookup) |");
        Line($"| Statement commands | Every guard constant in the reference executor's `Call` and the native session's `ApplyNativeSourceCommand`, " +
            $"following helpers and registries that receive the command name ({result.DispatchedStatements:N0} names). | Inferred statically from current dispatch source |");
        Line($"| Deferrals | `FalloutScriptCommandDeferral.Warn` reached from a name's dispatch branch, or named literally. " +
            $"The `{RuntimeDispatchIndex.HeadlessDeferral}` fallback is excluded because the game session always supplies the native command owner. | Inferred statically |");
        Line($"| Condition functions | Enum cases in `FalloutConditionRuntime` and the platform, NPC, creature and player native condition owners " +
            $"({result.DispatchedConditions:N0} functions). | Inferred statically |");
        Line($"| Package procedures | Procedure constants in NPC `AdvanceAi` and creature `SelectPackage` ({string.Join(", ", result.DispatchedProcedures.Select(value => ProcedureName((byte)value)))}). | Inferred statically |");
        Line();
        Line("Static inference reads the dispatch code at report time, so it cannot drift from the runtime, but it does not evaluate guards: " +
            "argument-count clauses, receiver forms and absent state owners can still refuse a dispatched name at run time.");
        if (result.UnanalyzedGuards.Count > 0)
        {
            Line();
            Line("Dispatch calls the analysis could not follow (their names count as unsupported unless another guard admits them):");
            Line();
            foreach (var site in result.UnanalyzedGuards) Line($"- `{site.File}:{site.Line}` {site.Kind}: `{site.Guard}`");
        }
        Line();
        Line("## Boundary");
        Line();
        Line("Quest reach = the QUST record (conditions, stage and objective programs), its attached script, every INFO whose QSTI names it, " +
            "and one hop from their compiled references: named packages, scripts and the scripts attached to named placed references. " +
            "Object scripts reached only through world placement, perk entry points and other record consumers are counted in the totals but not attributed to quests. " +
            "Inspection stops at the first unresolved operand of an expression, so later operands in that expression are not counted. " +
            "Statement arguments are not inspected. Record-type consumer coverage (whether a PERK or RCPE condition is evaluated at all) is not measured. " +
            "A dispatched name is not proof of correct behaviour or retail parity.");
        return text.ToString();
    }

    private static string Label(Need need) => need.Lane switch
    {
        "command" => "command " + need.Name,
        "function" => "function " + need.Name,
        "operand" => "unresolved operand " + need.Name,
        "condition" => "condition " + need.Name,
        "package" => "package " + need.Name,
        _ => need.Name,
    };
}
