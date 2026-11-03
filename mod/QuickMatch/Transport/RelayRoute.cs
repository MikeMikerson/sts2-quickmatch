using System;
using System.Collections.Generic;
using System.Linq;
using QuickMatch.Voice;

namespace QuickMatch.Transport;

/// <summary>What a receiver tells a sender about the sender's relay copies.</summary>
internal static class RelayVerdict
{
    /// <summary>Nothing has come over the relay for a while: send over lobby chat again.</summary>
    public const ushort Stale = 0;
    /// <summary>Relay copies arrive first: the lobby chat copies can stop.</summary>
    public const ushort RelayFirst = 1;
    /// <summary>Lobby chat copies arrive first: the relay copies can stop.</summary>
    public const ushort ChatFirst = 2;
}

/// <summary>
/// Carries one side of a lobby chat link over the QuickMatch relay where that is faster, and never
/// makes it slower. Lobby messages keep their lobby chat form, sequence numbers included; the relay
/// gets an encrypted copy (<see cref="RelayCrypto"/>) and the receiving <see cref="ChatLink"/> drops
/// whichever copy comes second. Each receiver counts which copy of a sender's messages came first and
/// tells the sender (<see cref="RelayVerdict"/>): a sender stops its lobby chat copies to a peer only
/// once that peer reports the relay first, and stops its relay copies if the peer reports lobby chat
/// first. A relay that goes quiet for <see cref="StaleMs"/>, a member who leaves the room, or a closed
/// connection all put the peer back on lobby chat, and anything lost meanwhile is asked for again by
/// the link from the sender's resend buffer.
///
/// The host makes a room key when its first client connects, joins the relay (region chosen by the
/// relay from where the host is) and offers region and room key to each client over the link. Lobby
/// messages of one sender reach every peer that follows it, whatever their frames' targets, so the
/// choice of channel depends on the peers' states only. Runs on the game's thread.
///
/// Who sent what: every player makes its own random lobby message key, voice key and relay secret
/// for the match, and announces the keys with its relay tag (<see cref="RelaySession"/>) in a
/// RelayKeys frame over the link: the host with each offer, a client when it takes the offer. Steam
/// delivers lobby chat with the real sender, so a receiver binds those keys to that Steam ID. The host
/// passes each client's voice key and tag on to the other clients, since voice goes between all of
/// them. A relay copy or voice packet is taken only from a match peer whose keys are bound, whose
/// connection in the room carries the announced tag, and only if it opens with that peer's key: a
/// member cannot pass anything off as another's, because it has neither the other's relay secret
/// (so not its tag) nor, for messages, a key the receiver would accept from its own ID. The room key
/// only finds the room.
///
/// Voice chat rides the same room (<see cref="SendVoice"/>): every member sends its voice packets to
/// all the others directly, sealed with <see cref="VoiceCrypto"/> under its own voice key, and they
/// never touch the link or lobby chat.
/// </summary>
internal sealed class RelayRoute : IDisposable
{
    /// <summary>A peer told "relay first" that sends nothing over the relay for this long is back on lobby chat.</summary>
    public const long StaleMs = 5000;
    /// <summary>Messages that came both ways before a verdict is given.</summary>
    public const int VerdictPairs = 8;
    public const long ReconnectDelayMs = 3000;
    /// <summary>Connections tried per match: the first and three more after losing one.</summary>
    public const int MaxConnects = 4;

    private const int ArrivalMemory = 256;
    /// <summary>Relay copies held per peer while its keys are on their way over lobby chat.</summary>
    private const int MaxUnchecked = 64;

    /// <summary>A member's announced relay tag and keys.</summary>
    private sealed class MemberKeys(ulong tag, byte[]? messageKey, byte[] voiceKey) : IDisposable
    {
        public readonly ulong Tag = tag;
        public readonly RelayCrypto? Messages = messageKey == null ? null : new RelayCrypto(messageKey);
        public readonly byte[] VoiceKey = voiceKey;
        public readonly VoiceCrypto Voice = new(voiceKey);

        public void Dispose()
        {
            Messages?.Dispose();
            Voice.Dispose();
        }
    }

    private static readonly byte[] NoKey = new byte[RelayCrypto.KeyBytes];

    private sealed class PeerState
    {
        /// <summary>
        /// Its ID is in the relay room, so our relay copies reach it if the connection is really its
        /// own; what comes from that ID is taken only from a connection carrying its announced tag.
        /// </summary>
        public bool OnRelay;
        /// <summary>Its verdict: our relay copies reach it first, so it gets no lobby chat copies.</summary>
        public bool Confirmed;
        /// <summary>Its verdict: our lobby chat copies reach it first, so it gets no relay copies.</summary>
        public bool Slower;
        public bool Offered;
        public long LastRelayAt;
        /// <summary>What we told it about its relay copies, if anything yet.</summary>
        public ushort? VerdictSent;
        public uint ArrivalSession;
        public uint ArrivalMax;
        public readonly Dictionary<uint, bool> FirstViaRelay = new();
        public int RelayFirst;
        public int ChatFirst;
        /// <summary>
        /// Relay copies that came before the peer's keys, with the tag its connection had then; checked
        /// and taken once the keys arrive, so its first messages over the relay are not lost.
        /// </summary>
        public readonly Queue<(ulong Tag, byte[] Payload)> Unchecked = new();

        public void ResetArrivals()
        {
            VerdictSent = null;
            FirstViaRelay.Clear();
            RelayFirst = 0;
            ChatFirst = 0;
        }
    }

    private readonly IRelayTransport _transport;
    private readonly ulong _local;
    private readonly bool _isHost;
    private readonly Func<long> _clock;
    private readonly Action<string>? _log;
    private readonly Dictionary<ulong, PeerState> _peers = new();
    /// <summary>Members whose keys we hold: our peers on the link and, on a client, the host's other clients (voice only).</summary>
    private readonly Dictionary<ulong, MemberKeys> _keys = new();
    private readonly byte[] _messageKey = RelayCrypto.NewKey();
    private readonly byte[] _voiceKey = VoiceCrypto.NewKey();
    private readonly byte[] _secret = RelaySession.NewSecret();
    private readonly ulong _tag;
    private readonly RelayCrypto _crypto;
    private readonly VoiceCrypto _voiceCrypto;
    private ChatLink? _link;
    /// <summary>The host's room key: made by the host, taken by a client from the host's offer.</summary>
    private byte[]? _roomKey;
    private string _region = "auto";
    private ulong _host;
    private RelaySession? _session;
    private int _connects;
    private long? _reconnectAt;
    /// <summary>A peer was in the room during this connection; a connection no peer used is not renewed.</summary>
    private bool _sessionUsed;
    private bool _stopped;
    private bool _reportedBadMessage;
    private bool _reportedBadVoice;
    private bool _reportedWrongTag;
    private int _voiceSent;
    private int _voiceReceived;
    private int _relaySent;
    private int _relayReceived;
    private int _chatSent;
    private int _chatSkipped;

    private RelayRoute(IRelayTransport transport, ulong local, bool isHost, Func<long> clock, Action<string>? log)
    {
        _transport = transport;
        _local = local;
        _isHost = isHost;
        _clock = clock;
        _log = log;
        _tag = RelaySession.TagOf(_secret);
        _crypto = new RelayCrypto(_messageKey);
        _voiceCrypto = new VoiceCrypto(_voiceKey);
    }

    /// <summary>A host's route, or null without a relay connection or AES-GCM (the match stays on lobby chat).</summary>
    public static RelayRoute? ForHost(IRelayTransport? transport, ulong local, Func<long> clock, Action<string>? log)
    {
        if (transport == null || !RelayCrypto.IsSupported)
        {
            return null;
        }
        return new RelayRoute(transport, local, isHost: true, clock, log) { _roomKey = RelayCrypto.NewKey() };
    }

    /// <summary>A client's route, or null without a relay connection or AES-GCM.</summary>
    public static RelayRoute? ForClient(IRelayTransport? transport, ulong local, Func<long> clock, Action<string>? log) =>
        transport == null || !RelayCrypto.IsSupported ? null : new RelayRoute(transport, local, isHost: false, clock, log);

    /// <summary>True when every peer gets relay copies only, so the link may send at the relay's pace.</summary>
    public bool AllOnRelay => _session is { IsJoined: true } && _peers.Count > 0 && _peers.Values.All(p => p.OnRelay && p.Confirmed);

    public void Attach(ChatLink link) => _link = link;

    /// <summary>Voice packets from the other members of the match, checked and decrypted; called during <see cref="Update"/>.</summary>
    public Action<ulong, VoicePacket>? VoiceReceived { get; set; }

    /// <summary>True while voice can travel: in the relay room with at least one other member of the match.</summary>
    public bool VoiceUp => !_stopped && _session is { IsJoined: true } session && session.Members.Any(InRoom);

    /// <summary>
    /// The other members of the match in the relay room, on connections carrying their announced tags:
    /// they hear our voice, and theirs is taken.
    /// </summary>
    public IReadOnlyCollection<ulong> VoiceMembers =>
        !_stopped && _session is { IsJoined: true } session ? session.Members.Where(InRoom).ToList() : Array.Empty<ulong>();

    /// <summary>Sends a voice packet to every other member of the room; dropped while voice cannot travel.</summary>
    public void SendVoice(VoicePacket packet)
    {
        if (!VoiceUp)
        {
            return;
        }
        _session!.Send(0, _voiceCrypto.Seal(packet));
        _voiceSent++;
    }

    /// <summary>Host: a client connected. The first one starts the relay connection.</summary>
    public void PeerConnected(ulong peer)
    {
        if (_stopped || !_isHost)
        {
            return;
        }
        var state = new PeerState();
        _peers[peer] = state;
        if (_session == null && _reconnectAt == null)
        {
            Connect();
        }
        else if (_session is { IsJoined: true } session)
        {
            state.OnRelay = session.Members.Contains(peer);
            _sessionUsed |= state.OnRelay;
            Offer(peer, state);
        }
    }

    /// <summary>Client: the host we greet. Its relay offer is taken only from it.</summary>
    public void HostKnown(ulong host)
    {
        _host = host;
        _peers[host] = new PeerState();
    }

    /// <summary>A peer's connection ended. A host tells its other clients to stop taking that member's voice, and leaves the relay without clients.</summary>
    public void PeerGone(ulong peer)
    {
        if (!_peers.Remove(peer))
        {
            return;
        }
        if (_keys.Remove(peer, out MemberKeys? keys))
        {
            keys.Dispose();
            if (_isHost && _link != null)
            {
                foreach (ulong other in _peers.Keys.Where(_keys.ContainsKey))
                {
                    _link.QueueRelayKeys(other, peer, 0, NoKey, NoKey, _clock());
                }
            }
        }
        if (_isHost && _peers.Count == 0)
        {
            Disconnect();
            _reconnectAt = null;
        }
    }

    /// <summary>Client: the host's offer of region and room key. Our keys go to the host before we join.</summary>
    public void TakeOffer(ulong sender, string region, byte[] key)
    {
        if (_stopped || _isHost || sender != _host || _roomKey != null || !IsRegionName(region) || key.Length != RelayCrypto.KeyBytes)
        {
            return;
        }
        _roomKey = key;
        _region = region;
        _link?.QueueRelayKeys(_host, _local, _tag, _messageKey, _voiceKey, _clock());
        Connect();
    }

    /// <summary>
    /// A peer's RelayKeys frame. A host takes only a client's own keys, and passes its voice key on to
    /// the other clients; a client takes the host's own keys, and other members' voice keys from the
    /// host (tag 0: that member left the match).
    /// </summary>
    public void TakeKeys(ulong sender, ulong member, ulong tag, byte[] messageKey, byte[] voiceKey)
    {
        if (_stopped || member == _local || member == 0 || !_peers.ContainsKey(sender))
        {
            return;
        }
        if (_isHost)
        {
            if (member != sender || tag == 0)
            {
                return;
            }
            Bind(member, new MemberKeys(tag, messageKey, voiceKey));
            if (_link != null)
            {
                long now = _clock();
                foreach ((ulong other, MemberKeys keys) in _keys)
                {
                    if (other != member && _peers.ContainsKey(other))
                    {
                        _link.QueueRelayKeys(other, member, tag, NoKey, voiceKey, now);
                        _link.QueueRelayKeys(member, other, keys.Tag, NoKey, keys.VoiceKey, now);
                    }
                }
            }
        }
        else if (sender != _host)
        {
            return;
        }
        else if (member == _host)
        {
            if (tag != 0)
            {
                Bind(member, new MemberKeys(tag, messageKey, voiceKey));
            }
        }
        else if (tag == 0)
        {
            if (_keys.Remove(member, out MemberKeys? gone))
            {
                gone.Dispose();
            }
        }
        else
        {
            Bind(member, new MemberKeys(tag, null, voiceKey));
        }
    }

    public void TakeVerdict(ulong sender, ushort verdict)
    {
        if (!_peers.TryGetValue(sender, out PeerState? peer))
        {
            return;
        }
        switch (verdict)
        {
            case RelayVerdict.RelayFirst:
                if (!peer.Confirmed)
                {
                    _log?.Invoke($"fast link up via relay for {sender}");
                }
                peer.Confirmed = true;
                peer.Slower = false;
                break;
            case RelayVerdict.ChatFirst:
                _log?.Invoke($"relay slower than lobby chat for {sender}; staying on lobby chat");
                peer.Confirmed = false;
                peer.Slower = true;
                break;
            case RelayVerdict.Stale:
                if (peer.Confirmed)
                {
                    _log?.Invoke($"{sender} hears nothing from us over the relay; back to lobby chat");
                }
                peer.Confirmed = false;
                peer.Slower = false;
                break;
        }
    }

    /// <summary>Notes a lobby chat message from a peer, to compare it with its relay copy.</summary>
    public void NoteChat(ulong sender, byte[] message)
    {
        if (_peers.TryGetValue(sender, out PeerState? peer))
        {
            NoteArrival(sender, peer, message, viaRelay: false);
        }
    }

    /// <summary>Handles the relay connection; lobby messages it carried go to <paramref name="receive"/>.</summary>
    public void Update(Action<ulong, byte[]> receive)
    {
        if (_stopped)
        {
            return;
        }
        long now = _clock();
        if (_session == null && _reconnectAt is { } at && now >= at)
        {
            _reconnectAt = null;
            Connect();
        }
        // A message handed on may end a peer's connection (a Bye), or this route.
        foreach (ulong id in _peers.Keys.ToList())
        {
            if (_peers.TryGetValue(id, out PeerState? peer) && peer.Unchecked.Count > 0
                && _keys.TryGetValue(id, out MemberKeys? keys) && keys.Messages != null)
            {
                while (!_stopped && _peers.ContainsKey(id) && peer.Unchecked.TryDequeue(out var held))
                {
                    Accept(id, peer, held.Tag == keys.Tag ? keys.Messages.Open(held.Payload) : null, now, receive);
                }
            }
        }
        if (_stopped)
        {
            return;
        }
        if (_session is { } session)
        {
            foreach (RelayEvent e in session.Poll(now))
            {
                // A message handed on may end the connection (a Bye) and with it this route.
                if (_stopped || _session != session)
                {
                    break;
                }
                Handle(e, now, receive);
            }
        }
        if (_stopped)
        {
            return;
        }
        foreach ((ulong id, PeerState peer) in _peers)
        {
            if (peer.VerdictSent == RelayVerdict.RelayFirst && now - peer.LastRelayAt > StaleMs)
            {
                // Assume the relay fails both ways: our own copies go over lobby chat again too, so
                // this verdict reaches the peer.
                _log?.Invoke($"nothing from {id} over the relay for {now - peer.LastRelayAt} ms; back to lobby chat");
                peer.ResetArrivals();
                peer.Confirmed = false;
                _link?.QueueRelayVerdict(id, RelayVerdict.Stale, now);
            }
        }
    }

    /// <summary>
    /// Sends one lobby message: over lobby chat unless every peer takes relay copies first, and over the
    /// relay to the peers in the room that are not faster on lobby chat.
    /// </summary>
    public bool Send(byte[] message, int length, ulong lobbyTarget, ChatSend chat)
    {
        bool relayUp = _session is { IsJoined: true };
        bool ok = true;
        if (!relayUp || _peers.Count == 0 || _peers.Values.Any(p => !(p.OnRelay && p.Confirmed)))
        {
            ok = chat(message, length, lobbyTarget);
            _chatSent++;
        }
        else
        {
            _chatSkipped++;
        }
        if (relayUp && _peers.Values.Any(p => p.OnRelay && !p.Slower))
        {
            byte[] sealedMessage = _crypto.Seal(message, length);
            if (sealedMessage.Length <= RelaySession.MaxPayloadBytes)
            {
                _session!.Send(_isHost ? 0 : _host, sealedMessage);
                _relaySent++;
            }
        }
        return ok;
    }

    /// <summary>
    /// Sends a last lobby message (a Bye) over lobby chat and, where it is up, the relay, whatever the
    /// verdicts: the relay copy may not leave before the connection closes.
    /// </summary>
    public bool SendFinal(byte[] message, int length, ulong lobbyTarget, ChatSend chat)
    {
        if (_session is { IsJoined: true } && _peers.Values.Any(p => p.OnRelay))
        {
            byte[] sealedMessage = _crypto.Seal(message, length);
            if (sealedMessage.Length <= RelaySession.MaxPayloadBytes)
            {
                _session.Send(_isHost ? 0 : _host, sealedMessage);
                _relaySent++;
            }
        }
        _chatSent++;
        return chat(message, length, lobbyTarget);
    }

    /// <summary>Leaves the relay for good and logs what went where.</summary>
    public void Stop()
    {
        if (_stopped)
        {
            return;
        }
        _stopped = true;
        if (_connects > 0)
        {
            _log?.Invoke($"relay: {_relaySent} lobby messages sent and {_relayReceived} received over the relay; " +
                $"{_chatSent} sent over lobby chat, {_chatSkipped} lobby chat copies left out; " +
                $"{_voiceSent} voice packets sent, {_voiceReceived} received");
        }
        Disconnect();
        _crypto.Dispose();
        _voiceCrypto.Dispose();
        foreach (MemberKeys keys in _keys.Values)
        {
            keys.Dispose();
        }
        _keys.Clear();
    }

    public void Dispose() => Stop();

    private void Connect()
    {
        if (_connects >= MaxConnects || _roomKey == null)
        {
            return;
        }
        _connects++;
        _sessionUsed = false;
        _session = new RelaySession(_transport, _region, RelayCrypto.RoomToken(_roomKey), _local, _clock(), _secret);
    }

    private void Disconnect()
    {
        _session?.Dispose();
        _session = null;
        foreach (PeerState peer in _peers.Values)
        {
            Unrelay(peer);
            peer.Unchecked.Clear();
        }
    }

    private void Handle(RelayEvent e, long now, Action<ulong, byte[]> receive)
    {
        switch (e.Kind)
        {
            case RelayEventKind.Joined:
                // A reconnect after this keeps the room the clients were offered.
                _region = _session!.Region;
                _log?.Invoke($"relay: in the room (region {_region}), {_session.Members.Count} other members there");
                foreach ((ulong id, PeerState peer) in _peers)
                {
                    peer.OnRelay = _session.Members.Contains(id);
                    _sessionUsed |= peer.OnRelay;
                    if (_isHost && !peer.Offered)
                    {
                        Offer(id, peer);
                    }
                }
                foreach (ulong member in _session.Members)
                {
                    ReportWrongTag(member);
                }
                break;
            case RelayEventKind.MemberJoined when _peers.TryGetValue(e.Member, out PeerState? joined):
                Unrelay(joined);
                joined.OnRelay = true;
                _sessionUsed = true;
                ReportWrongTag(e.Member, e.Tag);
                break;
            case RelayEventKind.MemberJoined:
                ReportWrongTag(e.Member, e.Tag);
                break;
            case RelayEventKind.MemberLeft when _peers.TryGetValue(e.Member, out PeerState? left):
                if (left.Confirmed)
                {
                    _log?.Invoke($"fast link lost for {e.Member}: it left the relay; back to lobby chat");
                }
                Unrelay(left);
                break;
            case RelayEventKind.Data when VoiceCrypto.IsVoice(e.Payload!):
                ReceiveVoice(e.Member, e.Tag, e.Payload!);
                break;
            case RelayEventKind.Data:
                Receive(e.Member, e.Tag, e.Payload!, now, receive);
                break;
            case RelayEventKind.Closed:
                _log?.Invoke($"relay connection closed: {e.Reason}; lobby chat carries the match");
                _session = null;
                foreach (PeerState peer in _peers.Values)
                {
                    Unrelay(peer);
                    peer.Unchecked.Clear();
                }
                // Renewed after a failure to connect, or when peers were using it; a connection that sat
                // unused until the relay closed it for idling is not.
                if (_connects < MaxConnects && _peers.Count > 0 && (_sessionUsed || !e.WasJoined))
                {
                    _reconnectAt = now + ReconnectDelayMs;
                }
                break;
        }
    }

    /// <param name="tag">The tag of the sender's connection when the relay forwarded the payload (0 if it was not in the room).</param>
    private void Receive(ulong sender, ulong tag, byte[] payload, long now, Action<ulong, byte[]> receive)
    {
        if (!_peers.TryGetValue(sender, out PeerState? peer))
        {
            return;
        }
        if (!_keys.TryGetValue(sender, out MemberKeys? keys) || keys.Messages == null)
        {
            // Its keys are still on their way over lobby chat.
            if (peer.Unchecked.Count < MaxUnchecked)
            {
                peer.Unchecked.Enqueue((tag, payload));
            }
            return;
        }
        Accept(sender, peer, tag == keys.Tag ? keys.Messages.Open(payload) : null, now, receive);
    }

    /// <summary>Takes a peer's opened relay copy, or reports once that one did not open (null).</summary>
    private void Accept(ulong sender, PeerState peer, byte[]? message, long now, Action<ulong, byte[]> receive)
    {
        if (message == null)
        {
            if (!_reportedBadMessage)
            {
                _log?.Invoke($"relay: dropped a message from {sender} that did not come from the connection and key it announced");
                _reportedBadMessage = true;
            }
            return;
        }
        _relayReceived++;
        peer.LastRelayAt = now;
        NoteArrival(sender, peer, message, viaRelay: true);
        receive(sender, message);
    }

    private void ReceiveVoice(ulong sender, ulong tag, byte[] payload)
    {
        VoicePacket? packet = _keys.TryGetValue(sender, out MemberKeys? keys) && tag == keys.Tag ? keys.Voice.Open(payload) : null;
        if (packet == null)
        {
            if (!_reportedBadVoice)
            {
                _log?.Invoke($"relay: dropped a voice packet from {sender}, who is not a member of this match or did not seal it with the key it announced");
                _reportedBadVoice = true;
            }
            return;
        }
        _voiceReceived++;
        VoiceReceived?.Invoke(sender, packet);
    }

    private void Offer(ulong id, PeerState peer)
    {
        if (_link == null || _roomKey == null || !IsRegionName(_region))
        {
            return;
        }
        long now = _clock();
        _link.QueueRelayKeys(id, _local, _tag, _messageKey, _voiceKey, now);
        _link.QueueRelayOffer(id, _region, _roomKey, now);
        peer.Offered = true;
    }

    private void Bind(ulong member, MemberKeys keys)
    {
        if (_keys.Remove(member, out MemberKeys? old))
        {
            old.Dispose();
        }
        _keys[member] = keys;
        ReportWrongTag(member);
    }

    private bool TagMatches(ulong member, MemberKeys keys) =>
        _session is { IsJoined: true } session && session.TryGetTag(member, out ulong tag) && tag == keys.Tag;

    /// <summary>A member whose keys we hold is in the room on a connection carrying its announced tag.</summary>
    private bool InRoom(ulong member) => _keys.TryGetValue(member, out MemberKeys? keys) && TagMatches(member, keys);

    /// <summary>Logs once per match a member of the match whose ID is in the room on a connection that is not its own.</summary>
    private void ReportWrongTag(ulong member, ulong? tagNow = null)
    {
        if (_reportedWrongTag || _session is not { IsJoined: true } session || !_keys.TryGetValue(member, out MemberKeys? keys))
        {
            return;
        }
        ulong tag = tagNow ?? (session.TryGetTag(member, out ulong current) ? current : 0);
        if (tag != 0 && tag != keys.Tag)
        {
            _log?.Invoke($"relay: {member} is in the room on a connection that is not the one it announced; ignoring it");
            _reportedWrongTag = true;
        }
    }

    /// <summary>Counts which copy of a sender's lobby message came first, and gives the verdict once enough did.</summary>
    private void NoteArrival(ulong sender, PeerState peer, byte[] message, bool viaRelay)
    {
        if (peer.VerdictSent != null || !ChatFraming.TryReadHeader(message, out uint session, out uint seq))
        {
            return;
        }
        if (session != peer.ArrivalSession)
        {
            peer.ResetArrivals();
            peer.ArrivalSession = session;
            peer.ArrivalMax = 0;
        }
        if (peer.FirstViaRelay.Remove(seq, out bool firstViaRelay))
        {
            if (firstViaRelay == viaRelay)
            {
                // A resend on the same channel: wait for the other copy again.
                peer.FirstViaRelay[seq] = firstViaRelay;
                return;
            }
            if (firstViaRelay)
            {
                peer.RelayFirst++;
            }
            else
            {
                peer.ChatFirst++;
            }
        }
        else
        {
            peer.FirstViaRelay[seq] = viaRelay;
            peer.ArrivalMax = Math.Max(peer.ArrivalMax, seq);
            if (peer.FirstViaRelay.Count > 2 * ArrivalMemory)
            {
                foreach (uint old in peer.FirstViaRelay.Keys.Where(k => k + ArrivalMemory < peer.ArrivalMax).ToList())
                {
                    peer.FirstViaRelay.Remove(old);
                }
            }
        }
        int pairs = peer.RelayFirst + peer.ChatFirst;
        if (pairs >= VerdictPairs)
        {
            ushort verdict = peer.RelayFirst * 4 >= pairs * 3 ? RelayVerdict.RelayFirst : RelayVerdict.ChatFirst;
            long now = _clock();
            peer.VerdictSent = verdict;
            peer.LastRelayAt = now;
            _link?.QueueRelayVerdict(sender, verdict, now);
            _log?.Invoke($"relay copies from {sender} came first {peer.RelayFirst} of {pairs} times");
        }
    }

    private static void Unrelay(PeerState peer)
    {
        peer.OnRelay = false;
        peer.Confirmed = false;
        peer.Slower = false;
        peer.ResetArrivals();
    }

    private static bool IsRegionName(string region) =>
        region.Length is > 0 and <= ChatFraming.MaxRegionLength && region.All(c => c is >= 'a' and <= 'z');
}
