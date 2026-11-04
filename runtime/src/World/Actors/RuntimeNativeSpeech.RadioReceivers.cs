using System.Buffers.Binary;
using Godot;
using OpenNV.Runtime.Content;

namespace OpenNV.Runtime.World.Actors;

// Physical radio receivers: placed ACTI radios that are switched on and actors
// given a station with SetNPCRadio. Each one mirrors its station's single
// transmitter clock as a positional source; the Pip-Boy receiver stays
// non-positional. Mirrors are presentation only and are rebuilt after a load.
internal partial class RuntimeNativeSpeech
{
    private sealed record RadioReceiverSource(FalloutFormKey StationBase, FalloutFormKey? Template);
    private sealed record RadioReceiver(FalloutFormKey Reference, Node3D Node, FalloutFormKey? Template);

    private sealed class RadioReceiverMirror(AudioStreamPlayer3D player, NativeOwnedPcmStream pcm, NativeOwnedPcmStream source)
    {
        internal readonly AudioStreamPlayer3D Player = player;
        internal readonly NativeOwnedPcmStream Pcm = pcm;
        internal readonly NativeOwnedPcmStream Source = source;
    }

    private Dictionary<FalloutFormKey, RadioReceiverSource>? _radioReceiverSources;
    private Dictionary<FalloutFormKey, FalloutFormKey[]>? _radioStationsByBase;
    private readonly Dictionary<FalloutFormKey, FalloutSoundRecord> _radioTemplates = [];
    private ulong _radioReceiverFrame = ulong.MaxValue;
    private Dictionary<FalloutFormKey, RadioReceiver[]> _radioStationReceivers = [];
    private (float Minimum, float Maximum, float DialogueMute)? _radioDialogueSettings;

    private Dictionary<FalloutFormKey, RadioReceiverSource> RadioReceiverSources()
    {
        if (_radioReceiverSources is not null) return _radioReceiverSources;
        var bases = new Dictionary<FalloutFormKey, RadioReceiverSource>();
        foreach (var record in _stack.EffectiveRecords("ACTI"))
        {
            FalloutFormKey? station = null, template = null;
            foreach (var field in record.ReadSubrecords().Where(field => field.Signature is "RNAM" or "INAM"))
            {
                if (field.Data.Length != 4)
                    throw new InvalidDataException($"Radio receiver {record.FormKey} {field.Signature} has invalid identity.");
                var form = record.Plugin.AdjustOptionalFormId(BinaryPrimitives.ReadUInt32LittleEndian(field.Data.Span));
                if (field.Signature == "RNAM") station = form; else template = form;
            }
            if (station is { } source) bases.Add(record.FormKey, new(source, template));
        }
        var receivers = new Dictionary<FalloutFormKey, RadioReceiverSource>();
        foreach (var reference in _stack.EffectiveRecords("REFR"))
            if (bases.TryGetValue(FalloutDialogueTopic.RequiredForm(reference, "NAME"), out var source))
                receivers.Add(reference.FormKey, source);
        return _radioReceiverSources = receivers;
    }

    private IReadOnlyList<FalloutFormKey> RadioStationsOfBase(FalloutFormKey stationBase)
    {
        _radioStationsByBase ??= _stack.ReadRadioStationIndex().Stations.Values.GroupBy(station => station.Base)
            .ToDictionary(group => group.Key, group => group.Select(station => station.Reference).ToArray());
        return _radioStationsByBase.GetValueOrDefault(stationBase) ?? [];
    }

    private Node3D? ResidentRadioNode(FalloutFormKey reference) =>
        _presentation?.Invoke(reference) is { } node && IsInstanceValid(node) && node.IsInsideTree() && node.IsVisibleInTree() &&
        _references!.IsEnabled(reference) ? node : null;

    // Every receiver currently able to play, keyed to the station reference
    // it receives. One scan per frame is shared by the scheduler and mixers.
    private RadioReceiver[] ActiveRadioReceivers(FalloutFormKey station)
    {
        var frame = Engine.GetProcessFrames();
        if (_radioReceiverFrame != frame)
        {
            var found = new List<(FalloutFormKey Station, RadioReceiver Receiver)>();
            foreach (var (reference, source) in RadioReceiverSources())
            {
                if (ResidentRadioNode(reference) is not { } node || !_references!.IsRadioReceiverOn(reference)) continue;
                foreach (var target in RadioStationsOfBase(source.StationBase))
                    found.Add((target, new(reference, node, source.Template)));
            }
            foreach (var (actor, target) in _references!.NpcRadios())
                if (ResidentRadioNode(actor) is { } node && _isDead?.Invoke(actor) != true)
                    found.Add((target, new(actor, node, null)));
            _radioStationReceivers = found.GroupBy(item => item.Station)
                .ToDictionary(group => group.Key, group => group.Select(item => item.Receiver).ToArray());
            _radioReceiverFrame = frame;
        }
        return _radioStationReceivers.GetValueOrDefault(station) ?? [];
    }
    private IReadOnlyCollection<FalloutFormKey> ListenedRadioStations()
    {
        _ = ActiveRadioReceivers(default);
        var stations = new HashSet<FalloutFormKey>(_radioStationReceivers.Keys);
        if (_references!.PipBoyRadio.CurrentStation is { } pipBoy) stations.Add(pipBoy);
        return stations;
    }

    private void UpdateRadioReceivers(Voice voice, FalloutRadioStation station)
    {
        var receivers = ActiveRadioReceivers(station.Reference).ToDictionary(receiver => receiver.Reference);
        foreach (var (reference, mirror) in voice.Receivers.ToArray())
            if (!receivers.ContainsKey(reference) || voice.Pcm != mirror.Source || !IsInstanceValid(mirror.Player) || !mirror.Player.IsInsideTree())
                RetireRadioReceiver(voice, reference);
        if (voice.Pcm is not { } pcm || !voice.UtteranceActive || !voice.Player.Playing || !voice.Player.HasStreamPlayback()) return;
        var listener = GetViewport()?.GetCamera3D();
        foreach (var receiver in receivers.Values)
        {
            if (!voice.Receivers.TryGetValue(receiver.Reference, out var mirror))
                voice.Receivers.Add(receiver.Reference, mirror = StartRadioReceiver(pcm, receiver, station));
            mirror.Player.VolumeDb = listener is null ? float.NegativeInfinity :
                RadioReceiverGainDb(receiver, mirror.Player.GlobalPosition.DistanceTo(listener.GlobalPosition) / _unitsToMetres) + RadioDialogueDuckDb();
        }
    }

    private RadioReceiverMirror StartRadioReceiver(NativeOwnedPcmStream source, RadioReceiver receiver, FalloutRadioStation station)
    {
        if (!float.IsFinite(_unitsToMetres) || _unitsToMetres <= 0)
            throw new InvalidDataException("Radio receivers have no owned game-unit scale.");
        // The receiver joins the transmitter at its current sample; both read
        // the same decoded samples on the same mixer clock.
        var pcm = new NativeOwnedPcmStream(source.Data, new(FalloutSoundLoopMode.None, 0, 0), source.Capture(), !CanProcess());
        var player = new AudioStreamPlayer3D
        {
            Name = "OwnedRadioReceiver",
            Stream = pcm.Stream,
            ProcessMode = ProcessModeEnum.Pausable,
            AttenuationModel = AudioStreamPlayer3D.AttenuationModelEnum.Disabled,
            MaxDistance = RadioReceiverMaximumDistance(receiver) * _unitsToMetres,
            AreaMask = 0,
            VolumeDb = float.NegativeInfinity,
        };
        try { receiver.Node.AddChild(player); player.Play(); }
        catch { player.Free(); pcm.Dispose(); throw; }
        GD.Print($"OPENNV_NATIVE_RADIO_RECEIVER_START station={station.Reference} receiver={receiver.Reference} " +
            $"template={receiver.Template?.ToString() ?? "default-dialogue-distance"} owner=radio-receiver-3d");
        return new(player, pcm, source);
    }

    private static void RetireRadioReceiver(Voice voice, FalloutFormKey reference)
    {
        if (!voice.Receivers.Remove(reference, out var mirror)) return;
        if (IsInstanceValid(mirror.Player)) { mirror.Player.Stop(); mirror.Player.Stream = null; mirror.Player.QueueFree(); }
        mirror.Pcm.Dispose();
    }

    private static void RetireRadioReceivers(Voice voice)
    {
        foreach (var reference in voice.Receivers.Keys.ToArray()) RetireRadioReceiver(voice, reference);
    }

    // A radio template (ACTI INAM) supplies the authored SOUN attenuation.
    // Without one the receiver uses the dialogue voice distances.
    private float RadioReceiverGainDb(RadioReceiver receiver, float distance)
    {
        if (receiver.Template is { } template) return RadioTemplate(template).AttenuationDbAtDistanceGameUnits(distance);
        var (minimum, maximum, _) = RadioDialogueSettings();
        _unbound.Add("radio-receiver-default-template-curve");
        if (distance <= minimum) return 0;
        if (distance >= maximum) return float.NegativeInfinity;
        return 20 * MathF.Log10(1 - (distance - minimum) / (maximum - minimum));
    }

    private float RadioReceiverMaximumDistance(RadioReceiver receiver) => receiver.Template is { } template
        ? RadioTemplate(template).MaximumDistanceGameUnits : RadioDialogueSettings().Maximum;

    private FalloutSoundRecord RadioTemplate(FalloutFormKey template)
    {
        if (_radioTemplates.TryGetValue(template, out var source)) return source;
        source = FalloutSoundRecordReader.Read(_stack, template);
        source.ValidateAttenuationCurve();
        _radioTemplates.Add(template, source);
        return source;
    }

    // fRadioDialogMute scales every radio while the player is in conversation.
    private float RadioDialogueDuckDb() => _conversationVoice is { Info: not null }
        ? 20 * MathF.Log10(RadioDialogueSettings().DialogueMute) : 0;

    private (float Minimum, float Maximum, float DialogueMute) RadioDialogueSettings()
    {
        if (_radioDialogueSettings is { } settings) return settings;
        var ini = _stack.IniSettings;
        settings = (ini.Float("fDialogMinDistance:Audio"), ini.Float("fDialogMaxDistance:Audio"), ini.Float("fRadioDialogMute:Audio"));
        if (!float.IsFinite(settings.Minimum) || settings.Minimum < 0 || !float.IsFinite(settings.Maximum) || settings.Maximum <= settings.Minimum ||
            !float.IsFinite(settings.DialogueMute) || settings.DialogueMute is <= 0 or > 1)
            throw new InvalidDataException("Owned dialogue/radio audio distances are invalid.");
        return (_radioDialogueSettings = settings).Value;
    }
}
