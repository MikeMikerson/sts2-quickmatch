using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using MegaCrit.Sts2.Core.ControllerInput;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using QuickMatch.Localization;
using QuickMatch.Voice;

namespace QuickMatch.Ui;

/// <summary>
/// Everything about voice in one place, opened from the pause menu's Voice button, the lobby's mic
/// button, or by holding the top bar's mic button. Left: the player's own voice (voice chat on or
/// off, mic, hearing, talk mode and key on desktop, sensitivity with a live meter, volume, strangers
/// start muted). Right: the other players of the match with their state, Mute and volume, "Mute
/// everyone else", and the remembered muted players. While it is open the game's hotkeys are held
/// back and Esc (or the controller's back) closes it, as the game's own screens do.
/// </summary>
internal sealed class VoicePanel
{
    private const string NodeName = "QuickMatchVoicePanel";
    private const int Layer = 100;

    private static VoicePanel? _open;

    private readonly CanvasLayer _layer;
    private readonly Control _panel;
    private readonly VBoxContainer _players;
    private readonly List<Action> _refresh = new();
    private readonly Action _closeAction;
    private SceneTree? _tree;
    private string _playersShown = "";
    private Button? _keyButton;
    private bool _waitingForKey;

    public static bool IsOpen => _open != null;

    public static void Open()
    {
        if (_open != null || Engine.GetMainLoop() is not SceneTree tree || tree.Root == null)
        {
            return;
        }
        _open = new VoicePanel(tree);
    }

    public static void CloseIfOpen() => _open?.Close();

    private VoicePanel(SceneTree tree)
    {
        _tree = tree;
        _closeAction = Close;
        Vector2 view = tree.Root.GetVisibleRect().Size;
        bool touch = VoiceWidgets.Touch;
        var size = new Vector2(Math.Min(view.X - 80, touch ? 2100 : 1500), Math.Min(view.Y - 60, touch ? 1010 : 820));

        _layer = new CanvasLayer { Name = NodeName, Layer = Layer };
        var backstop = new ColorRect { Color = new Color(0, 0, 0, 0.6f), MouseFilter = Control.MouseFilterEnum.Stop };
        backstop.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        backstop.GuiInput += OnBackstopInput;
        _layer.AddChild(backstop);
        var centre = new CenterContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        centre.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        backstop.AddChild(centre);

        var panel = new PanelContainer { CustomMinimumSize = size };
        panel.AddThemeStyleboxOverride("panel", VoiceWidgets.Box(VoiceWidgets.Plate, VoiceGlyphs.Live with { A = 0.7f }, 2));
        centre.AddChild(panel);
        _panel = panel;
        var margin = new MarginContainer();
        foreach (string side in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" })
        {
            margin.AddThemeConstantOverride(side, touch ? 36 : 28);
        }
        panel.AddChild(margin);
        var page = new VBoxContainer();
        margin.AddChild(page);

        var header = new HBoxContainer();
        header.AddChild(VoiceWidgets.Text(Loc.Get("QUICKMATCH.VOICE.title").ToUpperInvariant(), 34, VoiceGlyphs.Live, bold: true));
        header.AddChild(new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, MouseFilter = Control.MouseFilterEnum.Ignore });
        Button close = VoiceWidgets.Button(Loc.Get("QUICKMATCH.VOICE.close"), Close);
        header.AddChild(close);
        page.AddChild(header);
        Label why = Wrapped("", 22, VoiceGlyphs.Warning);
        page.AddChild(why);
        _refresh.Add(() =>
        {
            string? reason = VoiceChat.Session == null ? VoiceText.WhyNoMic(null) : null;
            why.Visible = reason != null;
            why.Text = reason ?? "";
        });
        page.AddChild(VoiceWidgets.Rule());

        var columns = new HBoxContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        columns.AddThemeConstantOverride("separation", touch ? 56 : 40);
        page.AddChild(columns);
        float leftWidth = size.X * 0.43f;
        VBoxContainer you = Column(columns, leftWidth, Control.SizeFlags.Fill);
        BuildYou(you, leftWidth);
        VBoxContainer others = Column(columns, 0, Control.SizeFlags.ExpandFill);
        others.AddChild(VoiceWidgets.Text(Loc.Get("QUICKMATCH.VOICE.players"), 26, VoiceGlyphs.Live, bold: true));
        _players = new VBoxContainer();
        _players.AddThemeConstantOverride("separation", touch ? 12 : 6);
        others.AddChild(_players);

        tree.Root.AddChild(_layer);
        tree.ProcessFrame += OnProcessFrame;
        NHotkeyManager? hotkeys = NHotkeyManager.Instance;
        if (hotkeys != null)
        {
            hotkeys.AddBlockingScreen(_layer);
            hotkeys.PushHotkeyReleasedBinding(MegaInput.pauseAndBack, _closeAction);
            hotkeys.PushHotkeyReleasedBinding(MegaInput.cancel, _closeAction);
        }
        OnProcessFrame();
        if (!touch)
        {
            close.GrabFocus();
        }
    }

    private void BuildYou(VBoxContainer you, float width)
    {
        you.AddChild(VoiceWidgets.Text(Loc.Get("QUICKMATCH.VOICE.you"), 26, VoiceGlyphs.Live, bold: true));
        VoiceSettings settings = VoiceStore.Settings;
        float labelWidth = width * 0.42f;

        AddSwitch(you, labelWidth, "QUICKMATCH.VOICE.enabled", () => settings.Enabled, on => settings.Enabled = on);

        HBoxContainer mic = Row(you, labelWidth, "QUICKMATCH.VOICE.mic");
        Button micButton = VoiceWidgets.Button("", () => VoiceChat.Session?.ToggleMic());
        mic.AddChild(micButton);
        Control meter = Meter();
        mic.AddChild(meter);
        _refresh.Add(() =>
        {
            VoiceSession? session = VoiceChat.Session;
            bool on = session?.LocalState is LocalVoiceState.Live or LocalVoiceState.Speaking or LocalVoiceState.Asking;
            micButton.Text = VoiceText.OnOff(on);
            micButton.Disabled = session == null || VoiceText.WhyNoMic(session) != null;
            VoiceWidgets.SetOn(micButton, on);
            meter.QueueRedraw();
        });

        AddSwitch(you, labelWidth, "QUICKMATCH.VOICE.hearing", () => settings.Hearing, on => settings.Hearing = on);

        if (VoiceChat.HasKeyboard)
        {
            HBoxContainer mode = Row(you, labelWidth, "QUICKMATCH.VOICE.talk_mode");
            Button voice = VoiceWidgets.Button(Loc.Get("QUICKMATCH.VOICE.voice_activated"), () => Change(() => settings.PushToTalkOnly = false));
            Button push = VoiceWidgets.Button(Loc.Get("QUICKMATCH.VOICE.push_to_talk"), () => Change(() => settings.PushToTalkOnly = true));
            mode.AddChild(voice);
            mode.AddChild(push);
            _refresh.Add(() =>
            {
                VoiceWidgets.SetOn(voice, !settings.PushToTalkOnly);
                VoiceWidgets.SetOn(push, settings.PushToTalkOnly);
            });
            HBoxContainer key = Row(you, labelWidth, "QUICKMATCH.VOICE.key");
            _keyButton = VoiceWidgets.Button("", StartKeyCapture);
            key.AddChild(_keyButton);
            _refresh.Add(() => _keyButton.Text = _waitingForKey ? Loc.Get("QUICKMATCH.VOICE.press_a_key") : VoiceText.KeyName(settings.PushToTalkKey));
        }

        HBoxContainer sensitivity = Row(you, labelWidth, "QUICKMATCH.VOICE.sensitivity");
        HSlider sensitivitySlider = VoiceWidgets.Slider(0, 1, 0.05, width * 0.5f, value => Change(() => settings.Sensitivity = value));
        sensitivitySlider.SetValueNoSignal(settings.Sensitivity);
        sensitivity.AddChild(sensitivitySlider);
        you.AddChild(Wrapped(Loc.Get("QUICKMATCH.VOICE.sensitivity_hint"), 20, VoiceGlyphs.Dim));

        HBoxContainer volume = Row(you, labelWidth, "QUICKMATCH.VOICE.volume");
        HSlider volumeSlider = VoiceWidgets.Slider(0, 1, 0.05, width * 0.5f, value => Change(() => settings.Volume = (float)value));
        volumeSlider.SetValueNoSignal(settings.Volume);
        volume.AddChild(volumeSlider);

        AddSwitch(you, labelWidth, "QUICKMATCH.VOICE.strangers", () => VoiceStore.Mutes.StrangersStartMuted, on => VoiceStore.Mutes.StrangersStartMuted = on);
    }

    private void AddSwitch(VBoxContainer column, float labelWidth, string key, Func<bool> get, Action<bool> set)
    {
        HBoxContainer row = Row(column, labelWidth, key);
        Button button = VoiceWidgets.Button("", () => Change(() => set(!get())));
        row.AddChild(button);
        _refresh.Add(() =>
        {
            bool on = get();
            button.Text = VoiceText.OnOff(on);
            VoiceWidgets.SetOn(button, on);
        });
    }

    private static void Change(Action change)
    {
        change();
        VoiceStore.Save();
    }

    /// <summary>The mic level while recording; gold while the voice gate would send it.</summary>
    private static Control Meter()
    {
        var meter = new Control
        {
            CustomMinimumSize = new Vector2(VoiceWidgets.Touch ? 220 : 160, VoiceWidgets.RowHeight * 0.3f),
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        meter.Draw += () =>
        {
            VoiceSession? session = VoiceChat.Session;
            float level = (float)Math.Clamp(session?.MicLevel ?? 0, 0, 1);
            bool heard = session?.LocalState == LocalVoiceState.Speaking;
            meter.DrawRect(new Rect2(Vector2.Zero, meter.Size), VoiceGlyphs.Ink with { A = 0.12f });
            meter.DrawRect(new Rect2(Vector2.Zero, new Vector2(meter.Size.X * level, meter.Size.Y)), heard ? VoiceGlyphs.Live : VoiceGlyphs.Ink with { A = 0.6f });
        };
        return meter;
    }

    private void RebuildPlayers()
    {
        VoiceSession? session = VoiceChat.Session;
        List<ulong> members = VoiceChat.Members.OrderBy(VoiceChat.NameOf).ToList();
        List<ulong> remembered = VoiceStore.Mutes.Muted.Where(id => !members.Contains(id) && id != VoiceChat.LocalId).ToList();
        string shown = string.Join(",", members) + "|" + string.Join(",", remembered);
        if (shown == _playersShown)
        {
            return;
        }
        _playersShown = shown;
        foreach (Node child in _players.GetChildren())
        {
            child.QueueFree();
        }
        _refresh.RemoveAll(r => r.Target is PlayerRow);
        if (members.Count == 0)
        {
            _players.AddChild(VoiceWidgets.Text(Loc.Get("QUICKMATCH.VOICE.no_players"), 22, VoiceGlyphs.Dim));
        }
        foreach (ulong player in members)
        {
            var row = new PlayerRow(player, inMatch: true);
            _players.AddChild(row.Root);
            _refresh.Add(row.Refresh);
        }
        if (session != null && members.Count > 1)
        {
            var everyone = new HBoxContainer();
            everyone.AddChild(VoiceWidgets.Button(Loc.Get("QUICKMATCH.VOICE.mute_everyone"), VoiceChat.MuteEveryone));
            _players.AddChild(everyone);
        }
        if (remembered.Count > 0)
        {
            _players.AddChild(VoiceWidgets.Rule());
            _players.AddChild(VoiceWidgets.Text(Loc.Get("QUICKMATCH.VOICE.muted_list"), 24, VoiceGlyphs.Live, bold: true));
            foreach (ulong player in remembered)
            {
                var row = new PlayerRow(player, inMatch: false);
                _players.AddChild(row.Root);
                _refresh.Add(row.Refresh);
            }
        }
        _players.AddChild(Wrapped(Loc.Get("QUICKMATCH.VOICE.private_note"), 20, VoiceGlyphs.Dim));
    }

    /// <summary>One other player: name, what their voice is doing, Mute, and (in the match) their volume.</summary>
    private sealed class PlayerRow
    {
        private readonly ulong _player;
        private readonly bool _inMatch;
        private readonly Label _status;
        private readonly Button _mute;
        private readonly HSlider? _volume;

        public PlayerRow(ulong player, bool inMatch)
        {
            _player = player;
            _inMatch = inMatch;
            Root = new HBoxContainer { CustomMinimumSize = new Vector2(0, VoiceWidgets.RowHeight) };
            Root.AddThemeConstantOverride("separation", VoiceWidgets.Touch ? 24 : 16);
            var names = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, Alignment = BoxContainer.AlignmentMode.Center };
            names.AddThemeConstantOverride("separation", 0);
            Label name = VoiceWidgets.Text(VoiceChat.NameOf(player), 24, VoiceGlyphs.Ink, bold: true);
            name.ClipText = true;
            names.AddChild(name);
            _status = VoiceWidgets.Text("", 20, VoiceGlyphs.Dim);
            names.AddChild(_status);
            Root.AddChild(names);
            _mute = VoiceWidgets.Button("", () => VoiceChat.SetMuted(_player, !IsMuted()));
            Root.AddChild(_mute);
            if (inMatch)
            {
                _volume = VoiceWidgets.Slider(VoiceMutes.MinVolume, VoiceMutes.MaxVolume, 0.05, VoiceWidgets.Touch ? 300 : 200, value =>
                {
                    if (VoiceChat.Session is { } session)
                    {
                        session.SetVolume(_player, (float)value);
                    }
                });
                _volume.SetValueNoSignal(VoiceStore.Mutes.VolumeOf(player));
                Root.AddChild(_volume);
            }
            else
            {
                _status.Visible = false;
            }
        }

        public HBoxContainer Root { get; }

        public void Refresh()
        {
            bool muted = IsMuted();
            _mute.Text = Loc.Get(muted ? "QUICKMATCH.VOICE.unmute" : "QUICKMATCH.VOICE.mute");
            VoiceWidgets.SetOn(_mute, muted);
            if (_inMatch && VoiceChat.Session is { } session)
            {
                _status.Text = VoiceText.StatusOf(session, _player);
                _status.AddThemeColorOverride("font_color", session.IsSpeaking(_player) ? VoiceGlyphs.Live : VoiceGlyphs.Dim);
            }
        }

        private bool IsMuted() => VoiceStore.Mutes.IsMuted(_player, VoiceChat.IsFriend(_player));
    }

    private void OnProcessFrame()
    {
        if (!VoiceChat.InMatch || !GodotObject.IsInstanceValid(_layer))
        {
            Close();
            return;
        }
        RebuildPlayers();
        foreach (Action refresh in _refresh.ToList())
        {
            refresh();
        }
    }

    private void OnBackstopInput(InputEvent input)
    {
        if (input is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true } button && !_panel.GetGlobalRect().HasPoint(button.GlobalPosition))
        {
            Close();
        }
    }

    private void StartKeyCapture()
    {
        if (_tree == null || _waitingForKey)
        {
            return;
        }
        _waitingForKey = true;
        _tree.Root.WindowInput += OnKeyCapture;
    }

    private void StopKeyCapture()
    {
        if (_waitingForKey && _tree != null)
        {
            _tree.Root.WindowInput -= OnKeyCapture;
        }
        _waitingForKey = false;
    }

    private void OnKeyCapture(InputEvent input)
    {
        if (input is not InputEventKey { Pressed: true, Echo: false } key)
        {
            return;
        }
        Key physical = key.PhysicalKeycode != Key.None ? key.PhysicalKeycode : key.Keycode;
        // Esc leaves the key as it was (and does not close the panel, see Close).
        if (physical != Key.Escape)
        {
            Change(() => VoiceStore.Settings.PushToTalkKey = (long)physical);
            StopKeyCapture();
        }
    }

    private void Close()
    {
        if (_waitingForKey)
        {
            StopKeyCapture();
            return;
        }
        if (_open != this)
        {
            return;
        }
        _open = null;
        NHotkeyManager? hotkeys = NHotkeyManager.Instance;
        if (hotkeys != null)
        {
            hotkeys.RemoveHotkeyReleasedBinding(MegaInput.cancel, _closeAction);
            hotkeys.RemoveHotkeyReleasedBinding(MegaInput.pauseAndBack, _closeAction);
            hotkeys.RemoveBlockingScreen(_layer);
        }
        if (_tree != null)
        {
            _tree.ProcessFrame -= OnProcessFrame;
            _tree = null;
        }
        if (GodotObject.IsInstanceValid(_layer))
        {
            _layer.QueueFree();
        }
    }

    private static HBoxContainer Row(VBoxContainer column, float labelWidth, string key)
    {
        var row = new HBoxContainer { CustomMinimumSize = new Vector2(0, VoiceWidgets.RowHeight) };
        row.AddThemeConstantOverride("separation", VoiceWidgets.Touch ? 20 : 14);
        Label label = VoiceWidgets.Text(Loc.Get(key), 24, VoiceGlyphs.Ink);
        label.CustomMinimumSize = new Vector2(labelWidth, 0);
        row.AddChild(label);
        column.AddChild(row);
        return row;
    }

    private static VBoxContainer Column(HBoxContainer columns, float width, Control.SizeFlags horizontal)
    {
        var scroll = new ScrollContainer
        {
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            CustomMinimumSize = new Vector2(width, 0),
            SizeFlagsHorizontal = horizontal,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
        };
        columns.AddChild(scroll);
        var column = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        column.AddThemeConstantOverride("separation", VoiceWidgets.Touch ? 8 : 4);
        scroll.AddChild(column);
        return column;
    }

    private static Label Wrapped(string text, int size, Color color)
    {
        Label label = VoiceWidgets.Text(text, size, color);
        label.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        label.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        return label;
    }
}
