using Function = OpenNV.Runtime.Content.Definitions.FalloutConditionFunction;
using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using OpenNV.Runtime.Content;
using OpenNV.Runtime.Gameplay.State;
using OpenNV.Runtime.World.Cells;

// Run-on resolution, empty-parameter substitution, evaluation failure and the
// combat-group, effect, weather, VATS camera and object functions.
internal static class ConditionFunctionContracts
{
    private const string Master = "Conditions.esm";

    internal static void Run()
    {
        var directory = Directory.CreateTempSubdirectory("opennv-condition-functions-");
        try
        {
            File.WriteAllBytes(Path.Combine(directory.FullName, Master), Plugin());
            using var records = FalloutPluginStack.Load(directory.FullName, [Master]);
            using var world = new FalloutReferenceWorld(records);
            world.LoadCell(FalloutCellSceneReader.Read(records, Key(0x100)));
            var globals = FalloutGlobalState.Read(records);
            var package = (FalloutFormKey?)null;
            var drawn = new HashSet<FalloutFormKey>();
            var runtime = new FalloutConditionRuntime(records, new FalloutQuestState(records), world)
            {
                Globals = globals, CurrentPackage = _ => package, WeaponOut = drawn.Contains,
            };
            var owner = records.GetEffective(Key(0x300));
            FalloutCondition Query(Function function, uint argument = 0, uint runOn = 0, byte flags = 0, float comparison = 0) =>
                new(owner, flags, comparison, (ushort)function, argument, 0, runOn, 0);
            var (a, b, c, item, player) = (Key(0x200), Key(0x201), Key(0x202), Key(0x203), records.RuntimeFormKey(0x14));
            var scope = new FalloutConditionScope(a, b, world.GetLinkedRef);
            float Eval(FalloutCondition condition, FalloutConditionScope? at = null) => runtime.Evaluate(condition, at ?? scope);
            bool Passes(FalloutCondition condition, FalloutConditionScope? at = null) =>
                FalloutCondition.AllPass([condition], value => Eval(value, at), evaluateRunOn: true);

            // Framework: unmapped run-on values run on the subject; legacy bit 0x08 is Reference.
            Require(Eval(Query(Function.GetIsID, 0x20, runOn: 7)) == 1 && Eval(Query(Function.GetIsID, 0x20, runOn: 20)) == 1 &&
                Query(Function.GetIsID, runOn: 1, flags: 8).SubjectRunOn == 2,
                "Unmapped or legacy run-on values did not follow the subject rules.");
            // Combat Target: no engagement leaves no subject, so every operator fails.
            var onTarget = Query(Function.GetIsID, 0x21, runOn: 3);
            Require(FalloutCondition.Failed(Eval(onTarget)) && !Passes(onTarget) && !Passes(onTarget with { Flags = 1 << 5 }),
                "A missing combat target did not make the condition false for every operator.");
            Require(FalloutCondition.Failed(Eval(Query(Function.GetIsID, 0x20, runOn: 4))), "A missing linked reference called the function.");
            Require(FalloutCondition.Failed(Eval(onTarget, new(item, b))), "A non-actor primary supplied a combat target.");
            world.Get(a).Engagement = new(b);
            world.Get(c).Engagement = new(b);
            Require(Eval(onTarget) == 1, "Run-on Combat Target did not resolve the primary's engagement target.");

            // Combat groups: A and C fight B.
            Require(Eval(Query(Function.GetGroupMemberCount)) == 2 && Eval(Query(Function.GetGroupTargetCount)) == 1 &&
                Eval(Query(Function.GetGroupMemberCount, runOn: 3)) == 1 && Eval(Query(Function.GetGroupTargetCount, runOn: 3)) == 2 &&
                Eval(Query(Function.GetGroupMemberCount), new(item, b)) == 0,
                "Combat group membership or target counts diverged from the live engagements.");
            Require(Eval(Query(Function.GetShouldAttack, 0x201), new(a)) == 100 &&
                Eval(Query(Function.GetShouldAttack, 0x200), new(b)) == 0 && Eval(Query(Function.GetShouldAttack, 0x203), new(b)) == 0,
                "GetShouldAttack ignored aggression or accepted a non-actor parameter.");
            world.Get(a).Engagement = null;
            world.Get(c).Engagement = null;
            Require(Eval(Query(Function.GetGroupMemberCount)) == 0, "An actor outside combat kept a combat group.");

            // Empty reference parameters take the run-on's other reference.
            world.MarkPlayerGrab(b, true);
            Require(Eval(Query(Function.IsPlayerGrabbedRef)) == 1 && Eval(Query(Function.IsPlayerGrabbedRef, runOn: 1)) == 0 &&
                Eval(Query(Function.IsPlayerGrabbedRef, 0x201), new(a)) == 1, "Grabbed-reference substitution diverged.");
            world.MarkPlayerGrab(b, false);
            Require(Eval(Query(Function.IsPlayerGrabbedRef, 0x201)) == 0, "Release kept the grabbed reference.");

            // Weapon skill: a weapon's skill, unarmed without one, otherwise failure.
            var skill = Query(Function.IsWeaponSkillType, 41);
            Require(Eval(skill, new(Key(0x204))) == 1 && Eval(Query(Function.IsWeaponSkillType, 38), new(Key(0x204))) == 0 &&
                Eval(Query(Function.IsWeaponSkillType, 45)) == 1 && FalloutCondition.Failed(Eval(skill)) && !Passes(skill with { Comparison = 0 }),
                "IsWeaponSkillType diverged for weapons, unarmed or its failure result.");
            Require(Eval(Query(Function.GetIsObjectType, 42)) == 1 && Eval(Query(Function.GetIsObjectType, 43)) == 0 &&
                Eval(Query(Function.GetScale), new(item)) == 2 && Eval(Query(Function.GetScale)) == 1,
                "Object type or placed scale diverged.");
            using (world.EnterUsedItem(Key(0x75)))
                Require(Eval(Query(Function.GetIsUsedItemEquipType, 10)) == 1 && Eval(Query(Function.GetIsUsedItemEquipType, 11)) == 0,
                    "Used-item equip type diverged.");
            Require(Eval(Query(Function.GetIsUsedItemEquipType, 10)) == 0, "No used item matched an equip type.");

            // Trespass warnings: no package and non-guard packages report -1. State
            // without an owner is refused, never a false or zero result.
            Require(Eval(Query(Function.GetTrespassWarningLevel)) == -1 && Eval(Query(Function.GetTrespassWarningLevel), new(player)) == -1,
                "Trespass warning level invented a package.");
            package = Key(0x81);
            Require(Eval(Query(Function.GetTrespassWarningLevel)) == -1, "A non-guard package reported a warning level.");
            package = Key(0x80);
            Reject(() => Eval(Query(Function.GetTrespassWarningLevel)));
            foreach (var unowned in new[] { Function.GetMajorCrimeCount, Function.GetFriendHit, Function.GetRadiationLevel })
            {
                Reject(() => Eval(Query(unowned)));
                Require(Eval(Query(unowned), new(item)) == 0, "A non-actor subject did not read zero.");
            }
            Require(Eval(Query(Function.GetCauseofDeath)) == -1 && Eval(Query(Function.HasBeenEaten)) == 0,
                "A living actor reported a death record.");
            // Disposition: base 50, then the target's Charisma 7 (-30 + 10 x (7 - 5)),
            // a drawn weapon (-10) and a shared faction's self modifier (20 x 1 x 2).
            // An empty dialogue target is the listener; without one it reads zero.
            Require(Eval(Query(Function.GetDisposition)) == 40 && Eval(Query(Function.GetDisposition, 0x201), new(a)) == 40 &&
                Eval(Query(Function.GetDisposition), new(a)) == 0 && Eval(Query(Function.GetDisposition, 0x202), new(a)) == 100 &&
                Eval(Query(Function.GetDisposition, 0x206), new(a)) == 60 && Eval(Query(Function.GetDisposition, 0x203), new(a)) == 0,
                "GetDisposition diverged from its base, Charisma, faction or identity terms.");
            drawn.Add(b);
            Require(Eval(Query(Function.GetDisposition)) == 30, "A drawn target weapon did not lower disposition.");
            drawn.Clear();
            // Exists: the subject differs from the (substituted) parameter.
            Require(Eval(Query(Function.Exists)) == 1 && Eval(Query(Function.Exists), new(a)) == 1 &&
                Eval(Query(Function.Exists, 0x200)) == 0 && Eval(Query(Function.Exists, 0x201)) == 1,
                "Exists did not compare its subject with the parameter.");

            // Active effects: the player's owner answers by source item and base effect.
            world.BindPlayerActiveEffects(() => [new(Key(0x90), Key(0x91))]);
            Require(Eval(Query(Function.IsSpellTarget, 0x90), new(player)) == 1 && Eval(Query(Function.HasMagicEffect, 0x91), new(player)) == 1 &&
                Eval(Query(Function.HasMagicEffect, 0x92), new(player)) == 0 && Eval(Query(Function.IsSpellTarget, 0x91), new(player)) == 0,
                "Active-effect queries diverged from the player's effect owner.");
            Reject(() => Eval(Query(Function.IsSpellTarget, 0x90)));

            // Weather and calendar.
            var weather = new FalloutWeatherTransition(Key(0x85), null, 1);
            var moving = false;
            world.BindSessionConditions(() => moving, () => weather);
            Require(Eval(Query(Function.IsRaining)) == 1 && Eval(Query(Function.IsPlayerMovingIntoNewSpace)) == 0,
                "A fully blended rainy weather was not raining.");
            weather = new(Key(0x86), Key(0x85), .2f);
            moving = true;
            Require(Eval(Query(Function.IsRaining)) == 1 && Eval(Query(Function.IsPlayerMovingIntoNewSpace)) == 1,
                "Outgoing rain or a space transition was lost.");
            weather = new(Key(0x86), Key(0x85), .9f);
            Require(Eval(Query(Function.IsRaining)) == 0, "Faded-out rain kept falling.");
            Require(Eval(Query(Function.GetDayOfWeek)) == 3, "GetDayOfWeek ignored GameDaysPassed.");

            // VATS camera probes on a synthetic scene: an obstacle 300 units to the right.
            var obstacle = Key(0x203);
            world.VatsConditions.BindCameraSpace(new(_ => (Vector3.Zero, 0), target => target == b ? new Vector3(0, 1000, 0) : null,
                (start, end) =>
                {
                    var hits = new List<FalloutCameraProbeHit> { new(a, .01f, true) };
                    if (end.X > 1000) hits.Add(new(obstacle, 300 / Vector3.Distance(start, end), false));
                    if (end == new Vector3(0, 1000, 0) && start.X > 200) hits.Add(new(obstacle, .5f, false));
                    if (end == new Vector3(0, 1000, 0)) hits.Add(new(c, .9f, true));
                    return hits;
                }));
            Require(MathF.Abs(Eval(Query(Function.GetVATSRightAreaFree)) - 300) < .01f && Eval(Query(Function.GetVATSLeftAreaFree)) == float.MaxValue &&
                Eval(Query(Function.GetVATSRightTargetVisible)) == 192 && Eval(Query(Function.GetVATSLeftTargetVisible)) == 320 &&
                Eval(Query(Function.GetVATSRightTargetVisible), new(a)) == 0 && Eval(Query(Function.GetVATSFrontAreaFree), new(item)) == 0,
                "VATS camera probes diverged from side clearance or target sight.");
            Console.WriteLine("OPENNV_CONDITION_FUNCTION_CONTRACT_PASS runOnCombatTarget=true unmappedRunOn=true emptyReference=true " +
                "requiresReference=true evaluationFailure=true combatGroups=true weaponSkill=true activeEffects=true weather=true " +
                "vatsCamera=true unownedRefused=true disposition=true existsCompares=true");
        }
        finally { directory.Delete(true); }
    }

    private static byte[] Plugin()
    {
        var aggressive = new byte[20]; aggressive[0] = 3; aggressive[1] = 2;
        var passive = new byte[20];
        var weapon = new byte[120]; BinaryPrimitives.WriteInt32LittleEndian(weapon.AsSpan(104), 41);
        var rainy = new byte[15]; rainy[6] = 128; rainy[7] = 128; rainy[11] = 0x04;
        var clear = new byte[15]; clear[11] = 0x01;
        var disposition = new byte[24]; BinaryPrimitives.WriteInt16LittleEndian(disposition.AsSpan(20), 50);
        static byte[] Special(byte charisma) => Join(BitConverter.GetBytes(100), [5, 5, 5, charisma, 5, 5, 5]);
        byte[] Package(byte type) { var data = new byte[12]; data[4] = type; return Field("PKDT", data); }
        byte[] Setting(string name, float value) => Join(Field("EDID", Text(name)), Field("DATA", BitConverter.GetBytes(value)));
        return Join(Header(),
            Record("NPC_", 0x20, Field("RNAM", BitConverter.GetBytes(0x30u)), Field("ACBS", disposition), Field("AIDT", aggressive), Field("DATA", Special(5)),
                Field("SNAM", Join(BitConverter.GetBytes(0x95u), new byte[4]))),
            Record("NPC_", 0x21, Field("RNAM", BitConverter.GetBytes(0x30u)), Field("ACBS", new byte[24]), Field("AIDT", passive), Field("DATA", Special(7))),
            Record("NPC_", 0x24, Field("RNAM", BitConverter.GetBytes(0x30u)), Field("ACBS", new byte[24]), Field("AIDT", passive), Field("DATA", Special(5)),
                Field("SNAM", Join(BitConverter.GetBytes(0x95u), new byte[4]))),
            Record("FACT", 0x95, Field("XNAM", Join(BitConverter.GetBytes(0x95u), BitConverter.GetBytes(20), BitConverter.GetBytes(2u))),
                Field("DATA", new byte[4])),
            Record("RACE", 0x30), Record("ACTI", 0x22), Record("WEAP", 0x71, Field("DNAM", weapon)),
            Record("ALCH", 0x75, Field("ETYP", BitConverter.GetBytes(10))),
            Record("GLOB", 0x39, Field("EDID", Text("GameDaysPassed")), Field("FNAM", [(byte)'f']), Field("FLTV", BitConverter.GetBytes(10.5f))),
            Record("PACK", 0x80, Package(14)), Record("PACK", 0x81, Package(5)),
            Record("WTHR", 0x85, Field("DATA", rainy)), Record("WTHR", 0x86, Field("DATA", clear)),
            Record("SPEL", 0x90), Record("MGEF", 0x91), Record("MGEF", 0x92),
            Record("GMST", 0x60, Setting("fVATSSmartCameraCheckHeight", 64)),
            Record("GMST", 0x61, Setting("fVATSSmartCameraCheckStepDistance", 64)),
            Record("GMST", 0x62, Setting("fVATSSmartCameraCheckStepCount", 5)),
            Record("GMST", 0x63, Setting("fDispActorCharBase", -30)), Record("GMST", 0x64, Setting("fDispActorCharMult", 10)),
            Record("GMST", 0x65, Setting("fDispTargetCharOffset", 5)), Record("GMST", 0x66, Setting("fDispTargetKarmaMult", .002f)),
            Record("GMST", 0x67, Setting("fDispActorRespBase", -50)), Record("GMST", 0x68, Setting("fDispActorRespMult", 1)),
            Record("GMST", 0x69, Setting("fDispTargetFactionMult", 1)), Record("GMST", 0x6a, Setting("fDispTargetFactionRankBase", 1)),
            Record("GMST", 0x6b, Setting("fDispTargetWeaponBase", -10)),
            Record("PACK", 0x300),
            Record("CELL", 0x100, Field("EDID", Text("ConditionRoom")), Field("DATA", [1])), Group(0x100,
                Record("ACHR", 0x200, Field("NAME", BitConverter.GetBytes(0x20u)), Field("DATA", new byte[24])),
                Record("ACHR", 0x201, Field("NAME", BitConverter.GetBytes(0x21u)), Field("DATA", new byte[24])),
                Record("ACHR", 0x202, Field("NAME", BitConverter.GetBytes(0x20u)), Field("DATA", new byte[24])),
                Record("REFR", 0x203, Field("NAME", BitConverter.GetBytes(0x22u)), Field("XSCL", BitConverter.GetBytes(2f)), Field("DATA", new byte[24])),
                Record("REFR", 0x204, Field("NAME", BitConverter.GetBytes(0x71u)), Field("DATA", new byte[24])),
                Record("ACHR", 0x206, Field("NAME", BitConverter.GetBytes(0x24u)), Field("DATA", new byte[24]))));
    }

    private static FalloutFormKey Key(uint id) => new(Master, id);
    private static void Require(bool value, string message) { if (!value) throw new InvalidDataException(message); }
    private static void Reject(Action action)
    {
        try { action(); }
        catch (NotSupportedException) { return; }
        throw new InvalidDataException("A condition without a state owner supplied a result.");
    }
    private static byte[] Header()
    {
        var data = new byte[12]; BinaryPrimitives.WriteSingleLittleEndian(data, 1.34f);
        return Record("TES4", 0, Field("HEDR", data));
    }
    private static byte[] Group(uint cell, params byte[][] records)
    {
        var data = Join(records);
        var bytes = new byte[24 + data.Length]; Encoding.ASCII.GetBytes("GRUP").CopyTo(bytes, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), (uint)bytes.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), cell); BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), 6);
        data.CopyTo(bytes, 24); return bytes;
    }
    private static byte[] Record(string signature, uint id, params byte[][] fields)
    {
        var data = Join(fields); var bytes = new byte[24 + data.Length]; Encoding.ASCII.GetBytes(signature).CopyTo(bytes, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), (uint)data.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), id); data.CopyTo(bytes, 24); return bytes;
    }
    private static byte[] Field(string signature, byte[] data)
    {
        var bytes = new byte[6 + data.Length]; Encoding.ASCII.GetBytes(signature).CopyTo(bytes, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4), checked((ushort)data.Length)); data.CopyTo(bytes, 6); return bytes;
    }
    private static byte[] Text(string value) => Encoding.ASCII.GetBytes(value + "\0");
    private static byte[] Join(params byte[][] fields) => fields.SelectMany(field => field).ToArray();
}
