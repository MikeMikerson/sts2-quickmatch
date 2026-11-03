using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using QuickMatch.Lobby;
using QuickMatchAndroid.Logging;
using SteamKit2;

namespace QuickMatchAndroid.Lobby;

/// <summary>
/// QuickMatch's lobby interface in the Android launcher, answered over SteamKit2 because the launcher
/// has no Steamworks (its libsteam_api is a stub). The lobbies, their data and their chat are the same
/// ones desktop players reach through Steamworks and iPhone players through the iPhone port, so all of
/// them match each other. Mirrors the iPhone port's PhoneLobbyBackend.
/// </summary>
internal sealed class AndroidLobbyBackend : ILobbyBackend
{
    /// <summary>The launcher allows 15 s per connection attempt.</summary>
    private static readonly TimeSpan SignInTimeout = TimeSpan.FromSeconds(30);

    /// <summary>A failed sign-in is tried again when QuickMatch next asks, but not more often than this.</summary>
    private static readonly TimeSpan SignInRetry = TimeSpan.FromSeconds(30);

    private readonly SteamKitLobbyService _lobbies;
    private readonly IModLog _log;
    private int _signingIn;
    private long _lastSignInTicks;

    public AndroidLobbyBackend(SteamKitLobbyService lobbies, IModLog log)
    {
        _lobbies = lobbies;
        _log = log;
        lobbies.ChatReceived += (lobby, sender, data) => Changed?.Invoke(new LobbyEvent(LobbyEventKind.Chat, lobby, sender, data));
        lobbies.MemberJoined += (lobby, member) => Changed?.Invoke(new LobbyEvent(LobbyEventKind.MemberJoined, lobby, member));
        lobbies.MemberLeft += (lobby, member) => Changed?.Invoke(new LobbyEvent(LobbyEventKind.MemberLeft, lobby, member));
    }

    /// <summary>
    /// True once this connection has signed in, which also tells us our Steam ID. Until then each
    /// question starts a sign-in in the background (see <see cref="StartSignIn"/>).
    /// </summary>
    public bool IsAvailable
    {
        get
        {
            if (LocalId != 0)
            {
                return true;
            }
            StartSignIn();
            return false;
        }
    }

    public ulong LocalId => _lobbies.LocalId;

    /// <summary>The phone's clock; QuickMatch only uses it to tell older lobbies from newer ones.</summary>
    public uint ServerTime => (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    public event Action<LobbyEvent>? Changed;

    /// <summary>
    /// Signs in on a worker thread unless signed in, already signing in, or the last try was under
    /// <see cref="SignInRetry"/> ago. The connection closes itself when idle; lobby calls reopen it.
    /// </summary>
    public void StartSignIn()
    {
        long now = DateTime.UtcNow.Ticks;
        if (LocalId != 0 || now - Interlocked.Read(ref _lastSignInTicks) < SignInRetry.Ticks
            || Interlocked.Exchange(ref _signingIn, 1) == 1)
        {
            return;
        }
        Interlocked.Exchange(ref _lastSignInTicks, now);
        _ = Task.Run(async () =>
        {
            try
            {
                using var timeout = new CancellationTokenSource(SignInTimeout);
                ulong id = await _lobbies.SignInAsync(timeout.Token).ConfigureAwait(false);
                _log.Info($"signed in to Steam for lobbies as {id}");
            }
            catch (Exception e)
            {
                _log.Warn($"Steam sign-in for lobbies failed, Quick Match waits: {e.GetType().Name}: {e.Message}");
            }
            finally
            {
                Interlocked.Exchange(ref _signingIn, 0);
            }
        });
    }

    public Task<ulong?> CreateAsync(LobbyKind kind, int maxMembers, CancellationToken ct) => _lobbies.CreateAsync((ELobbyType)kind, maxMembers, ct);

    public async Task<LobbyJoinResult> JoinAsync(ulong lobby, CancellationToken ct) => Map(await _lobbies.JoinAsync(lobby, ct));

    internal static LobbyJoinResult Map(EChatRoomEnterResponse response) => response switch
    {
        EChatRoomEnterResponse.Success => LobbyJoinResult.Ok,
        EChatRoomEnterResponse.DoesntExist => LobbyJoinResult.DoesNotExist,
        EChatRoomEnterResponse.NotAllowed => LobbyJoinResult.NotAllowed,
        EChatRoomEnterResponse.Full => LobbyJoinResult.Full,
        EChatRoomEnterResponse.Banned => LobbyJoinResult.Banned,
        EChatRoomEnterResponse.Limited => LobbyJoinResult.Limited,
        EChatRoomEnterResponse.ClanDisabled
            or EChatRoomEnterResponse.CommunityBan
            or EChatRoomEnterResponse.MemberBlockedYou
            or EChatRoomEnterResponse.YouBlockedMember => LobbyJoinResult.Blocked,
        // Steamworks' k_EChatRoomEnterResponseRatelimitExceeded; SteamKit2's enum has no name for it.
        (EChatRoomEnterResponse)15 => LobbyJoinResult.RateLimited,
        _ => LobbyJoinResult.Error,
    };

    public void Leave(ulong lobby) => _lobbies.Leave(lobby);

    public async Task<IReadOnlyList<LobbyListing>?> SearchAsync(LobbyQuery query, CancellationToken ct)
    {
        IReadOnlyList<LobbySummary>? found = await _lobbies.SearchAsync(query.Equal, query.MinFreeSlots, query.MaxResults, ct);
        return found == null ? null : Listings(found, query.ReadKeys);
    }

    internal static IReadOnlyList<LobbyListing> Listings(IEnumerable<LobbySummary> found, IReadOnlyList<string> readKeys) =>
        found.Select(lobby => new LobbyListing(lobby.Id, lobby.Members,
            readKeys.ToDictionary(key => key, key => lobby.Data.TryGetValue(key, out string? value) ? value : ""))).ToList();

    public void SetData(ulong lobby, string key, string value) => _lobbies.SetData(lobby, key, value);

    public void DeleteData(ulong lobby, string key) => _lobbies.DeleteData(lobby, key);

    public string GetData(ulong lobby, string key) => _lobbies.GetData(lobby, key);

    public bool SetKind(ulong lobby, LobbyKind kind) => _lobbies.SetType(lobby, (ELobbyType)kind);

    public void SetJoinable(ulong lobby, bool joinable) => _lobbies.SetJoinable(lobby, joinable);

    public ulong Owner(ulong lobby) => _lobbies.Owner(lobby);

    public IReadOnlyList<ulong> Members(ulong lobby) => _lobbies.Members(lobby);

    public bool SendChat(ulong lobby, byte[] data, int length, ulong target) => _lobbies.SendChat(lobby, data, length, target);

    /// <summary>A lobby member's Steam name, or ours; null if not known.</summary>
    public string? PlayerName(ulong id) => _lobbies.MemberName(id) ?? (id != 0 && id == LocalId ? _lobbies.OwnName : null);
}
