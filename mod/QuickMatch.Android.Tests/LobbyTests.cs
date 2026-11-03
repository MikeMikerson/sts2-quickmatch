using QuickMatch.Lobby;
using QuickMatchAndroid.Launcher;
using QuickMatchAndroid.Lobby;
using QuickMatchAndroid.Logging;
using SteamKit2;
using SteamKit2.Internal;
using Xunit;

namespace QuickMatch.Android.Tests;

/// <summary>The SteamKit2 lobby code, offline: Steam's own message classes, no connection.</summary>
public class LobbyTests
{
    private const uint Game = 2868840;
    private const ulong Lobby = 109775242500000001;
    private const ulong Sender = 76561198000000001;

    [Fact]
    public void ChatAndMembersAreReadForOurAppOnly()
    {
        var handler = new LobbyMessageHandler(Game);
        var chats = new List<(ulong, ulong, byte[])>();
        var joined = new List<(ulong, ulong)>();
        var left = new List<(ulong, ulong)>();
        handler.ChatReceived += (lobby, sender, data) => chats.Add((lobby, sender, data));
        handler.MemberJoined += (lobby, member) => joined.Add((lobby, member));
        handler.MemberLeft += (lobby, member) => left.Add((lobby, member));

        foreach (uint app in new[] { Game, 480u })
        {
            handler.HandleMsg(Packet(EMsg.ClientMMSLobbyChatMsg, new ClientMsgProtobuf<CMsgClientMMSLobbyChatMsg>(EMsg.ClientMMSLobbyChatMsg)
            {
                Body = { app_id = app, steam_id_lobby = Lobby, steam_id_sender = Sender, lobby_message = [7, 8, 9] },
            }));
            handler.HandleMsg(Packet(EMsg.ClientMMSUserJoinedLobby, new ClientMsgProtobuf<CMsgClientMMSUserJoinedLobby>(EMsg.ClientMMSUserJoinedLobby)
            {
                Body = { app_id = app, steam_id_lobby = Lobby, steam_id_user = Sender, persona_name = "Desk" },
            }));
            handler.HandleMsg(Packet(EMsg.ClientMMSUserLeftLobby, new ClientMsgProtobuf<CMsgClientMMSUserLeftLobby>(EMsg.ClientMMSUserLeftLobby)
            {
                Body = { app_id = app, steam_id_lobby = Lobby, steam_id_user = Sender },
            }));
        }

        (ulong chatLobby, ulong chatSender, byte[] chatData) = Assert.Single(chats);
        Assert.Equal((Lobby, Sender), (chatLobby, chatSender));
        Assert.Equal(new byte[] { 7, 8, 9 }, chatData);
        Assert.Equal(new[] { (Lobby, Sender) }, joined);
        Assert.Equal(new[] { (Lobby, Sender) }, left);
    }

    [Fact]
    public void ChatGoesToOneMemberOrToAll()
    {
        CMsgClientMMSSendLobbyChatMsg toOne = LobbyMessageHandler.BuildChat(Game, Lobby, Sender, [1, 2]).Body;
        Assert.Equal((Game, Lobby, Sender), (toOne.app_id, toOne.steam_id_lobby, toOne.steam_id_target));
        Assert.Equal(new byte[] { 1, 2 }, toOne.lobby_message);

        ClientMsgProtobuf<CMsgClientMMSSendLobbyChatMsg> toAll = LobbyMessageHandler.BuildChat(Game, Lobby, 0, [3]);
        Assert.Equal(0ul, toAll.Body.steam_id_target);
        Assert.Equal(Game, toAll.ProtoHeader.routing_appid);
    }

    [Fact]
    public void SearchUsesQuickMatchsFiltersWorldwide()
    {
        var equal = new Dictionary<string, string> { ["qm_proto"] = "3", ["qm_version"] = "v0.111.0" };
        List<SteamMatchmaking.Lobby.Filter> filters = SteamKitLobbyService.Filters(equal, 2);

        Assert.Equal(4, filters.Count);
        var strings = filters.OfType<SteamMatchmaking.Lobby.StringFilter>().ToList();
        Assert.Equal(new[] { "qm_proto=3", "qm_version=v0.111.0" }, strings.Select(f => $"{f.Key}={f.Value}"));
        Assert.All(strings, f => Assert.Equal(ELobbyComparison.Equal, f.Comparison));
        Assert.Equal(2, Assert.Single(filters.OfType<SteamMatchmaking.Lobby.SlotsAvailableFilter>()).SlotsAvailable);
        Assert.Equal(ELobbyDistanceFilter.Worldwide, Assert.Single(filters.OfType<SteamMatchmaking.Lobby.DistanceFilter>()).Value);

        Assert.Empty(SteamKitLobbyService.Filters(equal, 0).OfType<SteamMatchmaking.Lobby.SlotsAvailableFilter>());
    }

    [Fact]
    public void JoinAnswersMapLikeTheIPhoneBackend()
    {
        Assert.Equal(LobbyJoinResult.Ok, AndroidLobbyBackend.Map(EChatRoomEnterResponse.Success));
        Assert.Equal(LobbyJoinResult.DoesNotExist, AndroidLobbyBackend.Map(EChatRoomEnterResponse.DoesntExist));
        Assert.Equal(LobbyJoinResult.Full, AndroidLobbyBackend.Map(EChatRoomEnterResponse.Full));
        Assert.Equal(LobbyJoinResult.Blocked, AndroidLobbyBackend.Map(EChatRoomEnterResponse.YouBlockedMember));
        Assert.Equal(LobbyJoinResult.RateLimited, AndroidLobbyBackend.Map((EChatRoomEnterResponse)15));
        Assert.Equal(LobbyJoinResult.Error, AndroidLobbyBackend.Map(EChatRoomEnterResponse.Error));
    }

    [Fact]
    public void SearchResultsCarryTheAskedValuesAndEmptyForMissingOnes()
    {
        var found = new[] { new LobbySummary(Lobby, 2, new Dictionary<string, string> { ["qm_size"] = "3", ["other"] = "x" }) };

        LobbyListing listing = Assert.Single(AndroidLobbyBackend.Listings(found, ["qm_size", "qm_key"]));

        Assert.Equal((Lobby, 2), (listing.Id, listing.Members));
        Assert.Equal(new Dictionary<string, string> { ["qm_size"] = "3", ["qm_key"] = "" }, listing.Data);
    }

    [Fact]
    public void AClosedLobbyIsPrivateAndGetsItsLatestTypeBack()
    {
        var owned = new OwnedLobby(ELobbyType.Public, 4);
        owned.Set("qm_size", "3");

        owned.SetJoinable(false);
        Assert.Equal(ELobbyType.Private, owned.Snapshot().Type);
        owned.SetType(ELobbyType.FriendsOnly);
        Assert.Equal(ELobbyType.Private, owned.Snapshot().Type);

        owned.SetJoinable(true);
        (ELobbyType type, Dictionary<string, string> data) = owned.Snapshot();
        Assert.Equal(ELobbyType.FriendsOnly, type);
        Assert.Equal("3", data["qm_size"]);
        Assert.True(owned.Delete("qm_size"));
        Assert.Equal("", owned.Get("qm_size"));
    }

    [Fact]
    public async Task UnavailableUntilSignedInAndRetriedAtMostEvery30Seconds()
    {
        var session = new OfflineSession();
        var log = new ListLog();
        var backend = new AndroidLobbyBackend(new SteamKitLobbyService(session, Game, log), log);

        Assert.False(backend.IsAvailable);
        await WaitUntil(() => log.Warnings.Count == 1);
        Assert.False(backend.IsAvailable);
        Assert.False(backend.IsAvailable);
        await Task.Delay(200);

        Assert.Equal(1, session.Connects);
        Assert.Contains("TimeoutException", Assert.Single(log.Warnings));
        Assert.Equal(0ul, backend.LocalId);
    }

    [Fact]
    public void ChatIsRefusedWhileNotConnected()
    {
        var log = new ListLog();
        var service = new SteamKitLobbyService(new OfflineSession(), Game, log);

        Assert.False(service.SendChat(Lobby, [1, 2, 3], 3, 0));
        Assert.Empty(service.Members(Lobby));
        Assert.Equal(0ul, service.Owner(Lobby));
        Assert.Equal("", service.GetData(Lobby, "qm_size"));
    }

    private static IPacketMsg Packet<T>(EMsg type, ClientMsgProtobuf<T> message)
        where T : ProtoBuf.IExtensible, new() => new PacketClientMsgProtobuf(type, message.Serialize());

    private static async Task WaitUntil(Func<bool> done)
    {
        for (int i = 0; i < 200 && !done(); i++)
        {
            await Task.Delay(10);
        }
        Assert.True(done());
    }

    private sealed class OfflineSession : ISteamSession
    {
        public int Connects;

        public SteamClient Client { get; } = new();

        public bool IsConnected => false;

        public void Connect()
        {
            Interlocked.Increment(ref Connects);
            throw new TimeoutException("Steam connection timed out");
        }

        public void HoldOpen()
        {
        }

        public void Release()
        {
        }
    }

    private sealed class ListLog : IModLog
    {
        private readonly object _lock = new();

        public List<string> Warnings { get; } = new();

        public void Info(string message)
        {
        }

        public void Warn(string message)
        {
            lock (_lock)
            {
                Warnings.Add(message);
            }
        }
    }
}
