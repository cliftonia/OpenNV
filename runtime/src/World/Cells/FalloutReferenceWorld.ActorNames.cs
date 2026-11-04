using OpenNV.Runtime.Content;

namespace OpenNV.Runtime.World.Cells;

internal sealed record FalloutActorNameChange(FalloutFormKey ActorBase, FalloutFormKey NameForm);

// SetActorFullName replacements are campaign state. Saved as one optional
// field; each entry keeps the source name form, never a copied string.
internal sealed record FalloutActorNamesSnapshot(IReadOnlyList<FalloutActorNameChange> Changes)
{
    internal void Validate()
    {
        static bool Valid(FalloutFormKey key) => key.ObjectId != 0 && !string.IsNullOrWhiteSpace(key.OwnerPlugin);
        if (Changes is null || Changes.Any(value => value is null || !Valid(value.ActorBase) || !Valid(value.NameForm)) ||
            Changes.Select(value => value.ActorBase).Distinct().Count() != Changes.Count)
            throw new InvalidDataException("Saved actor names are invalid or duplicated.");
    }
}

internal sealed partial class FalloutReferenceWorld
{
    // SetActorFullName renames the actor base, so every reference sharing that
    // base resolves the replacement. The source name form is retained, not a
    // copied string, so presentation always reads the winning record text.
    private readonly Dictionary<FalloutFormKey, FalloutFormKey> _actorNameForms = [];

    internal void SetActorFullName(FalloutFormKey reference, FalloutFormKey nameForm)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var baseKey = ActorBase(reference);
        if (records.GetEffective(baseKey).Signature is not ("NPC_" or "CREA"))
            throw new InvalidDataException("SetActorFullName caller is not an actor reference.");
        _ = ActorNameText(nameForm);
        _actorNameForms[baseKey] = nameForm;
    }

    private string? ActorNameOverride(FalloutFormKey reference) =>
        _actorNameForms.TryGetValue(ActorBase(reference), out var form) ? ActorNameText(form) : null;

    internal FalloutActorNamesSnapshot? CaptureActorNames() => _actorNameForms.Count == 0 ? null : new(
        _actorNameForms.OrderBy(pair => records.RuntimeFormId(pair.Key))
            .Select(pair => new FalloutActorNameChange(pair.Key, pair.Value)).ToArray());

    internal void RestoreActorNames(FalloutActorNamesSnapshot? snapshot)
    {
        if (_actorNameForms.Count != 0) throw new InvalidOperationException("Actor name restore requires a fresh world.");
        if (snapshot is null) return;
        snapshot.Validate();
        foreach (var change in snapshot.Changes)
        {
            if (records.GetEffective(change.ActorBase).Signature is not ("NPC_" or "CREA"))
                throw new InvalidDataException("Saved actor name target is not an actor base.");
            _ = ActorNameText(change.NameForm);
        }
        foreach (var change in snapshot.Changes) _actorNameForms.Add(change.ActorBase, change.NameForm);
    }

    // A MESG supplies its message body; any other named form supplies FULL.
    private string ActorNameText(FalloutFormKey form)
    {
        var record = records.GetEffective(form);
        if (record.Signature != "MESG") return FalloutRecordDisplayName.Read(record);
        var text = record.ReadSubrecords().Where(field => field.Signature == "DESC").ToArray();
        if (text.Length != 1) throw new InvalidDataException($"SetActorFullName MESG {form} has no single DESC text.");
        return FalloutDialogueTopic.Text(text[0].Data.Span);
    }
}
