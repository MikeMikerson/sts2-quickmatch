using System;
using System.IO;
using System.Linq;
using MegaCrit.Sts2.Core.Modding;
using QuickMatch.Logging;
using QuickMatch.Transport;
using QuickMatchRelay.Relay;

namespace QuickMatchRelay;

/// <summary>
/// Mod entry point; the game initializes it after QuickMatch, which it depends on. Gives QuickMatch a
/// relay connection (<see cref="RelayTransport.Current"/>), so its lobby chat matches with phones also
/// travel over the relay where that is faster. Matches between desktop players use the game's Steam
/// sockets and never touch the relay. Settings: <see cref="RelaySettings"/>.
/// </summary>
[ModInitializer("Initialize")]
public static class MainFile
{
    public const string ModId = "QuickMatchRelay";

    public static ModLogger Logger { get; private set; } = new(ModId);

    public static void Initialize()
    {
        Logger = new ModLogger(ModId);
        string? path = SettingsPath();
        string? text = null;
        try
        {
            if (path != null && File.Exists(path))
            {
                text = File.ReadAllText(path);
            }
        }
        catch (Exception e)
        {
            Logger.Warn($"could not read {path}: {e.Message}; the fast link stays off");
            return;
        }

        RelaySettings settings = RelaySettings.Parse(text, problem => Logger.Warn(problem));
        if (!settings.Enabled)
        {
            Logger.Info($"fast link off ({path ?? RelaySettings.FileName}); Quick Match uses Steam lobby chat only");
            return;
        }
        if (RelayTransport.Current != null)
        {
            Logger.Info("this platform already supplies the relay connection; leaving it");
            return;
        }
        RelayTransport.Current = new WebSocketRelayTransport(settings.Relay!);
        Logger.Info($"fast link through {settings.Relay!.Host} ({path})");
    }

    /// <summary>The settings file next to this mod's manifest, or null if the folder cannot be found.</summary>
    private static string? SettingsPath()
    {
        try
        {
            string? folder = ModManager.Mods.FirstOrDefault(m => m.manifest?.id == ModId)?.path;
            if (!string.IsNullOrEmpty(folder))
            {
                return Path.Combine(Godot.ProjectSettings.GlobalizePath(folder), RelaySettings.FileName);
            }
        }
        catch (Exception e)
        {
            Logger.Warn($"could not ask the game for this mod's folder: {e.Message}");
        }
        string location = typeof(MainFile).Assembly.Location;
        return location.Length > 0 ? Path.Combine(Path.GetDirectoryName(location)!, RelaySettings.FileName) : null;
    }
}
