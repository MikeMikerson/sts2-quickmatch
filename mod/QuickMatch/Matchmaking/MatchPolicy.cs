using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace QuickMatch.Matchmaking;

/// <summary>A quick-match lobby seen in a Steam lobby search.</summary>
/// <param name="Id">Steam lobby id.</param>
/// <param name="Members">Players currently in the lobby (the host counts).</param>
/// <param name="CreatedAt">Steam server time (seconds) when the host opened the lobby; 0 if unknown.</param>
public readonly record struct LobbyCandidate(ulong Id, int Members, uint CreatedAt);

/// <summary>
/// Pure matching rules, kept free of Godot and Steam types so they can be unit tested.
/// </summary>
public static class MatchPolicy
{
    /// <summary>
    /// Total order over lobbies; better lobbies sort first: fuller groups, then older lobbies,
    /// then lower lobby ids. Everyone uses the same order, so searchers pile into the same
    /// lobbies (groups fill quickly) and two hosts waiting alone always agree on who yields.
    /// </summary>
    public static int Compare(LobbyCandidate a, LobbyCandidate b)
    {
        int byMembers = b.Members.CompareTo(a.Members);
        if (byMembers != 0)
        {
            return byMembers;
        }
        int byAge = a.CreatedAt.CompareTo(b.CreatedAt);
        return byAge != 0 ? byAge : a.Id.CompareTo(b.Id);
    }

    /// <summary>
    /// Candidates worth trying, best first. Drops our own lobby, lobbies that failed recently,
    /// duplicates, and lobbies whose data has not fully arrived (no creation time).
    /// </summary>
    public static List<LobbyCandidate> Rank(IEnumerable<LobbyCandidate> found, ulong ownLobbyId, Func<ulong, bool> skip)
    {
        return found
            .Where(c => c.Id != ownLobbyId && c.CreatedAt != 0 && !skip(c.Id))
            .GroupBy(c => c.Id)
            .Select(g => g.First())
            .OrderBy(c => c, Comparer<LobbyCandidate>.Create(Compare))
            .ToList();
    }

    /// <summary>
    /// For a host whose lobby is not yet full: the lobby its whole group (own.Members players)
    /// should move into, or null to keep waiting. Only a strictly better lobby with room for the
    /// group qualifies, so of two partial groups at most one moves, and never into each other.
    /// </summary>
    public static LobbyCandidate? PickMergeTarget(LobbyCandidate own, IEnumerable<LobbyCandidate> found, Func<ulong, bool> skip, int size)
    {
        if (own.Members < 1 || own.Members >= size)
        {
            return null;
        }
        foreach (LobbyCandidate candidate in Rank(found, own.Id, skip))
        {
            if (Compare(candidate, own) >= 0)
            {
                return null;
            }
            if (candidate.Members + own.Members <= size)
            {
                return candidate;
            }
        }
        return null;
    }

    /// <summary>
    /// Identifies one co-op run: its seed and the Steam ids of everyone in it. The host computes it
    /// from its save and every player from the running game, so all of them agree on it.
    /// </summary>
    public static string RunKey(string seed, IEnumerable<ulong> playerIds)
    {
        string ids = string.Join(",", playerIds.Distinct().OrderBy(id => id));
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(seed + "|" + ids));
        return Convert.ToHexString(digest, 0, 12).ToLowerInvariant();
    }

    /// <summary>
    /// A client follows a group move only when it comes from the host of the lobby it is in (the
    /// lobby's owner) and names another lobby: no other player can send the group elsewhere.
    /// </summary>
    public static bool AcceptsMove(ulong senderId, ulong hostId, ulong targetLobbyId, ulong currentLobbyId) =>
        hostId != 0 && senderId == hostId && targetLobbyId != 0 && targetLobbyId != currentLobbyId;

    /// <summary>
    /// Stable, order-independent fingerprint of the gameplay-affecting mod list
    /// ("id-version" entries, as the multiplayer handshake compares them). "none" when empty.
    /// </summary>
    public static string ModsHash(IEnumerable<string>? mods)
    {
        List<string> sorted = (mods ?? [])
            .Where(m => !string.IsNullOrEmpty(m))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(m => m, StringComparer.Ordinal)
            .ToList();
        if (sorted.Count == 0)
        {
            return "none";
        }
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", sorted)));
        return Convert.ToHexString(digest, 0, 8).ToLowerInvariant();
    }
}
