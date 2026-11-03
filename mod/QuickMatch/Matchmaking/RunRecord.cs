using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Godot;
using MegaCrit.Sts2.Core.Platform;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using QuickMatch.Lobby;

namespace QuickMatch.Matchmaking;

/// <summary>
/// The quick-match run this player last took part in. Every player writes it when a quick-match
/// run starts; it is how a client (which has no save file) knows it can rejoin, and how the host
/// knows that a save it loads came from quick match and should be findable by its group.
/// </summary>
internal sealed record RunRecord(string Key, ulong HostId, List<ulong> PlayerIds, int Size, long StartedAt)
{
    private const string FileName = "quickmatch_run.json";
    private static readonly TimeSpan MaxAge = TimeSpan.FromDays(7);

    private static RunRecord? _cached;
    private static bool _loaded;

    /// <summary>The stored record, or null if there is none or it has expired.</summary>
    public static RunRecord? Current
    {
        get
        {
            if (!_loaded)
            {
                _cached = Load();
                _loaded = true;
            }
            if (_cached != null && DateTimeOffset.UtcNow.ToUnixTimeSeconds() - _cached.StartedAt > MaxAge.TotalSeconds)
            {
                MainFile.Logger.Info($"run record {_cached.Key} expired");
                Clear("expired");
            }
            return _cached;
        }
    }

    public bool IsHost => HostId == LocalPlayerId();

    public static string KeyOf(RunState state) =>
        MatchPolicy.RunKey(state.Rng.StringSeed, state.Players.Select(p => p.NetId));

    public static string? KeyOf(SerializableRun save) =>
        save.SerializableRng?.Seed is { } seed ? MatchPolicy.RunKey(seed, save.Players.Select(p => p.NetId)) : null;

    /// <summary>True if the record describes this running game (a new or a reloaded quick-match run).</summary>
    public static bool Matches(RunState state) => Current is { } record && record.Key == KeyOf(state);

    public static void Save(RunState state, ulong hostId, int size)
    {
        var record = new RunRecord(KeyOf(state), hostId, state.Players.Select(p => p.NetId).ToList(), size, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        _cached = record;
        _loaded = true;
        try
        {
            string path = FilePath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(record, RunRecordJson.Default.RunRecord));
            MainFile.Logger.Info($"run record saved: key={record.Key} host={hostId} players={string.Join(",", record.PlayerIds)} seed={state.Rng.StringSeed}");
        }
        catch (Exception e)
        {
            MainFile.Logger.Warn($"could not save run record: {e.Message}");
        }
    }

    public static void Clear(string reason)
    {
        bool had = _cached != null || !_loaded;
        _cached = null;
        _loaded = true;
        try
        {
            string path = FilePath();
            if (File.Exists(path))
            {
                File.Delete(path);
                had = true;
            }
        }
        catch (Exception e)
        {
            MainFile.Logger.Warn($"could not delete run record: {e.Message}");
        }
        if (had)
        {
            MainFile.Logger.Info($"run record cleared ({reason})");
        }
    }

    /// <summary>
    /// Our Steam ID, as the lobby backend knows it: quick-match runs record players by Steam ID, and an
    /// app without Steamworks (Android's launcher) has a primary platform of None and a local ID of 1.
    /// </summary>
    public static ulong LocalPlayerId() =>
        LobbyBackend.Current.IsAvailable ? LobbyBackend.Current.LocalId : PlatformUtil.GetLocalPlayerId(PlatformType.None);

    private static RunRecord? Load()
    {
        try
        {
            string path = FilePath();
            return File.Exists(path) ? JsonSerializer.Deserialize(File.ReadAllText(path), RunRecordJson.Default.RunRecord) : null;
        }
        catch (Exception e)
        {
            MainFile.Logger.Warn($"could not read run record: {e.Message}");
            return null;
        }
    }

    /// <summary>Next to the modded saves of the signed-in Steam account.</summary>
    private static string FilePath() =>
        Path.Combine(ProjectSettings.GlobalizePath(UserDataPathProvider.GetAccountScopedBasePath("modded")), FileName);
}

/// <summary>
/// The record's JSON code, generated at build time: the iPhone port compiles the game ahead of time
/// with reflection-based JSON switched off, where the plain JsonSerializer calls failed. Same file
/// format as before.
/// </summary>
[JsonSerializable(typeof(RunRecord))]
internal sealed partial class RunRecordJson : JsonSerializerContext;
