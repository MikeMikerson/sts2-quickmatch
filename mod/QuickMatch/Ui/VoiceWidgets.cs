using System;
using Godot;
using MegaCrit.Sts2.Core.Localization.Fonts;

namespace QuickMatch.Ui;

/// <summary>
/// Labels, buttons and sliders for the voice controls in the game's fonts and colours: Kreon (or the
/// language's substitute, as the game's own labels get it), cream text, gold for what is on, on a
/// dark plate. Sizes grow on a touchscreen so every control is a comfortable thumb target.
/// </summary>
internal static class VoiceWidgets
{
    private const string RegularFontPath = "res://themes/kreon_regular_shared.tres";
    private const string BoldFontPath = "res://themes/kreon_bold_shared.tres";

    private static Font? _regular;
    private static Font? _bold;
    private static Texture2D? _grabber;

    public static bool Touch => DisplayServer.IsTouchscreenAvailable();

    /// <summary>A font size in the game's 1080-high view, larger on a touchscreen.</summary>
    public static int FontSize(int desktop) => Touch ? (int)(desktop * 1.35f) : desktop;

    /// <summary>The height of a row or button: about 40 points on a phone.</summary>
    public static float RowHeight => Touch ? 112f : 60f;

    public static Color Plate => new(0.07f, 0.06f, 0.05f, 0.97f);

    public static Label Text(string text, int size, Color color, bool bold = false)
    {
        var label = new Label
        {
            Text = text,
            VerticalAlignment = VerticalAlignment.Center,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        SetFont(label, bold);
        label.AddThemeFontSizeOverride("font_size", FontSize(size));
        label.AddThemeColorOverride("font_color", color);
        return label;
    }

    public static Button Button(string text, Action pressed)
    {
        var button = new Button
        {
            Text = text,
            CustomMinimumSize = new Vector2(Touch ? 170 : 110, RowHeight * 0.8f),
            FocusMode = Control.FocusModeEnum.All,
            ClipText = false,
        };
        SetFont(button, bold: true);
        button.AddThemeFontSizeOverride("font_size", FontSize(24));
        SetOn(button, false);
        button.AddThemeStyleboxOverride("focus", Box(new Color(0, 0, 0, 0), VoiceGlyphs.Live, 2));
        button.Pressed += pressed;
        return button;
    }

    /// <summary>Shows a button as chosen (filled gold, dark text) or not (outlined).</summary>
    public static void SetOn(Button button, bool on)
    {
        Color edge = on ? VoiceGlyphs.Live : VoiceGlyphs.Ink with { A = 0.6f };
        Color fill = on ? VoiceGlyphs.Live : new Color(1, 1, 1, 0.04f);
        Color text = on ? new Color(0.1f, 0.08f, 0.05f) : VoiceGlyphs.Ink;
        button.AddThemeStyleboxOverride("normal", Box(fill, edge, 2));
        button.AddThemeStyleboxOverride("hover", Box(on ? fill : new Color(1, 1, 1, 0.1f), VoiceGlyphs.Live, 2));
        button.AddThemeStyleboxOverride("pressed", Box(VoiceGlyphs.Live with { A = 0.8f }, VoiceGlyphs.Live, 2));
        button.AddThemeStyleboxOverride("disabled", Box(new Color(1, 1, 1, 0.02f), VoiceGlyphs.Dim with { A = 0.4f }, 2));
        foreach (string name in new[] { "font_color", "font_hover_color", "font_focus_color" })
        {
            button.AddThemeColorOverride(name, text);
        }
        button.AddThemeColorOverride("font_pressed_color", new Color(0.1f, 0.08f, 0.05f));
        button.AddThemeColorOverride("font_disabled_color", VoiceGlyphs.Dim);
    }

    public static HSlider Slider(double min, double max, double step, float width, Action<double> changed)
    {
        var slider = new HSlider
        {
            MinValue = min,
            MaxValue = max,
            Step = step,
            CustomMinimumSize = new Vector2(width, RowHeight * 0.7f),
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
            FocusMode = Control.FocusModeEnum.All,
        };
        float track = Touch ? 12 : 8;
        slider.AddThemeStyleboxOverride("slider", Bar(VoiceGlyphs.Ink with { A = 0.25f }, track));
        slider.AddThemeStyleboxOverride("grabber_area", Bar(VoiceGlyphs.Live with { A = 0.85f }, track));
        slider.AddThemeStyleboxOverride("grabber_area_highlight", Bar(VoiceGlyphs.Live, track));
        Texture2D grabber = Grabber();
        slider.AddThemeIconOverride("grabber", grabber);
        slider.AddThemeIconOverride("grabber_highlight", grabber);
        slider.ValueChanged += value => changed(value);
        return slider;
    }

    public static StyleBoxFlat Box(Color fill, Color edge, int width)
    {
        var box = new StyleBoxFlat { BgColor = fill, BorderColor = edge };
        box.SetBorderWidthAll(width);
        box.SetCornerRadiusAll(3);
        box.SetContentMarginAll(Touch ? 14 : 8);
        return box;
    }

    public static HSeparator Rule()
    {
        var rule = new HSeparator();
        var line = new StyleBoxLine { Color = VoiceGlyphs.Live with { A = 0.35f }, Thickness = 2 };
        rule.AddThemeStyleboxOverride("separator", line);
        rule.AddThemeConstantOverride("separation", Touch ? 24 : 16);
        return rule;
    }

    private static void SetFont(Control control, bool bold)
    {
        Font? font = bold ? _bold ??= ResourceLoader.Load<Font>(BoldFontPath) : _regular ??= ResourceLoader.Load<Font>(RegularFontPath);
        if (font != null)
        {
            control.AddThemeFontOverride("font", font);
        }
        control.ApplyLocaleFontSubstitution(bold ? FontType.Bold : FontType.Regular, "font");
    }

    private static StyleBoxFlat Bar(Color color, float height)
    {
        var bar = new StyleBoxFlat { BgColor = color };
        bar.SetCornerRadiusAll((int)(height / 2));
        bar.ContentMarginTop = height / 2;
        bar.ContentMarginBottom = height / 2;
        return bar;
    }

    /// <summary>A cream disc with a dark edge, drawn once: the mod ships no images.</summary>
    private static Texture2D Grabber()
    {
        if (_grabber != null)
        {
            return _grabber;
        }
        int size = Touch ? 44 : 26;
        Image image = Image.CreateEmpty(size, size, false, Image.Format.Rgba8);
        float r = size / 2f;
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float d = new Vector2(x + 0.5f - r, y + 0.5f - r).Length();
                float alpha = Math.Clamp(r - d, 0f, 1f);
                Color color = d > r - 3 ? new Color(0.1f, 0.08f, 0.05f) : VoiceGlyphs.Ink;
                image.SetPixel(x, y, color with { A = alpha });
            }
        }
        _grabber = ImageTexture.CreateFromImage(image);
        return _grabber;
    }
}
