using Godot;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect;
using QuickMatch.Localization;
using QuickMatch.Matchmaking;

namespace QuickMatch.Ui;

/// <summary>
/// "Waiting for players (k/N)" at the top of character select while a quick-match lobby is not
/// full. A copy of the screen's own ReadyAndWaitingPanel, moved from the bottom to the top so it
/// never overlaps the vanilla "Waiting for other players" panel shown after Embark.
/// </summary>
internal static class LobbyStatusPanel
{
    private const string NodeName = "QuickMatchStatus";
    private const string LabelName = "WaitingForPlayers";

    public static void Refresh(NCharacterSelectScreen screen)
    {
        StartRunLobby? lobby = Matchmaker.Lobby;
        bool show = lobby != null && screen.Lobby == lobby && lobby.Players.Count < Matchmaker.Size;
        Control? panel = show ? GetOrCreate(screen) : screen.GetNodeOrNull<Control>(NodeName);
        if (panel == null)
        {
            return;
        }
        panel.Visible = show;
        if (show)
        {
            panel.GetNode<MegaRichTextLabel>(LabelName).Text = Loc.Waiting(lobby!.Players.Count, Matchmaker.Size);
        }
    }

    public static void Hide(NCharacterSelectScreen screen)
    {
        Control? panel = screen.GetNodeOrNull<Control>(NodeName);
        if (panel != null)
        {
            panel.Visible = false;
        }
    }

    private static Control GetOrCreate(NCharacterSelectScreen screen)
    {
        Control? existing = screen.GetNodeOrNull<Control>(NodeName);
        if (existing != null)
        {
            return existing;
        }
        var source = screen.GetNode<Control>("ReadyAndWaitingPanel");
        var panel = (Control)source.Duplicate();
        panel.Name = NodeName;
        // The copy must not claim the screen's %WaitingForPlayers unique name.
        foreach (Node child in panel.GetChildren())
        {
            child.UniqueNameInOwner = false;
        }
        // Same width and height as the original, anchored to the top centre instead of the bottom.
        float height = source.OffsetBottom - source.OffsetTop;
        panel.AnchorTop = 0;
        panel.AnchorBottom = 0;
        panel.OffsetTop = 40;
        panel.OffsetBottom = 40 + height;
        panel.MouseFilter = Control.MouseFilterEnum.Ignore;
        panel.Visible = false;
        screen.AddChild(panel);
        return panel;
    }
}
