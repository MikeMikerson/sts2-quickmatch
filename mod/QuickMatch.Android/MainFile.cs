using System;
using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Modding;
using QuickMatch.Lobby;
using QuickMatch.Logging;
using QuickMatchAndroid.Launcher;
using QuickMatchAndroid.Lobby;
using QuickMatchAndroid.Logging;

namespace QuickMatchAndroid;

/// <summary>
/// Mod entry point; the game initializes it after QuickMatch, which it depends on. Inside the Android
/// StS2 Launcher it gives QuickMatch a SteamKit2 lobby backend on the launcher's saved Steam sign-in and
/// patches the Steam identity of quick-match runs. Anywhere else (desktop, where QuickMatch uses
/// Steamworks) it does nothing.
/// </summary>
[ModInitializer("Initialize")]
public static class MainFile
{
    public const string ModId = "QuickMatchAndroid";

    /// <summary>Slay the Spire 2.</summary>
    private const uint GameAppId = 2868840;

    public static ModLogger Logger { get; private set; } = new(ModId);

    /// <summary>The lobby backend, when running in the Android launcher; null elsewhere.</summary>
    internal static AndroidLobbyBackend? Backend { get; private set; }

    public static void Initialize()
    {
        Logger = new ModLogger(ModId);
        Func<string, Type?> findType = name => Type.GetType(name, throwOnError: false);
        if (!LauncherSession.InLauncher(findType))
        {
            Logger.Info("not running in the Android launcher; nothing to do");
            return;
        }

        LauncherSession? session = LauncherSession.Create(findType, problem => Logger.Warn(problem));
        if (session == null)
        {
            Logger.Warn("Quick Match stays unavailable");
            return;
        }
        var log = new GodotModLog(Logger);
        var backend = new AndroidLobbyBackend(new SteamKitLobbyService(session, GameAppId, log), log);
        Backend = backend;
        LobbyBackend.Current = backend;
        new Harmony(ModId).PatchAll(Assembly.GetExecutingAssembly());
        Logger.Info("QuickMatchAndroid initialized; lobbies go through the launcher's Steam sign-in");
        // Early, so the Quick Match card and our Steam ID are ready by the main menu.
        backend.StartSignIn();
    }
}
