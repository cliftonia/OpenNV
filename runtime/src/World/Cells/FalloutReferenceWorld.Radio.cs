using OpenNV.Runtime.Content;

namespace OpenNV.Runtime.World.Cells;

internal sealed partial class FalloutReferenceWorld
{
    internal bool GetBroadcastState(FalloutFormKey reference)
    {
        var station = FalloutRadioStation.Read(records, records.GetEffective(reference));
        return Get(reference).BroadcastState ?? station.Continuous;
    }

    internal void SetBroadcastState(FalloutFormKey reference, double value)
    {
        var broadcast = value switch
        {
            0 => false,
            1 => true,
            _ => throw new InvalidDataException("Radio broadcast state must be zero or one."),
        };
        _ = FalloutRadioStation.Read(records, records.GetEffective(reference));
        Get(reference).BroadcastState = broadcast;
    }

    // A placed radio receiver (ACTI with an RNAM station) has no Open/Close
    // sequence; its default action toggles the persistent logical activator
    // bit, which is the receiver's power state. Receivers start off; the
    // authored "StartOn" scripts activate themselves once on first load.
    internal bool IsRadioReceiverOn(FalloutFormKey reference) => Get(reference).DoorOpen;

    // SetNPCRadio turns a placed actor into a moving receiver of one station
    // reference. The station reference must be a winning radio transmitter.
    internal void SetNpcRadio(FalloutFormKey actor, bool on, FalloutFormKey station)
    {
        if (records.GetEffective(actor).Signature is not ("ACHR" or "ACRE"))
            throw new InvalidDataException("SetNPCRadio requires a placed actor.");
        _ = FalloutRadioStation.Read(records, records.GetEffective(station));
        var current = Overrides(actor);
        _actorOverrides[actor] = current with { NpcRadio = on ? FormOverride(station, "REFR", 1) : null };
    }

    internal IEnumerable<(FalloutFormKey Actor, FalloutFormKey Station)> NpcRadios() => _actorOverrides.Values
        .Where(value => value.NpcRadio is not null).Select(value => (value.Target, value.NpcRadio!.Form));
}
