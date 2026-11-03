using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace QuickMatch.Voice;

/// <summary>What a player's voice is set to, as their teammates' rows show it.</summary>
internal enum VoiceStatus : byte
{
    /// <summary>Voice chat switched off in their settings.</summary>
    Off = 0,
    /// <summary>They hear the others but cannot talk (no microphone permission on their device).</summary>
    ListenOnly = 1,
    /// <summary>Their mic is muted.</summary>
    Muted = 2,
    /// <summary>Their mic is on (they may be silent).</summary>
    Live = 3,
}

/// <summary>
/// One voice packet: a few consecutive Opus frames of one speaker. <see cref="Stream"/> is a random
/// number the speaker picks for each voice session; <see cref="FirstFrame"/> counts 20 ms frames on
/// the speaker's own clock, which keeps running through silence, so a receiver can tell a gap from
/// a pause and never sees the same frame number twice in a stream.
///
///   frames part = flags u8 | frame count u8 | (length u16 | Opus bytes) per frame
///
/// Flags: 1 = the first packet after silence, 2 = the last packet before silence, 4 = a status
/// packet, whose one 1-byte frame is the sender's <see cref="VoiceStatus"/> (sent when it changes and
/// every few seconds, under its own stream number so its nonces never meet the audio's). Numbers are
/// little-endian. The relay wraps the frames part in <see cref="VoiceCrypto"/>; between desktops the
/// game's own (Steam-encrypted) connection carries it as it is.
/// </summary>
internal sealed record VoicePacket(uint Stream, uint FirstFrame, bool StartsTalk, bool EndsTalk, IReadOnlyList<byte[]> Frames)
{
    private const byte StartFlag = 1;
    private const byte EndFlag = 2;
    private const byte StatusFlag = 4;

    /// <summary>Set on a status packet, which carries no audio.</summary>
    public VoiceStatus? Status { get; private init; }

    /// <summary>A status packet; <paramref name="seq"/> must grow with each one sent under <paramref name="stream"/>.</summary>
    public static VoicePacket ForStatus(uint stream, uint seq, VoiceStatus status) =>
        new(stream, seq, false, false, Array.Empty<byte[]>()) { Status = status };

    /// <summary>The longest frames part a valid packet can have.</summary>
    public const int MaxFramesPartBytes = 2 + VoiceFormat.MaxFramesPerPacket * (2 + VoiceFormat.MaxFrameBytes);

    public byte[] WriteFrames()
    {
        if (Status is { } status)
        {
            return new byte[] { StatusFlag, 1, 1, 0, (byte)status };
        }
        int length = 2;
        foreach (byte[] frame in Frames)
        {
            length += 2 + frame.Length;
        }
        var part = new byte[length];
        part[0] = (byte)((StartsTalk ? StartFlag : 0) | (EndsTalk ? EndFlag : 0));
        part[1] = (byte)Frames.Count;
        int at = 2;
        foreach (byte[] frame in Frames)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(part.AsSpan(at), (ushort)frame.Length);
            frame.CopyTo(part, at + 2);
            at += 2 + frame.Length;
        }
        return part;
    }

    /// <summary>
    /// Reads a frames part sent by another player, or returns null for anything malformed: no frames,
    /// too many, an empty or oversized frame, or bytes left over.
    /// </summary>
    public static VoicePacket? ReadFrames(uint stream, uint firstFrame, ReadOnlySpan<byte> part)
    {
        if (part.Length < 2 || part.Length > MaxFramesPartBytes)
        {
            return null;
        }
        byte flags = part[0];
        int count = part[1];
        if (flags == StatusFlag)
        {
            return part.Length == 5 && count == 1 && part[2] == 1 && part[3] == 0 && part[4] <= (byte)VoiceStatus.Live
                ? ForStatus(stream, firstFrame, (VoiceStatus)part[4])
                : null;
        }
        if (count is 0 or > VoiceFormat.MaxFramesPerPacket || (flags & ~(StartFlag | EndFlag)) != 0 || firstFrame > uint.MaxValue - (uint)count)
        {
            return null;
        }
        var frames = new byte[count][];
        int at = 2;
        for (int i = 0; i < count; i++)
        {
            if (part.Length < at + 2)
            {
                return null;
            }
            int length = BinaryPrimitives.ReadUInt16LittleEndian(part[at..]);
            at += 2;
            if (length is 0 or > VoiceFormat.MaxFrameBytes || part.Length < at + length)
            {
                return null;
            }
            frames[i] = part.Slice(at, length).ToArray();
            at += length;
        }
        return at == part.Length ? new VoicePacket(stream, firstFrame, (flags & StartFlag) != 0, (flags & EndFlag) != 0, frames) : null;
    }
}
