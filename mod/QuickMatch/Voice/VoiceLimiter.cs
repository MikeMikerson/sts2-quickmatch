using System;

namespace QuickMatch.Voice;

/// <summary>
/// Protects the listener's ears from one speaker. Every sample is kept under <see cref="Ceiling"/>
/// (a fast peak limiter, so a scream or a blown-out mic is capped at once), and a speaker who stays
/// loud for most of a few seconds is marked <see cref="TooLoud"/> and turned down by
/// <see cref="TooLoudGain"/> until the listener changes that speaker's volume. Works on 20 ms frames
/// after the listener's own volume for that speaker is applied.
/// </summary>
internal sealed class VoiceLimiter
{
    /// <summary>The highest level a sample may reach, as a fraction of full scale (about -6 dB).</summary>
    public const float Ceiling = 0.5f;
    /// <summary>A frame this loud (RMS, dB below full scale) counts towards <see cref="TooLoud"/>.</summary>
    public const double LoudDb = -12;
    /// <summary>The window loud frames are counted in (5 s) and how many of them make a speaker too loud (3 s).</summary>
    public const int WindowFrames = 250;
    public const int LoudFramesForTooLoud = 150;
    /// <summary>The extra gain on a speaker marked too loud (-12 dB).</summary>
    public const float TooLoudGain = 0.25f;

    /// <summary>How fast the limiter lets go after a peak: about 100 ms back to full gain.</summary>
    private const float Release = 1f / (VoiceFormat.SampleRate * 0.1f);

    private readonly bool[] _window = new bool[WindowFrames];
    private int _windowAt;
    private int _loudFrames;
    private float _reduction = 1f;

    public bool TooLoud { get; private set; }

    /// <summary>Applies <paramref name="volume"/> and the limits to a frame, in place.</summary>
    public void Process(Span<short> frame, float volume)
    {
        float gain = volume * (TooLoud ? TooLoudGain : 1f);
        double sentSum = 0;
        for (int i = 0; i < frame.Length; i++)
        {
            float sent = frame[i] / 32768f;
            sentSum += (double)sent * sent;
            float sample = sent * gain;
            float peak = Math.Abs(sample);
            _reduction = Math.Min(1f, _reduction + Release);
            if (peak * _reduction > Ceiling)
            {
                _reduction = Ceiling / peak;
            }
            sample *= _reduction;
            frame[i] = (short)Math.Clamp(sample * 32768f, short.MinValue, short.MaxValue);
        }
        CountLoudness(frame.Length == 0 ? VoiceGate.SilenceDb : 10 * Math.Log10(Math.Max(sentSum / frame.Length, 1e-10)));
    }

    /// <summary>The listener chose a volume for this speaker: undo the automatic turning down.</summary>
    public void ResetLoudness()
    {
        Array.Clear(_window);
        _loudFrames = 0;
        TooLoud = false;
    }

    /// <summary>
    /// Counts the frame's loudness as sent (before the listener's volume and the automatic turning
    /// down), so turning a speaker down does not by itself clear the mark.
    /// </summary>
    private void CountLoudness(double sentDb)
    {
        bool loud = sentDb >= LoudDb;
        if (_window[_windowAt])
        {
            _loudFrames--;
        }
        _window[_windowAt] = loud;
        if (loud)
        {
            _loudFrames++;
        }
        _windowAt = (_windowAt + 1) % WindowFrames;
        if (_loudFrames >= LoudFramesForTooLoud)
        {
            TooLoud = true;
        }
    }
}
