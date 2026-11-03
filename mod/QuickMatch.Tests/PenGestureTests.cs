using System.Numerics;
using QuickMatch.MapPen;
using Xunit;

namespace QuickMatch.Tests;

public class PenGestureTests
{
    private sealed class Recorder : IPenActions
    {
        public List<string> Calls { get; } = [];

        public float Scrolled { get; private set; }

        public void BeginStroke(Vector2 position, bool quick) => Calls.Add($"begin {position.X},{position.Y}{(quick ? " quick" : "")}");

        public void ExtendStroke(Vector2 position) => Calls.Add($"extend {position.X},{position.Y}");

        public void EndStroke(bool quick) => Calls.Add(quick ? "end quick" : "end");

        public void Scroll(float dy) => Scrolled += dy;

        public void HoldReached() => Calls.Add("hold");
    }

    private readonly Recorder _actions = new();
    private readonly PenGesture _gesture;

    public PenGestureTests()
    {
        _gesture = new PenGesture(_actions);
    }

    private static Vector2 At(float x, float y) => new(x, y);

    [Fact]
    public void PenTap_OnMap_LeavesADot()
    {
        // Godot's order: the mouse press made from the touch reaches the map before the touch itself.
        _gesture.MarkOnMap();
        _gesture.Down(0, At(100, 100), 0, penMode: true, onMap: false);
        _gesture.Tick(50);
        _gesture.Up(0, 80, cancelled: false);

        Assert.Equal(["begin 100,100", "end"], _actions.Calls);
        Assert.False(_gesture.IsTouching);
    }

    [Fact]
    public void PenTap_OnAButton_DrawsNothing()
    {
        // The press never reached the map (the quill button took it), so leaving pen mode leaves no dot.
        _gesture.Down(0, At(100, 1000), 0, penMode: true, onMap: false);
        _gesture.Tick(50);
        _gesture.Up(0, 80, cancelled: false);

        Assert.Empty(_actions.Calls);
    }

    [Fact]
    public void PenHeldOnAButton_DrawsNothing()
    {
        _gesture.Down(0, At(100, 1000), 0, penMode: true, onMap: false);
        _gesture.Tick(PenGesture.PenCommitMs + 10);
        _gesture.Move(0, At(140, 1000), 200);
        _gesture.Up(0, 300, cancelled: false);

        Assert.Empty(_actions.Calls);
    }

    [Fact]
    public void PenDrag_StrokeStartsWhereTheFingerLanded()
    {
        _gesture.Down(0, At(100, 100), 0, penMode: true, onMap: true);
        _gesture.Move(0, At(105, 100), 10);
        _gesture.Move(0, At(130, 100), 20);
        _gesture.Move(0, At(160, 120), 30);
        _gesture.Up(0, 40, cancelled: false);

        Assert.Equal(["begin 100,100", "extend 130,100", "extend 160,120", "end"], _actions.Calls);
        Assert.Equal(1, _gesture.Strokes);
    }

    [Fact]
    public void PenHeldStill_CommitsAfterTheDelay()
    {
        _gesture.Down(0, At(100, 100), 0, penMode: true, onMap: true);
        _gesture.Tick(PenGesture.PenCommitMs - 1);
        Assert.Empty(_actions.Calls);

        _gesture.Move(0, At(104, 100), PenGesture.PenCommitMs - 1);
        _gesture.Tick(PenGesture.PenCommitMs);
        _gesture.Move(0, At(110, 100), PenGesture.PenCommitMs + 10);

        Assert.Equal(["begin 100,100", "extend 104,100", "extend 110,100"], _actions.Calls);
    }

    [Fact]
    public void PenTwoFingers_ScrollTheMapWithoutInk()
    {
        _gesture.Down(0, At(100, 100), 0, penMode: true, onMap: true);
        _gesture.Down(1, At(300, 100), 40, penMode: true, onMap: false);
        _gesture.Move(0, At(100, 150), 50);
        _gesture.Move(1, At(300, 150), 50);
        _gesture.Tick(500);
        _gesture.Up(0, 600, cancelled: false);
        _gesture.Move(1, At(300, 170), 610);
        _gesture.Up(1, 620, cancelled: false);

        Assert.Empty(_actions.Calls);
        // Centroid moved 25 (finger 0), 25 (finger 1), then 20 with one finger left.
        Assert.Equal(70f, _actions.Scrolled);
        Assert.Equal(1, _gesture.Scrolls);
        Assert.False(_gesture.IsTouching);
    }

    [Fact]
    public void PenSecondFingerAfterTheStrokeBegan_EndsItAndScrolls()
    {
        _gesture.Down(0, At(100, 100), 0, penMode: true, onMap: true);
        _gesture.Move(0, At(130, 100), 20);
        _gesture.Down(1, At(300, 100), 200, penMode: true, onMap: false);
        _gesture.Move(1, At(300, 140), 210);
        _gesture.Move(0, At(130, 999), 220);
        _gesture.Up(0, 300, cancelled: false);
        _gesture.Up(1, 300, cancelled: false);

        Assert.Equal(["begin 100,100", "extend 130,100", "end"], _actions.Calls);
        Assert.Equal(20f + 449.5f, _actions.Scrolled);
    }

    [Fact]
    public void QuickStroke_PressOnTheMapBeforeTheTouch()
    {
        // The phone's real order, which the first build lost by resetting the mark in Down.
        _gesture.MarkOnMap();
        _gesture.Down(0, At(100, 100), 0, penMode: false, onMap: false);
        _gesture.Tick(PenGesture.QuickHoldMs);
        _gesture.Move(0, At(150, 100), 500);
        _gesture.Up(0, 600, cancelled: false);

        Assert.Equal(["hold", "begin 100,100 quick", "extend 150,100", "end quick"], _actions.Calls);
    }

    [Fact]
    public void MapPress_DoesNotCarryOverToTheNextTouch()
    {
        // A press noted for a touch the map ignored, then a hold on a button.
        _gesture.MarkOnMap();
        _gesture.IgnoredDown();
        _gesture.Down(0, At(100, 100), 0, penMode: false, onMap: false);
        _gesture.Tick(PenGesture.QuickHoldMs);
        _gesture.Up(0, 600, cancelled: false);

        Assert.Empty(_actions.Calls);
    }

    [Fact]
    public void QuickStroke_HoldThenDrag()
    {
        // The other order (the press noted after the touch) works too.
        _gesture.Down(0, At(100, 100), 0, penMode: false, onMap: false);
        _gesture.MarkOnMap();
        _gesture.Move(0, At(103, 102), 100);
        _gesture.Tick(PenGesture.QuickHoldMs - 1);
        Assert.Empty(_actions.Calls);
        Assert.False(_gesture.QuickStrokeActive);

        _gesture.Tick(PenGesture.QuickHoldMs);
        Assert.True(_gesture.QuickStrokeActive);
        Assert.True(_gesture.TouchDrewQuickStroke);
        _gesture.Move(0, At(150, 140), 500);
        _gesture.Up(0, 600, cancelled: false);

        Assert.Equal(["hold", "begin 103,102 quick", "extend 150,140", "end quick"], _actions.Calls);
        Assert.False(_gesture.QuickStrokeActive);
        // Still true after the finger lifts, for the map point's release that follows.
        Assert.True(_gesture.TouchDrewQuickStroke);
        Assert.Equal(1, _gesture.QuickStrokes);

        _gesture.Down(0, At(100, 100), 1000, penMode: false, onMap: false);
        Assert.False(_gesture.TouchDrewQuickStroke);
    }

    [Fact]
    public void QuickStroke_MovingBeforeTheHoldIsTheGamesScroll()
    {
        _gesture.Down(0, At(100, 100), 0, penMode: false, onMap: true);
        _gesture.Move(0, At(100, 130), 100);
        _gesture.Tick(PenGesture.QuickHoldMs + 100);
        _gesture.Move(0, At(100, 200), 600);
        _gesture.Up(0, 700, cancelled: false);

        Assert.Empty(_actions.Calls);
        Assert.Equal(0f, _actions.Scrolled);
    }

    [Fact]
    public void QuickStroke_NotFromButtonsOrOtherControls()
    {
        _gesture.Down(0, At(100, 100), 0, penMode: false, onMap: false);
        _gesture.Tick(PenGesture.QuickHoldMs + 100);
        _gesture.Move(0, At(200, 200), 600);
        _gesture.Up(0, 700, cancelled: false);

        Assert.Empty(_actions.Calls);
    }

    [Fact]
    public void QuickStroke_SecondFingerBeforeTheHoldCancelsIt()
    {
        _gesture.Down(0, At(100, 100), 0, penMode: false, onMap: true);
        _gesture.Down(1, At(300, 100), 100, penMode: false, onMap: false);
        _gesture.Tick(PenGesture.QuickHoldMs + 100);
        _gesture.Up(1, 600, cancelled: false);
        _gesture.Tick(1000);
        _gesture.Up(0, 1100, cancelled: false);

        Assert.Empty(_actions.Calls);
        Assert.Equal(0f, _actions.Scrolled);
    }

    [Fact]
    public void QuickStroke_SecondFingerDuringItIsIgnored()
    {
        _gesture.Down(0, At(100, 100), 0, penMode: false, onMap: true);
        _gesture.Tick(PenGesture.QuickHoldMs);
        _gesture.Down(1, At(300, 100), 500, penMode: false, onMap: false);
        _gesture.Move(1, At(300, 200), 510);
        _gesture.Move(0, At(120, 100), 520);
        _gesture.Up(1, 530, cancelled: false);
        _gesture.Up(0, 540, cancelled: false);

        Assert.Equal(["hold", "begin 100,100 quick", "extend 120,100", "end quick"], _actions.Calls);
        Assert.Equal(0f, _actions.Scrolled);
    }

    [Fact]
    public void TouchTakenAwayBySystem_EndsTheStrokeWithoutATap()
    {
        _gesture.Down(0, At(100, 100), 0, penMode: true, onMap: true);
        _gesture.Up(0, 50, cancelled: true);
        Assert.Empty(_actions.Calls);

        _gesture.Down(0, At(100, 100), 100, penMode: true, onMap: true);
        _gesture.Move(0, At(140, 100), 120);
        _gesture.Up(0, 150, cancelled: true);
        Assert.Equal(["begin 100,100", "extend 140,100", "end"], _actions.Calls);
    }

    [Fact]
    public void Abandon_ForgetsTheTouchWithoutDrawing()
    {
        _gesture.Down(0, At(100, 100), 0, penMode: false, onMap: true);
        _gesture.Tick(PenGesture.QuickHoldMs);
        _gesture.Abandon();
        _gesture.Move(0, At(200, 200), 500);
        _gesture.Up(0, 600, cancelled: false);
        _gesture.Tick(700);

        Assert.Equal(["hold", "begin 100,100 quick"], _actions.Calls);
        Assert.False(_gesture.IsTouching);
        Assert.False(_gesture.TouchDrewQuickStroke);
    }

    [Fact]
    public void UnknownFingers_AreIgnored()
    {
        // A finger that landed while the map was closed lifts after it opened.
        _gesture.Move(3, At(10, 10), 0);
        _gesture.Up(3, 10, cancelled: false);

        Assert.Empty(_actions.Calls);
        Assert.False(_gesture.IsTouching);
    }
}
