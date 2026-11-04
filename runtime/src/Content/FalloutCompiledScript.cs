using System.Buffers.Binary;

namespace OpenNV.Runtime.Content;

internal enum FalloutCompiledStatementKind
{
    ScriptName, Begin, End, Set, If, Else, ElseIf, EndIf, Return, Command,
}

// One SCDA statement. Jump is the compiled count of following statements a
// branch skips; it lands on the next Else, ElseIf or EndIf the compiler bound.
internal readonly record struct FalloutCompiledStatement(FalloutCompiledStatementKind Kind, ushort Opcode,
    ushort? Reference, ushort Jump, ushort Parameters, int Block);

// SCDA statement framing and branch extents. Retail executes these compiled
// statements, not SCTX. This reader binds framing, block extents and branch
// jumps; expressions and command operands remain owned by the paired source.
internal static class FalloutCompiledScript
{
    private const ushort BeginOpcode = 0x10, EndOpcode = 0x11, SetOpcode = 0x15, IfOpcode = 0x16, ElseOpcode = 0x17,
        ElseIfOpcode = 0x18, EndIfOpcode = 0x19, ReferenceOpcode = 0x1c, ScriptNameOpcode = 0x1d, ReturnOpcode = 0x1e,
        FirstCommandOpcode = 0x1000;

    internal static IReadOnlyList<FalloutCompiledStatement> Read(ReadOnlySpan<byte> code)
    {
        var statements = new List<FalloutCompiledStatement>();
        var at = 0;
        var block = -1;
        var blockEnd = -1;
        ushort? reference = null;
        while (at < code.Length)
        {
            var opcode = U16(code, at);
            if (opcode == ReferenceOpcode)
            {
                if (reference is not null || block < 0) throw new InvalidDataException("Compiled reference prefix is not followed by a block command.");
                reference = U16(code, at + 2);
                if (reference == 0) throw new InvalidDataException("Compiled reference prefix has no SCRO index.");
                at += 4;
                continue;
            }
            var length = U16(code, at + 2);
            var bodyStart = at + 4;
            if (bodyStart + length > code.Length) throw new InvalidDataException("Compiled statement exceeds its SCDA extent.");
            var body = code.Slice(bodyStart, length);
            at = bodyStart + length;
            var kind = opcode switch
            {
                ScriptNameOpcode => FalloutCompiledStatementKind.ScriptName,
                BeginOpcode => FalloutCompiledStatementKind.Begin,
                EndOpcode => FalloutCompiledStatementKind.End,
                SetOpcode => FalloutCompiledStatementKind.Set,
                IfOpcode => FalloutCompiledStatementKind.If,
                ElseOpcode => FalloutCompiledStatementKind.Else,
                ElseIfOpcode => FalloutCompiledStatementKind.ElseIf,
                EndIfOpcode => FalloutCompiledStatementKind.EndIf,
                ReturnOpcode => FalloutCompiledStatementKind.Return,
                >= FirstCommandOpcode => FalloutCompiledStatementKind.Command,
                _ => throw new NotSupportedException($"Compiled statement opcode 0x{opcode:x} is unbound."),
            };
            if (reference is not null && kind != FalloutCompiledStatementKind.Command)
                throw new InvalidDataException("Compiled reference prefix precedes a non-command statement.");
            ushort jump = 0, parameters = 0;
            switch (kind)
            {
                case FalloutCompiledStatementKind.ScriptName:
                    if (statements.Count != 0 || length != 0) throw new InvalidDataException("Compiled script name is not the empty first statement.");
                    break;
                case FalloutCompiledStatementKind.Begin:
                    if (block >= 0 || length < 6) throw new InvalidDataException("Compiled block begin is nested or truncated.");
                    block = statements.Count;
                    blockEnd = at + checked((int)BinaryPrimitives.ReadUInt32LittleEndian(body[2..]));
                    if (blockEnd > code.Length) throw new InvalidDataException("Compiled block exceeds its SCDA extent.");
                    break;
                case FalloutCompiledStatementKind.End:
                    if (block < 0 || length != 0 || at != blockEnd) throw new InvalidDataException("Compiled block end disagrees with its begin extent.");
                    break;
                case FalloutCompiledStatementKind.If or FalloutCompiledStatementKind.ElseIf:
                    if (length < 4 || U16(body, 2) != length - 4) throw new InvalidDataException("Compiled branch expression extent is invalid.");
                    jump = U16(body, 0);
                    break;
                case FalloutCompiledStatementKind.Else:
                    if (length != 2) throw new InvalidDataException("Compiled else extent is invalid.");
                    jump = U16(body, 0);
                    break;
                case FalloutCompiledStatementKind.EndIf or FalloutCompiledStatementKind.Return:
                    if (length != 0) throw new InvalidDataException("Compiled control statement has operands.");
                    break;
                case FalloutCompiledStatementKind.Command:
                    parameters = length >= 2 ? U16(body, 0) : (ushort)0;
                    break;
            }
            if (kind != FalloutCompiledStatementKind.ScriptName && kind != FalloutCompiledStatementKind.Begin && block < 0)
                throw new InvalidDataException("Compiled statement is outside a block.");
            statements.Add(new(kind, opcode, reference, jump, parameters, block));
            reference = null;
            if (kind == FalloutCompiledStatementKind.End) block = -1;
        }
        if (block >= 0 || reference is not null || statements.Count == 0 || statements[0].Kind != FalloutCompiledStatementKind.ScriptName)
            throw new InvalidDataException("Compiled script is unterminated or has no script name.");
        for (var index = 0; index < statements.Count; ++index)
        {
            var statement = statements[index];
            if (statement.Kind is not (FalloutCompiledStatementKind.If or FalloutCompiledStatementKind.ElseIf or FalloutCompiledStatementKind.Else))
                continue;
            // A false or completed branch resumes on the compiler-bound
            // continuation, never past its block or onto an ordinary statement.
            var target = index + 1 + statement.Jump;
            if (target >= statements.Count || statements[target].Block != statement.Block ||
                statements[target].Kind is not (FalloutCompiledStatementKind.Else or FalloutCompiledStatementKind.ElseIf or FalloutCompiledStatementKind.EndIf))
                throw new InvalidDataException("Compiled branch jump does not land on a branch continuation.");
        }
        return statements;
    }

    private static ushort U16(ReadOnlySpan<byte> data, int at) => at + 2 <= data.Length
        ? BinaryPrimitives.ReadUInt16LittleEndian(data[at..]) : throw new InvalidDataException("Compiled statement header is truncated.");
}
