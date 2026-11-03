using System;
using Godot;

namespace QuickMatch.Voice;

/// <summary>
/// Plays mixed voice through the game engine's own audio output (desktop and Android), with an
/// <see cref="AudioStreamGenerator"/> at the voice's 16 kHz; the engine resamples it. The game's
/// music and effects go through FMOD, so voice is mixed beside them by the system. The player node
/// is created on first use and removed by <see cref="Stop"/>.
/// </summary>
internal sealed class GodotVoiceOutput : IVoiceOutput
{
    private const string NodeName = "QuickMatchVoice";
    /// <summary>Room for 250 ms of voice; QuickMatch keeps far less than that queued.</summary>
    private const float BufferSeconds = 0.25f;

    private AudioStreamPlayer? _player;
    private AudioStreamGeneratorPlayback? _playback;
    private int _capacity;
    private Vector2[] _frames = Array.Empty<Vector2>();

    public int QueuedSamples => _playback == null ? 0 : Math.Max(0, _capacity - _playback.GetFramesAvailable());

    public void Play(ReadOnlySpan<short> samples)
    {
        if (_playback == null && !Start())
        {
            return;
        }
        int count = Math.Min(samples.Length, _playback!.GetFramesAvailable());
        if (_frames.Length != count)
        {
            _frames = new Vector2[count];
        }
        for (int i = 0; i < count; i++)
        {
            float value = samples[i] / 32768f;
            _frames[i] = new Vector2(value, value);
        }
        _playback.PushBuffer(_frames);
    }

    public void Stop()
    {
        if (_player != null && GodotObject.IsInstanceValid(_player))
        {
            _player.Stop();
            _player.QueueFree();
        }
        _player = null;
        _playback = null;
    }

    private bool Start()
    {
        if (Engine.GetMainLoop() is not SceneTree tree || tree.Root == null)
        {
            return false;
        }
        var stream = new AudioStreamGenerator { MixRate = VoiceFormat.SampleRate, BufferLength = BufferSeconds };
        _player = new AudioStreamPlayer { Name = NodeName, Stream = stream };
        tree.Root.AddChild(_player);
        _player.Play();
        _playback = _player.GetStreamPlayback() as AudioStreamGeneratorPlayback;
        if (_playback == null)
        {
            Stop();
            return false;
        }
        _capacity = _playback.GetFramesAvailable();
        return true;
    }
}
