using System.Buffers.Binary;
using System.Text;
using OpenNV.Runtime.Content;
using OpenNV.Runtime.Gameplay.State;

internal static class VatsMeleeContracts
{
    internal static void Run()
    {
        var directory = Directory.CreateTempSubdirectory("opennv-vats-melee-");
        try
        {
            uint id = 0x100;
            byte[] Setting(string name, float value) => Record("GMST", id++, Field("EDID", Text(name)), Field("DATA", BitConverter.GetBytes(value)));
            File.WriteAllBytes(Path.Combine(directory.FullName, "VatsMelee.esm"), Join(Record("TES4", 0, Field("HEDR", new byte[12])),
                Setting("fVATSMeleeArmConditionBase", .2f), Setting("fVATSMeleeArmConditionMult", .8f),
                Setting("fVATSMeleeChanceMult", 1), Setting("fVATSMaxChance", 95), Setting("fVATSMeleeReachMult", 2),
                Setting("fVATSMeleeMaxDistance", 300), Setting("fVATSMeleeWarpDistanceMult", .32f),
                Setting("fVATSH2HWarpDistanceMult", .27f), Setting("fActionPointsVATSMeleeMult", 1.5f),
                Setting("fActionPointsAttackUnarmed", 22), Setting("fActionPointsAttackOneHandMelee", 15),
                Weapon(0x50, 1, null), Weapon(0x51, 2, 40)));
            using var records = FalloutPluginStack.Load(directory.FullName, ["VatsMelee.esm"]);
            var chance = new FalloutVatsMeleeChance(records);
            Require(chance.Calculate(1, 1) == 95 && Near(chance.Calculate(0, 1), 60) && Near(chance.Calculate(0, 0), 20) &&
                Near(chance.Calculate(.5f, .5f), 60), "Melee VATS chance lost its cap or arm-condition weighting.");
            Reject(() => chance.Calculate(1.5f, 1));
            Reject(() => chance.Calculate(float.NaN, 1));
            const float units = .0142875f;
            Require(Near(chance.Range(128 * units, units), 256 * units) && Near(chance.Range(200 * units, units), 300 * units),
                "Melee VATS range lost its reach multiplier or absolute engage cap.");
            Require(Near(chance.ApproachDistance(100, false, units), 32) && Near(chance.ApproachDistance(100, true, units), 27),
                "Melee VATS approach did not use the weapon-class warp distance.");
            Require(chance.ActionPointCost(20) == 30, "Melee VATS AP did not apply its melee multiplier.");

            Require(FalloutVatsWeaponCost.Read(records, null) is { WeaponType: 0, BaseCost: 22 } &&
                FalloutVatsWeaponCost.Read(records, Key(0x50)).BaseCost == 15 && FalloutVatsWeaponCost.Read(records, Key(0x51)).BaseCost == 40,
                "Melee/unarmed VATS AP lost its weapon-type GMST or authored override.");
            Require(FalloutVatsActions.OrdinaryMelee(0) == FalloutVatsAction.Unarmed &&
                FalloutVatsActions.OrdinaryMelee(1) == FalloutVatsAction.OneHandMelee &&
                FalloutVatsActions.OrdinaryMelee(2) == FalloutVatsAction.TwoHandMelee && (uint)FalloutVatsAction.Pistol == 3,
                "Melee VATS actions lost their GetVATSValue action numbering.");
            Reject(() => FalloutVatsActions.OrdinaryMelee(3));
            Console.WriteLine("OPENNV_VATS_MELEE_CONTRACT_PASS chance=true armCondition=true range=true approach=true ap=true actions=true");
        }
        finally { directory.Delete(true); }
    }

    private static FalloutFormKey Key(uint id) => new("VatsMelee.esm", id);
    private static bool Near(float value, float expected) => MathF.Abs(value - expected) < 1e-4f;

    private static byte[] Weapon(uint id, uint type, float? apOverride)
    {
        var dnam = new byte[204];
        BinaryPrimitives.WriteUInt32LittleEndian(dnam, type);
        if (apOverride is { } cost)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(dnam.AsSpan(56), 8);
            BinaryPrimitives.WriteSingleLittleEndian(dnam.AsSpan(68), cost);
        }
        return Record("WEAP", id, Field("DNAM", dnam));
    }

    private static byte[] Record(string signature, uint id, params byte[][] fields)
    {
        var data = Join(fields); var bytes = new byte[24 + data.Length]; Encoding.ASCII.GetBytes(signature).CopyTo(bytes, 0);
        BitConverter.GetBytes((uint)data.Length).CopyTo(bytes, 4); BitConverter.GetBytes(id).CopyTo(bytes, 12); data.CopyTo(bytes, 24); return bytes;
    }
    private static byte[] Field(string signature, byte[] data)
    {
        var bytes = new byte[6 + data.Length]; Encoding.ASCII.GetBytes(signature).CopyTo(bytes, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4), checked((ushort)data.Length)); data.CopyTo(bytes, 6); return bytes;
    }
    private static byte[] Text(string value) => Encoding.ASCII.GetBytes(value + '\0');
    private static byte[] Join(params byte[][] values) => values.SelectMany(value => value).ToArray();
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Reject(Action action)
    {
        try { action(); } catch (Exception error) when (error is InvalidDataException or NotSupportedException) { return; }
        throw new InvalidOperationException("Invalid VATS melee input was accepted.");
    }
}
