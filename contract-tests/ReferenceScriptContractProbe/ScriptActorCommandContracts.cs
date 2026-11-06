using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using OpenNV.Runtime.Content;
using OpenNV.Runtime.Gameplay.State;
using OpenNV.Runtime.World.Cells;

internal static class ScriptActorCommandContracts
{
    internal static void Run()
    {
        var directory = Directory.CreateTempSubdirectory("opennv-script-actor-commands-");
        try
        {
            File.WriteAllBytes(Path.Combine(directory.FullName, "ScriptActors.esm"), Join(Header(),
                Faction(0x10, "EnemyFaction"), Faction(0x11, "OtherFaction"),
                Actor(7, 0x11), Actor(0x20, 0x10), Actor(0x21, 0x11),
                Record("GMST", 0x30, Field("EDID", Text("fSneakMaxDistance")), Field("DATA", BitConverter.GetBytes(2500f))),
                Setting(0x31, "fKnockbackAgilBase", 1), Setting(0x32, "fKnockbackAgilMult", -.008f),
                Setting(0x33, "fKnockbackDamageBase", 50), Setting(0x34, "fKnockbackDamageMult", 10),
                Record("ACTI", 0x40, Field("EDID", Text("Crate")), Field("DEST", [10, 0, 0, 0, 0, 1, 0, 0])),
                Record("SCPT", 0x50, Field("SCHR", new byte[20]), Field("SCRO", U32(0x14)), Field("SCRO", U32(0x10)), Field("SCRO", U32(0x93)),
                    Field("SCTX", Text("begin GameMode\nend"))),
                Record("QUST", 0x60, Field("DATA", [1, 0]), Field("SCRI", U32(0x51))),
                Record("SCPT", 0x51, Field("SCHR", QuestHeader()), Field("SCRO", U32(0x91)), Field("SCRO", U32(0x10)),
                    Field("SCTX", Text("begin GameMode\nActorRef.SetGhost 0\nSetPCEnemyOfFaction EnemyFaction 1\nend"))),
                Record("CELL", 0x80, Field("DATA", [1])), Group(0x80,
                    Reference("ACHR", 0x91, 0x20, "ActorRef"), Reference("ACHR", 0x92, 0x21), Reference("ACHR", 0x93, 0x20, "ThirdRef"), Reference("REFR", 0x94, 0x40))));
            using var records = FalloutPluginStack.Load(directory.FullName, ["ScriptActors.esm"]);
            var player = records.RuntimeFormKey(0x14);
            using var world = new FalloutReferenceWorld(records);
            world.LoadCell(FalloutCellSceneReader.Read(records, Key(0x80)));
            var scripts = new FalloutReferenceScripts(records, world, new(records), new((_, _) => false,
                _ => throw new InvalidOperationException("Actor command invented a presentation effect.")));
            void Execute(uint caller, string command) => scripts.ExecuteProgram(records.GetEffective(Key(caller)),
                records.GetEffective(Key(0x50)), FalloutGameModeProgram.Read($"begin GameMode\n{command}\nend"), 0);

            Execute(0x91, "SetGhost 1");
            Require(world.IsGhost(Key(0x91)) && !world.IsGhost(Key(0x93)), "SetGhost missed its caller or crossed references.");
            Execute(0x91, "SetPCEnemyOfFaction EnemyFaction 1");
            Require(world.IsPlayerEnemyOfFaction(Key(0x10)) && !world.IsPlayerEnemyOfFaction(Key(0x11)) &&
                world.ActorRelation(Key(0x91), player) == 1 && world.ActorRelation(player, Key(0x93)) == 1 &&
                world.ActorRelation(Key(0x92), player) == 0, "Player-enemy faction flag did not drive member relations only.");
            Execute(0x92, "SendAssaultAlarm Player EnemyFaction");
            Require(world.Get(Key(0x91)).Engagement?.Target == player && world.Get(Key(0x93)).Engagement?.Target == player &&
                world.Get(Key(0x92)).Engagement?.Target == player, "Assault alarm missed the victim or its faction responders.");
            Execute(0x93, "RemoveFromAllFactions");
            Require(world.ActorFactions(Key(0x93))[Key(0x10)] == -1 && world.ActorRelation(Key(0x93), player) == 0,
                "RemoveFromAllFactions retained an active membership.");

            var overrides = JsonSerializer.Deserialize<FalloutActorOverrides[]>(JsonSerializer.Serialize(world.CaptureActorOverrides()))!;
            using var cold = new FalloutReferenceWorld(records);
            cold.LoadCell(FalloutCellSceneReader.Read(records, Key(0x80)));
            cold.RestoreActorOverrides(overrides);
            Require(cold.IsGhost(Key(0x91)) && cold.IsPlayerEnemyOfFaction(Key(0x10)) && cold.ActorRelation(Key(0x91), player) == 1,
                "Cold restoration lost ghost or player-enemy faction state.");
            Reject(() => cold.RestoreActorOverrides(overrides.Select(value => value.PlayerEnemyFactions is null ? value
                : value with { PlayerEnemyFactions = value.PlayerEnemyFactions.Select(item => item with { Value = 2 }).ToArray() }).ToArray()));
            Reject(() => cold.RestoreActorOverrides(overrides.Select(value => value.Target != player ? value
                : value with { Target = Key(0x91) }).ToArray()));

            Reject(() => Execute(0x91, "SetGhost"));
            Reject(() => Execute(0x91, "SetGhost 1 1"));
            Reject(() => Execute(0x91, "ClearFactionPlayerEnemyFlag Player"));
            Require(world.IsGhost(Key(0x91)), "Malformed SetGhost changed its flag.");
            Execute(0x91, "ClearFactionPlayerEnemyFlag EnemyFaction");
            Require(!world.IsPlayerEnemyOfFaction(Key(0x10)), "ClearFactionPlayerEnemyFlag kept the flag.");
            Require(world.DetectsTarget(Key(0x91), player, null) && !world.DetectsTarget(Key(0x93), Key(0x92), null) &&
                world.DetectsTarget(Key(0x93), Key(0x92), (_, _) => 1), "GetDetected ignored engagement or detection distance.");

            Execute(0x91, "PushActorAway ThirdRef 5");
            var pushes = new List<(System.Numerics.Vector3? Origin, int Force)>();
            world.Get(Key(0x93)).ScriptPushAway = (origin, force) => pushes.Add((origin, force));
            world.PushPlayer = (origin, force) => pushes.Add((origin, force));
            Execute(0x91, "PushActorAway ThirdRef -2.5");
            Execute(0x91, "PushActorAway Player 5");
            Reject(() => Execute(0x91, "PushActorAway ThirdRef"));
            Require(pushes is [{ Origin: { } origin, Force: -2 }, { Origin: { } playerOrigin, Force: 5 }] &&
                origin == System.Numerics.Vector3.Zero && playerOrigin == origin,
                "PushActorAway lost its resident target, the player, caller origin or integer force.");
            // Agility 5, Force 10: (1 - .008 * 50) * (50 + 100) = 90. Only Force <= -6 reverses.
            Require(MathF.Abs(FalloutHitKnockback.Force(records, 5, 10) - 90) < .001f &&
                FalloutHitKnockback.Force(records, 5, -2) > 0 && FalloutHitKnockback.Force(records, 5, -6) < 0,
                "PushActorAway did not use the Agility knockback force.");

            Execute(0x92, "ForceFlee");
            var fled = world.Get(Key(0x92)).Engagement!;
            fled.Validate();
            Require(fled is { Action: "flee", ForcedFlee: true } && fled.Target == player && fled.ForConfidence(4).Action == "flee" &&
                world.Get(Key(0x91)).Engagement?.ForcedFlee != true, "ForceFlee did not hold its caller's flee procedure.");
            Reject(() => (fled with { Action = "pursue" }).Validate());
            Execute(0x91, "Player.ForceFlee");
            Require(world.Get(Key(0x91)).Engagement?.ForcedFlee != true, "A player ForceFlee caller changed an actor.");

            Execute(0x93, "SetCriticalStage GooEnd");
            Execute(0x93, "if IsInCriticalStage GooEnd\nSetGhost 1\nendif\nif IsInCriticalStage DisintegrateEnd\nSetGhost 0\nendif");
            Execute(0x94, "SetCriticalStage DisintegrateStart");
            Reject(() => Execute(0x93, "SetCriticalStage Melted"));
            Require(world.CriticalStage(Key(0x93)) == FalloutCriticalStage.GooEnd && world.IsGhost(Key(0x93)) &&
                world.CriticalStage(Key(0x94)) == FalloutCriticalStage.None && world.CriticalStage(Key(0x92)) == FalloutCriticalStage.None,
                "SetCriticalStage/IsInCriticalStage lost the actor stage or staged a non-actor.");
            var staged = JsonSerializer.Deserialize<FalloutActorOverrides[]>(JsonSerializer.Serialize(world.CaptureActorOverrides()))!;
            using var stagedCold = new FalloutReferenceWorld(records);
            stagedCold.LoadCell(FalloutCellSceneReader.Read(records, Key(0x80)));
            stagedCold.RestoreActorOverrides(staged);
            Require(stagedCold.CriticalStage(Key(0x93)) == FalloutCriticalStage.GooEnd, "Critical stage did not survive restoration.");
            Reject(() => stagedCold.RestoreActorOverrides(staged.Select(value => value with { CriticalStage = 5 }).ToArray()));

            Require(world.IsVatsTargetable(Key(0x94)) && !world.IsVatsTargetable(Key(0x93)), "DEST VATS Targetable was not the default.");
            Execute(0x94, "SetVATSTarget 0");
            Execute(0x93, "SetVATSTarget 0");
            var hidden = world.Get(Key(0x94)).Capture();
            Require(!world.IsVatsTargetable(Key(0x94)) && hidden.VatsTargetInverted && !world.Get(Key(0x93)).Capture().VatsTargetInverted,
                "SetVATSTarget did not store an inverted bit on its destructible only.");
            Execute(0x94, "SetVATSTarget 1");
            Require(world.IsVatsTargetable(Key(0x94)) && !world.Get(Key(0x94)).Capture().VatsTargetInverted,
                "SetVATSTarget matching the base flag kept an inverted bit.");
            Execute(0x91, "FireWeapon EnemyFaction");

            var quests = new FalloutQuestState(records);
            var questScripts = new FalloutQuestScripts(records, quests, new HashSet<FalloutFormKey>(), new(),
                defaultProcessingDelay: 0, references: world);
            questScripts.Advance(0.1);
            Require(questScripts.Capture().Instances.Single().Error is null && !world.IsGhost(Key(0x91)) &&
                world.IsPlayerEnemyOfFaction(Key(0x10)), "Quest-scoped actor commands lost their explicit or global owner.");
            Console.WriteLine("OPENNV_SCRIPT_ACTOR_COMMANDS_CONTRACT_PASS ghost=true playerEnemyFaction=true assaultAlarm=true " +
                "removeFromAllFactions=true cold=true invalidRestoreRefused=true detected=true pushActorAway=agility-force forceFlee=true playerForceFlee=noop " +
                "criticalStage=true vatsTarget=inverted-bit fireWeapon=validated quest=true");
        }
        finally { directory.Delete(true); }
    }

    private static FalloutFormKey Key(uint id) => new("ScriptActors.esm", id);
    private static byte[] Faction(uint id, string name) => Record("FACT", id, Field("EDID", Text(name)), Field("DATA", new byte[4]));
    private static byte[] Actor(uint id, uint faction) => Record("NPC_", id, Field("ACBS", new byte[24]),
        Field("AIDT", new byte[20]), Field("SNAM", Join(U32(faction), new byte[4])));
    private static byte[] Reference(string signature, uint id, uint form, string? name = null) => Record(signature, id,
        name is null ? [] : Field("EDID", Text(name)), Field("NAME", U32(form)), Field("DATA", new byte[24]));
    private static byte[] Setting(uint id, string name, float value) =>
        Record("GMST", id, Field("EDID", Text(name)), Field("DATA", BitConverter.GetBytes(value)));
    private static byte[] Header() => Record("TES4", 0, Field("HEDR", new byte[12]));
    private static byte[] QuestHeader() { var bytes = new byte[20]; bytes[16] = 1; return bytes; }
    private static byte[] Group(uint cell, params byte[][] records)
    {
        var data = Join(records); var bytes = new byte[24 + data.Length]; Encoding.ASCII.GetBytes("GRUP").CopyTo(bytes, 0);
        U32((uint)bytes.Length).CopyTo(bytes, 4); U32(cell).CopyTo(bytes, 8); U32(6).CopyTo(bytes, 12); data.CopyTo(bytes, 24); return bytes;
    }
    private static byte[] Record(string signature, uint id, params byte[][] fields)
    {
        var data = Join(fields); var bytes = new byte[24 + data.Length]; Encoding.ASCII.GetBytes(signature).CopyTo(bytes, 0);
        U32((uint)data.Length).CopyTo(bytes, 4); U32(id).CopyTo(bytes, 12); data.CopyTo(bytes, 24); return bytes;
    }
    private static byte[] Field(string signature, byte[] data)
    {
        var bytes = new byte[6 + data.Length]; Encoding.ASCII.GetBytes(signature).CopyTo(bytes, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4), checked((ushort)data.Length)); data.CopyTo(bytes, 6); return bytes;
    }
    private static byte[] Text(string value) => Encoding.ASCII.GetBytes(value + '\0');
    private static byte[] U32(uint value) => BitConverter.GetBytes(value);
    private static byte[] Join(params byte[][] values) => values.SelectMany(value => value).ToArray();
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Reject(Action action)
    {
        try { action(); } catch (Exception error) when (error is InvalidDataException or NotSupportedException or KeyNotFoundException) { return; }
        throw new InvalidOperationException("Invalid actor command state was accepted.");
    }
}
