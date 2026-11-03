using System.Collections.Generic;
using System.Numerics;

namespace QuickMatch.MapPen;

/// <summary>What a touch gesture asks of the map. <see cref="MapPenInput"/> turns these into the game's own drawing calls.</summary>
internal interface IPenActions
{
    /// <param name="quick">A press-and-hold stroke drawn without the pen tool (the phone's right-click hold).</param>
    void BeginStroke(Vector2 position, bool quick);

    void ExtendStroke(Vector2 position);

    void EndStroke(bool quick);

    /// <summary>Moves the map by <paramref name="dy"/> (positive follows the fingers down).</summary>
    void Scroll(float dy);

    /// <summary>The hold for a quick stroke was reached: a light haptic tick.</summary>
    void HoldReached();
}

/// <summary>
/// Map drawing for a touchscreen, which has no right or middle button. Positions are in the
/// game's 1920 x 1080 view, finger ids are the touch indices, times in milliseconds.
///
/// With the pen tool on (the map's Draw or Erase button), one finger draws and two fingers move
/// the map. A stroke starts only once the finger moves past <see cref="Slop"/> or stays down for
/// <see cref="PenCommitMs"/>, and then from where the finger landed, so the first finger of a
/// two-finger scroll leaves no mark. A tap leaves a dot, as a click does in the game.
///
/// Without the pen tool, one finger keeps the game's own map scroll and taps. Holding still on
/// the map for <see cref="QuickHoldMs"/> draws one stroke until the finger lifts.
///
/// A touch only draws when it landed on the map itself or on a map point (<see cref="Down"/>'s
/// onMap, or <see cref="MarkOnMap"/>), never on the drawing buttons or other controls.
/// </summary>
internal sealed class PenGesture(IPenActions actions)
{
    public const float Slop = 12f;
    public const double PenCommitMs = 150;
    public const double QuickHoldMs = 400;

    private enum Phase
    {
        Idle,
        /// <summary>First finger down, not yet a stroke, a scroll or the game's own touch.</summary>
        Pending,
        Stroke,
        /// <summary>Two fingers moving the map with the pen tool on.</summary>
        Scroll,
        /// <summary>The game handles this touch (its map scroll, a button); nothing until every finger lifts.</summary>
        Passive,
    }

    private readonly Dictionary<int, Vector2> _fingers = [];
    private Phase _phase;
    private bool _penMode;
    private bool _onMap;
    private bool _mapPressBeforeDown;
    private bool _quick;
    private int _primary;
    private Vector2 _downAt;
    private double _downMs;
    private float _centroidY;

    /// <summary>A quick stroke is being drawn; the game's own map scroll must not move the map under it.</summary>
    public bool QuickStrokeActive => _phase == Phase.Stroke && _quick;

    /// <summary>The current or last touch drew a quick stroke; its release must not choose a map point.</summary>
    public bool TouchDrewQuickStroke { get; private set; }

    public bool IsTouching => _fingers.Count > 0;

    public int Strokes { get; private set; }

    public int QuickStrokes { get; private set; }

    public int Scrolls { get; private set; }

    /// <param name="penMode">The game's Draw or Erase tool is on.</param>
    /// <param name="onMap">The finger landed on a map point (presses that reach the map arrive as <see cref="MarkOnMap"/>).</param>
    public void Down(int finger, Vector2 position, double nowMs, bool penMode, bool onMap)
    {
        bool mapPress = _mapPressBeforeDown;
        _mapPressBeforeDown = false;
        _fingers[finger] = position;
        if (_fingers.Count == 1)
        {
            _phase = Phase.Pending;
            _primary = finger;
            _penMode = penMode;
            _onMap = onMap || mapPress;
            _quick = false;
            _downAt = position;
            _downMs = nowMs;
            TouchDrewQuickStroke = false;
            return;
        }
        switch (_phase)
        {
            case Phase.Pending when _penMode:
                StartScroll();
                break;
            case Phase.Pending:
                _phase = Phase.Passive;
                break;
            case Phase.Stroke when !_quick:
                // A pen stroke that just began becomes a scroll; the short line already drawn stays.
                actions.EndStroke(quick: false);
                StartScroll();
                break;
            case Phase.Scroll:
                _centroidY = CentroidY();
                break;
        }
    }

    /// <summary>
    /// The first finger's press reached the map (not a button or other control). Godot sends the
    /// mouse press it makes from a touch before the touch itself, so this usually comes just before
    /// <see cref="Down"/>, which takes it over.
    /// </summary>
    public void MarkOnMap()
    {
        if (_phase == Phase.Pending)
        {
            _onMap = true;
        }
        else
        {
            _mapPressBeforeDown = true;
        }
    }

    /// <summary>A touch the map ignores (another screen in front): a press noted for it must not carry over to the next touch.</summary>
    public void IgnoredDown()
    {
        _mapPressBeforeDown = false;
    }

    public void Move(int finger, Vector2 position, double nowMs)
    {
        if (!_fingers.ContainsKey(finger))
        {
            return;
        }
        _fingers[finger] = position;
        switch (_phase)
        {
            case Phase.Pending when finger == _primary && Vector2.Distance(position, _downAt) > Slop:
                if (_penMode && _onMap)
                {
                    StartStroke(_downAt, quick: false);
                    actions.ExtendStroke(position);
                }
                else
                {
                    // Without the pen tool, a moving finger is the game's map scroll.
                    _phase = Phase.Passive;
                }
                break;
            case Phase.Stroke when finger == _primary:
                actions.ExtendStroke(position);
                break;
            case Phase.Scroll:
                float centroidY = CentroidY();
                float dy = centroidY - _centroidY;
                _centroidY = centroidY;
                if (dy != 0)
                {
                    actions.Scroll(dy);
                }
                break;
        }
    }

    /// <summary>Called every frame while a finger is down, so a finger held still still starts its stroke.</summary>
    public void Tick(double nowMs)
    {
        if (_phase != Phase.Pending)
        {
            return;
        }
        double held = nowMs - _downMs;
        if (_penMode && held >= PenCommitMs)
        {
            if (_onMap)
            {
                StartStroke(_downAt, quick: false);
                Vector2 current = _fingers[_primary];
                if (current != _downAt)
                {
                    actions.ExtendStroke(current);
                }
            }
            else
            {
                _phase = Phase.Passive;
            }
        }
        else if (!_penMode && held >= QuickHoldMs)
        {
            if (_onMap)
            {
                actions.HoldReached();
                StartStroke(_fingers[_primary], quick: true);
            }
            else
            {
                _phase = Phase.Passive;
            }
        }
    }

    /// <param name="cancelled">The system took the touch away (iOS reports it as a release at -1, -1): end without a tap.</param>
    public void Up(int finger, double nowMs, bool cancelled)
    {
        if (!_fingers.Remove(finger))
        {
            return;
        }
        if (finger == _primary)
        {
            switch (_phase)
            {
                case Phase.Pending when _penMode && _onMap && !cancelled:
                    // A tap: the game's BeginLine draws a dot.
                    StartStroke(_downAt, quick: false);
                    actions.EndStroke(quick: false);
                    break;
                case Phase.Stroke:
                    actions.EndStroke(_quick);
                    break;
            }
            if (_phase is Phase.Pending or Phase.Stroke)
            {
                _phase = Phase.Passive;
            }
        }
        if (_fingers.Count == 0)
        {
            _phase = Phase.Idle;
        }
        else if (_phase == Phase.Scroll)
        {
            // The remaining fingers carry on from where they are, without a jump.
            _centroidY = CentroidY();
        }
    }

    /// <summary>The map closed or its drawing was stopped by the game; forget the touch without drawing anything more.</summary>
    public void Abandon()
    {
        _fingers.Clear();
        _phase = Phase.Idle;
        _mapPressBeforeDown = false;
        TouchDrewQuickStroke = false;
    }

    private void StartStroke(Vector2 position, bool quick)
    {
        _phase = Phase.Stroke;
        _quick = quick;
        Strokes++;
        if (quick)
        {
            QuickStrokes++;
            TouchDrewQuickStroke = true;
        }
        actions.BeginStroke(position, quick);
    }

    private void StartScroll()
    {
        _phase = Phase.Scroll;
        Scrolls++;
        _centroidY = CentroidY();
    }

    private float CentroidY()
    {
        float sum = 0;
        foreach (Vector2 position in _fingers.Values)
        {
            sum += position.Y;
        }
        return sum / _fingers.Count;
    }
}
