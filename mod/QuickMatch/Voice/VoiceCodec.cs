using System;
using Concentus.Enums;
using Concentus.Structs;

namespace QuickMatch.Voice;

/// <summary>
/// Opus encoding of the local player's voice (<see cref="VoiceFormat"/>), with Concentus, the pure C#
/// port compiled into this mod (ThirdParty/Concentus). Only the managed codec is used: the native
/// libopus loader was left out, so this runs wherever the mod does.
/// </summary>
internal sealed class VoiceEncoder
{
    private readonly OpusEncoder _encoder;
    private readonly byte[] _buffer = new byte[VoiceFormat.MaxFrameBytes];

    public VoiceEncoder()
    {
#pragma warning disable CS0618 // The factory that replaces this constructor only adds the native library we left out.
        _encoder = new OpusEncoder(VoiceFormat.SampleRate, 1, OpusApplication.OPUS_APPLICATION_VOIP);
#pragma warning restore CS0618
        _encoder.Bitrate = VoiceFormat.Bitrate;
        _encoder.SignalType = OpusSignal.OPUS_SIGNAL_VOICE;
        // Mid complexity: plenty for speech and light on a phone's CPU.
        _encoder.Complexity = 5;
        // Steam sockets between desktops can lose packets; the relay cannot (it is one WebSocket).
        _encoder.UseInbandFEC = true;
        _encoder.PacketLossPercent = 5;
    }

    /// <summary>Encodes one 20 ms frame.</summary>
    public byte[] Encode(ReadOnlySpan<short> frame)
    {
        if (frame.Length != VoiceFormat.FrameSamples)
        {
            throw new ArgumentException($"a frame has {VoiceFormat.FrameSamples} samples", nameof(frame));
        }
        int length = _encoder.Encode(frame, VoiceFormat.FrameSamples, _buffer, _buffer.Length);
        return _buffer.AsSpan(0, length).ToArray();
    }

    /// <summary>Forgets the previous frames: called when speech starts after silence.</summary>
    public void Reset() => _encoder.ResetState();
}

/// <summary>Opus decoding of one other player's voice. Never throws on what that player sent.</summary>
internal sealed class VoiceDecoder
{
    private readonly OpusDecoder _decoder;

    public VoiceDecoder()
    {
#pragma warning disable CS0618 // See VoiceEncoder.
        _decoder = new OpusDecoder(VoiceFormat.SampleRate, 1);
#pragma warning restore CS0618
    }

    /// <summary>Decodes one frame into <paramref name="pcm"/>; on a bad frame, plays a concealed one instead.</summary>
    public void Decode(ReadOnlySpan<byte> frame, Span<short> pcm)
    {
        if (!TryDecode(frame, pcm, fec: false))
        {
            Conceal(pcm, next: default);
        }
    }

    /// <summary>
    /// Fills in a lost frame: from the error-correction data of the frame after it when there is one,
    /// otherwise by Opus's own concealment.
    /// </summary>
    public void Conceal(Span<short> pcm, ReadOnlySpan<byte> next)
    {
        if (next.Length > 0 && TryDecode(next, pcm, fec: true))
        {
            return;
        }
        if (!TryDecode(ReadOnlySpan<byte>.Empty, pcm, fec: false))
        {
            pcm[..VoiceFormat.FrameSamples].Clear();
        }
    }

    /// <summary>Forgets the previous frames: called when a new stretch of speech starts.</summary>
    public void Reset() => _decoder.ResetState();

    private bool TryDecode(ReadOnlySpan<byte> frame, Span<short> pcm, bool fec)
    {
        try
        {
            int samples = _decoder.Decode(frame, pcm, VoiceFormat.FrameSamples, fec);
            if (samples == VoiceFormat.FrameSamples)
            {
                return true;
            }
        }
        catch (Exception)
        {
            // A malformed frame from another player: fall through to concealment.
        }
        _decoder.ResetState();
        return false;
    }
}
