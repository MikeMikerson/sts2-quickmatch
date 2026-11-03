using System;
using Godot;
using QuickMatch.Voice;

namespace QuickMatch.Ui;

/// <summary>
/// Voice state over a player's portrait (the run's player list and the lobby's player slots), beside
/// the game's own turn-ended and disconnected marks: a gold ring while they talk (even when muted, so
/// the listener knows whom they muted) and a small symbol in the corner for muted by you, turned down
/// for being too loud, listening only, or mic off. Display only: it never takes a touch.
/// </summary>
internal sealed class VoiceBadge
{
    private const string NodeName = "QuickMatchVoiceBadge";

    private enum Mark
    {
        None,
        MutedByYou,
        TooLoud,
        ListenOnly,
        MicOff,
        NoVoice,
    }

    private readonly Control _control;
    private readonly ulong _player;
    private SceneTree? _tree;
    private bool _speaking;
    private Mark _mark;

    private VoiceBadge(ulong player)
    {
        _player = player;
        _control = new Control
        {
            Name = NodeName,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            Visible = false,
        };
        _control.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _control.Draw += OnDraw;
        _control.TreeEntered += OnTreeEntered;
        _control.TreeExiting += OnTreeExiting;
    }

    /// <summary>Puts a badge over <paramref name="portrait"/> for <paramref name="player"/>, once.</summary>
    public static void Attach(Control portrait, ulong player)
    {
        if (portrait.GetNodeOrNull(NodeName) == null)
        {
            portrait.AddChild(new VoiceBadge(player)._control);
        }
    }

    private void OnTreeEntered()
    {
        _tree = _control.GetTree();
        _tree.ProcessFrame += OnProcessFrame;
    }

    private void OnTreeExiting()
    {
        if (_tree != null)
        {
            _tree.ProcessFrame -= OnProcessFrame;
            _tree = null;
        }
    }

    private void OnProcessFrame()
    {
        VoiceSession? session = VoiceChat.InMatch ? VoiceChat.Session : null;
        bool speaking = false;
        Mark mark = Mark.None;
        if (session != null)
        {
            if (_player == VoiceChat.LocalId)
            {
                LocalVoiceState state = session.LocalState;
                speaking = state == LocalVoiceState.Speaking;
                mark = state switch
                {
                    LocalVoiceState.Muted => Mark.MicOff,
                    LocalVoiceState.ListenOnly => Mark.ListenOnly,
                    LocalVoiceState.Off => Mark.NoVoice,
                    _ => Mark.None,
                };
            }
            else
            {
                speaking = session.IsSpeaking(_player);
                mark = session.IsMuted(_player) ? Mark.MutedByYou
                    : session.IsTooLoud(_player) ? Mark.TooLoud
                    : session.StatusOf(_player) switch
                    {
                        VoiceStatus.ListenOnly => Mark.ListenOnly,
                        VoiceStatus.Muted => Mark.MicOff,
                        VoiceStatus.Off => Mark.NoVoice,
                        _ => Mark.None,
                    };
            }
        }
        bool visible = speaking || mark != Mark.None;
        if (visible != _control.Visible)
        {
            _control.Visible = visible;
        }
        if (speaking != _speaking || mark != _mark)
        {
            _speaking = speaking;
            _mark = mark;
            _control.QueueRedraw();
        }
    }

    private void OnDraw()
    {
        Vector2 size = _control.Size;
        float side = Math.Min(size.X, size.Y);
        Vector2 centre = size / 2;
        if (_speaking)
        {
            float width = Math.Max(2.5f, side * 0.08f);
            _control.DrawArc(centre, side * 0.56f, 0, Mathf.Tau, 40, VoiceGlyphs.Shadow, width + 2, true);
            _control.DrawArc(centre, side * 0.56f, 0, Mathf.Tau, 40, VoiceGlyphs.Live, width, true);
        }
        if (_mark == Mark.None)
        {
            return;
        }
        // Bottom left, half outside the portrait, clear of the game's marks in the middle and top right.
        float icon = Math.Max(18f, side * 0.5f);
        var at = new Vector2(icon * 0.3f, size.Y - icon * 0.3f);
        VoiceGlyphs.Plate(_control, at, icon);
        float glyph = icon * 0.85f;
        switch (_mark)
        {
            case Mark.MutedByYou:
                VoiceGlyphs.Speaker(_control, at, glyph, VoiceGlyphs.Ink, 1);
                VoiceGlyphs.Slash(_control, at, glyph, VoiceGlyphs.Stop);
                break;
            case Mark.TooLoud:
                VoiceGlyphs.Speaker(_control, at, glyph, VoiceGlyphs.Warning, 2);
                break;
            case Mark.ListenOnly:
                VoiceGlyphs.Headphones(_control, at, glyph, VoiceGlyphs.Ink);
                break;
            case Mark.MicOff:
                VoiceGlyphs.Mic(_control, at, glyph, VoiceGlyphs.Ink);
                VoiceGlyphs.Slash(_control, at, glyph, VoiceGlyphs.Dim);
                break;
            case Mark.NoVoice:
                VoiceGlyphs.Mic(_control, at, glyph, VoiceGlyphs.Dim);
                VoiceGlyphs.Slash(_control, at, glyph, VoiceGlyphs.Dim);
                break;
        }
    }
}
