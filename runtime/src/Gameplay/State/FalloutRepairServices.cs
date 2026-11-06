using OpenNV.Runtime.Content;

namespace OpenNV.Runtime.Gameplay.State;

// One damaged weapon/armor instance and what this vendor would do to it.
// A null Target means the vendor's Repair skill cannot improve the item.
internal sealed record FalloutRepairQuote(FalloutInventoryInstance Instance, float Condition, float? Target, int Cost);

/// <summary>ShowRepairMenu vendor repair: the vendor's Repair skill caps the restored condition and caps pay the value gained.</summary>
internal static class FalloutRepairServices
{
    internal const uint MenuId = 1058;

    // Every damaged weapon/armor instance the player carries, in inventory order.
    internal static IReadOnlyList<FalloutRepairQuote> Quotes(FalloutPluginStack records, FalloutPlayerInventory inventory, float vendorSkill)
    {
        if (!float.IsFinite(vendorSkill)) throw new InvalidDataException("Vendor Repair skill is not finite.");
        var limit = Math.Clamp(vendorSkill / 100f, 0, 1);
        return inventory.Items.Where(item => item.RecordType is "WEAP" or "ARMO")
            .SelectMany(item => inventory.Instances(item.FormKey).Select(instance => (item, instance)))
            .Where(pair => (pair.instance.Extra.Condition ?? 1) < 1)
            .Select(pair =>
            {
                var condition = pair.instance.Extra.Condition ?? 1;
                if (limit <= condition) return new FalloutRepairQuote(pair.instance, condition, null, 0);
                return new FalloutRepairQuote(pair.instance, condition, limit, Cost(records, pair.item, condition, limit));
            }).ToArray();
    }

    // The price is the trade value the repair adds, scaled by fItemRepairCostMult.
    internal static int Cost(FalloutPluginStack records, FalloutCampaignItem item, float condition, float target)
    {
        if (target <= condition || target > 1) throw new InvalidDataException("Vendor repair does not improve the item.");
        var value = item.Value ?? throw new NotSupportedException($"Item {item.FormKey} has no source value for repair pricing.");
        float Worth(float at) => FalloutInventoryItemStatistics.ConditionValue(records, value, at);
        var cost = Math.Ceiling((double)(Worth(target) - Worth(condition)) * records.NumericSettings.Float("fItemRepairCostMult"));
        if (!double.IsFinite(cost) || cost < 0 || cost > int.MaxValue)
            throw new NotSupportedException($"Item {item.FormKey} repair price exceeds runtime storage.");
        return (int)cost;
    }
}
