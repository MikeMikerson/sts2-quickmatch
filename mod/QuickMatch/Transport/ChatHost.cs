using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Transport;
using MegaCrit.Sts2.Core.TestSupport;
using QuickMatch.Lobby;
using QuickMatch.Steam;
using QuickMatch.Voice;

namespace QuickMatch.Transport;

/// <summary>
/// The game's host transport over lobby chat, in place of its Steam sockets host. Started through
/// NetHostGameService.StartTestHost (the game's entry point for a host it does not build itself), and
/// otherwise behaves as SteamHost: a lobby member who connects is a peer, closing the host leaves the
/// lobby. QuickMatch's lobby rules (unlisted once closed or stopping) live here too.
/// </summary>
internal sealed class ChatHost : AbstractTestNetHost, IChatHostEvents
{
    private readonly ILobbyBackend _backend;
    private readonly ChatHostCore _core;

    public ChatHost(INetHostHandler handler, ILobbyBackend backend)
        : base(handler)
    {
        _backend = backend;
        _core = new ChatHostCore(backend, this, ChatClock.Now, ChatClock.NewSession(), message => MainFile.Logger.Info($"chat host: {message}"),
            RelayTransport.Current);
    }

    public ulong? LobbyId { get; private set; }

    public override bool IsConnected => _core.IsRunning;

    public override ulong NetId => _backend.LocalId;

    public override IEnumerable<ulong> ConnectedPeerIds => _core.Peers;

    public override async Task<NetErrorInfo?> StartHost(int maxClients)
    {
        MainFile.Logger.Info($"starting lobby chat host for {maxClients}, our id {NetId}");
        // Friends only at first, as the game's host; QuickMatch publishes it once it is described.
        ulong? lobby = await _backend.CreateAsync(LobbyKind.FriendsOnly, maxClients, CancellationToken.None);
        if (lobby is not { } id)
        {
            return new NetErrorInfo(NetError.FailedToHost, selfInitiated: true);
        }
        LobbyId = id;
        _core.Start(id);
        return null;
    }

    public override void Update()
    {
        _core.Update();
        if (_core.IsRunning)
        {
            VoiceChat.Tick(_core.Route);
        }
    }

    public override void SetHostIsClosed(bool isClosed)
    {
        if (LobbyId is { } lobby)
        {
            QuickMatchLobby.SetClosed(lobby, isClosed);
        }
    }

    public override void SendMessageToClient(ulong peerId, byte[] bytes, int length, NetTransferMode mode, int channel = 0)
    {
        if (!_core.Peers.Contains(peerId))
        {
            throw new InvalidOperationException($"Could not find connection for peer {peerId}!");
        }
        ChatTiming.Note("sent", bytes, length);
        _core.Send(peerId, bytes, length, mode == NetTransferMode.Reliable, (byte)channel);
    }

    public override void SendMessageToAll(byte[] bytes, int length, NetTransferMode mode, int channel = 0)
    {
        ChatTiming.Note("sent", bytes, length);
        foreach (ulong peer in _core.Peers)
        {
            _core.Send(peer, bytes, length, mode == NetTransferMode.Reliable, (byte)channel);
        }
    }

    public override void DisconnectClient(ulong peerId, NetError reason, bool now = false)
    {
        _core.Disconnect(peerId, (ushort)reason);
    }

    public override void StopHost(NetError reason, bool now = false)
    {
        if (LobbyId is { } lobby)
        {
            QuickMatchLobby.OnHostStopping(lobby, reason);
        }
        _core.Stop((ushort)reason);
        VoiceChat.End();
        LobbyId = null;
        _handler.OnDisconnected(new NetErrorInfo(reason, selfInitiated: true));
    }

    public override string? GetRawLobbyIdentifier() => LobbyId?.ToString(CultureInfo.InvariantCulture);

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
}
