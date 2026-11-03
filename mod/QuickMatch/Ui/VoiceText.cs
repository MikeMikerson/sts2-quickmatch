using Godot;
using QuickMatch.Localization;
using QuickMatch.Voice;

namespace QuickMatch.Ui;

/// <summary>The voice chat words shown to the player: why the mic cannot be used, and each player's state.</summary>
internal static class VoiceText
{
    /// <summary>Why tapping the mic button cannot turn the mic on, or null when it can.</summary>
    public static string? WhyNoMic(VoiceSession? session)
    {
        if (session == null)
        {
            return Loc.Get(VoiceChat.HasKeyboard ? "QUICKMATCH.VOICE.why_no_relay_desktop" : "QUICKMATCH.VOICE.why_no_relay");
        }
        return session.LocalState switch
        {
            LocalVoiceState.Off => Loc.Get("QUICKMATCH.VOICE.why_off"),
            LocalVoiceState.Unavailable => Loc.Get("QUICKMATCH.VOICE.why_unavailable"),
            LocalVoiceState.ListenOnly => Loc.Get(VoiceChat.MicDenied ? "QUICKMATCH.VOICE.why_denied" : "QUICKMATCH.VOICE.why_listen_only"),
            _ => null,
        };
    }

    public static string HoldToTalk() => Loc.With("QUICKMATCH.VOICE.hold_to_talk", "key", KeyName(VoiceStore.Settings.PushToTalkKey));

    public static string KeyName(long key) => OS.GetKeycodeString((Key)key);

    public static string OnOff(bool on) => Loc.Get(on ? "QUICKMATCH.VOICE.on" : "QUICKMATCH.VOICE.off");

    /// <summary>Another player's voice as the local player sees it, most important first.</summary>
    public static string StatusOf(VoiceSession session, ulong player)
    {
        if (session.IsMuted(player))
        {
            return Loc.Get("QUICKMATCH.VOICE.status_muted_by_you");
        }
        if (session.IsTooLoud(player))
        {
            return Loc.Get("QUICKMATCH.VOICE.status_too_loud");
        }
        if (session.IsSpeaking(player))
        {
            return Loc.Get("QUICKMATCH.VOICE.status_speaking");
        }
        return session.StatusOf(player) switch
        {
            VoiceStatus.Live => Loc.Get("QUICKMATCH.VOICE.status_live"),
            VoiceStatus.Muted => Loc.Get("QUICKMATCH.VOICE.status_muted"),
            VoiceStatus.ListenOnly => Loc.Get("QUICKMATCH.VOICE.status_listen_only"),
            VoiceStatus.Off => Loc.Get("QUICKMATCH.VOICE.status_off"),
            _ => Loc.Get("QUICKMATCH.VOICE.status_unknown"),
        };
    }
}
