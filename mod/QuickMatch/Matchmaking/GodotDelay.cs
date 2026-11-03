using System.Threading;
using System.Threading.Tasks;
using Godot;

namespace QuickMatch.Matchmaking;

/// <summary>
/// Real-time delays driven by a SceneTree timer, so continuations stay on the main thread.
/// (The game's Cmd.Wait returns immediately when fast mode is set to Instant.)
/// </summary>
internal static class GodotDelay
{
    public static async Task Seconds(double seconds, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var done = new TaskCompletionSource();
        var tree = (SceneTree)Engine.GetMainLoop();
        SceneTreeTimer timer = tree.CreateTimer(seconds, processAlways: true, processInPhysics: false, ignoreTimeScale: true);
        timer.Timeout += () => done.TrySetResult();
        using (ct.Register(() => done.TrySetCanceled(ct)))
        {
            await done.Task;
        }
    }
}
