using Godot;
using QuickMatch.Localization;
using QuickMatch.Voice;

namespace QuickMatch.Ui;

/// <summary>
/// A Voice row in the panel the game opens when a teammate's row is tapped: what their voice is
/// doing, a large Mute toggle and their volume. Muting anyone is two taps from anywhere in a run.
/// Hidden outside a quick match and on the local player's own panel.
/// </summary>
internal sealed class VoiceStrip
{
    private const string NodeName = "QuickMatchVoiceStrip";

    private readonly ulong _player;
    private readonly HBoxContainer _row;
    private readonly Label _status;
    private readonly Button _mute;
    private readonly HSlider _volume;
    private SceneTree? _tree;
    private bool _muted;

    private VoiceStrip(ulong player)
    {
        _player = player;
        _row = new HBoxContainer { Name = NodeName, Visible = false, MouseFilter = Control.MouseFilterEnum.Ignore };
        _row.AddThemeConstantOverride("separation", VoiceWidgets.Touch ? 28 : 18);
        _row.AddChild(VoiceWidgets.Text(Loc.Get("QUICKMATCH.VOICE.title"), 26, VoiceGlyphs.Live, bold: true));
        _status = VoiceWidgets.Text("", 24, VoiceGlyphs.Ink);
        _status.CustomMinimumSize = new Vector2(VoiceWidgets.Touch ? 330 : 240, 0);
        _row.AddChild(_status);
        _mute = VoiceWidgets.Button("", ToggleMute);
        _row.AddChild(_mute);
        _row.AddChild(VoiceWidgets.Text(Loc.Get("QUICKMATCH.VOICE.volume"), 24, VoiceGlyphs.Ink));
        _volume = VoiceWidgets.Slider(VoiceMutes.MinVolume, VoiceMutes.MaxVolume, 0.05, VoiceWidgets.Touch ? 380 : 260, OnVolume);
        _row.AddChild(_volume);
        _row.TreeEntered += OnTreeEntered;
        _row.TreeExiting += OnTreeExiting;
        _muted = !VoiceStore.Mutes.IsMuted(player, VoiceChat.IsFriend(player));
    }

    /// <summary>Adds the row under the player's name in the game's expanded player panel.</summary>
    public static void Attach(Control panel, ulong player)
    {
        var container = panel.GetNodeOrNull<VBoxContainer>("ScreenContents/Container");
        if (container == null || container.GetNodeOrNull(NodeName) != null)
        {
            return;
        }
        var strip = new VoiceStrip(player);
        container.AddChild(strip._row);
        container.MoveChild(strip._row, 1);
    }

    private void OnTreeEntered()
    {
        _tree = _row.GetTree();
        _tree.ProcessFrame += OnProcessFrame;
        _volume.SetValueNoSignal(VoiceStore.Mutes.VolumeOf(_player));
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
        bool show = session != null && _player != VoiceChat.LocalId;
        if (_row.Visible != show)
        {
            _row.Visible = show;
        }
        if (!show)
        {
            return;
        }
        _status.Text = VoiceText.StatusOf(session!, _player);
        bool muted = session!.IsMuted(_player);
        if (muted != _muted)
        {
            _muted = muted;
            string name = VoiceChat.NameOf(_player);
            _mute.Text = Loc.With(muted ? "QUICKMATCH.VOICE.unmute_player" : "QUICKMATCH.VOICE.mute_player", "player", name);
            VoiceWidgets.SetOn(_mute, muted);
        }
    }

    private void ToggleMute() => VoiceChat.SetMuted(_player, !(VoiceChat.Session?.IsMuted(_player) ?? false));

    private void OnVolume(double value)
    {
        if (VoiceChat.Session is { } session)
        {
            session.SetVolume(_player, (float)value);
        }
        else
        {
            VoiceStore.Mutes.SetVolume(_player, (float)value);
        }
    }
}
