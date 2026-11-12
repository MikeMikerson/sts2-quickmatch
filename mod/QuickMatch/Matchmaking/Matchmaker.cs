using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Connection;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Runs;
using QuickMatch.Localization;
using QuickMatch.Lobby;
using QuickMatch.Steam;
using QuickMatch.Transport;
using QuickMatch.Ui;

namespace QuickMatch.Matchmaking;

internal enum MatchState
{
    Idle,
    Searching,
    Joining,
    CreatingHost,
    HostingWaiting,
    Merging,
    InLobbyClient,
    Rejoining,
}

/// <summary>
/// Runs one quick-match session at a time: search for an open lobby of the chosen size and join
/// it, or host one and wait in character select until it fills. The host's Steam lobby is the
/// queue entry; once grouped, the vanilla character select and ready flow take over. A rejoin
/// session instead looks for the lobby in which the host has reopened the group's saved run.
///
/// Two desktop players use the game's own Steam sockets, as in vanilla co-op. A player without
/// Steamworks (a phone) uses lobby chat instead (ChatHost, ChatClient); a desktop host takes both
/// (MixedHost), so anyone can be in any quick-match group. See LobbyLinks.
///
/// Everything runs on the Godot main thread (Steam callbacks and SceneTree timers fire there).
/// Each session owns a CancellationTokenSource; Reset bumps the generation and cancels it, and
/// every continuation re-checks the token before touching the UI.
/// </summary>
internal static class Matchmaker
{
    private const int SearchRounds = 2;
    private const int MaxJoinAttemptsPerRound = 5;
    private const double JoinTimeoutSeconds = 15;
    private const double MergeIntervalSeconds = 6;
    private const ulong FailedLobbyCooldownMs = 60_000;
    private const double MoveWaitSeconds = 5;
    private const double RejoinPollSeconds = 5;
    private const double RejoinTimeoutSeconds = 180;

    private static readonly Random Rng = new();
    private static readonly Dictionary<ulong, ulong> RecentlyFailed = new();

    private static CancellationTokenSource? _cts;
    private static int _generation;
    private static JoinFlow? _flow;
    private static NCharacterSelectScreen? _screen;
    private static bool _merging;
    private static uint _createdAt;
    private static INetGameService? _handlerService;

    /// <summary>Character picked in the lobby we are moving out of, to pick again in the next one.</summary>
    private static CharacterModel? _pendingCharacter;

    /// <summary>
    /// Set when a quick-match group embarks (with the time, in ms); read once the run is up to
    /// write the run record.
    /// </summary>
    public static (int Size, ulong HostId, ulong At)? PendingRunStart { get; set; }

    public static MatchState State { get; private set; }

    /// <summary>Party size being searched for (2-4).</summary>
    public static int Size { get; private set; }

    /// <summary>The character-select lobby of the current session, once grouped or hosting.</summary>
    public static StartRunLobby? Lobby { get; private set; }

    public static ulong SteamLobbyId { get; private set; }

    public static bool IsAvailable => LobbyBackend.Current.IsAvailable;

    public static void Start(int size)
    {
        if (State != MatchState.Idle)
        {
            MainFile.Logger.Info($"ignoring start({size}) while {State}");
            return;
        }
        if (!IsAvailable)
        {
            MainFile.Logger.Warn("quick match needs Steam");
            return;
        }
        Size = size;
        _cts = new CancellationTokenSource();
        int gen = ++_generation;
        CancellationToken ct = _cts.Token;
        MainFile.Logger.Info($"start size={size} {QuickMatchLobby.CompatKey.Local()}");
        _ = RunAsync(gen, ct, () => SearchJoinOrHostAsync(ct), "search");
    }

    /// <summary>Looks for the lobby in which the host of our last quick-match run has reopened it.</summary>
    public static void StartRejoin()
    {
        if (State != MatchState.Idle)
        {
            MainFile.Logger.Info($"ignoring rejoin while {State}");
            return;
        }
        if (!IsAvailable || RunRecord.Current is not { } record)
        {
            MainFile.Logger.Warn("rejoin needs Steam and a run record");
            return;
        }
        Size = record.Size;
        _cts = new CancellationTokenSource();
        int gen = ++_generation;
        CancellationToken ct = _cts.Token;
        MainFile.Logger.Info($"rejoin key={record.Key} host={record.HostId}");
        _ = RunAsync(gen, ct, () => RejoinAsync(record, ct), "rejoin");
    }

    public static void Cancel(string reason)
    {
        if (State == MatchState.Idle)
        {
            return;
        }
        MainFile.Logger.Info($"cancel: {reason}");
        Reset(reason);
    }

    /// <summary>Called (via patch) right before character select tears down its lobby.</summary>
    public static void OnCharacterSelectClosing(NCharacterSelectScreen screen)
    {
        if (Lobby == null || screen.Lobby != Lobby)
        {
            return;
        }
        LobbyStatusPanel.Hide(screen);
        if (_merging)
        {
            // We are giving up our own lobby to join a better one; keep the session going.
            EndSession();
            return;
        }
        Cancel("character select closed");
    }

    /// <summary>Called (via patch) when the group embarks. The run no longer needs the matchmaker.</summary>
    public static void OnRunStarted(NCharacterSelectScreen screen)
    {
        if (Lobby == null || screen.Lobby != Lobby)
        {
            return;
        }
        MainFile.Logger.Info($"run started with {Lobby.Players.Count}/{Size} players");
        ulong hostId = Lobby.NetService.Type == NetGameType.Host
            ? Lobby.NetService.NetId
            : LobbyBackend.Current.Owner(SteamLobbyId);
        PendingRunStart = (Size, hostId, Time.GetTicksMsec());
        LobbyStatusPanel.Hide(screen);
        Reset("run started");
    }

    /// <summary>
    /// Called (via patch) after character select opened. Opening always selects the first
    /// character, so after a move the one picked in the previous lobby is selected again.
    /// </summary>
    public static void OnCharacterSelectOpened(NCharacterSelectScreen screen)
    {
        if (_pendingCharacter is not { } character || Lobby == null || screen.Lobby != Lobby)
        {
            return;
        }
        _pendingCharacter = null;
        _ = RestoreCharacterAsync(screen, character, _generation);
    }

    /// <summary>
    /// Start gate: a quick-match lobby must be full before the run may begin. Without it two ready
    /// players in a 3-player lobby would start a 2-player run.
    /// </summary>
    public static bool BlocksStart(StartRunLobby lobby)
    {
        return Lobby != null && lobby == Lobby && lobby.Players.Count < Size;
    }

    private static async Task RunAsync(int gen, CancellationToken ct, Func<Task> body, string what)
    {
        try
        {
            await body();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            MainFile.Logger.Info($"{what} stopped");
        }
        catch (Exception e)
        {
            MainFile.Logger.Error($"{what} failed: {e}");
            if (gen == _generation)
            {
                Reset("error");
            }
        }
    }

    private static async Task SearchJoinOrHostAsync(CancellationToken ct)
    {
        for (int round = 1; round <= SearchRounds; round++)
        {
            SetState(MatchState.Searching, $"round {round}");
            SizePicker.ShowStatus(Loc.Get("QUICKMATCH.searching"));
            List<LobbyCandidate> found = await LobbySearch.FindAsync(Size, ct);
            List<LobbyCandidate> ranked = MatchPolicy.Rank(found, SteamLobbyId, IsRecentlyFailed);
            foreach (LobbyCandidate candidate in ranked.Take(MaxJoinAttemptsPerRound))
            {
                if (await TryJoinAsync(candidate.Id, ct))
                {
                    return;
                }
            }
            if (round < SearchRounds)
            {
                // Jitter, so two players who started at the same moment do not both give up and host.
                await GodotDelay.Seconds(1 + Rng.NextDouble() * 2, ct);
            }
        }
        await HostAsync(ct);
    }

    private static async Task<bool> TryJoinAsync(ulong lobbyId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        SetState(MatchState.Joining, $"lobby {lobbyId}");
        SizePicker.ShowStatus(Loc.Get("QUICKMATCH.joining"));
        if (await ConnectAsync(lobbyId, ct) is not { } connected)
        {
            MarkFailed(lobbyId);
            return false;
        }
        (JoinFlow flow, JoinResult result) = connected;

        try
        {
            ct.ThrowIfCancellationRequested();
            if (result.sessionState != RunSessionState.InLobby || result.gameMode != GameMode.Standard || result.isCustom || !result.joinResponse.HasValue)
            {
                MainFile.Logger.Info($"join {lobbyId}: not an open standard lobby (state={result.sessionState} mode={result.gameMode} custom={result.isCustom})");
                flow.NetService.Disconnect(result.sessionState == RunSessionState.Running ? NetError.RunInProgress : NetError.Quit);
                MarkFailed(lobbyId);
                return false;
            }
            NMainMenuSubmenuStack stack = Stack() ?? throw new InvalidOperationException("main menu is gone");
            NCharacterSelectScreen screen = stack.GetSubmenuType<NCharacterSelectScreen>();
            screen.InitializeMultiplayerAsClient(flow.NetService, result.joinResponse.Value);
            BeginSession(screen, lobbyId, MatchState.InLobbyClient);
            SizePicker.ShowStatus(null);
            stack.Push(screen);
            MainFile.Logger.Info($"joined lobby {lobbyId} ({screen.Lobby.Players.Count}/{Size})");
            return true;
        }
        catch
        {
            // Cancelled (or broke) after connecting: do not leave a live connection behind.
            if (flow.NetService.IsConnected)
            {
                flow.NetService.Disconnect(NetError.Quit);
            }
            throw;
        }
    }

    /// <summary>
    /// Runs the game's join flow against one lobby without its error popups. Returns null if the
    /// connection failed or timed out (the flow has disconnected by itself); throws when cancelled.
    /// </summary>
    private static async Task<(JoinFlow Flow, JoinResult Result)?> ConnectAsync(ulong lobbyId, CancellationToken ct)
    {
        var flow = new JoinFlow(new NetClientGameService(PeerVersionInfo.LocalDefault()), ConnectionFor(lobbyId));
        _flow = flow;
        bool finished = false;
        using (ct.Register(() => flow.CancelToken.Cancel()))
        {
            _ = CancelJoinAfterTimeoutAsync(flow, () => finished, ct);
            try
            {
                // On failure JoinFlow disconnects by itself before throwing.
                JoinResult result = await flow.Begin((SceneTree)Engine.GetMainLoop());
                return (flow, result);
            }
            catch (ClientConnectionFailedException e)
            {
                MainFile.Logger.Info($"join {lobbyId} failed: {e.info.GetReason()} ({e.Message})");
                return null;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                MainFile.Logger.Info($"join {lobbyId} timed out after {JoinTimeoutSeconds}s");
                return null;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                MainFile.Logger.Warn($"join {lobbyId} error: {e}");
                return null;
            }
            finally
            {
                finished = true;
                if (_flow == flow)
                {
                    _flow = null;
                }
            }
        }
    }

    /// <summary>The game's Steam sockets when we and the host both have them, else lobby chat (see LobbyLinks).</summary>
    private static IClientConnectionInitializer ConnectionFor(ulong lobbyId)
    {
        if (LobbyLinks.JoinOverSockets(QuickMatchHostService.HasSockets, LobbyLinks.OffersSockets(lobbyId)))
        {
            MainFile.Logger.Info($"joining lobby {lobbyId} over Steam sockets");
            return SocketsConnection(lobbyId);
        }
        // ChatClient logs the join itself.
        return ChatConnectionInitializer.FromLobby(lobbyId);
    }

    // Kept apart, so a phone's runtime never compiles code that uses the game's Steam client.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static IClientConnectionInitializer SocketsConnection(ulong lobbyId) => SteamClientConnectionInitializer.FromLobby(lobbyId);

    /// <summary>
    /// Polls for the lobby in which the host has reopened our saved run, and joins it. The game's
    /// load lobby takes over from there (it only admits players who are in the save).
    /// </summary>
    private static async Task RejoinAsync(RunRecord record, CancellationToken ct)
    {
        ulong started = Time.GetTicksMsec();
        while (true)
        {
            SetState(MatchState.Rejoining, "waiting for the host");
            SizePicker.ShowStatus(Loc.Get("QUICKMATCH.rejoin_waiting"));
            foreach (ulong lobbyId in await LobbySearch.FindResumeAsync(record.Key, ct))
            {
                if (await TryJoinResumeAsync(lobbyId, ct))
                {
                    return;
                }
                SetState(MatchState.Rejoining, "join failed");
            }
            if (Time.GetTicksMsec() - started > RejoinTimeoutSeconds * 1000)
            {
                break;
            }
            await GodotDelay.Seconds(RejoinPollSeconds, ct);
        }

        Reset("rejoin timed out");
        if (await RegroupPrompt.AskKeepWaitingAsync())
        {
            if (SizePicker.IsOnTop())
            {
                StartRejoin();
            }
        }
        else
        {
            RunRecord.Clear("forgotten by the player");
            if (SizePicker.IsOnTop())
            {
                Stack()?.Pop();
            }
            if (Stack()?.GetSubmenuType<NMultiplayerSubmenu>() is { } submenu)
            {
                QuickMatchButton.Refresh(submenu);
            }
        }
    }

    private static async Task<bool> TryJoinResumeAsync(ulong lobbyId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        SizePicker.ShowStatus(Loc.Get("QUICKMATCH.joining"));
        if (await ConnectAsync(lobbyId, ct) is not { } connected)
        {
            return false;
        }
        (JoinFlow flow, JoinResult result) = connected;

        try
        {
            ct.ThrowIfCancellationRequested();
            if (result.sessionState != RunSessionState.InLoadedLobby || result.gameMode != GameMode.Standard || result.isCustom || !result.loadJoinResponse.HasValue)
            {
                MainFile.Logger.Info($"rejoin {lobbyId}: not a loaded standard lobby (state={result.sessionState} mode={result.gameMode} custom={result.isCustom})");
                flow.NetService.Disconnect(NetError.Quit);
                return false;
            }
            NMainMenuSubmenuStack stack = Stack() ?? throw new InvalidOperationException("main menu is gone");
            NMultiplayerLoadGameScreen screen = stack.GetSubmenuType<NMultiplayerLoadGameScreen>();
            screen.InitializeAsClient(flow.NetService, result.loadJoinResponse.Value);
            Reset("rejoined");
            stack.Push(screen);
            MainFile.Logger.Info($"rejoined saved run in lobby {lobbyId}");
            return true;
        }
        catch
        {
            if (flow.NetService.IsConnected)
            {
                flow.NetService.Disconnect(NetError.Quit);
            }
            throw;
        }
    }

    private static async Task CancelJoinAfterTimeoutAsync(JoinFlow flow, Func<bool> finished, CancellationToken ct)
    {
        try
        {
            await GodotDelay.Seconds(JoinTimeoutSeconds, ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        if (!finished())
        {
            flow.CancelToken.Cancel();
        }
    }

    private static async Task HostAsync(CancellationToken ct)
    {
        SetState(MatchState.CreatingHost, "no open lobby found");
        SizePicker.ShowStatus(Loc.Get("QUICKMATCH.creating"));

        var netService = new QuickMatchHostService();
        NetErrorInfo? error = await netService.StartQuickMatchHost(Size);
        if (ct.IsCancellationRequested)
        {
            if (netService.IsConnected)
            {
                netService.Disconnect(NetError.Quit);
            }
            ct.ThrowIfCancellationRequested();
        }
        if (error.HasValue)
        {
            MainFile.Logger.Warn($"could not open a lobby: {error.Value.GetReason()}");
            Reset("host failed");
            NErrorPopup? popup = NErrorPopup.Create(error.Value);
            if (popup != null)
            {
                NModalContainer.Instance?.Add(popup);
            }
            return;
        }
        if (netService.LobbyId is not { } lobbyId)
        {
            netService.Disconnect(NetError.InternalError);
            throw new InvalidOperationException("host has no Steam lobby");
        }
        NMainMenuSubmenuStack? stack = Stack();
        if (stack == null)
        {
            netService.Disconnect(NetError.Quit);
            throw new InvalidOperationException("main menu is gone");
        }

        // Same steps as NMultiplayerHostSubmenu.StartHostAsync, with the chosen size instead of 4.
        NCharacterSelectScreen screen = stack.GetSubmenuType<NCharacterSelectScreen>();
        screen.InitializeMultiplayerAsHost(netService, Size);
        _createdAt = LobbyBackend.Current.ServerTime;
        BeginSession(screen, lobbyId, MatchState.HostingWaiting);
        SizePicker.ShowStatus(null);
        stack.Push(screen);
        QuickMatchLobby.Publish(lobbyId, Size, _createdAt, netService.TakesSockets);

        int gen = _generation;
        _ = RunAsync(gen, ct, () => MergeLoopAsync(gen, ct), "merge loop");
    }

    /// <summary>
    /// While the lobby is not full, look for a better lobby of the same size with room for our
    /// whole group (two players who searched at the same moment both end up hosting; two pairs
    /// form for a 4-player game). If one exists, the group gives up our lobby and moves there.
    /// </summary>
    private static async Task MergeLoopAsync(int gen, CancellationToken ct)
    {
        while (true)
        {
            await GodotDelay.Seconds(MergeIntervalSeconds + Rng.NextDouble() * 3, ct);
            if (gen != _generation || State != MatchState.HostingWaiting)
            {
                return;
            }
            int group = SettledGroupSize();
            if (group < 1 || group >= Size)
            {
                continue;
            }
            List<LobbyCandidate> found = await LobbySearch.FindAsync(Size, ct, minFreeSlots: group);
            if (gen != _generation || State != MatchState.HostingWaiting)
            {
                return;
            }
            MainFile.Logger.Info($"merge check: own lobby {(found.Any(c => c.Id == SteamLobbyId) ? "is" : "is not")} visible in search");
            if (SettledGroupSize() != group)
            {
                continue;
            }
            var own = new LobbyCandidate(SteamLobbyId, group, _createdAt);
            LobbyCandidate? target = MatchPolicy.PickMergeTarget(own, found, IsRecentlyFailed, Size);
            if (target == null)
            {
                continue;
            }

            // Stop new players from arriving, then make sure nobody slipped in or left meanwhile.
            LobbyBackend.Current.SetJoinable(SteamLobbyId, false);
            if (SettledGroupSize() != group)
            {
                LobbyBackend.Current.SetJoinable(SteamLobbyId, true);
                MainFile.Logger.Info("merge aborted: the group changed");
                continue;
            }

            ulong targetId = target.Value.Id;
            MainFile.Logger.Info($"merge: moving group of {group} from lobby {SteamLobbyId} (t={_createdAt}) to {targetId} (n={target.Value.Members} t={target.Value.CreatedAt})");
            SetState(MatchState.Merging, $"to {targetId}");
            if (group > 1 && Lobby?.NetService is NetHostGameService host)
            {
                host.SendMessage(new QuickMatchMoveMessage { targetLobbyId = targetId });
                await WaitForClientsToLeaveAsync(host, ct);
            }
            await LeaveForAsync(targetId, ct);
            return;
        }
    }

    /// <summary>Gives the clients time to leave by themselves, so none sees a "host left" popup.</summary>
    private static async Task WaitForClientsToLeaveAsync(NetHostGameService host, CancellationToken ct)
    {
        ulong started = Time.GetTicksMsec();
        while (host.ConnectedPeers.Count > 0 && Time.GetTicksMsec() - started < MoveWaitSeconds * 1000)
        {
            await GodotDelay.Seconds(0.25, ct);
        }
        MainFile.Logger.Info($"merge: {host.ConnectedPeers.Count} client(s) still connected after waiting");
    }

    /// <summary>Closes our character select (keeping the session and the pick) and joins the target lobby.</summary>
    private static async Task LeaveForAsync(ulong targetId, CancellationToken ct)
    {
        _pendingCharacter = Lobby?.LocalPlayer.character;
        _merging = true;
        try
        {
            CloseCharacterSelect();
        }
        finally
        {
            _merging = false;
        }
        if (QuickMatchHostService.HasSockets && LobbyLinks.OffersSockets(targetId) == null)
        {
            // A moving client only has the lobby's ID from its host; a search tells which connection it takes.
            await LobbySearch.FindAsync(Size, ct);
        }
        if (await TryJoinAsync(targetId, ct))
        {
            return;
        }
        // The pick is kept for whichever lobby the search ends up in.
        await SearchJoinOrHostAsync(ct);
    }

    /// <summary>Client side of a group move: the host told us where the group is going.</summary>
    private static void OnMoveMessage(QuickMatchMoveMessage message, ulong senderId)
    {
        if (State != MatchState.InLobbyClient || _cts == null || Lobby == null)
        {
            return;
        }
        ulong hostId = LobbyBackend.Current.Owner(SteamLobbyId);
        if (!MatchPolicy.AcceptsMove(senderId, hostId, message.targetLobbyId, SteamLobbyId))
        {
            MainFile.Logger.Info($"merge: ignored a move to lobby {message.targetLobbyId} from {senderId}; the host is {hostId}");
            return;
        }
        MainFile.Logger.Info($"merge: host {senderId} moves the group to lobby {message.targetLobbyId}");
        SetState(MatchState.Merging, $"to {message.targetLobbyId}");
        CancellationToken ct = _cts.Token;
        int gen = _generation;
        // Run outside the message dispatch: leaving tears down the connection that delivered this.
        Callable.From(() =>
        {
            if (gen == _generation)
            {
                _ = RunAsync(gen, ct, () => LeaveForAsync(message.targetLobbyId, ct), "move");
            }
        }).CallDeferred();
    }

    /// <summary>
    /// Number of players in our lobby if we host it and nobody is mid-connect, else 0. Our lobby
    /// list entry, the game's player list and the connected peers must all agree.
    /// </summary>
    private static int SettledGroupSize()
    {
        if (Lobby?.NetService is not NetHostGameService host)
        {
            return 0;
        }
        int players = Lobby.Players.Count;
        if (host.ConnectedPeers.Count != players - 1)
        {
            return 0;
        }
        return LobbyBackend.Current.Members(SteamLobbyId).Count <= players ? players : 0;
    }

    private static async Task RestoreCharacterAsync(NCharacterSelectScreen screen, CharacterModel character, int gen)
    {
        // After the opening selection and any focus change it triggers have settled.
        try
        {
            await GodotDelay.Seconds(0.2, CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        if (gen != _generation || !GodotObject.IsInstanceValid(screen) || Lobby == null || screen.Lobby != Lobby)
        {
            return;
        }
        NCharacterSelectButton? button = screen.GetNode<Control>("CharSelectButtons/ButtonContainer")
            .GetChildren()
            .OfType<NCharacterSelectButton>()
            .FirstOrDefault(b => !b.IsRandom && !b.IsLocked && b.Character?.Id == character.Id);
        if (button == null)
        {
            MainFile.Logger.Info($"previous character {character.Id} is not available here");
            return;
        }
        button.GrabFocus();
        button.Select();
        MainFile.Logger.Info($"picked {character.Id} again after the move");
    }

    /// <summary>Pops character select the same way its Back button does (tears down the host lobby).</summary>
    private static void CloseCharacterSelect()
    {
        NMainMenuSubmenuStack? stack = Stack();
        if (stack != null && _screen != null && stack.Peek() == _screen)
        {
            stack.Pop();
        }
    }

    private static void BeginSession(NCharacterSelectScreen screen, ulong steamLobbyId, MatchState state)
    {
        _screen = screen;
        Lobby = screen.Lobby;
        SteamLobbyId = steamLobbyId;
        Lobby.PlayerConnected += OnLobbyPlayersChanged;
        Lobby.PlayerDisconnected += OnLobbyPlayersChanged;
        if (Lobby.NetService.Type == NetGameType.Client)
        {
            _handlerService = Lobby.NetService;
            _handlerService.RegisterMessageHandler<QuickMatchMoveMessage>(OnMoveMessage);
        }
        SetState(state, $"lobby {steamLobbyId}");
    }

    private static void EndSession()
    {
        if (Lobby != null)
        {
            Lobby.PlayerConnected -= OnLobbyPlayersChanged;
            Lobby.PlayerDisconnected -= OnLobbyPlayersChanged;
        }
        if (_handlerService != null)
        {
            try
            {
                _handlerService.UnregisterMessageHandler<QuickMatchMoveMessage>(OnMoveMessage);
            }
            catch (Exception e)
            {
                MainFile.Logger.Warn($"could not unregister move handler: {e.Message}");
            }
            _handlerService = null;
        }
        if (_screen != null && GodotObject.IsInstanceValid(_screen))
        {
            LobbyStatusPanel.Hide(_screen);
        }
        _screen = null;
        Lobby = null;
        SteamLobbyId = 0;
    }

    private static void OnLobbyPlayersChanged(StartRunLobbyPlayer _)
    {
        if (Lobby == null)
        {
            return;
        }
        MainFile.Logger.Info($"lobby {SteamLobbyId}: {Lobby.Players.Count}/{Size} players");
        if (Lobby.NetService.Type == NetGameType.Host)
        {
            QuickMatchLobby.UpdateMembers(SteamLobbyId, Lobby.Players.Count);
        }
        if (_screen != null && GodotObject.IsInstanceValid(_screen))
        {
            LobbyStatusPanel.Refresh(_screen);
        }
    }

    private static void Reset(string reason)
    {
        _generation++;
        CancellationTokenSource? cts = _cts;
        JoinFlow? flow = _flow;
        _cts = null;
        _flow = null;
        _pendingCharacter = null;
        EndSession();
        SetState(MatchState.Idle, reason);
        SizePicker.ShowStatus(null);
        cts?.Cancel();
        flow?.CancelToken.Cancel();
    }

    private static void SetState(MatchState state, string reason)
    {
        if (State != state)
        {
            MainFile.Logger.Info($"state {State} -> {state} ({reason})");
            State = state;
        }
    }

    private static bool IsRecentlyFailed(ulong lobbyId)
    {
        return RecentlyFailed.TryGetValue(lobbyId, out ulong at) && Time.GetTicksMsec() - at < FailedLobbyCooldownMs;
    }

    private static void MarkFailed(ulong lobbyId)
    {
        RecentlyFailed[lobbyId] = Time.GetTicksMsec();
    }

    private static NMainMenuSubmenuStack? Stack() => NGame.Instance?.MainMenu?.SubmenuStack;
}
