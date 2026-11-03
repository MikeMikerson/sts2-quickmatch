using HarmonyLib;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using QuickMatch.Matchmaking;

namespace QuickMatch.Patches;

/// <summary>
/// A quick-match lobby only starts once it has the chosen number of players. Vanilla starts as
/// soon as everyone present is ready and there is more than one player.
/// </summary>
public static class StartGatePatches
{
    /// <summary>
    /// Makes the UI treat a partial lobby as "not about to begin", so Embark shows the vanilla
    /// "Waiting for other players" panel with an Unready button.
    /// </summary>
    [HarmonyPatch(typeof(StartRunLobby), nameof(StartRunLobby.IsAboutToBeginGame))]
    public static class AboutToBegin
    {
        public static void Postfix(StartRunLobby __instance, ref bool __result)
        {
            if (__result && Matchmaker.BlocksStart(__instance))
            {
                __result = false;
            }
        }
    }

    /// <summary>The host-side check that actually begins the run; gated as well in case the call above is inlined.</summary>
    [HarmonyPatch(typeof(StartRunLobby), "BeginRunForAllPlayersIfAllReady")]
    public static class BeginIfAllReady
    {
        public static bool Prefix(StartRunLobby __instance)
        {
            if (!Matchmaker.BlocksStart(__instance))
            {
                return true;
            }
            MainFile.Logger.Info($"start gate: waiting for {__instance.Players.Count}/{Matchmaker.Size} players");
            return false;
        }
    }
}
