using OpenNV.Runtime.Content;

namespace OpenNV.Runtime.Gameplay.State;

internal sealed partial class FalloutPlayerInventory
{
    // Adds a signed condition fraction to the selected worn weapon instance.
    // Full condition is stored as absent extra data, matching new stacks.
    internal void ModWeaponCondition(FalloutPluginStack records, FalloutFormKey weapon, double fraction)
    {
        if (!double.IsFinite(fraction)) throw new InvalidDataException("Weapon condition change is not finite.");
        if (Item(weapon) is null) throw new InvalidDataException("Equipped weapon has no inventory instance.");
        var selected = SelectedWeapon(weapon);
        var source = FalloutWeaponPresentation.Read(records, weapon, weaponMods: selected.Extra.WeaponMods);
        if (source.ConditionHealth <= 0) return; // A weapon without health has no condition to change.
        var current = selected.Extra.Condition ?? 1;
        var next = (float)Math.Clamp(current + fraction, 0, 1);
        if (next == current) return;
        ApplyRemovalTransaction(null, (staged, _) =>
            staged.ReplaceInstances(weapon, [(selected.Variant, 1)],
                [selected.Extra with { Count = 1, Condition = next >= 1 ? null : next }]));
    }
}
