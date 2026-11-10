// Herald PCB Reader — Copyright (C) 2026 Johnny.ch.Tsai
// SPDX-License-Identifier: GPL-3.0-only
// Free software under the GNU General Public License version 3 (LICENSE.txt), WITHOUT ANY WARRANTY; with an
// additional permission to combine it with AvalonDock (Ms-PL), see NOTICE.txt.
using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using static AdCore.Util.TextUtil;

namespace AdCore.Sch;

/// <summary>Shared geometry for KiCad / Eagle symbol instancing (ports of the v1 helpers).</summary>
public static class SymGeo
{
    /// <summary>An angle step brought into (-π, π] — what "while (x &gt; π) x -= 2π" did, but in one step, so a
    /// damaged file with a huge angle cannot hang the parser.</summary>
    public static double WrapPi(double x)
    {
        if (!double.IsFinite(x)) return 0;
        x = Math.IEEERemainder(x, 2 * Math.PI);
        return x <= -Math.PI ? x + 2 * Math.PI : x;
    }

    /// <summary>b moved on by whole periods to the first value ≥ a — what "while (b &lt; a) b += period" did, in one step.</summary>
    public static double AtLeast(double b, double a, double period) =>
        b >= a || !double.IsFinite(a) || !double.IsFinite(b) ? b : a + ((b - a) % period + period) % period;

    public const double MIL = 0.0254;
    public static readonly double[][] ROT = { new double[] { 1, 0, 0, -1 }, new double[] { 0, -1, -1, 0 }, new double[] { -1, 0, 0, 1 }, new double[] { 0, 1, 1, 0 } };
    public static double D(string s) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;

    public static List<P2> ArcSweep(double cx, double cy, double r, double a0, double sweep)
    {
        int n = Math.Max(6, (int)Math.Ceiling(Math.Abs(sweep) / (Math.PI / 18)));
        var p = new List<P2>(); for (int k = 0; k <= n; k++) { double a = a0 + sweep * k / n; p.Add(new P2(cx + r * Math.Cos(a), cy + r * Math.Sin(a))); }
        return p;
    }
    public static List<P2> Arc3(P2 a, P2 m, P2 b)
    {
        double d = 2 * (a.X * (m.Y - b.Y) + m.X * (b.Y - a.Y) + b.X * (a.Y - m.Y));
        if (Math.Abs(d) < 1e-12) return new() { a, b };
        double a2 = a.X * a.X + a.Y * a.Y, m2 = m.X * m.X + m.Y * m.Y, b2 = b.X * b.X + b.Y * b.Y;
        double cx = (a2 * (m.Y - b.Y) + m2 * (b.Y - a.Y) + b2 * (a.Y - m.Y)) / d, cy = (a2 * (b.X - m.X) + m2 * (a.X - b.X) + b2 * (m.X - a.X)) / d;
        double r = Math.Sqrt((a.X - cx) * (a.X - cx) + (a.Y - cy) * (a.Y - cy)), tau = Math.PI * 2;
        double Nm(double v) => ((v % tau) + tau) % tau;
        double t0 = Math.Atan2(a.Y - cy, a.X - cx), s = Nm(Math.Atan2(b.Y - cy, b.X - cx) - t0), sm = Nm(Math.Atan2(m.Y - cy, m.X - cx) - t0);
        return ArcSweep(cx, cy, r, t0, sm <= s ? s : s - tau);
    }
    public static List<P2> ArcShort(double cx, double cy, double r, double sx, double sy, double ex, double ey)
    {
        double t0 = Math.Atan2(sy - cy, sx - cx), sw = Math.Atan2(ey - cy, ex - cx) - t0;
        sw = SymGeo.WrapPi(sw);
        return ArcSweep(cx, cy, r, t0, sw);
    }
    public static List<P2> Bez(List<P2> p)
    {
        var o = new List<P2>();
        for (int k = 0; k <= 16; k++) { double t = k / 16.0, u = 1 - t; o.Add(new P2(u * u * u * p[0].X + 3 * u * u * t * p[1].X + 3 * u * t * t * p[2].X + t * t * t * p[3].X, u * u * u * p[0].Y + 3 * u * u * t * p[1].Y + 3 * u * t * t * p[2].Y + t * t * t * p[3].Y)); }
        return o;
    }
    public static List<P2> Rect(P2 a, P2 b) => new() { a, new(b.X, a.Y), b, new(a.X, b.Y), a };
    public static SchWire Seg(P2 a, P2 b, WireKind k) => new() { X1 = a.X, Y1 = a.Y, X2 = b.X, Y2 = b.Y, Kind = k };
    public static Func<double, double, P2> TL(double[] M) => (x, y) => new P2(M[0] * x + M[1] * y, M[2] * x + M[3] * y);

    public sealed class Item { public string K; public int Unit, Conv; public List<P2> Pts; public FillKind Fill; public double Cx, Cy, R, X, Y, Ang, Size = 1.27; public string Text, HJ = "center", VJ = "center"; public bool Hidden; }
    public sealed class LibPin { public int Unit, Conv; public double X, Y, Len = 2.54, Ang, NameSize = 1.27, NumSize = 1.27; public string Name = "", Num = "", Type = "passive"; public bool Inv, Hidden; }

    public static void Instantiate(SchComponent c, KicadLegacy.LibSymbol L, int unit, int conv, double[] M, double ox, double oy)
    {
        P2 T(double x, double y) => new(ox + M[0] * x + M[1] * y, oy + M[2] * x + M[3] * y);
        var tl = TL(M);
        bool Ok(int u, int cv) => (u == 0 || u == unit) && (cv == 0 || cv == conv);
        foreach (var it in L.Items)
        {
            if (!Ok(it.Unit, it.Conv)) continue;
            if (it.K == "poly") c.Prims.Add(new SchPoly { Pts = it.Pts.Select(p => T(p.X, p.Y)).ToList(), Fill = it.Fill });
            else if (it.K == "circle") { var q = T(it.Cx, it.Cy); c.Prims.Add(new SchCircle { Cx = q.X, Cy = q.Y, R = it.R, Fill = it.Fill }); }
            else if (it.K == "text" && !it.Hidden) { var q = T(it.X, it.Y); c.Prims.Add(SchText.Place(it.Text, q.X, q.Y, it.Ang, it.HJ, it.VJ, it.Size, tl, "stxt")); }
        }
        foreach (var p in L.Pins)
        {
            if (!Ok(p.Unit, p.Conv)) continue;
            double a = p.Ang * Math.PI / 180; int ux = (int)Math.Round(Math.Cos(a)), uy = (int)Math.Round(Math.Sin(a));
            var s = T(p.X, p.Y); var e = T(p.X + ux * p.Len, p.Y + uy * p.Len); var d = tl(ux, uy);
            c.Pins.Add(new SchPin { X = s.X, Y = s.Y, Ex = e.X, Ey = e.Y, Dx = d.X, Dy = d.Y, Num = p.Num, Name = p.Name == "~" ? "" : p.Name, Type = p.Type, Inv = p.Inv, Hidden = p.Hidden, NameSize = p.NameSize, NumSize = p.NumSize });
        }
        c.ShowNames = L.ShowNames; c.ShowNums = L.ShowNums; c.NameOffset = L.NameOffset;
    }

    public static void Placeholder(SchComponent c, double x, double y, bool pwr)
    {
        if (pwr)
        {
            c.Prims.Add(new SchCircle { Cx = x, Cy = y, R = 0.5 });
            c.Pins.Add(new SchPin { X = x, Y = y, Ex = x, Ey = y, Dx = 0, Dy = -1, Num = "1", Type = "power_in", Hidden = true });
        }
        else c.Prims.Add(new SchPoly { Pts = Rect(new P2(x - 2.54, y - 2.54), new P2(x + 2.54, y + 2.54)), Dash = true });
    }
}

/// <summary>Minimal S-expression reader for .kicad_sch.</summary>
static class SExpr
{
    public static object Parse(string src)
    {
        int n = src.Length, i = 0; var stack = new Stack<List<object>>(); stack.Push(new List<object>());
        while (i < n)
        {
            char c = src[i];
            if (c == '(') { var l = new List<object>(); stack.Peek().Add(l); stack.Push(l); i++; }
            else if (c == ')') { if (stack.Count < 2) throw new InvalidDataException("括號不成對（檔案可能損毀）"); stack.Pop(); i++; }
            else if (c == '"')
            {
                var sb = new System.Text.StringBuilder(); int j = i + 1;
                while (true)
                {
                    if (j >= n) throw new InvalidDataException("字串沒有結束引號（檔案可能損毀）");
                    char d = src[j];
                    if (d == '\\' && j + 1 < n) { char e = src[j + 1]; sb.Append(e == 'n' ? '\n' : e == 't' ? '\t' : e == 'r' ? "" : e.ToString()); j += 2; }
                    else if (d == '"') { j++; break; }
                    else { sb.Append(d); j++; }
                }
                stack.Peek().Add(sb.ToString()); i = j;
            }
            else if (c <= ' ') i++;
            else { int j = i + 1; while (j < n && src[j] > ' ' && src[j] != '(' && src[j] != ')' && src[j] != '"') j++; stack.Peek().Add(src[i..j]); i = j; }
        }
        return stack.Last().FirstOrDefault();
    }
    public static List<object> F(object node, string name) { if (node is not List<object> l) return null; for (int k = 1; k < l.Count; k++) if (l[k] is List<object> c && c.Count > 0 && c[0] as string == name) return c; return null; }
    public static IEnumerable<List<object>> FA(object node, string name) { if (node is not List<object> l) yield break; for (int k = 1; k < l.Count; k++) if (l[k] is List<object> c && c.Count > 0 && c[0] as string == name) yield return c; }
    public static string S(List<object> l, int i) => l != null && i < l.Count ? l[i] as string : null;
    public static double N(List<object> l, int i, double def = 0) { var s = S(l, i); return s == null ? def : SymGeo.D(s); }
    public static bool Hidden(object node, int from = 1)
    {
        if (node is not List<object> l) return false;
        for (int k = from; k < l.Count; k++) { if (l[k] as string == "hide") return true; if (l[k] is List<object> c && c.Count > 0 && c[0] as string == "hide" && S(c, 1) != "no") return true; }
        return false;
    }
    public static (double size, string hj, string vj, bool hidden) Effects(object node)
    {
        double size = 1.27; string hj = "center", vj = "center"; bool hid = false;
        var e = F(node, "effects"); if (e == null) return (size, hj, vj, hid);
        var f = F(e, "font"); if (f != null) { var sz = F(f, "size"); if (sz != null) size = N(sz, 1, 1.27); if (size == 0) size = 1.27; }
        var j = F(e, "justify"); if (j != null) for (int k = 1; k < j.Count; k++) { var t = j[k] as string; if (t is "left" or "right") hj = t; else if (t is "top" or "bottom") vj = t; }
        return (size, hj, vj, Hidden(e));
    }
    public static P2 XY(List<object> n) => new(N(n, 1), N(n, 2));
    public static List<P2> Pts(object node) { var p = F(node, "pts"); return p == null ? new() : FA(p, "xy").Select(XY).ToList(); }
    public static List<object> Find(object node, string tag)
    {
        if (node is not List<object> l) return null; if (l.Count > 0 && l[0] as string == tag) return l;
        for (int k = 1; k < l.Count; k++) { var r = Find(l[k], tag); if (r != null) return r; }
        return null;
    }
}

public static class KicadSch
{
    static readonly Dictionary<string, (double w, double h)> Paper = new() { ["A5"] = (210, 148), ["A4"] = (297, 210), ["A3"] = (420, 297), ["A2"] = (594, 420), ["A1"] = (841, 594), ["A0"] = (1189, 841), ["A"] = (279.4, 215.9), ["B"] = (431.8, 279.4), ["C"] = (558.8, 431.8), ["D"] = (863.6, 558.8), ["E"] = (1117.6, 863.6), ["USLetter"] = (279.4, 215.9), ["USLegal"] = (355.6, 215.9), ["USLedger"] = (431.8, 279.4) };
    static string VersionLabel(string v) { double x = SymGeo.D(v); return x >= 20250114 ? "KiCad 9" : x >= 20231120 ? "KiCad 8" : x >= 20230121 ? "KiCad 7" : x >= 20211123 ? "KiCad 6" : "KiCad 6 預覽版"; }
    static int AngDir(double a) { int r = (((int)Math.Round(a) % 360) + 360) % 360; return r == 90 ? 1 : r == 180 ? 2 : r == 270 ? 3 : 0; }
    static FillKind KFill(object node) { var f = SExpr.F(node, "fill"); var t = f != null ? SExpr.F(f, "type") : null; var v = SExpr.S(t, 1) ?? "none"; return v == "outline" ? FillKind.Fg : v is "background" or "color" ? FillKind.Bg : FillKind.None; }

    static SymGeo.Item KItem(List<object> c)
    {
        switch (c[0] as string)
        {
            case "polyline": return new SymGeo.Item { K = "poly", Pts = SExpr.Pts(c), Fill = KFill(c) };
            case "rectangle": return new SymGeo.Item { K = "poly", Pts = SymGeo.Rect(SExpr.XY(SExpr.F(c, "start")), SExpr.XY(SExpr.F(c, "end"))), Fill = KFill(c) };
            case "circle": { var ce = SExpr.XY(SExpr.F(c, "center")); return new SymGeo.Item { K = "circle", Cx = ce.X, Cy = ce.Y, R = SExpr.N(SExpr.F(c, "radius"), 1), Fill = KFill(c) }; }
            case "arc":
                {
                    var st = SExpr.F(c, "start"); var md = SExpr.F(c, "mid"); var en = SExpr.F(c, "end");
                    if (st != null && md != null && en != null) return new SymGeo.Item { K = "poly", Pts = SymGeo.Arc3(SExpr.XY(st), SExpr.XY(md), SExpr.XY(en)), Fill = KFill(c) };
                    var rd = SExpr.F(c, "radius");
                    if (rd != null && st != null && en != null) { var ce = SExpr.XY(SExpr.F(rd, "at")); double r = SExpr.N(SExpr.F(rd, "length"), 1); var s = SExpr.XY(st); var e = SExpr.XY(en); return new SymGeo.Item { K = "poly", Pts = SymGeo.ArcShort(ce.X, ce.Y, r, s.X, s.Y, e.X, e.Y), Fill = KFill(c) }; }
                    return null;
                }
            case "bezier": { var p = SExpr.Pts(c); return p.Count == 4 ? new SymGeo.Item { K = "poly", Pts = SymGeo.Bez(p), Fill = KFill(c) } : null; }
            case "text":
                {
                    var at = SExpr.F(c, "at"); var e = SExpr.Effects(c); if (at == null) return null;
                    return new SymGeo.Item { K = "text", Text = SExpr.S(c, 1) ?? "", X = SExpr.N(at, 1), Y = SExpr.N(at, 2), Ang = SExpr.N(at, 3) / 10, Size = e.size, HJ = e.hj, VJ = e.vj, Hidden = e.hidden };
                }
        }
        return null;
    }

    static SymGeo.LibPin KPin(List<object> c, int u, int cv)
    {
        var at = SExpr.F(c, "at"); var len = SExpr.F(c, "length"); var nm = SExpr.F(c, "name"); var nb = SExpr.F(c, "number");
        return new SymGeo.LibPin
        {
            Unit = u, Conv = cv, Type = SExpr.S(c, 1) ?? "passive", Inv = (SExpr.S(c, 2) ?? "").Contains("inverted"), X = SExpr.N(at, 1), Y = SExpr.N(at, 2), Ang = SExpr.N(at, 3),
            Len = len != null ? SExpr.N(len, 1, 2.54) : 2.54, Name = SExpr.S(nm, 1) ?? "", Num = SExpr.S(nb, 1) ?? "", NameSize = nm != null ? SExpr.Effects(nm).size : 1.27, NumSize = nb != null ? SExpr.Effects(nb).size : 1.27, Hidden = SExpr.Hidden(c),
        };
    }

    static KicadLegacy.LibSymbol LibSymbol(List<object> s)
    {
        var L = new KicadLegacy.LibSymbol { Name = SExpr.S(s, 1), Power = SExpr.F(s, "power") != null };
        var pn = SExpr.F(s, "pin_names"); if (pn != null) { var o = SExpr.F(pn, "offset"); if (o != null) L.NameOffset = SExpr.N(o, 1); if (SExpr.Hidden(pn)) L.ShowNames = false; }
        var pm = SExpr.F(s, "pin_numbers"); if (pm != null && SExpr.Hidden(pm)) L.ShowNums = false;
        // symbols nest only a level or two; the depth limit keeps a damaged file from overflowing the stack
        void Walk(List<object> node, int u, int cv, int depth)
        {
            for (int k = 1; k < node.Count; k++)
            {
                if (node[k] is not List<object> c || c.Count == 0) continue;
                var tag = c[0] as string;
                if (tag == "symbol")
                {
                    if (depth > 32) continue;
                    var m = Regex.Match(SExpr.S(c, 1) ?? "", @"_(\d+)_(\d+)$");
                    int uu = m.Success && int.TryParse(m.Groups[1].Value, out var u1) ? u1 : 0, cc = m.Success && int.TryParse(m.Groups[2].Value, out var c1) ? c1 : 0;
                    if (uu > L.Units) L.Units = uu;
                    Walk(c, uu, cc, depth + 1);
                }
                else if (tag == "pin") L.Pins.Add(KPin(c, u, cv));
                else { var it = KItem(c); if (it != null) { it.Unit = u; it.Conv = cv; L.Items.Add(it); } }
            }
        }
        Walk(s, 0, 0, 0);
        return L;
    }

    public static SchDocument Parse(string src, string fileName)
    {
        if (SExpr.Parse(src) is not List<object> root || root.Count == 0 || root[0] as string != "kicad_sch") throw new InvalidDataException("不是有效的 KiCad 電路圖");
        var ver = SExpr.S(SExpr.F(root, "version"), 1) ?? "";
        var sh = new SchSheet { Name = fileName };
        var pp = SExpr.F(root, "paper");
        if (pp != null)
        {
            var nm = SExpr.S(pp, 1) ?? "A4";
            var wh = nm == "User" ? (SExpr.N(pp, 2), SExpr.N(pp, 3)) : Paper.TryGetValue(nm, out var v) ? v : Paper["A4"];
            if (pp.Contains("portrait")) wh = (wh.Item2, wh.Item1);
            sh.Page = new SchPage { Name = nm, W = wh.Item1, H = wh.Item2, NoTitle = false, Frame = new PageFrame { M = 10, M2 = 12 } };
        }
        var tb = SExpr.F(root, "title_block");
        if (tb != null)
        {
            foreach (var k in new[] { "title", "date", "rev", "company" }) { var n = SExpr.F(tb, k); if (SExpr.S(n, 1) is { Length: > 0 } t) sh.Title[k] = t; }
            foreach (var c in SExpr.FA(tb, "comment")) if (SExpr.S(c, 2) is { Length: > 0 } t) sh.Title["comment" + SExpr.S(c, 1)] = t;
        }
        var libs = new Dictionary<string, KicadLegacy.LibSymbol>();
        var ls = SExpr.F(root, "lib_symbols"); if (ls != null) foreach (var s in SExpr.FA(ls, "symbol")) libs[SExpr.S(s, 1) ?? ""] = LibSymbol(s);
        var instRef = new Dictionary<string, string>();
        var si = SExpr.F(root, "symbol_instances");
        if (si != null) foreach (var p in SExpr.FA(si, "path")) { var r = SExpr.F(p, "reference"); if (r != null) instRef[(SExpr.S(p, 1) ?? "").Split('/').Last()] = SExpr.S(r, 1); }
        int missing = 0;
        P2 TLS(double x, double y) => new(x, -y);
        for (int k = 1; k < root.Count; k++)
        {
            if (root[k] is not List<object> c || c.Count == 0) continue;
            switch (c[0] as string)
            {
                case "wire": case "bus": { var p = SExpr.Pts(c); for (int q = 0; q + 1 < p.Count; q++) sh.Wires.Add(SymGeo.Seg(p[q], p[q + 1], c[0] as string == "bus" ? WireKind.Bus : WireKind.Wire)); break; }
                case "bus_entry": { var at = SExpr.XY(SExpr.F(c, "at")); var sz = SExpr.XY(SExpr.F(c, "size")); sh.Wires.Add(SymGeo.Seg(at, new P2(at.X + sz.X, at.Y + sz.Y), WireKind.Entry)); break; }
                case "junction": { var at = SExpr.XY(SExpr.F(c, "at")); sh.Junctions.Add(new SchJunction { X = at.X, Y = at.Y }); break; }
                case "no_connect": { var at = SExpr.XY(SExpr.F(c, "at")); sh.NoConnects.Add(new SchNoConnect { X = at.X, Y = at.Y }); break; }
                case "label": case "global_label": case "hierarchical_label":
                    {
                        var at = SExpr.F(c, "at"); var e = SExpr.Effects(c); var tag = c[0] as string;
                        sh.Labels.Add(new SchLabel { X = SExpr.N(at, 1), Y = SExpr.N(at, 2), Dir = AngDir(SExpr.N(at, 3)), Text = SExpr.S(c, 1) ?? "", Size = e.size, Kind = tag == "label" ? LabelKind.Local : tag == "global_label" ? LabelKind.Global : LabelKind.Hier });
                        break;
                    }
                case "text":
                    {
                        var at = SExpr.F(c, "at"); var e = SExpr.Effects(c);
                        if (at != null && !e.hidden) sh.Texts.Add(SchText.Place(SExpr.S(c, 1) ?? "", SExpr.N(at, 1), SExpr.N(at, 2), SExpr.N(at, 3), e.hj == "center" ? "left" : e.hj, e.vj == "center" ? "bottom" : e.vj, e.size, TLS, "ntxt"));
                        break;
                    }
                case "text_box":
                    {
                        var at = SExpr.F(c, "at") ?? SExpr.F(c, "start"); var sz = SExpr.F(c, "size"); var e = SExpr.Effects(c); if (at == null) break;
                        double x = SExpr.N(at, 1), y = SExpr.N(at, 2); var end = SExpr.F(c, "end");
                        double w = sz != null ? SExpr.N(sz, 1) : end != null ? SExpr.N(end, 1) - x : 0, h = sz != null ? SExpr.N(sz, 2) : end != null ? SExpr.N(end, 2) - y : 0;
                        sh.Graphics.Add(new SchPoly { Pts = SymGeo.Rect(new P2(x, y), new P2(x + w, y + h)), Role = "gr" });
                        sh.Texts.Add(new SchText { X = x + 0.8, Y = y + 0.8, Text = SExpr.S(c, 1) ?? "", Size = e.size, HA = HAnchor.Start, VA = VAnchor.Top, Role = "ntxt" });
                        break;
                    }
                case "polyline": case "rectangle": case "circle": case "arc": case "bezier":
                    {
                        var it = KItem(c); if (it == null) break;
                        var st = SExpr.F(c, "stroke"); var ty = st != null ? SExpr.F(st, "type") : null; bool dash = Regex.IsMatch(SExpr.S(ty, 1) ?? "", "dash|dot");
                        if (it.K == "poly") sh.Graphics.Add(new SchPoly { Pts = it.Pts, Dash = dash, Role = "gr" });
                        else if (it.K == "circle") sh.Graphics.Add(new SchCircle { Cx = it.Cx, Cy = it.Cy, R = it.R, LineStyle = dash ? 1 : 0, Role = "gr" });
                        break;
                    }
                case "sheet":
                    {
                        var at = SExpr.XY(SExpr.F(c, "at")); var sz = SExpr.XY(SExpr.F(c, "size"));
                        var b = new SheetBlock { X = at.X, Y = at.Y, W = sz.X, H = sz.Y };
                        foreach (var p in SExpr.FA(c, "property")) { var key = SExpr.S(p, 1) ?? ""; if (Regex.IsMatch(key, "^Sheet ?name$", RegexOptions.IgnoreCase)) b.Name = SExpr.S(p, 2) ?? ""; else if (Regex.IsMatch(key, "^Sheet ?file$", RegexOptions.IgnoreCase)) b.File = SExpr.S(p, 2) ?? ""; }
                        foreach (var p in SExpr.FA(c, "pin"))
                        {
                            var a = SExpr.F(p, "at"); double px = SExpr.N(a, 1), py = SExpr.N(a, 2); var e = SExpr.Effects(p);
                            double dl = Math.Abs(px - b.X), dr = Math.Abs(px - (b.X + b.W)), dt = Math.Abs(py - b.Y), db = Math.Abs(py - (b.Y + b.H)), mn = Math.Min(Math.Min(dl, dr), Math.Min(dt, db));
                            sh.Labels.Add(new SchLabel { X = px, Y = py, Dir = mn == dl ? 0 : mn == dr ? 2 : mn == dt ? 3 : 1, Text = SExpr.S(p, 1) ?? "", Size = e.size, Kind = LabelKind.SheetPin, Block = b });
                        }
                        sh.Blocks.Add(b); break;
                    }
                case "symbol":
                    {
                        var lid = SExpr.S(SExpr.F(c, "lib_id"), 1) ?? "?"; var lname = SExpr.S(SExpr.F(c, "lib_name"), 1);
                        var L = lname != null && libs.TryGetValue(lname, out var l1) ? l1 : libs.TryGetValue(lid, out var l2) ? l2 : null;
                        var at = SExpr.F(c, "at"); double x = SExpr.N(at, 1), y = SExpr.N(at, 2), ang = ((SExpr.N(at, 3) % 360) + 360) % 360;
                        var M = (double[])SymGeo.ROT[(int)Math.Round(ang / 90) % 4].Clone();
                        var mi = SExpr.F(c, "mirror");
                        if (mi != null) { if (SExpr.S(mi, 1) == "x") M = new[] { M[0], M[1], -M[2], -M[3] }; else if (SExpr.S(mi, 1) == "y") M = new[] { -M[0], -M[1], M[2], M[3] }; }
                        int unit = (int)SExpr.N(SExpr.F(c, "unit"), 1, 1), conv = (int)SExpr.N(SExpr.F(c, "convert") ?? SExpr.F(c, "body_style"), 1, 1);
                        var comp = new SchComponent { Lib = lid, Unit = unit, X = x, Y = y };
                        string reference = "?";
                        foreach (var p in SExpr.FA(c, "property"))
                        {
                            string key = SExpr.S(p, 1) ?? "", val = SExpr.S(p, 2) ?? ""; var a2 = SExpr.F(p, "at"); var e = SExpr.Effects(p);
                            if (key == "Reference") reference = val; else if (key == "Value") comp.Value = val; else if (key == "Footprint") comp.Footprint = val; else if (key == "Datasheet") comp.Datasheet = val;
                            comp.Fields.Add(new SchField { Key = key, Val = val, X = a2 != null ? SExpr.N(a2, 1) : x, Y = a2 != null ? SExpr.N(a2, 2) : y, Ang = a2 != null ? SExpr.N(a2, 3) : 0, HJ = e.hj, VJ = e.vj, Size = e.size, Hidden = e.hidden || SExpr.Hidden(p, 3) });
                        }
                        if (reference.Contains('?'))
                        {
                            var u = SExpr.S(SExpr.F(c, "uuid"), 1);
                            if (u != null && instRef.TryGetValue(u, out var ir)) reference = ir;
                            else { var r = SExpr.Find(SExpr.F(c, "instances"), "reference"); if (r != null) reference = SExpr.S(r, 1); }
                        }
                        comp.Ref = reference;
                        if (L != null) SymGeo.Instantiate(comp, L, unit, conv, M, x, y); else { missing++; SymGeo.Placeholder(comp, x, y, reference.StartsWith("#PWR")); comp.Missing = true; }
                        comp.Units = L?.Units ?? 1; comp.Power = L?.Power ?? reference.StartsWith("#PWR");
                        comp.InBom = SExpr.S(SExpr.F(c, "in_bom"), 1) != "no" && !reference.StartsWith('#'); comp.Dnp = SExpr.S(SExpr.F(c, "dnp"), 1) == "yes";
                        sh.Comps.Add(comp.Finish(SymGeo.TL(M)));
                        break;
                    }
            }
        }
        Connectivity.ComputeNets(sh, "kicad");
        var doc = new SchDocument { FileName = fileName, Format = "kicad", FmtLabel = VersionLabel(ver) + " · .kicad_sch", Version = ver };
        doc.Sheets.Add(sh);
        if (missing > 0) doc.Warnings.Add(missing + " 個零件在檔案內找不到符號定義，以虛線方框顯示。");
        return doc;
    }
}

public static class KicadLegacy
{
    public sealed class LibSymbol
    {
        public string Name = ""; public bool Power, ShowNames = true, ShowNums = true; public int Units = 1; public double NameOffset = 0.508;
        public List<SymGeo.Item> Items = new(); public List<SymGeo.LibPin> Pins = new(); public List<string> Aliases = new();
    }
    static readonly Dictionary<string, string> LType = new() { ["I"] = "input", ["O"] = "output", ["B"] = "bidirectional", ["T"] = "tri_state", ["P"] = "passive", ["U"] = "unspecified", ["W"] = "power_in", ["w"] = "power_out", ["C"] = "open_collector", ["E"] = "open_emitter", ["N"] = "no_connect" };

    static List<string> Tokens(string l) => Regex.Matches(l, "\"((?:[^\"\\\\]|\\\\.)*)\"|(\\S+)").Select(m => m.Groups[1].Success ? m.Groups[1].Value.Replace("\\\"", "\"") : m.Groups[2].Value).ToList();

    public static Dictionary<string, LibSymbol> ParseLib(string src)
    {
        var o = new Dictionary<string, LibSymbol>(); LibSymbol cur = null; bool draw = false;
        FillKind Fill(string v) => v == "F" ? FillKind.Fg : v == "f" ? FillKind.Bg : FillKind.None;
        foreach (var raw in src.Split('\n'))
        {
            var l = raw.Trim(); if (l == "" || l[0] == '#') continue;
            var t = Tokens(l); string T(int k) => k < t.Count ? t[k] : ""; double G(int k) => SymGeo.D(T(k)) * SymGeo.MIL; int I(int k) => (int)SymGeo.D(T(k));
            if (t[0] == "DEF") { cur = new LibSymbol { Name = T(1).TrimStart('~'), NameOffset = SymGeo.D(T(4)) * SymGeo.MIL, ShowNums = T(5) != "N", ShowNames = T(6) != "N", Units = Math.Max(1, I(7)), Power = T(9) == "P" }; draw = false; continue; }
            if (cur == null) continue;
            switch (t[0])
            {
                case "ALIAS": cur.Aliases.AddRange(t.Skip(1)); continue;
                case "DRAW": draw = true; continue;
                case "ENDDRAW": draw = false; continue;
                case "ENDDEF": o[cur.Name] = cur; foreach (var a in cur.Aliases) o[a] = cur; cur = null; continue;
            }
            if (!draw) continue;
            switch (t[0])
            {
                case "P": { int n = I(1); var pts = new List<P2>(); for (int k = 0; k < n; k++) pts.Add(new P2(G(5 + 2 * k), G(6 + 2 * k))); cur.Items.Add(new SymGeo.Item { K = "poly", Unit = I(2), Conv = I(3), Pts = pts, Fill = Fill(T(5 + 2 * n)) }); break; }
                case "S": cur.Items.Add(new SymGeo.Item { K = "poly", Unit = I(5), Conv = I(6), Pts = SymGeo.Rect(new P2(G(1), G(2)), new P2(G(3), G(4))), Fill = Fill(T(8)) }); break;
                case "C": cur.Items.Add(new SymGeo.Item { K = "circle", Unit = I(4), Conv = I(5), Cx = G(1), Cy = G(2), R = G(3), Fill = Fill(T(7)) }); break;
                case "A":
                    {
                        double a1 = SymGeo.D(T(4)) / 10 * Math.PI / 180, a2 = SymGeo.D(T(5)) / 10 * Math.PI / 180, sw = a2 - a1;
                        sw = SymGeo.WrapPi(sw);
                        cur.Items.Add(new SymGeo.Item { K = "poly", Unit = I(6), Conv = I(7), Pts = SymGeo.ArcSweep(G(1), G(2), G(3), a1, sw), Fill = Fill(T(9)) }); break;
                    }
                case "T":
                    cur.Items.Add(new SymGeo.Item { K = "text", Unit = I(6), Conv = I(7), X = G(2), Y = G(3), Ang = SymGeo.D(T(1)) / 10, Size = G(4), Text = T(8).Replace('~', ' '), Hidden = T(5) == "1", HJ = T(11) == "L" ? "left" : T(11) == "R" ? "right" : "center", VJ = T(12) == "T" ? "top" : T(12) == "B" ? "bottom" : "center" });
                    break;
                case "X":
                    {
                        var shp = T(12);
                        cur.Pins.Add(new SymGeo.LibPin
                        {
                            Unit = I(9), Conv = I(10), X = G(3), Y = G(4), Len = G(5), Ang = T(6) switch { "U" => 90, "L" => 180, "D" => 270, _ => 0 }, Name = T(1) == "~" ? "" : T(1), Num = T(2), NumSize = G(7), NameSize = G(8),
                            Type = LType.TryGetValue(T(11), out var ty) ? ty : "passive", Inv = shp.TrimStart('N').Contains('I'), Hidden = shp.StartsWith('N'),
                        });
                        break;
                    }
            }
        }
        return o;
    }

    static readonly Regex FRe = new("^F\\s+(\\d+)\\s+\"((?:[^\"\\\\]|\\\\.)*)\"\\s+([HV])\\s+(-?\\d+)\\s+(-?\\d+)\\s+(\\d+)\\s+(\\d+)\\s+([LRCB])\\s+([TBC])(\\S*)(?:\\s+\"((?:[^\"\\\\]|\\\\.)*)\")?");

    public static SchDocument Parse(string src, string fileName, IReadOnlyDictionary<string, LibSymbol> libs)
    {
        libs ??= new Dictionary<string, LibSymbol>();
        var lines = src.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
        var sh = new SchSheet { Name = fileName };
        var ver = Regex.Match(lines.FirstOrDefault() ?? "", @"Version\s+(\d+)").Groups[1].Value;
        double Mm(string v) => SymGeo.D(v) * SymGeo.MIL;
        int missing = 0, missingReal = 0;
        P2 TLS(double x, double y) => new(x, -y);
        for (int i = 0; i < lines.Length; i++)
        {
            var ln = lines[i].Trim(); if (ln == "") continue;
            if (ln.StartsWith("$Descr"))
            {
                var t = Regex.Split(ln, @"\s+"); sh.Page = new SchPage { Name = t[1], W = Mm(t.ElementAtOrDefault(2)), H = Mm(t.ElementAtOrDefault(3)), Frame = new PageFrame { M = 10, M2 = 12 } };
                while (++i < lines.Length)
                {
                    var l = lines[i].Trim(); if (l.StartsWith("$EndDescr")) break;
                    var m = Regex.Match(l, "^(\\w+)\\s+\"(.*)\"\\s*$");
                    if (m.Success)
                    {
                        var k = m.Groups[1].Value; var key = k == "Title" ? "title" : k == "Date" ? "date" : k == "Rev" ? "rev" : k == "Comp" ? "company" : Regex.IsMatch(k, @"^Comment\d$") ? "comment" + k[7..] : null;
                        if (key != null && m.Groups[2].Value != "") sh.Title[key] = m.Groups[2].Value.Replace("\\\"", "\"");
                    }
                    else if (Regex.IsMatch(l, @"^Sheet\s+\d+\s+\d+")) { var t2 = Regex.Split(l, @"\s+"); sh.Title["sheet"] = t2[1] + "/" + t2[2]; if (int.TryParse(t2[1], out var no)) sh.Number = no; }
                }
            }
            else if (ln == "$Comp")
            {
                string lname = "", reference = "?"; int unit = 1, conv = 1; double x = 0, y = 0; double[] M = { 1, 0, 0, -1 };
                var comp = new SchComponent();
                while (++i < lines.Length)
                {
                    var l = lines[i].Trim(); if (l == "$EndComp") break;
                    if (l.StartsWith("L ")) { var t = Regex.Split(l, @"\s+"); lname = t[1]; reference = t.ElementAtOrDefault(2) ?? "?"; }
                    else if (l.StartsWith("U ")) { var t = Regex.Split(l, @"\s+"); unit = Math.Max(1, (int)SymGeo.D(t[1])); conv = Math.Max(1, (int)SymGeo.D(t.ElementAtOrDefault(2))); }
                    else if (l.StartsWith("P ")) { var t = Regex.Split(l, @"\s+"); x = Mm(t[1]); y = Mm(t[2]); }
                    else if (l.StartsWith("F "))
                    {
                        var m = FRe.Match(l);
                        if (m.Success)
                        {
                            int id = int.Parse(m.Groups[1].Value); var val = m.Groups[2].Value.Replace("\\\"", "\""); if (val == "~") val = "";
                            comp.Fields.Add(new SchField
                            {
                                Key = id < 4 ? new[] { "Reference", "Value", "Footprint", "Datasheet" }[id] : m.Groups[11].Success ? m.Groups[11].Value : "Field" + id, Val = val,
                                X = Mm(m.Groups[4].Value), Y = Mm(m.Groups[5].Value), Ang = m.Groups[3].Value == "V" ? 90 : 0, Size = Mm(m.Groups[6].Value),
                                Hidden = (int.Parse(m.Groups[7].Value) & 1) == 1, HJ = m.Groups[8].Value == "L" ? "left" : m.Groups[8].Value == "R" ? "right" : "center", VJ = m.Groups[9].Value == "T" ? "top" : m.Groups[9].Value == "B" ? "bottom" : "center",
                            });
                        }
                    }
                    else if (Regex.IsMatch(l, @"^-?\d+\s+-?\d+\s+-?\d+\s+-?\d+$")) M = Regex.Split(l, @"\s+").Select(SymGeo.D).ToArray();
                }
                string Fv(string k) => comp.Fields.FirstOrDefault(f => f.Key == k)?.Val ?? "";
                if (Fv("Reference") != "") reference = Fv("Reference");
                comp.Ref = reference; comp.Value = Fv("Value"); comp.Footprint = Fv("Footprint"); comp.Datasheet = Fv("Datasheet"); comp.Lib = lname; comp.Unit = unit; comp.X = x; comp.Y = y;
                var L = libs.TryGetValue(lname, out var a) ? a : libs.TryGetValue(lname.Replace(':', '_'), out var b) ? b : libs.TryGetValue(lname.Split(':').Last(), out var c) ? c : null;
                bool pwr = reference.StartsWith("#PWR");
                if (L != null) SymGeo.Instantiate(comp, L, unit, conv, M, x, y);
                else { SymGeo.Placeholder(comp, x, y, pwr); missing++; if (!reference.StartsWith('#')) missingReal++; comp.Missing = true; }
                comp.Units = L?.Units ?? 1; comp.Power = L?.Power ?? pwr; comp.InBom = !reference.StartsWith('#');
                sh.Comps.Add(comp.Finish(SymGeo.TL(M)));
            }
            else if (Regex.IsMatch(ln, @"^Wire\s"))
            {
                var t = Regex.Split(ln, @"\s+"); var v = (i + 1 < lines.Length ? lines[++i] : "").Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(Mm).ToList();
                if (v.Count < 4) continue;
                if (t[1] == "Notes") sh.Graphics.Add(new SchPoly { Pts = { new(v[0], v[1]), new(v[2], v[3]) }, Dash = true, Role = "gr" });
                else sh.Wires.Add(SymGeo.Seg(new P2(v[0], v[1]), new P2(v[2], v[3]), t[1] == "Bus" ? WireKind.Bus : WireKind.Wire));
            }
            else if (Regex.IsMatch(ln, @"^Entry\s")) { var v = (i + 1 < lines.Length ? lines[++i] : "").Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(Mm).ToList(); if (v.Count >= 4) sh.Wires.Add(SymGeo.Seg(new P2(v[0], v[1]), new P2(v[2], v[3]), WireKind.Entry)); }
            else if (ln.StartsWith("Connection ")) { var t = Regex.Split(ln, @"\s+"); sh.Junctions.Add(new SchJunction { X = Mm(t[2]), Y = Mm(t[3]) }); }
            else if (ln.StartsWith("NoConn ")) { var t = Regex.Split(ln, @"\s+"); sh.NoConnects.Add(new SchNoConnect { X = Mm(t[2]), Y = Mm(t[3]) }); }
            else if (ln.StartsWith("Text "))
            {
                var t = Regex.Split(ln, @"\s+"); var kind = t[1]; double x = Mm(t[2]), y = Mm(t[3]); int o = ((int)SymGeo.D(t.ElementAtOrDefault(4))) & 3; double size = Mm(t.ElementAtOrDefault(5) ?? "50");
                var text = (i + 1 < lines.Length ? lines[++i] : "").Replace("\\n", "\n");
                if (kind == "Notes") sh.Texts.Add(SchText.Place(text, x, y, o * 90, "left", "bottom", size, TLS, "ntxt"));
                else if (kind is "Label" or "GLabel" or "HLabel")
                {
                    bool g = kind != "Label";
                    sh.Labels.Add(new SchLabel { X = x, Y = y, Dir = g ? new[] { 2, 1, 0, 3 }[o] : o, Text = text, Size = size, Kind = kind == "Label" ? LabelKind.Local : kind == "GLabel" ? LabelKind.Global : LabelKind.Hier });
                }
            }
            else if (ln == "$Sheet")
            {
                var b = new SheetBlock();
                while (++i < lines.Length)
                {
                    var l = lines[i].Trim(); if (l == "$EndSheet") break; Match m;
                    if ((m = Regex.Match(l, @"^S\s+(-?\d+)\s+(-?\d+)\s+(-?\d+)\s+(-?\d+)")).Success) { b.X = Mm(m.Groups[1].Value); b.Y = Mm(m.Groups[2].Value); b.W = Mm(m.Groups[3].Value); b.H = Mm(m.Groups[4].Value); }
                    else if ((m = Regex.Match(l, "^F0\\s+\"(.*?)\"")).Success) b.Name = m.Groups[1].Value;
                    else if ((m = Regex.Match(l, "^F1\\s+\"(.*?)\"")).Success) b.File = m.Groups[1].Value;
                    else if ((m = Regex.Match(l, "^F\\d+\\s+\"(.*?)\"\\s+([IOBTU])\\s+([LRTB])\\s+(-?\\d+)\\s+(-?\\d+)\\s+(\\d+)")).Success)
                        sh.Labels.Add(new SchLabel { X = Mm(m.Groups[4].Value), Y = Mm(m.Groups[5].Value), Dir = m.Groups[3].Value switch { "L" => 0, "R" => 2, "T" => 3, _ => 1 }, Text = m.Groups[1].Value, Size = Mm(m.Groups[6].Value), Kind = LabelKind.SheetPin, Block = b });
                }
                sh.Blocks.Add(b);
            }
            else if (ln.StartsWith("$Bitmap")) { while (++i < lines.Length && !lines[i].Trim().StartsWith("$EndBitmap")) { } }
        }
        Connectivity.ComputeNets(sh, "kicad");
        var doc = new SchDocument { FileName = fileName, Format = "kicad-legacy", FmtLabel = "KiCad 舊版 · EESchema v" + ver, Version = ver };
        doc.Sheets.Add(sh);
        if (missingReal > 0) doc.Warnings.Add("舊版 KiCad 電路圖不含符號圖形。把專案的 *-cache.lib 一起開啟，就能顯示完整符號、接腳與網路節點。");
        else if (missing > 0) doc.Warnings.Add("部分電源符號沒有符號庫，以小圓點表示。");
        return doc;
    }
}

public static class EagleSch
{
    static (bool mirror, double ang) ERot(string s) { var m = Regex.Match(s ?? "R0", @"^(S?)(M?)R(-?[\d.]+)$"); return m.Success ? (m.Groups[2].Value == "M", SymGeo.D(m.Groups[3].Value)) : (false, 0); }
    static (string hj, string vj) EAlign(string a) { a ??= "bottom-left"; return (a.EndsWith("left") ? "left" : a.EndsWith("right") ? "right" : "center", a.StartsWith("bottom") ? "bottom" : a.StartsWith("top") ? "top" : "center"); }
    static List<P2> EArc(double x1, double y1, double x2, double y2, double curve)
    {
        if (curve == 0) return new() { new(x1, y1), new(x2, y2) };
        double c = curve * Math.PI / 180, mx = (x1 + x2) / 2, my = (y1 + y2) / 2, dx = x2 - x1, dy = y2 - y1, L = Math.Sqrt(dx * dx + dy * dy);
        if (L < 1e-9) return new() { new(x1, y1) };
        double d = (L / 2) / Math.Tan(c / 2), cx = mx - dy / L * d, cy = my + dx / L * d;
        return SymGeo.ArcSweep(cx, cy, Math.Sqrt((x1 - cx) * (x1 - cx) + (y1 - cy) * (y1 - cy)), Math.Atan2(y1 - cy, x1 - cx), c);
    }
    static string A(XElement e, string k, string d = null) { var v = (string)e.Attribute(k); return string.IsNullOrEmpty(v) ? d : v; }
    static double N(XElement e, string k, double d = 0) { var v = (string)e.Attribute(k); return string.IsNullOrEmpty(v) ? d : SymGeo.D(v); }
    static IEnumerable<XElement> K(XElement e, string tag = null) => e == null ? Enumerable.Empty<XElement>() : tag == null ? e.Elements() : e.Elements(tag);
    static XElement K1(XElement e, string tag) => K(e, tag).FirstOrDefault();

    sealed class Dev { public string Pkg = ""; public Dictionary<string, string> Conn = new(); public Dictionary<string, string> Attrs = new(); }
    sealed class DSet { public Dictionary<string, string> Gates = new(); public List<string> GateOrder = new(); public Dictionary<string, Dev> Devices = new(); }
    sealed class Lib { public Dictionary<string, XElement> Symbols = new(); public Dictionary<string, DSet> Sets = new(); }
    sealed class Part { public string Name, Value, LibName, DeviceSet, Device; public Lib L; public DSet D; public Dev Dv; public Dictionary<string, string> Attrs = new(); public bool Virtual; }

    public static SchDocument Parse(string src, string fileName)
    {
        XDocument x;
        // the DOCTYPE of an Eagle file is skipped, never processed: no entity expansion, nothing fetched from outside
        var xs = new System.Xml.XmlReaderSettings { DtdProcessing = System.Xml.DtdProcessing.Ignore, XmlResolver = null, IgnoreWhitespace = true, MaxCharactersFromEntities = 1_000_000 };
        try { using var xr = System.Xml.XmlReader.Create(new StringReader(src), xs); x = XDocument.Load(xr); }
        catch (Exception e) { throw new InvalidDataException("XML 格式錯誤，無法解析：" + e.Message); }
        var eg = x.Root; var ver = A(eg, "version", "");
        var schem = eg.Descendants("schematic").FirstOrDefault() ?? throw new InvalidDataException("這個 Eagle 檔沒有電路圖（可能是 .brd 電路板或 .lbr 元件庫）");
        var libs = new Dictionary<string, Lib>();
        foreach (var lib in K(K1(schem, "libraries"), "library"))
        {
            var L = new Lib();
            foreach (var s in K(K1(lib, "symbols"), "symbol")) L.Symbols[A(s, "name", "")] = s;
            foreach (var ds in K(K1(lib, "devicesets"), "deviceset"))
            {
                var D = new DSet();
                foreach (var g in K(K1(ds, "gates"), "gate")) { D.Gates[A(g, "name", "")] = A(g, "symbol", ""); D.GateOrder.Add(A(g, "name", "")); }
                foreach (var dv in K(K1(ds, "devices"), "device"))
                {
                    var dev = new Dev { Pkg = A(dv, "package", "") };
                    foreach (var c in K(K1(dv, "connects"), "connect")) dev.Conn[A(c, "gate", "") + "|" + A(c, "pin", "")] = A(c, "pad", "");
                    foreach (var t in K(K1(dv, "technologies"), "technology")) foreach (var a in K(t, "attribute")) dev.Attrs.TryAdd(A(a, "name", ""), A(a, "value", ""));
                    D.Devices[A(dv, "name", "")] = dev;
                }
                L.Sets[A(ds, "name", "")] = D;
            }
            var nm = A(lib, "name", ""); var urn = A(lib, "urn", "");
            libs[nm + "|" + urn] = L; libs.TryAdd(nm + "|", L);
        }
        var parts = new Dictionary<string, Part>();
        foreach (var p in K(K1(schem, "parts"), "part"))
        {
            var L = libs.GetValueOrDefault(A(p, "library", "") + "|" + A(p, "library_urn", "")) ?? libs.GetValueOrDefault(A(p, "library", "") + "|");
            var D = L?.Sets.GetValueOrDefault(A(p, "deviceset", "")); var dev = D?.Devices.GetValueOrDefault(A(p, "device", ""));
            var pt = new Part { Name = A(p, "name", ""), L = L, D = D, Dv = dev, LibName = A(p, "library", ""), DeviceSet = A(p, "deviceset", ""), Device = A(p, "device", "") };
            if (dev != null) foreach (var kv in dev.Attrs) pt.Attrs[kv.Key] = kv.Value;
            foreach (var a in K(p, "attribute")) pt.Attrs[A(a, "name", "")] = A(a, "value", "");
            pt.Value = A(p, "value") ?? (pt.DeviceSet + pt.Device);
            pt.Virtual = dev == null || dev.Pkg == "";
            parts[pt.Name] = pt;
        }
        var PL = new Dictionary<string, double> { ["point"] = 0, ["short"] = 2.54, ["middle"] = 5.08, ["long"] = 7.62 };
        P2 TA(double a, double b) => new(a, -b);
        var doc = new SchDocument { FileName = fileName, Format = "eagle", FmtLabel = "Eagle " + ver + " · XML", Version = ver };
        int si = 0;
        foreach (var S in K(K1(schem, "sheets"), "sheet"))
        {
            si++;
            var sh = new SchSheet { Name = "第 " + si + " 頁" };
            var desc = K1(S, "description"); if (desc != null && desc.Value.Trim() != "") sh.Name += " · " + desc.Value.Trim()[..Math.Min(40, desc.Value.Trim().Length)];
            SchText TextOf(XElement el, Func<double, double, P2> T, Func<double, double, P2> TL, string txt, string role)
            {
                var r = ERot(A(el, "rot", "R0")); double a = r.ang * Math.PI / 180; var d = new P2(Math.Cos(a), Math.Sin(a)); var pp = new P2(-Math.Sin(a), Math.Cos(a)); if (r.mirror) d = new P2(-d.X, -d.Y);
                var q = T(N(el, "x"), N(el, "y")); var al = EAlign(A(el, "align", "bottom-left"));
                var dd = TL(d.X, d.Y); var dp = TL(pp.X, pp.Y);
                // place with explicit direction vectors (port of placeTextDP)
                double ang = Math.Atan2(-dd.Y, dd.X) * 180 / Math.PI;
                return PlaceDP(txt, q.X, q.Y, dd, dp, al.hj, al.vj, N(el, "size", 1.778) * 0.85, role);
            }
            foreach (var el in K(K1(S, "plain")))
            {
                switch (el.Name.LocalName)
                {
                    case "wire": sh.Graphics.Add(new SchPoly { Pts = EArc(N(el, "x1"), N(el, "y1"), N(el, "x2"), N(el, "y2"), N(el, "curve")).Select(q => TA(q.X, q.Y)).ToList(), Dash = Regex.IsMatch(A(el, "style", ""), "dash|dot"), Role = "gr" }); break;
                    case "text": sh.Texts.Add(TextOf(el, TA, TA, el.Value, "ntxt")); break;
                    case "circle": sh.Graphics.Add(new SchCircle { Cx = N(el, "x"), Cy = -N(el, "y"), R = N(el, "radius"), Role = "gr" }); break;
                    case "rectangle": case "frame": sh.Graphics.Add(new SchPoly { Pts = SymGeo.Rect(new P2(N(el, "x1"), -N(el, "y1")), new P2(N(el, "x2"), -N(el, "y2"))), Role = "gr" }); break;
                    case "polygon": { var pts = K(el, "vertex").Select(v => new P2(N(v, "x"), -N(v, "y"))).ToList(); if (pts.Count > 0) { pts.Add(pts[0]); sh.Graphics.Add(new SchPoly { Pts = pts, Role = "gr" }); } break; }
                }
            }
            var compByGate = new Dictionary<string, SchComponent>();
            var rawPinName = new Dictionary<SchPin, string>();
            foreach (var ins in K(K1(S, "instances"), "instance"))
            {
                if (!parts.TryGetValue(A(ins, "part", ""), out var part)) continue;
                var gate = A(ins, "gate", ""); var symName = part.D?.Gates.GetValueOrDefault(gate); var sym = symName != null ? part.L?.Symbols.GetValueOrDefault(symName) : null;
                double ix = N(ins, "x"), iy = N(ins, "y"); var r = ERot(A(ins, "rot", "R0")); double ca = Math.Cos(r.ang * Math.PI / 180), sa = Math.Sin(r.ang * Math.PI / 180);
                P2 U(double a, double b) { if (r.mirror) a = -a; return new P2(a * ca - b * sa, a * sa + b * ca); }
                P2 T(double a, double b) { var q = U(a, b); return new P2(ix + q.X, -(iy + q.Y)); }
                P2 TL(double a, double b) { var q = U(a, b); return new P2(q.X, -q.Y); }
                bool multi = part.D != null && part.D.GateOrder.Count > 1; var disp = part.Name + (multi ? gate : "");
                bool smashed = A(ins, "smashed", "") == "yes";
                string Subst(string s) => Regex.Replace(s, ">([A-Z_0-9]+)", m => m.Groups[1].Value switch { "NAME" => disp, "VALUE" => part.Value, "PART" => part.Name, "GATE" => gate, var k => part.Attrs.GetValueOrDefault(k, "") });
                var comp = new SchComponent { Ref = part.Name, Value = part.Value, Footprint = part.Dv?.Pkg ?? "", Lib = part.LibName + ":" + part.DeviceSet + (part.Device != "" ? "/" + part.Device : ""), Virtual = part.Virtual, InBom = !part.Virtual, X = ix, Y = -iy, Missing = sym == null, NameOffset = 0.762 };
                var ftexts = new List<SchText>();
                if (sym != null)
                    foreach (var el in K(sym))
                    {
                        switch (el.Name.LocalName)
                        {
                            case "wire": comp.Prims.Add(new SchPoly { Pts = EArc(N(el, "x1"), N(el, "y1"), N(el, "x2"), N(el, "y2"), N(el, "curve")).Select(q => T(q.X, q.Y)).ToList() }); break;
                            case "circle": { var c = T(N(el, "x"), N(el, "y")); comp.Prims.Add(new SchCircle { Cx = c.X, Cy = c.Y, R = N(el, "radius"), Fill = N(el, "width") == 0 ? FillKind.Fg : FillKind.None }); break; }
                            case "rectangle": comp.Prims.Add(new SchPoly { Pts = SymGeo.Rect(new P2(N(el, "x1"), N(el, "y1")), new P2(N(el, "x2"), N(el, "y2"))).Select(q => T(q.X, q.Y)).ToList(), Fill = FillKind.Fg }); break;
                            case "polygon": { var pts = K(el, "vertex").Select(v => T(N(v, "x"), N(v, "y"))).ToList(); if (pts.Count > 0) { pts.Add(pts[0]); comp.Prims.Add(new SchPoly { Pts = pts, Fill = FillKind.Fg }); } break; }
                            case "text":
                                {
                                    var raw = el.Value; if (smashed && Regex.IsMatch(raw.Trim(), "^>(NAME|VALUE)$")) break; var s = Subst(raw); if (s == "") break;
                                    ftexts.Add(TextOf(el, T, TL, s, raw.StartsWith(">NAME") ? "ref" : raw.StartsWith(">VALUE") ? "val" : "stxt")); break;
                                }
                            case "pin":
                                {
                                    var pname = A(el, "name", ""); double len = PL.GetValueOrDefault(A(el, "length", "long"), 7.62); var pr = ERot(A(el, "rot", "R0")); double a = pr.ang * Math.PI / 180;
                                    int ux = (int)Math.Round(Math.Cos(a)), uy = (int)Math.Round(Math.Sin(a));
                                    double px = N(el, "x"), py = N(el, "y"); var c = T(px, py); var e = T(px + ux * len, py + uy * len); var d = TL(ux, uy); var vis = A(el, "visible", "both");
                                    var pad = part.Dv?.Conn.GetValueOrDefault(gate + "|" + pname);
                                    var pin = new SchPin
                                    {
                                        X = c.X, Y = c.Y, Ex = e.X, Ey = e.Y, Dx = d.X, Dy = d.Y, Num = pad ?? "", Name = Regex.Replace(pname, "@.*$", ""), Type = A(el, "direction", "io"),
                                        Inv = A(el, "function", "").Contains("dot"), ShowName = vis is "both" or "pin", ShowNum = (vis is "both" or "pad") && !string.IsNullOrEmpty(pad), NameSize = 1.4, NumSize = 1.15,
                                    };
                                    comp.Pins.Add(pin); rawPinName[pin] = pname; break;
                                }
                        }
                    }
                if (smashed)
                    foreach (var a in K(ins, "attribute"))
                    {
                        if (A(a, "display", "") == "off") continue; var k = A(a, "name", "");
                        var s = k == "NAME" ? disp : k == "VALUE" ? part.Value : part.Attrs.GetValueOrDefault(k, ""); if (s == "") continue;
                        ftexts.Add(TextOf(a, TA, TA, s, k == "NAME" ? "ref" : k == "VALUE" ? "val" : "fld"));
                    }
                foreach (var kv in part.Attrs) comp.Fields.Add(new SchField { Key = kv.Key, Val = kv.Value, Hidden = true });
                if (sym == null) SymGeo.Placeholder(comp, ix, -iy, false);
                comp.Finish(SymGeo.TL(new[] { r.mirror ? -ca : ca, -sa, r.mirror ? sa : -sa, -ca }));
                comp.FTexts.AddRange(ftexts);
                sh.Comps.Add(comp); compByGate[part.Name + "|" + gate] = comp;
            }
            foreach (var b in K(K1(S, "busses"), "bus")) foreach (var sg in K(b, "segment")) foreach (var w in K(sg, "wire"))
                    {
                        var p = EArc(N(w, "x1"), N(w, "y1"), N(w, "x2"), N(w, "y2"), N(w, "curve")).Select(q => TA(q.X, q.Y)).ToList();
                        for (int k = 0; k + 1 < p.Count; k++) sh.Wires.Add(SymGeo.Seg(p[k], p[k + 1], WireKind.Bus));
                    }
            var netMap = new Dictionary<string, SheetNet>();
            foreach (var n in K(K1(S, "nets"), "net"))
            {
                var nm = A(n, "name", "");
                if (!netMap.TryGetValue(nm, out var net)) netMap[nm] = net = new SheetNet { Name = nm, Auto = Regex.IsMatch(nm, @"^N\$\d+$"), Ids = { new NetId { K = "global", N = nm } } };
                foreach (var sg in K(n, "segment"))
                    foreach (var el in K(sg))
                    {
                        switch (el.Name.LocalName)
                        {
                            case "wire": { var p = EArc(N(el, "x1"), N(el, "y1"), N(el, "x2"), N(el, "y2"), N(el, "curve")).Select(q => TA(q.X, q.Y)).ToList(); for (int k = 0; k + 1 < p.Count; k++) { var w = SymGeo.Seg(p[k], p[k + 1], WireKind.Wire); w.Net = nm; sh.Wires.Add(w); } break; }
                            case "junction": sh.Junctions.Add(new SchJunction { X = N(el, "x"), Y = -N(el, "y"), Net = nm }); break;
                            case "label":
                                {
                                    var r = ERot(A(el, "rot", "R0")); int dir = ((((int)Math.Round(r.ang) % 360) + 360) % 360) switch { 90 => 1, 180 => 2, 270 => 3, _ => 0 }; if (r.mirror) dir = new[] { 2, 1, 0, 3 }[dir];
                                    sh.Labels.Add(new SchLabel { X = N(el, "x"), Y = -N(el, "y"), Dir = dir, Text = nm, Size = N(el, "size", 1.778) * 0.85, Kind = A(el, "xref", "") == "yes" ? LabelKind.Global : LabelKind.Local, Net = nm }); break;
                                }
                            case "pinref":
                                {
                                    if (!compByGate.TryGetValue(A(el, "part", "") + "|" + A(el, "gate", ""), out var c)) break;
                                    var pin = c.Pins.FirstOrDefault(q => rawPinName.GetValueOrDefault(q) == A(el, "pin", "")); if (pin != null) pin.Net = nm;
                                    if (!c.Virtual) net.Members.Add(new NetMember { Ref = c.Ref, Pin = pin != null && pin.Num != "" ? pin.Num : A(el, "pin", ""), PinName = pin?.Name ?? A(el, "pin", "") });
                                    break;
                                }
                        }
                    }
            }
            sh.Nets = netMap.Values.ToList();
            foreach (var c in sh.Comps) { if (c.Virtual) continue; foreach (var p in c.Pins) if (p.Net == null && p.Type != "nc" && p.Type != "sup") sh.UnconnectedPins.Add(new Unconnected { Ref = c.Ref, Pin = p.Num != "" ? p.Num : p.Name, Name = p.Name }); }
            doc.Sheets.Add(sh);
        }
        if (doc.Sheets.Count == 0) throw new InvalidDataException("這個 Eagle 檔沒有任何頁面");
        if (K(K1(schem, "modules")).Any()) doc.Warnings.Add("檔案含有 Eagle 階層模組（modules），目前只顯示主頁面。");
        return doc;
    }

    static SchText PlaceDP(string text, double x, double y, P2 d, P2 p, string hj, string vj, double size, string role)
    {
        bool rot, fh, fv;
        if (Math.Abs(d.X) >= Math.Abs(d.Y)) { rot = false; fh = d.X < 0; fv = p.Y > 0; } else { rot = true; fh = d.Y > 0; fv = p.X > 0; }
        var ha = hj == "left" ? (fh ? HAnchor.End : HAnchor.Start) : hj == "right" ? (fh ? HAnchor.Start : HAnchor.End) : HAnchor.Middle;
        var va = vj == "top" ? (fv ? VAnchor.Bottom : VAnchor.Top) : vj == "bottom" ? (fv ? VAnchor.Top : VAnchor.Bottom) : VAnchor.Center;
        return new SchText { Text = text ?? "", X = x, Y = y, Size = size, Rot90 = rot, HA = ha, VA = va, Role = role };
    }
}
