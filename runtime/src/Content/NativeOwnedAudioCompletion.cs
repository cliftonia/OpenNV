using Godot;

namespace OpenNV.Runtime.Content;

// Watches one finite audio player so its owners never wait forever on the
// device. FalloutAudioCompletionWatch decides from the player's cursor; when
// the cursor has been frozen for the rest of the clip the player is stopped,
// marked, reported as OPENNV_AUDIO_UNCONFIRMED and Finished is emitted for
// every existing owner, who can read the mark to record a distinct end. A
// child of the player inherits its pause behavior. The watcher only processes
// while a clip may be pending: Watch re-enables it before each playback.
internal sealed partial class NativeOwnedAudioCompletion : Node
{
    private const string UnconfirmedMeta = "opennv_audio_completion_unconfirmed";
    private const string WatcherName = "OwnedAudioCompletion";
    private const string PcmLoopMeta = "opennv_pcm_loop";
    private readonly FalloutAudioCompletionWatch _watch = new();
    private Node _player = null!;
    private string _lane = "";
    private Func<string>? _identity;
    internal object State => _watch.State;

    // Call before every Play; one watcher per player, whichever owner binds it first.
    internal static void Watch(Node player, string lane, Func<string>? identity = null)
    {
        if (player is not (AudioStreamPlayer or AudioStreamPlayer3D))
            throw new ArgumentException("Finite audio completion requires an audio player.", nameof(player));
        if (Of(player) is { } existing) { existing._watch.Reset(); existing.SetProcess(true); return; }
        var watcher = new NativeOwnedAudioCompletion { Name = WatcherName, _player = player, _lane = lane, _identity = identity };
        if (player is AudioStreamPlayer flat) flat.Finished += watcher.Settle;
        else ((AudioStreamPlayer3D)player).Finished += watcher.Settle;
        player.AddChild(watcher);
    }

    internal static NativeOwnedAudioCompletion? Of(Node player) =>
        player.GetNodeOrNull<NativeOwnedAudioCompletion>(WatcherName);

    // True when the last clip was ended by the bound rather than the device.
    internal static bool Unconfirmed(Node player) => player.HasMeta(UnconfirmedMeta);

    // A PCM stream declares its loop mode when it is constructed.
    internal static void DeclarePcmLoop(AudioStream stream, bool loops) => stream.SetMeta(PcmLoopMeta, loops);

    private static bool Finite(AudioStream stream) => stream switch
    {
        AudioStreamWav wav => wav.LoopMode == AudioStreamWav.LoopModeEnum.Disabled,
        AudioStreamOggVorbis ogg => !ogg.Loop,
        AudioStreamMP3 mp3 => !mp3.Loop,
        _ => stream.HasMeta(PcmLoopMeta) && !stream.GetMeta(PcmLoopMeta).AsBool()
    };

    private void Settle() { _watch.Reset(); SetProcess(false); }

    public override void _Process(double delta)
    {
        var (playing, paused, stream, pitch) = _player switch
        {
            AudioStreamPlayer flat => (flat.Playing, flat.StreamPaused, flat.Stream, flat.PitchScale),
            AudioStreamPlayer3D spatial => (spatial.Playing, spatial.StreamPaused, spatial.Stream, spatial.PitchScale),
            _ => (false, false, null, 1f)
        };
        var observation = new FalloutAudioObservation(playing && stream is not null, paused, stream is not null && Finite(stream),
            stream?.GetInstanceId() ?? 0, stream?.GetLength() ?? 0, pitch, playing ? PlaybackPosition() : 0);
        switch (_watch.Observe(observation, Engine.TimeScale > 0 ? delta / Engine.TimeScale : 0))
        {
            case FalloutAudioCompletionStep.Armed:
                _player.RemoveMeta(UnconfirmedMeta);
                break;
            case FalloutAudioCompletionStep.Ended:
            case FalloutAudioCompletionStep.Unbounded:
                SetProcess(false);
                break;
            case FalloutAudioCompletionStep.Refused:
                // A decoding failure is not given a fabricated end: the owner
                // keeps waiting on the device and the gap stays visible.
                GD.PushError($"OPENNV_AUDIO_COMPLETION_UNBOUNDED lane={_lane} player={_player.Name} " +
                    $"length={observation.LengthSeconds} pitch={observation.Pitch}");
                SetProcess(false);
                break;
            case FalloutAudioCompletionStep.Expired:
                Expire(stream!);
                break;
            case FalloutAudioCompletionStep.Idle:
            case FalloutAudioCompletionStep.Waiting:
                break;
        }
    }

    private void Expire(AudioStream stream)
    {
        if (_player is AudioStreamPlayer flat) flat.Stop(); else ((AudioStreamPlayer3D)_player).Stop();
        _player.SetMeta(UnconfirmedMeta, true);
        var media = stream.GetMeta("opennv_owned_media_path", "unbound").AsString();
        GD.PushWarning(_watch.Divergence(_lane, $"player={_player.Name} media={media} {_identity?.Invoke()}".TrimEnd(),
            AudioServer.GetTimeSinceLastMix()));
        _player.EmitSignal(AudioStreamPlayer.SignalName.Finished);
    }

    private double PlaybackPosition() => _player is AudioStreamPlayer flat
        ? flat.GetPlaybackPosition() : ((AudioStreamPlayer3D)_player).GetPlaybackPosition();
}
