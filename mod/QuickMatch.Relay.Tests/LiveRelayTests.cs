using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using QuickMatch.Transport;
using QuickMatchRelay.Relay;
using Xunit;

namespace QuickMatchRelay.Tests;

/// <summary>
/// Runs the relay's own source locally (`wrangler dev`, no Cloudflare account needed, nothing is
/// deployed) for the live tests. Needs Node and the relay's `npm install`.
/// </summary>
public sealed class LocalRelay : IDisposable
{
    public const int Port = 18788;

    private readonly Process _dev;

    public LocalRelay()
    {
        string relayDir = typeof(LocalRelay).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == "RelayDir").Value!;
        var start = new ProcessStartInfo("npx", $"wrangler dev --port {Port} --ip 127.0.0.1 --show-interactive-dev-session=false")
        {
            WorkingDirectory = relayDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        _dev = Process.Start(start) ?? throw new InvalidOperationException("could not start npx");
        var ready = new TaskCompletionSource();
        var output = new System.Text.StringBuilder();
        void Look(object _, DataReceivedEventArgs e)
        {
            lock (output)
            {
                output.AppendLine(e.Data);
                if (e.Data?.Contains("Ready on") == true)
                {
                    ready.TrySetResult();
                }
            }
        }
        _dev.OutputDataReceived += Look;
        _dev.ErrorDataReceived += Look;
        _dev.BeginOutputReadLine();
        _dev.BeginErrorReadLine();
        if (!ready.Task.Wait(TimeSpan.FromSeconds(60)))
        {
            Dispose();
            throw new InvalidOperationException($"wrangler dev did not start:\n{output}");
        }
    }

    public Uri Address => new($"ws://127.0.0.1:{Port}");

    public void Dispose()
    {
        if (!_dev.HasExited)
        {
            _dev.Kill(entireProcessTree: true);
            _dev.WaitForExit(10000);
        }
        _dev.Dispose();
    }
}

/// <summary>QuickMatch's relay client (<see cref="RelaySession"/>) over this mod's WebSocket transport, against the real relay.</summary>
public class LiveRelayTests(LocalRelay relay) : IClassFixture<LocalRelay>
{
    private const ulong HostId = 76561198000000102;
    private const ulong ClientId = 76561198000000101;

    private static long Now => Environment.TickCount64;

    /// <summary>Polls the sessions every 10 ms, collecting their events, until <paramref name="done"/> holds.</summary>
    private static void Until(Func<bool> done, Dictionary<RelaySession, List<RelayEvent>> events, int limitMs = 10000)
    {
        var watch = Stopwatch.StartNew();
        while (true)
        {
            foreach ((RelaySession session, List<RelayEvent> list) in events)
            {
                list.AddRange(session.Poll(Now));
            }
            if (done())
            {
                return;
            }
            Assert.True(watch.ElapsedMilliseconds < limitMs, $"not done after {limitMs} ms: " +
                string.Join("; ", events.Values.SelectMany(l => l).Select(e => $"{e.Kind} {e.Member} {e.Reason}")));
            Thread.Sleep(10);
        }
    }

    [Fact]
    public void TwoPlayersMeetInARoomAndExchangeMessages()
    {
        var transport = new WebSocketRelayTransport(relay.Address);
        byte[] room = RandomNumberGenerator.GetBytes(32);
        using var host = new RelaySession(transport, "auto", room, HostId, Now);
        var events = new Dictionary<RelaySession, List<RelayEvent>> { [host] = new() };
        Until(() => host.IsJoined, events);
        Assert.Empty(host.Members);
        Assert.Matches("^[a-z]+$", host.Region);

        // The client goes to the region the host was given, as the host's offer tells it.
        var client = new RelaySession(transport, host.Region, room, ClientId, Now);
        events[client] = new();
        Until(() => client.IsJoined && events[host].Any(e => e.Kind == RelayEventKind.MemberJoined), events);
        Assert.Equal([HostId], client.Members);
        Assert.Equal(ClientId, events[host].Single(e => e.Kind == RelayEventKind.MemberJoined).Member);
        Assert.Equal(host.Region, client.Region);

        byte[] toAll = RandomNumberGenerator.GetBytes(40000);
        byte[] toHost = RandomNumberGenerator.GetBytes(300);
        host.Send(0, toAll);
        client.Send(HostId, toHost);
        Until(() => events[client].Any(e => e.Kind == RelayEventKind.Data) && events[host].Any(e => e.Kind == RelayEventKind.Data), events);
        RelayEvent atClient = events[client].Single(e => e.Kind == RelayEventKind.Data);
        RelayEvent atHost = events[host].Single(e => e.Kind == RelayEventKind.Data);
        Assert.Equal(HostId, atClient.Member);
        Assert.Equal(toAll, atClient.Payload);
        Assert.Equal(ClientId, atHost.Member);
        Assert.Equal(toHost, atHost.Payload);

        // A message queued right before letting go still leaves, then the room hears the client leave.
        byte[] last = RandomNumberGenerator.GetBytes(64);
        client.Send(HostId, last);
        client.Dispose();
        Until(() => events[host].Any(e => e.Kind == RelayEventKind.MemberLeft), events);
        Assert.Contains(events[host], e => e.Kind == RelayEventKind.Data && e.Payload!.SequenceEqual(last));
        Assert.Equal(ClientId, events[host].Single(e => e.Kind == RelayEventKind.MemberLeft).Member);
        Assert.Empty(host.Members);
        Assert.False(host.IsClosed);
    }

    [Fact]
    public void AnotherPlayersJoinUnderAnIdInUseIsRefusedButAReconnectWorks()
    {
        var transport = new WebSocketRelayTransport(relay.Address);
        byte[] room = RandomNumberGenerator.GetBytes(32);
        byte[] hostSecret = RelaySession.NewSecret();
        using var host = new RelaySession(transport, "apac", room, HostId, Now, hostSecret);
        using var client = new RelaySession(transport, "apac", room, ClientId, Now);
        var events = new Dictionary<RelaySession, List<RelayEvent>> { [host] = new(), [client] = new() };
        Until(() => host.IsJoined && client.IsJoined && client.Members.Count == 1, events);
        Assert.True(client.TryGetTag(HostId, out ulong hostTag));
        Assert.Equal(host.Tag, hostTag);
        events[client].Clear();

        // A second connection claiming the host's ID with its own secret is turned away; the host stays.
        using var hijack = new RelaySession(transport, "apac", room, HostId, Now);
        events[hijack] = new();
        Until(() => hijack.IsClosed, events);
        Assert.Contains("4009", events[hijack].Single(e => e.Kind == RelayEventKind.Closed).Reason);
        Assert.False(host.IsClosed);
        Assert.DoesNotContain(events[client], e => e.Kind is RelayEventKind.MemberLeft or RelayEventKind.MemberJoined);

        // The host itself, reconnecting with its secret, takes over its ID under the same tag.
        using var again = new RelaySession(transport, "apac", room, HostId, Now, hostSecret);
        events[again] = new();
        Until(() => again.IsJoined && host.IsClosed && events[client].Any(e => e.Kind == RelayEventKind.MemberJoined), events);
        Assert.Equal(hostTag, events[client].Single(e => e.Kind == RelayEventKind.MemberJoined).Tag);
        again.Send(ClientId, [42]);
        Until(() => events[client].Any(e => e.Kind == RelayEventKind.Data), events);
        Assert.Equal(HostId, events[client].Single(e => e.Kind == RelayEventKind.Data).Member);
        Assert.Equal(hostTag, events[client].Single(e => e.Kind == RelayEventKind.Data).Tag);
    }

    [Fact]
    public void ARoundTripThroughTheLocalRelayIsQuick()
    {
        var transport = new WebSocketRelayTransport(relay.Address);
        byte[] room = RandomNumberGenerator.GetBytes(32);
        using var host = new RelaySession(transport, "apac", room, HostId, Now);
        using var client = new RelaySession(transport, "apac", room, ClientId, Now);
        var events = new Dictionary<RelaySession, List<RelayEvent>> { [host] = new(), [client] = new() };
        Until(() => host.IsJoined && client.IsJoined && host.Members.Count == 1 && client.Members.Count == 1, events);

        var times = new List<long>();
        for (int i = 0; i < 20; i++)
        {
            var watch = Stopwatch.StartNew();
            client.Send(HostId, [(byte)i]);
            Until(() => events[host].Any(e => e.Kind == RelayEventKind.Data && e.Payload![0] == i), events);
            host.Send(ClientId, [(byte)i]);
            Until(() => events[client].Any(e => e.Kind == RelayEventKind.Data && e.Payload![0] == i), events);
            times.Add(watch.ElapsedMilliseconds);
        }

        times.Sort();
        Assert.InRange(times[times.Count / 2], 0, 100);
    }

    [Fact]
    public void AnUnusableRegionOrAddressEndsInClosed()
    {
        var events = new Dictionary<RelaySession, List<RelayEvent>>();
        using var badRegion = new RelaySession(new WebSocketRelayTransport(relay.Address), "../probe", new byte[32], HostId, Now);
        using var nobody = new RelaySession(new WebSocketRelayTransport(new Uri("ws://127.0.0.1:9")), "apac", new byte[32], HostId, Now);
        using var unknownRegion = new RelaySession(new WebSocketRelayTransport(relay.Address), "mars", new byte[32], HostId, Now);
        events[badRegion] = new();
        events[nobody] = new();
        events[unknownRegion] = new();

        Until(() => events.Values.All(l => l.Any(e => e.Kind == RelayEventKind.Closed)), events);

        Assert.Contains("refused region", events[badRegion].Single().Reason);
        Assert.All(events.Keys, s => Assert.True(s.IsClosed));
        Assert.All(events.Values, l => Assert.DoesNotContain(l, e => e.Kind == RelayEventKind.Joined));
    }
}
