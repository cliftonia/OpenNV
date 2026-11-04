using System.Buffers.Binary;
using System.Text.Json;
using OpenNV.Runtime.Content;
using OpenNV.Runtime.Gameplay.State;

internal static partial class InventoryQueryContracts
{
    // ShowRepairMenu: the vendor's Repair skill caps the restored condition and
    // fItemRepairCostMult prices the source condition value the repair adds.
    private static void RepairServiceQueries()
    {
        var directory = Directory.CreateTempSubdirectory("opennv-repair-services-");
        try
        {
            var data = new byte[15]; BinaryPrimitives.WriteInt32LittleEndian(data, 100); BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(4), 100);
            var stats = new byte[204]; BinaryPrimitives.WriteUInt32LittleEndian(stats, 3);
            byte[] Setting(uint id, string name, float value) => Record("GMST", id, Field("EDID", Text(name)), Field("DATA", BitConverter.GetBytes(value)));
            File.WriteAllBytes(Path.Combine(directory.FullName, "Vendor.esm"), Join(Header(),
                Record("WEAP", 0x200, Field("EDID", Text("VendorGun")), Field("DATA", data), Field("DNAM", stats)),
                Record("MISC", 0x201, Field("EDID", Text("Caps001")), Field("DATA", new byte[8])),
                Setting(0x300, "fItemConditionValueBase", 0), Setting(0x301, "fItemConditionValueMult", .1f),
                Setting(0x302, "fItemConditionValueExp", 1), Setting(0x303, "fItemRepairCostMult", 2)));
            using var records = FalloutPluginStack.Load(directory.FullName, ["Vendor.esm"]);
            FalloutFormKey Key(uint id) => new("Vendor.esm", id);
            var inventory = new FalloutPlayerInventory(3);
            inventory.Add(records, Key(0x200), 1, 1, true, extra: new(1, Condition: .2f));
            inventory.Add(records, Key(0x201), 70, 1, true);
            var quote = FalloutRepairServices.Quotes(records, inventory, 60).Single();
            Check(quote.Target == .6f && quote.Cost == 80, $"Vendor repair quote lost its skill cap or source price: {quote}");
            Check(FalloutRepairServices.Quotes(records, inventory, 15).Single().Target is null,
                "A vendor below the item's condition offered a repair.");
            var before = JsonSerializer.Serialize(inventory.Capture());
            Reject(() => inventory.RepairByVendor(records, Key(0x200), 0, 60, Key(0x201), inventory.Revision));
            Check(JsonSerializer.Serialize(inventory.Capture()) == before, "An unaffordable vendor repair mutated inventory.");
            inventory.Add(records, Key(0x201), 10, 1, true);
            inventory.RepairByVendor(records, Key(0x200), 0, 60, Key(0x201), inventory.Revision);
            Check(inventory.Instances(Key(0x200)).Single().Extra.Condition == .6f && inventory.Item(Key(0x201)) is null,
                "Vendor repair did not raise condition to the vendor cap and take the quoted caps.");
            Check(FalloutRepairServices.Quotes(records, inventory, 60).Single().Target is null,
                "A repaired item remained repairable by the same vendor.");
            inventory.RepairByVendor(records, Key(0x200), 0, 100, Key(0x201), inventory.Revision);
        }
        catch (InvalidOperationException error) when (error.Message.Contains("cannot pay", StringComparison.Ordinal))
        {
            // The final full repair has no caps left; the refusal is the proof.
            Console.WriteLine("OPENNV_REPAIR_SERVICES_CONTRACT_PASS vendorSkillCap=true sourcePrice=true unaffordableRefused=true " +
                "conditionAndCapsCommitted=true repairedItemExhausted=true");
            return;
        }
        finally { directory.Delete(true); }
        throw new InvalidDataException("Vendor repair without caps was admitted.");
    }
}
