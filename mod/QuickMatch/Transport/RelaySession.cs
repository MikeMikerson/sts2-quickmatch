using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace QuickMatch.Transport;

internal enum RelayEventKind
{
    /// <summary>In the room; <see cref="RelaySession.Region"/> and <see cref="RelaySession.Members"/> are set.</summary>
    Joined,
    MemberJoined,
    MemberLeft,
    Data,
    /// <summary>The connection ended or never opened. Nothing follows.</summary>
    Closed,
}

/// <param name="Tag">For <see cref="RelayEventKind.Data"/> and <see cref="RelayEventKind.MemberJoined"/>: the member's tag then, or 0 if it was not in the room.</param>
/// <param name="WasJoined">For <see cref="RelayEventKind.Closed"/>: the session had been in the room.</param>
internal readonly record struct RelayEvent(RelayEventKind Kind, ulong Member = 0, byte[]? Payload = null, string? Reason = null, bool WasJoined = false, ulong Tag = 0);

/// <summary>
/// One member's connection to a relay room: joins with the room token and a secret of its own, then
/// sends and receives data frames (the relay's README describes them). The first join of a member ID
/// in a room holds it: the relay refuses a later join for that ID unless it brings the same secret, so
/// a reconnect works and a second player claiming the ID does not. The relay tells every member each
/// other member's tag, the start of the SHA-256 of that member's secret; players announce their own
/// tag over Steam lobby chat, so a connection under someone else's ID shows up with the wrong tag.
/// Socket events may arrive on any thread and are held until <see cref="Poll"/>, which runs on the
/// game's thread like everything else here.
/// </summary>
internal sealed class RelaySession : IRelayListener, IDisposable
{
    /// <summary>2: joins carry a secret and the relay reports each member's tag.</summary>
    public const byte ProtocolVersion = 2;
    public const int SecretBytes = 16;
    /// <summary>Opening and joining must finish within this long, or the session closes.</summary>
    public const long JoinTimeoutMs = 10000;
    /// <summary>The relay's own limit on a data frame's payload.</summary>
    public const int MaxPayloadBytes = 64 * 1024;

    private const byte FrameJoin = 0x4a;
    private const byte FrameWelcome = 0x57;
    private const byte FrameData = 0x44;
    private const byte FrameMemberJoined = 0x2b;
    private const byte FrameMemberLeft = 0x2d;
    private const int JoinBytes = 42 + SecretBytes;
    private const int MemberEntryBytes = 16;
    private const int DataHeaderBytes = 9;

    private readonly IRelaySocket _socket;
    private readonly byte[] _room;
    private readonly ulong _local;
    private readonly byte[] _secret;
    private readonly long _openedAt;
    private readonly ConcurrentQueue<(byte[]? Message, string? Closed, bool Opened)> _inbox = new();
    /// <summary>The room's other members and their tags.</summary>
    private readonly Dictionary<ulong, ulong> _members = new();
    private bool _closed;

    /// <param name="secret">This member's secret for the match (<see cref="SecretBytes"/>); a new random one if null.</param>
    public RelaySession(IRelayTransport transport, string region, byte[] room, ulong local, long now, byte[]? secret = null)
    {
        _room = room;
        _local = local;
        _secret = secret ?? NewSecret();
        if (_secret.Length != SecretBytes)
        {
            throw new ArgumentException($"a relay secret has {SecretBytes} bytes", nameof(secret));
        }
        Tag = TagOf(_secret);
        _openedAt = now;
        _socket = transport.Open(region, this);
    }

    public bool IsJoined { get; private set; }

    public bool IsClosed => _closed;

    /// <summary>The relay region the room lives in, once joined.</summary>
    public string Region { get; private set; } = "";

    /// <summary>The room's other members.</summary>
    public IReadOnlyCollection<ulong> Members => _members.Keys;

    /// <summary>Our own tag, as the relay reports it to the others.</summary>
    public ulong Tag { get; }

    public static byte[] NewSecret() => RandomNumberGenerator.GetBytes(SecretBytes);

    /// <summary>The tag the relay reports for a member who joined with <paramref name="secret"/>.</summary>
    public static ulong TagOf(ReadOnlySpan<byte> secret) => BinaryPrimitives.ReadUInt64LittleEndian(SHA256.HashData(secret));

    /// <summary>The tag of another member in the room.</summary>
    public bool TryGetTag(ulong member, out ulong tag) => _members.TryGetValue(member, out tag);

    /// <summary>Takes what the socket delivered since the last call.</summary>
    public List<RelayEvent> Poll(long now)
    {
        var events = new List<RelayEvent>();
        while (!_closed && _inbox.TryDequeue(out var item))
        {
            if (item.Opened)
            {
                var join = new byte[JoinBytes];
                join[0] = FrameJoin;
                join[1] = ProtocolVersion;
                _room.CopyTo(join, 2);
                BinaryPrimitives.WriteUInt64LittleEndian(join.AsSpan(34), _local);
                _secret.CopyTo(join, 42);
                _socket.Send(join);
            }
            else if (item.Closed != null)
            {
                Close(events, item.Closed);
            }
            else if (item.Message != null)
            {
                Read(item.Message, events);
            }
        }
        if (!_closed && !IsJoined && now - _openedAt >= JoinTimeoutMs)
        {
            Close(events, $"not in the room after {JoinTimeoutMs} ms");
        }
        return events;
    }

    /// <summary>Sends a payload to one member, or to every other member with target 0.</summary>
    public void Send(ulong target, byte[] payload)
    {
        if (!IsJoined || _closed)
        {
            return;
        }
        var frame = new byte[DataHeaderBytes + payload.Length];
        frame[0] = FrameData;
        BinaryPrimitives.WriteUInt64LittleEndian(frame.AsSpan(1), target);
        payload.CopyTo(frame, DataHeaderBytes);
        _socket.Send(frame);
    }

    void IRelayListener.Opened() => _inbox.Enqueue((null, null, true));

    void IRelayListener.Received(byte[] message) => _inbox.Enqueue((message, null, false));

    void IRelayListener.Closed(string reason) => _inbox.Enqueue((null, reason, false));

    public void Dispose()
    {
        _closed = true;
        IsJoined = false;
        _members.Clear();
        _socket.Dispose();
    }

    private void Read(byte[] message, List<RelayEvent> events)
    {
        if (message.Length == 0)
        {
            return;
        }
        switch (message[0])
        {
            case FrameWelcome when !IsJoined && message.Length >= 4:
            {
                int regionLength = message[2];
                if (message[1] != ProtocolVersion || message.Length < 4 + regionLength)
                {
                    Close(events, "unexpected welcome from the relay");
                    return;
                }
                Region = Encoding.ASCII.GetString(message, 3, regionLength);
                int count = message[3 + regionLength];
                if (message.Length < 4 + regionLength + MemberEntryBytes * count)
                {
                    Close(events, "unexpected welcome from the relay");
                    return;
                }
                for (int i = 0; i < count; i++)
                {
                    int at = 4 + regionLength + MemberEntryBytes * i;
                    _members[BinaryPrimitives.ReadUInt64LittleEndian(message.AsSpan(at))] = BinaryPrimitives.ReadUInt64LittleEndian(message.AsSpan(at + 8));
                }
                IsJoined = true;
                events.Add(new RelayEvent(RelayEventKind.Joined));
                break;
            }
            case FrameMemberJoined when IsJoined && message.Length >= 1 + MemberEntryBytes:
            {
                ulong member = BinaryPrimitives.ReadUInt64LittleEndian(message.AsSpan(1));
                ulong tag = BinaryPrimitives.ReadUInt64LittleEndian(message.AsSpan(9));
                _members[member] = tag;
                events.Add(new RelayEvent(RelayEventKind.MemberJoined, member, Tag: tag));
                break;
            }
            case FrameMemberLeft when IsJoined && message.Length >= 9:
            {
                ulong member = BinaryPrimitives.ReadUInt64LittleEndian(message.AsSpan(1));
                _members.Remove(member);
                events.Add(new RelayEvent(RelayEventKind.MemberLeft, member));
                break;
            }
            case FrameData when IsJoined && message.Length > DataHeaderBytes:
            {
                ulong sender = BinaryPrimitives.ReadUInt64LittleEndian(message.AsSpan(1));
                _members.TryGetValue(sender, out ulong tag);
                events.Add(new RelayEvent(RelayEventKind.Data, sender, message.AsSpan(DataHeaderBytes).ToArray(), Tag: tag));
                break;
            }
        }
    }

    private void Close(List<RelayEvent> events, string reason)
    {
        if (_closed)
        {
            return;
        }
        bool wasJoined = IsJoined;
        Dispose();
        events.Add(new RelayEvent(RelayEventKind.Closed, Reason: reason, WasJoined: wasJoined));
    }
}
