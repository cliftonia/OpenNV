using System.Buffers.Binary;
using Godot;
using OpenNV.Runtime.Content;

namespace OpenNV.Runtime.World.Actors;

internal partial class RuntimeNativeSpeech
{
    private readonly Dictionary<FalloutFormKey, FalloutRadioConversation> _radioConversations = [];
    private Dictionary<FalloutFormKey, List<FalloutFormKey>>? _radioReceivers;

    internal void StartRadioConversation(FalloutFormKey station, FalloutFormKey? topic)
    {
        if (Error is not null) throw new InvalidOperationException(Error);
        try
        {
            if (!_radioConversations.TryGetValue(station, out var conversation))
                _radioConversations.Add(station, conversation = new(_stack,
                    _references ?? throw new InvalidOperationException("Radio has no reference world."),
                    _quests ?? throw new InvalidOperationException("Radio has no quest state."), EvaluateRadioCondition,
                    _questStage, _said, _dialogueRandom));
            if (IsDialogueBusy(station))
                throw new NotSupportedException("Radio interruption requires its retained result/cursor owner.");
            conversation.Start(station, topic);
            if (conversation.Info is { } info)
                StartCore(new(station.ToString(), "", conversation.Topic!.Value.ToString()), _stack.GetEffective(station),
                    conversation.Topic.Value, null, selectedInfo: info, radio: conversation);
            GD.Print($"OPENNV_NATIVE_RADIO_REQUEST station={station} topic={conversation.Topic} active={conversation.Active} continuous=false owner=source-radio-conversation");
        }
        catch (Exception error) when (error is InvalidDataException or NotSupportedException or FileNotFoundException or InvalidOperationException)
        { Fail(error); throw; }
    }

    private float EvaluateRadioCondition(FalloutFormKey station, FalloutCondition condition) => new FalloutDialogueConditions(
        _stack, _quests!, station, SpeakerIdentity(station), _conditionContext, actorValue: _actorValue,
        playerFemale: _playerFemale, actorRace: _actorRace, currentPackage: _currentPackage, vampireQuery: _vampireQuery,
        itemCount: _itemCount, referenceDistance: _referenceDistance, referenceInZone: _referenceInZone,
        sitting: _sitting, deadCount: _deadCount, isDead: _isDead,
        sleeping: reference => (_references ?? throw new NotSupportedException("Radio has no shared sleeping owner.")).GetSleeping(reference),
        world: _references, randomPercent: _dialogueRandom is null ? null : () => _dialogueRandom(100)).Evaluate(condition);

    private void BindRadioSpeaker(Voice voice, FalloutPluginRecord station, Node3D? presentation, FalloutRadioConversation radio)
    {
        EndListenerAnimation(voice);
        voice.Speaker = null; voice.Creature = null; voice.Presentation = presentation;
        voice.TalkingActivator = true;
        voice.DialogueSubject = station.FormKey;
        voice.Identity = radio.VoiceIdentity();
        voice.Radio = radio;
        UpdateRadioListener(voice);
    }

    private IReadOnlyList<FalloutFormKey> PhysicalRadioListeners(FalloutFormKey stationBase)
    {
        if (_radioReceivers is null)
        {
            _radioReceivers = [];
            var receivers = new Dictionary<FalloutFormKey, FalloutFormKey>();
            foreach (var record in _stack.EffectiveRecords("ACTI"))
            {
                var fields = record.ReadSubrecords().Where(field => field.Signature == "RNAM").ToArray();
                if (fields.Length == 0) continue;
                if (fields.Length != 1 || fields[0].Data.Length != 4)
                    throw new InvalidDataException($"Radio receiver {record.FormKey} has invalid station identity.");
                var source = record.Plugin.AdjustOptionalFormId(BinaryPrimitives.ReadUInt32LittleEndian(fields[0].Data.Span));
                if (source is { } form) receivers.Add(record.FormKey, form);
            }
            foreach (var reference in _stack.EffectiveRecords("REFR"))
                if (receivers.TryGetValue(FalloutDialogueTopic.RequiredForm(reference, "NAME"), out var source))
                {
                    if (!_radioReceivers.TryGetValue(source, out var list)) _radioReceivers.Add(source, list = []);
                    list.Add(reference.FormKey);
                }
        }
        return (_radioReceivers.GetValueOrDefault(stationBase) ?? []).Where(reference =>
            _presentation?.Invoke(reference) is { } node && node.IsVisibleInTree() && _references!.IsEnabled(reference)).ToArray();
    }

    private void UpdateRadioListener(Voice voice)
    {
        var station = voice.Radio!.Station!;
        // The transmitter clock continues without a receiver; only the Pip-Boy
        // receiver is audible. Resident ACTI radios have no distance
        // attenuation owner yet, so they must not mix in at full volume.
        var audible = _references!.PipBoyRadio.CurrentStation == station.Reference;
        voice.Player.VolumeDb = audible ? 0 : -80;
        _unbound.Add("radio-physical-receivers-and-attenuation");
    }

    private object RadioState => _radioConversations.Select(pair => new
    {
        station = pair.Key.ToString(),
        topic = pair.Value.Topic?.ToString(),
        info = pair.Value.Info?.Record.FormKey.ToString(),
        pair.Value.Active,
        pair.Value.CompletedLines,
        physicalListeners = pair.Value.Station is { } source ?
            PhysicalRadioListeners(source.Base).Select(form => form.ToString()).ToArray() : [],
        continuousGeneration = "unbound",
        interruption = "unbound",
        activeSave = _channels.TryGetValue(pair.Key, out var voice) && CanCaptureRadioVoice(voice) ? "source-pcm-response" : "unbound"
    }).ToArray();

    // Broadcast scheduler: a broadcasting station the player is tuned to is a
    // continuous transmitter. Whenever it has no active line it begins a new
    // RadioHello request; its links then chain songs, news and DJ lines. A
    // station with no eligible line or a failing line backs off and retries.
    private IDisposable? _pipBoyRadioBinding;
    private readonly Dictionary<FalloutFormKey, double> _radioRetryAt = [];
    private double _radioClock;

    private IDisposable TunePipBoyRadio(FalloutRadioStation station)
    {
        GD.Print($"OPENNV_NATIVE_RADIO_TUNE station={station.Reference} owner=pip-boy-broadcast-scheduler");
        _radioRetryAt.Remove(station.Reference);
        return new RadioTuning(() =>
        {
            StopRadioStation(station.Reference);
            GD.Print($"OPENNV_NATIVE_RADIO_UNTUNE station={station.Reference}");
        });
    }

    private sealed class RadioTuning(Action release) : IDisposable
    {
        private Action? _release = release;
        public void Dispose() { _release?.Invoke(); _release = null; }
    }

    private void AdvanceRadioBroadcasts(double delta)
    {
        _radioClock += delta;
        if (_references?.PipBoyRadio.CurrentStation is not { } station) return;
        if (_radioRetryAt.TryGetValue(station, out var retry) && retry > _radioClock) return;
        if (!_references.IsEnabled(station) || !_references.GetBroadcastState(station) || IsDialogueBusy(station) ||
            _radioConversations.TryGetValue(station, out var current) && current.Active) return;
        try
        {
            if (!_radioConversations.TryGetValue(station, out var conversation))
                _radioConversations.Add(station, conversation = new(_stack, _references,
                    _quests ?? throw new InvalidOperationException("Radio has no quest state."), EvaluateRadioCondition,
                    _questStage, _said, _dialogueRandom));
            conversation.Start(station, null, broadcast: true);
            if (conversation.Info is not { } info) { _radioRetryAt[station] = _radioClock + 5; return; }
            StartCore(new(station.ToString(), "", conversation.Topic!.Value.ToString()), _stack.GetEffective(station),
                conversation.Topic.Value, null, selectedInfo: info, radio: conversation);
            GD.Print($"OPENNV_NATIVE_RADIO_BROADCAST station={station} topic={conversation.Topic} info={info.Record.FormKey}");
        }
        catch (Exception error) when (error is InvalidDataException or NotSupportedException or FileNotFoundException or
            InvalidOperationException or KeyNotFoundException)
        {
            // A broadcast failure silences this station for a while; it never
            // fails the shared speech owner that dialogue depends on.
            _radioRetryAt[station] = _radioClock + 30;
            GD.PushError($"OPENNV_NATIVE_RADIO_BROADCAST_FAILED station={station} {error.GetType().Name}: {error.Message}");
        }
    }

    private void PlayRadioSound(Voice voice, FalloutDialogueInfo info, int responseNumber, FalloutFormKey sound)
    {
        var source = FalloutSoundRecordReader.Read(_stack, sound);
        var content = RuntimeLiveContentSource.Current ?? throw new InvalidOperationException("Owned source is absent.");
        var variants = FalloutAnimationSound.Variants(source, source.HasExactFile ? [] : content.ResourcePathsUnder(source.LogicalPath));
        var path = variants[variants.Count == 1 ? 0 : (int)((_dialogueRandom ?? (bound => (uint)Random.Shared.Next((int)bound)))((uint)variants.Count))];
        voice.Pcm = new(RadioPcm(path), new(FalloutSoundLoopMode.None, 0, 0), null, !CanProcess());
        voice.Player.Stream = voice.Pcm.Stream;
        voice.Player.ProcessMode = ProcessModeEnum.Pausable;
        voice.Player.Play();
        voice.UtteranceActive = true;
        UpdateRadioListener(voice);
        GD.Print($"OPENNV_NATIVE_SPEECH_BEGIN info={info.Record.FormKey} response={responseNumber} sound={sound} " +
            $"path={path} owner=radio-receiver-pcm");
    }

    // Leaving a station ends its current segment and releases its decoded
    // audio; the scheduler starts a fresh segment when it is tuned again.
    private void StopRadioStation(FalloutFormKey station)
    {
        if (_channels.TryGetValue(station, out var voice) && voice.Radio is not null)
        {
            voice.UtteranceActive = false;
            voice.Player.Stop();
            RetireRadioPcm(voice);
            voice.Advance = false;
            voice.Unrecorded = null;
            voice.Radio = null;
            voice.Info = null;
        }
        _radioConversations.Remove(station);
    }
}
