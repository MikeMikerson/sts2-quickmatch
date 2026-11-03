using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Multiplayer;
using QuickMatch.Localization;

namespace QuickMatch.Ui;

/// <summary>Yes/no questions shown with the game's own confirmation popup.</summary>
internal static class RegroupPrompt
{
    /// <summary>Host, mid-run: a player dropped. True to save, quit and reopen the run now.</summary>
    public static Task<bool> AskRegroupAsync(string playerName)
    {
        var body = new LocString(LocSetup.Table, "QUICKMATCH.REGROUP.body");
        // The popup's text is BBCode: a Steam name must not be able to add tags of its own.
        body.Add("player", EscapeBbcode(playerName));
        return AskAsync(body, "QUICKMATCH.REGROUP.header", "QUICKMATCH.REGROUP.no", "QUICKMATCH.REGROUP.yes");
    }

    /// <summary>The rejoin search found nothing for a while. True to keep waiting, false to forget the run.</summary>
    public static Task<bool> AskKeepWaitingAsync()
    {
        return AskAsync(new LocString(LocSetup.Table, "QUICKMATCH.REJOIN_TIMEOUT.body"),
            "QUICKMATCH.REJOIN_TIMEOUT.header", "QUICKMATCH.REJOIN_TIMEOUT.no", "QUICKMATCH.REJOIN_TIMEOUT.yes");
    }

    /// <summary>Square brackets as BBCode's [lb] and [rb], so the text shows as it is.</summary>
    public static string EscapeBbcode(string text)
    {
        var escaped = new System.Text.StringBuilder(text.Length);
        foreach (char c in text)
        {
            escaped.Append(c switch { '[' => "[lb]", ']' => "[rb]", _ => c.ToString() });
        }
        return escaped.ToString();
    }

    private static Task<bool> AskAsync(LocString body, string header, string no, string yes)
    {
        NGenericPopup? popup = NGenericPopup.Create();
        if (popup == null || NModalContainer.Instance == null)
        {
            return Task.FromResult(false);
        }
        NModalContainer.Instance.Add(popup);
        return popup.WaitForConfirmation(body, new LocString(LocSetup.Table, header), new LocString(LocSetup.Table, no), new LocString(LocSetup.Table, yes));
    }
}
