using OpenNV.Runtime.Content;

namespace OpenNV.Runtime.World.Cells;

internal sealed record FalloutCellOwnershipChange(FalloutFormKey Cell, FalloutFormKey Owner);
internal sealed record FalloutCellPublicChange(FalloutFormKey Cell, bool Public);

// SetCellOwnership replaces a CELL's XOWN owner; inherited reference ownership
// reads it through LocalOwnership. SetCellPublicFlag replaces the CELL DATA
// public-area bit. Both are saved as one optional campaign field.
internal sealed record FalloutCellOwnershipSnapshot(IReadOnlyList<FalloutCellOwnershipChange> Changes,
    IReadOnlyList<FalloutCellPublicChange>? Public = null)
{
    internal void Validate()
    {
        static bool Valid(FalloutFormKey key) => key.ObjectId != 0 && !string.IsNullOrWhiteSpace(key.OwnerPlugin);
        if (Changes is null || Changes.Any(value => value is null || !Valid(value.Cell) || !Valid(value.Owner)) ||
            Changes.Select(value => value.Cell).Distinct().Count() != Changes.Count ||
            Public is { } flags && (flags.Any(value => value is null || !Valid(value.Cell)) ||
                flags.Select(value => value.Cell).Distinct().Count() != flags.Count))
            throw new InvalidDataException("Saved CELL ownership is invalid or duplicated.");
    }
}

internal sealed partial class FalloutReferenceWorld
{
    private readonly Dictionary<FalloutFormKey, FalloutFormKey> _cellOwnership = [];
    private readonly Dictionary<FalloutFormKey, bool> _cellPublic = [];
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

    // CELL DATA bit 0x20 marks a public area: being there is never trespass,
    // although its owned objects remain owned.
    internal bool IsPublicCell(FalloutFormKey cell)
    {
        if (_cellPublic.TryGetValue(cell, out var overridden)) return overridden;
        var data = records.GetEffective(cell).ReadSubrecords().FirstOrDefault(field => field.Signature == "DATA").Data;
        return !data.IsEmpty && (data.Span[0] & 0x20) != 0;
    }

    internal void SetCellPublic(FalloutFormKey cell, bool value)
    {
        if (records.GetEffective(cell).Signature != "CELL") throw new InvalidDataException("SetCellPublicFlag target is not a CELL.");
        _cellPublic[cell] = value;
    }

    internal FalloutCellOwnershipSnapshot? CaptureCellOwnership() => _cellOwnership.Count == 0 && _cellPublic.Count == 0 ? null : new(
        _cellOwnership.OrderBy(pair => records.RuntimeFormId(pair.Key)).Select(pair => new FalloutCellOwnershipChange(pair.Key, pair.Value)).ToArray(),
        _cellPublic.Count == 0 ? null :
            _cellPublic.OrderBy(pair => records.RuntimeFormId(pair.Key)).Select(pair => new FalloutCellPublicChange(pair.Key, pair.Value)).ToArray());

    internal void RestoreCellOwnership(FalloutCellOwnershipSnapshot? snapshot)
    {
        if (_cellOwnership.Count != 0 || _cellPublic.Count != 0) throw new InvalidOperationException("CELL ownership restore requires a fresh world.");
        if (snapshot is null) return;
        snapshot.Validate();
        foreach (var change in snapshot.Changes)
        {
            if (records.GetEffective(change.Cell).Signature != "CELL") throw new InvalidDataException("Saved CELL ownership target is not a CELL.");
            RequireCommandOwner(change.Owner);
        }
        foreach (var change in snapshot.Public ?? [])
            if (records.GetEffective(change.Cell).Signature != "CELL") throw new InvalidDataException("Saved CELL public flag target is not a CELL.");
        foreach (var change in snapshot.Changes) _cellOwnership.Add(change.Cell, change.Owner);
        foreach (var change in snapshot.Public ?? []) _cellPublic.Add(change.Cell, change.Public);
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
