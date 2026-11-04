using System.Buffers.Binary;

namespace OpenNV.Runtime.Content;

internal static partial class FalloutExecutableStringTable
{
    // Some owned executables compile the input profile unit with frame-pointer
    // locals: the reader calls the imported profile function directly and a
    // setter stores each byte lane. Bind the same names, lanes and constructor
    // defaults from that shape. No key codes or retail addresses are a fallback.
    private static bool TryReadFramePointerProfileReader(ReadOnlySpan<byte> code, Func<uint, string?> literal,
        Func<uint, string?> import, out uint table, out int mouseBase)
    {
        table = 0; mouseBase = 0; var found = false;
        for (var at = 8; at <= code.Length - 11; ++at)
        {
            if (code[at] != 0x68 || literal(U32(code, at + 1)) != "Controls" ||
                code[at + 5] != 0xff || code[at + 6] != 0x15 || import(U32(code, at + 7)) != "GetPrivateProfileStringA") continue;
            if (code[at - 8] != 0x8b || (code[at - 6] & 0xc7) != 0x85 || code[at - 1] < 0x50 || code[at - 1] > 0x57)
                throw new NotSupportedException("Owned input profile table declaration is unbound.");
            var names = U32(code, at - 5);
            var setter = -1;
            for (var after = at + 11; after < Math.Min(code.Length - 5, at + 90); ++after)
            {
                if (code[after] != 0xe8) continue;
                var target = after + 5 + BinaryPrimitives.ReadInt32LittleEndian(code[(after + 1)..]);
                if (target >= 0 && target < code.Length - 64 && TryReadLaneSetter(code[target..], out var lane))
                { setter = lane; break; }
            }
            if (setter < 0) throw new NotSupportedException("Owned input profile byte order is unbound.");
            if (found) throw new InvalidDataException("Owned input profile reader is ambiguous.");
            found = true; table = names; mouseBase = setter;
        }
        return found;
    }

    // The setter masks the profile value into keyboard (bits 16-23), mouse
    // (bits 8-15) and joystick (bits 0-7) lanes, each 28 bytes apart.
    private static bool TryReadLaneSetter(ReadOnlySpan<byte> setter, out int mouseBase)
    {
        mouseBase = 0;
        var masks = new[] { 0x00ff0000u, 0x0000ff00u, 0x000000ffu };
        var offsets = new List<int>(3); var next = 0;
        for (var at = 0; at < Math.Min(setter.Length - 6, 96) && offsets.Count < 3; ++at)
        {
            if (setter[at] == 0xc2 || setter[at] == 0xc3) return false;
            if (next < 3 && (setter[at] == 0x25 || setter[at] == 0x81) && at + 5 < setter.Length)
            {
                var immediateAt = setter[at] == 0x25 ? at + 1 : at + 2;
                if (U32(setter, immediateAt) == masks[next]) next++;
                continue;
            }
            if (setter[at] == 0x88 && (setter[at + 1] & 0xc0) == 0x80 && (setter[at + 1] & 7) != 4 && next == offsets.Count + 1)
                offsets.Add(BinaryPrimitives.ReadInt32LittleEndian(setter[(at + 2)..]));
        }
        if (offsets.Count != 3 || offsets[1] - offsets[0] != 28 || offsets[2] - offsets[1] != 56) return false;
        mouseBase = offsets[1];
        return true;
    }

    // Constructor defaults: frame-local object loads paired with byte stores,
    // grouped by device checks. Keyboard and mouse groups precede joystick (2).
    private static byte[]? ReadFramePointerInputDefaults(ReadOnlySpan<byte> code, int mouseBase)
    {
        var keyboardBase = mouseBase - 28;
        byte[]? bindings = null;
        for (var at = 0; at <= code.Length - 10; ++at)
        {
            if (!IsFrameStore(code, at, out var first, out _) || first != keyboardBase) continue;
            var values = Enumerable.Repeat(byte.MaxValue, 56).ToArray(); var written = new bool[56];
            var next = at; var stopped = false;
            while (next <= code.Length - 10 && next - at < 1024)
            {
                if (IsFrameStore(code, next, out var offset, out var payload))
                {
                    var index = offset - keyboardBase;
                    if (index < 0 || index >= values.Length)
                        throw new NotSupportedException("Owned input initialization reaches another device lane.");
                    if (written[index]) throw new InvalidDataException("Owned input initialization overwrites a byte lane.");
                    written[index] = true; values[index] = payload; next += 10;
                }
                else if (code[next] == 0x83 && code[next + 1] == 0x7d && code[next + 2] == 0x08 &&
                    (code[next + 4] == 0x74 || code[next + 4] == 0x75))
                {
                    if (code[next + 3] == 2) { stopped = true; break; }
                    next += 6;
                }
                else break;
            }
            if (!stopped || !written.Take(28).Any(value => value) || !written.Skip(28).Any(value => value))
                throw new NotSupportedException("Owned input defaults are incomplete.");
            if (bindings is not null) throw new InvalidDataException("Owned input constructor is ambiguous.");
            bindings = values;
        }
        return bindings;
    }

    // mov r32, [ebp-disp8] followed by mov byte ptr [r32+disp32], imm8.
    private static bool IsFrameStore(ReadOnlySpan<byte> code, int at, out int offset, out byte payload)
    {
        offset = 0; payload = 0;
        if (code[at] != 0x8b || (code[at + 1] & 0xc7) != 0x45 || code[at + 3] != 0xc6) return false;
        var register = (code[at + 1] >> 3) & 7;
        if (code[at + 4] != (0x80 | register) || register == 4 || register == 5) return false;
        offset = BinaryPrimitives.ReadInt32LittleEndian(code[(at + 5)..]);
        payload = code[at + 9];
        return true;
    }
}
