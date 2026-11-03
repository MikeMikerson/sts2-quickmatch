using QuickMatch.Lobby;

namespace QuickMatch.Transport;

/// <summary>
/// Disconnect reasons as they travel in Hello, Reject and Bye frames: the numeric values of the game's
/// NetError, so a reason the game gives on one end comes out unchanged on the other. Kept as numbers
/// here so the transport core builds without the game (a test checks them against the installed game).
/// </summary>
internal static class ChatReasons
{
    public const ushort Quit = 1;
    public const ushort InvalidJoin = 5;
    public const ushort CancelledJoin = 6;
    public const ushort LobbyFull = 100;
    public const ushort VersionMismatch = 103;
    public const ushort JoinBlockedByUser = 104;
    public const ushort Timeout = 201;
    public const ushort InternalError = 202;
    public const ushort UnknownNetworkError = 203;
    public const ushort RateLimited = 204;

    /// <summary>The reason for a failed lobby entry, as the game maps Steam's lobby enter responses.</summary>
    public static ushort ForJoin(LobbyJoinResult result) => result switch
    {
        LobbyJoinResult.DoesNotExist => InvalidJoin,
        LobbyJoinResult.NotAllowed => InternalError,
        LobbyJoinResult.Full => LobbyFull,
        LobbyJoinResult.Banned or LobbyJoinResult.Blocked => JoinBlockedByUser,
        LobbyJoinResult.RateLimited => RateLimited,
        _ => UnknownNetworkError,
    };
}
