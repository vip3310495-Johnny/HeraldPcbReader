// Herald PCB Reader — Copyright (C) 2026 Johnny.ch.Tsai
// SPDX-License-Identifier: GPL-3.0-only
// Free software under the GNU General Public License version 3 (LICENSE.txt), WITHOUT ANY WARRANTY; with an
// additional permission to combine it with AvalonDock (Ms-PL), see NOTICE.txt.
using System.IO;
using System.Windows.Input;
using SkiaSharp;

namespace AdApp.Views;

/// <summary>The pointing hand of the "point finger" tool: drawn the same way as the mouse cursor and as the marks
/// left on the board. A neon magenta hand (a colour no copper layer uses) with a dark outline, a white glow, a cyan dot
/// on the fingertip and, on the board, a soft dark disc behind it, so it stands out on red, yellow, green and blue
/// copper alike, even with the whole board in view.</summary>
static class FingerArt
{
    /// <summary>Units of the drawing: the hand is 64 high, the fingertip at (0, 0), the hand below it (+y).</summary>
    const float Units = 64;
    static readonly SKPath Hand = Build();

    static SKPath Build()
    {
        static SKPath R(float l, float t, float r, float b, float rad) { var p = new SKPath(); p.AddRoundRect(new SKRect(l, t, r, b), rad, rad); return p; }
        var hand = R(-5.5f, 0, 5.5f, 36, 5.5f);                    // index finger
        foreach (var part in new[]
        {
            R(4.5f, 19, 15, 39, 5), R(13.5f, 22, 23, 41, 4.7f), R(21.5f, 26, 30, 43, 4.2f),   // curled fingers
            R(-9, 31, 30, 63, 10),                                                           // palm
        })
            hand = hand.Op(part, SKPathOp.Union) ?? hand;
        var thumb = R(-5, -12, 5, 12, 5);
        thumb.Transform(SKMatrix.CreateRotationDegrees(-48).PostConcat(SKMatrix.CreateTranslation(-12, 42)));
        return hand.Op(thumb, SKPathOp.Union) ?? hand;
    }

    /// <summary>Draws the hand with its fingertip at (x, y), pointing at angle (degrees, 0 = up, clockwise), size
    /// pixels high (in the canvas's current units).</summary>
    public static void Draw(SKCanvas c, float x, float y, float angle, float size, bool backdrop = false)
    {
        c.Save();
        c.Translate(x, y); c.RotateDegrees(angle); c.Scale(size / Units);
        if (backdrop)
        {
            // a soft dark disc behind a mark on the board: contrast on any copper colour, and a shape the eye finds at
            // a glance with the whole board in view
            using var shade = new SKPaint { IsAntialias = true, Shader = SKShader.CreateRadialGradient(new SKPoint(8, 32), 50, new[] { new SKColor(0, 0, 0, 190), new SKColor(0, 0, 0, 150), new SKColor(0, 0, 0, 0) }, new[] { 0f, 0.6f, 1f }, SKShaderTileMode.Clamp) };
            c.DrawCircle(8, 32, 50, shade);
        }
        using (var glow = new SKPaint { IsAntialias = true, Style = SKPaintStyle.StrokeAndFill, StrokeWidth = 9, Color = new SKColor(255, 255, 255, 215), MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 3.2f) })
            c.DrawPath(Hand, glow);
        using (var fill = new SKPaint { IsAntialias = true, Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(0, 63), new[] { new SKColor(0xFF, 0x9C, 0xEE), new SKColor(0xE8, 0x1C, 0xB4) }, SKShaderTileMode.Clamp) })
            c.DrawPath(Hand, fill);
        using (var line = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.6f, StrokeCap = SKStrokeCap.Round, Color = new SKColor(0x4A, 0x08, 0x38, 210) })
        {
            c.DrawLine(5, 21, 5, 33, line); c.DrawLine(14.2f, 25, 14.2f, 37, line); c.DrawLine(22.2f, 29, 22.2f, 40, line);
        }
        using (var outline = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2.6f, StrokeJoin = SKStrokeJoin.Round, Color = new SKColor(0x14, 0x14, 0x14) })
            c.DrawPath(Hand, outline);
        using (var dot = new SKPaint { IsAntialias = true, Color = new SKColor(0x00, 0xE5, 0xFF) })
        using (var ring = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.4f, Color = SKColors.White })
        {
            c.DrawCircle(0, 0, 3.6f, dot); c.DrawCircle(0, 0, 3.6f, ring);
        }
        c.Restore();
    }

    static readonly Dictionary<(int, int), Cursor> CursorCache = new();

    /// <summary>The hand as a mouse cursor (hot spot on the fingertip), sizePx device pixels high; cached.</summary>
    public static Cursor MakeCursor(double angle, double sizePx)
    {
        int a = (int)Math.Round(angle), s = (int)Math.Round(Math.Clamp(sizePx, 4, 400));
        if (CursorCache.TryGetValue((a, s), out var cur)) return cur;
        // the rotated hand with its glow, fitted into at most 256 × 256 (the largest cursor Windows takes)
        float k = s / Units, pad = 7 * k + 2;
        var m = SKMatrix.CreateRotationDegrees(a).PostConcat(SKMatrix.CreateScale(k, k));
        var b = m.MapRect(Hand.TightBounds);
        float w = b.Width + 2 * pad, h = b.Height + 2 * pad, fit = Math.Min(1, 256 / Math.Max(w, h));
        int W = Math.Max(8, (int)Math.Ceiling(w * fit)), H = Math.Max(8, (int)Math.Ceiling(h * fit));
        float hx = (pad - b.Left) * fit, hy = (pad - b.Top) * fit;
        using var surf = SKSurface.Create(new SKImageInfo(W, H, SKColorType.Bgra8888, SKAlphaType.Premul));
        var c = surf.Canvas; c.Clear(SKColors.Transparent);
        Draw(c, hx, hy, a, s * fit);
        using var img = surf.Snapshot();
        try { cur = new Cursor(CurFile(img, (int)Math.Round(hx), (int)Math.Round(hy))); }
        catch { cur = System.Windows.Input.Cursors.Hand; }
        CursorCache[(a, s)] = cur;
        return cur;
    }

    /// <summary>A .cur file: ICONDIR, one entry with the hot spot, a 32-bit DIB (bottom-up) and its AND mask.</summary>
    public static Stream CurFile(SKImage img, int hotX, int hotY)
    {
        int W = img.Width, H = img.Height;
        using var px = img.PeekPixels();
        var ms = new MemoryStream(); var bw = new BinaryWriter(ms);
        int xor = W * H * 4, andRow = (W + 31) / 32 * 4, and = andRow * H, size = 40 + xor + and;
        bw.Write((short)0); bw.Write((short)2); bw.Write((short)1);
        bw.Write((byte)(W >= 256 ? 0 : W)); bw.Write((byte)(H >= 256 ? 0 : H)); bw.Write((byte)0); bw.Write((byte)0);
        bw.Write((short)Math.Clamp(hotX, 0, W - 1)); bw.Write((short)Math.Clamp(hotY, 0, H - 1)); bw.Write(size); bw.Write(22);
        bw.Write(40); bw.Write(W); bw.Write(H * 2); bw.Write((short)1); bw.Write((short)32); bw.Write(0); bw.Write(xor + and); bw.Write(0); bw.Write(0); bw.Write(0); bw.Write(0);
        for (int y = H - 1; y >= 0; y--)
            for (int x = 0; x < W; x++)
            {
                var col = px.GetPixelColor(x, y);
                bw.Write(col.Blue); bw.Write(col.Green); bw.Write(col.Red); bw.Write(col.Alpha);
            }
        for (int i = 0; i < and; i++) bw.Write((byte)0);
        ms.Position = 0;
        return ms;
    }

    /// <summary>Tests: the cursor image as a PNG.</summary>
    public static void SavePng(string path, double angle, double sizePx)
    {
        int s = (int)Math.Round(sizePx); float k = s / Units, pad = 7 * k + 2;
        var b = SKMatrix.CreateRotationDegrees((float)angle).PostConcat(SKMatrix.CreateScale(k, k)).MapRect(Hand.TightBounds);
        int W = (int)Math.Ceiling(b.Width + 2 * pad), H = (int)Math.Ceiling(b.Height + 2 * pad);
        using var surf = SKSurface.Create(new SKImageInfo(W, H, SKColorType.Bgra8888, SKAlphaType.Premul));
        surf.Canvas.Clear(new SKColor(0x2E, 0x34, 0x38));
        Draw(surf.Canvas, pad - b.Left, pad - b.Top, (float)angle, s);
        using var img = surf.Snapshot(); using var data = img.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(path, data.ToArray());
    }
}
