using SteamKit2;

namespace QuickMatchAndroid.Launcher;

/// <summary>A signed-in SteamKit2 connection that lobbies and lobby chat travel on.</summary>
internal interface ISteamSession
{
    SteamClient Client { get; }

    bool IsConnected { get; }

    /// <summary>Connects and signs in when not connected. Blocks (up to 15 s in the launcher); throws on failure.</summary>
    void Connect();

    /// <summary>Keeps the connection open until <see cref="Release"/>; leaving it closes every lobby.</summary>
    void HoldOpen();

    void Release();
}
