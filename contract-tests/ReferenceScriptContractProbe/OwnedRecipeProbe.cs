using OpenNV.Runtime.Content;
using OpenNV.Runtime.Content.Definitions;
using OpenNV.Runtime.Gameplay.State;
using OpenNV.Runtime.World.Cells;

internal static class OwnedRecipeProbe
{
    internal static void Run(string game)
    {
        RuntimeLiveContentSource.Configure(game, RuntimeLiveContentSource.FalloutNewVegasGame,
            activePlugins: RuntimeLiveContentSource.ResolveVanillaPlugins(game), includeImplicitPlugins: false);
        using var content = RuntimeLiveContentSource.Current!;
        using var records = FalloutPluginStack.Load(content.PluginSources);
        using var world = new FalloutReferenceWorld(records);
        var quests = new FalloutQuestState(records);
        var globals = FalloutGlobalState.Read(records);
        var player = records.RuntimeFormKey(0x14);
        var failures = new List<string>();
        var recipes = new List<FalloutRecipe>();
        foreach (var record in records.EffectiveRecords("RCPE").Where(r => !r.IsDeleted))
        {
            try { recipes.Add(FalloutRecipe.Read(records, record)); }
            catch (Exception error) { failures.Add($"read {record.FormKey}: {error.Message}"); }
        }
        var inventory = new FalloutPlayerInventory();
        world.BindPlayerInventory(inventory);
        var questScripts = new FalloutQuestScripts(records, quests, new HashSet<FalloutFormKey>(), inventory,
            defaultProcessingDelay: 0, references: world);
        var stages = new FalloutQuestStages(records, quests, (_, _, _) => [], _ => 0);
        var acquisition = new FalloutInventoryAcquisition(records, world, inventory, quests, questScripts, () => stages, () => 1, globals);
        world.InventoryAcquisition = acquisition;
        var sounds = 0;
        using var soundBinding = questScripts.Sounds.Bind(sound => [sound.LogicalPath], request =>
            new(new(request.Selection.Path!, "owned-recipe-probe", new string('a', 64), 1), () => sounds++, _ => { }, () => { }), true);
        var cell = FalloutDialogueTopic.Find(records, "CELL", "GSDocMitchellHouse").FormKey;
        var conditions = new FalloutConditionRuntime(records, quests, world)
        {
            Globals = globals,
            HasPerk = (_, _) => false,
            ItemCount = (subject, item) => subject == player ? inventory.Item(item)?.Count ?? 0 : world.Inventory(subject, 1, globals).Contents.Item(item)?.Count ?? 0,
            Equipped = (subject, item) => subject == player ? inventory.Item(item) is { } owned && inventory.Equipped.Contains(owned.RuntimeFormId) : null,
            PlayerPlacement = () => new FalloutReferencePlacement(cell, [0, 0, 0], [0, 0, 0]),
            Native = (_, condition) => throw new NotSupportedException($"Recipe condition {condition.Kind} reached no runtime owner."),
        };
        float Evaluate(FalloutCondition condition) => conditions.Evaluate(condition, new(player, player));
        var skillNames = FalloutNativeTagSkillResolver.ResolveSkills(records).Select(skill =>
            (int)FalloutActorValueNames.Parse(FalloutNativeTagSkillResolver.ActorValueName(records, skill))).ToHashSet();
        var menuCategories = MenuCategories(records);
        var crafted = 0;
        var audio = new List<string>();
        foreach (var recipe in recipes)
        {
            try
            {
                _ = FalloutCondition.AllPass(recipe.Conditions, Evaluate, evaluateRunOn: true);
                if (recipe.RequiredSkill != -1 && !skillNames.Contains(recipe.RequiredSkill))
                    throw new InvalidDataException($"Recipe skill {recipe.RequiredSkill} has no source AVIF skill.");
                if (recipe.Category is { } category && !menuCategories.Contains(category))
                    throw new InvalidDataException($"Recipe category {category} is never opened by a source ShowRecipeMenu.");
                if (recipe.Subcategory is { } subcategory && !FalloutRecipeCategory.Read(records, subcategory).IsSubcategory)
                    throw new InvalidDataException("Recipe subcategory is not flagged as a subcategory.");
                if (recipe.Category is null) continue; // Unreachable from every menu; retained source data only.
                foreach (var quantity in new[] { 1, 2 })
                {
                    foreach (var held in inventory.Items.ToArray()) inventory.Remove(held.FormKey, held.Count, true);
                    // A scripted item owns one event list per acquisition; stock
                    // it the way repeated pickups would.
                    foreach (var ingredient in recipe.Ingredients)
                        if (FalloutScriptLocals.AttachedScript(records, records.GetEffective(ingredient.Form)) is null)
                            inventory.Add(records, ingredient.Form, ingredient.Count * quantity, 1, true, globals);
                        else for (var copy = 0; copy < ingredient.Count * quantity; copy++)
                            inventory.Add(records, ingredient.Form, 1, 1, true, globals);
                    if (inventory.MaximumCraftCount(recipe) != quantity || !inventory.CanCraft(recipe))
                        throw new InvalidDataException("Recipe admission differs from its staged ingredients.");
                    acquisition.Craft(recipe, quantity, inventory.Revision);
                    foreach (var ingredient in recipe.Ingredients.Where(value => recipe.Outputs.All(output => output.Form != value.Form)))
                        if (inventory.Item(ingredient.Form) is not null) throw new InvalidDataException("Craft retained a consumed ingredient.");
                    foreach (var group in recipe.Outputs.GroupBy(output => output.Form))
                    {
                        // Every ingredient (including a returned tool) was consumed.
                        if (inventory.Item(group.Key)?.Count != group.Sum(value => value.Count) * quantity)
                            throw new InvalidDataException($"Craft output {group.Key} count differs from its source quantity.");
                    }
                }
                ++crafted;
            }
            // Compressed-media PlaySound belongs to the audio owner; keep it
            // visible without hiding any crafting divergence behind it.
            catch (NotSupportedException error) when (error.Message.Contains("does not yet implement PlaySound", StringComparison.Ordinal))
            { audio.Add($"{recipe.Record.FormKey} {recipe.EditorId}: {error.Message}"); }
            catch (Exception error) { failures.Add($"{recipe.Record.FormKey} {recipe.EditorId}: {error.GetType().Name} {error.Message}"); }
        }
        foreach (var failure in failures) Console.WriteLine("OPENNV_OWNED_RECIPE_FAIL " + failure);
        foreach (var divergence in audio) Console.WriteLine("OPENNV_OWNED_RECIPE_AUDIO_DIVERGENCE " + divergence);
        if (failures.Count != 0) throw new InvalidDataException($"{failures.Count} owned recipes are not representable.");
        Console.WriteLine($"OPENNV_OWNED_RECIPE_PASS recipes={recipes.Count} crafted={crafted} audioDivergence={audio.Count} sounds={sounds} categories={menuCategories.Count} " +
            "conditions=true skills=true consumption=true outputCounts=true retail=unverified");
    }

    // Every RCCT a source script opens with ShowRecipeMenu, compiled into SCRO.
    private static HashSet<FalloutFormKey> MenuCategories(FalloutPluginStack records)
    {
        var result = new HashSet<FalloutFormKey>();
        foreach (var script in records.EffectiveRecords("SCPT").Where(record => !record.IsDeleted))
        {
            var fields = script.ReadSubrecords().ToArray();
            var text = fields.SingleOrDefault(field => field.Signature == "SCTX").Data;
            if (text.IsEmpty || !FalloutDialogueTopic.ScriptText(text.Span).Contains("showrecipemenu", StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var reference in fields.Where(field => field.Signature == "SCRO" && field.Data.Length == 4))
            {
                var form = script.Plugin.AdjustOptionalFormId(System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(reference.Data.Span));
                if (form is { } key && records.TryGetEffective(key, out var target) && target.Signature == "RCCT") result.Add(key);
            }
        }
        return result;
    }
}
