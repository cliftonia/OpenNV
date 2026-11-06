using OpenNV.Runtime.Content;

namespace OpenNV.Runtime.World.Cells;

internal sealed partial class FalloutReferenceWorld
{
    // PKDT door flags lock or unlock the lockable doors of the package
    // location's interior CELL: 0x8 lock / 0x40 unlock at start, 0x10 lock /
    // 0x80 unlock at end or change. Start actions apply only when the actor is
    // already in that CELL. A lockless door is never given a lock, and a door
    // whose lock a script or the player changed is left alone. Everything else
    // (exterior CELLs, unresolved locations, "at location" bits) is deferred.
    internal void ApplyPackageDoorActions(FalloutFormKey actor, FalloutScriptPackage package, string kind)
    {
        var (lockFlag, unlockFlag) = kind switch
        {
            "POBA" => (0x8u, 0x40u),
            "POEA" or "POCA" => (0x10u, 0x80u),
            _ => throw new InvalidDataException("Package door actions have no typed lifecycle phase.")
        };
        if ((package.Flags & 0x120u) != 0)
            FalloutScriptCommandDeferral.WarnPackage("package-doors-at-location", "PKDT lock/unlock doors at location has no arrival owner.");
        var locking = (package.Flags & lockFlag) != 0;
        var unlocking = (package.Flags & unlockFlag) != 0;
        if (!locking && !unlocking) return;
        if (PackageLocationCell(actor, package) is not { } cell)
        {
            FalloutScriptCommandDeferral.WarnPackage("package-doors-location", "PKDT door actions need a resolvable package location CELL.");
            return;
        }
        if (kind == "POBA" && Placement(actor).Cell != cell)
        {
            FalloutScriptCommandDeferral.WarnPackage("package-doors-start-away", "PKDT start door actions before the actor reaches its location CELL have no owner.");
            return;
        }
        if (!_placementSourceCells.TryGetValue(cell, out var scene))
            _placementSourceCells.Add(cell, scene = FalloutCellSceneReader.Read(records, cell));
        if ((scene.Cell.Flags & 1) == 0)
        {
            FalloutScriptCommandDeferral.WarnPackage("package-doors-exterior", "PKDT door actions in exterior CELLs have no world-space owner.");
            return;
        }
        var doors = scene.References.Where(reference => scene.BaseObjects[reference.Base].Signature == "DOOR")
            .Select(reference => reference.FormKey).Concat(_instances.Values
                .Where(instance => instance.Placement?.Cell == cell && records.GetEffective(instance.Base).Signature == "DOOR")
                .Select(instance => instance.Reference)).Distinct().ToArray();
        foreach (var door in doors)
        {
            if (Placement(door).Cell != cell || EffectiveLock(door).State is null) continue;
            var owner = Get(LockOwner(door));
            if (owner.LockState is { PackageActor: null } || owner.LockState is null && owner.Unlocked) continue;
            // A record asking for both in one phase is resolved in favour of unlocking.
            if (unlocking) UnlockReference(door, packageActor: actor);
            else LockReference(door, packageActor: actor);
        }
    }

    private FalloutFormKey? PackageLocationCell(FalloutFormKey actor, FalloutScriptPackage package) =>
        package.LocationDeclaration switch
        {
            { Type: 0, Form: { } reference } => Placement(reference).Cell,
            { Type: 1, Form: { } cell } => cell,
            { Type: 2 } => Get(actor).PackageStartLocation?.Cell ?? Placement(actor).Cell,
            { Type: 3 } => EditorPlacement(actor).Cell,
            { Type: 6 } => GetLinkedRef(actor) is { } linked ? Placement(linked).Cell : null,
            _ => null,
        };
}
