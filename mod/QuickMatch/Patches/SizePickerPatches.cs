using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using QuickMatch.Matchmaking;
using QuickMatch.Ui;

namespace QuickMatch.Patches;

/// <summary>
/// The size picker is an instance of the vanilla host submenu. On that instance only, the
/// Standard / Daily / Custom cards start a 2 / 3 / 4 player search instead of hosting.
/// </summary>
public static class SizePickerPatches
{
    [HarmonyPatch(typeof(NMultiplayerHostSubmenu), "OnStandardPressed")]
    public static class StandardPressed
    {
        public static bool Prefix(NMultiplayerHostSubmenu __instance) => !TryStart(__instance, 2);
    }

    [HarmonyPatch(typeof(NMultiplayerHostSubmenu), "OnDailyPressed")]
    public static class DailyPressed
    {
        public static bool Prefix(NMultiplayerHostSubmenu __instance) => !TryStart(__instance, 3);
    }

    [HarmonyPatch(typeof(NMultiplayerHostSubmenu), "OnCustomPressed")]
    public static class CustomPressed
    {
        public static bool Prefix(NMultiplayerHostSubmenu __instance) => !TryStart(__instance, 4);
    }

    [HarmonyPatch(typeof(NMultiplayerHostSubmenu), nameof(NMultiplayerHostSubmenu.OnSubmenuOpened))]
    public static class Opened
    {
        public static void Postfix(NMultiplayerHostSubmenu __instance)
        {
            if (SizePicker.Is(__instance))
            {
                SizePicker.OnOpened(__instance);
            }
        }
    }

    /// <summary>
    /// Leaving the picker (Back, or a Steam invite popping every submenu) cancels the search.
    /// NMultiplayerHostSubmenu does not override OnSubmenuClosed, so the base method is patched.
    /// </summary>
    [HarmonyPatch(typeof(NSubmenu), nameof(NSubmenu.OnSubmenuClosed))]
    public static class Closed
    {
        public static void Postfix(NSubmenu __instance)
        {
            if (SizePicker.Is(__instance))
            {
                Matchmaker.Cancel("size picker closed");
            }
        }
    }

    /// <returns>True if the press was on our picker and has been handled.</returns>
    private static bool TryStart(NMultiplayerHostSubmenu instance, int size)
    {
        if (!SizePicker.Is(instance))
        {
            return false;
        }
        Matchmaker.Start(size);
        return true;
    }
}
