using System;
using System.Collections.Generic;
using System.Linq;

namespace QuickMatch.Transport;

/// <summary>Why a link stopped hearing a peer.</summary>
internal enum LinkLoss
{
    /// <summary>Nothing arrived for <see cref="ChatLink.PeerTimeoutMs"/>.</summary>
    Timeout,
    /// <summary>Lobby messages went missing and were not sent again in time.</summary>
    Gap,
    /// <summary>The peer started a new session (its game restarted the connection).</summary>
    Restarted,
}

/// <summary>What a <see cref="ChatLink"/> found in incoming lobby messages.</summary>
internal interface IChatLinkEvents
{
    /// <summary>A Hello addressed to us. Return true to take the sender's messages from this one on.</summary>
    bool OnHello(ulong sender, ushort protocol);

    /// <summary>A Welcome addressed to us from a sender not yet followed. Return true to take its messages from this one on.</summary>
    bool OnWelcome(ulong sender);

    void OnReject(ulong sender, ushort reason);

    /// <summary>The sender ends the connection. The link has already stopped following it.</summary>
    void OnBye(ulong sender, ushort reason);

    void OnData(ulong sender, byte[] data, bool reliable, byte channel);

    /// <summary>A followed peer was lost. The link has already stopped following it.</summary>
    void OnLost(ulong peer, LinkLoss why);

    /// <summary>A followed peer (the host) offers this match's relay region and room key.</summary>
    void OnRelayOffer(ulong sender, string region, byte[] key);

    /// <summary>A followed peer announces a member's relay keys: its own, or (from a host) another member's.</summary>
    void OnRelayKeys(ulong sender, ulong member, ulong tag, byte[] messageKey, byte[] voiceKey);

    /// <summary>A followed peer says how our relay copies compare with lobby chat (<see cref="RelayVerdict"/>).</summary>
    void OnRelayVerdict(ulong sender, ushort verdict);
}

/// <summary>Sends one lobby chat message; target 0 is everyone. Returns false if Steam refused it.</summary>
internal delegate bool ChatSend(byte[] message, int length, ulong target);

/// <summary>
/// One player's end of the lobby chat transport, free of Steam and game types so it can be tested on
/// its own. Outgoing frames are batched into one lobby message per <see cref="MinSendIntervalMs"/> on
/// average: Steam lets a sender put about 17 lobby chat messages a second through and silently drops the
/// rest, so a link stays at 10. A link that was quiet may send up to
/// <see cref="BurstMessages"/> at once, so a card played between other messages does not wait for the
/// next slot; any one second still carries at most 13. Every lobby message carries the sender's session and a
/// sequence number. Messages from a followed peer are taken in order; a gap holds later ones back and is
/// asked for again with a Nack, and the sender keeps what it sent for <see cref="ResendKeepMs"/>.
/// The same lobby messages may also travel over the relay (<see cref="RelayRoute"/>); a copy that
/// arrives second is dropped by its sequence number, and with <see cref="FastPacing"/> (every peer on
/// the relay) the link sends at the relay's pace instead of Steam's.
/// Not thread safe: the owner calls everything from one thread.
/// </summary>
internal sealed class ChatLink
{
    public const int MaxMessageBytes = 32 * 1024;
    public const long MinSendIntervalMs = 100;
    public const int BurstMessages = 3;
    /// <summary>Pace when lobby chat is not used: at most 50 messages a second, half the relay's limit.</summary>
    public const long FastSendIntervalMs = 20;
    public const int FastBurstMessages = 5;
    public const long HeartbeatMs = 2000;
    /// <summary>The game's own Steam connections time out after 20 seconds.</summary>
    public const long PeerTimeoutMs = 20000;
    public const long NackIntervalMs = 1000;
    public const int MaxNacks = 5;
    public const long ResendKeepMs = 15000;
    /// <summary>An unreliable game message still waiting this long is dropped, as a congested socket would.</summary>
    public const long UnreliableMaxAgeMs = 1000;
    public const int MaxGameMessageBytes = 8 * 1024 * 1024;

    private const int MaxEarlyMessages = 256;
    private const uint MaxNackSpan = 64;
    private const int MaxNackRanges = 16;
    private const int MinPiece = 1024;

    private sealed class Outgoing
    {
        public FrameKind Kind;
        public ulong Target;
        public ushort Value;
        public string Text = "";
        public byte[] Bytes = [];
        public bool Reliable;
        public byte Channel;
        public ulong Member;
        public ulong Tag;
        public long QueuedAt;
        public int SentBytes;
    }

    private sealed class Peer(ulong id, uint session, uint nextSeq, long now)
    {
        public readonly ulong Id = id;
        public readonly uint Session = session;
        public uint NextSeq = nextSeq;
        public long LastHeard = now;
        public readonly SortedDictionary<uint, byte[]> Early = new();
        /// <summary>When the gaps were last asked for; 0 when there is no gap, so a new one is asked for at once.</summary>
        public long NackedAt;
        /// <summary>Requests since the last message that closed a gap.</summary>
        public int Nacks;
        /// <summary>Followed from an earlier message than the Welcome that arrived: skip frames until that Welcome.</summary>
        public bool AwaitingWelcome;
        public byte[]? Partial;
        public int PartialFilled;
        public bool PartialReliable;
        public byte PartialChannel;
    }

    private readonly record struct Sent(uint Seq, byte[] Bytes, ulong Target, long At);

    private readonly ulong _local;
    private readonly uint _session;
    private readonly IChatLinkEvents _events;
    private readonly Action<string>? _log;
    private readonly Queue<Outgoing> _queue = new();
    private readonly List<(ulong Target, uint Session, uint First, uint Last)> _nacks = new();
    private readonly Dictionary<ulong, Peer> _peers = new();
    private readonly Dictionary<ulong, uint> _welcomeStarts = new();
    private readonly Queue<Sent> _sent = new();
    private readonly Dictionary<uint, Sent> _sentBySeq = new();
    private readonly Queue<uint> _resend = new();
    private readonly HashSet<uint> _resendQueued = new();
    private readonly byte[] _buffer = new byte[MaxMessageBytes];
    private uint _nextSeq = 1;
    private long _lastSentAt;
    private bool _sentAny;
    /// <summary>Sending allowance in milliseconds: each lobby message costs MinSendIntervalMs, time refills it.</summary>
    private long _creditMs = BurstMessages * MinSendIntervalMs;
    private long? _creditAt;

    public ChatLink(ulong local, uint session, IChatLinkEvents events, Action<string>? log = null)
    {
        _local = local;
        _session = session;
        _events = events;
        _log = log;
    }

    /// <summary>Where lobby messages go: 0 for every member, or one member where the backend can target.</summary>
    public ulong SendTarget { get; set; }

    /// <summary>Send an empty lobby message after <see cref="HeartbeatMs"/> without one, so peers know we are there.</summary>
    public bool KeepAlive { get; set; }

    /// <summary>Pace for the relay instead of Steam lobby chat. Set while no lobby chat copies are sent.</summary>
    public bool FastPacing { get; set; }

    private long IntervalMs => FastPacing ? FastSendIntervalMs : MinSendIntervalMs;

    private int Burst => FastPacing ? FastBurstMessages : BurstMessages;

    public uint Session => _session;

    public bool IsFollowing(ulong peer) => _peers.ContainsKey(peer);

    /// <summary>Stops taking a peer's messages, without an event.</summary>
    public void Forget(ulong peer)
    {
        _peers.Remove(peer);
        _welcomeStarts.Remove(peer);
    }

    public void QueueHello(ulong target, ushort protocol, long now) => QueueValue(FrameKind.Hello, target, protocol, now);

    public void QueueWelcome(ulong target, long now) => QueueValue(FrameKind.Welcome, target, 0, now);

    public void QueueReject(ulong target, ushort reason, long now) => QueueValue(FrameKind.Reject, target, reason, now);

    public void QueueBye(ulong target, ushort reason, long now) => QueueValue(FrameKind.Bye, target, reason, now);

    public void QueueRelayOffer(ulong target, string region, byte[] key, long now) =>
        _queue.Enqueue(new Outgoing { Kind = FrameKind.RelayOffer, Target = target, Text = region, Bytes = key, QueuedAt = now });

    /// <summary>Queues a member's relay keys for one peer.</summary>
    public void QueueRelayKeys(ulong target, ulong member, ulong tag, byte[] messageKey, byte[] voiceKey, long now) =>
        _queue.Enqueue(new Outgoing { Kind = FrameKind.RelayKeys, Target = target, Member = member, Tag = tag, Bytes = [.. messageKey, .. voiceKey], QueuedAt = now });

    public void QueueRelayVerdict(ulong target, ushort verdict, long now) => QueueValue(FrameKind.RelayVerdict, target, verdict, now);

    /// <summary>Queues a game message. The bytes are copied, so the caller may reuse its buffer.</summary>
    public void QueueData(ulong target, byte[] bytes, int length, bool reliable, byte channel, long now)
    {
        if (length > MaxGameMessageBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(length), $"game message of {length} bytes is larger than {MaxGameMessageBytes}");
        }
        var copy = new byte[length];
        Array.Copy(bytes, copy, length);
        _queue.Enqueue(new Outgoing { Kind = FrameKind.Data, Target = target, Bytes = copy, Reliable = reliable, Channel = channel, QueuedAt = now });
    }

    public void Receive(ulong sender, byte[] message, long now)
    {
        if (sender == _local || !ChatFraming.TryReadHeader(message, out uint session, out uint seq))
        {
            return;
        }
        if (!_peers.TryGetValue(sender, out Peer? peer))
        {
            ReceiveUnfollowed(sender, message, session, seq, now);
            return;
        }
        if (session != peer.Session)
        {
            Lose(peer, LinkLoss.Restarted);
            ReceiveUnfollowed(sender, message, session, seq, now);
            return;
        }
        peer.LastHeard = now;
        if (seq < peer.NextSeq || peer.Early.ContainsKey(seq))
        {
            return;
        }
        // Requests to send again are answered at once, also from a message held behind a gap: each
        // side may be waiting for a lost message of the other.
        TakeNacks(message);
        if (seq > peer.NextSeq)
        {
            if (peer.Early.Count >= MaxEarlyMessages)
            {
                _log?.Invoke($"peer {peer.Id}: {peer.Early.Count} lobby messages wait behind a gap at {peer.NextSeq}");
                Lose(peer, LinkLoss.Gap);
                return;
            }
            peer.Early[seq] = message;
            return;
        }
        Take(peer, seq, message, ChatFraming.HeaderSize);
        while (IsCurrent(peer) && peer.Early.Count > 0 && peer.Early.Remove(peer.NextSeq, out byte[]? next))
        {
            Take(peer, peer.NextSeq, next, ChatFraming.HeaderSize);
        }
        peer.Nacks = 0;
        if (peer.Early.Count == 0)
        {
            peer.NackedAt = 0;
        }
    }

    /// <summary>Checks peers for silence and gaps, and sends the next lobby message if one is due.</summary>
    public void Tick(long now, ChatSend send)
    {
        foreach (Peer peer in _peers.Values.ToList())
        {
            if (now - peer.LastHeard > PeerTimeoutMs)
            {
                _log?.Invoke($"peer {peer.Id}: nothing heard for {now - peer.LastHeard} ms");
                Lose(peer, LinkLoss.Timeout);
                continue;
            }
            if (peer.Early.Count > 0 && now - peer.NackedAt >= NackIntervalMs)
            {
                if (peer.Nacks >= MaxNacks)
                {
                    _log?.Invoke($"peer {peer.Id}: lobby message {peer.NextSeq} still missing after {MaxNacks} requests");
                    Lose(peer, LinkLoss.Gap);
                    continue;
                }
                AskAgain(peer);
                peer.NackedAt = now;
                peer.Nacks++;
            }
        }
        while (_sent.Count > 0 && now - _sent.Peek().At > ResendKeepMs)
        {
            _sentBySeq.Remove(_sent.Dequeue().Seq);
        }

        if (_creditAt is { } since)
        {
            _creditMs = Math.Min(Burst * IntervalMs, _creditMs + (now - since));
        }
        _creditAt = now;
        if (_creditMs < IntervalMs)
        {
            return;
        }
        while (_resend.Count > 0)
        {
            uint seq = _resend.Dequeue();
            _resendQueued.Remove(seq);
            if (_sentBySeq.TryGetValue(seq, out Sent again))
            {
                send(again.Bytes, again.Bytes.Length, again.Target);
                _lastSentAt = now;
                _creditMs -= IntervalMs;
                return;
            }
        }
        bool heartbeatDue = KeepAlive && (!_sentAny || now - _lastSentAt >= HeartbeatMs);
        if (_queue.Count == 0 && _nacks.Count == 0 && !heartbeatDue)
        {
            return;
        }
        SendNext(now, send, heartbeatDue);
    }

    /// <summary>Sends whatever is queued at once, ignoring the rate. For the last message before leaving the lobby.</summary>
    public void Flush(long now, ChatSend send)
    {
        if (_queue.Count > 0 || _nacks.Count > 0)
        {
            SendNext(now, send, sendEmpty: false);
        }
    }

    private void QueueValue(FrameKind kind, ulong target, ushort value, long now)
    {
        _queue.Enqueue(new Outgoing { Kind = kind, Target = target, Value = value, QueuedAt = now });
    }

    private void SendNext(long now, ChatSend send, bool sendEmpty)
    {
        int length = Build(now);
        if (length == ChatFraming.HeaderSize && !sendEmpty)
        {
            return;
        }
        uint seq = _nextSeq++;
        ChatFraming.WriteHeader(_buffer, _session, seq);
        byte[] message = _buffer.AsSpan(0, length).ToArray();
        if (!send(message, length, SendTarget))
        {
            // Kept like any other: a peer that misses it asks for it again.
            _log?.Invoke($"Steam refused lobby message {seq} ({length} bytes)");
        }
        var sent = new Sent(seq, message, SendTarget, now);
        _sent.Enqueue(sent);
        _sentBySeq[seq] = sent;
        _lastSentAt = now;
        _creditMs -= IntervalMs;
        _sentAny = true;
    }

    /// <summary>Fills the buffer after the header with Nacks, then queued frames in order. Returns the length.</summary>
    private int Build(long now)
    {
        int position = ChatFraming.HeaderSize;
        int nacks = 0;
        while (nacks < _nacks.Count && position + ChatFraming.NackFrameSize <= MaxMessageBytes)
        {
            (ulong target, uint session, uint first, uint last) = _nacks[nacks++];
            position += ChatFraming.WriteNackFrame(_buffer.AsSpan(position), target, session, first, last);
        }
        _nacks.RemoveRange(0, nacks);

        while (_queue.Count > 0)
        {
            Outgoing next = _queue.Peek();
            if (next.Kind == FrameKind.Welcome)
            {
                if (position + ChatFraming.WelcomeFrameSize > MaxMessageBytes)
                {
                    break;
                }
                // The first Welcome's lobby message is where this peer starts; later ones point back to it.
                if (!_welcomeStarts.TryGetValue(next.Target, out uint start))
                {
                    start = _nextSeq;
                    _welcomeStarts[next.Target] = start;
                }
                position += ChatFraming.WriteWelcomeFrame(_buffer.AsSpan(position), next.Target, start);
                _queue.Dequeue();
                continue;
            }
            if (next.Kind == FrameKind.RelayOffer)
            {
                if (position + ChatFraming.RelayOfferFrameSize(next.Text) > MaxMessageBytes)
                {
                    break;
                }
                position += ChatFraming.WriteRelayOfferFrame(_buffer.AsSpan(position), next.Target, next.Text, next.Bytes);
                _queue.Dequeue();
                continue;
            }
            if (next.Kind == FrameKind.RelayKeys)
            {
                if (position + ChatFraming.RelayKeysFrameSize > MaxMessageBytes)
                {
                    break;
                }
                position += ChatFraming.WriteRelayKeysFrame(_buffer.AsSpan(position), next.Target, next.Member, next.Tag,
                    next.Bytes.AsSpan(0, RelayCrypto.KeyBytes), next.Bytes.AsSpan(RelayCrypto.KeyBytes));
                _queue.Dequeue();
                continue;
            }
            if (next.Kind != FrameKind.Data)
            {
                if (position + ChatFraming.ValueFrameSize > MaxMessageBytes)
                {
                    break;
                }
                position += ChatFraming.WriteValueFrame(_buffer.AsSpan(position), next.Kind, next.Target, next.Value);
                _queue.Dequeue();
                continue;
            }
            if (!next.Reliable && next.SentBytes == 0 && now - next.QueuedAt > UnreliableMaxAgeMs)
            {
                _queue.Dequeue();
                continue;
            }
            int room = MaxMessageBytes - position - ChatFraming.DataFrameHeaderSize;
            int remaining = next.Bytes.Length - next.SentBytes;
            // Do not start a message with a sliver at the end of a full lobby message.
            if (room <= 0 || (remaining > room && room < MinPiece && position > ChatFraming.HeaderSize))
            {
                break;
            }
            int piece = Math.Min(room, remaining);
            position += ChatFraming.WriteDataFrame(_buffer.AsSpan(position), next.Target, next.Reliable, next.Channel,
                next.Bytes.Length, next.SentBytes, next.Bytes.AsSpan(next.SentBytes, piece));
            next.SentBytes += piece;
            if (next.SentBytes < next.Bytes.Length)
            {
                break;
            }
            _queue.Dequeue();
        }
        return position;
    }

    private void ReceiveUnfollowed(ulong sender, byte[] message, uint session, uint seq, long now)
    {
        int position = ChatFraming.HeaderSize;
        while (ChatFraming.TryReadFrame(message, ref position, out Frame frame))
        {
            if (frame.Target != _local)
            {
                continue;
            }
            switch (frame.Kind)
            {
                case FrameKind.Hello when _events.OnHello(sender, frame.Value):
                    TakeFrames(Follow(sender, session, seq + 1, now), message, position);
                    return;
                case FrameKind.Welcome when _events.OnWelcome(sender):
                    if (frame.StartSeq == 0 || frame.StartSeq >= seq)
                    {
                        TakeFrames(Follow(sender, session, seq + 1, now), message, position);
                    }
                    else
                    {
                        // A repeated Welcome: the host's messages count from the first one, which never
                        // arrived. Hold this one back and ask for the ones before it.
                        Peer peer = Follow(sender, session, frame.StartSeq, now);
                        peer.AwaitingWelcome = true;
                        peer.Early[seq] = message;
                    }
                    return;
                case FrameKind.Reject:
                    _events.OnReject(sender, frame.Value);
                    break;
                case FrameKind.Bye:
                    _events.OnBye(sender, frame.Value);
                    break;
            }
        }
    }

    private void Take(Peer peer, uint seq, byte[] message, int position)
    {
        peer.NextSeq = seq + 1;
        TakeFrames(peer, message, position);
    }

    private void TakeFrames(Peer peer, byte[] message, int position)
    {
        while (IsCurrent(peer) && ChatFraming.TryReadFrame(message, ref position, out Frame frame))
        {
            bool forUs = frame.Target == _local || (frame.Target == 0 && frame.Kind is FrameKind.Data or FrameKind.Bye);
            if (!forUs)
            {
                continue;
            }
            if (peer.AwaitingWelcome)
            {
                peer.AwaitingWelcome = frame.Kind != FrameKind.Welcome;
                continue;
            }
            switch (frame.Kind)
            {
                case FrameKind.Hello:
                    _events.OnHello(peer.Id, frame.Value);
                    break;
                case FrameKind.Reject:
                    _events.OnReject(peer.Id, frame.Value);
                    break;
                case FrameKind.Bye:
                    Forget(peer.Id);
                    _events.OnBye(peer.Id, frame.Value);
                    return;
                case FrameKind.Data:
                    TakeData(peer, message, frame);
                    break;
                case FrameKind.RelayOffer:
                    (string region, byte[] key) = ChatFraming.ReadRelayOffer(message, frame);
                    _events.OnRelayOffer(peer.Id, region, key);
                    break;
                case FrameKind.RelayVerdict:
                    _events.OnRelayVerdict(peer.Id, frame.Value);
                    break;
                case FrameKind.RelayKeys:
                    (byte[] messageKey, byte[] voiceKey) = ChatFraming.ReadRelayKeys(message, frame);
                    _events.OnRelayKeys(peer.Id, frame.Member, frame.Tag, messageKey, voiceKey);
                    break;
            }
        }
    }

    private void TakeData(Peer peer, byte[] message, Frame frame)
    {
        if (frame.Offset == 0)
        {
            if (peer.Partial != null)
            {
                _log?.Invoke($"peer {peer.Id}: a game message of {peer.Partial.Length} bytes was cut off");
                peer.Partial = null;
            }
            if (frame.PayloadLength == frame.Total)
            {
                _events.OnData(peer.Id, message.AsSpan(frame.PayloadStart, frame.PayloadLength).ToArray(), frame.Reliable, frame.Channel);
                return;
            }
            if (frame.Total > MaxGameMessageBytes)
            {
                _log?.Invoke($"peer {peer.Id}: game message of {frame.Total} bytes refused");
                return;
            }
            peer.Partial = new byte[frame.Total];
            peer.PartialFilled = 0;
            peer.PartialReliable = frame.Reliable;
            peer.PartialChannel = frame.Channel;
        }
        if (peer.Partial == null || frame.Total != peer.Partial.Length || frame.Offset != peer.PartialFilled)
        {
            _log?.Invoke($"peer {peer.Id}: piece at {frame.Offset} of a {frame.Total} byte game message does not continue one");
            peer.Partial = null;
            return;
        }
        Array.Copy(message, frame.PayloadStart, peer.Partial, peer.PartialFilled, frame.PayloadLength);
        peer.PartialFilled += frame.PayloadLength;
        if (peer.PartialFilled == peer.Partial.Length)
        {
            byte[] whole = peer.Partial;
            peer.Partial = null;
            _events.OnData(peer.Id, whole, peer.PartialReliable, peer.PartialChannel);
        }
    }

    /// <summary>Asks the peer for every gap before its last held-back message.</summary>
    private void AskAgain(Peer peer)
    {
        uint from = peer.NextSeq;
        var asked = new List<string>();
        foreach (uint held in peer.Early.Keys)
        {
            if (held > from)
            {
                uint last = Math.Min(held - 1, from + MaxNackSpan - 1);
                _nacks.Add((peer.Id, peer.Session, from, last));
                asked.Add(from == last ? $"{from}" : $"{from}-{last}");
                if (asked.Count == MaxNackRanges)
                {
                    break;
                }
            }
            from = held + 1;
        }
        _log?.Invoke($"peer {peer.Id}: asking again for lobby messages {string.Join(", ", asked)}");
    }

    private void TakeNacks(byte[] message)
    {
        int position = ChatFraming.HeaderSize;
        while (ChatFraming.TryReadFrame(message, ref position, out Frame frame))
        {
            if (frame.Kind == FrameKind.Nack && frame.Target == _local && frame.NackSession == _session)
            {
                QueueResend(frame.NackFrom, frame.NackTo);
            }
        }
    }

    private void QueueResend(uint first, uint last)
    {
        for (uint seq = first; seq <= last && seq - first < MaxNackSpan; seq++)
        {
            if (_sentBySeq.ContainsKey(seq) && _resendQueued.Add(seq))
            {
                _resend.Enqueue(seq);
            }
        }
    }

    private Peer Follow(ulong sender, uint session, uint nextSeq, long now)
    {
        var peer = new Peer(sender, session, nextSeq, now);
        _peers[sender] = peer;
        return peer;
    }

    private bool IsCurrent(Peer peer) => _peers.TryGetValue(peer.Id, out Peer? current) && current == peer;

    private void Lose(Peer peer, LinkLoss why)
    {
        Forget(peer.Id);
        _events.OnLost(peer.Id, why);
    }
}
