using System;
using Godot;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using QuickMatch.Matchmaking;

namespace QuickMatch.Ui;

/// <summary>
/// The party-size screen: a second instance of the game's own multiplayer host submenu
/// (Standard / Daily / Custom), with its three cards relabelled 2 / 3 / 4 players. Card presses
/// are redirected to the matchmaker by <see cref="Patches.SizePickerPatches"/>. Its loading overlay
/// shows the search status, and Back cancels the search.
/// </summary>
internal static class SizePicker
{
    private const string HostIconPath = "res://images/ui/main_menu/submenu_host.png";

    private static readonly (string Node, int Size)[] Cards =
    [
        ("StandardButton", 2),
        ("DailyButton", 3),
        ("CustomRunButton", 4),
    ];

    private static NMultiplayerHostSubmenu? _picker;

    public static bool Is(NSubmenu submenu) => _picker != null && ReferenceEquals(submenu, _picker);

    /// <summary>True if the picker is the submenu currently shown.</summary>
    public static bool IsOnTop() =>
        _picker != null && GodotObject.IsInstanceValid(_picker) && NGame.Instance?.MainMenu?.SubmenuStack.Peek() == _picker;

    public static void Open()
    {
        NMainMenuSubmenuStack? stack = NGame.Instance?.MainMenu?.SubmenuStack;
        if (stack == null || !Matchmaker.IsAvailable)
        {
            return;
        }
        stack.Push(GetOrCreate(stack));
    }

    /// <summary>Called after the vanilla OnSubmenuOpened, which locks Daily/Custom by unlock progress.</summary>
    public static void OnOpened(NMultiplayerHostSubmenu picker)
    {
        foreach ((string node, _) in Cards)
        {
            var button = picker.GetNode<NSubmenuButton>(node);
            button.SetEnabled(true);
            button.RefreshLabels();
        }
        ShowStatus(null);
    }

    /// <summary>Shows a status line on the picker's loading overlay, or hides the overlay for null.</summary>
    public static void ShowStatus(string? text)
    {
        if (_picker == null || !GodotObject.IsInstanceValid(_picker))
        {
            return;
        }
        var overlay = _picker.GetNode<Control>("%LoadingOverlay");
        overlay.Visible = text != null;
        if (text != null)
        {
            overlay.GetNode<MegaLabel>("%Label").SetTextAutoSize(text);
        }
    }

    private static NMultiplayerHostSubmenu GetOrCreate(NMainMenuSubmenuStack stack)
    {
        // The main menu (and its submenu stack) is rebuilt after every run.
        if (_picker != null && GodotObject.IsInstanceValid(_picker) && _picker.GetParent() == stack)
        {
            return _picker;
        }
        NMultiplayerHostSubmenu picker = NMultiplayerHostSubmenu.Create()
            ?? throw new InvalidOperationException("could not create the size picker");
        picker.Name = "QuickMatchSizePicker";
        picker.Visible = false;
        stack.AddChild(picker);

        // Keep Back above the overlay so a search can always be cancelled with the mouse.
        picker.MoveChild(picker.GetNode<Node>("BackButton"), -1);

        Texture2D? icon = ResourceLoader.Exists(HostIconPath) ? GD.Load<Texture2D>(HostIconPath) : null;
        foreach ((string node, int size) in Cards)
        {
            var button = picker.GetNode<NSubmenuButton>(node);
            if (icon != null)
            {
                button.GetNode<TextureRect>("Icon").Texture = icon;
            }
            button.SetIconAndLocalization($"QUICKMATCH_{size}");
        }
        _picker = picker;
        return picker;
    }
}
