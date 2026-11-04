using OpenNV.Runtime.Content;
using OpenNV.Runtime.World.Cells;

namespace OpenNV.Runtime.Gameplay.State;

// Script commands that select inventory by condition or by native form type.
// They reuse the shared add/remove-all transactions instead of a second owner.
internal sealed partial class FalloutInventoryCommands
{
    // AddItemHealthPercent condition belongs to equipment with native health.
    // Full health stays the absent extra-data value so stacks still merge.
    private FalloutItemVariant? ConditionExtra(FalloutInventoryCommand command)
    {
        if (command.Condition is not { } condition || command.Item is not { } item) return null;
        if (!float.IsFinite(condition) || condition < 0)
            throw new InvalidDataException("Inventory addition condition must be a finite nonnegative fraction.");
        if (condition >= 1 || records.GetEffective(item).Signature is not ("WEAP" or "ARMO")) return null;
        return new FalloutItemVariant(1, Condition: condition);
    }

    // RemoveAllTypedItems: a native form-type filter over the shared RemoveAll
    // transaction. The optional FLST names exempt forms.
    internal static Func<FalloutCampaignItem, bool> TypedItemFilter(FalloutPluginStack records, uint typeCode,
        FalloutFormKey? exemptions, IFalloutFormLists? formLists)
    {
        var exempt = exemptions is { } list
            ? FalloutFormList.Entries(records, list, formLists).OfType<FalloutFormKey>().ToHashSet()
            : [];
        return item => FalloutReferenceWorld.FormTypeCode(item.RecordType) == typeCode && !exempt.Contains(item.FormKey);
    }

    // ModWeaponHealthPerc adds percentage points to the equipped weapon's
    // condition, clamped to the native 0..100 range.
    internal void ModWeaponHealthPercentage(FalloutFormKey target, double delta)
    {
        if (!double.IsFinite(delta)) throw new InvalidDataException("ModWeaponHealthPerc requires a finite percentage.");
        if (EquippedObject(target, 5) is not { } weapon) return;
        var inventory = Inventory(target);
        var revision = inventory.Revision;
        inventory.ModWeaponCondition(records, weapon, delta / 100);
        if (!IsPlayer(target) && inventory.Revision != revision) world.InventoryChanged(target);
    }
}
