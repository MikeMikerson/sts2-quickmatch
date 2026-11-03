using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using QuickMatch.Lobby;
using QuickMatch.Matchmaking;
using QuickMatch.Transport;

namespace QuickMatch.Steam;

/// <summary>Finds open quick-match lobbies of one size through the lobby backend's search.</summary>
internal static class LobbySearch
{
    private const double TimeoutSeconds = 10;

    /// <summary>
    /// Returns lobbies with at least <paramref name="minFreeSlots"/> free slots that match our
    /// size, game version, model database and gameplay mods. Returns an empty list on Steam errors
    /// or timeout; throws only when cancelled.
    /// </summary>
    public static async Task<List<LobbyCandidate>> FindAsync(int size, CancellationToken ct, int minFreeSlots = 1)
    {
        QuickMatchLobby.CompatKey key = QuickMatchLobby.CompatKey.Local();
        var query = new LobbyQuery(
            new Dictionary<string, string>
            {
                [LobbyKeys.Proto] = LobbyKeys.ProtoValue,
                [LobbyKeys.Size] = size.ToString(CultureInfo.InvariantCulture),
                [LobbyKeys.Version] = key.Version,
                [LobbyKeys.Db] = key.Db,
                [LobbyKeys.Mods] = key.Mods,
            },
            // The pool of modded players is small and co-op is turn based, so the search is worldwide.
            MinFreeSlots: minFreeSlots,
            MaxResults: 50,
            ReadKeys: [LobbyKeys.Members, LobbyKeys.CreatedAt, LobbyKeys.Link]);

        var found = new List<LobbyCandidate>();
        foreach (LobbyListing listing in await RequestAsync(query, ct) ?? [])
        {
            LobbyLinks.Remember(listing.Id, listing.Data.GetValueOrDefault(LobbyKeys.Link));
            int members = listing.Members;
            if (members <= 0)
            {
                int.TryParse(listing.Data.GetValueOrDefault(LobbyKeys.Members), NumberStyles.Integer, CultureInfo.InvariantCulture, out members);
            }
            uint.TryParse(listing.Data.GetValueOrDefault(LobbyKeys.CreatedAt), NumberStyles.Integer, CultureInfo.InvariantCulture, out uint createdAt);
            found.Add(new LobbyCandidate(listing.Id, members, createdAt));
        }

        string details = string.Join(", ", found.Select(c => $"{c.Id} n={c.Members} t={c.CreatedAt}{(LobbyLinks.OffersSockets(c.Id) == true ? " sockets" : "")}"));
        MainFile.Logger.Info($"lobby search size={size} free>={minFreeSlots} {key} -> {found.Count} result(s){(found.Count > 0 ? ": " + details : "")}");
        return found;
    }

    /// <summary>Lobbies in which a host has reopened the saved run with this key.</summary>
    public static async Task<List<ulong>> FindResumeAsync(string runKey, CancellationToken ct)
    {
        var query = new LobbyQuery(
            new Dictionary<string, string>
            {
                [LobbyKeys.ResumeProto] = LobbyKeys.ResumeProtoValue,
                [LobbyKeys.ResumeKey] = runKey,
                [LobbyKeys.Version] = QuickMatchLobby.CompatKey.Local().Version,
            },
            MinFreeSlots: 1,
            MaxResults: 10,
            ReadKeys: [LobbyKeys.Link]);

        var listings = await RequestAsync(query, ct) ?? [];
        foreach (LobbyListing listing in listings)
        {
            LobbyLinks.Remember(listing.Id, listing.Data.GetValueOrDefault(LobbyKeys.Link));
        }
        var found = listings.Select(l => l.Id).ToList();
        MainFile.Logger.Info($"resume search key={runKey} -> {found.Count} result(s){(found.Count > 0 ? ": " + string.Join(", ", found) : "")}");
        return found;
    }

    /// <summary>Runs one search; null on error or timeout.</summary>
    private static async Task<IReadOnlyList<LobbyListing>?> RequestAsync(LobbyQuery query, CancellationToken ct)
    {
        using var search = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task<IReadOnlyList<LobbyListing>?> request = LobbyBackend.Current.SearchAsync(query, search.Token);
        Task first = await Task.WhenAny(request, GodotDelay.Seconds(TimeoutSeconds, ct));
        ct.ThrowIfCancellationRequested();
        if (first != request)
        {
            search.Cancel();
            MainFile.Logger.Warn($"lobby search timed out after {TimeoutSeconds}s");
            return null;
        }
        try
        {
            return await request;
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            MainFile.Logger.Warn($"lobby search failed: {e.Message}");
            return null;
        }
    }
}
