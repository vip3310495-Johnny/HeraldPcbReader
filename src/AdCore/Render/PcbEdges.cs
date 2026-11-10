// Herald PCB Reader — Copyright (C) 2026 Johnny.ch.Tsai
// SPDX-License-Identifier: GPL-3.0-only
// Free software under the GNU General Public License version 3 (LICENSE.txt), WITHOUT ANY WARRANTY; with an
// additional permission to combine it with AvalonDock (Ms-PL), see NOTICE.txt.
using System.Runtime.CompilerServices;
using AdCore.Pcb;
using SkiaSharp;

namespace AdCore.Render;

/// <summary>Snapping for the measure tool (port of v1): centres / end points, copper edges (Ctrl) and the edge
/// crossed by a 0° / 45° / 90° locked line (Ctrl+Shift).</summary>
public static class PcbEdges
{
    enum K { Poly, Circ, Chain }
    sealed class Prim { public K Kind; public (double x, double y)[] Pts; public bool Closed; public (double x, double y) C; public double R; }
    static readonly ConditionalWeakTable<PcbObj, Prim[]> Cache = new();
    const double D2R = Math.PI / 180;

    static (double, double) Rot(double x, double y, double deg) { double c = Math.Cos(deg * D2R), s = Math.Sin(deg * D2R); return (x * c - y * s, x * s + y * c); }
    static (double, double)[] Xf(IEnumerable<(double x, double y)> pts, double x, double y, double rot) => pts.Select(p => { var (a, b) = Rot(p.x, p.y, rot); return (a + x, b + y); }).ToArray();

    /// <summary>Region outline with its arc vertices sampled (as the 2D path draws them).</summary>
    public static List<(double x, double y)> VertsPts(IEnumerable<RegionVertex> verts)
    {
        var o = new List<(double x, double y)>();
        foreach (var v in verts)
        {
            if (v.Round && v.R > 0)
            {
                var s = (v.Cx + Math.Cos(v.Sa * D2R) * v.R, v.Cy + Math.Sin(v.Sa * D2R) * v.R);
                var e = (v.Cx + Math.Cos(v.Ea * D2R) * v.R, v.Cy + Math.Sin(v.Ea * D2R) * v.R);
                double a0, a1;
                if (Dist(s, (v.X, v.Y)) < Dist(e, (v.X, v.Y))) { a0 = v.Sa * D2R; a1 = v.Ea * D2R; if (a1 <= a0) a1 += Math.PI * 2; }
                else { a0 = v.Ea * D2R; a1 = v.Sa * D2R; if (a1 >= a0) a1 -= Math.PI * 2; }
                int n = Math.Max(3, (int)Math.Ceiling(Math.Abs(a1 - a0) / (Math.PI / 24)));
                for (int i = 0; i <= n; i++) { double a = a0 + (a1 - a0) * i / n; o.Add((v.Cx + Math.Cos(a) * v.R, v.Cy + Math.Sin(a) * v.R)); }
            }
            else o.Add((v.X, v.Y));
        }
        return o;
    }

    static double Dist((double x, double y) a, (double x, double y) b) => Math.Sqrt((a.x - b.x) * (a.x - b.x) + (a.y - b.y) * (a.y - b.y));

    static Prim[] Geom(PcbObj o)
    {
        if (Cache.TryGetValue(o, out var g)) return g;
        g = null;
        switch (o.Src)
        {
            case PcbPad t when o.Kind == PcbObjKind.Pad:
                {
                    var sz = t.L == Layers.Bottom && t.Mode != 0 ? t.Bot : t.Top; double w = sz.Sx, h = sz.Sy;
                    (double, double)[] Rr(double r)
                    {
                        var pts = new List<(double x, double y)>();
                        foreach (var (cx, cy, a0) in new[] { (w / 2 - r, h / 2 - r, 0.0), (-w / 2 + r, h / 2 - r, 90.0), (-w / 2 + r, -h / 2 + r, 180.0), (w / 2 - r, -h / 2 + r, 270.0) })
                            for (int i = 0; i <= 6; i++) { double a = (a0 + i * 15) * D2R; pts.Add((cx + Math.Cos(a) * r, cy + Math.Sin(a) * r)); }
                        return Xf(pts, t.X, t.Y, t.Rot);
                    }
                    if (sz.Shape == 2) g = new[] { new Prim { Kind = K.Poly, Closed = true, Pts = sz.Alt == 9 ? Rr(Math.Min(w, h) / 2 * sz.Cr / 100) : Xf(new[] { (w / 2, h / 2), (-w / 2, h / 2), (-w / 2, -h / 2), (w / 2, -h / 2) }, t.X, t.Y, t.Rot) } };
                    else if (sz.Shape == 3)
                    {
                        double c = Math.Min(w, h) * .25;
                        g = new[] { new Prim { Kind = K.Poly, Closed = true, Pts = Xf(new[] { (w / 2 - c, h / 2), (-w / 2 + c, h / 2), (-w / 2, h / 2 - c), (-w / 2, -h / 2 + c), (-w / 2 + c, -h / 2), (w / 2 - c, -h / 2), (w / 2, -h / 2 + c), (w / 2, h / 2 - c) }, t.X, t.Y, t.Rot) } };
                    }
                    else if (sz.Alt == 9 && sz.Cr < 100) g = new[] { new Prim { Kind = K.Poly, Closed = true, Pts = Rr(Math.Min(w, h) / 2 * sz.Cr / 100) } };
                    else if (Math.Abs(w - h) < 1e-6) g = new[] { new Prim { Kind = K.Circ, C = (t.X, t.Y), R = w / 2 } };
                    else
                    {
                        double L = Math.Abs(w - h) / 2, r = Math.Min(w, h) / 2;
                        g = new[] { new Prim { Kind = K.Chain, R = r, Pts = Xf(w > h ? new[] { (-L, 0.0), (L, 0.0) } : new[] { (0.0, -L), (0.0, L) }, t.X, t.Y, t.Rot) } };
                    }
                    break;
                }
            case PcbVia v: g = new[] { new Prim { Kind = K.Circ, C = (v.X, v.Y), R = v.D / 2 } }; break;
            case PcbTrack t: g = new[] { new Prim { Kind = K.Chain, R = t.W / 2, Pts = new[] { (t.X1, t.Y1), (t.X2, t.Y2) } } }; break;
            case PcbArc t:
                {
                    double a0 = t.Sa * D2R, a1 = t.Ea * D2R; if (a1 <= a0) a1 += Math.PI * 2;
                    int n = Math.Max(4, (int)Math.Ceiling((a1 - a0) / (Math.PI / 36)));
                    var pts = new (double, double)[n + 1];
                    for (int i = 0; i <= n; i++) { double a = a0 + (a1 - a0) * i / n; pts[i] = (t.Cx + Math.Cos(a) * t.R, t.Cy + Math.Sin(a) * t.R); }
                    g = new[] { new Prim { Kind = K.Chain, R = t.W / 2, Pts = pts } };
                    break;
                }
            case PcbFill f:
                {
                    double cx = (f.X1 + f.X2) / 2, cy = (f.Y1 + f.Y2) / 2, w = Math.Abs(f.X2 - f.X1), h = Math.Abs(f.Y2 - f.Y1);
                    g = new[] { new Prim { Kind = K.Poly, Closed = true, Pts = Xf(new[] { (w / 2, h / 2), (-w / 2, h / 2), (-w / 2, -h / 2), (w / 2, -h / 2) }, cx, cy, f.Rot) } };
                    break;
                }
            case PcbRegion r when o.Mode != PaintMode.Outline:
                g = new[] { new Prim { Kind = K.Poly, Closed = true, Pts = VertsPts(r.Outline).ToArray() } }
                    .Concat(r.Holes.Where(h => h.Count >= 3).Select(h => new Prim { Kind = K.Poly, Closed = true, Pts = h.Select(q => (q.X, q.Y)).ToArray() })).ToArray();
                break;
        }
        g ??= Array.Empty<Prim>();
        Cache.AddOrUpdate(o, g);
        return g;
    }

    static (double, double) NearSeg((double x, double y) p, (double x, double y) a, (double x, double y) b)
    {
        double dx = b.x - a.x, dy = b.y - a.y, L = dx * dx + dy * dy; double u = L > 0 ? ((p.x - a.x) * dx + (p.y - a.y) * dy) / L : 0; u = Math.Clamp(u, 0, 1);
        return (a.x + u * dx, a.y + u * dy);
    }
    static ((double x, double y) q, double d) NearChain((double x, double y) p, (double x, double y)[] pts)
    {
        var best = pts[0]; double bd = Dist(p, best);
        for (int i = 1; i < pts.Length; i++) { var q = NearSeg(p, pts[i - 1], pts[i]); double d = Dist(q, p); if (d < bd) { bd = d; best = q; } }
        return (best, bd);
    }

    /// <summary>Nearest point of a primitive's boundary; for outlines only edges that can come within limit are
    /// worked out (a pour can have tens of thousands of edges and this runs on every mouse move).</summary>
    static (double x, double y)? Nearest(Prim g, (double x, double y) p, double limit = double.MaxValue)
    {
        if (g.Kind == K.Circ) { double dx = p.x - g.C.x, dy = p.y - g.C.y, d = Math.Sqrt(dx * dx + dy * dy); if (d < 1e-9) d = 1e-9; return (g.C.x + dx / d * g.R, g.C.y + dy / d * g.R); }
        if (g.Kind == K.Chain)
        {
            var (q, d) = NearChain(p, g.Pts); if (d < 1e-9) return null;
            return (q.x + (p.x - q.x) / d * g.R, q.y + (p.y - q.y) / d * g.R);
        }
        (double, double)? best = null; double bd = limit; int n = g.Pts.Length;
        for (int i = 0; i < n; i++)
        {
            if (!g.Closed && i == n - 1) break;
            var a = g.Pts[i]; var b = g.Pts[(i + 1) % n];
            if (a.x - p.x > bd && b.x - p.x > bd || p.x - a.x > bd && p.x - b.x > bd || a.y - p.y > bd && b.y - p.y > bd || p.y - a.y > bd && p.y - b.y > bd) continue;
            var q = NearSeg(p, a, b); double d = Dist(q, p); if (d < bd) { bd = d; best = q; }
        }
        return best;
    }

    /// <summary>Intersections of the line A + t·u with a primitive's boundary.</summary>
    static List<double> Ray(Prim g, (double x, double y) A, (double x, double y) u)
    {
        var o = new List<double>();
        void Seg((double x, double y) a, (double x, double y) b, List<double> outp)
        {
            double ex = b.x - a.x, ey = b.y - a.y, den = u.x * ey - u.y * ex; if (Math.Abs(den) < 1e-12) return;
            double wx = a.x - A.x, wy = a.y - A.y, t = (wx * ey - wy * ex) / den, s2 = (wx * u.y - wy * u.x) / den;
            if (s2 >= -1e-9 && s2 <= 1 + 1e-9) outp.Add(t);
        }
        void Circ((double x, double y) c, double r, List<double> outp)
        {
            double fx = A.x - c.x, fy = A.y - c.y, bq = fx * u.x + fy * u.y, cq = fx * fx + fy * fy - r * r, D = bq * bq - cq;
            if (D < 0) return; double sq = Math.Sqrt(D); outp.Add(-bq - sq); outp.Add(-bq + sq);
        }
        if (g.Kind == K.Circ) Circ(g.C, g.R, o);
        else if (g.Kind == K.Poly) { int n = g.Pts.Length; for (int i = 0; i < n; i++) { if (!g.Closed && i == n - 1) break; Seg(g.Pts[i], g.Pts[(i + 1) % n], o); } }
        else
        {
            // capsule chain: offset edges + vertex circles, keep the points that lie on the outer boundary
            var cand = new List<double>(); var P = g.Pts;
            for (int i = 1; i < P.Length; i++)
            {
                var a = P[i - 1]; var b = P[i]; double dx = b.x - a.x, dy = b.y - a.y, L = Math.Sqrt(dx * dx + dy * dy); if (L < 1e-12) L = 1;
                double nx = -dy / L * g.R, ny = dx / L * g.R;
                Seg((a.x + nx, a.y + ny), (b.x + nx, b.y + ny), cand); Seg((a.x - nx, a.y - ny), (b.x - nx, b.y - ny), cand);
            }
            foreach (var c in P) Circ(c, g.R, cand);
            foreach (var t in cand) { var q = (A.x + u.x * t, A.y + u.y * t); if (Math.Abs(NearChain(q, P).d - g.R) <= Math.Max(1e-4, g.R * 1e-3)) o.Add(t); }
        }
        return o;
    }

    static bool EdgeKind(PcbObj o) => o.Kind is PcbObjKind.Pad or PcbObjKind.Via or PcbObjKind.Track or PcbObjKind.Arc or PcbObjKind.Fill or PcbObjKind.Region;

    static IEnumerable<(PcbObj o, Prim[] g)> EdgeCands(PcbScene sc, PcbViewState st, double x, double y, double R)
    {
        foreach (var (k, list) in sc.ByLayer)
        {
            if (!st.IsVisible(k) || !(Layers.IsCopper(k) || Layers.IsPlane(k) || k == Layers.MultiLayer)) continue;
            foreach (var o in list)
            {
                if (!EdgeKind(o)) continue; var b = o.BB;
                if (x < b.Left - R || x > b.Right + R || y < b.Top - R || y > b.Bottom + R) continue;
                var g = Geom(o); if (g.Length > 0) yield return (o, g);
            }
        }
    }

    public static (SKPoint p, PcbObj o)? NearestEdge(PcbScene sc, PcbViewState st, double x, double y, double R)
    {
        (SKPoint, PcbObj)? best = null; double bd = R; var p = (x, y);
        foreach (var (o, gs) in EdgeCands(sc, st, x, y, R))
            foreach (var g in gs)
            {
                if (Nearest(g, p, bd) is not { } q) continue; double d = Dist(q, p);
                if (d < bd) { bd = d; best = (new SKPoint((float)q.x, (float)q.y), o); }
            }
        return best;
    }

    /// <summary>Q = the locked cursor point on the ray from A; the copper edge that ray crosses nearest to Q.</summary>
    public static (SKPoint p, PcbObj o)? EdgeOnRay(PcbScene sc, PcbViewState st, SKPoint A, SKPoint Q, double R)
    {
        double dx = Q.X - A.X, dy = Q.Y - A.Y, L = Math.Sqrt(dx * dx + dy * dy); if (L < 1e-9) return null;
        var u = (dx / L, dy / L); (SKPoint, PcbObj)? best = null; double bd = R;
        foreach (var (o, gs) in EdgeCands(sc, st, Q.X, Q.Y, R))
            foreach (var g in gs)
                foreach (var t in Ray(g, (A.X, A.Y), u))
                {
                    double d = Math.Abs(t - L);
                    if (d < bd && t > 1e-6) { bd = d; best = (new SKPoint((float)(A.X + u.Item1 * t), (float)(A.Y + u.Item2 * t)), o); }
                }
        return best;
    }

    /// <summary>Nearest centre / end point within R; a pad or via wins over a track end on the same spot.</summary>
    public static (SKPoint p, PcbObj o)? FindSnap(PcbScene sc, PcbViewState st, double x, double y, double R)
    {
        (SKPoint p, PcbObj o)? best = null; double bd = R;
        foreach (var (k, list) in sc.ByLayer)
        {
            if (!st.IsVisible(k)) continue;
            foreach (var o in list)
            {
                var b = o.BB; if (x < b.Left - R || x > b.Right + R || y < b.Top - R || y > b.Bottom + R) continue;
                bool pv = o.Kind is PcbObjKind.Pad or PcbObjKind.Via;
                foreach (var sp in o.Snaps)
                {
                    double d = Math.Sqrt((sp.X - x) * (sp.X - x) + (sp.Y - y) * (sp.Y - y));
                    if (d < bd - 1e-6 || best != null && Math.Abs(d - bd) <= 1e-6 && pv && best.Value.o.Kind is not (PcbObjKind.Pad or PcbObjKind.Via)) { bd = d; best = (sp, o); }
                }
            }
        }
        return best;
    }

    public static string EdgeName(PcbObj o) => o.Kind switch
    {
        PcbObjKind.Pad => "焊墊邊緣", PcbObjKind.Via => "導通孔邊緣", PcbObjKind.Track => "導線邊緣", PcbObjKind.Arc => "圓弧邊緣", PcbObjKind.Fill => "填充邊緣",
        _ => o.Src is PcbRegion { Poly: not Layers.None } ? "鋪銅邊緣" : "區域邊緣",
    };
    public static string CenterName(PcbObj o) => o.Kind switch
    {
        PcbObjKind.Pad => "焊墊中心", PcbObjKind.Via => "導通孔中心", PcbObjKind.Track => "導線端點", PcbObjKind.Arc => "圓弧端點／圓心", PcbObjKind.Fill => "填充中心", _ => "吸附點",
    };
}
