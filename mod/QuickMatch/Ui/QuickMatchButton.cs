using System;
using Godot;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Saves;
using QuickMatch.Matchmaking;

namespace QuickMatch.Ui;

/// <summary>
/// The "Quick Match" and "Rejoin Run" cards in the Multiplayer submenu, next to Host and Join.
/// Each is a fresh instance of the game's own submenu card scene (not a Duplicate(), whose
/// %Title / %Description unique-name lookups depend on scene ownership), sized and styled like its
/// neighbours.
/// </summary>
internal static class QuickMatchButton
{
    private const string NodeName = "QuickMatchButton";
    private const string RejoinNodeName = "QuickMatchRejoinButton";
    private const string ButtonScenePath = "res://scenes/ui/submenu_button.tscn";
    // The card's picture (an NSubmenuPortrait since game v0.113.0, a TextureRect named Icon before).
    internal const string Portrait = "SubmenuPortrait";

    // Hue for the card's hsv shader; the vanilla cards use 0 (Host) and 0.24 (Join).
    private const float Hue = 0.62f;
    private const float RejoinHue = 0.45f;

    public static void Attach(NMultiplayerSubmenu submenu)
    {
        var container = submenu.GetNode<Control>("ButtonContainer");
        if (container.HasNode(NodeName))
        {
            return;
        }
        var join = container.GetNode<NSubmenuButton>("JoinButton");
        AddCard(container, join, NodeName, Hue, "QUICKMATCH", SizePicker.Open);
        AddCard(container, join, RejoinNodeName, RejoinHue, "QUICKMATCH_REJOIN", OnRejoinPressed);
        Refresh(submenu);
    }

    /// <summary>
    /// Quick Match mirrors the Host card: hidden while a multiplayer save exists (Load/Abandon take
    /// the row). Rejoin shows for a player with a quick-match run to return to; the host of that run
    /// has the save and uses the vanilla Load card instead.
    /// </summary>
    public static void Refresh(NMultiplayerSubmenu submenu)
    {
        bool hasSave = SaveManager.Instance.HasMultiplayerRunSave;
        RunRecord? record = RunRecord.Current;
        if (record != null && record.IsHost && !hasSave)
        {
            // Our own run's save is gone (abandoned from this menu, or deleted).
            RunRecord.Clear("host has no save any more");
            record = null;
        }
        var button = submenu.GetNodeOrNull<NSubmenuButton>($"ButtonContainer/{NodeName}");
        if (button != null)
        {
            button.Visible = !hasSave;
        }
        var rejoin = submenu.GetNodeOrNull<NSubmenuButton>($"ButtonContainer/{RejoinNodeName}");
        if (rejoin != null)
        {
            rejoin.Visible = record != null && !hasSave;
        }
    }

    private static void OnRejoinPressed()
    {
        SizePicker.Open();
        if (SizePicker.IsOnTop())
        {
            Matchmaker.StartRejoin();
        }
    }

    private static void AddCard(Control container, NSubmenuButton join, string name, float hue, string locKey, Action onPressed)
    {
        var button = GD.Load<PackedScene>(ButtonScenePath).Instantiate<NSubmenuButton>();
        button.Name = name;
        button.CustomMinimumSize = join.CustomMinimumSize;
        button.SizeFlagsHorizontal = join.SizeFlagsHorizontal;
        button.SizeFlagsVertical = join.SizeFlagsVertical;

        // Own copy of the shader material, set before the card is ready (it caches the material then),
        // so hovering this card never lights up another one.
        var bg = button.GetNode<Control>("BgPanel");
        if (bg.Material is ShaderMaterial material)
        {
            var copy = (ShaderMaterial)material.Duplicate();
            copy.SetShaderParameter("h", hue);
            bg.Material = copy;
        }
        button.GetNode<TextureRect>(Portrait).Texture = join.GetNode<TextureRect>(Portrait).Texture;

        container.AddChild(button);
        container.MoveChild(button, join.GetIndex());
        button.SetEnabled(Matchmaker.IsAvailable);
        button.SetLocalization(locKey);
        button.Connect(NClickableControl.SignalName.Released, Callable.From<NButton>(_ => onPressed()));
    }
}
