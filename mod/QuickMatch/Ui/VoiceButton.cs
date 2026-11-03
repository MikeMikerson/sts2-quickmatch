using System;
using Godot;
using QuickMatch.Localization;
using QuickMatch.Voice;

namespace QuickMatch.Ui;

/// <summary>
/// The mic button: in the run's top bar (left of Map, the size of the top bar's buttons) and on the
/// character select screen. A tap mutes or unmutes the player's mic; holding it (or a right click)
/// opens the <see cref="VoicePanel"/>. When the mic cannot be used, a tap explains why. Shown only
/// during a quick match. Built from a plain Control fed by Godot events, as the rest of the mod.
/// </summary>
internal sealed class VoiceButton
{
    public const float Size = 80f;
    private const long LongPressMs = 550;
    private const long HintMs = 4000;

    private readonly Control _control;
    private readonly Label _hint;
    private SceneTree? _tree;
    private long? _pressedAt;
    private bool _held;
    private long _hintUntil;

    private VoiceButton(string name)
    {
        _control = new Control
        {
            Name = name,
            CustomMinimumSize = new Vector2(Size, Size),
            MouseFilter = Control.MouseFilterEnum.Stop,
            FocusMode = Control.FocusModeEnum.None,
            Visible = false,
        };
        _hint = new Label
        {
            Visible = false,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            HorizontalAlignment = HorizontalAlignment.Right,
            CustomMinimumSize = new Vector2(460, 0),
            Position = new Vector2(Size - 460, Size + 8),
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        _hint.AddThemeColorOverride("font_color", VoiceGlyphs.Ink);
        _hint.AddThemeColorOverride("font_outline_color", Colors.Black);
        _hint.AddThemeConstantOverride("outline_size", 8);
        _hint.AddThemeFontSizeOverride("font_size", DisplayServer.IsTouchscreenAvailable() ? 30 : 22);
        _control.AddChild(_hint);
        _control.Draw += OnDraw;
        _control.GuiInput += OnGuiInput;
        _control.TreeEntered += OnTreeEntered;
        _control.TreeExiting += OnTreeExiting;
    }

    public static Control Create(string name) => new VoiceButton(name)._control;

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
        bool show = VoiceChat.InMatch;
        if (_control.Visible != show)
        {
            _control.Visible = show;
        }
        if (!show)
        {
            return;
        }
        long now = (long)Time.GetTicksMsec();
        if (_pressedAt is { } at && !_held && now - at >= LongPressMs)
        {
            _held = true;
            VoicePanel.Open();
        }
        if (_hint.Visible && now >= _hintUntil)
        {
            _hint.Visible = false;
        }
        _control.QueueRedraw();
    }

    private void OnGuiInput(InputEvent input)
    {
        if (input is not InputEventMouseButton button)
        {
            return;
        }
        if (button.ButtonIndex == MouseButton.Right && button.Pressed)
        {
            VoicePanel.Open();
            _control.AcceptEvent();
            return;
        }
        if (button.ButtonIndex != MouseButton.Left)
        {
            return;
        }
        if (button.Pressed)
        {
            _pressedAt = (long)Time.GetTicksMsec();
            _held = false;
        }
        else if (_pressedAt != null)
        {
            _pressedAt = null;
            if (!_held && _control.GetGlobalRect().HasPoint(button.GlobalPosition))
            {
                Tap();
            }
        }
        _control.AcceptEvent();
    }

    private void Tap()
    {
        VoiceSession? session = VoiceChat.Session;
        string? hint = VoiceText.WhyNoMic(session);
        if (hint != null)
        {
            ShowHint(hint);
            return;
        }
        if (session!.PushToTalkOnly)
        {
            ShowHint(VoiceText.HoldToTalk());
            return;
        }
        session.ToggleMic();
    }

    private void ShowHint(string text)
    {
        _hint.Text = text;
        _hint.Visible = true;
        _hintUntil = (long)Time.GetTicksMsec() + HintMs;
    }

    private void OnDraw()
    {
        if (!VoiceChat.InMatch)
        {
            return;
        }
        var c = _control.Size / 2;
        float size = Math.Min(_control.Size.X, _control.Size.Y) * 0.8f;
        VoiceSession? session = VoiceChat.Session;
        LocalVoiceState state = session?.LocalState ?? LocalVoiceState.Unavailable;
        switch (state)
        {
            case LocalVoiceState.Off:
                VoiceGlyphs.Mic(_control, c, size, VoiceGlyphs.Dim);
                VoiceGlyphs.Slash(_control, c, size, VoiceGlyphs.Dim);
                break;
            case LocalVoiceState.Unavailable:
                VoiceGlyphs.Mic(_control, c, size, VoiceGlyphs.Dim);
                _control.DrawCircle(c + new Vector2(size * 0.32f, -size * 0.32f), size * 0.1f, VoiceGlyphs.Warning);
                break;
            case LocalVoiceState.ListenOnly:
                VoiceGlyphs.Headphones(_control, c, size, VoiceGlyphs.Ink);
                break;
            case LocalVoiceState.Asking:
                float pulse = 0.5f + 0.5f * MathF.Sin(Time.GetTicksMsec() / 150f);
                VoiceGlyphs.Mic(_control, c, size, VoiceGlyphs.Ink with { A = 0.4f + 0.6f * pulse });
                break;
            case LocalVoiceState.Muted:
                VoiceGlyphs.Mic(_control, c, size, VoiceGlyphs.Ink);
                VoiceGlyphs.Slash(_control, c, size, VoiceGlyphs.Stop);
                break;
            case LocalVoiceState.Live:
            case LocalVoiceState.Speaking:
                if (state == LocalVoiceState.Speaking)
                {
                    _control.DrawArc(c, size * 0.5f, 0, Mathf.Tau, 32, VoiceGlyphs.Live with { A = 0.8f }, Math.Max(2f, size * 0.06f), true);
                }
                VoiceGlyphs.Mic(_control, c, size, VoiceGlyphs.Live);
                DrawLevel(c, size, (float)(session?.MicLevel ?? 0));
                break;
        }
    }

    /// <summary>A thin bar beside the microphone that rises with the player's voice.</summary>
    private void DrawLevel(Vector2 c, float size, float level)
    {
        var bar = new Rect2(c.X + size * 0.36f, c.Y - size * 0.3f, size * 0.08f, size * 0.6f);
        _control.DrawRect(bar, VoiceGlyphs.Shadow);
        float height = bar.Size.Y * Math.Clamp(level, 0f, 1f);
        _control.DrawRect(new Rect2(bar.Position.X, bar.End.Y - height, bar.Size.X, height), VoiceGlyphs.Live);
    }
}
