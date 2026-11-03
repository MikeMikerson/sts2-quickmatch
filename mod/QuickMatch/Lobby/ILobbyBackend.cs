using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace QuickMatch.Lobby;

/// <summary>Who can find and join a lobby. Same values as Steam's ELobbyType.</summary>
public enum LobbyKind
{
    Private = 0,
    FriendsOnly = 1,
    Public = 2,
    Invisible = 3,
}

/// <summary>How entering a lobby went, as Steam reports it.</summary>
public enum LobbyJoinResult
{
    Ok,
    DoesNotExist,
    NotAllowed,
    Full,
    Error,
    Banned,
    Limited,
    Blocked,
    RateLimited,
}

public enum LobbyEventKind
{
    /// <summary>A lobby chat message; <see cref="LobbyEvent.Data"/> holds its bytes.</summary>
    Chat,
    MemberJoined,
    /// <summary>A member left, lost its connection, or was kicked or banned.</summary>
    MemberLeft,
}

/// <summary>Something that happened in a lobby we are in.</summary>
public readonly record struct LobbyEvent(LobbyEventKind Kind, ulong Lobby, ulong Member, byte[]? Data = null);

/// <summary>One search result: the lobby, its member count (0 if unknown) and the data values the search asked for.</summary>
public readonly record struct LobbyListing(ulong Id, int Members, IReadOnlyDictionary<string, string> Data);

/// <summary>
/// A worldwide lobby search: every key in <see cref="Equal"/> must hold exactly that value, at least
/// <see cref="MinFreeSlots"/> slots must be free, at most <see cref="MaxResults"/> come back, and each
/// result carries the values of <see cref="ReadKeys"/>.
/// </summary>
public sealed record LobbyQuery(
    IReadOnlyDictionary<string, string> Equal,
    int MinFreeSlots,
    int MaxResults,
    IReadOnlyList<string> ReadKeys);

/// <summary>
/// The Steam lobby operations QuickMatch needs, and lobby chat, which also carries the game's own
/// messages (see QuickMatch.Transport). Desktop uses Steamworks (<see cref="SteamworksLobbyBackend"/>);
/// an app without Steamworks, such as the iPhone port, sets its own in <see cref="LobbyBackend.Current"/>
/// before the mod initializes. Both reach the same Steam lobbies, so players of either can match.
/// </summary>
public interface ILobbyBackend
{
    /// <summary>True when signed in to Steam and able to use lobbies.</summary>
    bool IsAvailable { get; }

    /// <summary>Our own Steam ID.</summary>
    ulong LocalId { get; }

    /// <summary>Steam's clock, in seconds since 1970 (UTC).</summary>
    uint ServerTime { get; }

    /// <summary>Chat and membership changes in lobbies we are in. May be raised on any thread.</summary>
    event Action<LobbyEvent>? Changed;

    /// <summary>Creates a lobby we own and are in; null if Steam refused.</summary>
    Task<ulong?> CreateAsync(LobbyKind kind, int maxMembers, CancellationToken ct);

    Task<LobbyJoinResult> JoinAsync(ulong lobby, CancellationToken ct);

    void Leave(ulong lobby);

    /// <summary>The matching lobbies, or null when Steam reported an error. Throws only when cancelled.</summary>
    Task<IReadOnlyList<LobbyListing>?> SearchAsync(LobbyQuery query, CancellationToken ct);

    void SetData(ulong lobby, string key, string value);

    void DeleteData(ulong lobby, string key);

    /// <summary>A data value of a lobby we are in, or "" if unset.</summary>
    string GetData(ulong lobby, string key);

    bool SetKind(ulong lobby, LobbyKind kind);

    void SetJoinable(ulong lobby, bool joinable);

    ulong Owner(ulong lobby);

    /// <summary>Members of a lobby we are in, ourselves included.</summary>
    IReadOnlyList<ulong> Members(ulong lobby);

    /// <summary>
    /// Sends one lobby chat message. <paramref name="target"/> 0 goes to every member; a member's ID
    /// limits it to that member where Steam allows it (Steamworks cannot, and always sends to all).
    /// </summary>
    bool SendChat(ulong lobby, byte[] data, int length, ulong target);

    /// <summary>A player's Steam name, or null if not known.</summary>
    string? PlayerName(ulong id);
}
