using System;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using QuickMatch.Logging;

namespace QuickMatch.Transport;

/// <summary>
/// With QUICKMATCH_DEBUG=1 (<see cref="ModLogger.DebugEnabled"/>), one debug log line with a
/// millisecond UTC time for each of the game's action messages (card plays, end turn, choices) as it
/// leaves or reaches the game, so the logs of two players show where a card play's wait goes. The
/// game's own lines are stamped to the second only. Off by default: it writes a line for every action.
/// </summary>
internal static class ChatTiming
{
    public static void Note(string what, byte[] bytes, int length)
    {
        if (!ModLogger.DebugEnabled || length == 0 || !MessageTypes.TryGetMessageType(bytes[0], out Type? type) || type == null
            || !type.Name.Contains("Action"))
        {
            return;
        }
        MainFile.Logger.Debug($"timing: {what} {type.Name} ({length} B) at {DateTime.UtcNow:HH:mm:ss.fff}");
    }
}
