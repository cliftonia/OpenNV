using OpenNV.Runtime.Content;

namespace OpenNV.Runtime.World.Cells;

internal sealed partial class FalloutReferenceWorld
{
    // PKDT door flags lock or unlock the doors of the actor's CELL when its
    // package starts (0x8 lock, 0x40 unlock) or ends or changes (0x10 lock,
    // 0x80 unlock). Only doors with an effective lock participate; a lockless
    // door is never given a lock. The "at location" bits are deferred.
    internal void ApplyPackageDoorActions(FalloutFormKey actor, FalloutScriptPackage package, string kind)
    {
        var (lockFlag, unlockFlag) = kind switch
        {
            "POBA" => (0x8u, 0x40u),
            "POEA" or "POCA" => (0x10u, 0x80u),
            _ => throw new InvalidDataException("Package door actions have no typed lifecycle phase.")
        };
        if ((package.Flags & 0x120u) != 0)
            FalloutPackageDeferral.Warn("package-doors-at-location", "PKDT lock/unlock doors at location has no arrival owner.");
        var locking = (package.Flags & lockFlag) != 0;
        var unlocking = (package.Flags & unlockFlag) != 0;
        if (!locking && !unlocking) return;
        var cell = Placement(actor).Cell;
        if (!_placementSourceCells.TryGetValue(cell, out var scene))
            _placementSourceCells.Add(cell, scene = FalloutCellSceneReader.Read(records, cell));
        // Include the complete source set and runtime moved-in doors.
        var doors = scene.References.Where(reference => scene.BaseObjects[reference.Base].Signature == "DOOR")
            .Select(reference => reference.FormKey).Concat(_instances.Values
                .Where(instance => instance.Placement?.Cell == cell && records.GetEffective(instance.Base).Signature == "DOOR")
                .Select(instance => instance.Reference)).Distinct().ToArray();
        foreach (var door in doors)
        {
            if (Placement(door).Cell != cell || EffectiveLock(door).State is null) continue;
            // A record asking for both in one phase is resolved in favour of unlocking.
            if (unlocking) UnlockReference(door);
            else LockReference(door);
        }
    }
}
