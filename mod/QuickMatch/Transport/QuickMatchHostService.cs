using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Platform;
using QuickMatch.Lobby;

namespace QuickMatch.Transport;

/// <summary>
/// The game's host service with QuickMatch's host: a <see cref="MixedHost"/> (Steam sockets and lobby
/// chat) where Steamworks runs, a <see cref="ChatHost"/> (lobby chat only) elsewhere. The game's entry
/// point for such a host sets the platform to None, which would name players from a debug list and
/// record runs without Steam IDs; the game reads the platform only through INetGameService, so it is
/// answered here as Steam.
/// </summary>
internal sealed class QuickMatchHostService : NetHostGameService, INetGameService
{
    public QuickMatchHostService()
        : base(PeerVersionInfo.LocalDefault())
    {
    }

    PlatformType INetGameService.Platform => PlatformType.Steam;

    /// <summary>True where the game's own Steam sockets work: the desktop game with Steamworks.</summary>
    public static bool HasSockets => LobbyBackend.Current is SteamworksLobbyBackend { IsAvailable: true };

    /// <summary>The host's Steam lobby once started.</summary>
    public ulong? LobbyId => NetHost switch
    {
        MixedHost mixed => mixed.LobbyId,
        ChatHost chat => chat.LobbyId,
        _ => null,
    };

    /// <summary>True when the host also takes the game's Steam sockets.</summary>
    public bool TakesSockets => NetHost is MixedHost;

    /// <summary>True when the game runs on QuickMatch's own transport: as host, or as a client over lobby chat.</summary>
    public static bool UsesOwnTransport(INetGameService service) =>
        service is QuickMatchHostService or INetClientGameService { NetClient: ChatClient };

    public Task<NetErrorInfo?> StartQuickMatchHost(int maxClients) =>
        HasSockets ? StartMixedHost(maxClients) : StartTestHost(new ChatHost(this, LobbyBackend.Current), maxClients);

    // Kept apart, so a phone's runtime never compiles code that builds the game's Steam host.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private Task<NetErrorInfo?> StartMixedHost(int maxClients) => StartTestHost(new MixedHost(this, LobbyBackend.Current), maxClients);
}
