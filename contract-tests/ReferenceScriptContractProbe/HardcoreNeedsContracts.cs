using System.Buffers.Binary;
using System.Text;
using OpenNV.Runtime.Content;
using OpenNV.Runtime.Gameplay.State;
using OpenNV.Runtime.Presentation.Ui;
using OpenNV.Runtime.World.Actors;
using OpenNV.Runtime.World.Cells;

// Hardcore need accrual, DEHY/HUNG/SLPD stages and their diseases, death at
// the fatal stage, notices, HUD label styles, the save-schema boundary and
// the SetHardcore/IsPCSleeping/GetPCSleepHours script surface.
internal static class HardcoreNeedsContracts
{
    internal static void Run()
    {
        var directory = Directory.CreateTempSubdirectory("opennv-hardcore-needs-");
        try
        {
            File.WriteAllBytes(Path.Combine(directory.FullName, "Hardcore.esm"), Join(
                Record("TES4", 0, Field("HEDR", new byte[12])),
                Number(0x900, "fHCDehydrationRate", 10), Number(0x901, "fHCStarvationRate", 25),
                Number(0x902, "fHCSleepDeprivationRate", 50), Number(0x903, "fHCSleepRestorationMod", 60),
                Text(0x910, "sDehydrationIncrease", "Your dehydration level has increased."),
                Text(0x911, "sDehydrationDecrease", "Your dehydration level has decreased."),
                Text(0x912, "sDehydrationNotSick", "You no longer have dehydration sickness."),
                Text(0x913, "sRadiationSick", "You are now sick with"),
                Effect(0x800, 0x76, 7), Effect(0x801, 0x75, 16),
                Spell(0x810, 1, "Minor Dehydration", 0x800, 1), Spell(0x811, 4, "Fatal Dehydration", 0x801, 10000),
                Spell(0x812, 1, "Minor Sleep Dep.", 0x800, 1), Spell(0x813, 1, "Minor Starvation", 0x800, 1),
                Stage("DEHY", 0x820, 200, 0x810), Stage("DEHY", 0x821, 1000, 0x811),
                Stage("SLPD", 0x822, 200, 0x812), Stage("HUNG", 0x823, 200, 0x813)));
            using var records = FalloutPluginStack.Load(directory.FullName, ["Hardcore.esm"]);
            FalloutFormKey Key(uint id) => new("Hardcore.esm", id);
            Accrual(records);
            Stages(records, Key);
            HudStyles();
            SchemaBoundary();
            ScriptSurface();
            Console.WriteLine("OPENNV_HARDCORE_NEEDS_PASS realSecondRates=true wholePointsDropLeftover=true firstRunAnchors=true " +
                "sleepMovesSlpAnchor=true backwardTimeIdle=true stageOnlyNotices=true diseaseTemporaryPool=true fatalEntryDamage=true " +
                "sleepRestoration=true hudStyles=true schemaV74=true setHardcoreRange=true pcSleepQueries=true");
        }
        finally { directory.Delete(true); }
    }

    // Owned FalloutNV.esm/executable: every stage spell, rate, notice and rest
    // setting the runtime reads, plus the Hardcore water and Doctor's Bag effects.
    internal static void RunOwned(string game)
    {
        RuntimeLiveContentSource.Configure(game, RuntimeLiveContentSource.FalloutNewVegasGame);
        using var records = FalloutPluginStack.Load(RuntimeLiveContentSource.Current!.PluginSources);
        var needs = new FalloutHardcoreNeeds(records);
        var diseases = 0;
        foreach (var (signature, actorValue) in new[] { ("DEHY", 73), ("HUNG", 74), ("SLPD", 75) })
        {
            var stages = FalloutActorValueStage.Read(records, signature);
            Require(stages.Select(stage => stage.Threshold).SequenceEqual<uint>([1000, 800, 600, 400, 200]), $"{signature} stages differ.");
            foreach (var stage in stages)
            {
                if (stage.Threshold == 1000) Require(needs.EntryHealthDamage(stage.Spell!.Value) == 10000, "A fatal stage is not 10000 damage.");
                else diseases += needs.ConstantSpells(value => value == actorValue ? stage.Threshold : 0).Count;
            }
            var change = needs.Changes(new GameplayVitals(1, 100, 100, 70, 70, 0, 200), new GameplayVitals(1, 100, 100, 70, 70, 0, 200).WithNeed(actorValue, 200)).Single();
            foreach (var notice in FalloutHardcoreNeeds.Notices(change, records.RuntimeFormKey(7)))
                Require(FalloutHardcoreNeeds.Notice(records, notice).Text.Length > 0, "A need notice has no text.");
        }
        Require(diseases == 12, "Owned stage diseases are not all constant effects.");
        foreach (var name in new[] { "fHCDehydrationRate", "fHCStarvationRate", "fHCSleepDeprivationRate", "fHCSleepRestorationMod",
            "fHostileActorInteriorDistance", "fHostileActorExteriorDistance", "fHealingRateSleepingBase", "fHealingRateSleepingMult" })
            Require(float.IsFinite(FalloutGameSettingFloats.Read(records, name)), name + " is unreadable.");
        _ = NativeHudMeterThresholds.Read(records);
        foreach (var name in new[] { "sNoWaitTrespass", "sNoWaitWhileAlarmSounding", "sNoWaitUnderWater", "sNoWaitHostilActorsNear",
            "sNoWaitInAir", "sNoWaitInCell", "sNoWaitInRadiation", "sNoWaitTakingHealthDamage", "sNoSleepInOwnedBed", "sNoSleepTrespass",
            "sNoSleepHostileActorsNear", "sNoSleepInRadiation", "sNoSleepTakingHealthDamage", "sDaySunday", "sDaySaturday", "sHungerNotSick" })
            Require(FalloutGameSettingStrings.Read(records, name).Length > 0, name + " is empty.");
        foreach (var item in new[] { "WaterPurified", "NukaCola", "NVDoctorBag", "Cram" })
            foreach (var effect in FalloutIngestible.Read(records, FalloutDialogueTopic.Find(records, "ALCH", item).FormKey).Effects
                .Where(effect => effect.Archetype != 1))
                FalloutIngestibleApplications.Validate(effect, _ => false);
        Console.WriteLine("OPENNV_OWNED_HARDCORE_PASS stages=15 diseases=12 fatal=3 settings=true notices=true refusals=true " +
            "ingestibles=WaterPurified,NukaCola,NVDoctorBag,Cram");
    }

    private static void Accrual(FalloutPluginStack records)
    {
        var needs = new FalloutHardcoreNeeds(records);
        var start = new GameplayVitals(1, 100, 100, 70, 70, 0, 200);
        static float At(double minutes) => (float)(minutes / 1440);
        // The first run only places the anchors: nothing from before it counts.
        Require(ReferenceEquals(needs.Accrue(start, At(0), 30, false), start), "First accrual counted earlier time.");
        // TimeScale 30: one point per 5 (H2O), 12.5 (FOD) and 25 (SLP) game minutes.
        var state = needs.Accrue(start, At(12), 30, false);
        Require(state.Dehydration == 2 && state.Hunger == 0 && state.SleepDeprivation == 0, "Accrual is not whole points per interval.");
        // The H2O leftover (2 of 12 minutes) was discarded with its anchor.
        state = needs.Accrue(state, At(26), 30, false);
        Require(state.Dehydration == 4 && state.Hunger == 2 && state.SleepDeprivation == 1, "Accrual carried a leftover interval.");
        // Sleeping adds no SleepDeprivation but still consumes its time.
        state = needs.Accrue(state, At(52), 30, sleeping: true);
        Require(state.Dehydration == 9 && state.Hunger == 4 && state.SleepDeprivation == 1, "Sleeping accrued SleepDeprivation.");
        state = needs.Accrue(state, At(62), 30, false);
        Require(state.SleepDeprivation == 1, "Slept time was counted after waking.");
        // Time moving backwards accrues nothing until it passes the anchors.
        var backwards = needs.Accrue(state, At(40), 30, false);
        Require(ReferenceEquals(backwards, state), "Backward game time accrued needs.");
        // A faster clock does not change the real-time rate: 10 s per H2O point.
        var fast = new FalloutHardcoreNeeds(records);
        _ = fast.Accrue(start, At(0), 60, false);
        Require(fast.Accrue(start, At(11), 60, false).Dehydration == 1, "Accrual ignored the current TimeScale.");
        Require(needs.RestoreSleep(start with { SleepDeprivation = 100 }).SleepDeprivation == 40 &&
            needs.RestoreSleep(start with { SleepDeprivation = 30 }).SleepDeprivation == 0, "Sleep restoration is not min(60, current).");
        Require(!FalloutHardcoreNeeds.Reset(state).HasNeeds, "Turning Hardcore off kept a need.");
    }

    private static void Stages(FalloutPluginStack records, Func<uint, FalloutFormKey> key)
    {
        var needs = new FalloutHardcoreNeeds(records);
        var low = new GameplayVitals(1, 100, 100, 70, 70, 0, 200, Dehydration: 150);
        var sick = low with { Dehydration = 250 };
        var change = needs.Changes(low, sick).Single();
        Require(change.ActorValue == 73 && change.Increased && change.Previous is null && change.Current?.Spell == key(0x810),
            "Crossing a threshold did not enter its stage.");
        Require(needs.Changes(sick, sick with { Dehydration = 260 }).Count == 0, "A change inside one stage was announced.");
        var player = key(0x14);
        var notices = FalloutHardcoreNeeds.Notices(change, player);
        Require(notices.Count == 2 && FalloutHardcoreNeeds.Notice(records, notices[0]) is ("Your dehydration level has increased.", var pain, 2f) &&
            pain.EndsWith("in_pain.dds", StringComparison.Ordinal) &&
            FalloutHardcoreNeeds.Notice(records, notices[1]).Text == "You are now sick with Minor Dehydration",
            "Entering a stage lost its increase or sickness notice.");
        var cured = needs.Changes(sick, low with { Dehydration = 0 }).Single();
        var curedNotices = FalloutHardcoreNeeds.Notices(cured, player);
        Require(!cured.Increased && cured.Current is null && curedNotices[1].Text == "sDehydrationNotSick" &&
            FalloutHardcoreNeeds.Notice(records, curedNotices[1]).Icon.EndsWith("very_happy.dds", StringComparison.Ordinal),
            "Leaving every stage lost its recovery notice.");
        Require(needs.ConstantSpells(value => value == 73 ? 250 : 0).SequenceEqual([key(0x810)]) &&
            needs.ConstantSpells(value => value == 73 ? 1000 : value == 75 ? 200 : 0).SequenceEqual([key(0x812)]),
            "Stage diseases were not selected from the current values.");
        Require(needs.EntryHealthDamage(key(0x811)) == 10000 && needs.EntryHealthDamage(key(0x810)) is null,
            "The fatal stage is not instant health damage, or a disease was.");
        var disease = new FalloutAbilityModifiers(records).ConstantModifiers(key(0x810)).Single();
        Require(disease is { ActorValue: 7, Amount: -1, Pool: FalloutActorValuePool.Temporary }, "A stage disease is not a temporary penalty.");
        var dying = new GameplayVitals(1, 100, 100, 70, 70, 0, 200).Damage((float)needs.EntryHealthDamage(key(0x811))!);
        Require(dying.HitPoints == 0, "The fatal stage did not kill through the health owner.");
    }

    private static void HudStyles()
    {
        var thresholds = new NativeHudMeterThresholds(200, 400, 600, 800);
        Require(thresholds.Style(199, 200) == (false, 0) && thresholds.Style(200, 200) == (false, 124) &&
            thresholds.Style(400, 200) == (false, 255) && thresholds.Style(600, 200) == (true, 255) &&
            thresholds.Style(800, 200) == (true, 200), "HUD need label styles differ from their thresholds.");
        Require(thresholds.CrippledLimbs(0) == 0 && thresholds.CrippledLimbs(1) == 201 && thresholds.CrippledLimbs(3) == 601 &&
            thresholds.CrippledLimbs(6) == 801, "Crippled limbs do not map just past each style threshold.");
        var pulse = new NativeHudMeterPulse();
        pulse.Advance(.1);
        Require(pulse.Alpha == 205, "The flash pulse did not reverse at full alpha.");
        pulse.Advance(.2);
        Require(pulse.Alpha == 151, "The flash pulse did not reverse at alpha 128.");
    }

    private static void SchemaBoundary()
    {
        var needs = new GameplayVitals(1, 100, 100, 70, 70, 0, 200, Hunger: 5);
        var prior = new FalloutNativeCampaignState(FalloutNativeCampaignSave.BeforeHardcoreNeedsSchema, "id", default, "q", 0, "n",
            null!, null!, [], [], [], [], [], [], [], Vitals: needs with { Hunger = 0 });
        Require(FalloutNativeCampaignSave.AdmitPreviousOwnerSchema(prior).Schema == FalloutNativeCampaignSave.ExpectedSchema,
            "v73 saves without needs were not admitted.");
        try { _ = FalloutNativeCampaignSave.AdmitPreviousOwnerSchema(prior with { Vitals = needs }); }
        catch (InvalidDataException) { return; }
        throw new InvalidDataException("A v73 save claimed Hardcore need values.");
    }

    private static void ScriptSurface()
    {
        var session = new FalloutScriptSession { PlayerSleeping = true, PlayerSleepHours = 5 };
        var applied = new List<bool>();
        using var fixture = new ScriptSaveFixture("set saved to IsPCSleeping\nset suffix to GetPCSleepHours\nSetHardcore 2\nSetHardcore 1");
        var scripts = new FalloutReferenceScripts(fixture.Records, fixture.World, new(fixture.Records), new((_, _) => false, effect =>
        {
            if (effect.Kind != FalloutReferenceEffectKind.Hardcore) throw new InvalidDataException("Unexpected effect.");
            applied.Add(effect.Enable);
        }, Session: session));
        Require(scripts.Activate(fixture.Caller, fixture.Player).Error is null && fixture.Value(1) == 1 && fixture.Value(2) == 5 &&
            applied.SequenceEqual([true]), "Sleep queries or the SetHardcore range check failed.");
        using var missing = new ScriptSaveFixture("set saved to IsPCSleeping");
        Require(missing.Scripts.Activate(missing.Caller, missing.Player).Error is not null, "IsPCSleeping invented a rest owner.");
    }

    private static byte[] Number(uint id, string name, float value) =>
        Record("GMST", id, Field("EDID", Text(name)), Field("DATA", BitConverter.GetBytes(value)));
    private static byte[] Text(uint id, string name, string value) => Record("GMST", id, Field("EDID", Text(name)), Field("DATA", Text(value)));
    private static byte[] Effect(uint id, uint flags, uint actorValue)
    {
        var data = new byte[72]; U32(data, 0, flags); U32(data, 16, uint.MaxValue); U32(data, 68, actorValue);
        return Record("MGEF", id, Field("DATA", data));
    }
    private static byte[] Spell(uint id, uint type, string name, uint effect, uint magnitude)
    {
        var header = new byte[16]; U32(header, 0, type);
        var item = new byte[20]; U32(item, 0, magnitude);
        return Record("SPEL", id, Field("FULL", Text(name)), Field("SPIT", header), Field("EFID", BitConverter.GetBytes(effect)), Field("EFIT", item));
    }
    private static byte[] Stage(string signature, uint id, uint threshold, uint spell) =>
        Record(signature, id, Field("DATA", [.. BitConverter.GetBytes(threshold), .. BitConverter.GetBytes(spell)]));
    private static void U32(byte[] bytes, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value);
    private static void Require(bool value, string error) { if (!value) throw new InvalidDataException(error); }
    private static byte[] Record(string name, uint id, params byte[][] fields)
    {
        var data = Join(fields); var bytes = new byte[24 + data.Length]; Encoding.ASCII.GetBytes(name).CopyTo(bytes, 0);
        U32(bytes, 4, (uint)data.Length); U32(bytes, 12, id); data.CopyTo(bytes, 24); return bytes;
    }
    private static byte[] Field(string name, byte[] data)
    {
        var bytes = new byte[6 + data.Length]; Encoding.ASCII.GetBytes(name).CopyTo(bytes, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4), (ushort)data.Length); data.CopyTo(bytes, 6); return bytes;
    }
    private static byte[] Text(string text) => Encoding.ASCII.GetBytes(text + '\0');
    private static byte[] Join(params byte[][] fields) => fields.SelectMany(field => field).ToArray();
}
