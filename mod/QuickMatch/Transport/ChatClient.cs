using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Transport;
using QuickMatch.Lobby;
using QuickMatch.Matchmaking;
using QuickMatch.Voice;

namespace QuickMatch.Transport;

/// <summary>The game's client transport over lobby chat, in place of its Steam sockets client.</summary>
internal sealed class ChatClient : NetClient, IChatClientEvents
{
    private readonly ILobbyBackend _backend;
    private readonly ChatClientCore _core;

    public ChatClient(INetClientHandler handler, ILobbyBackend backend)
        : base(handler)
    {
        _backend = backend;
        _core = new ChatClientCore(backend, this, ChatClock.Now, ChatClock.NewSession(), message => MainFile.Logger.Info($"chat client: {message}"),
            RelayTransport.Current);
    }

    public override bool IsConnected => _core.IsConnected;

    public override ulong NetId => _backend.LocalId;

    public override ulong HostNetId => _core.Host;

    public async Task<NetErrorInfo?> ConnectToLobby(ulong lobbyId, CancellationToken ct)
    {
        MainFile.Logger.Info($"joining lobby {lobbyId} over lobby chat, our id {NetId}");
        ushort? failure = await _core.ConnectAsync(lobbyId, NextFrame, ct);
        if (failure is { } reason)
        {
            return new NetErrorInfo((NetError)reason, selfInitiated: false);
        }
        MainFile.Logger.Info($"connected to host {_core.Host} in lobby {lobbyId}");
        _handler.OnConnectedToHost();
        _core.BeginDelivery();
        return null;
    }

    public override void Update()
    {
        _core.Update();
        if (_core.IsConnected)
        {
            VoiceChat.Tick(_core.Route);
        }
    }

    public override void SendMessageToHost(byte[] bytes, int length, NetTransferMode mode, int channel = 0)
    {
        ChatTiming.Note("sent", bytes, length);
        _core.Send(bytes, length, mode == NetTransferMode.Reliable, (byte)channel);
    }

    public override void DisconnectFromHost(NetError reason, bool now = false)
    {
        if (!_core.IsConnected)
        {
            return;
        }
        ulong host = _core.Host;
        _core.Disconnect((ushort)reason);
        VoiceChat.End();
        _handler.OnDisconnectedFromHost(host, new NetErrorInfo(reason, selfInitiated: true));
    }

    public override string? GetRawLobbyIdentifier()
    {
        ulong lobby = _core.Lobby;
        return lobby == 0 ? null : lobby.ToString(CultureInfo.InvariantCulture);
    }

    void IChatClientEvents.Disconnected(ushort reason)
    {
        MainFile.Logger.Info($"chat client: disconnected from host {_core.Host} ({(NetError)reason})");
        VoiceChat.End();
        _handler.OnDisconnectedFromHost(_core.Host, new NetErrorInfo((NetError)reason, selfInitiated: false));
    }

    void IChatClientEvents.Data(byte[] data, bool reliable, byte channel)
    {
        ChatTiming.Note("received", data, data.Length);
        _handler.OnPacketReceived(_core.Host, data, reliable ? NetTransferMode.Reliable : NetTransferMode.Unreliable, channel);
    }

    private static Task NextFrame(CancellationToken ct) => GodotDelay.Seconds(0.05, ct);
}
