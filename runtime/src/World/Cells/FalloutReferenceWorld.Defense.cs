using System.Buffers.Binary;
using OpenNV.Runtime.Content;
using OpenNV.Runtime.Gameplay.State;
using OpenNV.Runtime.World.Actors;

namespace OpenNV.Runtime.World.Cells;

// Target-side terms of one hit: the Charisma scale of a player teammate, the
// defender's Modify Damage Threshold entries, the target's value of the weapon's
// resistance actor value and the player's VATS damage multiplier.
internal sealed record FalloutHitTarget(float TeammateScale = 1, float ThresholdBonus = 0,
    float WeaponResistance = 0, float VatsMultiplier = 1)
{
    // A player teammate's dealt damage, resistance and threshold scale with the player's Charisma.
    internal static float CompanionScale(float playerCharisma) => float.IsFinite(playerCharisma)
        ? 1 + .05f * playerCharisma : throw new InvalidDataException("Player Charisma is not finite.");
}

internal readonly record struct FalloutActorDefense(float Threshold, float Resistance)
{
    // Resistance (capped by fMaxArmorRating) precedes threshold. The ammo damage
    // effect and weapon damage perks follow the threshold, then the minimum floor,
    // the weapon's resistance type and, for the player in VATS, its multiplier.
    internal float Mitigate(FalloutPluginStack records, FalloutWeaponDamage hit, FalloutHitTarget target)
    {
        var damage = hit.Amount;
        if (!float.IsFinite(damage) || damage < 0 || !float.IsFinite(target.TeammateScale) || target.TeammateScale <= 0 ||
            !float.IsFinite(target.ThresholdBonus) || !float.IsFinite(target.WeaponResistance) ||
            !float.IsFinite(target.VatsMultiplier) || target.VatsMultiplier < 0)
            throw new InvalidDataException("Hit mitigation input is invalid.");
        var minimum = damage * FalloutGameSettingFloats.Read(records, "fMinDamMultiplier");
        var resistance = Math.Min(Resistance, 100) * target.TeammateScale;
        var threshold = Math.Max(Threshold, 0) * target.TeammateScale;
        foreach (var effect in hit.AmmoEffects ?? [])
        {
            if (effect.Type == FalloutAmmoEffect.DamageResistance) resistance = effect.Apply(resistance);
            else if (effect.Type == FalloutAmmoEffect.DamageThreshold) threshold = effect.Apply(threshold);
        }
        damage *= 1 - Math.Clamp(resistance, 0, FalloutGameSettingFloats.Read(records, "fMaxArmorRating")) / 100;
        threshold = Math.Max(threshold - hit.ThresholdReduction + target.ThresholdBonus, 0);
        damage = hit.AutomaticMeleeRounds > 0 ? damage * Math.Max(0, 1 - threshold / hit.AutomaticMeleeRounds) : damage - threshold;
        damage = Math.Max(hit.AfterThreshold(damage), minimum);
        var weaponResistance = Math.Min(target.WeaponResistance, 100) / 100;
        if (weaponResistance is > 0 and <= 1) damage *= 1 - weaponResistance;
        damage *= target.VatsMultiplier;
        return float.IsFinite(damage) ? damage : throw new InvalidDataException("Mitigated hit damage is not finite.");
    }
}

internal sealed partial class FalloutReferenceWorld
{
    private readonly FalloutActorDefenseResolver _actorDefense = new(records);
    internal FalloutActorDefense Defense(FalloutFormKey reference, int level, FalloutGlobalState globals)
    {
        var actor = Actor(reference);
        var equipped = EquippedArmor(reference, level, globals);
        var result = _actorDefense.Read(actor.Base, actor.Inventory!.Contents, equipped, actor.Templates);
        // DamageResist is AV18; AV12 is ActionPoints and cannot add armor.
        return new(result.Threshold + AcquiredModifier(reference, 76), result.Resistance + AcquiredModifier(reference, 18));
    }

    // The weapon resistance types (EMP, fire, energy...) have an empty base pool;
    // abilities, worn enchantments, acquired perks and script modifiers fill it.
    internal float WeaponResistance(FalloutFormKey reference, int actorValue, int level, FalloutGlobalState globals)
    {
        var actor = Actor(reference);
        var pool = actor.ActorValues.GetValueOrDefault(FalloutActorValue.SourceSlot(actorValue));
        return _actorDefense.Modifier(actor.Base, EquippedArmor(reference, level, globals), actor.Templates, actorValue) +
            AcquiredModifier(reference, actorValue) + (pool is null ? 0 : pool.Current - pool.Base);
    }

    private float AcquiredModifier(FalloutFormKey reference, int actorValue) =>
        FalloutActorDefenseResolver.Unconditional(PerkModifiers(reference), actorValue, "acquired");
}

internal sealed class FalloutActorDefenseResolver(FalloutPluginStack records)
{
    private readonly Dictionary<FalloutFormKey, IReadOnlyList<FalloutAbilityModifier>> _actorEffects = [];
    private readonly Dictionary<FalloutFormKey, FalloutArmorDefense> _armorDefense = [];
    private readonly Dictionary<FalloutFormKey, IReadOnlyList<FalloutAbilityModifier>> _armorEffects = [];
    private readonly FalloutAbilityModifiers _defenseAbilities = new(records);

    internal static float Unconditional(IEnumerable<FalloutAbilityModifier> modifiers, int actorValue, string owner)
    {
        var total = 0f;
        foreach (var effect in modifiers.Where(effect => effect.ActorValue == actorValue))
        {
            if (effect.Conditions.Count != 0) throw new NotSupportedException($"Conditional {owner} resistance requires its condition owner.");
            total += effect.Amount;
        }
        return total;
    }

    internal FalloutActorDefense Read(FalloutFormKey actor, FalloutPlayerInventory inventory, IReadOnlyList<FalloutFormKey> equipped,
        FalloutActorTemplateSelection? selection = null)
    {
        var armorThreshold = 0f; var armorResistance = 0f;
        foreach (var key in equipped)
        {
            if (!_armorDefense.TryGetValue(key, out var armor))
                _armorDefense.Add(key, armor = FalloutArmorDefense.Read(records.GetEffective(key)));
            var item = inventory.Item(key)!;
            var condition = FalloutWeaponCondition.SelectedCondition(item);
            var rating = (Base: FalloutGameSettingFloats.Read(records, "fArmorRatingBase"), Maximum: FalloutGameSettingFloats.Read(records, "fArmorRatingMax"));
            var factor = rating.Base + condition * (rating.Maximum - rating.Base);
            armorThreshold += armor.Threshold * factor;
            armorResistance += armor.Resistance * factor;
        }
        return new(armorThreshold + Modifier(actor, equipped, selection, 76), armorResistance + Modifier(actor, equipped, selection, 18));
    }

    // Constant ability (SPLO) and worn enchantment (EITM) modifiers of one actor value.
    internal float Modifier(FalloutFormKey actor, IReadOnlyList<FalloutFormKey> equipped, FalloutActorTemplateSelection? selection,
        int actorValue)
    {
        var effects = FalloutActorTemplateOwner.Resolve(records, records.GetEffective(actor), 8, selection);
        if (!_actorEffects.TryGetValue(effects.FormKey, out var abilities))
            _actorEffects.Add(effects.FormKey, abilities = Constant(effects, "SPLO"));
        var total = Unconditional(abilities, actorValue, "actor");
        foreach (var key in equipped)
        {
            if (!_armorEffects.TryGetValue(key, out var worn)) _armorEffects.Add(key, worn = Constant(records.GetEffective(key), "EITM"));
            total += Unconditional(worn, actorValue, "armor");
        }
        return total;
    }

    private IReadOnlyList<FalloutAbilityModifier> Constant(FalloutPluginRecord source, string signature)
    {
        var result = new List<FalloutAbilityModifier>();
        foreach (var field in source.ReadSubrecords().Where(field => field.Signature == signature))
        {
            if (field.Data.Length != 4) throw new InvalidDataException($"{signature} effect identity extent is invalid.");
            var form = source.Plugin.AdjustOptionalFormId(BinaryPrimitives.ReadUInt32LittleEndian(field.Data.Span));
            if (form is not null && _defenseAbilities.TryGetConstantModifiers(form.Value, out var modifiers)) result.AddRange(modifiers);
        }
        return result;
    }
}
