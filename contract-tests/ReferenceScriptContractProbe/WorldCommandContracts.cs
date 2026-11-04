using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using OpenNV.Runtime.Content;
using OpenNV.Runtime.Gameplay.State;
using OpenNV.Runtime.World.Cells;

// Item condition, typed removal, CELL ownership, quest reset, challenge
// recurrence, trigger admission and rotation commands from source scripts.
internal static class WorldCommandContracts
{
    internal static void Run()
    {
        var directory = Directory.CreateTempSubdirectory("opennv-world-commands-");
        try
        {
            var header = new byte[12]; BinaryPrimitives.WriteSingleLittleEndian(header, 1.34f);
            var weaponData = new byte[15]; BinaryPrimitives.WriteInt32LittleEndian(weaponData.AsSpan(4), 100);
            var weaponStats = new byte[196]; BinaryPrimitives.WriteUInt32LittleEndian(weaponStats, 3);
            BinaryPrimitives.WriteSingleLittleEndian(weaponStats.AsSpan(4), 1); BinaryPrimitives.WriteSingleLittleEndian(weaponStats.AsSpan(60), 1);
            var biped = new byte[8]; BinaryPrimitives.WriteUInt32LittleEndian(biped, 1u << 2);
            var challenge = new byte[24]; BinaryPrimitives.WriteUInt32LittleEndian(challenge, 13);
            BinaryPrimitives.WriteInt32LittleEndian(challenge.AsSpan(4), 1); BinaryPrimitives.WriteUInt32LittleEndian(challenge.AsSpan(8), 2);
            File.WriteAllBytes(Path.Combine(directory.FullName, "World.esm"), Join(
                Record("TES4", 0, Field("HEDR", header)),
                Record("WEAP", 1, Field("EDID", Text("Gun")), Field("DATA", weaponData), Field("DNAM", weaponStats), Field("ETYP", BitConverter.GetBytes(2))),
                Record("MISC", 2, Field("EDID", Text("Loot")), Field("DATA", new byte[8])),
                Record("ARMO", 5, Field("EDID", Text("Suit")), Field("DATA", new byte[12]), Field("BMDT", biped)),
                Record("ARMO", 6, Field("EDID", Text("Kept")), Field("DATA", new byte[12]), Field("BMDT", biped)),
                Record("FLST", 0x710, Field("EDID", Text("KeptList")), Field("LNAM", BitConverter.GetBytes(6u))),
                Record("NPC_", 7, Field("ACBS", new byte[24])),
                Record("FACT", 0x700, Field("EDID", Text("Owners"))),
                Record("CHAL", 0x300, Field("EDID", Text("Chal")), Field("FULL", Text("Chal")), Field("DESC", Text("Desc")), Field("DATA", challenge)),
                Record("QUST", 0x601, Field("EDID", Text("ResetMe")), Field("DATA", new byte[8])),
                Record("NPC_", 0x110, Field("ACBS", new byte[24])), Record("CONT", 0x112), Record("ACTI", 0x111),
                Record("CELL", 0x800, Field("EDID", Text("TestCell")), Field("DATA", [1])), Group(0x800,
                    Join(Reference("ACHR", 0x900, 0x110, "ActorRef"), Reference("REFR", 0x901, 0x112, "ChestRef"),
                        Reference("REFR", 0x903, 0x111, "BoxRef"))),
                Record("QUST", 0x600, [Field("EDID", Text("WorldQuest")), .. new uint[] { 0x14, 1, 2, 5, 6, 0x710, 0x700, 0x300, 0x601, 0x800, 0x900, 0x901, 0x903 }
                    .Select(id => Field("SCRO", BitConverter.GetBytes(id)))])));
            using var records = FalloutPluginStack.Load(directory.FullName, ["World.esm"]);
            using var world = new FalloutReferenceWorld(records);
            world.LoadCell(FalloutCellSceneReader.Read(records, Key(0x800)));
            var player = new FalloutPlayerInventory(5);
            world.BindPlayerInventory(player);
            var quests = new FalloutQuestState(records);
            var challenges = new FalloutChallenges(records, new());
            var executor = new FalloutReferenceScripts(records, world, quests, new((_, _) => false,
                _ => throw new InvalidDataException("World command used an unrelated host effect."),
                Inventory: new FalloutInventoryCommands(records, world, player, () => 1), Challenges: challenges));
            quests.EnterStage(Key(0x601), 10);
            var quest = records.GetEffective(Key(0x600));
            executor.ExecuteStage(quest, quest.ReadSubrecords().ToArray(), string.Join('\n',
                "ActorRef.AddItemHealthPercent Gun 1 0.35", "ActorRef.AddItemHealthPercent Suit 1 0.5",
                "ActorRef.AddItemHealthPercent Kept 1 1", "ActorRef.AddItemHealthPercent Loot 2 0.5",
                "ActorRef.RemoveAllTypedItems ChestRef 0 1 24 KeptList",
                "SetCellOwnership TestCell Owners", "SetCellPublicFlag TestCell 1", "ResetQuest ResetMe", "RemoveRecurringFromChallenge Chal",
                "PurgeCellBuffers", "SetSceneIsComplex 1", "BoxRef.EnterTrigger player", "BoxRef.TrapUpdate",
                "ActorRef.SetAngle Z 90"));
            var actor = world.Inventory(Key(0x900), 1).Contents;
            var chest = world.Inventory(Key(0x901), 1).Contents;
            Require(actor.Item(Key(1))?.Variants?.Single().Condition == .35f && actor.Item(Key(2)) is { Count: 2, Variants: null or [{ Condition: null }] },
                "AddItemHealthPercent lost weapon condition or conditioned a non-equipment item.");
            Require(chest.Item(Key(5))?.Variants?.Single().Condition == .5f && actor.Item(Key(5)) is null &&
                actor.Item(Key(6)) is { Variants: null or [{ Condition: null }] } && actor.Item(Key(2)) is not null && chest.Item(Key(6)) is null,
                "RemoveAllTypedItems ignored its type code, exemption list or the item's condition.");
            Require(world.Ownership(Key(0x901)).Owner == Key(0x700), "SetCellOwnership did not reach inherited reference ownership.");
            Require(world.IsPublicCell(Key(0x800)) && world.HasUnownedCell(Key(0x900), null, 1), "SetCellPublicFlag did not make its owned cell trespass-free.");
            executor.ExecuteStage(quest, quest.ReadSubrecords().ToArray(), "SetPublic TestCell 0");
            Require(!world.IsPublicCell(Key(0x800)) && !world.HasUnownedCell(Key(0x900), null, 1), "SetPublic did not clear the public flag.");
            executor.ExecuteStage(quest, quest.ReadSubrecords().ToArray(), "SetCellPublicFlag TestCell 1");
            Require(!quests.StageDone(Key(0x601), 10) && quests.Stage(Key(0x601)) == 0, "ResetQuest retained entered stages.");
            challenges.IncrementScripted(Key(0x300));
            Require(challenges.State(Key(0x300)) is { Completed: true, RecurringRemoved: true }, "Challenge stayed recurring.");
            Require(world.ForcedTriggers.Take(Key(0x903), false) is { Name: "OnTriggerEnter" } trigger && trigger.ActionReference == Key(0x14) &&
                !world.ForcedTriggers.HasPending(Key(0x903)), "EnterTrigger did not admit one trigger-enter event for its actor.");
            Require(Math.Abs(world.Placement(Key(0x900)).RotationRadians[2] - MathF.PI / 2) < 1e-5, "SetAngle did not rotate the reference.");
            world.RotateAxis(Key(0x900), 2, 90, .5); world.RotateAxis(Key(0x900), 2, -360, 0);
            Require(Math.Abs(world.Placement(Key(0x900)).RotationRadians[2] - MathF.PI * .75f) < 1e-5, "Rotate did not turn the reference by speed times frame time.");
            using var cold = new FalloutReferenceWorld(records);
            cold.RestoreCellOwnership(JsonSerializer.Deserialize<FalloutCellOwnershipSnapshot>(JsonSerializer.Serialize(world.CaptureCellOwnership())));
            cold.Restore(world.Capture());
            cold.LoadCell(FalloutCellSceneReader.Read(records, Key(0x800)));
            Require(cold.Ownership(Key(0x901)).Owner == Key(0x700) && cold.IsPublicCell(Key(0x800)), "CELL ownership or public flag did not survive save restoration.");
            var restoredChallenges = new FalloutChallenges(records, new());
            restoredChallenges.Restore(challenges.Capture());
            Require(restoredChallenges.State(Key(0x300)).RecurringRemoved, "Challenge recurrence removal did not survive restoration.");
            Reject(() => executor.ExecuteStage(quest, quest.ReadSubrecords().ToArray(), "ActorRef.RemoveAllTypedItems 0 1"));
            Console.WriteLine("OPENNV_WORLD_COMMAND_CONTRACT_PASS item-condition typed-removal cell-ownership cell-public quest-reset challenge-recurrence trigger-enter set-angle rotate cold");
        }
        finally { directory.Delete(true); }
    }

    private static FalloutFormKey Key(uint id) => new("World.esm", id);
    private static void Require(bool value, string error) { if (!value) throw new InvalidDataException(error); }
    private static void Reject(Action action)
    {
        try { action(); }
        catch (Exception error) when (error is InvalidDataException or NotSupportedException) { return; }
        throw new InvalidDataException("Malformed world command was accepted.");
    }
    private static byte[] Reference(string type, uint id, uint form, string name) => Record(type, id,
        Field("NAME", BitConverter.GetBytes(form)), Field("DATA", new byte[24]), Field("EDID", Text(name)));
    private static byte[] Group(uint cell, byte[] data)
    {
        var bytes = new byte[24 + data.Length]; Encoding.ASCII.GetBytes("GRUP").CopyTo(bytes, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), (uint)bytes.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), cell); BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), 6); data.CopyTo(bytes, 24); return bytes;
    }
    private static byte[] Record(string name, uint id, params byte[][] fields)
    {
        var data = Join(fields); var bytes = new byte[24 + data.Length]; Encoding.ASCII.GetBytes(name).CopyTo(bytes, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), (uint)data.Length); BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), id); data.CopyTo(bytes, 24); return bytes;
    }
    private static byte[] Field(string name, byte[] data)
    {
        var bytes = new byte[6 + data.Length]; Encoding.ASCII.GetBytes(name).CopyTo(bytes, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4), (ushort)data.Length); data.CopyTo(bytes, 6); return bytes;
    }
    private static byte[] Text(string text) => Encoding.ASCII.GetBytes(text + '\0');
    private static byte[] Join(params byte[][] fields) => fields.SelectMany(field => field).ToArray();
}
