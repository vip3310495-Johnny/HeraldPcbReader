// Herald PCB Reader — Copyright (C) 2026 Johnny.ch.Tsai
// SPDX-License-Identifier: GPL-3.0-only
// Free software under the GNU General Public License version 3 (LICENSE.txt), WITHOUT ANY WARRANTY; with an
// additional permission to combine it with AvalonDock (Ms-PL), see NOTICE.txt.
using SkiaSharp;

namespace AdCli;

/// <summary>Generates the icons (multi-size .ico with PNG entries). Herald PCB Reader: the herald who brings the light —
/// the morning star over a dawn horizon, the sky turning from night blue to gold, gold traces on the ground below.</summary>
static class Icon
{
    static readonly SKColor Night = new(0x0A, 0x12, 0x2E), Ground = new(0x0B, 0x11, 0x26), StarCore = new(0xFF, 0xFD, 0xF4), StarGold = new(0xFF, 0xD5, 0x6E);
    const float Cx = 128, Cy = 100, Horizon = 192;   // the star, and the top of the horizon arc

    /// <summary>The tile in a 256-unit box: a rounded square.</summary>
    static SKPath TilePath() { var p = new SKPath(); p.AddRoundRect(new SKRect(16, 16, 240, 240), 54, 54); return p; }

    /// <summary>A four-pointed star with concave sides: arms up / down / sideways, waist k (bigger = fuller).</summary>
    static SKPath Sparkle(float cx, float cy, float up, float down, float side, float k)
    {
        var p = new SKPath();
        p.MoveTo(cx, cy - up);
        p.QuadTo(cx + k, cy - k, cx + side, cy); p.QuadTo(cx + k, cy + k, cx, cy + down);
        p.QuadTo(cx - k, cy + k, cx - side, cy); p.QuadTo(cx - k, cy - k, cx, cy - up);
        p.Close();
        return p;
    }

    /// <summary>The Herald mark into the 256-unit box set on the canvas. detail: the faint stars, the diagonal glints and
    /// the traces on the ground; off for small sizes, where the star is drawn bigger and fuller instead.</summary>
    static void MorningStar(SKCanvas c, bool detail, bool shadow = true)
    {
        using var tile = TilePath();
        if (shadow)
            using (var sh = new SKPaint { IsAntialias = true, Color = new SKColor(0, 0, 0, 110), MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 6) })
            { c.Save(); c.Translate(0, 5); c.DrawPath(tile, sh); c.Restore(); }
        c.Save(); c.ClipPath(tile, antialias: true);
        using var p = new SKPaint { IsAntialias = true };
        // the sky: night at the top, violet, then rose and gold down to the horizon
        p.Shader = SKShader.CreateLinearGradient(new SKPoint(0, 16), new SKPoint(0, Horizon + 6),
            new[] { Night, new SKColor(0x1C, 0x2A, 0x66), new SKColor(0x5B, 0x3C, 0x8C), new SKColor(0xD9, 0x6A, 0x63), new SKColor(0xFF, 0xB8, 0x5C) },
            new[] { 0f, 0.38f, 0.64f, 0.86f, 1f }, SKShaderTileMode.Clamp);
        c.DrawRect(0, 0, 256, 256, p);
        // the sun about to rise: a warm glow centred under the horizon
        p.Shader = SKShader.CreateRadialGradient(new SKPoint(Cx, Horizon + 14), 130, new[] { new SKColor(0xFF, 0xE7, 0xA8, 235), new SKColor(0xFF, 0xB0, 0x5A, 120), new SKColor(0xFF, 0x9A, 0x50, 0) }, new[] { 0f, 0.35f, 1f }, SKShaderTileMode.Clamp);
        c.DrawRect(0, 0, 256, 256, p);
        if (detail)
        {
            p.Shader = null; p.Color = new SKColor(0xFF, 0xFF, 0xFF, 190);
            foreach (var (x, y, r) in new[] { (54f, 52f, 2.6f), (198f, 46f, 2.2f), (214f, 112f, 1.8f), (42f, 120f, 1.7f), (172f, 28f, 1.5f), (84f, 30f, 1.4f) }) c.DrawCircle(x, y, r, p);
        }
        // the ground: the curve of the earth, with a gold rim of light along the horizon
        using var earth = new SKPath(); earth.AddCircle(Cx, Horizon + 470, 470);
        p.Color = SKColors.White; p.Shader = SKShader.CreateLinearGradient(new SKPoint(0, Horizon), new SKPoint(0, 240), new[] { new SKColor(0x1A, 0x1F, 0x3E), Ground }, null, SKShaderTileMode.Clamp);
        c.DrawPath(earth, p);
        using (var rim = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = detail ? 3.5f : 6, Shader = SKShader.CreateLinearGradient(new SKPoint(30, 0), new SKPoint(226, 0), new[] { new SKColor(0xFF, 0xC8, 0x6E, 0), new SKColor(0xFF, 0xF1, 0xC4), new SKColor(0xFF, 0xC8, 0x6E, 0) }, null, SKShaderTileMode.Clamp) })
            c.DrawPath(earth, rim);
        if (detail)
        {
            // traces on the ground running up to the light, pads at their ends
            using var tr = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 4.5f, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round, Color = new SKColor(0xE9, 0xB9, 0x5E, 200) };
            using var pad = new SKPaint { IsAntialias = true, Color = new SKColor(0xF4, 0xC9, 0x70, 230) };
            using var hole = new SKPaint { IsAntialias = true, Color = Ground };
            using var t = new SKPath();
            t.MoveTo(Cx, 244); t.LineTo(Cx, 206);
            t.MoveTo(84, 244); t.LineTo(84, 230); t.LineTo(100, 214);
            t.MoveTo(172, 244); t.LineTo(172, 230); t.LineTo(156, 214);
            c.DrawPath(t, tr);
            foreach (var q in new[] { new SKPoint(Cx, 206), new SKPoint(100, 214), new SKPoint(156, 214) }) { c.DrawCircle(q, 6.5f, pad); c.DrawCircle(q, 2.6f, hole); }
        }
        // the morning star: a soft halo, then the star, longer below — the light it brings down to the horizon
        p.Color = SKColors.White; p.Shader = SKShader.CreateRadialGradient(new SKPoint(Cx, Cy), detail ? 92 : 104, new[] { new SKColor(0xFF, 0xF4, 0xD2, 170), new SKColor(0xFF, 0xE0, 0x96, 70), new SKColor(0xFF, 0xD0, 0x80, 0) }, new[] { 0f, 0.4f, 1f }, SKShaderTileMode.Clamp);
        c.DrawCircle(Cx, Cy, detail ? 92 : 104, p);
        if (detail)
        {
            using var glint = Sparkle(Cx, Cy, 34, 34, 34, 5);
            glint.Transform(SKMatrix.CreateRotationDegrees(45, Cx, Cy));
            p.Shader = null; p.Color = new SKColor(0xFF, 0xF4, 0xD6, 150); c.DrawPath(glint, p);
        }
        using var star = detail ? Sparkle(Cx, Cy, 66, 84, 50, 9) : Sparkle(Cx, Cy, 72, 88, 62, 16);
        p.Color = SKColors.White; p.Shader = SKShader.CreateRadialGradient(new SKPoint(Cx, Cy), 80, new[] { StarCore, new SKColor(0xFF, 0xF0, 0xBE), StarGold }, new[] { 0f, 0.25f, 1f }, SKShaderTileMode.Clamp);
        c.DrawPath(star, p);
        p.Shader = null; p.Color = StarCore; c.DrawCircle(Cx, Cy, detail ? 9 : 13, p);
        c.Restore();
        if (detail)
            using (var edge = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2, Color = new SKColor(0xFF, 0xFF, 0xFF, 40) })
                c.DrawPath(tile, edge);
    }

    static byte[] Draw(int n)
    {
        using var surf = SKSurface.Create(new SKImageInfo(n, n, SKColorType.Bgra8888, SKAlphaType.Premul));
        var c = surf.Canvas; c.Clear(SKColors.Transparent); c.Scale(n / 256f);
        MorningStar(c, n > 32);
        using var img = surf.Snapshot(); using var data = img.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    /// <summary>Explorer file-type icon: a white page with a folded corner, a glyph for the kind and a coloured
    /// band (SCH yellow, PCB green, PRJ blue — the same colours as the document tabs) so the kinds tell apart at 16 px;
    /// from 40 px the band also carries the Herald mark and the kind's name.</summary>
    static byte[] DrawDoc(int n, string kind)
    {
        using var surf = SKSurface.Create(new SKImageInfo(n, n, SKColorType.Bgra8888, SKAlphaType.Premul));
        var c = surf.Canvas; c.Clear(SKColors.Transparent); c.Scale(n / 256f);
        bool small = n <= 32, label = n >= 40;
        var (band, bandDark, ink, text) = kind switch
        {
            "sch" => (new SKColor(0xF2, 0xC9, 0x4C), new SKColor(0xC9, 0x9A, 0x1E), new SKColor(0x2A, 0x22, 0x08), "SCH"),
            "pcb" => (new SKColor(0x4C, 0xB8, 0x5A), new SKColor(0x2F, 0x8F, 0x4E), SKColors.White, "PCB"),
            _ => (new SKColor(0x4D, 0x8E, 0xE0), new SKColor(0x2F, 0x63, 0xB0), SKColors.White, "PRJ"),
        };
        float L = small ? 30 : 38, R = small ? 226 : 218, T = 8, B = 248, fold = small ? 64 : 58;
        using var page = new SKPath();
        page.MoveTo(L, T); page.LineTo(R - fold, T); page.LineTo(R, T + fold); page.LineTo(R, B); page.LineTo(L, B); page.Close();

        using (var sh = new SKPaint { IsAntialias = true, Color = new SKColor(0, 0, 0, 70), MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 5) })
        { c.Save(); c.Translate(0, 4); c.DrawPath(page, sh); c.Restore(); }
        using (var p = new SKPaint { IsAntialias = true })
        {
            p.Shader = SKShader.CreateLinearGradient(new SKPoint(0, T), new SKPoint(0, B), new[] { SKColors.White, new SKColor(0xE9, 0xEC, 0xF0) }, null, SKShaderTileMode.Clamp);
            c.DrawPath(page, p);
        }
        // coloured band across the lower part of the page
        float bandTop = small ? 150 : 168;
        c.Save(); c.ClipPath(page, antialias: true);
        using (var p = new SKPaint { IsAntialias = true })
        {
            p.Shader = SKShader.CreateLinearGradient(new SKPoint(0, bandTop), new SKPoint(0, B), new[] { band, bandDark }, null, SKShaderTileMode.Clamp);
            c.DrawRect(L, bandTop, R - L, B - bandTop, p);
        }
        c.Restore();
        using (var edge = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = small ? 10 : 5, Color = new SKColor(0x6E, 0x76, 0x80), StrokeJoin = SKStrokeJoin.Round })
            c.DrawPath(page, edge);
        // glyph in the white area
        float gy0 = small ? 34 : 42, gy1 = bandTop - (small ? 14 : 18), gx0 = L + (small ? 16 : 22), gx1 = R - (small ? 16 : 26);
        float sw = small ? 16 : 9;
        using var stroke = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = sw, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round };
        using var fill = new SKPaint { IsAntialias = true };
        float cy = (gy0 + gy1) / 2;
        if (kind == "sch")
        {
            // op-amp: triangle with two inputs and an output, Altium colours (dark red body, blue wires)
            float tx0 = gx0 + (gx1 - gx0) * 0.30f, tx1 = gx0 + (gx1 - gx0) * 0.74f, h = (gy1 - gy0) * 0.5f;
            using var tri = new SKPath(); tri.MoveTo(tx0, cy - h); tri.LineTo(tx1, cy); tri.LineTo(tx0, cy + h); tri.Close();
            stroke.Color = new SKColor(0x1F, 0x3F, 0xA8);
            float iy = h * 0.5f;
            c.DrawLine(gx0, cy - iy, tx0, cy - iy, stroke); c.DrawLine(gx0, cy + iy, tx0, cy + iy, stroke); c.DrawLine(tx1, cy, gx1, cy, stroke);
            fill.Color = new SKColor(0xFF, 0xF2, 0xA8); c.DrawPath(tri, fill);
            stroke.Color = new SKColor(0x8A, 0x1F, 0x11); c.DrawPath(tri, stroke);
            if (!small)
            {
                using var sign = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 5, Color = new SKColor(0x8A, 0x1F, 0x11) };
                float sx = tx0 + 16, s = 7;
                c.DrawLine(sx - s, cy - iy, sx + s, cy - iy, sign); c.DrawLine(sx, cy - iy - s, sx, cy - iy + s, sign);
                c.DrawLine(sx - s, cy + iy, sx + s, cy + iy, sign);
                fill.Color = new SKColor(0x1F, 0x3F, 0xA8); c.DrawCircle(gx0, cy - iy, 7, fill); c.DrawCircle(gx0, cy + iy, 7, fill);
            }
        }
        else if (kind == "pcb")
        {
            // solder-mask green board with a copper trace from a via to a pad
            var board = new SKRect(gx0, gy0, gx1, gy1);
            fill.Shader = SKShader.CreateLinearGradient(new SKPoint(0, gy0), new SKPoint(0, gy1), new[] { new SKColor(0x2E, 0x8B, 0x4A), new SKColor(0x1A, 0x5E, 0x30) }, null, SKShaderTileMode.Clamp);
            c.DrawRoundRect(board, 8, 8, fill); fill.Shader = null;
            var cu = new SKColor(0xF0, 0xA8, 0x5C);
            stroke.Color = cu; stroke.StrokeWidth = small ? 16 : 11;
            float w = gx1 - gx0, hh = gy1 - gy0;
            var via = new SKPoint(gx0 + w * 0.2f, gy0 + hh * 0.72f);
            using var tr = new SKPath(); tr.MoveTo(via); tr.LineTo(gx0 + w * 0.48f, via.Y); tr.LineTo(gx0 + w * 0.70f, gy0 + hh * 0.36f);
            c.DrawPath(tr, stroke);
            fill.Color = cu;
            c.DrawCircle(via, small ? 20 : 15, fill);
            var pad = new SKRect(gx0 + w * 0.62f, gy0 + hh * 0.14f, gx0 + w * 0.86f, gy0 + hh * 0.50f);
            c.DrawRoundRect(pad, 4, 4, fill);
            if (!small) { fill.Color = new SKColor(0x1A, 0x5E, 0x30); c.DrawCircle(via, 6, fill); }
        }
        else
        {
            // project: a tree with a schematic (yellow) and a PCB (green) leaf
            stroke.Color = new SKColor(0x55, 0x5E, 0x6A); stroke.StrokeWidth = small ? 12 : 7;
            float x0 = gx0 + 14, s = small ? 30 : 26, xl = gx0 + (gx1 - gx0) * 0.45f;
            float y0 = gy0 + s / 2, y1 = cy + 4, y2 = gy1 - s / 2;
            c.DrawLine(x0, y0, x0, y2, stroke); c.DrawLine(x0, y1, xl, y1, stroke); c.DrawLine(x0, y2, xl, y2, stroke);
            fill.Color = new SKColor(0x4D, 0x8E, 0xE0); c.DrawRoundRect(new SKRect(x0 - s / 2, y0 - s / 2, x0 + s / 2, y0 + s / 2), 4, 4, fill);
            fill.Color = new SKColor(0xE0, 0xB0, 0x2A); c.DrawRoundRect(new SKRect(xl, y1 - s / 2, xl + s * 1.9f, y1 + s / 2), 4, 4, fill);
            fill.Color = new SKColor(0x2F, 0x9E, 0x55); c.DrawRoundRect(new SKRect(xl, y2 - s / 2, xl + s * 1.9f, y2 + s / 2), 4, 4, fill);
        }
        // folded corner on top of the glyph
        using (var f = new SKPath())
        {
            f.MoveTo(R - fold, T); f.LineTo(R - fold, T + fold); f.LineTo(R, T + fold); f.Close();
            using var fp = new SKPaint { IsAntialias = true, Color = new SKColor(0xC5, 0xCB, 0xD3) }; c.DrawPath(f, fp);
            using var fe = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = small ? 10 : 5, Color = new SKColor(0x6E, 0x76, 0x80), StrokeJoin = SKStrokeJoin.Round }; c.DrawPath(f, fe);
        }

        if (label)
        {
            // Herald badge over the lower-left corner of the page (as Office does with its app letter), label beside it
            const float bs = 0.42f, bx = -2, by = 144;
            c.Save(); c.Translate(bx, by); c.Scale(bs); MorningStar(c, n >= 128, shadow: n >= 64); c.Restore();
            float lx0 = bx + 240 * bs;
            using var tf = SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Bold);
            using var tp = new SKPaint { IsAntialias = true, Typeface = tf, TextSize = 54, Color = ink, TextAlign = SKTextAlign.Center };
            var bounds = new SKRect(); tp.MeasureText(text, ref bounds);
            c.DrawText(text, (lx0 + R) / 2 + 2, (bandTop + B) / 2 - bounds.MidY, tp);
        }
        using var img = surf.Snapshot(); using var data = img.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    /// <summary>SchDoc.ico, PcbDoc.ico, PrjPcb.ico into dir (installed next to the program, used as the file-type icons).</summary>
    public static void WriteDocs(string dir)
    {
        Directory.CreateDirectory(dir);
        foreach (var (kind, name) in new[] { ("sch", "SchDoc"), ("pcb", "PcbDoc"), ("prj", "PrjPcb") })
            Ico(Path.Combine(dir, name + ".ico"), n => DrawDoc(n, kind));
    }

    public static void Write(string path) => Ico(path, Draw);

    static void Ico(string path, Func<int, byte[]> draw)
    {
        int[] sizes = { 16, 20, 24, 32, 40, 48, 64, 128, 256 };
        var pngs = sizes.Select(draw).ToList();
        using var f = File.Create(path); using var w = new BinaryWriter(f);
        w.Write((short)0); w.Write((short)1); w.Write((short)sizes.Length);
        int off = 6 + 16 * sizes.Length;
        for (int i = 0; i < sizes.Length; i++)
        {
            w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i])); w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i])); w.Write((byte)0); w.Write((byte)0);
            w.Write((short)1); w.Write((short)32); w.Write(pngs[i].Length); w.Write(off); off += pngs[i].Length;
        }
        foreach (var png in pngs) w.Write(png);
        File.WriteAllBytes(Path.ChangeExtension(path, ".png"), pngs[^1]);
        File.WriteAllBytes(Path.ChangeExtension(path, null) + "-32.png", pngs[3]);
        File.WriteAllBytes(Path.ChangeExtension(path, null) + "-16.png", pngs[0]);
        Console.WriteLine("wrote " + path);
    }
}
