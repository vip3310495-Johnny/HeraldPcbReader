// Herald PCB Reader — Copyright (C) 2026 Johnny.ch.Tsai
// SPDX-License-Identifier: GPL-3.0-only
// Free software under the GNU General Public License version 3 (LICENSE.txt), WITHOUT ANY WARRANTY; with an
// additional permission to combine it with AvalonDock (Ms-PL), see NOTICE.txt.
using AdCore.Sch;
using SkiaSharp;
using static AdCore.Util.TextUtil;

namespace AdCore.Render;

public sealed class SchTheme
{
    public string Name;
    public SKColor Paper, Grid, Frame, FrameText, Sym, SymFill, SymText, Ref, Val, Pin, PinNum, Wire, Bus, Label, Note, Nc, Jn, Pwr, PortFill, Blk, BlkFill, BlkText, Hl, Dim, Workspace;
    public bool UseFileColors;

    static SKColor C(string h) => SKColor.Parse(h);
    public static readonly SchTheme Altium = new()
    {
        Name = "Altium", Paper = C("#FFFFFF"), Grid = C("#E4E4E4"), Frame = C("#000000"), FrameText = C("#000000"), Sym = C("#000080"), SymFill = C("#FFFFB0"),
        SymText = C("#000000"), Ref = C("#000080"), Val = C("#000080"), Pin = C("#000080"), PinNum = C("#000000"), Wire = C("#000080"), Bus = C("#000080"),
        Label = C("#800000"), Note = C("#000080"), Nc = C("#FF0000"), Jn = C("#000080"), Pwr = C("#800000"), PortFill = C("#FFFF80"), Blk = C("#800000"),
        BlkFill = C("#80FF80"), BlkText = C("#000080"), Hl = C("#D3186D"), Dim = C("#C3CAC2"), Workspace = C("#D6D6D6"), UseFileColors = true,
    };
    public static readonly SchTheme Dark = new()
    {
        Name = "Dark", Paper = C("#0F1513"), Grid = C("#1D2723"), Frame = C("#3D4B43"), FrameText = C("#7D8D84"), Sym = C("#C0D0E5"), SymFill = C("#18212D"),
        SymText = C("#C5D2E2"), Ref = C("#DCE7F4"), Val = C("#9AABBD"), Pin = C("#E58A7C"), PinNum = C("#E58A7C"), Wire = C("#5FD49C"), Bus = C("#80A9FF"),
        Label = C("#E3B85C"), Note = C("#93A4C8"), Nc = C("#80A9FF"), Jn = C("#5FD49C"), Pwr = C("#E3B85C"), PortFill = C("#0F1513"), Blk = C("#80A9FF"),
        BlkFill = C("#18212D"), BlkText = C("#80A9FF"), Hl = C("#FF4FA0"), Dim = C("#36423B"), Workspace = C("#1E1E1E"),
    };
    public static readonly SchTheme Mono = new()
    {
        Name = "Mono", Paper = C("#FFFFFF"), Grid = C("#EEEEEE"), Frame = C("#000000"), FrameText = C("#000000"), Sym = C("#000000"), SymFill = C("#FFFFFF"),
        SymText = C("#000000"), Ref = C("#000000"), Val = C("#000000"), Pin = C("#000000"), PinNum = C("#000000"), Wire = C("#000000"), Bus = C("#000000"),
        Label = C("#000000"), Note = C("#000000"), Nc = C("#000000"), Jn = C("#000000"), Pwr = C("#000000"), PortFill = C("#FFFFFF"), Blk = C("#000000"),
        BlkFill = C("#FFFFFF"), BlkText = C("#000000"), Hl = C("#000000"), Dim = C("#BBBBBB"), Workspace = C("#D6D6D6"),
    };
}

public sealed class SchRenderOptions
{
    public SchTheme Theme = SchTheme.Altium;
    public double FontScale = 1.0;
    public double PixelMm = 0.1;          // world size of one device pixel (for hairlines)
    public HashSet<string> HlNets;         // local net names on this sheet
    public string HlRef;
    public HashSet<string> HlRefs;         // area selection
    public bool Mask;
    public bool ShowParams = true;         // values / parameters (designators always)
    public bool ShowGrid;
    public string FileName = "";
}

public static class SchRenderer
{
    static readonly double[] LwMm = { 0, 0.254, 0.508, 1.016 };

    sealed class Ctx
    {
        public SKCanvas C; public SchRenderOptions O; public SchTheme T; public float Px;
        public SKColor Col(SKColor? file, SKColor theme) => T.UseFileColors && file.HasValue ? file.Value : theme;
    }

    public static void DrawSheet(SKCanvas c, SchSheet sh, SchRenderOptions o)
    {
        var x = new Ctx { C = c, O = o, T = o.Theme, Px = (float)o.PixelMm };
        if (sh.Page != null)
        {
            using var bg = new SKPaint { Color = x.Col(sh.Page.AreaColor, x.T.Paper), Style = SKPaintStyle.Fill };
            c.DrawRect(0, 0, (float)sh.Page.W, (float)sh.Page.H, bg);
        }
        else
        {
            // formats without a page (Eagle): paper behind the drawing's extent
            using var bg = new SKPaint { Color = x.T.Paper, Style = SKPaintStyle.Fill };
            var b = sh.BBox; c.DrawRect((float)b[0] - 6, (float)b[1] - 6, (float)(b[2] - b[0]) + 12, (float)(b[3] - b[1]) + 12, bg);
        }
        if (o.ShowGrid) DrawGrid(x, sh);
        if (sh.Page != null) DrawFrame(x, sh);
        bool netHl(string n) => n != null && o.HlNets != null && o.HlNets.Contains(n);
        foreach (var g in sh.Graphics) DrawPrim(x, g, State(x, false));
        foreach (var t in sh.Texts) DrawText(x, t, State(x, false));
        foreach (var b in sh.Blocks) DrawBlock(x, b, State(x, false));
        foreach (var comp in sh.Comps)
        {
            bool hl = (o.HlRef != null && comp.Ref == o.HlRef) || (o.HlRefs != null && o.HlRefs.Contains(comp.Ref)) || ((comp.Power || comp.Virtual) && comp.Pins.Any(p => netHl(p.Net)));
            DrawComp(x, comp, hl, netHl);
        }
        foreach (var w in sh.Wires) DrawWire(x, w, State(x, w.Kind == WireKind.Wire && netHl(w.Net)));
        foreach (var j in sh.Junctions)
        {
            var st = State(x, netHl(j.Net));
            using var p = Fill(st.hl ? x.T.Hl : st.dim ? x.T.Dim : x.Col(j.Color, x.T.Jn));
            c.DrawCircle((float)j.X, (float)j.Y, st.hl ? 0.6f : 0.46f, p);
        }
        foreach (var n in sh.NoConnects)
        {
            var st = State(x, false); const float d = 0.635f;
            using var p = Stroke(st.dim ? x.T.Dim : x.Col(n.Color, x.T.Nc), x.Px);
            c.DrawLine((float)n.X - d, (float)n.Y - d, (float)n.X + d, (float)n.Y + d, p);
            c.DrawLine((float)n.X - d, (float)n.Y + d, (float)n.X + d, (float)n.Y - d, p);
        }
        foreach (var l in sh.Labels) DrawLabel(x, l, State(x, netHl(l.Net)));
    }

    static (bool hl, bool dim) State(Ctx x, bool hl) => (hl, !hl && x.O.Mask);

    static SKPaint Stroke(SKColor c, float w, int style = 0, float px = 0)
    {
        var p = new SKPaint { Color = c, Style = SKPaintStyle.Stroke, StrokeWidth = w, IsAntialias = true, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round };
        if (style > 0 && px > 0)
        {
            float[] iv = style switch { 1 => new[] { 4 * px, 3 * px }, 2 => new[] { 1 * px, 2.5f * px }, _ => new[] { 5 * px, 2 * px, 1 * px, 2 * px } };
            p.PathEffect = SKPathEffect.CreateDash(iv, 0);
            p.StrokeCap = SKStrokeCap.Butt;
        }
        return p;
    }
    static SKPaint Fill(SKColor c) => new() { Color = c, Style = SKPaintStyle.Fill, IsAntialias = true };

    static void DrawGrid(Ctx x, SchSheet sh)
    {
        double w = sh.Page?.W ?? sh.BBox[2], h = sh.Page?.H ?? sh.BBox[3];
        double step = 2.54; while (step / x.Px < 6) step *= 5;
        using var p = Stroke(x.T.Grid, x.Px);
        for (double gx = 0; gx <= w; gx += step) x.C.DrawLine((float)gx, 0, (float)gx, (float)h, p);
        for (double gy = 0; gy <= h; gy += step) x.C.DrawLine(0, (float)gy, (float)w, (float)gy, p);
    }

    static SKColor RoleColor(Ctx x, string role) => role switch
    {
        "ref" => x.T.Ref, "val" => x.T.Val, "fld" => x.T.Val, "ntxt" => x.T.Note, "lbl" => x.T.Label, "pname" => x.T.SymText,
        "pnum" => x.T.PinNum, "blkt" => x.T.BlkText, "frm-t" or "frm-v" => x.T.FrameText, _ => x.T.SymText,
    };

    public static SKTypeface FaceOf(SchFont f) => TextPainter.Face(f?.Family ?? "Times New Roman", f?.Bold ?? false, f?.Italic ?? false);

    public static float EmSize(SchText t, double fontScale) => (float)(t.Size * SchText.FS * fontScale);

    /// <summary>Wrap text frame content at maxW (CJK can break anywhere).</summary>
    static List<string> Wrap(string text, SKTypeface tf, float fs, float maxW)
    {
        var o = new List<string>();
        foreach (var para in text.Split('\n'))
        {
            var toks = System.Text.RegularExpressions.Regex.Matches(para, @"[⺀-鿿豈-﫿＀-￯]|[^\s⺀-鿿豈-﫿＀-￯]+|\s+").Select(m => m.Value).ToList();
            if (toks.Count == 0) toks.Add("");
            var line = "";
            foreach (var tk in toks)
            {
                var cand = line + tk;
                if (line != "" && TextPainter.Measure(cand.TrimEnd(), tf, fs) > maxW && !string.IsNullOrWhiteSpace(tk)) { o.Add(line.TrimEnd()); line = tk.TrimStart(); }
                else line = cand;
            }
            o.Add(line.TrimEnd());
        }
        return o;
    }

    static void DrawText(Ctx x, SchText t, (bool hl, bool dim) st) => DrawTextCore(x.C, t, st.hl ? x.T.Hl : st.dim ? x.T.Dim : x.Col(t.Color, RoleColor(x, t.Role)), x.O.FontScale);

    public static void DrawTextCore(SKCanvas c, SchText t, SKColor color, double fontScale)
    {
        if (string.IsNullOrEmpty(t.Text)) return;
        float fs = EmSize(t, fontScale); if (fs <= 0) return;
        var tf = FaceOf(t.Font);
        using var paint = new SKPaint { Color = color, IsAntialias = true, Style = SKPaintStyle.Fill };
        var lines = t.Text.Split('\n').ToList();
        float lh = fs * 1.15f, x0 = (float)t.X, y0;
        HAnchor ha = t.HA;
        if (t.Frame != null)
        {
            var F = t.Frame; float inner = (float)Math.Max(1, F.W - 2 * F.Margin);
            if (F.Wrap) lines = Wrap(t.Text, tf, fs, inner);
            x0 = (float)(F.Align == 2 ? F.X + F.W / 2 : F.Align == 3 ? F.X + F.W - F.Margin : F.X + F.Margin);
            ha = F.Align == 2 ? HAnchor.Middle : F.Align == 3 ? HAnchor.End : HAnchor.Start;
            y0 = (float)(F.Y + F.Margin + fs * 0.8);
        }
        else
        {
            float shift = t.VA == VAnchor.Top ? fs * 0.72f : t.VA == VAnchor.Center ? fs * 0.36f : 0; lh = fs * 1.2f;
            y0 = (float)t.Y + shift;
            if (t.VA == VAnchor.Bottom) y0 -= (lines.Count - 1) * lh; else if (t.VA == VAnchor.Center) y0 -= (lines.Count - 1) * lh / 2;
        }
        c.Save();
        if (t.Rot90) c.RotateDegrees(-90, (float)t.X, (float)t.Y);
        for (int k = 0; k < lines.Count; k++)
        {
            var ln = lines[k]; if (ln.Length == 0) continue;
            float w = ha == HAnchor.Start ? 0 : TextPainter.Measure(ln, tf, fs);
            float lx = ha == HAnchor.Start ? x0 : ha == HAnchor.Middle ? x0 - w / 2 : x0 - w;
            TextPainter.Draw(c, ln, lx, y0 + k * lh, tf, fs, paint);
        }
        c.Restore();
    }

    static void DrawPrim(Ctx x, SchPrim p, (bool hl, bool dim) st, SKColor? strokeOverride = null)
    {
        if (p is SchText t) { DrawText(x, t, st); return; }
        SKColor baseStroke = strokeOverride ?? (p.Role == "pwr" ? x.T.Pwr : x.T.Sym);
        SKColor sc = st.hl ? x.T.Hl : st.dim ? x.T.Dim : x.Col(p.Stroke, baseStroke);
        float w = Math.Max(x.Px, (float)LwMm[Math.Clamp(p.LineWidth, 0, 3)]);
        if (st.hl) w += x.Px;
        if (p is SchPoly q)
        {
            if (q.Pts.Count < 2) return;
            using var path = new SKPath();
            path.MoveTo((float)q.Pts[0].X, (float)q.Pts[0].Y);
            for (int i = 1; i < q.Pts.Count; i++) path.LineTo((float)q.Pts[i].X, (float)q.Pts[i].Y);
            if (q.Fill != FillKind.None)
            {
                SKColor fc = q.Fill == FillKind.Fg ? sc : (st.dim ? x.T.Paper : x.Col(q.FillColor, q.Extra == "tfill" ? x.T.Paper : x.T.SymFill));
                if (q.Extra == "cmask") fc = x.T.UseFileColors ? new SKColor(0xE6, 0xE6, 0xE6, 0x80) : fc.WithAlpha(0x40);
                if (q.Transparent) fc = fc.WithAlpha(0x60);
                using var fp = Fill(fc);
                x.C.DrawPath(path, fp);
                if (q.Extra == "tfill") return;
            }
            using var sp = Stroke(sc, w, q.Dash ? 1 : q.LineStyle, x.Px);
            x.C.DrawPath(path, sp);
        }
        else if (p is SchCircle ci)
        {
            if (ci.Fill != FillKind.None)
            {
                using var fp = Fill(ci.Fill == FillKind.Fg ? sc : st.dim ? x.T.Paper : x.Col(ci.FillColor, x.T.SymFill));
                x.C.DrawCircle((float)ci.Cx, (float)ci.Cy, (float)ci.R, fp);
            }
            using var sp = Stroke(sc, w, p.LineStyle, x.Px);
            x.C.DrawCircle((float)ci.Cx, (float)ci.Cy, (float)ci.R, sp);
        }
    }

    static void DrawComp(Ctx x, SchComponent c, bool hl, Func<string, bool> netHl)
    {
        var st = State(x, hl);
        SKColor? symStroke = c.Power ? x.T.Pwr : null;
        foreach (var p in c.Prims) DrawPrim(x, p, st, symStroke);
        foreach (var p in c.Pins) DrawPin(x, p, c, hl ? st : State(x, netHl(p.Net)));
        foreach (var t in c.FTexts) if (x.O.ShowParams || t.Role == "ref" || c.Power || c.Virtual) DrawText(x, t, st);
        if (c.Dnp)
        {
            var b = c.BBox; using var dp = Stroke(x.T.Hl.WithAlpha(0x40), x.Px * 2);
            x.C.DrawLine((float)b[0], (float)b[1], (float)b[2], (float)b[3], dp); x.C.DrawLine((float)b[0], (float)b[3], (float)b[2], (float)b[1], dp);
        }
    }

    static void DrawPin(Ctx x, SchPin p, SchComponent c, (bool hl, bool dim) st)
    {
        if (p.Hidden) return;
        SKColor col = st.hl ? x.T.Hl : st.dim ? x.T.Dim : x.Col(p.Color, x.T.Pin);
        double len = Math.Sqrt((p.Ex - p.X) * (p.Ex - p.X) + (p.Ey - p.Y) * (p.Ey - p.Y));
        float w = x.Px * (st.hl ? 2 : 1);
        if (len > 0.01)
        {
            using var lp = Stroke(col, w);
            if (p.Inv)
            {
                double r = Math.Min(0.5, len / 3), bx = p.Ex - p.Dx * r, by = p.Ey - p.Dy * r;
                x.C.DrawLine((float)p.X, (float)p.Y, (float)(bx - p.Dx * r), (float)(by - p.Dy * r), lp);
                x.C.DrawCircle((float)bx, (float)by, (float)r, lp);
            }
            else x.C.DrawLine((float)p.X, (float)p.Y, (float)p.Ex, (float)p.Ey, lp);
        }
        bool horiz = Math.Abs(p.Dx) >= Math.Abs(p.Dy);
        double mx = (p.X + p.Ex) / 2, my = (p.Y + p.Ey) / 2, off = 0.35;
        var nm = p.Name != "~" ? p.Name : "";
        bool showName = (p.ShowName ?? c.ShowNames) && nm != "";
        bool showNum = (p.ShowNum ?? c.ShowNums) && p.Num != "" && !c.Power;
        bool inside = c.NameOffset > 0;
        if (showName)
        {
            SchText t;
            if (inside)
            {
                double tx = p.Ex + p.Dx * c.NameOffset, ty = p.Ey + p.Dy * c.NameOffset;
                t = horiz ? new SchText { X = tx, Y = ty, Text = nm, Size = p.NameSize, Font = p.NameFont, HA = p.Dx > 0 ? HAnchor.Start : HAnchor.End, VA = VAnchor.Center, Role = "pname" }
                          : new SchText { X = tx, Y = ty, Text = nm, Size = p.NameSize, Font = p.NameFont, Rot90 = true, HA = p.Dy < 0 ? HAnchor.Start : HAnchor.End, VA = VAnchor.Center, Role = "pname" };
            }
            else
                t = horiz ? new SchText { X = mx, Y = my - off, Text = nm, Size = p.NameSize, Font = p.NameFont, HA = HAnchor.Middle, VA = VAnchor.Bottom, Role = "pname" }
                          : new SchText { X = mx - off, Y = my, Text = nm, Size = p.NameSize, Font = p.NameFont, Rot90 = true, HA = HAnchor.Middle, VA = VAnchor.Bottom, Role = "pname" };
            DrawText(x, t, (st.hl, st.dim));
        }
        if (showNum && len > 0.01)
        {
            bool below = showName && !inside;
            var t = horiz ? new SchText { X = mx, Y = below ? my + off : my - off, Text = p.Num, Size = p.NumSize, Font = p.NumFont, HA = HAnchor.Middle, VA = below ? VAnchor.Top : VAnchor.Bottom, Role = "pnum" }
                          : new SchText { X = below ? mx + off : mx - off, Y = my, Text = p.Num, Size = p.NumSize, Font = p.NumFont, Rot90 = true, HA = HAnchor.Middle, VA = below ? VAnchor.Top : VAnchor.Bottom, Role = "pnum" };
            DrawText(x, t, (st.hl, st.dim));
        }
    }

    static void DrawWire(Ctx x, SchWire w, (bool hl, bool dim) st)
    {
        SKColor col = st.hl ? x.T.Hl : st.dim ? x.T.Dim : x.Col(w.Color, w.Kind == WireKind.Wire ? x.T.Wire : x.T.Bus);
        float width = w.Kind == WireKind.Bus ? Math.Max(2 * x.Px, 0.5f) : Math.Max(x.Px, 0.18f);
        if (st.hl) width = width * 1.8f + x.Px;
        using var p = Stroke(col, width);
        x.C.DrawLine((float)w.X1, (float)w.Y1, (float)w.X2, (float)w.Y2, p);
    }

    public sealed class LabelShape { public List<P2> Poly; public SchText Text; public double[] BB; }

    public static LabelShape LabelGeom(SchLabel l, double fontScale)
    {
        if (l.Kind == LabelKind.Port && l.Port != null) return PortGeom(l);
        double fs = l.Size * SchText.FS * fontScale, sc = fontScale;
        var txt = PlainText(l.Text);
        double tw = TextPainter.Measure(txt, FaceOf(l.Font), (float)fs), h = l.Size * sc * 1.9, gap = l.Size * sc * 0.45;
        var u = new (int x, int y)[] { (1, 0), (0, -1), (-1, 0), (0, 1) }[l.Dir & 3];
        bool horiz = (l.Dir & 1) == 0; var v = horiz ? (x: 0, y: 1) : (x: 1, y: 0);
        P2 P(double a, double b) => new(l.X + u.x * a + v.x * b, l.Y + u.y * a + v.y * b);
        bool rot = !horiz; var ha = horiz ? (u.x > 0 ? HAnchor.Start : HAnchor.End) : (u.y < 0 ? HAnchor.Start : HAnchor.End);
        List<P2> poly = null; P2 tp; var va = VAnchor.Center; double far;
        if (l.Kind == LabelKind.Global) { far = h / 2 + gap + tw + gap; poly = new() { P(0, 0), P(h / 2, -h / 2), P(far, -h / 2), P(far, h / 2), P(h / 2, h / 2) }; tp = P(h / 2 + gap, 0); }
        else if (l.Kind is LabelKind.Hier or LabelKind.SheetPin) { poly = new() { P(0, 0), P(h * .45, -h * .45), P(h * .9, -h * .45), P(h * .9, h * .45), P(h * .45, h * .45) }; tp = P(h * .9 + gap, 0); far = h * .9 + gap + tw; }
        else { tp = P(0.25, -0.3); va = VAnchor.Bottom; far = tw + 0.3; }
        var q1 = poly != null ? P(0, -h / 2) : P(0, -fs * 0.9); var q2 = poly != null ? P(far, h / 2) : P(far, 0);
        return new LabelShape
        {
            Poly = poly, Text = new SchText { X = tp.X, Y = tp.Y, Text = l.Text, Size = l.Size, Font = l.Font, Rot90 = rot, HA = ha, VA = va, Role = "lbl", Color = l.TextColor },
            BB = new[] { Math.Min(q1.X, q2.X), Math.Min(q1.Y, q2.Y), Math.Max(q1.X, q2.X), Math.Max(q1.Y, q2.Y) },
        };
    }

    static LabelShape PortGeom(SchLabel l)
    {
        var P = l.Port; double h = P.H, t = h / 2;
        if (!P.Vert)
        {
            double xa = Math.Min(P.X1, P.X2), xb = Math.Max(P.X1, P.X2), y = P.Y1;
            var poly = new List<P2> { new(xa + (P.StartTip ? t : 0), y - t), new(xb - (P.EndTip ? t : 0), y - t), new(xb, y), new(xb - (P.EndTip ? t : 0), y + t), new(xa + (P.StartTip ? t : 0), y + t), new(xa, y) };
            return new LabelShape { Poly = poly, Text = new SchText { X = (xa + xb) / 2, Y = y, Text = l.Text, Size = l.Size, Font = l.Font, HA = HAnchor.Middle, VA = VAnchor.Center, Role = "lbl", Color = l.TextColor }, BB = new[] { xa, y - t, xb, y + t } };
        }
        double ya = Math.Min(P.Y1, P.Y2), yb = Math.Max(P.Y1, P.Y2), x = P.X1;
        var pv = new List<P2> { new(x - t, ya + (P.EndTip ? t : 0)), new(x, ya), new(x + t, ya + (P.EndTip ? t : 0)), new(x + t, yb - (P.StartTip ? t : 0)), new(x, yb), new(x - t, yb - (P.StartTip ? t : 0)) };
        return new LabelShape { Poly = pv, Text = new SchText { X = x, Y = (ya + yb) / 2, Text = l.Text, Size = l.Size, Font = l.Font, Rot90 = true, HA = HAnchor.Middle, VA = VAnchor.Center, Role = "lbl", Color = l.TextColor }, BB = new[] { x - t, ya, x + t, yb } };
    }

    static void DrawLabel(Ctx x, SchLabel l, (bool hl, bool dim) st)
    {
        if (l.SkipDraw) return;
        var g = LabelGeom(l, x.O.FontScale);
        if (g.Poly != null)
        {
            using var path = new SKPath();
            path.MoveTo((float)g.Poly[0].X, (float)g.Poly[0].Y);
            for (int i = 1; i < g.Poly.Count; i++) path.LineTo((float)g.Poly[i].X, (float)g.Poly[i].Y);
            path.Close();
            using var fp = Fill(st.dim ? x.T.Paper : x.Col(l.Fill, x.T.PortFill));
            x.C.DrawPath(path, fp);
            using var sp = Stroke(st.hl ? x.T.Hl : st.dim ? x.T.Dim : x.Col(l.Stroke, x.T.Label), x.Px * (st.hl ? 2 : 1));
            x.C.DrawPath(path, sp);
        }
        DrawText(x, g.Text, st);
    }

    static void DrawBlock(Ctx x, SheetBlock b, (bool hl, bool dim) st)
    {
        var r = new SKRect((float)b.X, (float)b.Y, (float)(b.X + b.W), (float)(b.Y + b.H));
        using (var fp = Fill(st.dim ? x.T.Paper : x.Col(b.Fill, x.T.BlkFill))) x.C.DrawRect(r, fp);
        using (var sp = Stroke(st.dim ? x.T.Dim : x.Col(b.Stroke, x.T.Blk), x.Px)) x.C.DrawRect(r, sp);
        if (b.Name != "") DrawText(x, new SchText { X = b.X, Y = b.Y - 0.4, Text = b.Name, Size = b.NameSize, Font = b.NameFont, HA = HAnchor.Start, VA = VAnchor.Bottom, Role = "blkt", Color = b.NameColor }, st);
        if (b.File != "") DrawText(x, new SchText { X = b.X, Y = b.Y + b.H + 0.4, Text = b.File, Size = b.FileSize, Font = b.FileFont, HA = HAnchor.Start, VA = VAnchor.Top, Role = "blkt", Color = b.FileColor }, st);
    }

    static void DrawFrame(Ctx x, SchSheet sh)
    {
        var pg = sh.Page; if (pg.NoBorder) return;
        double w = pg.W, h = pg.H, m = pg.Frame.M, m2 = pg.Frame.M2;
        var T = sh.Title; string Tt(string k) => T.TryGetValue(k, out var v) ? v : "";
        using var lp = Stroke(x.T.Frame, x.Px);
        x.C.DrawRect((float)m, (float)m, (float)(w - 2 * m), (float)(h - 2 * m), lp);
        x.C.DrawRect((float)m2, (float)m2, (float)(w - 2 * m2), (float)(h - 2 * m2), lp);
        int nx = pg.Frame.Nx > 0 ? pg.Frame.Nx : Math.Max(2, (int)Math.Round((w - 2 * m) / 50)), ny = pg.Frame.Ny > 0 ? pg.Frame.Ny : Math.Max(2, (int)Math.Round((h - 2 * m) / 50));
        double zc = (m + m2) / 2;
        void Tx(double tx, double ty, string t, double size, string role = "frm-t", HAnchor ha = HAnchor.Middle, VAnchor va = VAnchor.Center) =>
            DrawText(x, new SchText { X = tx, Y = ty, Text = t, Size = size, HA = ha, VA = va, Role = role }, (false, false));
        void L(double x1, double y1, double x2, double y2) => x.C.DrawLine((float)x1, (float)y1, (float)x2, (float)y2, lp);
        for (int k = 0; k < nx; k++)
        {
            double x0 = m + (w - 2 * m) * k / nx, x1 = m + (w - 2 * m) * (k + 1) / nx;
            if (k > 0) { L(x0, m, x0, m2); L(x0, h - m2, x0, h - m); }
            Tx((x0 + x1) / 2, zc, (k + 1).ToString(), 0.9); Tx((x0 + x1) / 2, h - zc, (k + 1).ToString(), 0.9);
        }
        for (int k = 0; k < ny; k++)
        {
            double y0 = m + (h - 2 * m) * k / ny, y1 = m + (h - 2 * m) * (k + 1) / ny; var lab = ((char)(65 + k)).ToString();
            if (k > 0) { L(m, y0, m2, y0); L(w - m2, y0, w - m, y0); }
            Tx(zc, (y0 + y1) / 2, lab, 0.9); Tx(w - zc, (y0 + y1) / 2, lab, 0.9);
        }
        if (pg.NoTitle) return;
        double bw = Math.Min(110, w - 2 * m2 - 10), bx2 = w - m2, bx1 = bx2 - bw; double[] rows = { 6, 9, 6.5, 6.5 };
        double by2 = h - m2, by1 = by2 - rows.Sum();
        x.C.DrawRect((float)bx1, (float)by1, (float)bw, (float)(by2 - by1), lp);
        double yy = by1; var ys = new List<double>();
        foreach (var r in rows) { ys.Add(yy); yy += r; if (yy < by2 - 0.01) L(bx1, yy, bx2, yy); }
        void Lab(double lx, double ly, string t) => Tx(lx + 1, ly + 0.8, t, 0.8, "frm-t", HAnchor.Start, VAnchor.Top);
        void Val(double lx, double ly, string t, double size) => Tx(lx + 1, ly, t, size, "frm-v", HAnchor.Start, VAnchor.Bottom);
        Lab(bx1, ys[0], "Company"); Val(bx1, ys[0] + 5.3, Tt("company"), 1.2);
        Lab(bx1, ys[1], "Title"); Val(bx1, ys[1] + 7.8, Tt("title"), 1.9);
        double c1 = bx1 + bw * 0.3, c2 = bx1 + bw * 0.72;
        L(c1, ys[2], c1, ys[3]); L(c2, ys[2], c2, ys[3]);
        Lab(bx1, ys[2], "Size"); Val(bx1, ys[2] + 5.8, pg.Name, 1.1); Lab(c1, ys[2], "Date"); Val(c1, ys[2] + 5.8, Tt("date"), 1.1); Lab(c2, ys[2], "Rev"); Val(c2, ys[2] + 5.8, Tt("rev"), 1.1);
        Lab(bx1, ys[3], "File"); Val(bx1, ys[3] + 5.8, x.O.FileName ?? "", 1.0);
        Tx(bx2 - 1, ys[3] + 5.8, "Sheet " + (Tt("sheet") != "" ? Tt("sheet") : "1/1"), 1.0, "frm-v", HAnchor.End, VAnchor.Bottom);
    }
}
