using System.Buffers.Binary;
using System.Text;
using OpenNV.Runtime.Content;
using OpenNV.Runtime.World.Cells;
using Function = OpenNV.Runtime.Content.Definitions.FalloutConditionFunction;

// Actor value read kinds, talking activators, ownership, factions, teammates,
// destruction, existence and casino stages gate perks, dialogue and packages.
internal static class ConditionOwnerContracts
{
    internal static void Run()
    {
        var directory = Directory.CreateTempSubdirectory("opennv-condition-owners-");
        try
        {
            File.WriteAllBytes(Path.Combine(directory.FullName, "Owners.esm"), Join(Record("TES4", 0, Field("HEDR", new byte[12])),
                Record("FACT", 0x30, Field("XNAM", Join(U32(0x31), U32(0), U32(1))), Field("DATA", [0])), Record("FACT", 0x31, Field("DATA", [0])),
                Actor(0x20, 0x30, 0), Actor(0x21, 0x31, 0), Record("NPC_", 7, Field("ACBS", new byte[24]), Field("AIDT", new byte[20])), Record("STAT", 0x50), Record("TACT", 0x51), Record("CSNO", 0x40),
                Record("QUST", 0x60, Field("DATA", [1, 0])),
                Record("CELL", 0x80, Field("DATA", [1])), Group(0x80,
                    Reference("ACHR", 0x90, 0x20), Reference("ACHR", 0x91, 0x21),
                    Record("REFR", 0x92, Field("NAME", U32(0x50)), Field("XOWN", U32(0x30)), Field("DATA", new byte[24])),
                    Reference("REFR", 0x93, 0x51))));
            using var records = FalloutPluginStack.Load(directory.FullName, ["Owners.esm"]);
            FalloutFormKey Key(uint id) => new("Owners.esm", id);
            using var world = new FalloutReferenceWorld(records);
            world.LoadCell(FalloutCellSceneReader.Read(records, Key(0x80)));
            var player = records.RuntimeFormKey(0x14);
            var quest = records.GetEffective(Key(0x60));
            var runtime = new FalloutConditionRuntime(records, new(records), world)
            {
                TalkingThroughActivator = subject => subject == Key(0x90) ? true : null,
            };
            float Query(Function function, FalloutFormKey caller, uint argument1 = 0, uint argument2 = 0) =>
                runtime.Evaluate(new FalloutCondition(quest, 0, 1, (ushort)function, argument1, argument2, 0, 0), new(caller));

            world.ChangeActorValue(Key(0x90), "Confidence", "setav", 3);
            world.ChangeActorValue(Key(0x90), "Confidence", "modav", -1);
            Check(Query(Function.GetBaseActorValue, Key(0x90), 1) == 3 && Query(Function.GetPermanentActorValue, Key(0x90), 1) == 2 &&
                Query(Function.GetActorValue, Key(0x90), 1) == 2, "Base/permanent actor value reads did not share the actor pools.");
            var read = new FalloutConditionRuntime(records, new(records), world) { ActorValueRead = (_, value, kind) => value * 10 + (int)kind };
            Check(read.Evaluate(new FalloutCondition(quest, 0, 1, (ushort)Function.GetPermanentActorValue, 5, 0, 0, 0), new(player)) == 51,
                "Permanent actor value bypassed its live owner.");

            Check(Query(Function.IsActorTalkingThroughActivator, Key(0x90)) == 1 && Query(Function.IsActorTalkingThroughActivator, Key(0x93)) == 0,
                "Talking-activator speech ignored the conversation voice or answered for a non-actor.");
            Reject(() => Query(Function.IsActorTalkingThroughActivator, Key(0x91)));

            Check(Query(Function.IsOwner, Key(0x92), 0x30) == 1 && Query(Function.IsOwner, Key(0x92), 0x31) == 0 &&
                Query(Function.IsOwner, Key(0x92)) == 0, "IsOwner ignored the reference owner.");
            Reject(() => Query(Function.IsOwner, Key(0x92), 0x50));

            Check(Query(Function.GetFactionRelation, Key(0x90), 0x91) == 1 && Query(Function.GetFactionRelation, Key(0x90)) == 0 &&
                Query(Function.GetFactionRelation, Key(0x90), 0x91) == world.ActorRelation(Key(0x90), Key(0x91)),
                "GetFactionRelation diverged from combat's actor relation.");
            Check(Query(Function.GetFactionCombatReaction, Key(0x90), 0x30, 0x31) == 1 &&
                Query(Function.GetFactionCombatReaction, Key(0x90), 0x31, 0x30) == 0, "Faction combat reaction lost its direction.");
            Check(Query(Function.GetFactionRankDifference, Key(0x90), 0x30, 0x91) == 1, "Faction rank difference ignored a non-member.");

            Check(Query(Function.GetPlayerTeammate, Key(0x90)) == 0 && Query(Function.GetPlayerTeammateCount, player) == 0,
                "A teammate was invented.");
            world.SetPlayerTeammate(Key(0x90), true);
            Check(Query(Function.GetPlayerTeammate, Key(0x90)) == 1 && Query(Function.GetPlayerTeammate, player) == 0 &&
                Query(Function.GetPlayerTeammateCount, player) == 1, "Teammate state diverged from SetPlayerTeammate.");

            Check(Query(Function.GetDestroyed, Key(0x92)) == 0 && Query(Function.GetDestroyed, player) == 0 &&
                Query(Function.GetDestructionStage, Key(0x92)) == -1, "Destruction queries invented destructible state.");
            Check(Query(Function.Exists, Key(0x90), 0x92) == 1 && Query(Function.Exists, Key(0x90)) == 0 &&
                Query(Function.Exists, Key(0x90), 0x50) == 0, "Exists accepted a null or non-reference form.");
            Check(Query(Function.IsCombatTarget, Key(0x90), 0x91) == 0, "An idle actor reported a combat target.");
            Check(Query(Function.GetActorFactionPlayerEnemy, Key(0x90)) == 0, "A player enemy faction was invented.");
            world.SetPlayerEnemyOfFaction(Key(0x30), true);
            Check(Query(Function.GetActorFactionPlayerEnemy, Key(0x90)) == 1 && Query(Function.GetActorFactionPlayerEnemy, Key(0x91)) == 0 &&
                Query(Function.GetActorFactionPlayerEnemy, player) == 0, "Actor player-enemy state ignored its faction memberships.");

            world.CasinoWinnings.SetLevel(Key(0x40), 2);
            Check(Query(Function.GetCasinoWinningStage, player, 0x40) == 2, "Casino stage diverged from the script winnings level.");
            Reject(() => Query(Function.GetCasinoWinningStage, player, 0x50));
            Reject(() => new FalloutConditionRuntime(records, new(records)).Evaluate(
                new FalloutCondition(quest, 0, 1, (ushort)Function.GetPlayerTeammateCount, 0, 0, 0, 0), new(player)));
            Console.WriteLine("OPENNV_CONDITION_OWNER_PASS actorValueReads=true talkingActivator=true owner=true factions=true teammates=true destruction=true exists=true casino=true");
        }
        finally { directory.Delete(true); }
    }

    private static void Check(bool value, string message) { if (!value) throw new InvalidDataException(message); }
    private static void Reject(Func<float> action)
    {
        try { _ = action(); } catch (Exception error) when (error is InvalidDataException or NotSupportedException) { return; }
        throw new InvalidDataException("Unowned condition input was accepted.");
    }
    private static byte[] Actor(uint id, uint faction, sbyte rank) => Record("NPC_", id, Field("ACBS", new byte[24]),
        Field("AIDT", new byte[20]), Field("SNAM", Join(U32(faction), [unchecked((byte)rank), 0, 0, 0])));
    private static byte[] Reference(string signature, uint id, uint form) =>
        Record(signature, id, Field("NAME", U32(form)), Field("DATA", new byte[24]));
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
    private static byte[] Group(uint cell, params byte[][] rows)
    {
        var data = Join(rows); var bytes = new byte[24 + data.Length]; Encoding.ASCII.GetBytes("GRUP").CopyTo(bytes, 0);
        U32((uint)bytes.Length).CopyTo(bytes, 4); U32(cell).CopyTo(bytes, 8); U32(6).CopyTo(bytes, 12); data.CopyTo(bytes, 24); return bytes;
    }
}
