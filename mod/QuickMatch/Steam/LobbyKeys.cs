namespace QuickMatch.Steam;

/// <summary>
/// Steam lobby data keys written by a quick-match host and filtered on by searchers.
/// Version, database hash and mods hash mirror what the multiplayer handshake compares,
/// so any lobby a search returns should also pass the game's own join checks.
/// </summary>
internal static class LobbyKeys
{
    /// <summary>Marker; bump the value if the protocol changes so old and new builds never match.</summary>
    public const string Proto = "qm_proto";
    /// <summary>
    /// 5 (QuickMatch 0.3.0): per-player relay keys, which 0.2.0 cannot read or check. 4 (0.2.0) was
    /// lobby chat with relay frames (QuickMatch.Transport.RelayRoute); 3 was lobby chat alone. Players
    /// of different releases never find each other's lobbies, and the connection's own protocol check
    /// (QuickMatch.Transport.ChatHostCore.Protocol) turns away anyone who joins one anyway.
    /// </summary>
    public const string ProtoValue = "5";

    public const string Size = "qm_size";
    public const string Version = "qm_ver";
    public const string Db = "qm_db";
    public const string Mods = "qm_mods";
    public const string CreatedAt = "qm_t";
    public const string Members = "qm_n";

    /// <summary>
    /// Set by a host that also takes the game's Steam sockets (a desktop host); absent when the host
    /// takes lobby chat only (a phone, or a build before this key). Older builds ignore it, so the
    /// protocol values stay. See QuickMatch.Transport.LobbyLinks.
    /// </summary>
    public const string Link = "qm_link";
    public const string LinkSockets = "sockets";

    /// <summary>
    /// Marker of a host reopening a saved quick-match run. Kept apart from <see cref="Proto"/>, so
    /// normal quick-match searches never list these lobbies.
    /// </summary>
    public const string ResumeProto = "qm_rproto";
    /// <summary>4 (QuickMatch 0.3.0): per-player relay keys, as <see cref="ProtoValue"/> 5. 3 was 0.2.0.</summary>
    public const string ResumeProtoValue = "4";

    /// <summary>Run key (seed and players) of the saved run; only players of that run search for it.</summary>
    public const string ResumeKey = "qm_resume";
}
