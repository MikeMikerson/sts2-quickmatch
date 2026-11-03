using System;
using Godot;
using MegaCrit.Sts2.Core.Helpers;

namespace QuickMatch.Ui;

/// <summary>
/// The voice chat symbols, drawn with lines and shapes in the game's own colours (the mod ships no
/// art): a microphone, a speaker, headphones for "listening only", and a slash across any of them.
/// Every method draws into a square of side <paramref name="size"/> centred on <paramref name="c"/>.
/// </summary>
internal static class VoiceGlyphs
{
    public static readonly Color Ink = StsColors.cream;
    public static readonly Color Live = StsColors.gold;
    public static readonly Color Warning = StsColors.orange;
    public static readonly Color Stop = StsColors.red;
    public static readonly Color Dim = StsColors.gray;
    public static readonly Color Shadow = new(0f, 0f, 0f, 0.55f);

    public static void Mic(CanvasItem canvas, Vector2 c, float size, Color color)
    {
        float w = size * 0.26f;
        float h = size * 0.42f;
        float line = Math.Max(1.5f, size * 0.07f);
        var top = new Vector2(c.X, c.Y - size * 0.22f);
        var bottom = new Vector2(c.X, c.Y + h - size * 0.22f - w);
        // The capsule: two round ends and the body between them.
        canvas.DrawCircle(top, w / 2, color);
        canvas.DrawCircle(bottom, w / 2, color);
        canvas.DrawRect(new Rect2(c.X - w / 2, top.Y, w, bottom.Y - top.Y), color);
        // The holder around its lower half, the stem and the foot.
        canvas.DrawArc(bottom, w * 0.95f, 0, Mathf.Pi, 16, color, line, true);
        var stemTop = new Vector2(c.X, bottom.Y + w * 0.95f);
        var stemBottom = new Vector2(c.X, c.Y + size * 0.38f);
        canvas.DrawLine(stemTop, stemBottom, color, line, true);
        canvas.DrawLine(new Vector2(c.X - size * 0.16f, stemBottom.Y), new Vector2(c.X + size * 0.16f, stemBottom.Y), color, line, true);
    }

    public static void Speaker(CanvasItem canvas, Vector2 c, float size, Color color, int waves)
    {
        float s = size;
        var body = new[]
        {
            new Vector2(c.X - s * 0.38f, c.Y - s * 0.12f),
            new Vector2(c.X - s * 0.2f, c.Y - s * 0.12f),
            new Vector2(c.X + s * 0.02f, c.Y - s * 0.32f),
            new Vector2(c.X + s * 0.02f, c.Y + s * 0.32f),
            new Vector2(c.X - s * 0.2f, c.Y + s * 0.12f),
            new Vector2(c.X - s * 0.38f, c.Y + s * 0.12f),
        };
        canvas.DrawColoredPolygon(body, color);
        float line = Math.Max(1.5f, s * 0.07f);
        for (int i = 1; i <= waves; i++)
        {
            canvas.DrawArc(new Vector2(c.X + s * 0.02f, c.Y), s * (0.12f + 0.13f * i), -0.8f, 0.8f, 12, color, line, true);
        }
    }

    public static void Headphones(CanvasItem canvas, Vector2 c, float size, Color color)
    {
        float line = Math.Max(1.5f, size * 0.08f);
        canvas.DrawArc(new Vector2(c.X, c.Y + size * 0.05f), size * 0.3f, Mathf.Pi, Mathf.Tau, 20, color, line, true);
        canvas.DrawRect(new Rect2(c.X - size * 0.38f, c.Y + size * 0.02f, size * 0.16f, size * 0.28f), color);
        canvas.DrawRect(new Rect2(c.X + size * 0.22f, c.Y + size * 0.02f, size * 0.16f, size * 0.28f), color);
    }

    /// <summary>A slash from top right to bottom left, with a dark edge so it reads on any background.</summary>
    public static void Slash(CanvasItem canvas, Vector2 c, float size, Color color)
    {
        var from = new Vector2(c.X + size * 0.36f, c.Y - size * 0.36f);
        var to = new Vector2(c.X - size * 0.36f, c.Y + size * 0.36f);
        canvas.DrawLine(from, to, Shadow, Math.Max(3f, size * 0.16f), true);
        canvas.DrawLine(from, to, color, Math.Max(2f, size * 0.09f), true);
    }

    /// <summary>A small round plate behind a symbol, so it stays readable over a portrait.</summary>
    public static void Plate(CanvasItem canvas, Vector2 c, float size) => canvas.DrawCircle(c, size * 0.5f, Shadow);
}
