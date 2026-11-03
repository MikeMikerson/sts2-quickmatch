using System.Reflection;
using QuickMatch.Lobby;
using QuickMatch.Steam;
using QuickMatch.Transport;
using Xunit;

namespace QuickMatch.Tests;

/// <summary>The lobby chat transport between a host and clients over <see cref="FakeSteam"/>.</summary>
public class TransportTests
{
    private const ulong HostId = 76561198000000102;
    private const ulong ClientId = 76561198000000101;
    private const ulong OtherClientId = 76561198000000001;

    private sealed class HostProbe : IChatHostEvents
    {
        public readonly List<ulong> Connected = new();
        public readonly List<(ulong Peer, ushort Reason, bool Self)> Disconnected = new();
        public readonly List<(ulong Peer, byte[] Data, bool Reliable)> Received = new();
        public Action<ulong>? OnConnected;

        public void PeerConnected(ulong peer)
        {
            Connected.Add(peer);
            OnConnected?.Invoke(peer);
        }

        public void PeerDisconnected(ulong peer, ushort reason, bool selfInitiated) => Disconnected.Add((peer, reason, selfInitiated));

        public void Data(ulong peer, byte[] data, bool reliable, byte channel) => Received.Add((peer, data, reliable));
    }

    private sealed class ClientProbe : IChatClientEvents
    {
        public readonly List<ushort> Disconnected = new();
        public readonly List<byte[]> Received = new();

        void IChatClientEvents.Disconnected(ushort reason) => Disconnected.Add(reason);

        void IChatClientEvents.Data(byte[] data, bool reliable, byte channel) => Received.Add(data);
    }

    private sealed class Session
    {
        public readonly FakeSteam Steam = new();
        public readonly HostProbe HostEvents = new();
        public readonly List<string> Log = new();
        public FakeBackend HostBackend = null!;
        public ChatHostCore Host = null!;
        public ulong Lobby;

        public static Session Hosting()
        {
            var s = new Session();
            s.HostBackend = s.Steam.Member(HostId);
            s.Host = new ChatHostCore(s.HostBackend, s.HostEvents, () => s.Steam.Now, 1111, m => s.Log.Add("host: " + m));
            s.Lobby = s.HostBackend.CreateAsync(LobbyKind.FriendsOnly, 4, default).Result!.Value;
            s.Host.Start(s.Lobby);
            s.Steam.OnTick(s.Host.Update);
            return s;
        }

        public (ChatClientCore Client, ClientProbe Events, FakeBackend Backend) NewClient(ulong id, bool canTarget = false)
        {
            FakeBackend backend = Steam.Member(id, canTarget);
            var events = new ClientProbe();
            var client = new ChatClientCore(backend, events, () => Steam.Now, (uint)(id % 100000) + 7, m => Log.Add($"client {id}: " + m));
            Steam.OnTick(client.Update);
            return (client, events, backend);
        }

        /// <summary>Connects as ChatClient does: the game is told, then messages flow.</summary>
        public ushort? Connect(ChatClientCore client, CancellationToken ct = default)
        {
            ushort? failure = ConnectOnly(client, ct);
            if (failure == null)
            {
                client.BeginDelivery();
            }
            return failure;
        }

        public ushort? ConnectOnly(ChatClientCore client, CancellationToken ct = default) =>
            client.ConnectAsync(Lobby, _ => { Steam.Advance(50); return Task.CompletedTask; }, ct).GetAwaiter().GetResult();
    }

    private static byte[] Message(int index, int size = 40)
    {
        var bytes = new byte[size];
        BitConverter.GetBytes(index).CopyTo(bytes, 0);
        for (int i = 4; i < size; i++)
        {
            bytes[i] = (byte)(index * 31 + i);
        }
        return bytes;
    }

    private static int MostInOneSecond(List<long> times) =>
        times.Count == 0 ? 0 : times.Max(start => times.Count(t => t >= start && t < start + 1000));

    private static bool CarriesWelcome(byte[] message)
    {
        int position = ChatFraming.HeaderSize;
        while (ChatFraming.TryReadFrame(message, ref position, out Frame frame))
        {
            if (frame.Kind == FrameKind.Welcome)
            {
                return true;
            }
        }
        return false;
    }

    [Fact]
    public void FramesRoundTrip()
    {
        var buffer = new byte[256];
        ChatFraming.WriteHeader(buffer, 0xA1B2C3D4, 77);
        int position = ChatFraming.HeaderSize;
        position += ChatFraming.WriteValueFrame(buffer.AsSpan(position), FrameKind.Hello, HostId, 1);
        position += ChatFraming.WriteWelcomeFrame(buffer.AsSpan(position), ClientId, 5);
        position += ChatFraming.WriteDataFrame(buffer.AsSpan(position), ClientId, reliable: true, channel: 1, total: 100, offset: 40, [9, 8, 7]);
        position += ChatFraming.WriteNackFrame(buffer.AsSpan(position), HostId, 1111, 3, 9);
        position += ChatFraming.WriteValueFrame(buffer.AsSpan(position), FrameKind.Bye, 0, ChatReasons.Quit);
        byte[] message = buffer.AsSpan(0, position).ToArray();

        Assert.True(ChatFraming.TryReadHeader(message, out uint session, out uint seq));
        Assert.Equal((0xA1B2C3D4u, 77u), (session, seq));
        int read = ChatFraming.HeaderSize;
        var frames = new List<Frame>();
        while (ChatFraming.TryReadFrame(message, ref read, out Frame frame))
        {
            frames.Add(frame);
        }
        Assert.Equal(message.Length, read);
        Assert.Equal([FrameKind.Hello, FrameKind.Welcome, FrameKind.Data, FrameKind.Nack, FrameKind.Bye], frames.Select(f => f.Kind));
        Assert.Equal((HostId, (ushort)1), (frames[0].Target, frames[0].Value));
        Assert.Equal((ClientId, 5u), (frames[1].Target, frames[1].StartSeq));
        Assert.True(frames[2].Reliable);
        Assert.Equal((1, 100, 40), ((int)frames[2].Channel, frames[2].Total, frames[2].Offset));
        Assert.Equal(new byte[] { 9, 8, 7 }, message.AsSpan(frames[2].PayloadStart, frames[2].PayloadLength).ToArray());
        Assert.Equal((1111u, 3u, 9u), (frames[3].NackSession, frames[3].NackFrom, frames[3].NackTo));
        Assert.Equal((0ul, ChatReasons.Quit), (frames[4].Target, frames[4].Value));
    }

    [Fact]
    public void MalformedInputIsRefused()
    {
        Assert.False(ChatFraming.TryReadHeader("hello world"u8, out _, out _));
        var buffer = new byte[64];
        ChatFraming.WriteHeader(buffer, 1, 1);
        int end = ChatFraming.HeaderSize + ChatFraming.WriteDataFrame(buffer.AsSpan(ChatFraming.HeaderSize), HostId, true, 0, 10, 0, [1, 2, 3]);
        // The payload length claims more bytes than the message holds.
        byte[] truncated = buffer.AsSpan(0, end - 2).ToArray();
        int position = ChatFraming.HeaderSize;
        Assert.False(ChatFraming.TryReadFrame(truncated, ref position, out _));
        // A piece may not reach past the message it belongs to.
        ChatFraming.WriteDataFrame(buffer.AsSpan(ChatFraming.HeaderSize), HostId, true, 0, 2, 0, [1, 2, 3]);
        position = ChatFraming.HeaderSize;
        Assert.False(ChatFraming.TryReadFrame(buffer.AsSpan(0, end).ToArray(), ref position, out _));
    }

    [Fact]
    public void ConnectsAndExchangesMessagesInOrder()
    {
        Session s = Session.Hosting();
        var (client, events, _) = s.NewClient(ClientId);

        Assert.Null(s.Connect(client));
        Assert.True(client.IsConnected);
        Assert.Equal(HostId, client.Host);
        s.Steam.Advance(1000);
        Assert.Equal([ClientId], s.HostEvents.Connected);

        for (int i = 0; i < 50; i++)
        {
            s.Host.Send(ClientId, Message(i), 40, reliable: true, channel: 0);
            client.Send(Message(1000 + i), 40, reliable: true, channel: 0);
            s.Steam.Advance(30);
        }
        s.Steam.Advance(3000);

        Assert.Equal(Enumerable.Range(0, 50).Select(i => Message(i)), events.Received);
        Assert.Equal(Enumerable.Range(1000, 50).Select(i => Message(i)), s.HostEvents.Received.Select(r => r.Data));
        Assert.All(s.HostEvents.Received, r => Assert.Equal(ClientId, r.Peer));
        Assert.Equal(0, s.Steam.RateDrops);
        Assert.Empty(s.HostEvents.Disconnected);
    }

    [Fact]
    public void HostsFirstMessageWaitsUntilTheGameKnowsTheConnection()
    {
        // The host's game starts its handshake as soon as a client connects, so its first message
        // comes with the Welcome. The client's game reads handshake messages only after it has been
        // told the connection is up; handed over earlier, the message is lost and the join times out.
        Session s = Session.Hosting();
        s.HostEvents.OnConnected = peer => s.Host.Send(peer, Message(1), 40, reliable: true, channel: 0);
        var (client, events, _) = s.NewClient(ClientId);

        Assert.Null(s.ConnectOnly(client));
        s.Steam.Advance(1000);
        Assert.Empty(events.Received);

        client.BeginDelivery();
        Assert.Equal([Message(1)], events.Received);
        s.Host.Send(ClientId, Message(2), 40, reliable: true, channel: 0);
        s.Steam.Advance(1000);
        Assert.Equal([Message(1), Message(2)], events.Received);
    }

    [Fact]
    public void AMessageRightAfterAnotherGoesAtOnce()
    {
        // A card played just after some other game message: with a fixed slot of one lobby message per
        // 100 ms it waited for the next slot on each side, on top of Steam's own delay.
        Session s = Session.Hosting();
        var (client, _, _) = s.NewClient(ClientId);
        Assert.Null(s.Connect(client));
        s.Steam.Advance(1000);
        int before = s.Steam.Sent[ClientId].Count;

        client.Send(Message(1), 40, reliable: true, channel: 0);
        s.Steam.Advance(10);
        client.Send(Message(2), 40, reliable: true, channel: 0);
        s.Steam.Advance(10);

        Assert.Equal(before + 2, s.Steam.Sent[ClientId].Count);
    }

    [Fact]
    public void BurstsStayUnderSteamsRate()
    {
        Session s = Session.Hosting();
        var (client, events, _) = s.NewClient(ClientId);
        Assert.Null(s.Connect(client));

        // A combat turn's worth of game messages at once, and more while they go out.
        for (int round = 0; round < 5; round++)
        {
            for (int i = 0; i < 60; i++)
            {
                int index = round * 60 + i;
                s.Host.Send(ClientId, Message(index), 40, reliable: true, channel: 0);
                client.Send(Message(10_000 + index), 40, reliable: true, channel: 0);
            }
            s.Steam.Advance(200);
        }
        s.Steam.Advance(5000);

        Assert.Equal(0, s.Steam.RateDrops);
        int ceiling = ChatLink.BurstMessages + (int)(1000 / ChatLink.MinSendIntervalMs);
        Assert.InRange(MostInOneSecond(s.Steam.Sent[HostId]), 1, ceiling);
        Assert.InRange(MostInOneSecond(s.Steam.Sent[ClientId]), 1, ceiling);
        Assert.True(ceiling < 17, "below the rate at which Steam drops lobby messages");
        Assert.Equal(300, events.Received.Count);
        Assert.Equal(Enumerable.Range(0, 300).Select(i => Message(i)), events.Received);
        Assert.Equal(Enumerable.Range(10_000, 300).Select(i => Message(i)), s.HostEvents.Received.Select(r => r.Data));
    }

    [Fact]
    public void LargeMessagesArriveWholeAndInOrder()
    {
        Session s = Session.Hosting();
        var (client, events, _) = s.NewClient(ClientId);
        Assert.Null(s.Connect(client));

        s.Host.Send(ClientId, Message(1, 10), 10, true, 0);
        s.Host.Send(ClientId, Message(2, 300_000), 300_000, true, 0);
        s.Host.Send(ClientId, Message(3, 10), 10, true, 0);
        s.Host.Send(ClientId, Message(4, ChatLink.MaxMessageBytes), ChatLink.MaxMessageBytes, true, 0);
        s.Steam.Advance(6000);

        Assert.Equal([Message(1, 10), Message(2, 300_000), Message(3, 10), Message(4, ChatLink.MaxMessageBytes)], events.Received);
        Assert.Equal(0, s.Steam.RateDrops);
    }

    [Fact]
    public void LostLobbyMessagesAreSentAgain()
    {
        Session s = Session.Hosting();
        var (client, events, _) = s.NewClient(ClientId);
        Assert.Null(s.Connect(client));
        s.Steam.Advance(500);

        // From here on Steam loses every third lobby message of either side.
        s.Steam.Lose = (_, index, _) => index % 3 == 2;
        for (int i = 0; i < 80; i++)
        {
            s.Host.Send(ClientId, Message(i), 40, true, 0);
            client.Send(Message(500 + i), 40, true, 0);
            s.Steam.Advance(60);
        }
        s.Steam.Advance(8000);

        Assert.Equal(Enumerable.Range(0, 80).Select(i => Message(i)), events.Received);
        Assert.Equal(Enumerable.Range(500, 80).Select(i => Message(i)), s.HostEvents.Received.Select(r => r.Data));
        Assert.Empty(events.Disconnected);
        Assert.Empty(s.HostEvents.Disconnected);
        Assert.Contains(s.Log, line => line.Contains("asking again"));
    }

    [Fact]
    public void ALostWelcomeDoesNotSkipWhatFollowedIt()
    {
        Session s = Session.Hosting();
        // The game greets a new client at once (its handshake); that lands in the Welcome's message.
        s.HostEvents.OnConnected = peer => s.Host.Send(peer, Message(42), 40, true, 0);
        bool dropped = false;
        s.Steam.Lose = (sender, _, bytes) =>
        {
            if (sender != HostId || dropped || !CarriesWelcome(bytes))
            {
                return false;
            }
            return dropped = true;
        };
        var (client, events, _) = s.NewClient(ClientId);

        Assert.Null(s.Connect(client));
        s.Steam.Advance(4000);

        Assert.True(dropped);
        Assert.Equal([Message(42)], events.Received);
    }

    [Fact]
    public void KickReachesTheClient()
    {
        Session s = Session.Hosting();
        var (client, events, backend) = s.NewClient(ClientId);
        Assert.Null(s.Connect(client));
        s.Steam.Advance(500);

        s.Host.Send(ClientId, Message(7), 40, true, 0);
        s.Host.Disconnect(ClientId, 4);
        s.Steam.Advance(2000);

        Assert.Equal([Message(7)], events.Received);
        Assert.Equal([(ushort)4], events.Disconnected);
        Assert.Equal([(ClientId, (ushort)4, true)], s.HostEvents.Disconnected);
        Assert.Contains(s.Lobby, backend.Left);
        Assert.False(client.IsConnected);
    }

    [Fact]
    public void AClientThatQuitsTellsTheHost()
    {
        Session s = Session.Hosting();
        var (client, events, _) = s.NewClient(ClientId);
        Assert.Null(s.Connect(client));
        s.Steam.Advance(500);

        client.Send(Message(8), 40, true, 0);
        client.Disconnect(ChatReasons.Quit);
        s.Steam.Advance(3000);

        Assert.Equal([Message(8)], s.HostEvents.Received.Select(r => r.Data));
        Assert.Equal([(ClientId, ChatReasons.Quit, false)], s.HostEvents.Disconnected);
        Assert.Empty(events.Disconnected);
    }

    [Fact]
    public void AStoppingHostTellsEveryClient()
    {
        Session s = Session.Hosting();
        var (a, aEvents, _) = s.NewClient(ClientId);
        var (b, bEvents, _) = s.NewClient(OtherClientId);
        Assert.Null(s.Connect(a));
        Assert.Null(s.Connect(b));
        s.Steam.Advance(500);

        s.Host.Stop(3);
        s.Steam.Advance(3000);

        Assert.Equal([(ushort)3], aEvents.Disconnected);
        Assert.Equal([(ushort)3], bEvents.Disconnected);
        Assert.Contains(s.Lobby, s.HostBackend.Left);
    }

    [Fact]
    public void AMemberWhoLeavesWithoutAGoodbyeIsDropped()
    {
        Session s = Session.Hosting();
        var (client, _, backend) = s.NewClient(ClientId);
        Assert.Null(s.Connect(client));
        s.Steam.Advance(500);

        // The client's game is gone: Steam takes it out of the lobby, nothing else arrives.
        backend.Leave(s.Lobby);
        s.Steam.Advance(1000);
        Assert.Empty(s.HostEvents.Disconnected);
        s.Steam.Advance(2000);

        Assert.Equal([(ClientId, ChatReasons.Timeout, false)], s.HostEvents.Disconnected);
    }

    [Fact]
    public void ASilentPeerTimesOut()
    {
        Session s = Session.Hosting();
        var (client, events, _) = s.NewClient(ClientId);
        Assert.Null(s.Connect(client));
        s.Steam.Advance(500);

        s.Steam.Lose = (sender, _, _) => sender == HostId;
        s.Steam.Advance(ChatLink.PeerTimeoutMs - 3000);
        Assert.Empty(events.Disconnected);
        s.Steam.Advance(4000);

        Assert.Equal([ChatReasons.Timeout], events.Disconnected);
    }

    [Theory]
    // 2 is QuickMatch 0.2.0's protocol: a 0.2.0 player who gets into a 0.3.0 host's lobby is turned away.
    [InlineData(2)]
    [InlineData(ChatHostCore.Protocol + 1)]
    public async Task AnotherProtocolIsRejected(int protocol)
    {
        Session s = Session.Hosting();
        FakeBackend backend = s.Steam.Member(ClientId);
        var heard = new LinkRecorder();
        var link = new ChatLink(ClientId, 5, heard);
        backend.Changed += e => { if (e.Kind == LobbyEventKind.Chat) link.Receive(e.Member, e.Data!, s.Steam.Now); };
        await backend.JoinAsync(s.Lobby, default);
        link.QueueHello(HostId, (ushort)protocol, s.Steam.Now);
        s.Steam.OnTick(() => link.Tick(s.Steam.Now, (m, l, t) => backend.SendChat(s.Lobby, m, l, t)));
        s.Steam.Advance(2000);

        Assert.Equal([(HostId, ChatReasons.VersionMismatch)], heard.Rejected);
        Assert.Empty(s.HostEvents.Connected);
    }

    [Fact]
    public void EachReleaseMatchesOnlyItself()
    {
        // The lobby markers searches filter on, and the connection protocol, of each release. A release
        // that cannot play with the ones before it gets a row of new values.
        var releases = new Dictionary<string, (string Proto, string ResumeProto, ushort Protocol)>
        {
            ["v0.2.0"] = ("4", "3", 2),
            ["v0.3.0"] = ("5", "4", 3),
        };
        string manifest = typeof(TransportTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == "QuickMatchManifest").Value!;
        string version = System.Text.Json.JsonDocument.Parse(File.ReadAllText(manifest)).RootElement.GetProperty("version").GetString()!;

        Assert.True(releases.TryGetValue(version, out var current), $"QuickMatch.json says {version}: give it a row here");
        Assert.Equal(current, (LobbyKeys.ProtoValue, LobbyKeys.ResumeProtoValue, ChatHostCore.Protocol));
        foreach ((string other, var values) in releases.Where(r => r.Key != version))
        {
            Assert.True(values.Proto != current.Proto && values.ResumeProto != current.ResumeProto && values.Protocol != current.Protocol,
                $"{version} would find or accept {other} players");
        }
    }

    [Fact]
    public async Task NoAnswerTimesOut()
    {
        var steam = new FakeSteam();
        FakeBackend owner = steam.Member(HostId);
        ulong lobby = (await owner.CreateAsync(LobbyKind.FriendsOnly, 4, default))!.Value;
        FakeBackend backend = steam.Member(ClientId);
        var client = new ChatClientCore(backend, new ClientProbe(), () => steam.Now, 9);
        long started = steam.Now;

        ushort? result = await client.ConnectAsync(lobby, _ => { steam.Advance(50); return Task.CompletedTask; }, default);

        Assert.Equal(ChatReasons.Timeout, result);
        Assert.InRange(steam.Now - started, ChatClientCore.ConnectTimeoutMs, ChatClientCore.ConnectTimeoutMs + 200);
        Assert.Contains(lobby, backend.Left);
    }

    [Fact]
    public void CancellingAJoinLeavesTheLobby()
    {
        Session s = Session.Hosting();
        // A host that never answers, so the join is still waiting when it is cancelled.
        s.Steam.Lose = (sender, _, _) => sender == HostId;
        var (client, _, backend) = s.NewClient(ClientId);
        using var cts = new CancellationTokenSource();
        long started = s.Steam.Now;

        Assert.ThrowsAny<OperationCanceledException>(() => client.ConnectAsync(s.Lobby, ct =>
        {
            s.Steam.Advance(50);
            if (s.Steam.Now - started > 3000)
            {
                cts.Cancel();
            }
            return Task.CompletedTask;
        }, cts.Token).GetAwaiter().GetResult());

        Assert.Contains(s.Lobby, backend.Left);
    }

    [Fact]
    public void TargetingClientsKeepToThemselves()
    {
        Session s = Session.Hosting();
        var (a, aEvents, _) = s.NewClient(ClientId, canTarget: true);
        var (b, bEvents, bBackend) = s.NewClient(OtherClientId, canTarget: true);
        int heardFromA = 0;
        bBackend.Changed += e => { if (e.Kind == LobbyEventKind.Chat && e.Member == ClientId) heardFromA++; };
        Assert.Null(s.Connect(a));
        Assert.Null(s.Connect(b));
        s.Steam.Advance(500);

        for (int i = 0; i < 10; i++)
        {
            s.Host.Send(ClientId, Message(i), 40, true, 0);
            s.Host.Send(OtherClientId, Message(100 + i), 40, true, 0);
            a.Send(Message(200 + i), 40, true, 0);
        }
        s.Steam.Advance(3000);

        Assert.Equal(Enumerable.Range(0, 10).Select(i => Message(i)), aEvents.Received);
        Assert.Equal(Enumerable.Range(100, 10).Select(i => Message(i)), bEvents.Received);
        Assert.Equal(10, s.HostEvents.Received.Count(r => r.Peer == ClientId));
        Assert.Equal(0, heardFromA);
    }

    [Fact]
    public void ReasonsAreTheGamesNetErrorValues()
    {
        string dataPath = typeof(TransportTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == "Sts2DataPath").Value!;
        string game = Path.Combine(dataPath, "sts2.dll");
        Assert.True(File.Exists(game), $"game not found at {dataPath}");
        using var context = new MetadataLoadContext(new PathAssemblyResolver(Directory.GetFiles(dataPath, "*.dll")), "System.Private.CoreLib");
        Type netError = context.LoadFromAssemblyPath(game).GetType("MegaCrit.Sts2.Core.Entities.Multiplayer.NetError", throwOnError: true)!;

        foreach (FieldInfo reason in typeof(ChatReasons).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            FieldInfo? gameValue = netError.GetField(reason.Name);
            Assert.True(gameValue != null, $"NetError.{reason.Name} is not in the installed game");
            Assert.Equal(Convert.ToInt32(gameValue!.GetRawConstantValue()), Convert.ToInt32(reason.GetValue(null)));
        }
    }

    private sealed class LinkRecorder : IChatLinkEvents
    {
        public readonly List<(ulong, ushort)> Rejected = new();

        public bool OnHello(ulong sender, ushort protocol) => false;

        public bool OnWelcome(ulong sender) => false;

        public void OnReject(ulong sender, ushort reason) => Rejected.Add((sender, reason));

        public void OnBye(ulong sender, ushort reason)
        {
        }

        public void OnData(ulong sender, byte[] data, bool reliable, byte channel)
        {
        }

        public void OnLost(ulong peer, LinkLoss why)
        {
        }

        public void OnRelayOffer(ulong sender, string region, byte[] key)
        {
        }

        public void OnRelayVerdict(ulong sender, ushort verdict)
        {
        }

        public void OnRelayKeys(ulong sender, ulong member, ulong tag, byte[] messageKey, byte[] voiceKey)
        {
        }
    }
}
