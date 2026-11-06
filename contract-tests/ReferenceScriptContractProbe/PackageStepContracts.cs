using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using OpenNV.Runtime.Content;
using OpenNV.Runtime.Gameplay.State;

// PKDT flag admission/masking and the stepped package procedures (Wander,
// UseItemAt, Ambush, FleeNotCombat, UseWeapon) over synthetic records.
internal static class PackageStepContracts
{
    private static readonly FalloutPackageStepSettings Settings = new(15000, 1000, 30, 5);

    internal static void Run()
    {
        var directory = Path.Combine(Path.GetTempPath(), "opennv-package-steps-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var header = new byte[12]; BinaryPrimitives.WriteSingleLittleEndian(header, 1.34f);
            File.WriteAllBytes(Path.Combine(directory, "Steps.esm"), Join(Record("TES4", 0, Field("HEDR", header)),
                // Wander: Must Complete, Once Per Day and garbage 0xCDCD behaviour flags, stale Sandbox bits.
                Package(0x10, 5, 0x404, behavior: 0xcdcd, specific: 0x3f, location: Location(0, 0x900, 300)),
                // Travel carrying stale Sandbox bits, No Warn and Pretend In Combat, door flags.
                Package(0x11, 6, 0x12000048, specific: 0x21, location: Location(0, 0x900, 0)),
                // UseItemAt with Sit Down plus stale bits; object-ID target.
                Package(0x12, 8, 0x1000, specific: 0x10f2, location: Location(0, 0x900, 0), target: Target(1, 0x700, 1)),
                // Ambush: Hide While Ambushing; reference victim; zone PLD2 copied after PKAM.
                Package(0x13, 9, 0x00801000, specific: 0x3, location: Location(0, 0x900, 64), target: Target(0, 0x901, 0),
                    extra: [Field("PKAM", []), Field("PLD2", Location(0, 0x902, 1400)), Field("PLD2", Location(0, 0x902, 1400))]),
                // FleeNotCombat: threat at safe distance 500, Must Reach.
                Package(0x14, 10, 0x1002, target: Target(0, 0x901, 500)),
                // UseWeapon: volley fire, 2 bursts, 2..2 shots, pause 1..1 s, Must Complete; attack target PTD2.
                Package(0x15, 16, 0x04801004, location: Location(2, 0, 0), target: Target(1, 0x710, 0),
                    extra: [Field("PTD2", Target(0, 0x901, 0)), Field("PKW3", Weapon(volley: true, bursts: 2, minimum: 2, maximum: 2, pause: 1)),
                        Field("CTDA", UnloadedActorPackageContracts.StageCondition(0x900))]),
                // UseWeapon without its weapon declaration degrades to Travel.
                Package(0x16, 16, 0x1000, location: Location(2, 0, 0)),
                // CastMagic has no procedure; it runs as Travel.
                Package(0x17, 11, 0x1000, location: Location(2, 0, 0)),
                // Sandbox with Must Complete and an always-active schedule.
                Package(0x18, 12, 0x1004, location: Location(2, 0, 0)),
                Record("NPC_", 0x800, Field("ACBS", new byte[24]), Field("PKID", BitConverter.GetBytes(0x16u)), Field("PKID", BitConverter.GetBytes(0x15u)),
                    Field("PKID", BitConverter.GetBytes(0x10u)), Field("PKID", BitConverter.GetBytes(0x18u)))));
            using var records = FalloutPluginStack.Load(directory, ["Steps.esm"]);
            FalloutPluginRecord Source(uint id) => records.GetEffective(new("Steps.esm", id));
            FlagAdmission(records, Source);
            Wander(FalloutSteppedPackage.Read(Source(0x10)));
            UseItem(FalloutSteppedPackage.Read(Source(0x12)));
            Ambush(FalloutSteppedPackage.Read(Source(0x13)));
            Flee(FalloutSteppedPackage.Read(Source(0x14)));
            UseWeapon(FalloutSteppedPackage.Read(Source(0x15)));
            foreach (var id in new uint[] { 0x16, 0x17 })
                Require(FalloutSteppedPackage.Read(Source(id)).Steps.SequenceEqual([FalloutPackageStep.Travel, FalloutPackageStep.Done]),
                    "A package missing its target/location or CastMagic did not degrade to Travel.");
            Console.WriteLine("OPENNV_PACKAGE_STEP_CONTRACT_PASS wander=true useItemAt=true ambush=true flee=true useWeapon=true invalidAsTravel=true duration=true persistence=true");
        }
        finally { Directory.Delete(directory, true); }
    }

    private static void FlagAdmission(FalloutPluginStack records, Func<uint, FalloutPluginRecord> source)
    {
        var wander = FalloutPackageData.Read(source(0x10));
        Require(wander.BehaviorFlags == 0xcdcd && wander.SpecificFlags == 0, "Wander lost raw behaviour flags or kept stale Sandbox bits.");
        var stepped = FalloutSteppedPackage.Read(source(0x10));
        Require(stepped.MustComplete && stepped.OncePerDay && stepped.Steps.Count == 3, "Wander refused garbage behaviour flags or lost general flags.");
        var travel = FalloutTravelPackage.Read(source(0x11));
        Require(travel.Flags == 0x12000048 && FalloutPackageData.Read(source(0x11)).SpecificFlags == 0,
            "Travel refused No Warn/Pretend In Combat/door flags or kept stale type-specific bits.");
        Require(FalloutPackageData.Read(source(0x12)).SpecificFlags == 0x2 && FalloutSteppedPackage.Read(source(0x12)).SitDown,
            "UseItemAt lost Sit Down or kept bits outside its own set (0x2, 0x100-0x400 keep only defined ones).");
        Require(FalloutPackageData.Read(source(0x13)).SpecificFlags == 0x1 && FalloutSteppedPackage.Read(source(0x13)).HideWhileAmbushing,
            "Ambush lost Hide While Ambushing or kept a stale bit.");
        Require(FalloutPackageFlags.TypeSpecificMask(12) == 0x3f && FalloutPackageFlags.TypeSpecificMask(0) == 0x700 &&
            FalloutPackageFlags.TypeSpecificMask(14) == 0, "Type-specific masks differ from each procedure's defined bits.");
        // Must Complete / Must Reach hold against lower priority, never against higher priority or once done.
        var npc = new FalloutFormKey("Steps.esm", 0x800);
        var useWeapon = source(0x15);
        bool Retains(uint current, uint? next, bool done, bool? arrived, float stage = 0) => FalloutAiPackages.RetainsMustPackage(records, npc, null,
            source(current), next is { } id ? source(id).FormKey : null, done, arrived, null, _ => stage);
        var retention = new (bool Holds, string Case)[]
        {
            (Retains(0x15, 0x10, false, null), "lower priority"), (Retains(0x15, null, false, null), "no selection"),
            (!Retains(0x15, 0x16, false, null), "higher priority"), (!Retains(0x15, 0x10, true, null), "done"),
            (!Retains(0x15, null, false, null, stage: 1), "conditions lapse"), (Retains(0x14, null, false, false), "must reach"),
            (!Retains(0x14, null, false, true), "arrived"), (!Retains(0x14, null, false, null), "no arrival owner"),
            (!Retains(0x18, 0x10, false, null), "no Done owner"), (!Retains(0x10, 0x18, false, null), "wander without duration"),
        };
        Require(retention.All(value => value.Holds), "Must Complete/Must Reach retention failed: " +
            string.Join(", ", retention.Where(value => !value.Holds).Select(value => value.Case)));
        Console.WriteLine("OPENNV_PACKAGE_FLAG_ADMISSION_CONTRACT_PASS garbageBehavior=true staleSpecificMasked=true generalFlagsAdmitted=true mustCompleteRetention=true mustReachRetention=true higherPriorityPreempts=true conditionsLapse=true noDoneOwnerNoHold=true alwaysActiveSchedule=true");
    }

    private static FalloutPackageStepResult Step(FalloutSteppedPackage source, FalloutPackageStepProgress progress,
        FalloutPackageStepObservation observation, uint draw = 0) =>
        FalloutPackageSteps.Advance(source, progress, observation, _ => draw, Settings);

    private static FalloutPackageStepObservation At(double seconds = 0.1, double hours = 0) => new(hours, seconds) { AtLocation = true };

    private static void Wander(FalloutSteppedPackage source)
    {
        var progress = FalloutPackageSteps.Start(source, 0, [0, 0, 0]);
        var result = Step(source, progress, At() with { AtLocation = false });
        Require(result.Progress.Step == 0 && result.Action.HasFlag(FalloutPackageStepAction.MoveToLocation), "Wander did not travel first.");
        result = Step(source, progress, At());
        Require(result.Progress.Step == 1, "Wander did not leave Travel on arrival.");
        var leg = Step(source, result.Progress, At());
        Require(leg.Action.HasFlag(FalloutPackageStepAction.WanderLeg) && leg.Progress.Cooldown == 30, "Wander did not start a leg capped by fAIMaxWanderTime.");
        var arrived = Step(source, leg.Progress, At() with { LegArrived = true });
        Require(arrived.Progress.Timer == 5 && arrived.Action.HasFlag(FalloutPackageStepAction.Hold), "Wander did not idle for fAIIdleWaitTime.");
        var next = Step(source, arrived.Progress, At(5.1) with { LegArrived = true });
        Require(next.Action.HasFlag(FalloutPackageStepAction.WanderLeg), "Wander did not pick its next leg after idling.");
        var abandoned = Step(source, leg.Progress, At(31));
        Require(abandoned.Action.HasFlag(FalloutPackageStepAction.WanderLeg), "Wander did not abandon an overlong leg.");
        // Wander never completes by itself; with no PSDT duration it stays.
        Require(Step(source, leg.Progress, At(1, 1000)).Progress.Step == 1, "Wander finished without a duration.");
    }

    private static void UseItem(FalloutSteppedPackage source)
    {
        var progress = FalloutPackageSteps.Start(source, 0, [0, 0, 0]);
        progress = Step(source, progress, At()).Progress; // Acquire +1
        progress = Step(source, progress, At()).Progress; // Travel +1
        Require(FalloutPackageSteps.Current(source, progress) == FalloutPackageStep.UseItemAt, "UseItemAt did not run Acquire then Travel.");
        Require(Step(source, progress, At() with { PlayerDistance = 2000, UseKind = 9, HasUsedForm = true }).Progress == progress,
            "UseItemAt acted while the player was beyond its proximity distance.");
        Require(Step(source, progress, At() with { ConditionsPass = false }).Progress.Step == 1, "Lapsed conditions did not re-travel.");
        var sit = Step(source, progress, At() with { UseKind = 9, HasUsedForm = true, HasUseTarget = true });
        Require(sit.Action.HasFlag(FalloutPackageStepAction.Sit) && sit.Progress.Step == 2, "Sit Down did not sit before use.");
        var item = At() with { UseKind = 9, HasUsedForm = true, HasUseTarget = true, Seated = true };
        var use = Step(source, progress, item, draw: 2500);
        Require(use.Action.HasFlag(FalloutPackageStepAction.PlayUseIdle) && use.Progress.Uses == 0, "Item use was counted before its idle was performed.");
        Require(Step(source, use.Progress, item).Progress.Uses == 0, "An unperformed item use advanced the package.");
        var played = Step(source, use.Progress, item with { UsePerformed = true }, draw: 2500);
        Require(played.Progress.Uses == 1 && Math.Abs(played.Progress.Timer - 7.4) < 1e-9, "A performed use idle did not start its 5-10 s use timer.");
        var unconsumed = Step(source, played.Progress with { Timer = 0 }, item);
        Require(unconsumed.Progress.Step == 2 && unconsumed.Action.HasFlag(FalloutPackageStepAction.ConsumeItem),
            "Item use completed without an actual inventory removal.");
        var consumed = Step(source, played.Progress with { Timer = 0 }, item with { ItemConsumed = true });
        Require(consumed.Progress.Step == 3, "A consumed item use did not complete.");
        var done = Step(source, consumed.Progress, At());
        Require(done.Action.HasFlag(FalloutPackageStepAction.CompletePackage) && done.Action.HasFlag(FalloutPackageStepAction.ReevaluatePackages) &&
            done.Progress.DoneHandled, "Done did not fire the package end once and re-evaluate a duration-0 package.");
        var after = Step(source, done.Progress, At());
        Require(after.Action == FalloutPackageStepAction.Hold, "Completed package replayed its end or re-evaluated every tick.");
        var retravel = Step(source, done.Progress, At() with { AtLocation = false });
        Require(retravel.Progress.Step == 1 && retravel.Action.HasFlag(FalloutPackageStepAction.MoveToLocation),
            "Completed package that left its location did not return to its Travel step.");
        var activator = At() with { UseKind = 1, HasUseTarget = true, Seated = true };
        var activate = Step(source, progress, activator);
        Require(activate.Action.HasFlag(FalloutPackageStepAction.Activate) && activate.Progress.Uses == 0 &&
            Step(source, activate.Progress, activator).Progress.Step == 2, "An unperformed activation completed the package.");
        Require(Step(source, activate.Progress, activator with { UsePerformed = true }).Progress.Step == 3,
            "A performed activation did not complete the package.");
        Require(Step(source, progress, At() with { UseKind = 12, HasUseTarget = true, Seated = true }).Action.HasFlag(FalloutPackageStepAction.WeaponTargetWarning),
            "A weapon UseItemAt target was not reported.");
    }

    private static void Ambush(FalloutSteppedPackage source)
    {
        Require(source.Zone is { Radius: 1400 } && source.Location is { Radius: 64 }, "Ambush lost its wait spot or zone.");
        var progress = Step(source, FalloutPackageSteps.Start(source, 0, [0, 0, 0]), At()).Progress;
        Require(FalloutPackageSteps.Current(source, progress) == FalloutPackageStep.AmbushWait, "Ambush did not wait after Travel.");
        var hide = Step(source, progress, At() with { Victim = FalloutAmbushVictim.Alive });
        Require(hide.Action.HasFlag(FalloutPackageStepAction.MoveToHidingPoint) && hide.Progress.Hidden, "Ambush did not issue its hiding point once.");
        var crouch = Step(source, hide.Progress, At() with { Victim = FalloutAmbushVictim.Alive });
        Require(crouch.Action.HasFlag(FalloutPackageStepAction.Sneak) && !crouch.Action.HasFlag(FalloutPackageStepAction.MoveToHidingPoint),
            "Hidden ambusher did not sneak.");
        Require(Step(source, hide.Progress, At() with { Victim = FalloutAmbushVictim.Alive, VictimInZone = true }).Action ==
            FalloutPackageStepAction.StartCombatWithVictim, "Victim in the zone did not spring the ambush.");
        Require(Step(source, hide.Progress, At() with { PlayerTrigger = true }).Action == FalloutPackageStepAction.StartCombatWithPlayer,
            "Player trigger did not start combat.");
        Require(Step(source, hide.Progress, At() with { Victim = FalloutAmbushVictim.Alive, LeashExceeded = true }).Progress.Step == 0,
            "Leash did not re-travel to the wait spot.");
        var deleted = Step(source, hide.Progress, At() with { Victim = FalloutAmbushVictim.Deleted });
        Require(deleted.Progress.Step == 2 && deleted.Progress.TargetHandled, "Deleted victim did not complete and mark handled.");
        var dead = Step(source, hide.Progress, At() with { Victim = FalloutAmbushVictim.DeadActor });
        Require(dead.Progress.Step == 1 && dead.Progress.TargetHandled, "Dead victim did not mark handled while waiting.");
        var retry = Step(source, progress, At(0.5) with { Victim = FalloutAmbushVictim.Unresolved });
        Require(retry.Action.HasFlag(FalloutPackageStepAction.ResolveVictim) && retry.Progress.Timer == 10, "Unresolved victim did not retry on a 10 s timer.");
    }

    private static void Flee(FalloutSteppedPackage source)
    {
        Require(source.Steps.SequenceEqual([FalloutPackageStep.FleeNotCombat, FalloutPackageStep.Done]), "Flee lost its single step.");
        var progress = FalloutPackageSteps.Start(source, 0, [0, 0, 0]);
        Require(Step(source, progress, At() with { HasThreat = true, InDanger = true }).Action == FalloutPackageStepAction.Flee, "Threat inside safe distance did not flee.");
        Require(Step(source, progress, At() with { HasThreat = true }).Progress.Step == 0, "Safe stationary actor dropped its package.");
        Require(Step(source, progress, At() with { HasThreat = true, Moving = true }).Progress.Step == 1, "Must Reach did not complete once safe.");
        Require(Step(source, progress, At()).Progress.Step == 1, "No threat and no refuge did not complete.");
        Require(Step(source, progress, At() with { HasThreat = true, InDanger = true, CannotFlee = true }).Progress.Step == 1, "An actor unable to flee did not complete.");
    }

    private static void UseWeapon(FalloutSteppedPackage source)
    {
        Require(source.Weapon is { VolleyFire: true, Bursts: 2, VolleyMinimum: 2, PauseMinimum: 1 } && source.AttackTarget is { Type: 0 },
            "UseWeapon lost its PKW3 or attack target.");
        var progress = Step(source, Step(source, FalloutPackageSteps.Start(source, 0, [0, 0, 0]), At()).Progress, At()).Progress;
        Require(FalloutPackageSteps.Current(source, progress) == FalloutPackageStep.UseWeapon, "UseWeapon did not run Acquire then Travel.");
        var ready = At() with { HasMatchingWeapon = true, EquippedMatches = true, ClipRounds = 30, WeaponOut = true };
        Require(Step(source, progress, ready with { PlayerDistance = 20000 }).Progress == progress, "UseWeapon fired beyond fAIUseWeaponDistance.");
        Require(Step(source, progress, ready with { EquippedMatches = false, HasMatchingWeapon = false }).Progress.Step == 3,
            "A missing weapon did not complete the package.");
        Require(Step(source, progress, ready with { WeaponOut = false }).Action.HasFlag(FalloutPackageStepAction.DrawWeapon), "UseWeapon did not draw first.");
        Require(Step(source, progress, ready with { ConditionsPass = false }).Progress.Step == 1, "Lapsed conditions did not re-travel.");
        var shot = Step(source, progress, ready);
        Require(shot.Action.HasFlag(FalloutPackageStepAction.Fire) && shot.Progress.ShotTarget == 2, "Volley fire did not size its volley from PKW3.");
        Require(!Step(source, shot.Progress, ready with { DeltaSeconds = 0.1, ShotsFired = 1 }).Action.HasFlag(FalloutPackageStepAction.Fire),
            "UseWeapon ignored its refire cooldown.");
        var unfired = Step(source, shot.Progress with { Cooldown = 0 }, ready);
        Require(unfired.Action.HasFlag(FalloutPackageStepAction.Fire) && unfired.Progress.Shots == 0 && unfired.Progress.Volleys == 0,
            "An undischarged shot was counted.");
        var second = Step(source, shot.Progress with { Cooldown = 0 }, ready with { ShotsFired = 1 });
        var paused = Step(source, second.Progress with { Cooldown = 0 }, ready with { ShotsFired = 1 });
        Require(paused.Progress.Volleys == 1 && paused.Progress.Timer > 0 && !paused.Action.HasFlag(FalloutPackageStepAction.Fire),
            "Finished volley did not pause between volleys.");
        Require(!Step(source, shot.Progress with { Cooldown = 0 }, ready with { Blocked = true }).Action.HasFlag(FalloutPackageStepAction.Fire),
            "Hold Fire When Blocked fired through a blocker.");
        var finished = Step(source, paused.Progress with { Timer = 0, Volleys = 2, Shots = 0, ShotTarget = 0 }, ready);
        Require(finished.Progress.Step == 3, "UseWeapon did not complete after its bursts.");
        // PSDT duration expiry always lands on Done.
        var timed = source with { DurationHours = 2 };
        Require(Step(timed, progress, ready with { NowHours = 2 }).Progress.Step == 3, "Duration expiry did not complete the package.");
        // Step progress survives a save with its package motion.
        var motion = new FalloutActorPackageMotion(source.Form, new string('a', 64), "idle", new string('b', 64), 0, false,
            [0, 0, 0], [0, 0, 0, 1], Steps: paused.Progress with { Anchor = [1, 2, 3] });
        var cold = JsonSerializer.Deserialize<FalloutActorPackageMotion>(JsonSerializer.Serialize(motion))!;
        cold.Validate();
        Require(cold.Steps is { Volleys: 1, Step: 2 } steps && steps.Anchor!.SequenceEqual([1f, 2f, 3f]) && steps.Timer == paused.Progress.Timer,
            "Saved step progress lost its cursor, counters or anchor.");
        Reject(() => (motion with { Steps = motion.Steps! with { Package = new("Steps.esm", 0x10) } }).Validate());
        Reject(() => (motion with { Steps = motion.Steps! with { Step = 9 } }).Validate());
        // A saved step outside the package's own step list is refused on restore.
        Reject(() => FalloutPackageSteps.Validate(source with { Steps = [FalloutPackageStep.Travel, FalloutPackageStep.Done] }, motion.Steps!));
    }

    private static byte[] Package(uint id, byte procedure, uint flags, ushort behavior = 0, ushort specific = 0,
        byte[]? location = null, byte[]? target = null, byte[][]? extra = null)
    {
        var data = new byte[12]; BinaryPrimitives.WriteUInt32LittleEndian(data, flags); data[4] = procedure;
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(6), behavior); BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(8), specific);
        var schedule = new byte[8]; schedule[0] = 0xff; schedule[1] = 0xff; schedule[3] = 0xff;
        return Record("PACK", id, [Field("EDID", Encoding.ASCII.GetBytes($"Step{id:x}\0")), Field("PKDT", data),
            location is null ? [] : Field("PLDT", location), Field("PSDT", schedule), target is null ? [] : Field("PTDT", target),
            .. extra ?? []]);
    }

    private static byte[] Location(int type, uint form, int radius) =>
        Join(BitConverter.GetBytes(type), BitConverter.GetBytes(form), BitConverter.GetBytes(radius));

    private static byte[] Target(int type, uint form, int count) =>
        Join(BitConverter.GetBytes(type), BitConverter.GetBytes(form), BitConverter.GetBytes(count), new byte[4]);

    private static byte[] Weapon(bool volley, ushort bursts, ushort minimum, ushort maximum, float pause)
    {
        var data = new byte[24]; data[0] = 1; data[1] = 1; data[3] = 1; data[4] = volley ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(6), bursts);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(8), minimum); BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(10), maximum);
        BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(12), pause); BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(16), pause);
        return data;
    }

    private static byte[] Join(params byte[][] values) => values.SelectMany(value => value).ToArray();
    private static byte[] Field(string name, byte[] data)
    {
        var bytes = new byte[6 + data.Length]; Encoding.ASCII.GetBytes(name).CopyTo(bytes, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4), checked((ushort)data.Length)); data.CopyTo(bytes, 6); return bytes;
    }
    private static byte[] Record(string name, uint id, params byte[][] fields)
    {
        var data = Join(fields); var bytes = new byte[24 + data.Length]; Encoding.ASCII.GetBytes(name).CopyTo(bytes, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), (uint)data.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), id); data.CopyTo(bytes, 24); return bytes;
    }
    private static void Require(bool value, string message) { if (!value) throw new InvalidDataException(message); }
    private static void Reject(Action action)
    {
        try { action(); } catch (Exception error) when (error is InvalidDataException or NotSupportedException) { return; }
        throw new InvalidOperationException("Invalid package step input was accepted.");
    }
}
