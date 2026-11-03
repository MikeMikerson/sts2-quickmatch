namespace QuickMatch.Logging;

/// <summary>Writes to godot.log with a mod prefix so lines are easy to grep.</summary>
public class ModLogger(string prefix)
{
    /// <summary>True when the game was started with the environment variable QUICKMATCH_DEBUG=1.</summary>
    public static bool DebugEnabled { get; } = System.Environment.GetEnvironmentVariable("QUICKMATCH_DEBUG") == "1";

    public void Info(string message) => Godot.GD.Print($"[{prefix}] {message}");

    /// <summary>Written only with <see cref="DebugEnabled"/>.</summary>
    public void Debug(string message)
    {
        if (DebugEnabled)
        {
            Godot.GD.Print($"[{prefix}] DEBUG {message}");
        }
    }

    public void Warn(string message) => Godot.GD.Print($"[{prefix}] WARN {message}");

    public void Error(string message) => Godot.GD.Print($"[{prefix}] ERROR {message}");
}
