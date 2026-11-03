using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Transport;
using MegaCrit.Sts2.Core.Multiplayer.Transport.Steam;
using MegaCrit.Sts2.Core.TestSupport;
using QuickMatch.Lobby;
using QuickMatch.Steam;

namespace QuickMatch.Transport;

/// <summary>
/// The desktop host: the game's own Steam sockets host, exactly as in vanilla co-op, for players who
/// have Steamworks, plus lobby chat on the same Steam lobby for players who do not (phones). The game
/// sees one host; <see cref="MixedRoutes"/> keeps each client on the connection it came over. Started
/// through NetHostGameService.StartTestHost like <see cref="ChatHost"/>, and with the same lobby rules
/// (QuickMatchLobby.SetClosed and OnHostStopping in place of SteamHost's own).
/// </summary>
internal sealed class MixedHost : AbstractTestNetHost, INetHostHandler, IChatHostEvents
{
    private readonly SteamHost _sockets;
    private readonly ChatHostCore _chat;
    private readonly MixedRoutes _routes;
    private bool _stopping;
    private bool _reportedStop;

    public MixedHost(INetHostHandler handler, ILobbyBackend backend)
        : base(handler)
    {
        _sockets = new SteamHost(this);
        _chat = new ChatHostCore(backend, this, ChatClock.Now, ChatClock.NewSession(), message => MainFile.Logger.Info($"chat host: {message}"),
            RelayTransport.Current);
        _routes = new MixedRoutes(new SocketsAdapter(_sockets), _chat);
    }

    public ulong? LobbyId { get; private set; }

    public override bool IsConnected => _sockets.IsConnected;

    public override ulong NetId => _sockets.NetId;

    public override IEnumerable<ulong> ConnectedPeerIds => _routes.Peers;

    public override async Task<NetErrorInfo?> StartHost(int maxClients)
    {
        MainFile.Logger.Info($"starting host for {maxClients} over Steam sockets and lobby chat, our id {NetId}");
        NetErrorInfo? error = await _sockets.StartHost(maxClients);
        if (error.HasValue)
        {
            return error;
        }
        if (_sockets.LobbyId is not { } lobby)
        {
            return new NetErrorInfo(NetError.FailedToHost, selfInitiated: true);
        }
        LobbyId = lobby.m_SteamID;
        _chat.Start(lobby.m_SteamID);
        return null;
    }

    public override void Update()
    {
        _routes.Update();
        if (!_stopping && _chat.IsRunning)
        {
            // Voice travels only through the relay room, which phones (and QuickMatchRelay users) join.
            Voice.VoiceChat.Tick(_chat.Route);
        }
    }

    public override void SetHostIsClosed(bool isClosed)
    {
        // Not SteamHost's own: it would reopen the lobby as friends only.
        if (LobbyId is { } lobby)
        {
            QuickMatchLobby.SetClosed(lobby, isClosed);
        }
    }

    public override void SendMessageToClient(ulong peerId, byte[] bytes, int length, NetTransferMode mode, int channel = 0)
    {
        if (!_routes.Has(peerId))
        {
            throw new InvalidOperationException($"Could not find connection for peer {peerId}!");
        }
        ChatTiming.Note("sent", bytes, length);
        _routes.Send(peerId, bytes, length, mode == NetTransferMode.Reliable, channel);
    }

    public override void SendMessageToAll(byte[] bytes, int length, NetTransferMode mode, int channel = 0)
    {
        ChatTiming.Note("sent", bytes, length);
        _routes.SendAll(bytes, length, mode == NetTransferMode.Reliable, channel);
    }

    public override void DisconnectClient(ulong peerId, NetError reason, bool now = false)
    {
        _routes.Disconnect(peerId, (ushort)reason, now);
    }

    public override void StopHost(NetError reason, bool now = false)
    {
        if (LobbyId is { } lobby)
        {
            QuickMatchLobby.OnHostStopping(lobby, reason);
        }
        _stopping = true;
        // Lobby chat first: it says goodbye in the lobby, which SteamHost then leaves.
        _chat.Stop((ushort)reason);
        Voice.VoiceChat.End();
        if (_sockets.IsConnected)
        {
            _sockets.StopHost(reason, now);
        }
        LobbyId = null;
        ReportStopped(new NetErrorInfo(reason, selfInitiated: true));
    }

    public override string? GetRawLobbyIdentifier() => LobbyId?.ToString(CultureInfo.InvariantCulture);

    private void ReportStopped(NetErrorInfo info)
    {
        if (!_reportedStop)
        {
            _reportedStop = true;
            _handler.OnDisconnected(info);
        }
    }

    // SteamHost's events, passed on to the game.

    void INetHostHandler.OnPeerConnected(ulong peerId)
    {
        MainFile.Logger.Info($"sockets host: client {peerId} connected");
        _handler.OnPeerConnected(peerId);
    }

    void INetHostHandler.OnPeerDisconnected(ulong peerId, NetErrorInfo info)
    {
        MainFile.Logger.Info($"sockets host: client {peerId} disconnected ({info.GetReason()})");
        _handler.OnPeerDisconnected(peerId, info);
    }

    void INetHostHandler.OnDisconnected(NetErrorInfo info)
    {
        if (!_stopping)
        {
            // The sockets host ended by itself; the game stops the whole host.
            MainFile.Logger.Warn($"sockets host stopped: {info.GetReason()}");
            _chat.Stop((ushort)info.GetReason());
            Voice.VoiceChat.End();
        }
        ReportStopped(info);
    }

    void INetHandler.OnPacketReceived(ulong senderId, byte[] packetBytes, NetTransferMode mode, int channel)
    {
        ChatTiming.Note("received", packetBytes, packetBytes.Length);
        _handler.OnPacketReceived(senderId, packetBytes, mode, channel);
    }

    // Lobby chat's events, as ChatHost passes them on.

    void IChatHostEvents.PeerConnected(ulong peer)
    {
        MainFile.Logger.Info($"chat host: client {peer} connected");
        _handler.OnPeerConnected(peer);
    }

    void IChatHostEvents.PeerDisconnected(ulong peer, ushort reason, bool selfInitiated)
    {
        MainFile.Logger.Info($"chat host: client {peer} disconnected ({(NetError)reason}, by us: {selfInitiated})");
        _handler.OnPeerDisconnected(peer, new NetErrorInfo((NetError)reason, selfInitiated));
    }

    void IChatHostEvents.Data(ulong peer, byte[] data, bool reliable, byte channel)
    {
        ChatTiming.Note("received", data, data.Length);
        _handler.OnPacketReceived(peer, data, reliable ? NetTransferMode.Reliable : NetTransferMode.Unreliable, channel);
    }

    private sealed class SocketsAdapter(SteamHost host) : ISocketsHost
    {
        public IEnumerable<ulong> Peers => host.ConnectedPeerIds;

        public void Send(ulong peer, byte[] bytes, int length, bool reliable, int channel) =>
            host.SendMessageToClient(peer, bytes, length, reliable ? NetTransferMode.Reliable : NetTransferMode.Unreliable, channel);

        public void Disconnect(ulong peer, ushort reason, bool now) => host.DisconnectClient(peer, (NetError)reason, now);

        public void Update()
        {
            if (host.IsConnected)
            {
                host.Update();
            }
        }
    }
}
