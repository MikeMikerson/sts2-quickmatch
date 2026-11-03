using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect;
using QuickMatch.Matchmaking;
using QuickMatch.Ui;

namespace QuickMatch.Patches;

/// <summary>Hooks the co-op character select so the matchmaker follows the lobby's lifecycle.</summary>
public static class CharacterSelectPatches
{
    [HarmonyPatch(typeof(NCharacterSelectScreen), nameof(NCharacterSelectScreen.OnSubmenuOpened))]
    public static class Opened
    {
        public static void Postfix(NCharacterSelectScreen __instance)
        {
            LobbyStatusPanel.Refresh(__instance);
            Matchmaker.OnCharacterSelectOpened(__instance);
        }
    }

    /// <summary>Prefix: the vanilla method nulls the lobby, and we need it to know whose screen closed.</summary>
    [HarmonyPatch(typeof(NCharacterSelectScreen), nameof(NCharacterSelectScreen.OnSubmenuClosed))]
    public static class Closing
    {
        public static void Prefix(NCharacterSelectScreen __instance)
        {
            Matchmaker.OnCharacterSelectClosing(__instance);
        }
    }

    [HarmonyPatch(typeof(NCharacterSelectScreen), nameof(NCharacterSelectScreen.BeginRun))]
    public static class RunBegins
    {
        public static void Postfix(NCharacterSelectScreen __instance)
        {
            Matchmaker.OnRunStarted(__instance);
        }
    }
}
