using System;
using System.Collections.Generic;
using Godot;
using MegaCrit.Sts2.Core.Platform.Steam;
using QuickMatch.Lobby;
using QuickMatch.Transport;
using Steamworks;

namespace QuickMatch.Voice;

/// <summary>
/// Voice chat for the running quick match: one <see cref="VoiceSession"/> per match, carried by the
/// match's relay room. The lobby chat transports call <see cref="Tick"/> every frame with their relay
/// route and <see cref="End"/> when the match connection closes; the interface reads
/// <see cref="Session"/> and <see cref="InMatch"/>. Game thread only.
/// </summary>
internal static class VoiceChat
{
    /// <summary>Without a tick for this long the match is over (the transport stopped updating).</summary>
    private const long MatchGoneMs = 1500;

    private sealed class RelayLink : IVoiceLink
    {
        private readonly RelayRoute _route;

        public RelayLink(RelayRoute route) => _route = route;

        public bool IsUp => _route.VoiceUp;

        public void Send(VoicePacket packet) => _route.SendVoice(packet);
    }

    private static VoiceSession? _session;
    private static RelayRoute? _route;
    private static GodotVoiceOutput? _godotOutput;
    private static long _lastTickAt = long.MinValue;

    /// <summary>This match's voice, or null when the match has no relay room (desktop without QuickMatchRelay).</summary>
    public static VoiceSession? Session => _session;

    /// <summary>A quick match is running, so the voice controls are shown.</summary>
    public static bool InMatch => ChatClock.Now() - _lastTickAt < MatchGoneMs;

    /// <summary>The other players of the match who can hear us, for the Voice panel.</summary>
    public static IReadOnlyCollection<ulong> Members => _session?.Members ?? (IReadOnlyCollection<ulong>)Array.Empty<ulong>();

    public static ulong LocalId => LobbyBackend.Current.LocalId;

    /// <summary>The player refused the microphone (the device can record, but not for this game).</summary>
    public static bool MicDenied => VoiceDevice.Current?.CaptureState == VoiceCaptureState.Denied;

    public static string NameOf(ulong player)
    {
        string? name = LobbyBackend.Current.PlayerName(player) ?? VoiceStore.RememberedName(player);
        if (name == null && SteamInitializer.Initialized)
        {
            name = SteamFriends.GetFriendPersonaName(new CSteamID(player));
        }
        return string.IsNullOrEmpty(name) ? player.ToString() : name;
    }

    /// <summary>Mutes or unmutes a player, keeping their name for the muted list.</summary>
    public static void SetMuted(ulong player, bool muted)
    {
        VoiceStore.RememberName(player, NameOf(player));
        if (_session != null)
        {
            if (muted)
            {
                _session.Mute(player);
            }
            else
            {
                _session.Unmute(player);
            }
        }
        else if (muted)
        {
            VoiceStore.Mutes.Mute(player);
        }
        else
        {
            VoiceStore.Mutes.Unmute(player);
        }
    }

    public static void MuteEveryone()
    {
        foreach (ulong player in Members)
        {
            VoiceStore.RememberName(player, NameOf(player));
        }
        _session?.MuteEveryone();
    }

    /// <summary>Called by the lobby chat transports after their own update, every frame of a match.</summary>
    public static void Tick(RelayRoute? route)
    {
        _lastTickAt = ChatClock.Now();
        if (route != _route)
        {
            End();
            if (route == null)
            {
                return;
            }
            Begin(route);
        }
        if (_session == null || _route == null)
        {
            return;
        }
        Apply(VoiceStore.Settings, _session);
        _session.SetMembers(_route.VoiceMembers);
        _session.Update(_lastTickAt, PushToTalkHeld());
    }

    /// <summary>The match connection closed: stop recording and playing, and log what voice did.</summary>
    public static void End()
    {
        if (_route != null)
        {
            _route.VoiceReceived = null;
        }
        _session?.Dispose();
        _session = null;
        _route = null;
    }

    public static bool IsFriend(ulong player)
    {
        if (!SteamInitializer.Initialized)
        {
            return false;
        }
        return SteamFriends.GetFriendRelationship(new CSteamID(player)) == EFriendRelationship.k_EFriendRelationshipFriend;
    }

    /// <summary>True on desktop, where push to talk and the talk mode setting apply.</summary>
    public static bool HasKeyboard => !OperatingSystem.IsIOS() && !OperatingSystem.IsAndroid();

    private static void Begin(RelayRoute route)
    {
        // Desktop records through Steam; the iPhone app sets its own device before the game starts.
        VoiceDevice.Current ??= SteamVoiceDevice.TryCreate();
        IVoiceDevice? device = VoiceDevice.Current;
        IVoiceOutput output = device?.Output ?? (_godotOutput ??= new GodotVoiceOutput());
        var session = new VoiceSession(LocalId, device, output, VoiceStore.Mutes, IsFriend, line => MainFile.Logger.Info(line))
        {
            Link = new RelayLink(route),
        };
        Apply(VoiceStore.Settings, session);
        _session = session;
        _route = route;
        route.VoiceReceived = (from, packet) => _session?.Receive(from, packet);
        MainFile.Logger.Info($"voice: ready for this match, microphone {device?.CaptureState.ToString() ?? "none (listening only)"}");
    }

    private static void Apply(VoiceSettings settings, VoiceSession session)
    {
        session.Enabled = settings.Enabled;
        session.Hearing = settings.Hearing;
        session.PushToTalkOnly = settings.PushToTalkOnly && HasKeyboard;
        session.Sensitivity = settings.Sensitivity;
        session.Volume = settings.Volume;
    }

    private static bool PushToTalkHeld() =>
        HasKeyboard && DisplayServer.WindowIsFocused() && Input.IsPhysicalKeyPressed((Key)VoiceStore.Settings.PushToTalkKey);
}
