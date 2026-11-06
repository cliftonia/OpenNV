using System.Buffers.Binary;
using OpenNV.Runtime.Content;
using OpenNV.Runtime.Content.Definitions;
using OpenNV.Runtime.World.Cells;

// Measures what the selected load order uses (demand) against what the current
// runtime dispatch handles (supply). Supply comes from the runtime itself:
// expression functions from the live function dispatch during inspection,
// statements/conditions/packages from the dispatch source (RuntimeDispatchIndex).
internal static partial class CompletenessReport
{
    internal enum Status { Executed, Deferred, Unsupported }
    internal sealed record Need(string Lane, string Name);
    internal sealed record Supply(Status Status, string Evidence);
    internal sealed record QuestResult(FalloutPluginRecord Quest, string EditorId, string Name, string? Line,
        IReadOnlyDictionary<Need, long> Needs, IReadOnlyList<Need> Blockers, IReadOnlyList<Need> Deferred, int Records);
    internal sealed record CompiledLane(int Programs, int Decoded, int FramingFailures, int Commands, int DistinctOpcodes, int PairedPrograms, int NamedOpcodes,
        IReadOnlyList<string> CompiledOnly);
    internal sealed record Result(IReadOnlyList<string> Plugins, IReadOnlyDictionary<Need, Supply> Supply,
        IReadOnlyDictionary<Need, long> Uses, IReadOnlyList<QuestResult> Quests, CompiledLane Compiled,
        IReadOnlyList<RuntimeDispatchIndex.Site> UnanalyzedGuards, int DispatchedStatements, int DispatchedConditions,
        IReadOnlyList<int> DispatchedProcedures, int LiveRecords);

    private sealed class Demand
    {
        internal readonly Dictionary<Need, long> Uses = [];
        internal readonly HashSet<FalloutFormKey> References = new(FalloutFormKeyComparer.Instance);
        internal void Add(string lane, string name) => Uses[new(lane, name)] = Uses.GetValueOrDefault(new(lane, name)) + 1;
    }

    // Display names for PKDT procedure types (xEdit wbDefinitionsFNV). They
    // label demand only; support comes from the package selection dispatch.
    private static readonly string[] ProcedureNames = ["Find", "Follow", "Escort", "Eat", "Sleep", "Wander", "Travel", "Accompany",
        "UseItemAt", "Ambush", "FleeNotCombat", "Unknown11", "Sandbox", "Patrol", "Guard", "Dialogue", "UseWeapon"];

    internal static int RunCommand(string[] arguments)
    {
        string? dataRoot = null, output = null;
        var repository = Directory.GetCurrentDirectory();
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        string Value(ref int index) => ++index < arguments.Length ? arguments[index] :
            throw new ArgumentException("Missing value for " + arguments[index - 1]);
        for (var index = 0; index < arguments.Length; index++)
        {
            switch (arguments[index])
            {
                case "--data-root": dataRoot = Value(ref index); break;
                case "--out": output = Path.GetFullPath(Value(ref index)); break;
                case "--repository": repository = Path.GetFullPath(Value(ref index)); break;
                default:
                    if (!DevelopmentLabSource.IsOption(arguments[index])) throw new ArgumentException("Unknown completeness option: " + arguments[index]);
                    DevelopmentLabSource.ReadOption(arguments, ref index, options);
                    break;
            }
        }
        if (dataRoot is null) throw new ArgumentException("completeness requires --data-root <installation>.");
        using var content = DevelopmentLabSource.Open(dataRoot, DevelopmentLabSource.ReadSelection(options));
        // Reports are owned-data derivatives: never inside the data or the repository.
        if (output is not null) QuestGraphAudit.RequireOutputOutsideInputs(output, content.ContentRoots.Append(repository));
        using var records = FalloutPluginStack.Load(content.PluginSources);
        var result = Build(records, repository);
        var markdown = Markdown(result);
        if (output is null) { Console.Write(markdown); return 0; }
        File.WriteAllText(output, markdown);
        Console.WriteLine(Headline(result) + " report=" + output);
        return 0;
    }

    internal static Result Build(FalloutPluginStack records, string repository)
    {
        var statements = RuntimeDispatchIndex.Strings(repository, RuntimeDispatchIndex.Statements);
        var functions = RuntimeDispatchIndex.Strings(repository, RuntimeDispatchIndex.Functions);
        var conditions = RuntimeDispatchIndex.ConditionFunctions(repository);
        var packages = RuntimeDispatchIndex.Procedures(repository);
        var live = QuestGraphAudit.WinningSourceRecords(records).Where(record => !record.IsDeleted).ToArray();
        using var world = new FalloutReferenceWorld(records);
        var inspector = QuestGraphAudit.StaticInspector(records, world);
        var demand = new Dictionary<FalloutFormKey, Demand>(FalloutFormKeyComparer.Instance);
        var opcodes = new Dictionary<ushort, HashSet<string>>();
        int compiledPrograms = 0, decodedPrograms = 0, framingFailures = 0, compiledCommands = 0, pairedPrograms = 0;
        var compiledOnly = new List<(string Scope, ushort[] Opcodes)>();
        foreach (var record in live)
            demand.Add(record.FormKey, Scan(record));

        Demand Scan(FalloutPluginRecord record)
        {
            var result = new Demand();
            FalloutPluginSubrecord[] fields;
            try { fields = record.ReadSubrecords().ToArray(); }
            catch (Exception error) when (Refusal(error)) { result.Add("program", "unreadable record"); return result; }
            foreach (var field in fields.Where(value => value.Signature == "CTDA"))
            {
                try { result.Add("condition", ConditionName(FalloutCondition.Read(record, field.Data.Span).Function)); }
                catch (Exception error) when (Refusal(error)) { result.Add("program", "CTDA decode failure"); }
            }
            if (record.Signature == "PACK")
            {
                try { result.Add("package", ProcedureName(FalloutPackageData.Read(record).Procedure)); }
                catch (Exception error) when (Refusal(error)) { result.Add("program", "PACK data decode failure"); }
            }
            foreach (var scope in QuestGraphAudit.ProgramScopes(fields))
            {
                var body = fields[scope.FieldStart..scope.FieldEnd];
                foreach (var reference in body.Where(value => value.Signature == "SCRO" && value.Data.Length == 4))
                    if (BinaryPrimitives.ReadUInt32LittleEndian(reference.Data.Span) is var raw and not 0)
                        result.References.Add(record.Plugin.AdjustFormId(raw));
                var compiled = body.Where(value => value.Signature == "SCDA").ToArray();
                var sources = body.Where(value => value.Signature == "SCTX").ToArray();
                IReadOnlyList<FalloutCompiledStatement>? compiledStatements = null;
                if (compiled.Any(value => value.Data.Length > 0)) ++compiledPrograms;
                // The statement framing reader covers standalone scripts; embedded
                // result fragments carry no name/block framing and stay undecoded.
                if (record.Signature == "SCPT" && compiled.Length == 1 && compiled[0].Data.Length > 0)
                {
                    try
                    {
                        compiledStatements = FalloutCompiledScript.Read(compiled[0].Data.Span)
                            .Where(value => value.Kind == FalloutCompiledStatementKind.Command).ToArray();
                        ++decodedPrograms; compiledCommands += compiledStatements.Count;
                    }
                    catch (Exception error) when (Refusal(error)) { ++framingFailures; }
                }
                if (sources.Length == 0)
                {
                    // OpenNV executes parsed source only; compiled bytes alone have no interpreter.
                    if (compiled.Any(value => value.Data.Length > 0))
                    {
                        result.Add("program", "compiled-only program (no SCDA interpreter)");
                        compiledOnly.Add(($"{record.FormKey}/{scope.Scope}", compiledStatements?.Select(value => value.Opcode).ToArray() ?? []));
                    }
                    continue;
                }
                var sourceCommands = new List<string>();
                foreach (var source in sources)
                {
                    IReadOnlyList<FalloutScriptEventProgram> events;
                    try { events = QuestGraphAudit.SourceEvents(record.Signature, FalloutDialogueTopic.ScriptText(source.Data.Span), compiled); }
                    catch (Exception error) when (Refusal(error)) { result.Add("program", "source parse failure"); continue; }
                    try
                    {
                        var inspectValue = QuestGraphAudit.OperandInspector(records, record, record.Signature, body, _ => { },
                            name => result.Add("operand", Operation(name)));
                        foreach (var block in events)
                        {
                            string? previous = null;
                            // The parser probes an optional argument and then resolves the same
                            // token; one consecutive repeat is one use.
                            var coverage = inspector.Inspect(record, record, body, block.Program, inspectValue, (name, admitted) =>
                            {
                                if (admitted && name != previous) result.Add("function", Operation(name));
                                previous = admitted ? name : null;
                            });
                            foreach (var statement in coverage)
                            {
                                if (statement.Ownership == "statement-dispatch-not-inspected")
                                {
                                    // A receiver token with no command (`Ref. Command`) is one malformed shape, not one gap per receiver.
                                    var operation = statement.Operation.Length == 0 ? "<receiver-only `ref.` token>" : statement.Operation;
                                    result.Add("command", operation);
                                    sourceCommands.Add(operation);
                                }
                                else if (statement.Ownership == "unbound" && statement.Error is { } error && !error.StartsWith("Operand ", StringComparison.Ordinal))
                                    result.Add("program", "unbound syntax: " + error);
                            }
                        }
                    }
                    catch (Exception error) when (Refusal(error)) { result.Add("program", "source inspection failure"); }
                }
                // Learn opcode names only where compiled and source statement sequences align exactly.
                if (compiledStatements is not null && compiledStatements.Count == sourceCommands.Count)
                {
                    ++pairedPrograms;
                    for (var index = 0; index < sourceCommands.Count; ++index)
                    {
                        if (!opcodes.TryGetValue(compiledStatements[index].Opcode, out var names)) opcodes.Add(compiledStatements[index].Opcode, names = []);
                        names.Add(sourceCommands[index]);
                    }
                }
            }
            return result;
        }

        var supply = new Dictionary<Need, Supply>();
        Supply Classify(Need need)
        {
            if (supply.TryGetValue(need, out var known)) return known;
            bool Deferred(string name) => statements.LiteralDeferrals.Contains(name) ||
                statements.Names.GetValueOrDefault(name)?.Any(site => site.Deferred) == true ||
                functions.Names.GetValueOrDefault(name)?.Any(site => site.Deferred) == true;
            Supply Dispatched(RuntimeDispatchIndex.Index index, string key, string name)
            {
                var sites = index.Names.GetValueOrDefault(key)?.Where(site => !site.Rejected).ToArray() ?? [];
                if (sites.Length == 0) return new(Status.Unsupported, index.Names.ContainsKey(key) ? "dispatch rejects it" : "no dispatch guard");
                var owners = string.Join(", ", sites.Select(site => site.Owner).Distinct().Order());
                return Deferred(name) ? new(Status.Deferred, owners + "; deferral warning on at least one path") : new(Status.Executed, owners);
            }
            var result = need.Lane switch
            {
                "command" => Dispatched(statements, need.Name, need.Name),
                "function" => Deferred(need.Name) ? new(Status.Deferred, "runtime function dispatch; deferral warning on at least one path") :
                    new Supply(Status.Executed, "runtime function dispatch admitted it"),
                "operand" => new(Status.Unsupported, "runtime function dispatch has no owner and no declaration binds it"),
                "condition" => Dispatched(conditions, need.Name.Split(' ')[0], ""),
                "package" => Dispatched(packages, need.Name.Split('(', ')')[1], ""),
                _ => new(Status.Unsupported, "no runtime owner"),
            };
            supply.Add(need, result);
            return result;
        }

        var uses = new Dictionary<Need, long>();
        foreach (var (need, count) in demand.Values.SelectMany(value => value.Uses))
            uses[need] = uses.GetValueOrDefault(need) + count;
        foreach (var need in uses.Keys) Classify(need);

        var infos = live.Where(record => record.Signature == "INFO").Select(record => (Record: record, Quest: QuestOf(record)))
            .Where(value => value.Quest is not null).ToLookup(value => value.Quest!.Value, value => value.Record, FalloutFormKeyComparer.Instance);
        var quests = new List<QuestResult>();
        foreach (var quest in live.Where(record => record.Signature == "QUST"))
        {
            var closure = new HashSet<FalloutFormKey>(FalloutFormKeyComparer.Instance) { quest.FormKey };
            if (Attached(quest) is { } script) closure.Add(script);
            closure.UnionWith(infos[quest.FormKey].Select(info => info.FormKey));
            // One hop: packages, scripts and placed references' scripts the quest's programs name.
            foreach (var reference in closure.ToArray().SelectMany(key => demand[key].References).ToArray())
            {
                if (!records.TryGetWinner(reference, out var target) || target.IsDeleted) continue;
                if (target.Signature is "PACK" or "SCPT") closure.Add(target.FormKey);
                else if (target.Signature is "REFR" or "ACHR" or "ACRE" && Attached(target) is { } referenced) closure.Add(referenced);
            }
            var needs = new Dictionary<Need, long>();
            foreach (var (need, count) in closure.Where(demand.ContainsKey).SelectMany(key => demand[key].Uses))
                needs[need] = needs.GetValueOrDefault(need) + count;
            var editorId = Text(quest, "EDID") ?? quest.FormKey.ToString();
            quests.Add(new(quest, editorId, Text(quest, "FULL") ?? "", MainLine(editorId),
                needs, needs.Keys.Where(need => supply[need].Status == Status.Unsupported).OrderBy(need => need.Lane).ThenBy(need => need.Name).ToArray(),
                needs.Keys.Where(need => supply[need].Status == Status.Deferred).OrderBy(need => need.Lane).ThenBy(need => need.Name).ToArray(),
                closure.Count));
        }

        FalloutFormKey? Attached(FalloutPluginRecord owner)
        {
            try { return FalloutScriptLocals.AttachedScript(records, owner)?.FormKey; }
            catch (Exception error) when (Refusal(error)) { return null; }
        }
        FalloutFormKey? QuestOf(FalloutPluginRecord info)
        {
            var link = info.ReadSubrecords().FirstOrDefault(field => field.Signature == "QSTI");
            return link.Data.Length == 4 ? info.Plugin.AdjustFormId(BinaryPrimitives.ReadUInt32LittleEndian(link.Data.Span)) : null;
        }

        var named = opcodes.Where(pair => pair.Value.Count == 1).ToDictionary(pair => pair.Key, pair => pair.Value.Single());
        return new(records.Plugins.Select(value => value.Plugin.Name).ToArray(), supply, uses, quests,
            new(compiledPrograms, decodedPrograms, framingFailures, compiledCommands, opcodes.Count, pairedPrograms, named.Count,
                compiledOnly.Select(value => value.Scope + ": " + (value.Opcodes.Length == 0 ? "embedded fragment, opcodes not framed" :
                    string.Join(' ', value.Opcodes.Select(opcode => named.TryGetValue(opcode, out var name) ? name : $"0x{opcode:x4}")))).ToArray()),
            [.. statements.DynamicGuards, .. functions.DynamicGuards],
            statements.Names.Count, conditions.Names.Count, packages.Names.Keys.Select(int.Parse).Order().ToArray(), live.Length);
    }

    private static bool Refusal(Exception error) => error is InvalidDataException or NotSupportedException or
        InvalidOperationException or KeyNotFoundException or OverflowException or ArgumentException;

    private static string Operation(string name) => name[(name.LastIndexOf('.') + 1)..].ToLowerInvariant();

    internal static string ConditionName(ushort function) => Enum.IsDefined((FalloutConditionFunction)function)
        ? $"{(FalloutConditionFunction)function} ({function})" : $"#{function} (unnamed in OpenNV definitions)";

    private static string ProcedureName(byte procedure) =>
        $"{(procedure < ProcedureNames.Length ? ProcedureNames[procedure] : "Unknown")} ({procedure})";

    private static string? Text(FalloutPluginRecord record, string signature) => record.ReadSubrecords()
        .Where(field => field.Signature == signature).Select(field => FalloutDialogueTopic.Text(field.Data.Span)).FirstOrDefault();

    // Main quest lines follow the shipped editor-ID convention.
    internal static string? MainLine(string editorId)
    {
        if (System.Text.RegularExpressions.Regex.IsMatch(editorId, "^(VCG0[1-3]|VMQ|VHD)", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            return "New Vegas";
        var dlc = System.Text.RegularExpressions.Regex.Match(editorId, "^NVDLC0([1-4])MQ", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return dlc.Success ? dlc.Groups[1].Value switch
        {
            "1" => "Dead Money",
            "2" => "Honest Hearts",
            "3" => "Old World Blues",
            _ => "Lonesome Road",
        } : null;
    }
}
