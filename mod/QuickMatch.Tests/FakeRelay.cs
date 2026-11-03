using System.Buffers.Binary;
using System.Text;
using QuickMatch.Transport;

namespace QuickMatch.Tests;

/// <summary>
/// The QuickMatch relay (`relay/src/index.js`) in memory, on <see cref="FakeSteam"/>'s clock: rooms by
/// token, up to 4 members, Join with a secret, Welcome and member joined with each member's tag, Data,
/// member left; a member joining again with the same secret replaces its earlier connection, and a
/// join for a member ID held under another secret is refused. Each game gets its own
/// <see cref="Transport"/>. Tests can refuse connections, make data vanish, slow the relay down, change
/// what it forwards, close one member's connection or every connection.
/// </summary>
internal sealed class FakeRelay
{
    public const string Region = "apac";

    private readonly FakeSteam _steam;
    private readonly Dictionary<string, Dictionary<ulong, Socket>> _rooms = new();

    public FakeRelay(FakeSteam steam) => _steam = steam;

    /// <summary>Time from a game to the relay.</summary>
    public long UpMs { get; set; } = 15;

    /// <summary>Time from the relay to a game.</summary>
    public long DownMs { get; set; } = 20;

    /// <summary>Connections fail, as when the network or the relay is down.</summary>
    public bool Refuse { get; set; }

    /// <summary>Data frames vanish while connections stay open.</summary>
    public bool BlackHole { get; set; }

    /// <summary>Replaces a forwarded payload (sender, payload).</summary>
    public Func<ulong, byte[], byte[]>? Tamper { get; set; }

    public int Opened { get; private set; }

    /// <summary>Data frames the relay received from games.</summary>
    public int DataFrames { get; private set; }

    public IRelayTransport Transport() => new Client(this);

    /// <summary>Ends every connection from the relay's side.</summary>
    public void CloseAll(string reason)
    {
        foreach (Socket socket in _rooms.Values.SelectMany(r => r.Values).ToList())
        {
            Close(socket, reason);
        }
    }

    /// <summary>Ends one member's connections from the relay's side, as a dropped network would.</summary>
    public void Kick(ulong member, string reason)
    {
        foreach (Socket socket in _rooms.Values.SelectMany(r => r.Values).Where(s => s.Member == member).ToList())
        {
            Close(socket, reason);
        }
    }

    /// <summary>Joins refused because another connection held the member ID.</summary>
    public int Refused { get; private set; }

    /// <summary>Sends a data frame into every room as <paramref name="sender"/>, who is in none of them.</summary>
    public void Inject(ulong sender, byte[] payload)
    {
        foreach (Socket socket in _rooms.Values.SelectMany(r => r.Values).ToList())
        {
            Deliver(socket, DataFrame(sender, payload));
        }
    }

    private IRelaySocket Open(string region, IRelayListener listener)
    {
        Opened++;
        var socket = new Socket(this, listener) { Region = region == "auto" ? Region : region };
        if (Refuse)
        {
            socket.Gone = true;
            _steam.After(UpMs + DownMs, () => socket.RaiseClosed("connection refused"));
        }
        else
        {
            _steam.After(UpMs + DownMs, socket.RaiseOpened);
        }
        return socket;
    }

    private void FromGame(Socket socket, byte[] message)
    {
        if (socket.Gone)
        {
            return;
        }
        if (socket.Room == null)
        {
            if (message.Length != 42 + RelaySession.SecretBytes || message[0] != (byte)'J' || message[1] != RelaySession.ProtocolVersion)
            {
                Close(socket, "bad join");
                return;
            }
            string room = Convert.ToHexString(message, 2, 32);
            ulong member = BinaryPrimitives.ReadUInt64LittleEndian(message.AsSpan(34));
            byte[] secret = message.AsSpan(42).ToArray();
            Dictionary<ulong, Socket> members = _rooms.TryGetValue(room, out var existing) ? existing : _rooms[room] = new();
            if (members.TryGetValue(member, out Socket? earlier))
            {
                if (!earlier.Secret.AsSpan().SequenceEqual(secret))
                {
                    Refused++;
                    Close(socket, "relay closed the connection (4009 member ID in use)");
                    return;
                }
                Close(earlier, "replaced");
            }
            if (members.Count >= 4)
            {
                Close(socket, "room full");
                return;
            }
            socket.Room = room;
            socket.Member = member;
            socket.Secret = secret;
            byte[] region = Encoding.ASCII.GetBytes(socket.Region);
            var welcome = new byte[4 + region.Length + 16 * members.Count];
            welcome[0] = (byte)'W';
            welcome[1] = RelaySession.ProtocolVersion;
            welcome[2] = (byte)region.Length;
            region.CopyTo(welcome, 3);
            welcome[3 + region.Length] = (byte)members.Count;
            int at = 4 + region.Length;
            foreach ((ulong other, Socket otherSocket) in members)
            {
                BinaryPrimitives.WriteUInt64LittleEndian(welcome.AsSpan(at), other);
                BinaryPrimitives.WriteUInt64LittleEndian(welcome.AsSpan(at + 8), RelaySession.TagOf(otherSocket.Secret));
                at += 16;
            }
            Deliver(socket, welcome);
            byte[] joined = new byte[17];
            joined[0] = (byte)'+';
            BinaryPrimitives.WriteUInt64LittleEndian(joined.AsSpan(1), member);
            BinaryPrimitives.WriteUInt64LittleEndian(joined.AsSpan(9), RelaySession.TagOf(secret));
            foreach (Socket other in members.Values)
            {
                Deliver(other, joined);
            }
            members[member] = socket;
            return;
        }
        if (message.Length < 9 || message[0] != (byte)'D')
        {
            Close(socket, "bad frame");
            return;
        }
        DataFrames++;
        if (BlackHole)
        {
            return;
        }
        ulong target = BinaryPrimitives.ReadUInt64LittleEndian(message.AsSpan(1));
        byte[] payload = message.AsSpan(9).ToArray();
        payload = Tamper?.Invoke(socket.Member, payload) ?? payload;
        foreach ((ulong id, Socket other) in _rooms[socket.Room])
        {
            if (id != socket.Member && (target == 0 || target == id))
            {
                Deliver(other, DataFrame(socket.Member, payload));
            }
        }
    }

    private void Leave(Socket socket)
    {
        if (socket.Gone)
        {
            return;
        }
        socket.Gone = true;
        if (socket.Room != null && _rooms.TryGetValue(socket.Room, out var members) && members.TryGetValue(socket.Member, out Socket? current) && current == socket)
        {
            members.Remove(socket.Member);
            foreach (Socket other in members.Values)
            {
                Deliver(other, Notice((byte)'-', socket.Member));
            }
        }
    }

    private void Close(Socket socket, string reason)
    {
        Leave(socket);
        _steam.After(DownMs, () => socket.RaiseClosed(reason));
    }

    private void Deliver(Socket socket, byte[] message) =>
        _steam.After(DownMs, () =>
        {
            if (!socket.Gone)
            {
                socket.RaiseReceived(message);
            }
        });

    private static byte[] Notice(byte kind, ulong member)
    {
        var notice = new byte[9];
        notice[0] = kind;
        BinaryPrimitives.WriteUInt64LittleEndian(notice.AsSpan(1), member);
        return notice;
    }

    private static byte[] DataFrame(ulong sender, byte[] payload)
    {
        var frame = new byte[9 + payload.Length];
        frame[0] = (byte)'D';
        BinaryPrimitives.WriteUInt64LittleEndian(frame.AsSpan(1), sender);
        payload.CopyTo(frame, 9);
        return frame;
    }

    private sealed class Client(FakeRelay relay) : IRelayTransport
    {
        public IRelaySocket Open(string region, IRelayListener listener) => relay.Open(region, listener);
    }

    private sealed class Socket(FakeRelay relay, IRelayListener listener) : IRelaySocket
    {
        public string Region = "";
        public string? Room;
        public ulong Member;
        public byte[] Secret = [];
        /// <summary>Out of the relay's rooms; nothing more reaches the game.</summary>
        public bool Gone;
        private bool _disposed;
        private bool _closed;

        public void Send(byte[] message)
        {
            if (!_disposed && !Gone)
            {
                relay._steam.After(relay.UpMs, () => relay.FromGame(this, message));
            }
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                relay._steam.After(relay.UpMs, () => relay.Leave(this));
            }
        }

        public void RaiseOpened()
        {
            if (!_disposed && !_closed && !Gone)
            {
                listener.Opened();
            }
        }

        public void RaiseReceived(byte[] message)
        {
            if (!_disposed && !_closed)
            {
                listener.Received(message);
            }
        }

        public void RaiseClosed(string reason)
        {
            if (!_disposed && !_closed)
            {
                _closed = true;
                listener.Closed(reason);
            }
        }
    }
}
