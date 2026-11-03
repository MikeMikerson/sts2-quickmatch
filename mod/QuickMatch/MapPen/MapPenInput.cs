using Godot;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Nodes.Screens.ScreenContext;
using NumericsVector2 = System.Numerics.Vector2;

namespace QuickMatch.MapPen;

/// <summary>
/// Map drawing on a touchscreen (phones), where the game's right-click and middle-click drawing
/// cannot be reached: <see cref="PenGesture"/> decides what each touch means, and this draws it
/// through the game's own NMapDrawings, so colours, the eraser, Clear, saving and multiplayer sync
/// are all the game's. Touches come from the window, which reports every finger; the game itself
/// only sees the first finger, as a left mouse button. One per map screen, none without a
/// touchscreen, so desktop play is unchanged.
/// </summary>
internal sealed class MapPenInput : IPenActions
{
    // The game's 60 px drawing buttons are about 21 pt on a phone; this makes them about 34 pt.
    private const float ToolbarScale = 1.6f;
    private const float ToolbarLift = 8f;
    // ScrollHelper.GetDragForScrollEvent moves the map by -delta.y * 50 for a pan gesture.
    private const float PanScrollSpeed = 50f;

    private readonly NMapScreen _screen;
    private readonly Window _window;
    private bool _ticking;

    private MapPenInput(NMapScreen screen)
    {
        _screen = screen;
        _window = screen.GetWindow();
        Gesture = new PenGesture(this);
    }

    /// <summary>The map screen's touch drawing, or null without a touchscreen.</summary>
    public static MapPenInput? Current { get; private set; }

    public PenGesture Gesture { get; }

    public static void Attach(NMapScreen screen)
    {
        if (!DisplayServer.IsTouchscreenAvailable())
        {
            return;
        }
        Current?.Detach();
        var input = new MapPenInput(screen);
        Current = input;
        input.EnlargeToolbar();
        input._window.WindowInput += input.OnWindowInput;
        screen.VisibilityChanged += input.OnVisibilityChanged;
        screen.TreeExiting += input.Detach;
        MainFile.Logger.Info("map pen: touch drawing on");
    }

    /// <summary>The first finger's press reached the bare map: a pen stroke or a quick stroke may start there.</summary>
    public void OnMapPressed()
    {
        Gesture.MarkOnMap();
    }

    private void Detach()
    {
        _window.WindowInput -= OnWindowInput;
        _screen.VisibilityChanged -= OnVisibilityChanged;
        _screen.TreeExiting -= Detach;
        StopTicking();
        if (Current == this)
        {
            Current = null;
        }
    }

    /// <summary>Grows the game's Draw, Erase and Clear buttons from their bottom-left corner to a size a thumb can hit.</summary>
    private void EnlargeToolbar()
    {
        var tools = _screen.GetNode<Control>("%DrawingTools");
        tools.PivotOffset = new Vector2(0, tools.OffsetBottom - tools.OffsetTop);
        tools.Scale = new Vector2(ToolbarScale, ToolbarScale);
        tools.OffsetTop -= ToolbarLift;
        tools.OffsetBottom -= ToolbarLift;
    }

    private void OnWindowInput(InputEvent inputEvent)
    {
        double now = Time.GetTicksMsec();
        switch (inputEvent)
        {
            case InputEventScreenTouch { Pressed: true } touch:
                // Only while the map is the screen in front: not under the deck view, a popup or the pause menu.
                if (!_screen.IsVisibleInTree() || !ActiveScreenContext.Instance.IsCurrent(_screen))
                {
                    Gesture.IgnoredDown();
                    return;
                }
                Vector2 position = ToView(touch.Position);
                Gesture.Down(touch.Index, ToNumerics(position), now, PenToolOn(), !_screen.IsTraveling && IsOnMapPoint(position));
                StartTicking();
                break;
            case InputEventScreenTouch touch:
                // iOS reports a touch it takes away (an edge swipe, a notification) as a release at (-1, -1).
                Gesture.Up(touch.Index, now, touch.Canceled || touch.Position == new Vector2(-1, -1));
                if (!Gesture.IsTouching)
                {
                    StopTicking();
                }
                break;
            case InputEventScreenDrag drag when Gesture.IsTouching:
                Gesture.Move(drag.Index, ToNumerics(ToView(drag.Position)), now);
                break;
        }
    }

    private void OnProcessFrame()
    {
        Gesture.Tick(Time.GetTicksMsec());
    }

    private void OnVisibilityChanged()
    {
        if (_screen.Visible)
        {
            return;
        }
        // The game ends the local line and drawing mode itself when the map closes.
        Gesture.Abandon();
        StopTicking();
        if (Gesture.Strokes > 0 || Gesture.Scrolls > 0)
        {
            MainFile.Logger.Info($"map pen: {Gesture.Strokes} strokes ({Gesture.QuickStrokes} by holding), {Gesture.Scrolls} two-finger scrolls");
        }
    }

    private void StartTicking()
    {
        if (!_ticking)
        {
            _ticking = true;
            _screen.GetTree().ProcessFrame += OnProcessFrame;
        }
    }

    private void StopTicking()
    {
        if (_ticking)
        {
            _ticking = false;
            _screen.GetTree().ProcessFrame -= OnProcessFrame;
        }
    }

    private bool PenToolOn() => _screen.Drawings.GetLocalDrawingMode(useOverride: false) != DrawingMode.None;

    private bool IsOnMapPoint(Vector2 position)
    {
        foreach (Node child in _screen.GetNode("TheMap/Points").GetChildren())
        {
            if (child is NMapPoint point && point.IsVisibleInTree() && point.GetGlobalRect().HasPoint(position))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Window pixels to the game's view, the conversion the viewport applies to every event it receives.</summary>
    private Vector2 ToView(Vector2 windowPosition) => _window.GetFinalTransform().AffineInverse() * windowPosition;

    /// <summary>The game's view to the drawing layer, which scrolls with the map (as NMapScreen converts its mouse events).</summary>
    private Vector2 ToDrawings(NumericsVector2 viewPosition) =>
        _screen.Drawings.GetGlobalTransform().Inverse() * new Vector2(viewPosition.X, viewPosition.Y);

    private static NumericsVector2 ToNumerics(Vector2 vector) => new(vector.X, vector.Y);

    void IPenActions.BeginStroke(NumericsVector2 position, bool quick)
    {
        NMapDrawings drawings = _screen.Drawings;
        if (quick)
        {
            // As the desktop's right-click hold does: other players see the quill while it lasts.
            drawings.SetDrawingModeLocal(DrawingMode.Drawing);
        }
        if (drawings.IsLocalDrawing())
        {
            drawings.StopLineLocal();
        }
        drawings.BeginLineLocal(ToDrawings(position), null);
    }

    void IPenActions.ExtendStroke(NumericsVector2 position)
    {
        // The game may have ended the line already (the pen tool turned off, the screen changed).
        if (_screen.Drawings.IsLocalDrawing())
        {
            _screen.Drawings.UpdateCurrentLinePositionLocal(ToDrawings(position));
        }
    }

    void IPenActions.EndStroke(bool quick)
    {
        NMapDrawings drawings = _screen.Drawings;
        if (drawings.IsLocalDrawing())
        {
            drawings.StopLineLocal();
        }
        if (quick && drawings.GetLocalDrawingMode(useOverride: false) == DrawingMode.Drawing)
        {
            drawings.SetDrawingModeLocal(DrawingMode.None);
        }
    }

    void IPenActions.Scroll(float dy)
    {
        // The map's own pan-gesture scroll, which works while a drawing tool is on (its mouse drag does not).
        _screen._GuiInput(new InputEventPanGesture { Delta = new Vector2(0, -dy / PanScrollSpeed) });
    }

    void IPenActions.HoldReached()
    {
        // A light tick. Android's launcher may lack the vibrate permission, so iOS only for now.
        if (OS.GetName() == "iOS")
        {
            Input.VibrateHandheld(20, 0.5f);
        }
    }
}
