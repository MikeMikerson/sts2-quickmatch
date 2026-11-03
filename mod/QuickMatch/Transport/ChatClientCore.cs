using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using QuickMatch.Lobby;

namespace QuickMatch.Transport;

internal interface IChatClientEvents
{
    /// <summary>The connection ended without us asking (host's Bye, host gone, or lost).</summary>
    void Disconnected(ushort reason);

    void Data(byte[] data, bool reliable, byte channel);
}

/// <summary>
/// Client side of the lobby chat transport: join the lobby, say Hello to its owner until it answers,
/// then exchange game messages with it. Lobby events may arrive on any thread; everything else runs on
/// the caller's thread.
/// </summary>
internal sealed class ChatClientCore : IChatLinkEvents
{
    public const long HelloIntervalMs = 2000;
    /// <summary>The game's own Steam connections give up after 20 seconds.</summary>
    public const long ConnectTimeoutMs = 20000;

    private enum State
    {
        Idle,
        Connecting,
        Connected,
        Closed,
    }

    private readonly ILobbyBackend _backend;
    private readonly IChatClientEvents _events;
    private readonly Func<long> _clock;
    private readonly Action<string>? _log;
    private readonly ChatLink _link;
    private readonly RelayRoute? _route;
    private readonly ConcurrentQueue<LobbyEvent> _inbox = new();
    private readonly Queue<(byte[] Data, bool Reliable, byte Channel)> _early = new();
    private bool _delivering;
    private State _state;
    private ulong _lobby;
    private ushort? _failure;
    private long? _hostLeftAt;

    /// <param name="relay">The relay connection to use where the host offers it and it is faster, or null for lobby chat only.</param>
    public ChatClientCore(ILobbyBackend backend, IChatClientEvents events, Func<long> clock, uint session, Action<string>? log = null,
        IRelayTransport? relay = null)
    {
        _backend = backend;
        _events = events;
        _clock = clock;
        _log = log;
        _link = new ChatLink(backend.LocalId, session, this, log);
        _route = RelayRoute.ForClient(relay, backend.LocalId, clock, log);
        _route?.Attach(_link);
    }

    /// <summary>True when our lobby messages reach the host over the relay only.</summary>
    public bool OnRelay => _route?.AllOnRelay ?? false;

    /// <summary>The relay route, which also carries voice chat; null without a relay connection.</summary>
    public RelayRoute? Route => _route;

    public ulong Host { get; private set; }

    /// <summary>The lobby while joining or connected, else 0.</summary>
    public ulong Lobby => _state is State.Connecting or State.Connected ? _lobby : 0;

    public bool IsConnected => _state == State.Connected;

    public ChatLink Link => _link;

    /// <summary>
    /// Joins the lobby and greets its owner. Returns null once connected, or the reason it failed (the
    /// lobby is left again). <paramref name="nextFrame"/> waits a moment between steps. Throws when
    /// cancelled, after leaving the lobby.
    /// </summary>
    public async Task<ushort?> ConnectAsync(ulong lobby, Func<CancellationToken, Task> nextFrame, CancellationToken ct)
    {
        if (_state != State.Idle)
        {
            throw new InvalidOperationException("a ChatClientCore connects once");
        }
        _lobby = lobby;
        _state = State.Connecting;
        // Subscribed before joining, so nothing the host sends right after is missed.
        _backend.Changed += OnLobbyEvent;
        LobbyJoinResult joined;
        try
        {
            joined = await _backend.JoinAsync(lobby, ct);
        }
        catch
        {
            _backend.Changed -= OnLobbyEvent;
            _state = State.Closed;
            throw;
        }
        if (joined != LobbyJoinResult.Ok)
        {
            _log?.Invoke($"could not enter lobby {lobby}: {joined}");
            _backend.Changed -= OnLobbyEvent;
            _state = State.Closed;
            return ChatReasons.ForJoin(joined);
        }

        Host = _backend.Owner(lobby);
        _link.SendTarget = Host;
        _route?.HostKnown(Host);
        long started = _clock();
        long helloAt = 0;
        bool greeted = false;
        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                long now = _clock();
                if (!greeted || now - helloAt >= HelloIntervalMs)
                {
                    _link.QueueHello(Host, ChatHostCore.Protocol, now);
                    helloAt = now;
                    greeted = true;
                }
                Pump(now);
                if (_state == State.Connected)
                {
                    _link.KeepAlive = true;
                    return null;
                }
                if (_failure is { } reason)
                {
                    _log?.Invoke($"host {Host} refused us: reason {reason}");
                    Close(flush: false);
                    return reason;
                }
                if (now - started >= ConnectTimeoutMs)
                {
                    _log?.Invoke($"host {Host} did not answer in {ConnectTimeoutMs} ms");
                    Close(flush: false);
                    return ChatReasons.Timeout;
                }
                await nextFrame(ct);
            }
        }
        catch (OperationCanceledException)
        {
            if (_state != State.Closed)
            {
                _link.QueueBye(Host, ChatReasons.CancelledJoin, _clock());
                Close(flush: true);
            }
            throw;
        }
    }

    /// <summary>
    /// Hands game messages to the events from now on, first those that came before. The host's game
    /// starts its handshake the moment we connect, so its first message travels with the Welcome; the
    /// game's client reads handshake messages only once it has been told the connection is up, and
    /// takes anything earlier for an unknown message. Call this right after telling it.
    /// </summary>
    public void BeginDelivery()
    {
        _delivering = true;
        while (_state == State.Connected && _early.TryDequeue(out var held))
        {
            _events.Data(held.Data, held.Reliable, held.Channel);
        }
    }

    public void Update()
    {
        if (_state == State.Connected)
        {
            Pump(_clock());
        }
    }

    public void Send(byte[] bytes, int length, bool reliable, byte channel)
    {
        if (_state == State.Connected)
        {
            _link.QueueData(Host, bytes, length, reliable, channel, _clock());
        }
    }

    /// <summary>Tells the host the reason, sends what is still queued and leaves the lobby.</summary>
    public void Disconnect(ushort reason)
    {
        if (_state != State.Connected)
        {
            return;
        }
        _link.QueueBye(Host, reason, _clock());
        Close(flush: true);
    }

    bool IChatLinkEvents.OnHello(ulong sender, ushort protocol) => false;

    bool IChatLinkEvents.OnWelcome(ulong sender)
    {
        if (sender != Host || _state != State.Connecting)
        {
            return false;
        }
        _state = State.Connected;
        return true;
    }

    void IChatLinkEvents.OnReject(ulong sender, ushort reason)
    {
        if (sender == Host && _state == State.Connecting)
        {
            _failure = reason;
        }
    }

    void IChatLinkEvents.OnBye(ulong sender, ushort reason)
    {
        if (sender != Host)
        {
            return;
        }
        if (_state == State.Connecting)
        {
            _failure = reason;
        }
        else if (_state == State.Connected)
        {
            Lose(reason);
        }
    }

    void IChatLinkEvents.OnData(ulong sender, byte[] data, bool reliable, byte channel)
    {
        if (sender != Host || _state != State.Connected)
        {
            return;
        }
        if (_delivering)
        {
            _events.Data(data, reliable, channel);
        }
        else
        {
            _early.Enqueue((data, reliable, channel));
        }
    }

    void IChatLinkEvents.OnLost(ulong peer, LinkLoss why)
    {
        if (peer == Host && _state == State.Connected)
        {
            _log?.Invoke($"host {peer} lost: {why}");
            Lose(ChatReasons.Timeout);
        }
    }

    void IChatLinkEvents.OnRelayOffer(ulong sender, string region, byte[] key)
    {
        if (sender == Host && _state == State.Connected)
        {
            _route?.TakeOffer(sender, region, key);
        }
    }

    void IChatLinkEvents.OnRelayVerdict(ulong sender, ushort verdict)
    {
        if (sender == Host && _state == State.Connected)
        {
            _route?.TakeVerdict(sender, verdict);
        }
    }

    void IChatLinkEvents.OnRelayKeys(ulong sender, ulong member, ulong tag, byte[] messageKey, byte[] voiceKey)
    {
        if (sender == Host && _state == State.Connected)
        {
            _route?.TakeKeys(sender, member, tag, messageKey, voiceKey);
        }
    }

    private void Pump(long now)
    {
        while (_state is State.Connecting or State.Connected && _inbox.TryDequeue(out LobbyEvent e))
        {
            if (e.Kind == LobbyEventKind.Chat && e.Data != null)
            {
                _route?.NoteChat(e.Member, e.Data);
                _link.Receive(e.Member, e.Data, now);
            }
            else if (e.Kind == LobbyEventKind.MemberLeft && e.Member == Host)
            {
                _hostLeftAt ??= now;
            }
        }
        if (_hostLeftAt is { } left && now - left >= ChatHostCore.LeaveGraceMs)
        {
            if (_state == State.Connected)
            {
                _log?.Invoke($"host {Host} left the lobby without a goodbye");
                Lose(ChatReasons.Timeout);
            }
            else if (_state == State.Connecting)
            {
                _failure ??= ChatReasons.InvalidJoin;
            }
        }
        if (_state == State.Connected && _route != null)
        {
            _route.Update((sender, message) => _link.Receive(sender, message, now));
            _link.FastPacing = _route.AllOnRelay;
        }
        if (_state is State.Connecting or State.Connected)
        {
            _link.Tick(now, Send);
        }
    }

    private void Lose(ushort reason)
    {
        Close(flush: false);
        _events.Disconnected(reason);
    }

    private void Close(bool flush)
    {
        if (flush)
        {
            _link.Flush(_clock(), SendFinal);
        }
        _route?.Stop();
        _state = State.Closed;
        _backend.Changed -= OnLobbyEvent;
        _backend.Leave(_lobby);
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
