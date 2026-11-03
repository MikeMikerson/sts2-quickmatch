using System.Globalization;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer;
using QuickMatch.Lobby;
using QuickMatch.Matchmaking;

namespace QuickMatch.Steam;

/// <summary>Turns the host's (friends-only) lobby into a searchable quick-match lobby and back.</summary>
internal static class QuickMatchLobby
{
    /// <summary>The values a lobby must carry to match this game instance.</summary>
    public readonly record struct CompatKey(string Version, string Db, string Mods)
    {
        public static CompatKey Local()
        {
            PeerVersionInfo info = PeerVersionInfo.LocalDefault();
            return new CompatKey(
                info.version,
                info.idDatabaseHash.ToString(CultureInfo.InvariantCulture),
                MatchPolicy.ModsHash(info.gameplayAffectingMods));
        }

        public override string ToString() => $"ver={Version} db={Db} mods={Mods}";
    }

    private static ILobbyBackend Backend => LobbyBackend.Current;

    /// <param name="takesSockets">The host also takes the game's Steam sockets (a desktop host).</param>
    public static void Publish(ulong lobby, int size, uint createdAt, bool takesSockets)
    {
        CompatKey key = CompatKey.Local();
        Backend.SetData(lobby, LobbyKeys.Size, size.ToString(CultureInfo.InvariantCulture));
        Backend.SetData(lobby, LobbyKeys.Version, key.Version);
        Backend.SetData(lobby, LobbyKeys.Db, key.Db);
        Backend.SetData(lobby, LobbyKeys.Mods, key.Mods);
        Backend.SetData(lobby, LobbyKeys.CreatedAt, createdAt.ToString(CultureInfo.InvariantCulture));
        Backend.SetData(lobby, LobbyKeys.Members, "1");
        SetLink(lobby, takesSockets);
        // Marker last, so a lobby only matches searches once it is fully described.
        Backend.SetData(lobby, LobbyKeys.Proto, LobbyKeys.ProtoValue);
        bool isPublic = Backend.SetKind(lobby, LobbyKind.Public);
        MainFile.Logger.Info($"published lobby {lobby} size={size} t={createdAt} {key} {LinkText(takesSockets)} public={isPublic}");
    }

    private static void SetLink(ulong lobby, bool takesSockets)
    {
        if (takesSockets)
        {
            Backend.SetData(lobby, LobbyKeys.Link, LobbyKeys.LinkSockets);
        }
    }

    private static string LinkText(bool takesSockets) => takesSockets ? "link=sockets+chat" : "link=chat";

    public static void UpdateMembers(ulong lobby, int members)
    {
        Backend.SetData(lobby, LobbyKeys.Members, members.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>True if we own this lobby and it carries the quick-match marker.</summary>
    public static bool IsQuickMatch(ulong lobby)
    {
        return Backend.GetData(lobby, LobbyKeys.Proto) == LobbyKeys.ProtoValue;
    }

    /// <summary>Removes the marker so the lobby stops matching searches.</summary>
    public static void Unpublish(ulong lobby)
    {
        Backend.DeleteData(lobby, LobbyKeys.Proto);
    }

    /// <summary>
    /// Makes the host's load lobby findable by the players of the saved run. Strangers cannot get
    /// in: they do not know the key, and the game's load lobby turns away anyone not in the save.
    /// </summary>
    public static void PublishResume(ulong lobby, string runKey, bool takesSockets)
    {
        Backend.SetData(lobby, LobbyKeys.Version, CompatKey.Local().Version);
        Backend.SetData(lobby, LobbyKeys.ResumeKey, runKey);
        SetLink(lobby, takesSockets);
        Backend.SetData(lobby, LobbyKeys.ResumeProto, LobbyKeys.ResumeProtoValue);
        bool isPublic = Backend.SetKind(lobby, LobbyKind.Public);
        MainFile.Logger.Info($"published resume lobby {lobby} key={runKey} {LinkText(takesSockets)} public={isPublic}");
    }

    /// <summary>True if we own this lobby and it is a reopened quick-match run.</summary>
    public static bool IsResume(ulong lobby)
    {
        return Backend.GetData(lobby, LobbyKeys.ResumeProto) == LobbyKeys.ResumeProtoValue;
    }

    public static void UnpublishResume(ulong lobby)
    {
        Backend.DeleteData(lobby, LobbyKeys.ResumeProto);
        Backend.DeleteData(lobby, LobbyKeys.ResumeKey);
    }

    /// <summary>
    /// The game closes its lobby when the run starts (private) and reopens it as friends only. Our
    /// lobbies: closed drops the marker and goes private, open stays public. A reopened run is
    /// unlisted and private once it starts.
    /// </summary>
    public static void SetClosed(ulong lobby, bool isClosed)
    {
        if (IsResume(lobby))
        {
            if (isClosed)
            {
                UnpublishResume(lobby);
            }
            Backend.SetKind(lobby, isClosed ? LobbyKind.Private : LobbyKind.Public);
            MainFile.Logger.Info($"resume lobby {lobby} closed={isClosed}");
            return;
        }
        if (!IsQuickMatch(lobby))
        {
            Backend.SetKind(lobby, isClosed ? LobbyKind.Private : LobbyKind.FriendsOnly);
            return;
        }
        if (isClosed)
        {
            Unpublish(lobby);
            Backend.SetKind(lobby, LobbyKind.Private);
        }
        else
        {
            Backend.SetKind(lobby, LobbyKind.Public);
        }
        MainFile.Logger.Info($"lobby {lobby} closed={isClosed}");
    }

    /// <summary>
    /// When the host leaves, Steam hands the lobby to a remaining member. Unpublish first so it is
    /// never listed again without a host behind it.
    /// </summary>
    public static void OnHostStopping(ulong lobby, NetError reason)
    {
        if (IsResume(lobby))
        {
            UnpublishResume(lobby);
            Backend.SetJoinable(lobby, false);
            MainFile.Logger.Info($"resume lobby {lobby} unpublished (host stopping: {reason})");
            return;
        }
        if (!IsQuickMatch(lobby))
        {
            return;
        }
        Unpublish(lobby);
        Backend.SetJoinable(lobby, false);
        MainFile.Logger.Info($"lobby {lobby} unpublished (host stopping: {reason})");
    }
}
