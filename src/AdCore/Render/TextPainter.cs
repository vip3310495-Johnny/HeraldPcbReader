// Herald PCB Reader — Copyright (C) 2026 Johnny.ch.Tsai
// SPDX-License-Identifier: GPL-3.0-only
// Free software under the GNU General Public License version 3 (LICENSE.txt), WITHOUT ANY WARRANTY; with an
// additional permission to combine it with AvalonDock (Ms-PL), see NOTICE.txt.
using System.Collections.Concurrent;
using SkiaSharp;

namespace AdCore.Render;

/// <summary>Typeface cache + CJK fallback + overbar markup (~{...}) for all text drawing.</summary>
public static class TextPainter
{
    static readonly ConcurrentDictionary<string, SKTypeface> Faces = new();
    static SKTypeface _cjk;

    public static SKTypeface Face(string family, bool bold = false, bool italic = false, SKFontStyleWidth width = SKFontStyleWidth.Normal)
    {
        family = string.IsNullOrWhiteSpace(family) ? "Times New Roman" : family.Trim();
        var key = family + "|" + bold + "|" + italic + "|" + width;
        return Faces.GetOrAdd(key, _ =>
        {
            var style = new SKFontStyle(bold ? SKFontStyleWeight.Bold : SKFontStyleWeight.Normal, width, italic ? SKFontStyleSlant.Italic : SKFontStyleSlant.Upright);
            var tf = SKFontManager.Default.MatchFamily(family, style);
            if (tf == null)
            {
                var low = family.ToLowerInvariant();
                string fb = low.Contains("mono") || low.Contains("courier") || low.Contains("consol") ? "Consolas"
                    : low.Contains("times") || low.Contains("serif") && !low.Contains("sans") || low.Contains("roman") ? "Times New Roman" : "Arial";
                tf = SKFontManager.Default.MatchFamily(fb, style) ?? SKTypeface.Default;
            }
            return tf;
        });
    }

    public static SKTypeface Cjk => _cjk ??= SKFontManager.Default.MatchFamily("Microsoft JhengHei") ?? SKFontManager.Default.MatchCharacter('中') ?? SKTypeface.Default;

    /// <summary>Stroke-font stand-in for Altium PCB text (condensed sans shipped with Windows).</summary>
    public static SKTypeface StrokeFace => Face("Bahnschrift", false, false, SKFontStyleWidth.SemiCondensed);

    public readonly record struct Seg(string Text, bool Bar);

    public static List<Seg> Parse(string s)
    {
        var o = new List<Seg>();
        if (string.IsNullOrEmpty(s)) return o;
        int i = 0;
        while (i < s.Length)
        {
            int k = s.IndexOf("~{", i, StringComparison.Ordinal);
            if (k < 0) { o.Add(new Seg(s[i..], false)); break; }
            if (k > i) o.Add(new Seg(s[i..k], false));
            int e = s.IndexOf('}', k + 2);
            if (e < 0) { o.Add(new Seg(s[(k + 2)..], true)); break; }
            o.Add(new Seg(s[(k + 2)..e], true));
            i = e + 1;
        }
        return o;
    }

    static float Width(SKFont f, string s)
    {
        var g = new ushort[s.Length];
        f.GetGlyphs(s, g);
        return f.MeasureText(g);
    }

    static SKFont MakeFont(SKTypeface tf, float size) => new(tf, size) { Subpixel = true, Hinting = SKFontHinting.None, LinearMetrics = true, Edging = SKFontEdging.SubpixelAntialias };

    /// <summary>Split a string into runs that the primary face can draw and runs that need the CJK fallback.</summary>
    static IEnumerable<(string run, SKTypeface tf)> Runs(string s, SKTypeface primary)
    {
        if (string.IsNullOrEmpty(s)) yield break;
        bool Has(char c) => c < 0x2E80 || primary.ContainsGlyph(c);
        int start = 0; bool cur = Has(s[0]);
        for (int i = 1; i <= s.Length; i++)
        {
            bool h = i < s.Length && Has(s[i]);
            if (i == s.Length || h != cur) { yield return (s[start..i], cur ? primary : Cjk); start = i; cur = h; }
        }
    }

    public static float Measure(string text, SKTypeface tf, float size)
    {
        float w = 0;
        foreach (var seg in Parse(text))
            foreach (var (run, f) in Runs(seg.Text, tf)) { using var font = MakeFont(f, size); w += Width(font, run); }
        return w;
    }

    /// <summary>Draw at (x, baselineY) left-aligned; overbar segments get a line above the cap height.</summary>
    public static float Draw(SKCanvas c, string text, float x, float y, SKTypeface tf, float size, SKPaint paint)
    {
        float cx = x;
        foreach (var seg in Parse(text))
        {
            float x0 = cx;
            foreach (var (run, f) in Runs(seg.Text, tf))
            {
                using var font = MakeFont(f, size);
                c.DrawText(run, cx, y, font, paint);
                cx += Width(font, run);
            }
            if (seg.Bar && cx > x0)
            {
                using var lp = new SKPaint { Color = paint.Color, StrokeWidth = Math.Max(size * 0.06f, 0), IsStroke = true, IsAntialias = true };
                float by = y - size * 0.78f;
                c.DrawLine(x0, by, cx, by, lp);
            }
        }
        return cx - x;
    }
}
