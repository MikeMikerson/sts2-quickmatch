using System;
using System.Threading;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Connection;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Platform;
using QuickMatch.Lobby;

namespace QuickMatch.Transport;

/// <summary>Joins a QuickMatch lobby over lobby chat; the counterpart of the game's SteamClientConnectionInitializer.FromLobby.</summary>
internal sealed class ChatConnectionInitializer : IClientConnectionInitializer
{
    private readonly ulong _lobby;

    private ChatConnectionInitializer(ulong lobby)
    {
        _lobby = lobby;
    }

    public static ChatConnectionInitializer FromLobby(ulong lobby) => new(lobby);

    public async Task<NetErrorInfo?> Connect(INetClientGameService gameService, CancellationToken cancelToken = default)
    {
        if (gameService.IsConnected)
        {
            throw new InvalidOperationException("NetClientGameService must not be connected when passed to ChatConnectionInitializer!");
        }
        var client = new ChatClient(gameService, LobbyBackend.Current);
        gameService.Initialize(client, PlatformType.Steam);
        return await client.ConnectToLobby(_lobby, cancelToken);
    }

    public override string ToString() => $"ChatConnectionInitializer lobby: {_lobby}";
}
