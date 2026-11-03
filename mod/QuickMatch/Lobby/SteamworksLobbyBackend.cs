using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Multiplayer.Transport.Steam;
using MegaCrit.Sts2.Core.Platform.Steam;
using Steamworks;

namespace QuickMatch.Lobby;

/// <summary>
/// Lobbies through the desktop game's Steamworks. Steam callbacks, and so <see cref="Changed"/>, run
/// on the Godot main thread (the game pumps them every frame).
/// </summary>
internal sealed class SteamworksLobbyBackend : ILobbyBackend
{
    // Steam delivered lobby chat messages of 256 KB in tests; the transport sends at most 32 KB.
    private const int ChatBufferSize = 256 * 1024;

    private readonly byte[] _buffer = new byte[ChatBufferSize];
    private Callback<LobbyChatMsg_t>? _chatCallback;
    private Callback<LobbyChatUpdate_t>? _updateCallback;
    private Action<LobbyEvent>? _changed;

    public bool IsAvailable => SteamInitializer.Initialized && !CommandLineHelper.HasArg("fastmp");

    public ulong LocalId => SteamUser.GetSteamID().m_SteamID;

    public uint ServerTime => SteamUtils.GetServerRealTime();

    public event Action<LobbyEvent>? Changed
    {
        add
        {
            // Created on first use, once Steam is up; kept for the life of the process.
            _chatCallback ??= Callback<LobbyChatMsg_t>.Create(OnChat);
            _updateCallback ??= Callback<LobbyChatUpdate_t>.Create(OnUpdate);
            _changed += value;
        }
        remove => _changed -= value;
    }

    public async Task<ulong?> CreateAsync(LobbyKind kind, int maxMembers, CancellationToken ct)
    {
        using var call = new SteamCallResult<LobbyCreated_t>(SteamMatchmaking.CreateLobby((ELobbyType)kind, maxMembers), ct);
        try
        {
            LobbyCreated_t created = await call.Task;
            if (created.m_eResult != EResult.k_EResultOK)
            {
                MainFile.Logger.Warn($"Steam refused to create a lobby: {created.m_eResult}");
                return null;
            }
            return created.m_ulSteamIDLobby;
        }
        catch (IOException e)
        {
            MainFile.Logger.Warn($"lobby creation failed: {e.Message}");
            return null;
        }
    }

    public async Task<LobbyJoinResult> JoinAsync(ulong lobby, CancellationToken ct)
    {
        using var call = new SteamCallResult<LobbyEnter_t>(SteamMatchmaking.JoinLobby(new CSteamID(lobby)), ct);
        LobbyEnter_t entered;
        try
        {
            entered = await call.Task;
        }
        catch (IOException e)
        {
            MainFile.Logger.Warn($"joining lobby {lobby} failed: {e.Message}");
            return LobbyJoinResult.Error;
        }
        if (entered.m_ulSteamIDLobby != lobby)
        {
            return LobbyJoinResult.Error;
        }
        return (EChatRoomEnterResponse)entered.m_EChatRoomEnterResponse switch
        {
            EChatRoomEnterResponse.k_EChatRoomEnterResponseSuccess => LobbyJoinResult.Ok,
            EChatRoomEnterResponse.k_EChatRoomEnterResponseDoesntExist => LobbyJoinResult.DoesNotExist,
            EChatRoomEnterResponse.k_EChatRoomEnterResponseNotAllowed => LobbyJoinResult.NotAllowed,
            EChatRoomEnterResponse.k_EChatRoomEnterResponseFull => LobbyJoinResult.Full,
            EChatRoomEnterResponse.k_EChatRoomEnterResponseBanned => LobbyJoinResult.Banned,
            EChatRoomEnterResponse.k_EChatRoomEnterResponseLimited => LobbyJoinResult.Limited,
            EChatRoomEnterResponse.k_EChatRoomEnterResponseClanDisabled
                or EChatRoomEnterResponse.k_EChatRoomEnterResponseCommunityBan
                or EChatRoomEnterResponse.k_EChatRoomEnterResponseMemberBlockedYou
                or EChatRoomEnterResponse.k_EChatRoomEnterResponseYouBlockedMember => LobbyJoinResult.Blocked,
            EChatRoomEnterResponse.k_EChatRoomEnterResponseRatelimitExceeded => LobbyJoinResult.RateLimited,
            _ => LobbyJoinResult.Error,
        };
    }

    public void Leave(ulong lobby) => SteamMatchmaking.LeaveLobby(new CSteamID(lobby));

    public async Task<IReadOnlyList<LobbyListing>?> SearchAsync(LobbyQuery query, CancellationToken ct)
    {
        // Steam clears the filters after every request, so they are added each time.
        foreach ((string key, string value) in query.Equal)
        {
            SteamMatchmaking.AddRequestLobbyListStringFilter(key, value, ELobbyComparison.k_ELobbyComparisonEqual);
        }
        SteamMatchmaking.AddRequestLobbyListFilterSlotsAvailable(query.MinFreeSlots);
        SteamMatchmaking.AddRequestLobbyListDistanceFilter(ELobbyDistanceFilter.k_ELobbyDistanceFilterWorldwide);
        SteamMatchmaking.AddRequestLobbyListResultCountFilter(query.MaxResults);

        using var call = new SteamCallResult<LobbyMatchList_t>(SteamMatchmaking.RequestLobbyList(), ct);
        LobbyMatchList_t list;
        try
        {
            list = await call.Task;
        }
        catch (IOException e)
        {
            MainFile.Logger.Warn($"lobby search failed: {e.Message}");
            return null;
        }
        var found = new List<LobbyListing>();
        for (int i = 0; i < (int)list.m_nLobbiesMatching; i++)
        {
            CSteamID lobby = SteamMatchmaking.GetLobbyByIndex(i);
            var data = new Dictionary<string, string>();
            foreach (string key in query.ReadKeys)
            {
                data[key] = SteamMatchmaking.GetLobbyData(lobby, key);
            }
            found.Add(new LobbyListing(lobby.m_SteamID, SteamMatchmaking.GetNumLobbyMembers(lobby), data));
        }
        return found;
    }

    public void SetData(ulong lobby, string key, string value) => SteamMatchmaking.SetLobbyData(new CSteamID(lobby), key, value);

    public void DeleteData(ulong lobby, string key) => SteamMatchmaking.DeleteLobbyData(new CSteamID(lobby), key);

    public string GetData(ulong lobby, string key) => SteamMatchmaking.GetLobbyData(new CSteamID(lobby), key);

    public bool SetKind(ulong lobby, LobbyKind kind) => SteamMatchmaking.SetLobbyType(new CSteamID(lobby), (ELobbyType)kind);

    public void SetJoinable(ulong lobby, bool joinable) => SteamMatchmaking.SetLobbyJoinable(new CSteamID(lobby), joinable);

    public ulong Owner(ulong lobby) => SteamMatchmaking.GetLobbyOwner(new CSteamID(lobby)).m_SteamID;

    public IReadOnlyList<ulong> Members(ulong lobby)
    {
        var id = new CSteamID(lobby);
        int count = SteamMatchmaking.GetNumLobbyMembers(id);
        var members = new List<ulong>(count);
        for (int i = 0; i < count; i++)
        {
            members.Add(SteamMatchmaking.GetLobbyMemberByIndex(id, i).m_SteamID);
        }
        return members;
    }

    public bool SendChat(ulong lobby, byte[] data, int length, ulong target)
    {
        // Steamworks lobby chat always goes to every member; receivers skip what is not theirs.
        return SteamMatchmaking.SendLobbyChatMsg(new CSteamID(lobby), data, length);
    }

    public string? PlayerName(ulong id)
    {
        string name = SteamFriends.GetFriendPersonaName(new CSteamID(id));
        return string.IsNullOrEmpty(name) ? null : name;
    }

    private void OnChat(LobbyChatMsg_t message)
    {
        int size = SteamMatchmaking.GetLobbyChatEntry(new CSteamID(message.m_ulSteamIDLobby), (int)message.m_iChatID,
            out CSteamID sender, _buffer, _buffer.Length, out EChatEntryType type);
        if (type != EChatEntryType.k_EChatEntryTypeChatMsg || size <= 0)
        {
            return;
        }
        var data = new byte[size];
        Array.Copy(_buffer, data, size);
        _changed?.Invoke(new LobbyEvent(LobbyEventKind.Chat, message.m_ulSteamIDLobby, sender.m_SteamID, data));
    }

    private void OnUpdate(LobbyChatUpdate_t update)
    {
        var change = (EChatMemberStateChange)update.m_rgfChatMemberStateChange;
        LobbyEventKind kind = (change & EChatMemberStateChange.k_EChatMemberStateChangeEntered) != 0
            ? LobbyEventKind.MemberJoined
            : LobbyEventKind.MemberLeft;
        _changed?.Invoke(new LobbyEvent(kind, update.m_ulSteamIDLobby, update.m_ulSteamIDUserChanged));
    }
}
