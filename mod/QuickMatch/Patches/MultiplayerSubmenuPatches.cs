using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using QuickMatch.Ui;

namespace QuickMatch.Patches;

/// <summary>Adds the Quick Match card when the Multiplayer submenu is built.</summary>
[HarmonyPatch(typeof(NMultiplayerSubmenu), nameof(NMultiplayerSubmenu._Ready))]
public static class MultiplayerSubmenuReadyPatch
{
    public static void Postfix(NMultiplayerSubmenu __instance)
    {
        QuickMatchButton.Attach(__instance);
    }
}

/// <summary>Keeps the card's visibility in step with the Host card (e.g. after abandoning a run).</summary>
[HarmonyPatch(typeof(NMultiplayerSubmenu), "UpdateButtons")]
public static class MultiplayerSubmenuUpdateButtonsPatch
{
    public static void Postfix(NMultiplayerSubmenu __instance)
    {
        QuickMatchButton.Refresh(__instance);
    }
}

/// <summary>The submenu outlives a visit; refresh the cards whenever it comes back into view (the run record may have changed).</summary>
[HarmonyPatch(typeof(NMultiplayerSubmenu), "OnSubmenuShown")]
public static class MultiplayerSubmenuShownPatch
{
    public static void Postfix(NMultiplayerSubmenu __instance)
    {
        QuickMatchButton.Refresh(__instance);
    }
}
