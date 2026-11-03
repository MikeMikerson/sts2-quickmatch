using System;

namespace QuickMatch.Voice;

/// <summary>
/// Decides, frame by frame, whether the local player is speaking, so silence is never sent. It
/// follows the room's background noise and opens when a frame is clearly louder than that; once
/// open it stays open for <see cref="HangoverFrames"/> after the last loud frame, so the ends of
/// words are not cut. <see cref="Sensitivity"/> (0 to 1, from the settings) sets how much louder
/// than the background speech must be.
/// </summary>
internal sealed class VoiceGate
{
    /// <summary>300 ms of quieter frames before the gate closes.</summary>
    public const int HangoverFrames = 15;
    /// <summary>Frames quieter than this are never speech, however quiet the room is.</summary>
    public const double MinimumDb = -55;
    /// <summary>Frames this loud are always speech, however noisy the room is.</summary>
    public const double AlwaysOpenDb = -18;
    /// <summary>The level used for digital silence (log of zero).</summary>
    public const double SilenceDb = -96;

    private double _floorDb = -60;
    private int _hangover;

    /// <summary>0: only clearly raised voices open the gate. 1: quiet speech does too.</summary>
    public double Sensitivity { get; set; } = 0.5;

    public bool IsOpen { get; private set; }

    /// <summary>The level of the last frame, in dB below full scale.</summary>
    public double LevelDb { get; private set; } = SilenceDb;

    /// <summary>The background noise level the gate measures against.</summary>
    public double NoiseFloorDb => _floorDb;

    /// <summary>The level of the last frame from 0 to 1, for the mic meter.</summary>
    public double Meter => Math.Clamp((LevelDb - MinimumDb) / -MinimumDb, 0, 1);

    /// <summary>Takes one 20 ms frame and returns whether it should be sent.</summary>
    public bool Process(ReadOnlySpan<short> frame)
    {
        LevelDb = Level(frame);
        double threshold = Math.Min(Math.Max(_floorDb + MarginDb(), MinimumDb), AlwaysOpenDb);
        bool loud = LevelDb >= threshold;
        if (loud)
        {
            _hangover = HangoverFrames;
        }
        IsOpen = loud || _hangover > 0;
        if (!loud && _hangover > 0)
        {
            _hangover--;
        }

        // The background drops quickly when the room goes quiet and rises slowly (about 2.5 dB a
        // second), so a long sentence is not mistaken for background noise.
        if (LevelDb < _floorDb)
        {
            _floorDb += (LevelDb - _floorDb) * 0.3;
        }
        else if (!loud)
        {
            _floorDb += Math.Min(0.05, LevelDb - _floorDb);
        }
        _floorDb = Math.Max(_floorDb, SilenceDb);
        return IsOpen;
    }

    public void Reset()
    {
        _hangover = 0;
        IsOpen = false;
    }

    /// <summary>The RMS level of a frame in dB below full scale.</summary>
    public static double Level(ReadOnlySpan<short> frame)
    {
        if (frame.IsEmpty)
        {
            return SilenceDb;
        }
        double sum = 0;
        foreach (short sample in frame)
        {
            sum += (double)sample * sample;
        }
        double rms = Math.Sqrt(sum / frame.Length) / 32768.0;
        return rms <= 0 ? SilenceDb : Math.Max(20 * Math.Log10(rms), SilenceDb);
    }

    /// <summary>How far above the background speech must be: 18 dB at sensitivity 0, 6 dB at 1.</summary>
    private double MarginDb() => 18 - 12 * Math.Clamp(Sensitivity, 0, 1);
}
