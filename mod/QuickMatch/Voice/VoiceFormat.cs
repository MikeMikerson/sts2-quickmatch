namespace QuickMatch.Voice;

/// <summary>
/// The one audio format every QuickMatch player records, sends and plays: 16 kHz mono, cut into
/// 20 ms Opus frames, three frames to a packet. A packet every 60 ms keeps the relay's daily
/// allowance safe (about 17 messages a second per speaker, and none during silence); the game is
/// turn based, so the extra 40 ms over single-frame packets does not matter.
/// </summary>
internal static class VoiceFormat
{
    public const int SampleRate = 16000;
    public const int FrameMs = 20;
    public const int FrameSamples = SampleRate * FrameMs / 1000;
    public const int FramesPerPacket = 3;
    /// <summary>Opus bitrate in bits per second: clear speech at wideband, about 45 bytes a frame.</summary>
    public const int Bitrate = 18000;
    /// <summary>The largest Opus frame accepted from another player (a 20 ms frame at this bitrate is far smaller).</summary>
    public const int MaxFrameBytes = 400;
    /// <summary>The most frames a packet may carry; more is treated as a malformed packet.</summary>
    public const int MaxFramesPerPacket = 6;
}
