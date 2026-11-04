using System.Buffers.Binary;
using OpenNV.Runtime.Content;

// Retail executes SCDA. Malformed SCTX branch structure (Lucky 38 penthouse
// terminal, Lonesome Road launch terminal, ...) runs on compiled jump extents.
internal static class CompiledBranchContracts
{
    internal static void Run()
    {
        PenthouseShape();
        RepeatedElse();
        CompilerSkippedPunctuation();
        Console.WriteLine("OPENNV_COMPILED_BRANCH_CONTRACT_PASS framing=true jumps=true unopenedElseIf=true repeatedElse=true pairing=true");
    }

    // Shape of Lucky38MrHouseTerminalCodeSCRIPT GameMode: a stray EndIf closes
    // the Else level, so the second ElseIf follows the closed inner If (dead
    // after the taken Else) and the third ElseIf is at the block level.
    private static void PenthouseShape()
    {
        const string source = "scn Shape\nshort bKey\n\nBegin GameMode\n if bKey == 1\n  Return\n else\n  if A == 1\n" +
            "   SetObjectiveDisplayed Q 55 1\n   set bKey to 1\n  endif\n  elseif B == 1 ; stray arm\n   SetObjectiveDisplayed Q 12 1\n" +
            "   set bKey to 1\n  endif\n  elseif C == 1\n   SetObjectiveDisplayed Q 42 1\n   set bKey to 1\n  endif\n endif\nEnd\n";
        var compiled = Script(Block(0,
            If(1), Plain(0x1e), Else(4), If(2), Command(0x11a3, 3), Set(), Plain(0x19),
            ElseIf(2), Command(0x11a3, 3), Set(), Plain(0x19),
            ElseIf(2), Command(0x11a3, 3), Set(), Plain(0x19), Plain(0x19)));
        Reject(() => FalloutGameModeProgram.ReadEvents(source), "Malformed source branch structure was admitted without compiled extents.");
        var block = FalloutGameModeProgram.ReadEvents(source, compiled).Single();
        Require(block.Event == "GameMode" && block.Program.UsesCompiledBranches && block.Program.StatementCount == 16,
            "Compiled pairing lost statements or the event.");
        foreach (var (key, a, b, c, expected) in new (int, int, int, int, string[])[]
        {
            (1, 1, 1, 1, []),
            (0, 1, 0, 0, ["SetObjectiveDisplayed Q 55 1", "set bKey 1"]),
            (0, 0, 1, 0, []),
            (0, 0, 0, 1, ["SetObjectiveDisplayed Q 42 1", "set bKey 1"]),
            (0, 1, 0, 1, ["SetObjectiveDisplayed Q 55 1", "set bKey 1", "SetObjectiveDisplayed Q 42 1", "set bKey 1"]),
        })
        {
            var values = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase) { ["bKey"] = key, ["A"] = a, ["B"] = b, ["C"] = c };
            Require(Run(block.Program, values).SequenceEqual(expected),
                $"Compiled penthouse branches diverged for bKey={key} A={a} B={b} C={c}.");
        }
        // A combined invocation resets branch state at every compiled block.
        var twice = FalloutGameModeProgram.Read(source.Replace("End\n", "End\nBegin GameMode\n elseif C == 1\n  Disable\n endif\nEnd\n",
            StringComparison.Ordinal), Script(Block(0,
            If(1), Plain(0x1e), Else(4), If(2), Command(0x11a3, 3), Set(), Plain(0x19),
            ElseIf(2), Command(0x11a3, 3), Set(), Plain(0x19),
            ElseIf(2), Command(0x11a3, 3), Set(), Plain(0x19), Plain(0x19)), Block(0, ElseIf(1), Command(0x1022, 0), Plain(0x19))));
        Require(Run(twice, new(StringComparer.OrdinalIgnoreCase) { ["bKey"] = 0, ["A"] = 0, ["B"] = 0, ["C"] = 1 })
            .SequenceEqual(["SetObjectiveDisplayed Q 42 1", "set bKey 1", "Disable"]),
            "A later compiled block inherited the previous block's selection.");
        // Pairing is exact: a missing or substituted statement never runs.
        Reject(() => FalloutGameModeProgram.ReadEvents(source, Script(Block(0,
            If(1), Plain(0x1e), Else(4), If(2), Command(0x11a3, 3), Set(), Plain(0x19),
            ElseIf(2), Command(0x11a3, 3), Set(), Plain(0x19),
            ElseIf(2), Command(0x11a3, 3), Plain(0x1e), Plain(0x19), Plain(0x19)))), "A set/return substitution paired.");
        Reject(() => FalloutGameModeProgram.ReadEvents(source, Script(Block(0, If(1), Plain(0x1e), Plain(0x19)))),
            "A shorter compiled stream paired.");
        Reject(() => FalloutGameModeProgram.ReadEvents(source, Script(Block(0,
            If(1), Plain(0x1e), Else(2), If(2), Command(0x11a3, 3), Set(), Plain(0x19),
            ElseIf(2), Command(0x11a3, 3), Set(), Plain(0x19),
            ElseIf(2), Command(0x11a3, 3), Set(), Plain(0x19), Plain(0x19)))), "A jump onto an ordinary statement was admitted.");
    }

    // NVDLC03X13SoundCueTriggerSCRIPT shape: repeated Else arms after ElseIf.
    private static void RepeatedElse()
    {
        const string source = "scn Cue\nBegin OnTriggerEnter Player\n If (N <= 20)\n  PlaySound First\n ElseIf (N <= 40)\n" +
            "  PlaySound Second\n Else (N > 40) && (N <= 60)\n  PlaySound Third\n Else\n  PlaySound Fourth\n EndIf\n Disable\nEnd\n";
        var compiled = Script(Block(2, If(1), Command(0x1028, 1), ElseIf(1), Command(0x1028, 1), Else(1), Command(0x1028, 1),
            Else(1), Command(0x1028, 1), Plain(0x19), Command(0x1022, 0)));
        Reject(() => FalloutGameModeProgram.ReadEvents(source), "Repeated source Else was admitted without compiled extents.");
        var block = FalloutGameModeProgram.ReadEvents(source, compiled).Single();
        Require(block.Event == "OnTriggerEnter" && block.Filter == "Player", "Compiled pairing lost the event filter.");
        foreach (var (n, expected) in new (int, string[])[]
        {
            (10, ["PlaySound First", "Disable"]), (30, ["PlaySound Second", "Disable"]), (90, ["PlaySound Third", "Disable"]),
        })
            Require(Run(block.Program, new(StringComparer.OrdinalIgnoreCase) { ["N"] = n }).SequenceEqual(expected),
                $"Repeated compiled Else diverged for N={n}.");
    }

    // NVDLC03SinkMainComputerSCRIPT: the compiler skipped a trailing colon.
    private static void CompilerSkippedPunctuation()
    {
        const string source = "scn Sink\nBegin OnActivate\n ShowMessage UnlockMSG:\nEnd\n";
        Reject(() => FalloutGameModeProgram.ReadEvents(source), "Unbound source punctuation was admitted without compiled operands.");
        var block = FalloutGameModeProgram.ReadEvents(source, Script(Block(2, Command(0x1059, 1)))).Single();
        Require(Run(block.Program, new()).SequenceEqual(["ShowMessage UnlockMSG"]), "Compiled operand recovery changed the command.");
        Reject(() => FalloutGameModeProgram.ReadEvents(source, Script(Block(2, Command(0x1059, 2)))),
            "Recovered operands disagree with the compiled count but paired.");
    }

    private static List<string> Run(FalloutGameModeProgram program, Dictionary<string, double> values)
    {
        var calls = new List<string>();
        program.Execute(name => values.TryGetValue(name, out var value) ? value : throw new KeyNotFoundException(name),
            (name, value) => { values[name] = value; calls.Add($"set {name} {value}"); },
            (name, arguments) => calls.Add(string.Join(" ", new[] { name }.Concat(arguments))));
        return calls;
    }

    private static byte[] Script(params byte[][] blocks) => [.. Plain(0x1d), .. blocks.SelectMany(block => block)];
    private static byte[] Block(ushort type, params byte[][] statements)
    {
        var body = statements.SelectMany(statement => statement).Concat(Plain(0x11)).ToArray();
        var header = new byte[6];
        BinaryPrimitives.WriteUInt16LittleEndian(header, type);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(2), (uint)body.Length);
        return [.. Statement(0x10, header), .. body];
    }
    private static byte[] If(ushort jump) => Branch(0x16, jump);
    private static byte[] ElseIf(ushort jump) => Branch(0x18, jump);
    private static byte[] Branch(ushort opcode, ushort jump) => Statement(opcode, [.. U16(jump), .. U16(3), 0x20, 0x31, 0x20]);
    private static byte[] Else(ushort jump) => Statement(0x17, U16(jump));
    private static byte[] Set() => Statement(0x15, [0x73, 1, 0, 2, 0, 0x20, 0x31]);
    private static byte[] Command(ushort opcode, ushort parameters) =>
        Statement(opcode, [.. U16(parameters), .. Enumerable.Repeat((byte)0x6e, parameters * 5)]);
    private static byte[] Plain(ushort opcode) => Statement(opcode, []);
    private static byte[] Statement(ushort opcode, byte[] body) => [.. U16(opcode), .. U16(checked((ushort)body.Length)), .. body];
    private static byte[] U16(ushort value) { var bytes = new byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(bytes, value); return bytes; }

    private static void Reject(Action action, string message)
    {
        try { action(); }
        catch (Exception error) when (error is InvalidDataException or NotSupportedException) { return; }
        throw new InvalidDataException(message);
    }
    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidDataException(message); }
}
