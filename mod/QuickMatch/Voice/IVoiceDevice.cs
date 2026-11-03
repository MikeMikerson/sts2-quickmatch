using System;

namespace QuickMatch.Voice;

public enum VoiceCaptureState
{
    /// <summary>This platform cannot record (the Android launcher asks for no microphone permission).</summary>
    Unsupported,
    /// <summary>Recording is possible once the player allows it; nothing has been asked yet.</summary>
    NeedsPermission,
    /// <summary>The system's permission prompt is showing.</summary>
    Asking,
    /// <summary>The player refused; only the system's settings can change that.</summary>
    Denied,
    Ready,
}

/// <summary>
/// The microphone, and optionally the speaker, for voice chat, supplied by the platform: the mod
/// itself never touches audio hardware, so it also runs where code mods may not (the iPhone port
/// refuses native calls in mods). The iPhone app, and on desktop QuickMatch's Steam voice device,
/// set <see cref="VoiceDevice.Current"/>. All calls come from the game's thread.
/// </summary>
public interface IVoiceDevice
{
    VoiceCaptureState CaptureState { get; }

    /// <summary>Shows the system's microphone prompt if it has not been answered; the answer appears in <see cref="CaptureState"/>.</summary>
    void RequestPermission();

    /// <summary>Starts recording 16 kHz mono. False if recording cannot start now.</summary>
    bool StartCapture();

    void StopCapture();

    /// <summary>Copies recorded samples, oldest first, into <paramref name="destination"/>; returns how many.</summary>
    int ReadCapture(Span<short> destination);

    /// <summary>
    /// The device's own voice output, or null to play voice through the game engine. The iPhone plays
    /// voice itself so its echo cancellation knows what is coming out of the speaker.
    /// </summary>
    IVoiceOutput? Output { get; }
}

/// <summary>Where mixed voice (16 kHz mono) is played. Called from the game's thread.</summary>
public interface IVoiceOutput
{
    /// <summary>Samples handed to <see cref="Play"/> that have not been heard yet.</summary>
    int QueuedSamples { get; }

    void Play(ReadOnlySpan<short> samples);

    /// <summary>Drops anything queued and releases the output until the next <see cref="Play"/>.</summary>
    void Stop();
}

/// <summary>The platform's voice device, or null for none: the player can then only listen, through the game engine.</summary>
public static class VoiceDevice
{
    public static IVoiceDevice? Current { get; set; }
}
