using System.Threading.Tasks;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using QuickMatch.Matchmaking;

namespace QuickMatch.Patches;

/// <summary>Follows quick-match runs from launch to end, so a dropped group can regroup.</summary>
public static class RunPatches
{
    /// <summary>Every run, new or loaded, goes through Launch once it is set up.</summary>
    [HarmonyPatch(typeof(RunManager), nameof(RunManager.Launch))]
    public static class Launched
    {
        public static void Postfix(RunState __result)
        {
            Regroup.OnRunLaunched(__result);
        }
    }

    [HarmonyPatch(typeof(RunManager), nameof(RunManager.CleanUp))]
    public static class CleaningUp
    {
        public static void Prefix()
        {
            Regroup.OnRunCleanUp();
        }
    }

    [HarmonyPatch(typeof(RunManager), nameof(RunManager.OnEnded))]
    public static class Ended
    {
        public static void Postfix()
        {
            Regroup.OnRunEnded();
        }
    }

    /// <summary>
    /// A client that loses the host mid-run goes to the rejoin search instead of the main menu
    /// with an error popup. Not called for game over or abandon (vanilla filters those first).
    /// </summary>
    [HarmonyPatch(typeof(RunManager), "ReturnToMainMenuWithError")]
    public static class LostHost
    {
        public static bool Prefix(NetErrorInfo info, ref Task __result)
        {
            if (Regroup.ReplaceDisconnectFlow(info) is not { } replacement)
            {
                return true;
            }
            __result = replacement;
            return false;
        }
    }

    /// <summary>
    /// The host reopens a saved run (vanilla Load, or a regroup). Our last quick-match run is
    /// reopened over lobby chat, like every quick-match lobby, and made findable by that run's
    /// players; other saves keep the vanilla host and friends-only lobby.
    /// </summary>
    [HarmonyPatch(typeof(NMultiplayerSubmenu), nameof(NMultiplayerSubmenu.StartHost))]
    public static class LoadHosting
    {
        public static bool Prefix(SerializableRun run)
        {
            string? key = RunRecord.KeyOf(run);
            if (key == null || RunRecord.Current?.Key != key || run.GameMode != GameMode.Standard || run.IsCustomGame)
            {
                MainFile.Logger.Info($"loaded run key={key} is not our quick-match run; vanilla host");
                return true;
            }
            TaskHelper.RunSafely(Regroup.HostSavedRunAsync(run, key));
            return false;
        }
    }
}
