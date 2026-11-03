using System.Collections.Generic;
using System.Linq;

namespace QuickMatch.Transport;

/// <summary>The game's own Steam sockets host as <see cref="MixedRoutes"/> drives it; tests stand in for Steamworks here.</summary>
internal interface ISocketsHost
{
    IEnumerable<ulong> Peers { get; }

    void Send(ulong peer, byte[] bytes, int length, bool reliable, int channel);

    void Disconnect(ulong peer, ushort reason, bool now);

    void Update();
}

/// <summary>
/// The traffic of a host that takes both connections (<see cref="MixedHost"/>): each client stays on
/// the one it connected over, the game's Steam sockets or lobby chat, and a message to everyone goes
/// to each client once, over its own connection.
/// </summary>
internal sealed class MixedRoutes(ISocketsHost sockets, ChatHostCore chat)
{
    public IEnumerable<ulong> Peers => sockets.Peers.Concat(chat.Peers);

    public bool IsChatPeer(ulong peer) => chat.Peers.Contains(peer);

    public bool Has(ulong peer) => IsChatPeer(peer) || sockets.Peers.Contains(peer);

    public void Send(ulong peer, byte[] bytes, int length, bool reliable, int channel)
    {
        if (IsChatPeer(peer))
        {
            chat.Send(peer, bytes, length, reliable, (byte)channel);
        }
        else
        {
            sockets.Send(peer, bytes, length, reliable, channel);
        }
    }

    public void SendAll(byte[] bytes, int length, bool reliable, int channel)
    {
        foreach (ulong peer in sockets.Peers.ToList())
        {
            sockets.Send(peer, bytes, length, reliable, channel);
        }
        foreach (ulong peer in chat.Peers.ToList())
        {
            chat.Send(peer, bytes, length, reliable, (byte)channel);
        }
    }

    public void Disconnect(ulong peer, ushort reason, bool now)
    {
        if (IsChatPeer(peer))
        {
            chat.Disconnect(peer, reason);
        }
        else
        {
            sockets.Disconnect(peer, reason, now);
        }
    }

    public void Update()
    {
        sockets.Update();
        chat.Update();
    }
}
