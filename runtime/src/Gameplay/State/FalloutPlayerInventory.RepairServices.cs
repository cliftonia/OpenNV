using OpenNV.Runtime.Content;

namespace OpenNV.Runtime.Gameplay.State;

internal sealed partial class FalloutPlayerInventory
{
    // Vendor repair re-quotes the selected instance, then raises its condition
    // and pays the vendor in one inventory transaction.
    internal FalloutRepairQuote RepairByVendor(FalloutPluginStack records, FalloutFormKey item, int variant, float vendorSkill,
        FalloutFormKey caps, long revision)
    {
        RequireRevision(revision);
        var quote = FalloutRepairServices.Quotes(records, this, vendorSkill)
            .SingleOrDefault(value => value.Instance.Form == item && value.Instance.Variant == variant) ??
            throw new InvalidOperationException("Selected repair item is no longer damaged.");
        if (quote.Target is not { } target) throw new InvalidOperationException("The vendor's Repair skill is too low for this item.");
        if ((Item(caps)?.Count ?? 0) < quote.Cost) throw new InvalidOperationException("The player cannot pay for this repair.");
        var selected = quote.Instance.Extra;
        var weapon = Item(item)!.RecordType == "WEAP";
        ApplyRemovalTransaction(null, (staged, _) =>
        {
            staged.ReplaceInstances(item, [(variant, 1)], [selected with
            {
                Count = 1, Condition = target >= 1 ? null : target,
                Instance = weapon ? selected.Instance ?? Guid.NewGuid() : null,
            }]);
            if (quote.Cost > 0) staged.ReplaceInstances(caps, [(0, quote.Cost)], []);
        });
        return quote;
    }
}
