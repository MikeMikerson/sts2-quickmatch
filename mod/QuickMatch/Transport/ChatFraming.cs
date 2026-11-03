using System;
using System.Buffers.Binary;

namespace QuickMatch.Transport;

internal enum FrameKind : byte
{
    /// <summary>Client to lobby owner: let me connect. Value: protocol version.</summary>
    Hello = 1,
    /// <summary>
    /// Host to client: connected. The client takes everything the host sent from the lobby message that
    /// carried the first Welcome on (a repeated Welcome names that message, so nothing in between is skipped).
    /// </summary>
    Welcome = 2,
    /// <summary>Host to client: not connected. Value: reason.</summary>
    Reject = 3,
    /// <summary>Either side: the connection ends. Value: reason. Target 0 from a host: to every client.</summary>
    Bye = 4,
    /// <summary>A game message, or a piece of one. Target 0 from a host: to every client.</summary>
    Data = 5,
    /// <summary>Receiver to sender: lobby messages in this range never arrived, send them again.</summary>
    Nack = 6,
    /// <summary>Host to client: this match's relay region and room key (see <see cref="RelayRoute"/>).</summary>
    RelayOffer = 7,
    /// <summary>Receiver to sender: how the sender's relay copies compare with lobby chat. Value: <see cref="RelayVerdict"/>.</summary>
    RelayVerdict = 8,
    /// <summary>
    /// A member's relay keys (see <see cref="RelayRoute"/>): from any player its own, and from a host the
    /// voice key of another member of the match (tag 0: that member left).
    /// </summary>
    RelayKeys = 9,
}

/// <summary>One frame read from a lobby message. A data payload is a range of the message buffer.</summary>
internal struct Frame
{
    public FrameKind Kind;
    public ulong Target;
    public ushort Value;
    public bool Reliable;
    public byte Channel;
    public int Total;
    public int Offset;
    public int PayloadStart;
    public int PayloadLength;
    public uint NackSession;
    public uint NackFrom;
    public uint NackTo;
    public uint StartSeq;
    public ulong Member;
    public ulong Tag;
}

/// <summary>
/// Layout of a QuickMatch lobby chat message: a header (magic "QC", format version, the sender's session
/// and the message's sequence number), then frames. Every frame starts with its kind and target (a Steam
/// ID, or 0 for everyone). Numbers are little-endian.
///
///   Hello, Reject, Bye            kind | target u64 | value u16
///   Welcome                       kind | target u64 | start seq u32
///   Data                          kind | target u64 | flags u8 (1 = reliable) | channel u8 | total u32 | offset u32 | length u32 | bytes
///   Nack                          kind | target u64 | session u32 | first seq u32 | last seq u32
///   RelayOffer                    kind | target u64 | region length u8 | region (ASCII) | room key [32]
///   RelayVerdict                  kind | target u64 | value u16
///   RelayKeys                     kind | target u64 | member u64 | tag u64 | message key [32] | voice key [32]
///
/// A game message larger than the room left in a lobby message continues in the sender's next lobby
/// messages (same total, growing offset). A sender finishes one game message before starting the next,
/// so a receiver only ever has one partly received message per sender.
/// </summary>
internal static class ChatFraming
{
    public const byte FormatVersion = 1;
    public const int HeaderSize = 11;
    public const int ValueFrameSize = 11;
    public const int WelcomeFrameSize = 13;
    public const int DataFrameHeaderSize = 23;
    public const int NackFrameSize = 21;
    public const int MaxRegionLength = 32;
    public const int RelayKeysFrameSize = 25 + 2 * RelayCrypto.KeyBytes;

    private const byte Magic0 = (byte)'Q';
    private const byte Magic1 = (byte)'C';
    private const byte ReliableFlag = 1;

    public static void WriteHeader(Span<byte> message, uint session, uint seq)
    {
        message[0] = Magic0;
        message[1] = Magic1;
        message[2] = FormatVersion;
        BinaryPrimitives.WriteUInt32LittleEndian(message[3..], session);
        BinaryPrimitives.WriteUInt32LittleEndian(message[7..], seq);
    }

    public static bool TryReadHeader(ReadOnlySpan<byte> message, out uint session, out uint seq)
    {
        session = 0;
        seq = 0;
        if (message.Length < HeaderSize || message[0] != Magic0 || message[1] != Magic1 || message[2] != FormatVersion)
        {
            return false;
        }
        session = BinaryPrimitives.ReadUInt32LittleEndian(message[3..]);
        seq = BinaryPrimitives.ReadUInt32LittleEndian(message[7..]);
        return true;
    }

    public static int WriteValueFrame(Span<byte> destination, FrameKind kind, ulong target, ushort value)
    {
        destination[0] = (byte)kind;
        BinaryPrimitives.WriteUInt64LittleEndian(destination[1..], target);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[9..], value);
        return ValueFrameSize;
    }

    public static int WriteWelcomeFrame(Span<byte> destination, ulong target, uint startSeq)
    {
        destination[0] = (byte)FrameKind.Welcome;
        BinaryPrimitives.WriteUInt64LittleEndian(destination[1..], target);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[9..], startSeq);
        return WelcomeFrameSize;
    }

    public static int WriteDataFrame(Span<byte> destination, ulong target, bool reliable, byte channel, int total, int offset, ReadOnlySpan<byte> piece)
    {
        destination[0] = (byte)FrameKind.Data;
        BinaryPrimitives.WriteUInt64LittleEndian(destination[1..], target);
        destination[9] = reliable ? ReliableFlag : (byte)0;
        destination[10] = channel;
        BinaryPrimitives.WriteInt32LittleEndian(destination[11..], total);
        BinaryPrimitives.WriteInt32LittleEndian(destination[15..], offset);
        BinaryPrimitives.WriteInt32LittleEndian(destination[19..], piece.Length);
        piece.CopyTo(destination[DataFrameHeaderSize..]);
        return DataFrameHeaderSize + piece.Length;
    }

    public static int WriteNackFrame(Span<byte> destination, ulong target, uint session, uint first, uint last)
    {
        destination[0] = (byte)FrameKind.Nack;
        BinaryPrimitives.WriteUInt64LittleEndian(destination[1..], target);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[9..], session);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[13..], first);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[17..], last);
        return NackFrameSize;
    }

    public static int RelayOfferFrameSize(string region) => 10 + region.Length + RelayCrypto.KeyBytes;

    public static int WriteRelayOfferFrame(Span<byte> destination, ulong target, string region, ReadOnlySpan<byte> key)
    {
        if (region.Length > MaxRegionLength || key.Length != RelayCrypto.KeyBytes)
        {
            throw new ArgumentException("relay region too long or key of the wrong size");
        }
        destination[0] = (byte)FrameKind.RelayOffer;
        BinaryPrimitives.WriteUInt64LittleEndian(destination[1..], target);
        destination[9] = (byte)region.Length;
        for (int i = 0; i < region.Length; i++)
        {
            destination[10 + i] = (byte)region[i];
        }
        key.CopyTo(destination[(10 + region.Length)..]);
        return RelayOfferFrameSize(region);
    }

    /// <summary>The region and key of a relay offer frame read by <see cref="TryReadFrame"/>.</summary>
    public static (string Region, byte[] Key) ReadRelayOffer(ReadOnlySpan<byte> message, in Frame frame)
    {
        var region = new char[frame.PayloadLength];
        for (int i = 0; i < region.Length; i++)
        {
            region[i] = (char)message[frame.PayloadStart + i];
        }
        return (new string(region), message.Slice(frame.PayloadStart + frame.PayloadLength, RelayCrypto.KeyBytes).ToArray());
    }

    public static int WriteRelayKeysFrame(Span<byte> destination, ulong target, ulong member, ulong tag, ReadOnlySpan<byte> messageKey, ReadOnlySpan<byte> voiceKey)
    {
        if (messageKey.Length != RelayCrypto.KeyBytes || voiceKey.Length != RelayCrypto.KeyBytes)
        {
            throw new ArgumentException("relay key of the wrong size");
        }
        destination[0] = (byte)FrameKind.RelayKeys;
        BinaryPrimitives.WriteUInt64LittleEndian(destination[1..], target);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[9..], member);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[17..], tag);
        messageKey.CopyTo(destination[25..]);
        voiceKey.CopyTo(destination[(25 + RelayCrypto.KeyBytes)..]);
        return RelayKeysFrameSize;
    }

    /// <summary>The message and voice keys of a relay keys frame read by <see cref="TryReadFrame"/>.</summary>
    public static (byte[] MessageKey, byte[] VoiceKey) ReadRelayKeys(ReadOnlySpan<byte> message, in Frame frame) =>
        (message.Slice(frame.PayloadStart, RelayCrypto.KeyBytes).ToArray(),
         message.Slice(frame.PayloadStart + RelayCrypto.KeyBytes, RelayCrypto.KeyBytes).ToArray());

    /// <summary>Reads the frame at <paramref name="position"/> and moves past it. False at the end or on a malformed frame.</summary>
    public static bool TryReadFrame(ReadOnlySpan<byte> message, ref int position, out Frame frame)
    {
        frame = default;
        ReadOnlySpan<byte> rest = message[position..];
        if (rest.Length < ValueFrameSize)
        {
            return false;
        }
        frame.Kind = (FrameKind)rest[0];
        frame.Target = BinaryPrimitives.ReadUInt64LittleEndian(rest[1..]);
        switch (frame.Kind)
        {
            case FrameKind.Hello:
            case FrameKind.Reject:
            case FrameKind.Bye:
            case FrameKind.RelayVerdict:
                frame.Value = BinaryPrimitives.ReadUInt16LittleEndian(rest[9..]);
                position += ValueFrameSize;
                return true;
            case FrameKind.Welcome:
                if (rest.Length < WelcomeFrameSize)
                {
                    return false;
                }
                frame.StartSeq = BinaryPrimitives.ReadUInt32LittleEndian(rest[9..]);
                position += WelcomeFrameSize;
                return true;
            case FrameKind.Nack:
                if (rest.Length < NackFrameSize)
                {
                    return false;
                }
                frame.NackSession = BinaryPrimitives.ReadUInt32LittleEndian(rest[9..]);
                frame.NackFrom = BinaryPrimitives.ReadUInt32LittleEndian(rest[13..]);
                frame.NackTo = BinaryPrimitives.ReadUInt32LittleEndian(rest[17..]);
                position += NackFrameSize;
                return true;
            case FrameKind.RelayOffer:
            {
                int regionLength = rest[9];
                if (regionLength > MaxRegionLength || rest.Length < 10 + regionLength + RelayCrypto.KeyBytes)
                {
                    return false;
                }
                for (int i = 0; i < regionLength; i++)
                {
                    if (rest[10 + i] is < (byte)'a' or > (byte)'z')
                    {
                        return false;
                    }
                }
                frame.PayloadStart = position + 10;
                frame.PayloadLength = regionLength;
                position += 10 + regionLength + RelayCrypto.KeyBytes;
                return true;
            }
            case FrameKind.RelayKeys:
                if (rest.Length < RelayKeysFrameSize)
                {
                    return false;
                }
                frame.Member = BinaryPrimitives.ReadUInt64LittleEndian(rest[9..]);
                frame.Tag = BinaryPrimitives.ReadUInt64LittleEndian(rest[17..]);
                frame.PayloadStart = position + 25;
                frame.PayloadLength = 2 * RelayCrypto.KeyBytes;
                position += RelayKeysFrameSize;
                return true;
            case FrameKind.Data:
                if (rest.Length < DataFrameHeaderSize)
                {
                    return false;
                }
                frame.Reliable = (rest[9] & ReliableFlag) != 0;
                frame.Channel = rest[10];
                frame.Total = BinaryPrimitives.ReadInt32LittleEndian(rest[11..]);
                frame.Offset = BinaryPrimitives.ReadInt32LittleEndian(rest[15..]);
                frame.PayloadLength = BinaryPrimitives.ReadInt32LittleEndian(rest[19..]);
                if (frame.Total < 0 || frame.Offset < 0 || frame.PayloadLength < 0
                    || frame.PayloadLength > rest.Length - DataFrameHeaderSize
                    || (long)frame.Offset + frame.PayloadLength > frame.Total)
                {
                    return false;
                }
                frame.PayloadStart = position + DataFrameHeaderSize;
                position += DataFrameHeaderSize + frame.PayloadLength;
                return true;
            default:
                return false;
        }
    }
}
