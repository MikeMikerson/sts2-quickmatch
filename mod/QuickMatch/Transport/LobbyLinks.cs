using System.Collections.Generic;
using QuickMatch.Steam;

namespace QuickMatch.Transport;

/// <summary>
/// Which connection a quick-match game uses. A desktop host takes the game's own Steam sockets, as
/// vanilla co-op does, and lobby chat for players without Steamworks (phones), and says so in its
/// lobby data (<see cref="LobbyKeys.Link"/>). A host without Steamworks, or a build from before the
/// key, takes lobby chat only. A joiner uses the sockets when both sides have them, else lobby chat,
/// so two desktop players play exactly as in vanilla co-op.
///
/// What each lobby offers is remembered from search results, which carry its data before we join.
/// Main thread only, like the rest of matchmaking.
/// </summary>
internal static class LobbyLinks
{
    private const int MaxRemembered = 256;

    private static readonly Dictionary<ulong, bool> _sockets = new();

    /// <summary>Notes what a search result says about its host (the value of <see cref="LobbyKeys.Link"/>, or "").</summary>
    public static void Remember(ulong lobby, string? advertised)
    {
        if (_sockets.Count >= MaxRemembered && !_sockets.ContainsKey(lobby))
        {
            _sockets.Clear();
        }
        _sockets[lobby] = advertised == LobbyKeys.LinkSockets;
    }

    /// <summary>True or false once a search has listed the lobby; null if we never saw it.</summary>
    public static bool? OffersSockets(ulong lobby) => _sockets.TryGetValue(lobby, out bool sockets) ? sockets : null;

    /// <summary>Steam sockets only when we have them and the host said it takes them; anything unknown goes over lobby chat, which every host takes.</summary>
    public static bool JoinOverSockets(bool weHaveSockets, bool? hostOffersSockets) => weHaveSockets && hostOffersSockets == true;

    internal static void Forget() => _sockets.Clear();
}
