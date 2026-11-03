using System;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Multiplayer;
using MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect;
using MegaCrit.Sts2.Core.Nodes.Screens.PauseMenu;
using MegaCrit.Sts2.addons.mega_text;
using QuickMatch.Localization;
using QuickMatch.Ui;
using QuickMatch.Voice;

namespace QuickMatch.Patches;

/// <summary>
/// Puts the voice controls into the game's screens. Each control hides itself outside a quick match
/// (<see cref="VoiceChat.InMatch"/>), so ordinary runs and other lobbies look as before.
/// </summary>
public static class VoicePatches
{
    /// <summary>The mic button in the run's top bar, left of Map.</summary>
    [HarmonyPatch(typeof(NTopBar), nameof(NTopBar._Ready))]
    public static class TopBar
    {
        public static void Postfix(NTopBar __instance)
        {
            Node? group = __instance.Map.GetParent();
            if (group == null || group.GetNodeOrNull("QuickMatchVoice") != null)
            {
                return;
            }
            Control button = VoiceButton.Create("QuickMatchVoice");
            group.AddChild(button);
            group.MoveChild(button, __instance.Map.GetIndex());
        }
    }

    /// <summary>Speaking ring and voice state on each player's portrait in the run's player list.</summary>
    [HarmonyPatch(typeof(NMultiplayerPlayerState), nameof(NMultiplayerPlayerState._Ready))]
    public static class PlayerRow
    {
        public static void Postfix(NMultiplayerPlayerState __instance)
        {
            if (__instance.Player != null && __instance.GetNodeOrNull<Control>("%CharacterIcon") is { } icon)
            {
                VoiceBadge.Attach(icon, __instance.Player.NetId);
            }
        }
    }

    /// <summary>Mute and volume in the panel a tapped player row opens.</summary>
    [HarmonyPatch(typeof(NMultiplayerPlayerExpandedState), nameof(NMultiplayerPlayerExpandedState.Create))]
    public static class PlayerPanel
    {
        public static void Postfix(Player player, NMultiplayerPlayerExpandedState __result)
        {
            if (player != null && __result != null)
            {
                VoiceStrip.Attach(__result, player.NetId);
            }
        }
    }

    /// <summary>Speaking ring and voice state on each player slot of the lobby.</summary>
    [HarmonyPatch(typeof(NRemoteLobbyPlayer), nameof(NRemoteLobbyPlayer._Ready))]
    public static class LobbySlot
    {
        public static void Postfix(NRemoteLobbyPlayer __instance)
        {
            if (__instance.GetNodeOrNull<Control>("%CharacterIcon") is { } icon)
            {
                VoiceBadge.Attach(icon, __instance.PlayerId);
            }
        }
    }

    /// <summary>The mic button in the lobby, just right of the player slots: voice works while players pick characters.</summary>
    [HarmonyPatch(typeof(NCharacterSelectScreen), nameof(NCharacterSelectScreen._Ready))]
    public static class Lobby
    {
        public static void Postfix(NCharacterSelectScreen __instance)
        {
            if (__instance.GetNodeOrNull("QuickMatchVoice") != null)
            {
                return;
            }
            Control button = VoiceButton.Create("QuickMatchVoice");
            var slots = __instance.GetNodeOrNull<Control>("RemotePlayerContainer");
            float left = slots != null ? slots.OffsetRight + 16 : 600;
            float top = slots != null ? slots.OffsetTop : 45;
            button.Position = new Vector2(left, top);
            __instance.AddChild(button);
        }
    }

    /// <summary>A Voice button in the pause menu, under Settings, made from the game's own Settings button.</summary>
    [HarmonyPatch(typeof(NPauseMenu), nameof(NPauseMenu._Ready))]
    public static class PauseMenu
    {
        private const string NodeName = "QuickMatchVoice";

        public static void Postfix(NPauseMenu __instance)
        {
            var container = __instance.GetNodeOrNull<Control>("%ButtonContainer");
            var settings = container?.GetNodeOrNull<NPauseMenuButton>("Settings");
            if (container == null || settings == null || container.GetNodeOrNull(NodeName) != null)
            {
                return;
            }
            // Without the signals: the menu connected Settings to its own handler, and the copy connects its own in _Ready.
            var voice = (NPauseMenuButton)settings.Duplicate((int)(Node.DuplicateFlags.Groups | Node.DuplicateFlags.Scripts | Node.DuplicateFlags.UseInstantiation));
            voice.Name = NodeName;
            // The highlight is a shader parameter on the button image's material: the copy needs its own,
            // or hovering Voice would light up Settings too. Before _Ready, which picks the material up.
            if (voice.GetNodeOrNull<CanvasItem>("ButtonImage") is { Material: { } material } image)
            {
                image.Material = (Material)material.Duplicate();
            }
            container.AddChild(voice);
            container.MoveChild(voice, settings.GetIndex() + 1);
            voice.GetNodeOrNull<MegaLabel>("Label")?.SetTextAutoSize(Loc.Get("QUICKMATCH.VOICE.title"));
            voice.Connect(NClickableControl.SignalName.Released, Callable.From<NButton>(_ => VoicePanel.Open()));
            voice.Visible = VoiceChat.InMatch;
            __instance.VisibilityChanged += () =>
            {
                if (GodotObject.IsInstanceValid(voice))
                {
                    voice.Visible = VoiceChat.InMatch;
                }
            };
        }
    }
}
