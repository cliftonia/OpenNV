using System.Buffers.Binary;
using System.Text;
using OpenNV.Runtime.Content;

// The completeness report classifies synthetic demand against the actual
// repository dispatch, and its static dispatch reader against a synthetic one.
internal static class CompletenessReportContracts
{
    private const string Missing = "OpenNVContractMissingCommand";

    internal static void Run()
    {
        var directory = Path.Combine(Path.GetTempPath(), "opennv-completeness-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            DispatchReader(directory);
            Report(directory);
            Console.WriteLine("OPENNV_COMPLETENESS_REPORT_CONTRACT_PASS supportedCommand=true unsupportedCommand=true supportedCondition=true " +
                "unsupportedCondition=true unresolvedFunction=true blockedQuest=true registry=true deferral=true headlessDeferralIgnored=true rejection=true");
        }
        finally { Directory.Delete(directory, true); }
    }

    private static void DispatchReader(string directory)
    {
        var repository = Path.Combine(directory, "dispatch");
        Directory.CreateDirectory(Path.Combine(repository, "runtime", "src"));
        File.WriteAllText(Path.Combine(repository, "runtime", "src", "Dispatch.cs"), """
            internal static class Dispatch
            {
                private static readonly HashSet<string> Owned = ["registered"];
                private static readonly Dictionary<string, string> Late = new() { ["lateeffect"] = "value-not-a-key" };
                internal static void Call(string command)
                {
                    var operation = command.ToLowerInvariant();
                    if (operation == "direct") return;
                    if (Owned.Contains(operation)) return;
                    if (Late.TryGetValue(operation, out var reason)) { FalloutScriptCommandDeferral.Warn(operation, reason); return; }
                    if (operation == "refused") throw new NotSupportedException("no owner");
                    if (Helper(operation)) return;
                    switch (operation)
                    {
                        case "switched" when command.Length > 0: break;
                        case "headless": FalloutScriptCommandDeferral.Warn(operation, "no-native-session-owner"); break;
                    }
                }
                private static bool Helper(string name) => name is "helped" or "other";
            }
            """);
        var index = RuntimeDispatchIndex.Strings(repository, [new("test", "runtime/src/Dispatch.cs", "Call", "operation")]);
        bool Plain(string name) => index.Names.TryGetValue(name, out var sites) && sites.All(site => !site.Deferred && !site.Rejected);
        Require(new[] { "direct", "registered", "switched", "helped", "other", "headless" }.All(Plain),
            "The dispatch reader missed an equality, registry, switch or helper guard, or counted a headless fallback as a deferral.");
        Require(index.Names["lateeffect"].All(site => site.Deferred) && index.Names["refused"].All(site => site.Rejected),
            "The dispatch reader lost a deferral or an explicit rejection.");
        Require(!index.Names.ContainsKey("value-not-a-key") && index.Names.Count == 8,
            "The dispatch reader admitted a registry value or an unguarded name.");
    }

    private static void Report(string directory)
    {
        var repository = AppContext.BaseDirectory;
        while (!Directory.Exists(Path.Combine(repository, "runtime", "src")) || !File.Exists(Path.Combine(repository, "AGENTS.md")))
            repository = Path.GetDirectoryName(repository.TrimEnd(Path.DirectorySeparatorChar)) ??
                throw new DirectoryNotFoundException("The completeness contract needs the OpenNV checkout.");
        var data = Path.Combine(directory, "data");
        Directory.CreateDirectory(data);
        File.WriteAllBytes(Path.Combine(data, "Completeness.esm"), Join(Header(),
            Quest(0x100, "SupportedQuest", Condition(77), "EnablePlayerControls"),
            Quest(0x101, "BlockedQuest", Condition(9),
                $"{Missing}\nif OpenNVContractMissingFunction == 1\nEnablePlayerControls\nendif")));
        using var records = FalloutPluginStack.Load(data, ["Completeness.esm"]);
        var result = CompletenessReport.Build(records, repository);
        CompletenessReport.Status Status(string lane, string name) => result.Supply[new(lane, name)].Status;
        Require(Status("command", "enableplayercontrols") == CompletenessReport.Status.Executed &&
            Status("command", Missing.ToLowerInvariant()) == CompletenessReport.Status.Unsupported,
            "A dispatched or an unknown statement command was misclassified.");
        Require(Status("condition", CompletenessReport.ConditionName(77)) == CompletenessReport.Status.Executed &&
            Status("condition", CompletenessReport.ConditionName(9)) == CompletenessReport.Status.Unsupported,
            "A dispatched or an unknown condition function was misclassified.");
        Require(Status("operand", "opennvcontractmissingfunction") == CompletenessReport.Status.Unsupported,
            "An expression call with no runtime function owner was admitted.");
        var supported = result.Quests.Single(quest => quest.EditorId == "SupportedQuest");
        var blocked = result.Quests.Single(quest => quest.EditorId == "BlockedQuest");
        Require(supported.Blockers.Count == 0 && blocked.Blockers.Select(need => need.Lane).Order().SequenceEqual(["command", "condition", "operand"]),
            "Quest blocking did not follow its own unsupported stage and condition dependencies.");
        Require(CompletenessReport.Gaps(result).Take(3).All(gap => gap.Quests == 1) &&
            CompletenessReport.Markdown(result).Contains("| BlockedQuest |", StringComparison.Ordinal),
            "The gap ranking or the rendered quest table lost the blocked quest.");
    }

    // A quest with one CTDA and one stage whose result program has the given source.
    private static byte[] Quest(uint id, string editorId, byte[] condition, string result) => Record("QUST", id,
        Field("EDID", Text(editorId)), Field("CTDA", condition), Field("INDX", BitConverter.GetBytes((short)10)), Field("QSDT", [0]),
        Field("SCHR", new byte[20]), Field("SCTX", Encoding.ASCII.GetBytes(result)));

    private static byte[] Condition(ushort function)
    {
        var data = new byte[28];
        BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(8), function);
        return data;
    }

    private static byte[] Header()
    {
        var data = new byte[12]; BinaryPrimitives.WriteSingleLittleEndian(data, 1.34f);
        return Record("TES4", 0, Field("HEDR", data));
    }
    private static byte[] Record(string signature, uint id, params byte[][] fields)
    {
        var data = Join(fields);
        var bytes = new byte[24 + data.Length]; Encoding.ASCII.GetBytes(signature).CopyTo(bytes, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), (uint)data.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), id); data.CopyTo(bytes, 24); return bytes;
    }
    private static byte[] Field(string signature, byte[] data)
    {
        var bytes = new byte[6 + data.Length]; Encoding.ASCII.GetBytes(signature).CopyTo(bytes, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4), checked((ushort)data.Length)); data.CopyTo(bytes, 6); return bytes;
    }
    private static byte[] Text(string text) => Encoding.ASCII.GetBytes(text + '\0');
    private static byte[] Join(params byte[][] values) => values.SelectMany(value => value).ToArray();
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
}
