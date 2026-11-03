namespace QuickMatchAndroid.Logging;

/// <summary>Where the lobby code logs: godot.log in the game (<see cref="GodotModLog"/>), a list in tests.</summary>
internal interface IModLog
{
    void Info(string message);

    void Warn(string message);
}
