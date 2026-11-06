using System.Buffers.Binary;
using OpenNV.Runtime.Content;

namespace OpenNV.Runtime.Gameplay.State;

// Amount is the attacker-side hit damage that enters the target's mitigation.
// The ammo damage effect and Calculate Weapon Damage perks run after the
// target's threshold, so they travel as an affine perk map (scale, offset).
internal readonly record struct FalloutWeaponDamage(float Amount, float LimbMultiplier, float Skill, float Condition,
    IReadOnlyList<FalloutAmmoEffect>? AmmoEffects = null, Guid? WeaponScriptInstance = null,
    float PerkScale = 1, float PerkOffset = 0, float ThresholdReduction = 0, int ResistanceActorValue = -1,
    FalloutCriticalData? Critical = null, int AutomaticMeleeRounds = 0)
{
    // Post-threshold attacker terms: the ammo damage effect, then entry 0.
    internal float AfterThreshold(float damage)
    {
        foreach (var effect in AmmoEffects ?? [])
            if (effect.Type == FalloutAmmoEffect.Damage) damage = Math.Max(0, effect.Apply(damage));
        damage = damage * PerkScale + PerkOffset;
        return float.IsFinite(damage) ? Math.Max(0, damage) : throw new InvalidDataException("Post-threshold weapon damage is not finite.");
    }

    // Objects without armor take the attacker damage with its post-threshold terms.
    internal float Unmitigated => AfterThreshold(Amount);
}

// WEAP CRDT after the attacker's entries: Chance is the attacker's percentage
// before the target's entry 36, the VATS bonus and the per-mille conversion.
internal sealed record FalloutCriticalData(float Chance, float Damage, bool OnDeath, FalloutFormKey? Effect, bool Vats);

// Attacker state the damage formula reads besides actor values: attack-arm
// condition fractions, the companion Charisma scale and whether VATS runs.
internal sealed record FalloutWeaponAttacker(Func<(float Left, float Right)> ArmConditions, Func<float> CompanionMultiplier,
    Func<bool> Vats);

internal sealed class FalloutWeaponDamageResolver(FalloutPluginStack records, FalloutPlayerInventory inventory,
    Func<int, float> actorValue, Func<IReadOnlyList<FalloutPerkEntry>> perks, FalloutWeaponAttacker attacker,
    Func<FalloutCondition, float>? evaluateCondition = null)
{
    private float Setting(string name)
    {
        var value = FalloutGameSettingFloats.Read(records, name);
        return float.IsFinite(value) ? value : throw new InvalidDataException($"Weapon damage setting {name} is not finite.");
    }

    // WEAP damage is one discharge's total. Every actual projectile contact
    // applies its share before the target's independent DT/DR and limb rules.
    // AMMO projectile overrides therefore preserve total damage (e.g. a slug),
    // rather than multiplying damage by the number of emitted projectiles.
    internal FalloutWeaponDamage Resolve(FalloutWeaponShot shot)
    {
        if (shot.Projectiles <= 0) throw new InvalidDataException("Projectile damage requires a positive emitted count.");
        var damage = Resolve(shot.Weapon, shot.BaseDamage, shot.AmmoEffects);
        return damage with { Amount = damage.Amount / shot.Projectiles, PerkOffset = damage.PerkOffset / shot.Projectiles };
    }

    internal FalloutWeaponDamage Resolve(FalloutWeaponShot shot, float baseDamage)
        => Resolve(shot.Weapon, baseDamage, shot.AmmoEffects);

    internal FalloutWeaponDamage Resolve(FalloutFormKey form, float baseDamage)
        => Resolve(form, baseDamage, []);

    internal FalloutWeaponDamage ResolveInventory(FalloutFormKey form, float baseDamage,
        FalloutItemVariant selected, IReadOnlyList<FalloutAmmoEffect> ammoEffects)
    {
        if (!inventory.Instances(form).Any(instance => instance.Extra == selected))
            throw new InvalidOperationException("Inventory damage selected variant is stale.");
        return Resolve(form, baseDamage, ammoEffects, selected);
    }

    private FalloutWeaponDamage Resolve(FalloutFormKey form, float baseDamage, IReadOnlyList<FalloutAmmoEffect> ammoEffects,
        FalloutItemVariant? selected = null)
    {
        var weapon = records.GetEffective(form);
        var fields = weapon.ReadSubrecords().ToArray();
        var data = fields.Single(field => field.Signature == "DNAM").Data.Span;
        if (data.Length < 120) throw new NotSupportedException("Weapon limb/skill layout is unbound.");
        var type = BinaryPrimitives.ReadUInt32LittleEndian(data);
        var skill = actorValue(BinaryPrimitives.ReadInt32LittleEndian(data[104..]));
        var limb = FalloutProjectile.Number(data, 116);
        // DNAM before the resistance field (shorter source layouts) declares none.
        var resistance = data.Length >= 124 ? BinaryPrimitives.ReadInt32LittleEndian(data[120..]) : -1;
        var unarmed = selected is null && form == FalloutDefaultUnarmedWeapon.Form(records) && type == 0 &&
            !inventory.Items.Any(item => item.RecordType == "WEAP" && inventory.Equipped.Contains(item.RuntimeFormId));
        var item = inventory.Item(form);
        if (!unarmed && item is null) throw new InvalidOperationException("Used weapon is absent from inventory.");
        var condition = unarmed ? 1 : selected is null ? FalloutWeaponCondition.SelectedCondition(item!) : selected.Condition ?? 1;
        baseDamage = FalloutWeaponModifications.Damage(records, form, unarmed ? (byte)0 : selected?.WeaponMods ?? inventory.WeaponModMask(form), baseDamage);
        // The attack arm is both arms for two-handed animation types, else the right.
        var arms = attacker.ArmConditions();
        if (!float.IsFinite(arms.Left) || arms.Left is < 0 or > 1 || !float.IsFinite(arms.Right) || arms.Right is < 0 or > 1)
            throw new InvalidDataException("Attack-arm condition is not a fraction.");
        var arm = type is 0 or 2 or (>= 5 and <= 9) ? (arms.Left + arms.Right) / 2 : arms.Right;
        var armFactor = Setting("fDamageArmConditionBase") + Setting("fDamageArmConditionMult") * arm;
        var handToHand = unarmed || type == 0;
        var skillFactor = handToHand ? armFactor : Setting("fDamageSkillBase") + Setting("fDamageSkillMult") * skill / 100 * armFactor;
        var addend = type is 1 or 2 or 13 ? actorValue(17) : handToHand ? actorValue(56) : 0;
        var companion = attacker.CompanionMultiplier();
        var damage = (baseDamage * Setting("fDamageWeaponMult") * skillFactor + addend) * NewVegasConditionMultiplier(condition) * companion;
        var (perkScale, perkOffset) = PerkMap(0);
        var thresholdReduction = Entry(58, 0);
        if (!float.IsFinite(damage) || damage < 0 || limb < 0 || !float.IsFinite(companion) || companion <= 0)
            throw new InvalidDataException("Weapon damage is invalid.");
        var sourceInstance = unarmed ? null : (selected ?? inventory.SelectedWeapon(form).Extra).ScriptState?.Identity;
        if (!unarmed && sourceInstance is null && FalloutScriptLocals.AttachedScript(records, weapon) is not null)
            throw new NotSupportedException("Weapon damage has no actual retained source item event-list.");
        var automaticMelee = type is 1 or 2 && (data[12] & 2) != 0 ? ClipRounds(fields) : 0;
        return new(damage, limb, skill, condition, ammoEffects, sourceInstance, perkScale, perkOffset, thresholdReduction,
            resistance, Critical(weapon, fields, unarmed, damage), automaticMelee);
    }

    private FalloutCriticalData? Critical(FalloutPluginRecord weapon, IReadOnlyList<FalloutPluginSubrecord> fields, bool unarmed, float damage)
    {
        var source = fields.SingleOrDefault(field => field.Signature == "CRDT");
        if (source.Signature is null) return null;
        var data = source.Data.Span;
        if (data.Length != 16) throw new NotSupportedException($"WEAP CRDT extent {data.Length} is unbound.");
        var multiplier = FalloutProjectile.Number(data, 4);
        var chance = Entry(1, actorValue(14) * multiplier);
        // Without a weapon the hand-to-hand fallback scales the hit's own damage.
        var critical = Entry(2, unarmed ? damage * Setting("fCombatUnarmedCritDamageMult") : BinaryPrimitives.ReadUInt16LittleEndian(data));
        var effect = weapon.Plugin.AdjustOptionalFormId(BinaryPrimitives.ReadUInt32LittleEndian(data[12..]));
        if (!float.IsFinite(chance) || !float.IsFinite(critical)) throw new InvalidDataException("Weapon critical data is not finite.");
        return new(chance, critical, (data[8] & 1) != 0, effect, attacker.Vats());
    }

    private static int ClipRounds(IReadOnlyList<FalloutPluginSubrecord> fields)
    {
        var data = fields.Single(field => field.Signature == "DATA").Data.Span;
        if (data.Length < 15) throw new NotSupportedException("Automatic melee clip extent is unbound.");
        return Math.Max(data[14], (byte)1);
    }

    private float Entry(byte entry, float operand) => Apply(perks(), entry, operand, evaluateCondition);
    private (float Scale, float Offset) PerkMap(byte entry) => Map(perks(), entry, evaluateCondition);

    // Combat entries owned by either side of a hit (attacker, target or defender).
    internal static float Apply(IEnumerable<FalloutPerkEntry> perks, byte entry, float operand,
        Func<FalloutCondition, float>? evaluateCondition)
    {
        var (scale, offset) = Map(perks, entry, evaluateCondition);
        return operand * scale + offset;
    }

    // Active entries compose in acquired order; each set/add/multiply is affine.
    private static (float Scale, float Offset) Map(IEnumerable<FalloutPerkEntry> perks, byte entry,
        Func<FalloutCondition, float>? evaluateCondition)
    {
        float scale = 1, offset = 0;
        foreach (var perk in perks.Where(perk => perk.Entry == entry))
        {
            perk.RequireActorConditionScope();
            if (perk.Conditions.Count != 0)
            {
                if (evaluateCondition is null)
                    throw new NotSupportedException($"Conditional combat perk entry {entry} has no condition owner.");
                if (!FalloutCondition.AllPass(perk.Conditions, evaluateCondition)) continue;
            }
            (scale, offset) = perk.Function switch
            {
                1 => (0f, perk.Value),
                2 => (scale, offset + perk.Value),
                3 => (scale * perk.Value, offset * perk.Value),
                _ => throw new NotSupportedException($"Weapon perk entry {entry} operation is unbound.")
            };
        }
        return float.IsFinite(scale) && float.IsFinite(offset) ? (scale, offset) :
            throw new InvalidDataException($"Weapon perk entry {entry} is not finite.");
    }

    // NV's normal-hit and inventory-damage paths share this rule. It is flat
    // above 75% condition and loses 0.67 damage fraction per condition fraction
    // below that threshold. FO3's condition GMSTs do not own this NV curve.
    internal static float NewVegasConditionMultiplier(float condition)
    {
        if (!float.IsFinite(condition) || condition is < 0 or > 1)
            throw new InvalidDataException("Weapon condition must be a finite fraction.");
        return condition > .75f ? 1 : (float)(1 - (.75 - condition) * (double).67f);
    }
}
