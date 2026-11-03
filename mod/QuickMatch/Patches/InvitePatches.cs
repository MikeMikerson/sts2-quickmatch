using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Nodes.Multiplayer;
using QuickMatch.Transport;

namespace QuickMatch.Patches;

/// <summary>
/// The lobby's Invite button opens the Steam overlay's invite, which vanilla only does for its own
/// Steam host or client and throws for QuickMatch's host, and which a client over lobby chat cannot
/// use (a friend who accepts joins over the game's Steam sockets). The button is hidden there. A
/// desktop client over Steam sockets keeps it, as in vanilla: its host takes Steam sockets too.
/// </summary>
public static class InvitePatches
{
    [HarmonyPatch(typeof(NInvitePlayersButton), nameof(NInvitePlayersButton.Initialize))]
    public static class HideForChatLobbies
    {
        public static void Postfix(NInvitePlayersButton __instance, StartRunLobby lobby)
        {
            if (QuickMatchHostService.UsesOwnTransport(lobby.NetService))
            {
                ((CanvasItem)__instance).Visible = false;
            }
        }
    }
}
