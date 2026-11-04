using OpenNV.Runtime.Content;

namespace OpenNV.Runtime.World.Cells;

// Live-state answers for CTDA functions whose state already has a world owner.
internal sealed partial class FalloutReferenceWorld
{
    // IsCombatTarget: the caller's current combat engagement names the target.
    internal bool IsCombatTarget(FalloutFormKey caller, FalloutFormKey target)
    {
        if (caller == _enginePlayer) throw new NotSupportedException("Player combat target has no combat-target owner.");
        var actor = Actor(caller);
        return actor.Engagement?.Target is { } current && current == target && actor.Injury?.Dead != true;
    }

    // GetPlayerTeammateCount: living actors currently flagged as teammates.
    internal int PlayerTeammateCount() => _instances.Values.Count(instance =>
        instance.PlayerTeammate && !instance.Deleted && instance.Injury?.Dead != true);

    // Exists: a non-null placed reference that the winning source has not deleted.
    internal bool ReferenceExists(FalloutFormKey reference)
    {
        if (reference == _enginePlayer) return true;
        if (!records.TryGetWinner(reference, out var source) || source.IsDeleted ||
            !FalloutCellSceneReader.IsReferenceType(source.Signature)) return false;
        return !_instances.TryGetValue(reference, out var instance) || !instance.Deleted;
    }
}
