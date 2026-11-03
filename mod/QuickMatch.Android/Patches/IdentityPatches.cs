using System.Linq;
using HarmonyLib;
using MegaCrit.Sts2.Core.Platform;
using MegaCrit.Sts2.Core.Platform.Steam;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;

namespace QuickMatchAndroid.Patches;

/// <summary>
/// Steam identity for quick-match runs in the Android launcher. The game reports a quick-match run's
/// platform as Steam and then asks the Steam platform for the local player and for names; the
/// launcher's Steamworks stub answers one made-up Steam ID for every Android player and no names.
/// These answer the signed-in account instead. The None platform (the launcher's own saves under
/// default/1 and its LAN play) is never touched. Applied only when the mod is active.
/// </summary>
internal static class IdentityPatches
{
    /// <summary>Reads progress, run history and the abandon path of a finished Steam-platform run.</summary>
    [HarmonyPatch(typeof(SteamPlatformUtilStrategy), nameof(SteamPlatformUtilStrategy.GetLocalPlayerId))]
    private static class LocalPlayerId
    {
        private static bool Prefix(ref ulong __result)
        {
            ulong id = MainFile.Backend?.LocalId ?? 0;
            if (id == 0)
            {
                return true;
            }
            __result = id;
            return false;
        }
    }

    [HarmonyPatch(typeof(SteamPlatformUtilStrategy), nameof(SteamPlatformUtilStrategy.GetPlayerName))]
    private static class PlayerName
    {
        private static bool Prefix(ulong playerId, ref string __result)
        {
            if (MainFile.Backend is not { } backend)
            {
                return true;
            }
            // The game's own fallback when Steam knows no name.
            __result = backend.PlayerName(playerId) ?? playerId.ToString();
            return false;
        }
    }

    /// <summary>
    /// The multiplayer menu loads the run save with the primary platform's local ID, which is 1 here.
    /// A quick-match save lists Steam IDs, so the game would call the save invalid and rename it as
    /// broken. For a Steam-platform save that lists our Steam ID and not the given one, use ours.
    /// </summary>
    [HarmonyPatch(typeof(RunManager), nameof(RunManager.CanonicalizeSave))]
    private static class CanonicalizeSave
    {
        private static void Prefix(SerializableRun save, ref ulong localPlayerId)
        {
            ulong steamId = MainFile.Backend?.LocalId ?? 0;
            if (steamId == 0 || steamId == localPlayerId || save.PlatformType != PlatformType.Steam || save.Players == null)
            {
                return;
            }
            ulong given = localPlayerId;
            if (save.Players.Any(p => p.NetId == given) || !save.Players.Any(p => p.NetId == steamId))
            {
                return;
            }
            MainFile.Logger.Info($"loading a Steam-platform run save as {steamId} instead of {given}");
            localPlayerId = steamId;
        }
    }
}
