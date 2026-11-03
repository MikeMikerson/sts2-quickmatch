using System;
using System.Threading;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Audio;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Screens.Capstones;
using MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Platform;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using QuickMatch.Steam;
using QuickMatch.Transport;
using QuickMatch.Ui;

namespace QuickMatch.Matchmaking;

/// <summary>
/// Gets a quick-match group back into the same run after someone drops. The game cannot rejoin a
/// run in progress, but it can reload the host's save, so a regroup is: the host reopens the
/// saved run, and every other player finds that lobby by the run key and joins it.
///
/// - Host: when a client drops, offers to save, quit and reopen the run right away.
/// - Client: when the connection to the host is lost, goes to the rejoin search instead of the
///   vanilla error popup.
/// </summary>
internal static class Regroup
{
    private const ulong PendingRunMaxAgeMs = 120_000;

    private static RunLobby? _trackedLobby;
    private static bool _promptOpen;
    private static bool _regrouping;

    /// <summary>True while the running game is a quick-match run (new or reloaded).</summary>
    public static bool InQuickMatchRun { get; private set; }

    /// <summary>Called (via patch) once a run is set up and launched, new or loaded.</summary>
    public static void OnRunLaunched(RunState state)
    {
        RunManager run = RunManager.Instance;
        if (Matchmaker.PendingRunStart is { } pending)
        {
            Matchmaker.PendingRunStart = null;
            // A run launches a few seconds after embarking; an old marker belongs to a start that failed.
            if (Godot.Time.GetTicksMsec() - pending.At < PendingRunMaxAgeMs)
            {
                RunRecord.Save(state, pending.HostId, pending.Size);
            }
        }
        InQuickMatchRun = RunRecord.Matches(state);
        _promptOpen = false;
        _regrouping = false;
        MainFile.Logger.Info($"run launched: quick match={InQuickMatchRun} key={RunRecord.KeyOf(state)} as {run.NetService.Type}");
        if (InQuickMatchRun && run.NetService.Type == NetGameType.Host && run.RunLobby != null)
        {
            _trackedLobby = run.RunLobby;
            _trackedLobby.RemotePlayerDisconnected += OnRemotePlayerDisconnected;
        }
    }

    /// <summary>Called (via patch) before the run is torn down, for any reason.</summary>
    public static void OnRunCleanUp()
    {
        if (InQuickMatchRun && RunManager.Instance.IsAbandoned)
        {
            RunRecord.Clear("run abandoned");
        }
        if (_trackedLobby != null)
        {
            _trackedLobby.RemotePlayerDisconnected -= OnRemotePlayerDisconnected;
            _trackedLobby = null;
        }
        InQuickMatchRun = false;
    }

    /// <summary>Called (via patch) when the run is over (victory, defeat or abandon).</summary>
    public static void OnRunEnded()
    {
        if (InQuickMatchRun)
        {
            RunRecord.Clear("run ended");
        }
    }

    /// <summary>
    /// Client lost the host mid-run. Returns the task that replaces the vanilla "return to menu
    /// and show an error" flow, or null to let vanilla handle it.
    /// </summary>
    public static Task? ReplaceDisconnectFlow(NetErrorInfo info)
    {
        if (!InQuickMatchRun || RunRecord.Current == null || info.SelfInitiated || info.GetReason() == NetError.HostAbandoned)
        {
            return null;
        }
        MainFile.Logger.Info($"lost the host ({info.GetReason()}); going to the rejoin search");
        return ReturnAndRejoinAsync();
    }

    private static async Task ReturnAndRejoinAsync()
    {
        try
        {
            // Same as vanilla before it returns to the menu.
            NCapstoneContainer.Instance?.Close();
            NMapScreen.Instance?.Close(animateOut: false);
            await NGame.Instance!.ReturnToMainMenuAfterRun();
            NMainMenu menu = await MainMenuAsync();
            menu.OpenMultiplayerSubmenu();
            SizePicker.Open();
            Matchmaker.StartRejoin();
        }
        catch (Exception e)
        {
            MainFile.Logger.Error($"could not start the rejoin search: {e}");
        }
    }

    private static void OnRemotePlayerDisconnected(ulong playerId)
    {
        if (!InQuickMatchRun || _promptOpen || _regrouping || RunManager.Instance.IsAbandoned)
        {
            return;
        }
        // A quick-match run's players are Steam players, whatever this app's primary platform is.
        string name = PlatformUtil.GetPlayerName(PlatformType.Steam, playerId);
        MainFile.Logger.Info($"player {playerId} ({name}) disconnected from the run");
        _ = PromptAsync(name);
    }

    private static async Task PromptAsync(string playerName)
    {
        _promptOpen = true;
        bool regroup;
        try
        {
            regroup = await RegroupPrompt.AskRegroupAsync(playerName);
        }
        finally
        {
            _promptOpen = false;
        }
        if (!regroup || !InQuickMatchRun || _regrouping)
        {
            MainFile.Logger.Info("host keeps playing without the disconnected player");
            return;
        }
        _regrouping = true;
        try
        {
            await HostRegroupAsync();
        }
        catch (Exception e)
        {
            MainFile.Logger.Error($"regroup failed: {e}");
        }
        finally
        {
            _regrouping = false;
        }
    }

    /// <summary>Save and quit (as the pause menu does), then reopen the save as the host's Load button does.</summary>
    private static async Task HostRegroupAsync()
    {
        MainFile.Logger.Info("regroup: saving, quitting and reopening the run");
        NRunMusicController.Instance?.StopMusic();
        await NGame.Instance!.ReturnToMainMenu();
        if (SaveManager.Instance.CurrentRunSaveTask is { } saving)
        {
            await saving;
        }
        NMainMenu menu = await MainMenuAsync();
        NMultiplayerSubmenu submenu = menu.OpenMultiplayerSubmenu();
        ReadSaveResult<SerializableRun> save = SaveManager.Instance.LoadAndCanonicalizeMultiplayerRunSave(RunRecord.LocalPlayerId());
        if (!save.Success || save.SaveData == null)
        {
            MainFile.Logger.Warn("regroup: the multiplayer save could not be read; use Load by hand");
            return;
        }
        submenu.StartHost(save.SaveData);
    }

    /// <summary>
    /// Reopens our quick-match save on QuickMatch's host (Steam sockets and lobby chat on desktop,
    /// lobby chat on a phone), the way the Multiplayer menu's own host step does for a standard run
    /// (four slots; the load lobby admits only the save's players), and makes it findable by the run key.
    /// </summary>
    public static async Task HostSavedRunAsync(SerializableRun run, string key)
    {
        NMainMenuSubmenuStack? stack = NGame.Instance?.MainMenu?.SubmenuStack;
        if (stack == null)
        {
            MainFile.Logger.Warn("regroup: no main menu to host from");
            return;
        }
        MainFile.Logger.Info($"reopening quick-match run {key} over {(QuickMatchHostService.HasSockets ? "Steam sockets and lobby chat" : "lobby chat")}");
        var netService = new QuickMatchHostService();
        NetErrorInfo? error = await netService.StartQuickMatchHost(4);
        if (error.HasValue)
        {
            MainFile.Logger.Warn($"could not reopen the run: {error.Value.GetReason()}");
            if (NErrorPopup.Create(error.Value) is { } popup)
            {
                NModalContainer.Instance?.Add(popup);
            }
            return;
        }
        if (netService.LobbyId is not { } lobby)
        {
            netService.Disconnect(NetError.InternalError);
            MainFile.Logger.Warn("reopened run has no lobby");
            return;
        }
        NMultiplayerLoadGameScreen screen = stack.GetSubmenuType<NMultiplayerLoadGameScreen>();
        screen.InitializeAsHost(netService, run);
        stack.Push(screen);
        QuickMatchLobby.PublishResume(lobby, key, netService.TakesSockets);
    }

    /// <summary>The main menu once it exists and has run its _Ready.</summary>
    private static async Task<NMainMenu> MainMenuAsync()
    {
        for (int i = 0; i < 50; i++)
        {
            if (NGame.Instance?.MainMenu is { } menu && menu.IsNodeReady() && menu.SubmenuStack != null)
            {
                return menu;
            }
            await GodotDelay.Seconds(0.1, CancellationToken.None);
        }
        throw new InvalidOperationException("main menu did not appear");
    }
}
