using QuickMatch.Logging;

namespace QuickMatchAndroid.Logging;

/// <summary>QuickMatch's godot.log writer. Godot cannot print outside the game, so tests use their own log.</summary>
internal sealed class GodotModLog(ModLogger logger) : IModLog
{
    public void Info(string message) => logger.Info(message);

    public void Warn(string message) => logger.Warn(message);
}
