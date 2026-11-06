using Godot;

namespace OpenNV.Runtime.Content;

// Watches one finite audio player so its owners never wait forever on the
// device. While the player actually plays (inside the tree, processing, not
// stream-paused) the clip's decoded length is counted; if the device has not
// reported Finished by then, the player is stopped, the gap is reported as
// OPENNV_AUDIO_UNCONFIRMED and Finished is emitted for every existing owner.
// A child of the player inherits its pause behavior, so a paused world or a
// suspended voice also suspends the bound. Looping and unknown streams have no
// finite end and are left to their owners.
internal sealed partial class NativeOwnedAudioCompletion : Node
{
    internal const string UnconfirmedMeta = "opennv_audio_completion_unconfirmed";
    private const string WatcherName = "OwnedAudioCompletion";
    private const string PcmLoopMeta = "opennv_pcm_loop";
    private Node _player = null!;
    private string _lane = "";
    private Func<string>? _identity;
    private FalloutAudioCompletionClock? _clock;
    private AudioStream? _clockStream, _refusedStream;
    internal long Unconfirmed { get; private set; }
    internal object? State => _clock?.State;

    // Idempotent: one watcher per player, whichever owner binds it first.
    internal static void Watch(Node player, string lane, Func<string>? identity = null)
    {
        if (player is not (AudioStreamPlayer or AudioStreamPlayer3D))
            throw new ArgumentException("Finite audio completion requires an audio player.", nameof(player));
        if (player.HasNode(WatcherName)) return;
        var watcher = new NativeOwnedAudioCompletion { Name = WatcherName, _player = player, _lane = lane, _identity = identity };
        if (player is AudioStreamPlayer flat) flat.Finished += watcher.Disarm;
        else ((AudioStreamPlayer3D)player).Finished += watcher.Disarm;
        player.AddChild(watcher);
    }

    internal static NativeOwnedAudioCompletion? Of(Node player) =>
        player.GetNodeOrNull<NativeOwnedAudioCompletion>(WatcherName);

    // A PCM stream declares its loop mode when it is constructed.
    internal static void DeclarePcmLoop(AudioStream stream, bool loops) => stream.SetMeta(PcmLoopMeta, loops);

    private static bool Finite(AudioStream stream) => stream switch
    {
        AudioStreamWav wav => wav.LoopMode == AudioStreamWav.LoopModeEnum.Disabled,
        AudioStreamOggVorbis ogg => !ogg.Loop,
        AudioStreamMP3 mp3 => !mp3.Loop,
        _ => stream.HasMeta(PcmLoopMeta) && !stream.GetMeta(PcmLoopMeta).AsBool()
    };

    private void Disarm() { _clock = null; _clockStream = null; }

    public override void _Process(double delta)
    {
        var (playing, paused, stream, pitch) = _player switch
        {
            AudioStreamPlayer flat => (flat.Playing, flat.StreamPaused, flat.Stream, flat.PitchScale),
            AudioStreamPlayer3D spatial => (spatial.Playing, spatial.StreamPaused, spatial.Stream, spatial.PitchScale),
            _ => (false, false, null, 1f)
        };
        if (!playing || stream is null) { Disarm(); return; }
        if (paused || !Finite(stream) || ReferenceEquals(stream, _refusedStream)) return;
        if (_clock is null || !ReferenceEquals(_clockStream, stream))
        {
            // A new clip: its start is wherever the owner began playback.
            var duration = stream.GetLength() / pitch;
            try { _clock = new(duration, Math.Clamp(PlaybackPosition() / pitch, 0, Math.Max(duration, 0))); }
            catch (InvalidDataException error)
            {
                // A decoding failure is not given a fabricated end: the owner
                // keeps waiting on the device and the gap stays visible.
                _refusedStream = stream;
                GD.PushError($"OPENNV_AUDIO_COMPLETION_UNBOUNDED lane={_lane} player={_player.Name} {error.Message}");
                return;
            }
            _clockStream = stream;
            _player.RemoveMeta(UnconfirmedMeta);
        }
        if (!_clock.Advance(Engine.TimeScale > 0 ? delta / Engine.TimeScale : 0)) return;
        var position = PlaybackPosition();
        var clock = _clock;
        Disarm();
        ++Unconfirmed;
        if (_player is AudioStreamPlayer stopFlat) stopFlat.Stop(); else ((AudioStreamPlayer3D)_player).Stop();
        _player.SetMeta(UnconfirmedMeta, true);
        var media = stream.GetMeta("opennv_owned_media_path", "unbound").AsString();
        GD.PushWarning(clock.Divergence(_lane, $"player={_player.Name} media={media} {_identity?.Invoke()}".TrimEnd(),
            position, AudioServer.GetTimeSinceLastMix()));
        _player.EmitSignal(AudioStreamPlayer.SignalName.Finished);
    }

    private double PlaybackPosition() => _player is AudioStreamPlayer flat
        ? flat.GetPlaybackPosition() : ((AudioStreamPlayer3D)_player).GetPlaybackPosition();
}
