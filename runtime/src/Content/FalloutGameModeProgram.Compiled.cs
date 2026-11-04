using System.Text.RegularExpressions;

namespace OpenNV.Runtime.Content;

// Retail executes SCDA. Where editor SCTX has no well-formed branch structure,
// the compiled statement stream is the authority: every source statement is
// paired with exactly one compiled statement of the same kind, and branches
// follow the compiled jump extents instead of source nesting.
internal sealed partial class FalloutGameModeProgram
{
    private readonly int[]? _jumps;
    private readonly IReadOnlySet<int>? _blockStarts;

    internal bool UsesCompiledBranches => _jumps is not null;

    private FalloutGameModeProgram(IReadOnlyList<string[]> lines, int[] jumps, IReadOnlySet<int> blockStarts)
        : this(lines, new HashSet<int>())
    {
        _jumps = jumps;
        _blockStarts = blockStarts;
    }

    internal static IReadOnlyList<FalloutScriptEventProgram> ReadEvents(string source, ReadOnlySpan<byte> compiled)
    {
        try { return ReadEvents(source); }
        catch (Exception error) when (error is InvalidDataException or NotSupportedException && !compiled.IsEmpty)
        {
            // Only the source structure failed. The compiled stream must pair
            // with every source statement; otherwise the original failure stays.
            try { return ReadCompiledEvents(source, FalloutCompiledScript.Read(compiled)); }
            catch (Exception compiledError) when (compiledError is InvalidDataException or NotSupportedException)
            { throw new InvalidDataException($"{error.Message} Compiled recovery: {compiledError.Message}", error); }
        }
    }

    internal static FalloutGameModeProgram Read(string source, ReadOnlySpan<byte> compiled, string blockName = "GameMode",
        uint? argument = null) => Combine(ReadEvents(source, compiled), blockName, argument);

    // A malformed source was admitted only if its compiled stream pairs.
    internal static bool IsCompiledRecovery(string source, ReadOnlySpan<byte> compiled)
    {
        try { _ = ReadEvents(source); return false; }
        catch (Exception error) when (error is InvalidDataException or NotSupportedException)
        {
            _ = ReadEvents(source, compiled);
            return true;
        }
    }

    internal static ReadOnlySpan<byte> CompiledBody(IEnumerable<FalloutPluginSubrecord> fields)
    {
        var bodies = fields.Where(field => field.Signature == "SCDA").Take(2).ToArray();
        return bodies.Length switch
        {
            0 => default,
            1 => bodies[0].Data.Span,
            _ => throw new InvalidDataException("Script has ambiguous compiled bodies."),
        };
    }

    private static readonly string[] Declarations = ["short", "int", "long", "float", "ref", "reference"];

    private static IReadOnlyList<FalloutScriptEventProgram> ReadCompiledEvents(string source,
        IReadOnlyList<FalloutCompiledStatement> code)
    {
        var events = new List<FalloutScriptEventProgram>();
        List<string[]>? lines = null;
        List<int>? jumps = null;
        string? eventName = null, filter = null;
        var opcodes = new Dictionary<string, ushort>(StringComparer.OrdinalIgnoreCase);
        var cursor = 0;
        var lineNumber = 0;
        FalloutCompiledStatement Next(FalloutCompiledStatementKind kind)
        {
            if (cursor >= code.Count || code[cursor].Kind != kind)
                throw new InvalidDataException($"Script line {lineNumber} does not pair with compiled statement {cursor} ({kind}).");
            return code[cursor++];
        }
        foreach (var raw in source.Split('\n'))
        {
            ++lineNumber;
            var line = StripComment(raw).Trim();
            if (line.Length == 0) continue;
            var keyword = Regex.Match(line, @"^[A-Za-z_][A-Za-z0-9_.]*", RegexOptions.CultureInvariant).Value.ToLowerInvariant();
            if (Declarations.Contains(keyword)) continue;
            if (lines is null)
            {
                if (keyword is "scn" or "scriptname") { Next(FalloutCompiledStatementKind.ScriptName); continue; }
                if (keyword != "begin") throw new InvalidDataException($"Script line {lineNumber} is outside a compiled block.");
                var header = Tokens(line);
                if (header.Length is < 2 or > 3 || header[1].Equals("Function", StringComparison.OrdinalIgnoreCase))
                    throw new NotSupportedException("Compiled script block header is unbound.");
                Next(FalloutCompiledStatementKind.Begin);
                (lines, jumps, eventName, filter) = ([], [], header[1], header.Length == 3 ? header[2] : null);
                continue;
            }
            if (keyword == "end")
            {
                Next(FalloutCompiledStatementKind.End);
                events.Add(new(eventName!, filter, new(lines, jumps!.ToArray(), new HashSet<int> { 0 })));
                lines = null;
                continue;
            }
            var kind = keyword switch
            {
                "if" => FalloutCompiledStatementKind.If,
                "elseif" => FalloutCompiledStatementKind.ElseIf,
                "else" => FalloutCompiledStatementKind.Else,
                "endif" => FalloutCompiledStatementKind.EndIf,
                "set" => FalloutCompiledStatementKind.Set,
                "return" => FalloutCompiledStatementKind.Return,
                "while" or "loop" or "break" or "continue" or "let" or "eval" or "begin" or "scn" or "scriptname" =>
                    throw new NotSupportedException($"Script line {lineNumber} has no vanilla compiled statement."),
                _ => FalloutCompiledStatementKind.Command,
            };
            var statement = Next(kind);
            string[] tokens;
            try { tokens = Tokens(line); }
            catch (NotSupportedException) when (kind == FalloutCompiledStatementKind.Command)
            {
                // The compiler skipped the unbound characters; admit that only
                // when the remaining operands are exactly its compiled count.
                tokens = Regex.Matches(line, @"""[^""]*""|[A-Za-z0-9_.+-]+", RegexOptions.CultureInvariant).Select(match => match.Value).ToArray();
                if (tokens.Length - 1 != statement.Parameters)
                    throw new InvalidDataException($"Script line {lineNumber} operands disagree with its compiled command.");
            }
            if (kind is FalloutCompiledStatementKind.Else or FalloutCompiledStatementKind.EndIf or FalloutCompiledStatementKind.Return)
                tokens = [tokens[0]]; // The compiler emits no operands; trailing editor text is inert.
            if (kind == FalloutCompiledStatementKind.Command)
            {
                var receiver = tokens[0].Contains('.');
                if (receiver != (statement.Reference is not null))
                    throw new InvalidDataException($"Script line {lineNumber} receiver disagrees with its compiled command.");
                var operation = tokens[0][(tokens[0].LastIndexOf('.') + 1)..];
                // One source spelling names one compiled command throughout a script.
                if (!opcodes.TryAdd(operation, statement.Opcode) && opcodes[operation] != statement.Opcode)
                    throw new InvalidDataException($"Script line {lineNumber} command disagrees with its compiled opcode.");
            }
            lines.Add(tokens);
            jumps!.Add(statement.Jump);
        }
        if (lines is not null || cursor != code.Count) throw new InvalidDataException("Compiled script has statements without source.");
        return events;
    }

    // Retail branch state: one selection per open If plus the block level.
    // A false or completed arm skips its compiled extent. An unopened ElseIf or
    // Else reads the block level, and an unopened EndIf has nothing to close.
    private int CompiledBranch(int line, List<bool> taken, Func<bool> condition)
    {
        if (taken.Count == 0) taken.Add(false);
        var skip = line + _jumps![line];
        switch (_lines[line][0].ToLowerInvariant())
        {
            case "if":
                taken.Add(condition());
                return taken[^1] ? line : skip;
            case "elseif":
                if (taken[^1]) return skip;
                taken[^1] = condition();
                return taken[^1] ? line : skip;
            case "else":
                if (taken[^1]) return skip;
                taken[^1] = true;
                return line;
            default:
                if (taken.Count > 1) taken.RemoveAt(taken.Count - 1);
                return line;
        }
    }
}
