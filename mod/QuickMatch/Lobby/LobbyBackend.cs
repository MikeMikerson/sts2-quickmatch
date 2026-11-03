namespace QuickMatch.Lobby;

/// <summary>
/// The lobby backend QuickMatch uses: Steamworks, unless the app set another one before the mod
/// initialized (the iPhone port has no Steamworks and talks to Steam through its own client).
/// </summary>
public static class LobbyBackend
{
    private static ILobbyBackend? _current;

    public static ILobbyBackend Current
    {
        get => _current ??= new SteamworksLobbyBackend();
        set => _current = value;
    }
}
