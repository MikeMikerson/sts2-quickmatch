using System.Collections.Generic;
using HarmonyLib;
using MegaCrit.Sts2.Core.Localization;

namespace QuickMatch.Localization;

/// <summary>
/// Merges the mod's strings into the game's <c>main_menu_ui</c> table, where
/// <c>NSubmenuButton.SetLocalization</c> looks up <c>KEY.title</c> / <c>KEY.description</c>.
///
/// Patches the private <c>LocManager.SetLanguageInternal</c> rather than <c>Initialize</c>:
/// every language load (first start and later language changes) goes through it with a fresh
/// set of tables, and merging before it runs means the strings exist before the game fires its
/// locale-change notification. <c>LocManager.Instance</c> is still null during the first call,
/// so the tables are taken from the method argument.
/// </summary>
[HarmonyPatch(typeof(LocManager), "SetLanguageInternal")]
internal static class LocSetup
{
    public const string Table = "main_menu_ui";

    private static readonly Dictionary<string, string> Entries = new()
    {
        { "QUICKMATCH.title", "Quick Match" },
        { "QUICKMATCH.description", "Team up with random players who are also looking for a group." },
        { "QUICKMATCH.LOCKED.description", "Quick Match needs Steam." },
        { "QUICKMATCH_2.title", "2 Players" },
        { "QUICKMATCH_2.description", "Find one other player." },
        { "QUICKMATCH_3.title", "3 Players" },
        { "QUICKMATCH_3.description", "Find two other players." },
        { "QUICKMATCH_4.title", "4 Players" },
        { "QUICKMATCH_4.description", "Find three other players." },
        { "QUICKMATCH.searching", "Looking for players..." },
        { "QUICKMATCH.joining", "Joining a group..." },
        { "QUICKMATCH.creating", "Opening a lobby..." },
        { "QUICKMATCH.waiting", "[thinky_dots]Waiting for players ({count}/{size})...[/thinky_dots]" },
        { "QUICKMATCH_REJOIN.title", "Rejoin Run" },
        { "QUICKMATCH_REJOIN.description", "Get back into your last Quick Match run once its host reopens it." },
        { "QUICKMATCH_REJOIN.LOCKED.description", "Rejoining needs Steam." },
        { "QUICKMATCH.rejoin_waiting", "Waiting for the host to reopen your run..." },
        { "QUICKMATCH.REJOIN_TIMEOUT.header", "Run Not Found" },
        { "QUICKMATCH.REJOIN_TIMEOUT.body", "The host has not reopened your run yet. They need to press Load in the Multiplayer menu." },
        { "QUICKMATCH.REJOIN_TIMEOUT.yes", "Keep Waiting" },
        { "QUICKMATCH.REJOIN_TIMEOUT.no", "Forget Run" },
        { "QUICKMATCH.REGROUP.header", "Player Disconnected" },
        { "QUICKMATCH.REGROUP.body", "{player} disconnected. Save and reopen the run so they can rejoin? Everyone else rejoins automatically. A fight in progress restarts." },
        { "QUICKMATCH.REGROUP.yes", "Regroup" },
        { "QUICKMATCH.REGROUP.no", "Keep Playing" },
        { "QUICKMATCH.VOICE.title", "Voice" },
        { "QUICKMATCH.VOICE.close", "Close" },
        { "QUICKMATCH.VOICE.you", "You" },
        { "QUICKMATCH.VOICE.players", "Players in this match" },
        { "QUICKMATCH.VOICE.no_players", "Nobody else has joined yet." },
        { "QUICKMATCH.VOICE.enabled", "Voice chat" },
        { "QUICKMATCH.VOICE.mic", "My mic" },
        { "QUICKMATCH.VOICE.hearing", "Hear voice" },
        { "QUICKMATCH.VOICE.talk_mode", "Talk mode" },
        { "QUICKMATCH.VOICE.voice_activated", "Voice activated" },
        { "QUICKMATCH.VOICE.push_to_talk", "Push to talk" },
        { "QUICKMATCH.VOICE.key", "Push-to-talk key" },
        { "QUICKMATCH.VOICE.press_a_key", "Press a key..." },
        { "QUICKMATCH.VOICE.sensitivity", "Mic sensitivity" },
        { "QUICKMATCH.VOICE.sensitivity_hint", "With your mic on, the bar turns gold while you would be heard." },
        { "QUICKMATCH.VOICE.volume", "Voice volume" },
        { "QUICKMATCH.VOICE.strangers", "Strangers start muted" },
        { "QUICKMATCH.VOICE.on", "On" },
        { "QUICKMATCH.VOICE.off", "Off" },
        { "QUICKMATCH.VOICE.mute", "Mute" },
        { "QUICKMATCH.VOICE.unmute", "Unmute" },
        { "QUICKMATCH.VOICE.mute_player", "Mute {player}" },
        { "QUICKMATCH.VOICE.unmute_player", "Unmute {player}" },
        { "QUICKMATCH.VOICE.mute_everyone", "Mute everyone else" },
        { "QUICKMATCH.VOICE.muted_list", "Muted players (remembered)" },
        { "QUICKMATCH.VOICE.private_note", "Only you know whom you muted. Their voice is thrown away before it plays." },
        { "QUICKMATCH.VOICE.status_speaking", "speaking" },
        { "QUICKMATCH.VOICE.status_live", "mic on" },
        { "QUICKMATCH.VOICE.status_muted", "mic off" },
        { "QUICKMATCH.VOICE.status_listen_only", "listening only" },
        { "QUICKMATCH.VOICE.status_off", "no voice" },
        { "QUICKMATCH.VOICE.status_unknown", "no voice yet" },
        { "QUICKMATCH.VOICE.status_muted_by_you", "muted by you" },
        { "QUICKMATCH.VOICE.status_too_loud", "too loud, turned down" },
        { "QUICKMATCH.VOICE.why_no_relay", "Voice travels over the Quick Match relay, which this match is not using." },
        { "QUICKMATCH.VOICE.why_no_relay_desktop", "Voice travels over the Quick Match relay. Install QuickMatchRelay to talk with this group." },
        { "QUICKMATCH.VOICE.why_off", "Voice chat is off. Hold this button to open the voice settings." },
        { "QUICKMATCH.VOICE.why_unavailable", "The relay connection to the other players is down, so voice is paused. The run carries on." },
        { "QUICKMATCH.VOICE.why_listen_only", "This device can only listen: its launcher does not allow the microphone." },
        { "QUICKMATCH.VOICE.why_denied", "Microphone access is off for this game. Turn it on in the system's privacy settings." },
        { "QUICKMATCH.VOICE.hold_to_talk", "Push to talk is on: hold {key} to talk." },
    };

    [HarmonyPrefix]
    public static void Prefix(Dictionary<string, LocTable> tables)
    {
        if (tables.TryGetValue(Table, out LocTable? table))
        {
            table.MergeWith(Entries);
        }
    }
}

internal static class Loc
{
    public static string Get(string key) => new LocString(LocSetup.Table, key).GetFormattedText();

    /// <summary>A string with one text value filled in, such as a player's name.</summary>
    public static string With(string key, string name, string value)
    {
        var text = new LocString(LocSetup.Table, key);
        text.Add(name, value);
        return text.GetFormattedText();
    }

    public static string Waiting(int count, int size)
    {
        var text = new LocString(LocSetup.Table, "QUICKMATCH.waiting");
        text.Add("count", count);
        text.Add("size", size);
        return text.GetFormattedText();
    }
}
