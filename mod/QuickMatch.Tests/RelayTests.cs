using QuickMatch.Lobby;
using QuickMatch.Transport;
using QuickMatch.Voice;
using Xunit;

namespace QuickMatch.Tests;

/// <summary>The lobby chat transport with the relay route, over <see cref="FakeSteam"/> and <see cref="FakeRelay"/>.</summary>
public class RelayTests
{
    private const ulong HostId = 76561198000000102;
    private const ulong ClientId = 76561198000000101;
    private const ulong OtherClientId = 76561198000000001;
    private const ulong StrangerId = 76561198000000099;

    private sealed class HostProbe : IChatHostEvents
    {
        public readonly List<(ulong Peer, ushort Reason, bool Self)> Disconnected = new();
        public readonly List<(ulong Peer, byte[] Data)> Received = new();
        public Action<ulong, byte[]>? OnData;

        public void PeerConnected(ulong peer)
        {
        }

        public void PeerDisconnected(ulong peer, ushort reason, bool selfInitiated) => Disconnected.Add((peer, reason, selfInitiated));

        public void Data(ulong peer, byte[] data, bool reliable, byte channel)
        {
            Received.Add((peer, data));
            OnData?.Invoke(peer, data);
        }
    }

    private sealed class ClientProbe : IChatClientEvents
    {
        public readonly List<ushort> Disconnected = new();
        public readonly List<byte[]> Received = new();

        void IChatClientEvents.Disconnected(ushort reason) => Disconnected.Add(reason);

        void IChatClientEvents.Data(byte[] data, bool reliable, byte channel) => Received.Add(data);
    }

    private sealed class Match
    {
        public readonly FakeSteam Steam = new();
        public readonly FakeRelay Relay;
        public readonly HostProbe HostEvents = new();
        public readonly List<string> Log = new();
        public ChatHostCore Host = null!;
        public ulong Lobby;
        private readonly Dictionary<ChatClientCore, ulong> _ids = new(ReferenceEqualityComparer.Instance);

        private Match() => Relay = new FakeRelay(Steam);

        public static Match Hosting(bool relay = true)
        {
            var m = new Match();
            FakeBackend backend = m.Steam.Member(HostId);
            m.Host = new ChatHostCore(backend, m.HostEvents, () => m.Steam.Now, 1111, line => m.Log.Add("host: " + line),
                relay ? m.Relay.Transport() : null);
            m.Lobby = backend.CreateAsync(LobbyKind.FriendsOnly, 4, default).Result!.Value;
            m.Host.Start(m.Lobby);
            m.Steam.OnTick(m.Host.Update);
            return m;
        }

        public (ChatClientCore Client, ClientProbe Events) Join(ulong id, bool relay = true)
        {
            FakeBackend backend = Steam.Member(id);
            var events = new ClientProbe();
            var client = new ChatClientCore(backend, events, () => Steam.Now, (uint)(id % 100000) + 7, line => Log.Add($"client {id}: " + line),
                relay ? Relay.Transport() : null);
            Steam.OnTick(client.Update);
            ushort? failure = client.ConnectAsync(Lobby, _ => { Steam.Advance(50); return Task.CompletedTask; }, default).GetAwaiter().GetResult();
            Assert.Null(failure);
            client.BeginDelivery();
            _ids[client] = id;
            return (client, events);
        }

        /// <summary>Game traffic both ways, one message each way every 100 ms, as in a combat.</summary>
        public void Chatter(long ms, params ChatClientCore[] clients)
        {
            for (long t = 0; t < ms; t += 100)
            {
                foreach (ChatClientCore client in clients)
                {
                    Host.Send(_ids[client], Message(-1), 40, reliable: true, channel: 0);
                    client.Send(Message(-2), 40, reliable: true, channel: 0);
                }
                Steam.Advance(100);
            }
        }

        public int LogCount(string text) => Log.Count(line => line.Contains(text));

        public void ClearReceived(params ClientProbe[] clients)
        {
            HostEvents.Received.Clear();
            foreach (ClientProbe client in clients)
            {
                client.Received.Clear();
            }
        }

        /// <summary>Simulated time until <paramref name="done"/> holds, checked every 10 ms.</summary>
        public long Until(Func<bool> done, long limitMs = 5000)
        {
            long started = Steam.Now;
            while (!done())
            {
                Assert.True(Steam.Now - started < limitMs, $"not done after {limitMs} ms");
                Steam.Advance(10);
            }
            return Steam.Now - started;
        }
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

    private static List<int> Numbers(IEnumerable<byte[]> messages) =>
        messages.Select(m => BitConverter.ToInt32(m, 0)).Where(n => n >= 0).ToList();

    /// <summary>Plays until both sides take each other's messages over the relay only.</summary>
    private static void UntilFast(Match m, ChatClientCore client)
    {
        for (int i = 0; i < 100 && !(m.Host.AllOnRelay && client.OnRelay); i++)
        {
            m.Chatter(100, client);
        }
        Assert.True(m.Host.AllOnRelay && client.OnRelay, string.Join("\n", m.Log));
    }

    [Fact]
    public void RelayFramesRoundTrip()
    {
        byte[] key = RelayCrypto.NewKey();
        var buffer = new byte[128];
        ChatFraming.WriteHeader(buffer, 5, 9);
        int end = ChatFraming.HeaderSize;
        end += ChatFraming.WriteRelayOfferFrame(buffer.AsSpan(end), ClientId, "apac", key);
        end += ChatFraming.WriteValueFrame(buffer.AsSpan(end), FrameKind.RelayVerdict, HostId, RelayVerdict.ChatFirst);
        byte[] message = buffer.AsSpan(0, end).ToArray();

        int position = ChatFraming.HeaderSize;
        Assert.True(ChatFraming.TryReadFrame(message, ref position, out Frame offer));
        Assert.Equal((FrameKind.RelayOffer, ClientId), (offer.Kind, offer.Target));
        (string region, byte[] read) = ChatFraming.ReadRelayOffer(message, offer);
        Assert.Equal("apac", region);
        Assert.Equal(key, read);
        Assert.True(ChatFraming.TryReadFrame(message, ref position, out Frame verdict));
        Assert.Equal((FrameKind.RelayVerdict, HostId, RelayVerdict.ChatFirst), (verdict.Kind, verdict.Target, verdict.Value));
        Assert.Equal(message.Length, position);

        // A region name is lower case letters only, since it becomes part of the relay's address.
        message[ChatFraming.HeaderSize + 10] = (byte)'A';
        position = ChatFraming.HeaderSize;
        Assert.False(ChatFraming.TryReadFrame(message, ref position, out _));
        message[ChatFraming.HeaderSize + 10] = (byte)'/';
        position = ChatFraming.HeaderSize;
        Assert.False(ChatFraming.TryReadFrame(message, ref position, out _));
    }

    [Fact]
    public void RelayEncryptionHidesAndProtectsMessages()
    {
        byte[] key = RelayCrypto.NewKey();
        using var sender = new RelayCrypto(key);
        using var receiver = new RelayCrypto(key);
        using var outsider = new RelayCrypto(RelayCrypto.NewKey());
        byte[] message = LobbyMessage(seq: 3, Message(42, 200));
        byte[] next = LobbyMessage(seq: 4, Message(42, 200));

        byte[] sealedMessage = sender.Seal(message, message.Length);
        Assert.Equal(message, receiver.Open(sealedMessage));
        Assert.Equal(message.Length + RelayCrypto.TagBytes, sealedMessage.Length);
        // The relay sees the header (session and sequence number), not the frames.
        Assert.Equal(message.AsSpan(0, ChatFraming.HeaderSize).ToArray(), sealedMessage.AsSpan(0, ChatFraming.HeaderSize).ToArray());
        Assert.False(message.AsSpan(ChatFraming.HeaderSize).SequenceEqual(sealedMessage.AsSpan(ChatFraming.HeaderSize, message.Length - ChatFraming.HeaderSize)));
        // A resend is byte for byte the same lobby message and encrypts the same way.
        Assert.Equal(sealedMessage, sender.Seal(message, message.Length));
        // The same frames under another sequence number do not repeat the key stream.
        byte[] sealedNext = sender.Seal(next, next.Length);
        Assert.False(sealedMessage.AsSpan(ChatFraming.HeaderSize).SequenceEqual(sealedNext.AsSpan(ChatFraming.HeaderSize)));
        // Each player encrypts with its own key: the same message from another player differs.
        Assert.False(sealedMessage.AsSpan(ChatFraming.HeaderSize).SequenceEqual(outsider.Seal(message, message.Length).AsSpan(ChatFraming.HeaderSize)));

        Assert.Null(outsider.Open(sealedMessage));
        foreach (int index in new[] { 3, 8, ChatFraming.HeaderSize + 5, sealedMessage.Length - 1 })
        {
            byte[] changed = (byte[])sealedMessage.Clone();
            changed[index] ^= 1;
            Assert.Null(receiver.Open(changed));
        }
        Assert.Null(receiver.Open(sealedMessage.AsSpan(0, 20).ToArray()));

        byte[] roomKey = RelayCrypto.NewKey();
        Assert.Equal(32, RelayCrypto.RoomToken(roomKey).Length);
        Assert.Equal(RelayCrypto.RoomToken(roomKey), RelayCrypto.RoomToken((byte[])roomKey.Clone()));
        Assert.NotEqual(RelayCrypto.RoomToken(roomKey), RelayCrypto.RoomToken(key));
    }

    [Fact]
    public void RelayKeysFramesRoundTrip()
    {
        byte[] messageKey = RelayCrypto.NewKey();
        byte[] voiceKey = VoiceCrypto.NewKey();
        var buffer = new byte[128];
        int end = ChatFraming.HeaderSize;
        end += ChatFraming.WriteRelayKeysFrame(buffer.AsSpan(end), ClientId, HostId, 0x0102030405060708, messageKey, voiceKey);

        int position = ChatFraming.HeaderSize;
        Assert.True(ChatFraming.TryReadFrame(buffer.AsSpan(0, end), ref position, out Frame frame));
        Assert.Equal((FrameKind.RelayKeys, ClientId, HostId, 0x0102030405060708ul), (frame.Kind, frame.Target, frame.Member, frame.Tag));
        (byte[] readMessageKey, byte[] readVoiceKey) = ChatFraming.ReadRelayKeys(buffer, frame);
        Assert.Equal(messageKey, readMessageKey);
        Assert.Equal(voiceKey, readVoiceKey);
        Assert.Equal(end, position);
        position = ChatFraming.HeaderSize;
        Assert.False(ChatFraming.TryReadFrame(buffer.AsSpan(0, end - 1), ref position, out _));
    }

    [Fact]
    public void RelayCarriesTheMatchOnceItIsFaster()
    {
        Match m = Match.Hosting();
        var (client, events) = m.Join(ClientId);

        UntilFast(m, client);

        Assert.Equal(1, m.LogCount($"host: fast link up via relay for {ClientId}"));
        Assert.Equal(1, m.LogCount($"client {ClientId}: fast link up via relay for {HostId}"));
        Assert.True(m.Host.Link.FastPacing);
        Assert.True(client.Link.FastPacing);

        // A client's card: its request reaches the host, the host's answer comes back.
        m.Steam.Advance(1000);
        m.ClearReceived(events);
        m.HostEvents.OnData = (peer, data) => m.Host.Send(peer, data, data.Length, reliable: true, channel: 0);
        client.Send(Message(1), 40, reliable: true, channel: 0);
        long roundTrip = m.Until(() => events.Received.Any(d => d.SequenceEqual(Message(1))));
        Assert.InRange(roundTrip, 1, 150);
        m.HostEvents.OnData = null;

        // Lobby chat copies stop while the relay carries everything, and nothing is lost or reordered.
        m.Steam.Advance(500);
        m.ClearReceived(events);
        int hostChat = m.Steam.Sent[HostId].Count;
        int clientChat = m.Steam.Sent[ClientId].Count;
        for (int i = 0; i < 60; i++)
        {
            m.Host.Send(ClientId, Message(i), 40, reliable: true, channel: 0);
            client.Send(Message(1000 + i), 40, reliable: true, channel: 0);
            m.Steam.Advance(i % 3 == 0 ? 10 : 50);
        }
        m.Steam.Advance(1000);

        Assert.Equal(Enumerable.Range(0, 60), Numbers(events.Received));
        Assert.Equal(Enumerable.Range(1000, 60), Numbers(m.HostEvents.Received.Select(r => r.Data)));
        Assert.Equal(hostChat, m.Steam.Sent[HostId].Count);
        Assert.Equal(clientChat, m.Steam.Sent[ClientId].Count);
        Assert.Empty(m.HostEvents.Disconnected);
        Assert.Empty(events.Disconnected);
    }

    [Fact]
    public void ALobbyChatRoundTripTakesTwoSteamDeliveries()
    {
        // The same card without the relay, for comparison with the test above.
        Match m = Match.Hosting(relay: false);
        var (client, events) = m.Join(ClientId);
        m.Chatter(3000, client);
        m.Steam.Advance(1000);
        m.ClearReceived(events);
        m.HostEvents.OnData = (peer, data) => m.Host.Send(peer, data, data.Length, reliable: true, channel: 0);

        client.Send(Message(1), 40, reliable: true, channel: 0);
        long roundTrip = m.Until(() => events.Received.Any(d => d.SequenceEqual(Message(1))));

        Assert.InRange(roundTrip, 2 * FakeSteam.LatencyMs, 2 * FakeSteam.LatencyMs + 100);
        Assert.Equal(0, m.Relay.Opened);
    }

    [Fact]
    public void APlayerWithoutTheRelayKeepsTheMatchOnLobbyChat()
    {
        Match m = Match.Hosting();
        var (client, events) = m.Join(ClientId, relay: false);
        m.Chatter(5000, client);
        m.ClearReceived(events);

        for (int i = 0; i < 30; i++)
        {
            m.Host.Send(ClientId, Message(i), 40, reliable: true, channel: 0);
            client.Send(Message(1000 + i), 40, reliable: true, channel: 0);
            m.Steam.Advance(100);
        }
        m.Steam.Advance(2000);

        Assert.Equal(Enumerable.Range(0, 30), Numbers(events.Received));
        Assert.Equal(Enumerable.Range(1000, 30), Numbers(m.HostEvents.Received.Select(r => r.Data)));
        Assert.False(m.Host.AllOnRelay);
        Assert.False(m.Host.Link.FastPacing);
        // The host opened the room and offered it; nobody came, so it sent nothing over the relay.
        Assert.Equal(1, m.Relay.Opened);
        Assert.Equal(0, m.Relay.DataFrames);
        Assert.Equal(0, m.LogCount("fast link up"));
    }

    [Fact]
    public void AnUnreachableRelayEndsInLobbyChat()
    {
        Match m = Match.Hosting();
        m.Relay.Refuse = true;
        var (client, events) = m.Join(ClientId);
        m.Chatter(30000, client);
        m.ClearReceived(events);

        for (int i = 0; i < 30; i++)
        {
            m.Host.Send(ClientId, Message(i), 40, reliable: true, channel: 0);
            client.Send(Message(1000 + i), 40, reliable: true, channel: 0);
            m.Steam.Advance(100);
        }
        m.Steam.Advance(2000);

        Assert.Equal(Enumerable.Range(0, 30), Numbers(events.Received));
        Assert.Equal(Enumerable.Range(1000, 30), Numbers(m.HostEvents.Received.Select(r => r.Data)));
        // The host tried its connections and gave up; without a room there was nothing to offer.
        Assert.Equal(RelayRoute.MaxConnects, m.Relay.Opened);
        Assert.Equal(RelayRoute.MaxConnects, m.LogCount("host: relay connection closed: connection refused"));
        Assert.Equal(0, m.LogCount($"client {ClientId}: relay"));
        Assert.Empty(m.HostEvents.Disconnected);
    }

    [Fact]
    public void LosingTheRelayMidBurstLosesNothing()
    {
        Match m = Match.Hosting();
        var (client, events) = m.Join(ClientId);
        UntilFast(m, client);
        m.Steam.Advance(500);
        m.ClearReceived(events);

        for (int i = 0; i < 100; i++)
        {
            m.Host.Send(ClientId, Message(i), 40, reliable: true, channel: 0);
            client.Send(Message(1000 + i), 40, reliable: true, channel: 0);
            if (i == 50)
            {
                m.Relay.CloseAll("relay restarted");
            }
            m.Steam.Advance(30);
        }
        m.Steam.Advance(5000);

        Assert.Equal(Enumerable.Range(0, 100), Numbers(events.Received));
        Assert.Equal(Enumerable.Range(1000, 100), Numbers(m.HostEvents.Received.Select(r => r.Data)));
        Assert.Empty(m.HostEvents.Disconnected);
        Assert.Empty(events.Disconnected);
        Assert.Equal(1, m.LogCount("host: relay connection closed: relay restarted"));
        Assert.Equal(1, m.LogCount($"client {ClientId}: relay connection closed: relay restarted"));

        // Both connect again after a moment, and the fast link comes back.
        UntilFast(m, client);
        Assert.Equal(4, m.Relay.Opened);
        Assert.Equal(2, m.LogCount($"host: fast link up via relay for {ClientId}"));
        Assert.Equal(2, m.LogCount($"client {ClientId}: fast link up via relay for {HostId}"));
    }

    [Fact]
    public void ASilentRelayIsNoticedAndTheMatchGoesOn()
    {
        Match m = Match.Hosting();
        var (client, events) = m.Join(ClientId);
        UntilFast(m, client);
        m.Steam.Advance(500);
        m.ClearReceived(events);

        // The connections stay open but nothing gets through.
        m.Relay.BlackHole = true;
        for (int i = 0; i < 40; i++)
        {
            m.Host.Send(ClientId, Message(i), 40, reliable: true, channel: 0);
            client.Send(Message(1000 + i), 40, reliable: true, channel: 0);
            m.Steam.Advance(100);
        }
        m.Steam.Advance(RelayRoute.StaleMs + 5000);

        Assert.Equal(Enumerable.Range(0, 40), Numbers(events.Received));
        Assert.Equal(Enumerable.Range(1000, 40), Numbers(m.HostEvents.Received.Select(r => r.Data)));
        Assert.Empty(m.HostEvents.Disconnected);
        Assert.Empty(events.Disconnected);
        Assert.False(m.Host.AllOnRelay);
        Assert.False(client.OnRelay);
        Assert.True(m.LogCount("over the relay for") >= 1, string.Join("\n", m.Log));

        // Once messages get through again, the relay takes over again.
        m.Relay.BlackHole = false;
        UntilFast(m, client);
    }

    [Fact]
    public void ARelaySlowerThanLobbyChatIsNotUsed()
    {
        Match m = Match.Hosting();
        m.Relay.UpMs = 300;
        m.Relay.DownMs = 300;
        var (client, events) = m.Join(ClientId);
        m.Chatter(10000, client);

        Assert.Equal(1, m.LogCount($"host: relay slower than lobby chat for {ClientId}"));
        Assert.Equal(1, m.LogCount($"client {ClientId}: relay slower than lobby chat for {HostId}"));
        Assert.False(m.Host.AllOnRelay);
        Assert.False(client.OnRelay);

        // Neither side sends relay copies any more.
        m.Steam.Advance(1000);
        int frames = m.Relay.DataFrames;
        m.ClearReceived(events);
        m.Chatter(3000, client);
        m.Steam.Advance(1000);
        Assert.Equal(frames, m.Relay.DataFrames);
        Assert.Equal(30, events.Received.Count(d => BitConverter.ToInt32(d, 0) == -1));
    }

    [Fact]
    public void TamperedAndForeignRelayMessagesAreDropped()
    {
        Match m = Match.Hosting();
        // The relay changes one byte of everything the host sends through it.
        m.Relay.Tamper = (sender, payload) =>
        {
            if (sender != HostId)
            {
                return payload;
            }
            byte[] changed = (byte[])payload.Clone();
            changed[^1] ^= 1;
            return changed;
        };
        var (client, events) = m.Join(ClientId);
        m.Chatter(3000, client);
        m.ClearReceived(events);

        for (int i = 0; i < 30; i++)
        {
            m.Host.Send(ClientId, Message(i), 40, reliable: true, channel: 0);
            m.Steam.Advance(100);
            if (i == 10)
            {
                // A stranger in the room, and a message claiming to be the host's under another key.
                m.Relay.Inject(StrangerId, Message(500, 100));
                byte[] forged = LobbyMessage(seq: 9999, Message(501));
                using var other = new RelayCrypto(RelayCrypto.NewKey());
                m.Relay.Inject(HostId, other.Seal(forged, forged.Length));
            }
        }
        m.Steam.Advance(2000);

        Assert.Equal(Enumerable.Range(0, 30), Numbers(events.Received));
        Assert.Equal(Enumerable.Range(0, 30).Select(i => Message(i)), events.Received.Where(d => BitConverter.ToInt32(d, 0) >= 0));
        Assert.Equal(1, m.LogCount($"client {ClientId}: relay: dropped a message from {HostId} that did not come from the connection and key it announced"));
        // The client never saw the host's relay copies, so the host keeps sending over lobby chat.
        Assert.False(m.Host.AllOnRelay);
        Assert.Empty(events.Disconnected);
    }

    private static VoicePacket Voice(uint first) => new(9, first, StartsTalk: first == 0, EndsTalk: false, new[] { new byte[] { 1, 2 }, new byte[] { 3 }, new byte[] { 4, 5, 6 } });

    [Fact]
    public void VoiceReachesEveryOtherMemberDirectlyAndNeverTheGame()
    {
        Match m = Match.Hosting();
        var (alice, aliceEvents) = m.Join(ClientId);
        var (bob, bobEvents) = m.Join(OtherClientId);
        m.Until(() => m.Host.Route!.VoiceUp && alice.Route!.VoiceUp && bob.Route!.VoiceUp && bob.Route.VoiceMembers.Count == 2);
        var heard = new List<(string Who, ulong From, uint First)>();
        m.Host.Route!.VoiceReceived = (from, p) => heard.Add(("host", from, p.FirstFrame));
        alice.Route!.VoiceReceived = (from, p) => heard.Add(("alice", from, p.FirstFrame));
        bob.Route!.VoiceReceived = (from, p) => heard.Add(("bob", from, p.FirstFrame));
        m.Steam.Advance(500);
        m.ClearReceived(aliceEvents, bobEvents);
        int framesBefore = m.Relay.DataFrames;

        alice.Route.SendVoice(Voice(0));
        alice.Route.SendVoice(Voice(3));
        m.Steam.Advance(100);

        // One relay message per packet, heard by the host and by the other client without the host passing it on.
        Assert.Equal(2, m.Relay.DataFrames - framesBefore);
        Assert.Equal(new[] { ("bob", ClientId, 0u), ("bob", ClientId, 3u), ("host", ClientId, 0u), ("host", ClientId, 3u) },
            heard.OrderBy(h => h.Who).ThenBy(h => h.First));
        Assert.Empty(m.HostEvents.Received);
        Assert.Empty(bobEvents.Received);
        Assert.Empty(aliceEvents.Disconnected);
    }

    [Fact]
    public void TamperedAndForeignVoiceIsDropped()
    {
        Match m = Match.Hosting();
        m.Relay.Tamper = (sender, payload) =>
        {
            if (sender != ClientId || !VoiceCrypto.IsVoice(payload))
            {
                return payload;
            }
            byte[] changed = (byte[])payload.Clone();
            changed[^1] ^= 1;
            return changed;
        };
        var (client, events) = m.Join(ClientId);
        m.Until(() => m.Host.Route!.VoiceUp && client.Route!.VoiceUp);
        int heard = 0;
        m.Host.Route!.VoiceReceived = (_, _) => heard++;

        client.Route!.SendVoice(Voice(0));
        using (var other = new VoiceCrypto(VoiceCrypto.NewKey()))
        {
            m.Relay.Inject(OtherClientId, other.Seal(Voice(30)));
        }
        m.Steam.Advance(500);

        Assert.Equal(0, heard);
        // Reported once per match, for whichever came first.
        Assert.Equal(1, m.LogCount("host: relay: dropped a voice packet from"));
        Assert.Empty(events.Disconnected);
        m.Chatter(1000, client);
        Assert.NotEmpty(m.HostEvents.Received);
    }

    [Fact]
    public void WithoutTheRelayThereIsNoVoice()
    {
        Match m = Match.Hosting(relay: false);
        var (client, _) = m.Join(ClientId, relay: false);
        m.Chatter(1000, client);
        Assert.Null(m.Host.Route);
        Assert.Null(client.Route);
    }

    [Fact]
    public void OneClientOnTheRelayAndOneOnLobbyChat()
    {
        Match m = Match.Hosting();
        var (fast, fastEvents) = m.Join(ClientId);
        var (slow, slowEvents) = m.Join(OtherClientId, relay: false);
        m.Chatter(6000, fast, slow);

        Assert.True(fast.OnRelay);
        Assert.False(m.Host.AllOnRelay);
        Assert.False(m.Host.Link.FastPacing);
        Assert.Equal(1, m.LogCount($"client {ClientId}: fast link up via relay for {HostId}"));

        m.Steam.Advance(1000);
        m.ClearReceived(fastEvents, slowEvents);
        for (int i = 0; i < 30; i++)
        {
            m.Host.Send(ClientId, Message(i), 40, reliable: true, channel: 0);
            m.Host.Send(OtherClientId, Message(100 + i), 40, reliable: true, channel: 0);
            fast.Send(Message(1000 + i), 40, reliable: true, channel: 0);
            slow.Send(Message(2000 + i), 40, reliable: true, channel: 0);
            m.Steam.Advance(100);
        }
        m.Steam.Advance(2000);

        Assert.Equal(Enumerable.Range(0, 30), Numbers(fastEvents.Received));
        Assert.Equal(Enumerable.Range(100, 30), Numbers(slowEvents.Received));
        Assert.Equal(Enumerable.Range(1000, 30), Numbers(m.HostEvents.Received.Where(r => r.Peer == ClientId).Select(r => r.Data)));
        Assert.Equal(Enumerable.Range(2000, 30), Numbers(m.HostEvents.Received.Where(r => r.Peer == OtherClientId).Select(r => r.Data)));

        // The relay client's messages reach the host at relay speed.
        m.ClearReceived(fastEvents, slowEvents);
        fast.Send(Message(7), 40, reliable: true, channel: 0);
        Assert.InRange(m.Until(() => m.HostEvents.Received.Any(r => r.Data.SequenceEqual(Message(7)))), 1, 100);
    }

    [Fact]
    public void TheHostsGoodbyeArrivesOverTheRelay()
    {
        Match m = Match.Hosting();
        var (client, events) = m.Join(ClientId);
        UntilFast(m, client);
        m.Steam.Advance(500);

        m.Host.Stop(ChatReasons.Quit);
        long took = m.Until(() => events.Disconnected.Count > 0);

        Assert.InRange(took, 1, 150);
        Assert.Equal([ChatReasons.Quit], events.Disconnected);
        m.Steam.Advance(2000);
        Assert.Single(events.Disconnected);
    }

    [Fact]
    public void AClientsGoodbyeArrivesOverTheRelay()
    {
        Match m = Match.Hosting();
        var (client, events) = m.Join(ClientId);
        UntilFast(m, client);
        m.Steam.Advance(500);

        client.Disconnect(ChatReasons.Quit);
        long took = m.Until(() => m.HostEvents.Disconnected.Count > 0);

        Assert.InRange(took, 1, 150);
        Assert.Equal([(ClientId, ChatReasons.Quit, false)], m.HostEvents.Disconnected);
        // The host's last client is gone, so it leaves the relay room.
        m.Steam.Advance(2000);
        Assert.Contains("relay: ", string.Join("\n", m.Log.Where(l => l.StartsWith("host: "))));
    }

    /// <summary>
    /// Another member of the lobby who reads everything sent over lobby chat, as every member of a
    /// Steamworks lobby can: the host's offer (room key and region) and every player's relay keys.
    /// </summary>
    private sealed class Eavesdropper
    {
        private readonly List<(ulong Sender, byte[] Message)> _heard = new();

        public Eavesdropper(Match m, ulong id)
        {
            FakeBackend backend = m.Steam.Member(id);
            backend.Changed += e =>
            {
                if (e.Kind == LobbyEventKind.Chat)
                {
                    _heard.Add((e.Member, e.Data!));
                }
            };
            backend.JoinAsync(m.Lobby, default).GetAwaiter().GetResult();
        }

        private IEnumerable<(ulong Sender, Frame Frame, byte[] Message)> Frames()
        {
            foreach ((ulong sender, byte[] message) in _heard)
            {
                int position = ChatFraming.HeaderSize;
                while (ChatFraming.TryReadFrame(message, ref position, out Frame frame))
                {
                    yield return (sender, frame, message);
                }
            }
        }

        public (string Region, byte[] RoomKey) Offer()
        {
            (_, Frame frame, byte[] message) = Frames().First(f => f.Frame.Kind == FrameKind.RelayOffer);
            return ChatFraming.ReadRelayOffer(message, frame);
        }

        /// <summary>The keys a member announced for itself.</summary>
        public (ulong Tag, byte[] MessageKey, byte[] VoiceKey) KeysOf(ulong member)
        {
            (_, Frame frame, byte[] message) = Frames().First(f => f.Frame.Kind == FrameKind.RelayKeys && f.Frame.Member == member && f.Sender == member);
            (byte[] messageKey, byte[] voiceKey) = ChatFraming.ReadRelayKeys(message, frame);
            return (frame.Tag, messageKey, voiceKey);
        }
    }

    /// <summary>A relay connection under any member ID, driven by the simulated clock.</summary>
    private sealed class RawMember
    {
        public readonly RelaySession Session;
        public readonly List<RelayEvent> Events = new();

        public RawMember(Match m, string region, byte[] roomKey, ulong id)
        {
            Session = new RelaySession(m.Relay.Transport(), region, RelayCrypto.RoomToken(roomKey), id, m.Steam.Now);
            m.Steam.OnTick(() => Events.AddRange(Session.Poll(m.Steam.Now)));
        }
    }

    [Fact]
    public void AMemberCannotPassItselfOffAsTheHost()
    {
        Match m = Match.Hosting();
        var eve = new Eavesdropper(m, StrangerId);
        var (client, events) = m.Join(ClientId);
        UntilFast(m, client);
        int voiceHeard = 0;
        client.Route!.VoiceReceived = (_, _) => voiceHeard++;
        // Everything the host announced over lobby chat, keys included, is known to the attacker.
        (string region, byte[] roomKey) = eve.Offer();
        (_, byte[] hostMessageKey, byte[] hostVoiceKey) = eve.KeysOf(HostId);

        // While the host is in the room, a join under its ID is refused.
        var refused = new RawMember(m, region, roomKey, HostId);
        m.Until(() => refused.Session.IsClosed);
        Assert.Equal(1, m.Relay.Refused);
        Assert.DoesNotContain(refused.Events, e => e.Kind == RelayEventKind.Joined);
        m.Chatter(500, client);
        Assert.True(m.Host.AllOnRelay && client.OnRelay);

        // The host's connection drops and the attacker takes its ID in the room before it is back,
        // then sends lobby messages and voice sealed with the host's own keys.
        m.Relay.Kick(HostId, "network lost");
        var squatter = new RawMember(m, region, roomKey, HostId);
        m.Until(() => squatter.Session.IsJoined);
        using (var hostMessages = new RelayCrypto(hostMessageKey))
        using (var hostVoice = new VoiceCrypto(hostVoiceKey))
        {
            for (uint seq = 1; seq <= 400; seq++)
            {
                byte[] forged = LobbyMessage(seq, Message(666));
                squatter.Session.Send(0, hostMessages.Seal(forged, forged.Length));
            }
            squatter.Session.Send(0, hostVoice.Seal(Voice(0)));
        }
        m.Steam.Advance(200);

        // The client sees the host's ID back on a connection that is not the host's and takes nothing from it.
        Assert.Equal(1, m.LogCount($"client {ClientId}: relay: {HostId} is in the room on a connection that is not the one it announced"));
        Assert.Equal(1, m.LogCount($"client {ClientId}: relay: dropped a message from {HostId} that did not come from the connection and key it announced"));
        Assert.Equal(1, m.LogCount($"client {ClientId}: relay: dropped a voice packet from {HostId}"));
        Assert.Equal(0, voiceHeard);

        // The host's own joins are refused while the attacker holds its ID, and the match goes on over lobby chat.
        m.ClearReceived(events);
        for (int i = 0; i < 30; i++)
        {
            m.Host.Send(ClientId, Message(i), 40, reliable: true, channel: 0);
            client.Send(Message(1000 + i), 40, reliable: true, channel: 0);
            m.Steam.Advance(500);
        }
        m.Steam.Advance(3000);
        Assert.Equal(Enumerable.Range(0, 30), Numbers(events.Received));
        Assert.Equal(Enumerable.Range(1000, 30), Numbers(m.HostEvents.Received.Select(r => r.Data)));
        Assert.DoesNotContain(events.Received, d => BitConverter.ToInt32(d, 0) == 666);
        Assert.True(m.LogCount("host: relay connection closed: relay closed the connection (4009 member ID in use)") >= 1);
        Assert.False(m.Host.AllOnRelay);
        Assert.False(client.OnRelay);
        Assert.Empty(events.Disconnected);
        Assert.Empty(m.HostEvents.Disconnected);
    }

    [Fact]
    public void VoiceFromOutsideTheMatchOrUnderAnotherMembersKeyIsDropped()
    {
        Match m = Match.Hosting();
        var eve = new Eavesdropper(m, StrangerId);
        var (alice, _) = m.Join(ClientId);
        var (bob, _) = m.Join(OtherClientId);
        m.Until(() => m.Host.Route!.VoiceMembers.Count == 2 && alice.Route!.VoiceMembers.Count == 2 && bob.Route!.VoiceMembers.Count == 2);
        var heard = new List<(string Who, ulong From, uint First)>();
        m.Host.Route!.VoiceReceived = (from, p) => heard.Add(("host", from, p.FirstFrame));
        alice.Route!.VoiceReceived = (from, p) => heard.Add(("alice", from, p.FirstFrame));
        (string region, byte[] roomKey) = eve.Offer();
        (_, _, byte[] aliceVoiceKey) = eve.KeysOf(ClientId);

        // Someone who found the room but is not in the match speaks under its own ID and key.
        var stranger = new RawMember(m, region, roomKey, StrangerId);
        m.Until(() => stranger.Session.IsJoined);
        using (var strangerVoice = new VoiceCrypto(VoiceCrypto.NewKey()))
        {
            stranger.Session.Send(0, strangerVoice.Seal(Voice(10)));
        }
        // A member of the match seals its packets with another member's voice key.
        m.Relay.Tamper = (sender, payload) =>
        {
            if (sender != OtherClientId || !VoiceCrypto.IsVoice(payload))
            {
                return payload;
            }
            using var aliceVoice = new VoiceCrypto(aliceVoiceKey);
            return aliceVoice.Seal(Voice(20));
        };
        bob.Route!.SendVoice(Voice(20));
        alice.Route.SendVoice(Voice(30));
        m.Steam.Advance(200);

        // Only Alice's own packet is heard; the stranger is not a voice member either.
        Assert.Equal(new[] { ("host", ClientId, 30u) }, heard);
        Assert.DoesNotContain(StrangerId, m.Host.Route.VoiceMembers);
        Assert.DoesNotContain(StrangerId, alice.Route.VoiceMembers);
        Assert.Equal(1, m.LogCount("host: relay: dropped a voice packet from"));
        Assert.Equal(1, m.LogCount($"client {ClientId}: relay: dropped a voice packet from"));
    }

    /// <summary>A lobby message as a link builds it: header, then one data frame.</summary>
    private static byte[] LobbyMessage(uint seq, byte[] payload)
    {
        var buffer = new byte[ChatFraming.HeaderSize + ChatFraming.DataFrameHeaderSize + payload.Length];
        ChatFraming.WriteHeader(buffer, 1111, seq);
        ChatFraming.WriteDataFrame(buffer.AsSpan(ChatFraming.HeaderSize), ClientId, true, 0, payload.Length, 0, payload);
        return buffer;
    }
}
