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
                Record("GLOB", 0x500, Field("EDID", Text("RoomRented")), Field("FNAM", [(byte)'s']), Field("FLTV", new byte[4]))));
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

            Require(FalloutPlayerRest.Refusal(FalloutRestKind.Sleep, new(true, true, true)) == "sNoSleepCombat" &&
                FalloutPlayerRest.Refusal(FalloutRestKind.Wait, new(true, false, false)) == "sNoWaitInCombat" &&
                FalloutPlayerRest.Refusal(FalloutRestKind.Wait, new(false, true, false)) == "sNoWaitInAir" &&
                FalloutPlayerRest.Refusal(FalloutRestKind.Sleep, new(false, false, true)) == "sNoSleepInOwnedBed" &&
                FalloutPlayerRest.Refusal(FalloutRestKind.Wait, new(false, false, true)) is null &&
                FalloutPlayerRest.Refusal(FalloutRestKind.Sleep, new(false, false, false)) is null,
                "Rest refusal lost its combat, airborne or owned-bed priority.");
            Require(new[] { "sNoSleepCombat", "sNoWaitInCombat", "sNoWaitInAir", "sNoSleepInOwnedBed" }
                .All(name => FalloutPlayerRest.IsRefusalNotice(name) && !string.IsNullOrWhiteSpace(name)) &&
                !FalloutPlayerRest.IsRefusalNotice("sQuickSaving"), "Rest refusal notices are not declared HUD notices.");

            var hurt = new GameplayVitals(3, 40, 120, 70, 70, 300, 450, .5f, new Dictionary<byte, float> { [2] = 30 });
            var slept = FalloutPlayerRest.Recover(hurt, FalloutRestKind.Sleep, 1, hardcore: false);
            Require(slept.HitPoints == 120 && slept.HitPointFraction == 0 && slept.LimbDamage is null &&
                slept.ExperiencePoints == hurt.ExperiencePoints, "Sleeping did not restore health and limbs.");
            var hardcore = FalloutPlayerRest.Recover(hurt, FalloutRestKind.Sleep, 8, hardcore: true);
            Require(hardcore.HitPoints == 120 && hardcore.LimbDamage?[2] == 30, "Hardcore sleep healed crippled limbs.");
            Require(FalloutPlayerRest.Recover(hurt, FalloutRestKind.Wait, 8, false) == hurt &&
                FalloutPlayerRest.Recover(hurt, FalloutRestKind.Sleep, 0, false) == hurt, "Waiting or a cancelled sleep restored health.");
            Require(FalloutPlayerRest.RequireHours(1) == 1 && FalloutPlayerRest.RequireHours(24) == 24, "Rest hours rejected the menu range.");
            foreach (var invalid in new[] { 0, 25, 1.5, double.NaN }) Reject(() => FalloutPlayerRest.RequireHours(invalid));
            Console.WriteLine("OPENNV_PLAYER_REST_CONTRACT_PASS bed-ownership rented-global faction-rank refusal-priority sleep-recovery hardcore-limbs hours");
        }
        finally { directory.Delete(true); }
    }

    private static void Require(bool value, string error) { if (!value) throw new InvalidDataException(error); }
    private static void Reject(Action action)
    {
        try { action(); }
        catch (InvalidDataException) { return; }
        throw new InvalidDataException("Invalid rest input was accepted.");
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
