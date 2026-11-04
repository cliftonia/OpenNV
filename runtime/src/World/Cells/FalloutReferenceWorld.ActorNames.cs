using OpenNV.Runtime.Content;

namespace OpenNV.Runtime.World.Cells;

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
