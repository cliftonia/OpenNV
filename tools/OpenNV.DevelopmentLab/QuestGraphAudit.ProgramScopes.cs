using System.Buffers.Binary;
using OpenNV.Runtime.Content;
using OpenNV.Runtime.World.Cells;

internal static partial class QuestGraphAudit
{
    internal sealed record SourceProgramScope(int FieldStart, int FieldEnd, string Scope, bool HasHeader);

    // Audit selection retains deleted winners without changing runtime indexes.
    internal static IReadOnlyList<FalloutPluginRecord> WinningSourceRecords(FalloutPluginStack records)
    {
        var seen = new HashSet<FalloutFormKey>(FalloutFormKeyComparer.Instance);
        var winners = new List<FalloutPluginRecord>();
        foreach (var context in records.Plugins)
            foreach (var declared in context.Plugin.Records)
                if (seen.Add(declared.FormKey) && records.TryGetWinner(declared.FormKey, out var winner)) winners.Add(winner);
        if (winners.Count != records.WinnerRecordCount)
            throw new InvalidDataException("Quest inventory differs from the actual source winner denominator.");
        return winners;
    }

    // The runtime's static inspector: metadata-only bindings, no world state.
    internal static FalloutReferenceScripts StaticInspector(FalloutPluginStack records, FalloutReferenceWorld world) =>
        new(records, world, new(records), new((_, _) => throw new InvalidOperationException("Static audit queried furniture."),
            _ => throw new InvalidOperationException("Static audit applied gameplay.")));

    // Standalone scripts declare their own blocks; embedded results run as one GameMode block.
    internal static IReadOnlyList<FalloutScriptEventProgram> SourceEvents(string signature, string text,
        IEnumerable<FalloutPluginSubrecord> compiled) => signature == "SCPT"
        ? FalloutGameModeProgram.ReadEvents(text, FalloutGameModeProgram.CompiledBody(compiled))
        : [new FalloutScriptEventProgram("Result", null, FalloutGameModeProgram.Read("begin GameMode\n" + text + "\nend"))];

    // Admits an operand only when a declaration or compiled binding owns it;
    // anything else is refused and reported to the caller first.
    internal static Action<string> OperandInspector(FalloutPluginStack records, FalloutPluginRecord record, string signature,
        IReadOnlyList<FalloutPluginSubrecord> body, Action<Exception> declarationFailure, Action<string>? unresolved = null)
    {
        var localScript = signature == "SCPT" ? record : FalloutScriptLocals.AttachedScript(records, record);
        var locals = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (localScript is not null)
        {
            try { locals.UnionWith(FalloutScriptLocals.ReadDeclarations(localScript).Keys); }
            catch (Exception error)
            {
                // Standalone declaration failures were already recorded. They
                // cannot suppress inspection of the successfully parsed source.
                if (signature != "SCPT") declarationFailure(error);
            }
        }
        var declarationBindings = new FalloutScriptBindings(records, record, record, body);
        return name =>
        {
            if (name.Equals("this", StringComparison.OrdinalIgnoreCase) || locals.Contains(name) ||
                FalloutScriptBindings.IsPlayer(name) && declarationBindings.HasPlayerReference ||
                declarationBindings.TryForm(name) is not null || declarationBindings.HasVariable(name)) return;
            unresolved?.Invoke(name);
            throw new NotSupportedException($"Operand {name} needs a declared value/function or runtime caller context.");
        };
    }

    internal static bool IsProgramField(string signature) =>
        signature is "SCHR" or "SCDA" or "SCTX" or "SCRO" or "SCRV" or "SLSD" or "SCVR" or "SCED";

    // This partitions ordered source fields for accounting. It never admits a
    // canonical runtime scope or invents an owner for orphan/future fields.
    internal static IReadOnlyList<SourceProgramScope> ProgramScopes(IReadOnlyList<FalloutPluginSubrecord> fields)
    {
        var result = new List<SourceProgramScope>();
        var scope = "root"; short? stage = null; var entry = 0;
        int? start = null; var header = false;
        void Flush(int end)
        {
            if (start is { } first) result.Add(new(first, end, scope, header));
            start = null; header = false;
        }
        for (var index = 0; index < fields.Count; ++index)
        {
            var field = fields[index];
            if (field.Signature is "INDX" or "QSDT" or "POBA" or "POEA" or "POCA" or "NEXT")
            {
                Flush(index);
                if (field.Signature == "INDX")
                {
                    stage = field.Data.Length == 2 ? BinaryPrimitives.ReadInt16LittleEndian(field.Data.Span) : null;
                    entry = 0; scope = "stage:" + stage + "/index-field:" + index;
                }
                else if (field.Signature == "QSDT") scope = $"stage:{stage}/entry:{entry++}/marker:{index}";
                else scope = field.Signature + "/marker:" + index;
            }
            if (field.Signature == "SCHR") { Flush(index); start = index; header = true; }
            else if (IsProgramField(field.Signature) && start is null) start = index;
        }
        Flush(fields.Count);
        return result;
    }

    internal static void RequireOutputOutsideInputs(string output, IEnumerable<string> roots)
    {
        var destination = Path.GetFullPath(output);
        foreach (var input in roots)
        {
            var relative = Path.GetRelativePath(Path.GetFullPath(input), destination);
            if (relative == "." || !Path.IsPathFullyQualified(relative) && relative != ".." &&
                !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                throw new IOException("Quest audit output must remain outside owned input roots.");
        }
    }
}
