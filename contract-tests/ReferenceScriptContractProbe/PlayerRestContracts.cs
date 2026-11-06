using System.Buffers.Binary;
using System.Text;
using OpenNV.Runtime.Content;
using OpenNV.Runtime.Gameplay.State;
using OpenNV.Runtime.World.Cells;

// Sleep/Wait admission, bed ownership and sleep recovery rules shared by beds,
// the Wait control and ShowSleepWaitMenu/SetPCSleepHours.
internal static class PlayerRestContracts
{
    internal static void Run()
    {
        var directory = Directory.CreateTempSubdirectory("opennv-player-rest-");
        try
        {
            var header = new byte[12]; BinaryPrimitives.WriteSingleLittleEndian(header, 1.34f);
            File.WriteAllBytes(Path.Combine(directory.FullName, "Rest.esm"), Join(
                Record("TES4", 0, Field("HEDR", header)),
                Record("NPC_", 7, Field("ACBS", new byte[24])),
                Record("NPC_", 0x110, Field("EDID", Text("Innkeeper")), Field("ACBS", new byte[24])),
                Record("FACT", 0x700, Field("EDID", Text("Guild"))),
                Record("GLOB", 0x500, Field("EDID", Text("RoomRented")), Field("FNAM", [(byte)'s']), Field("FLTV", new byte[4])),
                Record("CELL", 0x600, Field("DATA", [1])), Flagged(Record("CELL", 0x601, Field("DATA", [1])), 0x80000),
                Setting(0x700 + 1, "fHealingRateSleepingBase", 2), Setting(0x700 + 2, "fHealingRateSleepingMult", 2),
                Name(0x710, "sDayWednesday", "Wednesday"), Name(0x711, "sAMTime", "AM"), Name(0x712, "sPMTime", "PM")));
            using var records = FalloutPluginStack.Load(directory.FullName, ["Rest.esm"]);
            FalloutFormKey Key(uint id) => new("Rest.esm", id);
            var rented = 0f;
            int? rank = null;
            bool Other(FalloutReferenceOwnership ownership) =>
                FalloutPlayerRest.OwnedByOther(records, ownership, _ => rank, _ => rented);
            Require(!Other(new(null, null, null)) && !Other(new(Key(7), null, null)) && Other(new(Key(0x110), null, null)),
                "Bed ownership ignored an unowned, player-owned or NPC-owned bed.");
            Require(Other(new(Key(0x110), null, Key(0x500))), "An unset ownership global admitted an owned bed.");
            rented = 1;
            Require(!Other(new(Key(0x110), null, Key(0x500))), "A set ownership global did not admit a rented bed.");
            Require(Other(new(Key(0x700), 2, null)), "A faction bed admitted a non-member.");
            rank = 1;
            Require(Other(new(Key(0x700), 2, null)), "A faction bed admitted a member below its required rank.");
            rank = 2;
            Require(!Other(new(Key(0x700), 2, null)), "A faction bed refused a member at its required rank.");

            // The Wait control: first matching check wins, in the original order.
            var all = new FalloutRestCircumstances(true, true, true, true, true, true, true, true, true, true, true);
            string?[] waits = [];
            for (var circumstances = all; FalloutPlayerRest.WaitRefusal(circumstances) is { } refusal;)
            {
                waits = [.. waits, refusal];
                circumstances = refusal switch
                {
                    "sNoWaitTrespass" => circumstances with { Trespassing = false },
                    "sNoWaitWhileAlarmSounding" => circumstances with { AlarmSounding = false },
                    "sNoWaitUnderWater" => circumstances with { Underwater = false },
                    "sNoWaitHostilActorsNear" => circumstances with { HostileActorsNear = false },
                    "sNoWaitInAir" => circumstances with { Airborne = false },
                    "sNoWaitInCell" => circumstances with { CantWaitHere = false, WaitDisabled = false },
                    "sNoWaitInRadiation" => circumstances with { EnvironmentRadiation = false, RadiationRate = false },
                    _ => circumstances with { TakingHealthDamage = false },
                };
            }
            Require(waits.SequenceEqual(["sNoWaitTrespass", "sNoWaitWhileAlarmSounding", "sNoWaitUnderWater", "sNoWaitHostilActorsNear",
                "sNoWaitInAir", "sNoWaitInCell", "sNoWaitInRadiation", "sNoWaitTakingHealthDamage"]), "Wait refusals lost their order.");
            Require(FalloutPlayerRest.WaitRefusal(new(WaitDisabled: true)) == "sNoWaitInCell" &&
                FalloutPlayerRest.WaitRefusal(new(RadiationRate: true)) == "sNoWaitInRadiation" &&
                FalloutPlayerRest.WaitRefusal(new(BedOwnedByOther: true)) is null, "Wait ignored EnableFastTravel or checked bed ownership.");
            // Beds: ownership, then Can't Wait (the wait string, not the
            // EnableFastTravel flag), trespass, hostiles, radiation, damage.
            Require(FalloutPlayerRest.SleepRefusal(all) == "sNoSleepInOwnedBed" &&
                FalloutPlayerRest.SleepRefusal(all with { BedOwnedByOther = false }) == "sNoWaitInCell" &&
                FalloutPlayerRest.SleepRefusal(all with { BedOwnedByOther = false, CantWaitHere = false }) == "sNoSleepTrespass" &&
                FalloutPlayerRest.SleepRefusal(new(HostileActorsNear: true, TakingHealthDamage: true)) == "sNoSleepHostileActorsNear" &&
                FalloutPlayerRest.SleepRefusal(new(EnvironmentRadiation: true)) == "sNoSleepInRadiation" &&
                FalloutPlayerRest.SleepRefusal(new(TakingHealthDamage: true)) == "sNoSleepTakingHealthDamage" &&
                FalloutPlayerRest.SleepRefusal(new(WaitDisabled: true, Airborne: true, RadiationRate: true, AlarmSounding: true, Underwater: true)) is null,
                "Bed refusals differ from their original chain.");
            // The combat strings are never produced, but saved pending notices stay displayable.
            Require(waits.Append("sNoSleepInOwnedBed").Append("sNoSleepCombat").Append("sNoWaitInCombat").All(FalloutPlayerRest.IsRefusalNotice) &&
                !FalloutPlayerRest.IsRefusalNotice("sQuickSaving") &&
                waits.Concat([FalloutPlayerRest.SleepRefusal(all)]).All(refusal => refusal is not ("sNoSleepCombat" or "sNoWaitInCombat")),
                "Rest refusal notices are not the original declared HUD notices.");
            Require(!FalloutPlayerRest.CantWait(records, Key(0x600)) && FalloutPlayerRest.CantWait(records, Key(0x601)),
                "The cell Can't Wait record flag was not read.");

            // Only a natural sleep heals fully; regeneration is per hour.
            var hurt = new GameplayVitals(3, 40, 120, 70, 70, 300, 450, .5f, new Dictionary<byte, float> { [2] = 30 });
            var slept = FalloutPlayerRest.EndOfSleep(hurt);
            Require(slept.HitPoints == 120 && slept.HitPointFraction == 0 && slept.LimbDamage is null &&
                slept.ExperiencePoints == hurt.ExperiencePoints, "The end of sleep did not restore health and limbs.");
            Require(FalloutPlayerRest.EndOfSleep(hurt.Damage(1000)).HitPoints == 0, "The end of sleep revived a dead player.");
            Require(FalloutPlayerRest.HourHealing(records, 0, false, 120) == 0 &&
                Math.Abs(FalloutPlayerRest.HourHealing(records, 0, true, 120) - 2f / 30) < 1e-6 &&
                Math.Abs(FalloutPlayerRest.HourHealing(records, 36, true, 120) - (2f / 30 + 2.4f)) < 1e-5,
                "Hourly rest regeneration differs from its sleeping base and multiplier.");
            Require(FalloutPlayerRest.Heal(hurt, 10).ExactHitPoints == hurt.ExactHitPoints + 10 &&
                FalloutPlayerRest.Heal(hurt, 1000).HitPoints == 120 && FalloutPlayerRest.Heal(hurt, 1000).LimbDamage?[2] == 30,
                "Rest regeneration exceeded maximum health or healed limbs.");
            Require(FalloutPlayerRest.ClockText(records, new(9, 13, 3, 0.5f), 2281) == "Wednesday, 10.13.81, 12:30 AM" &&
                FalloutPlayerRest.ClockText(records, new(9, 13, 3, 12.25f), 2281) == "Wednesday, 10.13.81, 12:15 PM" &&
                FalloutPlayerRest.ClockText(records, new(9, 13, 3, 13f), 2281) == "Wednesday, 10.13.81, 1:00 PM",
                "The rest clock lost its day, date or 12-hour time.");
            Console.WriteLine("OPENNV_PLAYER_REST_CONTRACT_PASS bed-ownership rented-global faction-rank wait-chain bed-chain cant-wait-flag " +
                "natural-sleep-heal hourly-regeneration clock-text");
        }
        finally { directory.Delete(true); }
    }

    private static void Require(bool value, string error) { if (!value) throw new InvalidDataException(error); }
    private static byte[] Record(string name, uint id, params byte[][] fields)
    {
        var data = Join(fields); var bytes = new byte[24 + data.Length]; Encoding.ASCII.GetBytes(name).CopyTo(bytes, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), (uint)data.Length); BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), id); data.CopyTo(bytes, 24); return bytes;
    }
    private static byte[] Flagged(byte[] record, uint flags)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(8), flags); return record;
    }
    private static byte[] Setting(uint id, string name, float value) =>
        Record("GMST", id, Field("EDID", Text(name)), Field("DATA", BitConverter.GetBytes(value)));
    private static byte[] Name(uint id, string name, string value) => Record("GMST", id, Field("EDID", Text(name)), Field("DATA", Text(value)));
    private static byte[] Field(string name, byte[] data)
    {
        var bytes = new byte[6 + data.Length]; Encoding.ASCII.GetBytes(name).CopyTo(bytes, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4), (ushort)data.Length); data.CopyTo(bytes, 6); return bytes;
    }
    private static byte[] Text(string text) => Encoding.ASCII.GetBytes(text + '\0');
    private static byte[] Join(params byte[][] fields) => fields.SelectMany(field => field).ToArray();
}
