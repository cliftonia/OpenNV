using OpenNV.Runtime.Content;
using OpenNV.Runtime.World.Cells;

namespace OpenNV.Runtime.Gameplay.State;

internal sealed partial class FalloutPlayerInventory
{
    internal int MaximumCraftCount(FalloutRecipe recipe) => Requirements(recipe).Aggregate(100, (maximum, requirement) =>
        Math.Min(maximum, (Item(requirement.Key)?.Count ?? 0) / requirement.Value));

    internal bool CanCraft(FalloutRecipe recipe) => Requirements(recipe).All(requirement =>
        Item(requirement.Key)?.Count >= requirement.Value);

    // Without an inventory-event owner every output must be event-free.
    internal void Craft(FalloutPluginStack records, FalloutRecipe recipe, int level, FalloutGlobalState? globals,
        int quantity = 1, long? expectedRevision = null)
    {
        if (level < 1) throw new ArgumentOutOfRangeException(nameof(level));
        Craft(records, recipe, quantity, expectedRevision, null, (staged, outputs) =>
        {
            foreach (var (form, count) in outputs)
                staged.Add(records, form, count, level, silent: true, globals,
                    validateAddition: selected => FalloutReferenceWorld.RequirePlayerItemAcquisition(
                        records, records.GetEffective(selected), records.RuntimeFormKey(0x14)));
        });
    }

    // Ingredients leave the staged inventory before the output owner adds the
    // products, so OnAdd scripts observe the post-consumption inventory.
    // A scripted item owns one event list per acquisition; its outputs are
    // added one at a time, like repeated pickups.
    internal void Craft(FalloutPluginStack records, FalloutRecipe recipe, int quantity, long? expectedRevision,
        FalloutInventoryChangeLease? effects, Action<FalloutPlayerInventory, IReadOnlyList<(FalloutFormKey Form, int Count)>> addOutputs)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(recipe);
        ArgumentNullException.ThrowIfNull(addOutputs);
        if (quantity is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(quantity));
        if (expectedRevision is { } revision) RequireRevision(revision);
        var ingredients = Requirements(recipe).Select(requirement =>
            new KeyValuePair<FalloutFormKey, int>(requirement.Key, checked(requirement.Value * quantity))).ToArray();
        var outputs = recipe.Outputs.Select(output => output with { Count = checked(output.Count * quantity) }).ToArray();
        if (ingredients.Any(requirement => (Item(requirement.Key)?.Count ?? 0) < requirement.Value))
            throw new InvalidOperationException($"Recipe {recipe.Record.FormKey} ingredients are no longer available.");
        var additions = outputs.SelectMany(output =>
                FalloutScriptLocals.AttachedScript(records, records.GetEffective(output.Form)) is null
                    ? [(output.Form, output.Count)] : Enumerable.Repeat((output.Form, 1), output.Count))
            .ToArray();
        var events = ingredients.Select(requirement => new FalloutHudEvent(
                FalloutHudEventKind.ItemRemoved, requirement.Key, requirement.Value))
            .Concat(outputs.GroupBy(output => output.Form).Select(group => new FalloutHudEvent(
                FalloutHudEventKind.ItemAdded, group.Key, group.Sum(output => output.Count))))
            .ToArray();
        Notifications.RequirePublish(events);
        ApplyRemovalTransaction(null, (staged, _) =>
        {
            foreach (var (form, count) in ingredients)
                staged.RemoveCore(form, count, silent: true);
            addOutputs(staged, additions);
        }, effects);

        Notifications.Publish(events);
    }

    private static IReadOnlyList<KeyValuePair<FalloutFormKey, int>> Requirements(FalloutRecipe recipe) =>
        recipe.Ingredients.GroupBy(item => item.Form)
            .Select(group => new KeyValuePair<FalloutFormKey, int>(group.Key,
                group.Aggregate(0, (count, item) => checked(count + item.Count))))
            .ToArray();
}
