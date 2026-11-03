using System;
using System.Collections.Generic;
using System.Diagnostics;
using MegaCrit.Sts2.Core.Platform.Steam;
using Steamworks;

namespace QuickMatch.Voice;

/// <summary>
/// Desktop recording through Steam's voice API: it uses the microphone chosen in Steam's own voice
/// settings and Steam's noise handling, on Windows, macOS, Linux and Steam Deck alike. Steam's
/// compressed voice is decompressed here to 16 kHz and then sent as QuickMatch's own Opus, which
/// every platform can play. Steam may stop delivering during silence (its own transmit threshold),
/// so missing time is filled with silence and the voice clock stays real time.
/// </summary>
internal sealed class SteamVoiceDevice : IVoiceDevice
{
    /// <summary>Steam sends no voice in under this long while recording: the gap is silence, not lateness.</summary>
    private const int SilenceAfterSamples = 2 * VoiceFormat.FrameSamples;

    private readonly byte[] _compressed = new byte[8 * 1024];
    private readonly byte[] _pcm = new byte[64 * 1024];
    private readonly Queue<short> _samples = new();
    private readonly Stopwatch _clock = new();
    private long _delivered;
    private bool _recording;

    private SteamVoiceDevice()
    {
    }

    /// <summary>The device where Steamworks is running (the desktop game), else null.</summary>
    public static SteamVoiceDevice? TryCreate() => SteamInitializer.Initialized ? new SteamVoiceDevice() : null;

    public VoiceCaptureState CaptureState => SteamInitializer.Initialized ? VoiceCaptureState.Ready : VoiceCaptureState.Unsupported;

    public IVoiceOutput? Output => null;

    public void RequestPermission()
    {
    }

    public bool StartCapture()
    {
        if (!SteamInitializer.Initialized)
        {
            return false;
        }
        SteamUser.StartVoiceRecording();
        _recording = true;
        _samples.Clear();
        _delivered = 0;
        _clock.Restart();
        return true;
    }

    public void StopCapture()
    {
        if (_recording)
        {
            SteamUser.StopVoiceRecording();
            _recording = false;
        }
        _clock.Reset();
        _samples.Clear();
    }

    public int ReadCapture(Span<short> destination)
    {
        if (!_recording)
        {
            return 0;
        }
        Pump();
        long due = _clock.ElapsedTicks * VoiceFormat.SampleRate / Stopwatch.Frequency - _delivered;
        if (_samples.Count == 0 && due > SilenceAfterSamples)
        {
            // Keep one frame of slack so Steam's next delivery is not treated as late.
            for (long i = 0; i < due - VoiceFormat.FrameSamples; i++)
            {
                _samples.Enqueue(0);
            }
        }
        int count = 0;
        while (count < destination.Length && _samples.TryDequeue(out short sample))
        {
            destination[count++] = sample;
        }
        _delivered += count;
        return count;
    }

    private void Pump()
    {
        while (SteamUser.GetAvailableVoice(out uint available) == EVoiceResult.k_EVoiceResultOK && available > 0)
        {
            if (SteamUser.GetVoice(true, _compressed, (uint)_compressed.Length, out uint written) != EVoiceResult.k_EVoiceResultOK || written == 0)
            {
                return;
            }
            if (SteamUser.DecompressVoice(_compressed, written, _pcm, (uint)_pcm.Length, out uint bytes, VoiceFormat.SampleRate) != EVoiceResult.k_EVoiceResultOK)
            {
                return;
            }
            for (int i = 0; i + 1 < bytes; i += 2)
            {
                _samples.Enqueue((short)(_pcm[i] | (_pcm[i + 1] << 8)));
            }
            // Never hold more than half a second: if the game stalls, old speech is dropped.
            while (_samples.Count > VoiceFormat.SampleRate / 2)
            {
                _samples.Dequeue();
            }
        }
    }
}
