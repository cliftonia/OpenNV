using System.Buffers.Binary;
using System.Text;
using OpenNV.Runtime.Content;
using OpenNV.Runtime.Gameplay.State;
using OpenNV.Runtime.World.Cells;

// Synthetic proof of the hit damage pipeline: attacker formula, critical roll
// and target mitigation order (DR, DT, then ammo/perk damage, floor, resistance).
internal static class CombatDamageContracts
{
    internal static void Run()
    {
        var directory = Path.Combine(Path.GetTempPath(), "opennv-combat-damage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllBytes(Path.Combine(directory, "Combat.esm"), Join(Header(),
                Weapon(0x20, type: 3, resistance: 61, critical: Critical(20, 2, onDeath: true, effect: 0x30)),
                Weapon(0x21, type: 1, resistance: -1, critical: null, automatic: true, clip: 10),
                Record("SPEL", 0x30),
                Setting(1, "fDamageWeaponMult", 1), Setting(2, "fDamageSkillBase", .5f), Setting(3, "fDamageSkillMult", .5f),
                Setting(4, "fDamageArmConditionBase", .2f), Setting(5, "fDamageArmConditionMult", .8f),
                Setting(6, "fMinDamMultiplier", .2f), Setting(7, "fMaxArmorRating", 70),
                Setting(8, "fVATSCriticalChanceBonus", 5), Setting(9, "fCombatSneakAttackBonusMult", 100),
                Setting(10, "fCombatUnarmedCritDamageMult", 1), Setting(11, "fVATSStealthMult", .5f),
                Setting(12, "fVATSMeleeArmConditionBase", 1), Setting(13, "fVATSMeleeArmConditionMult", 0),
                Setting(14, "fVATSMeleeChanceMult", 1.5f), Setting(15, "fVATSMaxChance", 95)));
            using var records = FalloutPluginStack.Load(directory, ["Combat.esm"]);
            FalloutFormKey Key(uint id) => new("Combat.esm", id);
            var inventory = new FalloutPlayerInventory();
            inventory.Publish([new(Key(0x20), 0x20, "Pistol", "WEAP", 1, 1, 1), new(Key(0x21), 0x21, "Ripper", "WEAP", 1, 1, 1)]);
            var vats = false;
            FalloutPerkEntry Entry(byte entry, byte function, float value) => new(entry, function, value, []);
            var perks = new[] { Entry(0, 3, 1.5f), Entry(58, 2, 5), Entry(1, 2, 10), Entry(2, 3, 2) };
            var resolver = new FalloutWeaponDamageResolver(records, inventory,
                value => value switch { 41 => 50, 38 => 50, 14 => 5, 17 => 4, _ => throw new InvalidDataException($"Unexpected attacker AV {value}.") },
                () => perks, new(() => (1, .5f), () => 1, () => vats));

            // Pistol: right arm only (.5) -> armFactor .6; skill 50 -> .5 + .5 * .5 * .6 = .65.
            var hit = resolver.Resolve(Key(0x20), 20);
            var critical = hit.Critical ?? throw new InvalidOperationException("Weapon CRDT was not read.");
            Require(Near(hit.Amount, 13) && hit.PerkScale == 1.5f && hit.PerkOffset == 0 && hit.ThresholdReduction == 5 &&
                hit.ResistanceActorValue == 61 && critical is { Chance: 20, Damage: 40, OnDeath: true, Vats: false } &&
                critical.Effect == Key(0x30), "Attacker formula, arm factor, post-threshold perk map or critical data changed.");
            // Teammate scaling (Charisma 10) multiplies dealt damage; MeleeDamage joins melee hits.
            var teammate = new FalloutWeaponDamageResolver(records, inventory, value => value is 38 ? 50 : value is 17 ? 4 : 5, () => [],
                new(() => (1, 1), () => FalloutHitTarget.CompanionScale(10), () => false));
            var melee = teammate.Resolve(Key(0x21), 20);
            Require(Near(melee.Amount, (20 * (.5f + .5f * .5f) + 4) * 1.5f) && melee.AutomaticMeleeRounds == 10 && melee.Critical is null,
                "Companion scale, MeleeDamage addend or automatic melee clip was lost.");

            // Mitigation: DR 50 then DT (10 - 5 attacker + 0 defender), then ammo x2 and perk x1.5,
            // the 20% floor, 20% weapon resistance and the 0.75 VATS player multiplier.
            var ammo = new FalloutAmmoEffect(Key(0x40), FalloutAmmoEffect.Damage, 1, 2);
            var shot = new FalloutWeaponDamage(100, 1, 50, 1, [ammo], PerkScale: 1.5f, ThresholdReduction: 5);
            var defense = new FalloutActorDefense(10, 50);
            Require(Near(defense.Mitigate(records, shot, new(WeaponResistance: 20, VatsMultiplier: .75f)), 81),
                "Mitigation order is not DR, DT, ammo/perk damage, floor, weapon resistance, VATS.");
            Require(Near(defense.Mitigate(records, shot with { AmmoEffects = null, PerkScale = 1 }, new()), 45) &&
                Near(new FalloutActorDefense(0, 95).Mitigate(records, shot with { AmmoEffects = null, PerkScale = 1, ThresholdReduction = 0 }, new()), 30),
                "Resistance is not capped by fMaxArmorRating or threshold ignored the attacker reduction.");
            Require(Near(new FalloutActorDefense(1000, 0).Mitigate(records, shot, new()), 20),
                "Minimum damage floor did not follow the post-threshold terms.");
            Require(Near(new FalloutActorDefense(4, 40).Mitigate(records, shot with { AmmoEffects = null, PerkScale = 1, ThresholdReduction = 0 },
                new(TeammateScale: FalloutHitTarget.CompanionScale(10), ThresholdBonus: 2)), 32),
                "Teammate Charisma or the defender threshold entry did not reach DR/DT.");
            Require(Near(defense.Mitigate(records, new FalloutWeaponDamage(100, 1, 50, 1, AutomaticMeleeRounds: 20), new()), 25),
                "Automatic melee did not divide the threshold by its clip.");

            // Criticals: chance 20% -> 200 per mille; the target's entry 36 and VATS bonus apply first.
            float Fixed(float value) => value;
            Require(FalloutCriticalHits.Roll(records, critical, Fixed, false, () => .199f).Critical &&
                !FalloutCriticalHits.Roll(records, critical, Fixed, false, () => .2f).Critical &&
                !FalloutCriticalHits.Roll(records, critical, chance => chance - 20, false, () => 0).Critical &&
                FalloutCriticalHits.Roll(records, critical with { Vats = true }, Fixed, false, () => .249f).Critical &&
                FalloutCriticalHits.Roll(records, critical, Fixed, true, () => .999f) is { Critical: true, SneakAttack: true, Damage: 40 },
                "Critical roll lost its per-mille conversion, target entry, VATS bonus or sneak multiplier.");
            var outcome = FalloutCriticalHits.Roll(records, critical, Fixed, false, () => 0);
            Require(outcome.EffectFor(false) is null && outcome.EffectFor(true) == Key(0x30) &&
                (outcome with { OnDeath = false }).EffectFor(false) == Key(0x30), "On Death critical effects were cast on nonlethal hits.");
            Reject(() => FalloutCriticalHits.Roll(records, critical, Fixed, false, null));
            // VATS stealth: x0.5 before the 95% cap, cancelled by improved detection.
            var stealth = FalloutVatsRangedChance.StealthMultiplier(records, true, false);
            var melee95 = new FalloutVatsMeleeChance(records);
            Require(stealth == .5f && FalloutVatsRangedChance.StealthMultiplier(records, true, true) == 1 &&
                FalloutVatsRangedChance.StealthMultiplier(records, false, false) == 1 &&
                melee95.Calculate(1, 1, 1) == 95 && melee95.Calculate(1, 1, stealth) == 75,
                "VATS stealth penalty did not precede the cap or ignored improved detection.");
            vats = true;
            Require(resolver.Resolve(Key(0x20), 20).Critical!.Vats, "VATS state did not reach the critical data.");
            Console.WriteLine("OPENNV_COMBAT_DAMAGE_CONTRACT_PASS armFactor=true companion=true resistanceCap=fMaxArmorRating70 " +
                "order=DR,DT,ammo,perk,floor,resistance,vats automaticMelee=true critical=per-mille onDeath=true vatsStealth=pre-cap");
        }
        finally { Directory.Delete(directory, true); }
    }

    private static bool Near(float value, float expected) => MathF.Abs(value - expected) < .0001f;

    private static byte[] Critical(ushort damage, float multiplier, bool onDeath, uint effect)
    {
        var data = new byte[16];
        BinaryPrimitives.WriteUInt16LittleEndian(data, damage);
        BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(4), multiplier);
        data[8] = (byte)(onDeath ? 1 : 0);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(12), effect);
        return data;
    }

    private static byte[] Weapon(uint id, uint type, int resistance, byte[]? critical, bool automatic = false, byte clip = 0)
    {
        var dnam = new byte[124];
        BinaryPrimitives.WriteUInt32LittleEndian(dnam, type);
        if (automatic) dnam[12] = 2;
        BinaryPrimitives.WriteInt32LittleEndian(dnam.AsSpan(104), type == 1 ? 38 : 41);
        BinaryPrimitives.WriteSingleLittleEndian(dnam.AsSpan(116), 1);
        BinaryPrimitives.WriteInt32LittleEndian(dnam.AsSpan(120), resistance);
        var data = new byte[15]; data[14] = clip;
        return Record("WEAP", id, Field("DATA", data), Field("DNAM", dnam), critical is null ? [] : Field("CRDT", critical));
    }

    private static byte[] Header() => Record("TES4", 0, Field("HEDR", new byte[12]));
    private static byte[] Setting(uint id, string name, float value) =>
        Record("GMST", id, Field("EDID", Text(name)), Field("DATA", BitConverter.GetBytes(value)));
    private static byte[] Text(string value) => Encoding.ASCII.GetBytes(value + '\0');
    private static byte[] Join(params byte[][] fields) => fields.SelectMany(value => value).ToArray();
    private static byte[] Field(string name, byte[] data)
    {
        var bytes = new byte[6 + data.Length]; Encoding.ASCII.GetBytes(name).CopyTo(bytes, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4), checked((ushort)data.Length)); data.CopyTo(bytes, 6); return bytes;
    }
    private static byte[] Record(string name, uint id, params byte[][] fields)
    {
        var data = Join(fields); var bytes = new byte[24 + data.Length]; Encoding.ASCII.GetBytes(name).CopyTo(bytes, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), (uint)data.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), id); data.CopyTo(bytes, 24); return bytes;
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Reject(Action action)
    {
        try { action(); } catch (Exception error) when (error is InvalidDataException or NotSupportedException) { return; }
        throw new InvalidOperationException("Unsupported combat state was accepted.");
    }
}
