using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using QuickMatch.Lobby;

namespace QuickMatch.Transport;

internal interface IChatHostEvents
{
    void PeerConnected(ulong peer);

    void PeerDisconnected(ulong peer, ushort reason, bool selfInitiated);

    void Data(ulong peer, byte[] data, bool reliable, byte channel);
}

/// <summary>
/// Host side of the lobby chat transport: a client that is in our lobby and says Hello is connected,
/// the way the game's Steam host accepts a socket from a lobby member. Lobby events may arrive on any
/// thread; everything else runs on the caller's thread through <see cref="Update"/>.
/// </summary>
internal sealed class ChatHostCore : IChatLinkEvents
{
    /// <summary>
    /// Version of the connection protocol; a client with another one is rejected. 3 (QuickMatch 0.3.0):
    /// per-player relay keys. 2 (0.2.0) was relay frames with keys derived from one match key.
    /// </summary>
    public const ushort Protocol = 3;

    /// <summary>A member who leaves without a Bye is dropped after this long, in case the Bye is on its way.</summary>
    public const long LeaveGraceMs = 1500;

    private readonly ILobbyBackend _backend;
    private readonly IChatHostEvents _events;
    private readonly Func<long> _clock;
    private readonly Action<string>? _log;
    private readonly ChatLink _link;
    private readonly RelayRoute? _route;
    private readonly ConcurrentQueue<LobbyEvent> _inbox = new();
    private readonly List<ulong> _peers = new();
    private readonly Dictionary<ulong, long> _leftAt = new();
    private ulong _lobby;
    private bool _running;

    /// <param name="relay">The relay connection to use where it is faster than lobby chat, or null for lobby chat only.</param>
    public ChatHostCore(ILobbyBackend backend, IChatHostEvents events, Func<long> clock, uint session, Action<string>? log = null,
        IRelayTransport? relay = null)
    {
        _backend = backend;
        _events = events;
        _clock = clock;
        _log = log;
        _link = new ChatLink(backend.LocalId, session, this, log);
        _route = RelayRoute.ForHost(relay, backend.LocalId, clock, log);
        _route?.Attach(_link);
    }

    /// <summary>True when every client takes the host's lobby messages over the relay only.</summary>
    public bool AllOnRelay => _route?.AllOnRelay ?? false;

    /// <summary>The relay route, which also carries voice chat; null without a relay connection.</summary>
    public RelayRoute? Route => _route;

    public ulong Lobby => _lobby;

    public bool IsRunning => _running;

    public IReadOnlyList<ulong> Peers => _peers;

    public ChatLink Link => _link;

    /// <summary>Starts serving a lobby we created and are in.</summary>
    public void Start(ulong lobby)
    {
        _lobby = lobby;
        _running = true;
        _backend.Changed += OnLobbyEvent;
    }

    public void Update()
    {
        if (!_running)
        {
            return;
        }
        long now = _clock();
        while (_running && _inbox.TryDequeue(out LobbyEvent e))
        {
            if (e.Kind == LobbyEventKind.Chat && e.Data != null)
            {
                _route?.NoteChat(e.Member, e.Data);
                _link.Receive(e.Member, e.Data, now);
            }
            else if (e.Kind == LobbyEventKind.MemberLeft && _peers.Contains(e.Member))
            {
                _leftAt.TryAdd(e.Member, now);
            }
        }
        foreach ((ulong peer, long at) in _leftAt.ToList())
        {
            if (now - at >= LeaveGraceMs)
            {
                _log?.Invoke($"client {peer} left the lobby without a goodbye");
                Drop(peer, ChatReasons.Timeout, selfInitiated: false);
            }
        }
        if (_running && _route != null)
        {
            _route.Update((sender, message) => _link.Receive(sender, message, now));
            _link.FastPacing = _route.AllOnRelay;
        }
        if (_running)
        {
            // Heartbeats only while someone listens; an open lobby waiting for players stays quiet.
            _link.KeepAlive = _peers.Count > 0;
            _link.Tick(now, Send);
        }
    }

    public void Send(ulong peer, byte[] bytes, int length, bool reliable, byte channel)
    {
        _link.QueueData(peer, bytes, length, reliable, channel, _clock());
    }

    /// <summary>Ends one client's connection; the client hears the reason.</summary>
    public void Disconnect(ulong peer, ushort reason)
    {
        if (!_peers.Contains(peer))
        {
            return;
        }
        _link.QueueBye(peer, reason, _clock());
        // At once and over lobby chat too: the client may be leaving the relay room as well.
        _link.Flush(_clock(), SendFinal);
        Drop(peer, reason, selfInitiated: true);
    }

    /// <summary>Tells every client the reason, sends what is still queued and leaves the lobby.</summary>
    public void Stop(ushort reason)
    {
        if (!_running)
        {
            return;
        }
        long now = _clock();
        _link.QueueBye(0, reason, now);
        _link.Flush(now, SendFinal);
        _route?.Stop();
        _running = false;
        _backend.Changed -= OnLobbyEvent;
        _backend.Leave(_lobby);
        _peers.Clear();
        _leftAt.Clear();
    }

    bool IChatLinkEvents.OnHello(ulong sender, ushort protocol)
    {
        if (protocol != Protocol)
        {
            _log?.Invoke($"client {sender} speaks protocol {protocol}, we speak {Protocol}");
            _link.QueueReject(sender, ChatReasons.VersionMismatch, _clock());
            return false;
        }
        if (_peers.Contains(sender))
        {
            // Its Hello crossed our Welcome, or the Welcome was lost: say it again.
            _link.QueueWelcome(sender, _clock());
            return true;
        }
        if (!_backend.Members(_lobby).Contains(sender))
        {
            // Lobby chat only comes from members, so the member list is behind; it asks again.
            _log?.Invoke($"Hello from {sender}, who is not yet listed in lobby {_lobby}");
            return false;
        }
        _peers.Add(sender);
        _leftAt.Remove(sender);
        _link.QueueWelcome(sender, _clock());
        // After the Welcome, so a relay offer for a room already open travels behind it.
        _route?.PeerConnected(sender);
        _events.PeerConnected(sender);
        return true;
    }

    bool IChatLinkEvents.OnWelcome(ulong sender) => false;

    void IChatLinkEvents.OnReject(ulong sender, ushort reason)
    {
    }

    void IChatLinkEvents.OnBye(ulong sender, ushort reason)
    {
        if (_peers.Contains(sender))
        {
            Drop(sender, reason, selfInitiated: false);
        }
    }

    void IChatLinkEvents.OnData(ulong sender, byte[] data, bool reliable, byte channel)
    {
        if (_peers.Contains(sender))
        {
            _events.Data(sender, data, reliable, channel);
        }
    }

    void IChatLinkEvents.OnLost(ulong peer, LinkLoss why)
    {
        if (_peers.Contains(peer))
        {
            _log?.Invoke($"client {peer} lost: {why}");
            Drop(peer, ChatReasons.Timeout, selfInitiated: false);
        }
    }

    void IChatLinkEvents.OnRelayOffer(ulong sender, string region, byte[] key)
    {
    }

    void IChatLinkEvents.OnRelayVerdict(ulong sender, ushort verdict)
    {
        if (_peers.Contains(sender))
        {
            _route?.TakeVerdict(sender, verdict);
        }
    }

    void IChatLinkEvents.OnRelayKeys(ulong sender, ulong member, ulong tag, byte[] messageKey, byte[] voiceKey)
    {
        if (_peers.Contains(sender))
        {
            _route?.TakeKeys(sender, member, tag, messageKey, voiceKey);
        }
    }

    private void Drop(ulong peer, ushort reason, bool selfInitiated)
    {
        _peers.Remove(peer);
        _leftAt.Remove(peer);
        _link.Forget(peer);
        _route?.PeerGone(peer);
        _events.PeerDisconnected(peer, reason, selfInitiated);
    }

    private bool Send(byte[] message, int length, ulong target) =>
        _route?.Send(message, length, target, SendChat) ?? SendChat(message, length, target);

    private bool SendFinal(byte[] message, int length, ulong target) =>
        _route?.SendFinal(message, length, target, SendChat) ?? SendChat(message, length, target);

    private bool SendChat(byte[] message, int length, ulong target) => _backend.SendChat(_lobby, message, length, target);

    private void OnLobbyEvent(LobbyEvent e)
    {
        if (e.Lobby == _lobby)
        {
            _inbox.Enqueue(e);
        }
    }
}
