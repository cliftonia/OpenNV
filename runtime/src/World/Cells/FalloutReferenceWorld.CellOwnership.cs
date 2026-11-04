using OpenNV.Runtime.Content;

namespace OpenNV.Runtime.World.Cells;

internal sealed record FalloutCellOwnershipChange(FalloutFormKey Cell, FalloutFormKey Owner);

// SetCellOwnership replaces a CELL's XOWN owner; inherited reference ownership
// reads it through LocalOwnership. Saved as one optional campaign field.
internal sealed record FalloutCellOwnershipSnapshot(IReadOnlyList<FalloutCellOwnershipChange> Changes)
{
    internal void Validate()
    {
        static bool Valid(FalloutFormKey key) => key.ObjectId != 0 && !string.IsNullOrWhiteSpace(key.OwnerPlugin);
        if (Changes is null || Changes.Any(value => value is null || !Valid(value.Cell) || !Valid(value.Owner)) ||
            Changes.Select(value => value.Cell).Distinct().Count() != Changes.Count)
            throw new InvalidDataException("Saved CELL ownership is invalid or duplicated.");
    }
}

internal sealed partial class FalloutReferenceWorld
{
    private readonly Dictionary<FalloutFormKey, FalloutFormKey> _cellOwnership = [];
    private FalloutForcedTriggerEvents? _forcedTriggers;
    internal FalloutForcedTriggerEvents ForcedTriggers => _forcedTriggers ??= new();

    private FalloutFormKey? CellOwnershipOverride(FalloutFormKey cell) =>
        _cellOwnership.TryGetValue(cell, out var owner) ? owner : null;

    // An absent owner is the player, matching SetOwnership.
    internal void SetCellOwnership(FalloutFormKey cell, FalloutFormKey? owner)
    {
        if (records.GetEffective(cell).Signature != "CELL") throw new InvalidDataException("SetCellOwnership target is not a CELL.");
        var selected = owner ?? records.RuntimeFormKey(7);
        RequireCommandOwner(selected);
        _cellOwnership[cell] = selected;
    }

    internal FalloutCellOwnershipSnapshot? CaptureCellOwnership() => _cellOwnership.Count == 0 ? null : new(
        _cellOwnership.OrderBy(pair => records.RuntimeFormId(pair.Key)).Select(pair => new FalloutCellOwnershipChange(pair.Key, pair.Value)).ToArray());

    internal void RestoreCellOwnership(FalloutCellOwnershipSnapshot? snapshot)
    {
        if (_cellOwnership.Count != 0) throw new InvalidOperationException("CELL ownership restore requires a fresh world.");
        if (snapshot is null) return;
        snapshot.Validate();
        foreach (var change in snapshot.Changes)
        {
            if (records.GetEffective(change.Cell).Signature != "CELL") throw new InvalidDataException("Saved CELL ownership target is not a CELL.");
            RequireCommandOwner(change.Owner);
        }
        foreach (var change in snapshot.Changes) _cellOwnership.Add(change.Cell, change.Owner);
    }
}

// EnterTrigger admits an OnTriggerEnter event for the caller on its next
// native reference frame, exactly like a physical contact would. The queue is
// frame-transient and not saved; a request is consumed on the target's next
// processed frame.
internal sealed class FalloutForcedTriggerEvents
{
    private readonly Dictionary<FalloutFormKey, Queue<FalloutFormKey>> _pending = [];

    internal bool HasPending(FalloutFormKey trigger) => _pending.ContainsKey(trigger);

    internal void Enter(FalloutFormKey trigger, FalloutFormKey actor)
    {
        if (!_pending.TryGetValue(trigger, out var queue)) _pending.Add(trigger, queue = new());
        queue.Enqueue(actor);
    }

    // One enter per frame; a physical enter in the same frame keeps precedence.
    internal FalloutReferenceScriptEvent? Take(FalloutFormKey trigger, bool contactEnterAdmitted)
    {
        if (contactEnterAdmitted || !_pending.TryGetValue(trigger, out var queue)) return null;
        var actor = queue.Dequeue();
        if (queue.Count == 0) _pending.Remove(trigger);
        return new("OnTriggerEnter", actor);
    }

    internal void Clear(FalloutFormKey trigger) => _pending.Remove(trigger);
}
