using System.Buffers.Binary;
using System.Text;
using OpenNV.Runtime.Content;
using OpenNV.Runtime.World.Cells;
using Function = OpenNV.Runtime.Content.Definitions.FalloutConditionFunction;

// GetInWorldspace, GetInCellParam, GetIsCreature and GetIsCreatureType gate
// quest objective targets (worldspace-specific markers) and dialogue lines.
internal static class WorldspaceQueryContracts
{
    internal static void Run()
    {
        var directory = Directory.CreateTempSubdirectory("opennv-worldspace-");
        try
        {
            var creatureData = new byte[17]; creatureData[0] = 6; // Robot
            File.WriteAllBytes(Path.Combine(directory.FullName, "Worlds.esm"), Join(Record("TES4", 0, Field("HEDR", new byte[12])),
                Record("NPC_", 2), Record("CREA", 3, Field("ACBS", new byte[24]), Field("DATA", creatureData)), Record("STAT", 4),
                Record("QUST", 0x60, Field("DATA", [1, 0])),
                Record("WRLD", 0x82), WorldGroup(0x82,
                    Record("CELL", 0x83, Field("DATA", [0]), Field("XCLC", new byte[8])), Group(0x83,
                        Record("ACHR", 0x90, Field("NAME", U32(2)), Field("DATA", new byte[24])),
                        Record("ACRE", 0x91, Field("NAME", U32(3)), Field("DATA", new byte[24])))),
                Record("WRLD", 0x84), WorldGroup(0x84,
                    Record("CELL", 0x85, Field("DATA", [0]), Field("XCLC", new byte[8]))),
                Record("CELL", 0x81, Field("EDID", Encoding.ASCII.GetBytes("Lab\0")), Field("DATA", [1])),
                Record("CELL", 0x80, Field("EDID", Encoding.ASCII.GetBytes("Vault\0")), Field("DATA", [1])), Group(0x80,
                    Record("REFR", 0x92, Field("NAME", U32(4)), Field("DATA", new byte[24])))));
            using var records = FalloutPluginStack.Load(directory.FullName, ["Worlds.esm"]);
            FalloutFormKey Key(uint id) => new("Worlds.esm", id);
            using var world = new FalloutReferenceWorld(records);
            world.LoadCell(FalloutCellSceneReader.Read(records, Key(0x83)));
            world.LoadCell(FalloutCellSceneReader.Read(records, Key(0x80)));
            var player = records.RuntimeFormKey(0x14);
            var playerPlacement = new FalloutReferencePlacement(Key(0x85), [0, 0, 0], [0, 0, 0]);
            var runtime = new FalloutConditionRuntime(records, new(records), world) { PlayerPlacement = () => playerPlacement };
            var quest = records.GetEffective(Key(0x60));
            float Query(Function function, FalloutFormKey caller, uint argument1 = 0, uint argument2 = 0) =>
                runtime.Evaluate(new FalloutCondition(quest, 0, 1, (ushort)function, argument1, argument2, 0, 0), new(caller));

            Check(Query(Function.GetInWorldspace, Key(0x90), 0x82) == 1 && Query(Function.GetInWorldspace, Key(0x90), 0x84) == 0,
                "Exterior actor lost its parent WRLD.");
            Check(Query(Function.GetInWorldspace, Key(0x92), 0x82) == 0, "Interior reference inherited a worldspace.");
            Check(Query(Function.GetInWorldspace, player, 0x84) == 1 && Query(Function.GetInWorldspace, player, 0x82) == 0,
                "Player worldspace ignored the live player cell.");
            world.SetPlacement(Key(0x90), new(Key(0x85), [0, 0, 0], [0, 0, 0]));
            Check(Query(Function.GetInWorldspace, Key(0x90), 0x84) == 1, "Moved actor retained its source worldspace.");
            Reject(() => Query(Function.GetInWorldspace, Key(0x90), 0x83));

            // GetInCellParam shares GetInCell's interior EDID-prefix semantics,
            // applied to its second operand rather than the caller.
            Check(Query(Function.GetInCellParam, Key(0x90), 0x80, 0x92) == 1 && Query(Function.GetInCellParam, Key(0x92), 0x80, 0x90) == 0 &&
                Query(Function.GetInCellParam, Key(0x90), 0x81, 0x92) == 0, "GetInCellParam ignored its located reference operand.");
            playerPlacement = playerPlacement with { Cell = Key(0x80) };
            Check(Query(Function.GetInCellParam, Key(0x92), 0x80, 0x14) == 1, "GetInCellParam lost the live player cell.");
            playerPlacement = playerPlacement with { Cell = Key(0x85) };
            Reject(() => Query(Function.GetInCellParam, Key(0x92), 0x82, 0x90));

            Check(Query(Function.GetIsCreature, Key(0x91)) == 1 && Query(Function.GetIsCreature, Key(0x90)) == 0 &&
                Query(Function.GetIsCreature, player) == 0, "Creature identity used something other than the actor base type.");
            Check(Query(Function.GetIsCreatureType, Key(0x91), 6) == 1 && Query(Function.GetIsCreatureType, Key(0x91), 5) == 0 &&
                Query(Function.GetIsCreatureType, Key(0x90), 6) == 0, "Creature type ignored CREA DATA or matched an NPC.");

            var unowned = new FalloutConditionRuntime(records, new(records), world);
            Reject(() => unowned.Evaluate(new FalloutCondition(quest, 0, 1, (ushort)Function.GetInWorldspace, 0x84, 0, 0, 0), new(player)));
            Console.WriteLine("OPENNV_WORLDSPACE_QUERY_PASS parentWorld=true interiorNone=true livePlayer=true moved=true cellParam=true creature=true creatureType=true missingOwnerRefused=true");
        }
        finally { directory.Delete(true); }
    }

    private static void Check(bool value, string message) { if (!value) throw new InvalidDataException(message); }
    private static void Reject(Func<float> action)
    {
        try { _ = action(); } catch (Exception error) when (error is InvalidDataException or NotSupportedException) { return; }
        throw new InvalidDataException("Invalid worldspace query input was accepted.");
    }
    private static byte[] U32(uint value) => BitConverter.GetBytes(value);
    private static byte[] Join(params byte[][] values) => values.SelectMany(value => value).ToArray();
    private static byte[] Field(string name, byte[] data)
    {
        var bytes = new byte[6 + data.Length]; Encoding.ASCII.GetBytes(name).CopyTo(bytes, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4), checked((ushort)data.Length)); data.CopyTo(bytes, 6); return bytes;
    }
    private static byte[] Record(string name, uint id, params byte[][] fields)
    {
        var data = Join(fields); var bytes = new byte[24 + data.Length]; Encoding.ASCII.GetBytes(name).CopyTo(bytes, 0);
        U32((uint)data.Length).CopyTo(bytes, 4); U32(id).CopyTo(bytes, 12); data.CopyTo(bytes, 24); return bytes;
    }
    private static byte[] Group(uint cell, params byte[][] rows) => GeneralGroup(cell, 6, rows);
    private static byte[] WorldGroup(uint world, params byte[][] rows) => GeneralGroup(world, 1, rows);
    private static byte[] GeneralGroup(uint label, int kind, params byte[][] rows)
    {
        var data = Join(rows); var bytes = new byte[24 + data.Length]; Encoding.ASCII.GetBytes("GRUP").CopyTo(bytes, 0);
        U32((uint)bytes.Length).CopyTo(bytes, 4); U32(label).CopyTo(bytes, 8); U32((uint)kind).CopyTo(bytes, 12); data.CopyTo(bytes, 24); return bytes;
    }
}
