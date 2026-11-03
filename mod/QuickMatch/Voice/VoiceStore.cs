using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Godot;
using MegaCrit.Sts2.Core.Saves;

namespace QuickMatch.Voice;

/// <summary>The voice settings and choices kept on this device for the signed-in Steam account.</summary>
internal sealed class VoiceSettings
{
    public bool Enabled { get; set; } = true;
    public bool Hearing { get; set; } = true;
    public bool PushToTalkOnly { get; set; }
    public double Sensitivity { get; set; } = 0.5;
    public float Volume { get; set; } = 1f;
    /// <summary>A Godot <c>Key</c> value; V by default (the game binds no letter V).</summary>
    public long PushToTalkKey { get; set; } = (long)Key.V;
    public bool StrangersStartMuted { get; set; }
    public List<ulong> Muted { get; set; } = new();
    public List<ulong> Heard { get; set; } = new();
    public Dictionary<ulong, float> Volumes { get; set; } = new();
    /// <summary>The names of players with a mute choice, for the muted list once they have left the match.</summary>
    public Dictionary<ulong, string> Names { get; set; } = new();
}

/// <summary>
/// Loads and saves <see cref="VoiceSettings"/> as <c>quickmatch_voice.json</c> next to the modded
/// saves of the signed-in Steam account, and keeps a <see cref="VoiceMutes"/> in step with it.
/// Mutes are never sent anywhere. Runs on the game's thread.
/// </summary>
internal static class VoiceStore
{
    private const string FileName = "quickmatch_voice.json";

    private static VoiceSettings? _settings;
    private static VoiceMutes? _mutes;

    public static VoiceSettings Settings
    {
        get
        {
            Load();
            return _settings!;
        }
    }

    public static VoiceMutes Mutes
    {
        get
        {
            Load();
            return _mutes!;
        }
    }

    public static void Save()
    {
        if (_settings == null || _mutes == null)
        {
            return;
        }
        _settings.Muted = _mutes.Muted.ToList();
        _settings.Heard = _mutes.Heard.ToList();
        _settings.Volumes = _mutes.Volumes.ToDictionary(v => v.Key, v => v.Value);
        _settings.StrangersStartMuted = _mutes.StrangersStartMuted;
        foreach (ulong gone in _settings.Names.Keys.Where(id => !_settings.Muted.Contains(id) && !_settings.Heard.Contains(id)).ToList())
        {
            _settings.Names.Remove(gone);
        }
        try
        {
            string path = FilePath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(_settings, VoiceSettingsJson.Default.VoiceSettings));
        }
        catch (Exception e)
        {
            MainFile.Logger.Warn($"could not save voice settings: {e.Message}");
        }
    }

    /// <summary>Keeps a player's name beside their mute choice (saved with the next change).</summary>
    public static void RememberName(ulong player, string name)
    {
        Load();
        _settings!.Names[player] = name;
    }

    public static string? RememberedName(ulong player) => Settings.Names.TryGetValue(player, out string? name) ? name : null;

    private static void Load()
    {
        if (_settings != null)
        {
            return;
        }
        try
        {
            string path = FilePath();
            _settings = File.Exists(path) ? JsonSerializer.Deserialize(File.ReadAllText(path), VoiceSettingsJson.Default.VoiceSettings) : null;
        }
        catch (Exception e)
        {
            MainFile.Logger.Warn($"could not read voice settings: {e.Message}");
        }
        _settings ??= new VoiceSettings();
        _settings.Sensitivity = Math.Clamp(_settings.Sensitivity, 0, 1);
        _settings.Volume = Math.Clamp(_settings.Volume, 0f, 1f);
        _mutes = new VoiceMutes { StrangersStartMuted = _settings.StrangersStartMuted };
        _mutes.Load(_settings.Muted, _settings.Heard, _settings.Volumes);
        _mutes.Changed += Save;
    }

    /// <summary>Next to the modded saves of the signed-in Steam account, as the run record.</summary>
    private static string FilePath() =>
        Path.Combine(ProjectSettings.GlobalizePath(UserDataPathProvider.GetAccountScopedBasePath("modded")), FileName);
}

/// <summary>Generated at build time: the iPhone port has reflection-based JSON switched off.</summary>
[JsonSerializable(typeof(VoiceSettings))]
internal sealed partial class VoiceSettingsJson : JsonSerializerContext;
