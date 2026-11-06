using OpenNV.Runtime.Content;
using OpenNV.Runtime.Gameplay.State;
using OpenNV.Runtime.World.Actors;

// Hardcore water/food/soda need effects and the Doctor's Bag limb-condition
// archetype, applied through the ordinary Aid transaction.
internal static partial class IngestibleContracts
{
    private static void HardcoreNeedEffects(FalloutPluginStack records, FalloutPlayerVitals vitals, FalloutBodyPartData parts)
    {
        FalloutFormKey Key(uint id) => new("Test.esm", id);
        var inventory = new FalloutPlayerInventory();
        foreach (var id in new[] { 0x50u, 0x51u, 0x52u }) inventory.Add(records, Key(id), 2, 1, true);
        var hardcore = false;
        var owner = new FalloutPlayerIngestibles(records, inventory, vitals, parts, _ => 50, _ => false, () => hardcore);
        var health = vitals.State.ExactHitPoints;
        owner.Prepare(Key(0x50)).Commit();
        Check(vitals.State.Dehydration == 130 && vitals.State.ExactHitPoints == health,
            "A Hardcore-only water effect changed needs outside Hardcore.");
        hardcore = true;
        // Restore lowers the need (medicine-scaled 50 -> 100) and stops at zero.
        owner.Prepare(Key(0x50)).Commit();
        Check(vitals.State.Dehydration == 30, "Restore Dehydration did not lower the need by its scaled magnitude.");
        // Damage raises it, like radiation.
        owner.Prepare(Key(0x52)).Commit();
        Check(vitals.State.Dehydration == 40, "Damage Dehydration did not raise the need.");
        Check(vitals.State.WithNeed(73, -5).Dehydration == 0, "A need was restored below zero.");
        // Limb Condition restores every limb by its magnitude and leaves Health.
        var limbsBefore = vitals.State.LimbDamage![1];
        health = vitals.State.ExactHitPoints;
        owner.Prepare(Key(0x51)).Commit();
        Check(vitals.State.ExactHitPoints == health && vitals.State.LimbDamage![1] < limbsBefore,
            "Limb Condition healed Health or left the damaged limb untouched.");
        Reject(() => FalloutIngestibleApplications.Validate(new(0, Key(17), new string('0', 64), 35, -1, 0x2070, 36, 5, 0, 0,
            false, []), _ => false));
        Console.WriteLine("OPENNV_HARDCORE_INGESTIBLE_PASS needsHardcoreOnly=true restoreLowers=true damageRaises=true " +
            "clampZero=true limbConditionLimbsOnly=true timedLimbConditionRefused=true");
    }
}
