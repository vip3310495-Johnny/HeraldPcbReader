// Herald PCB Reader — Copyright (C) 2026 Johnny.ch.Tsai
// SPDX-License-Identifier: GPL-3.0-only
// Free software under the GNU General Public License version 3 (LICENSE.txt), WITHOUT ANY WARRANTY; with an
// additional permission to combine it with AvalonDock (Ms-PL), see NOTICE.txt.
using static AdCore.Step.StepFile;

namespace AdCore.Step;

/// <summary>Triangles of one face: positions and normals (mm, model space) and indices.</summary>
public sealed class FaceMesh
{
    public readonly List<V3> Pos = new(), Nrm = new();
    public readonly List<int> Idx = new();
    public void Add(FaceMesh b)
    {
        int o = Pos.Count; Pos.AddRange(b.Pos); Nrm.AddRange(b.Nrm);
        foreach (var i in b.Idx) Idx.Add(i + o);
    }
}

/// <summary>STEP B-rep faces → triangles. Edge curves are sampled once (both faces of an edge share the points, so
/// there are no cracks); a face is triangulated in its own parameter space (a plane in the plane), then its triangles
/// are cut along a grid so they follow a curved surface. Planes, cylinders, cones, spheres, tori, B-spline surfaces
/// (rational too) and linear extrusions; lines, circles, ellipses, B-spline curves and polylines.</summary>
public sealed class StepTess
{
    const double Tau = 2 * Math.PI;
    readonly StepFile _st;
    readonly double _tol, _maxAng;
    readonly Dictionary<int, List<V3>> _edges = new();
    readonly Dictionary<int, Curve> _curves = new();
    readonly Dictionary<int, Surf> _surfs = new();
    public int Faces, Failed;

    public StepTess(StepFile st, double tol = 0.03, double maxAngDeg = 20) { _st = st; _tol = tol; _maxAng = maxAngDeg * Math.PI / 180; }
    /// <summary>For other B-rep sources (Parasolid): geometry is handed over through the internal builders.</summary>
    internal StepTess(double tol, double maxAngDeg) : this(null, tol, maxAngDeg) { }

    /// <summary>Angle per segment on radius r: the chord tolerance, capped by an angle that grows for small radii (a
    /// 0.1 mm fillet on a chip part needs no more than a facet or two at board scale).</summary>
    double AngStep(double r)
    {
        if (!(r > 1e-9)) return _maxAng;
        double cap = r < 0.3 ? Math.PI / 2 : r < 0.8 ? Math.PI / 4 : r < 2.5 ? Math.PI / 6 : _maxAng;
        double a = 2 * Math.Acos(Math.Max(-1, 1 - _tol / r));
        return Math.Max(Math.Min(a, cap), Tau / 360);
    }

    // ================= B-splines (The NURBS Book, A2.1 / A2.2) =================
    static double[] ExpandKnots(List<object> knots, List<object> mults)
    {
        var u = new List<double>();
        for (int i = 0; i < Math.Min(knots.Count, mults.Count); i++) for (int k = 0; k < (int)Num(mults[i]) && k < 64; k++) u.Add(Num(knots[i]));
        return u.ToArray();
    }
    static int FindSpan(int n, int p, double u, double[] U)
    {
        if (u >= U[n + 1]) return n;
        if (u <= U[p]) return p;
        int lo = p, hi = n + 1, mid = (lo + hi) >> 1, g = 0;
        while ((u < U[mid] || u >= U[mid + 1]) && g++ < 64) { if (u < U[mid]) hi = mid; else lo = mid; mid = (lo + hi) >> 1; }
        return mid;
    }
    static void Basis(int i, double u, int p, double[] U, Span<double> N)
    {
        Span<double> left = stackalloc double[p + 1], right = stackalloc double[p + 1];
        N[0] = 1;
        for (int j = 1; j <= p; j++)
        {
            left[j] = u - U[i + 1 - j]; right[j] = U[i + j] - u;
            double saved = 0;
            for (int r = 0; r < j; r++) { double d = right[r + 1] + left[j - r]; double t = d == 0 ? 0 : N[r] / d; N[r] = saved + right[r + 1] * t; saved = left[j - r] * t; }
            N[j] = saved;
        }
    }

    internal sealed class BCurve
    {
        public readonly int P, N; readonly V3[] _pts; readonly double[] _u, _w; public readonly double T0, T1;
        BCurve(int p, V3[] pts, double[] u, double[] w) { P = p; _pts = pts; _u = u; _w = w; N = pts.Length - 1; T0 = u[p]; T1 = u[N + 1]; }
        public static BCurve Make(int p, V3[] pts, double[] u, double[] w)
        {
            if (p < 1 || p > 25 || pts.Length < p + 1 || u.Length != pts.Length + p + 1 || (w != null && w.Length != pts.Length)) return null;
            return u[pts.Length] > u[p] ? new BCurve(p, pts, u, w) : null;
        }
        public V3 At(double t)
        {
            t = Math.Clamp(t, T0, T1);
            int s = FindSpan(N, P, t, _u); Span<double> b = stackalloc double[P + 1]; Basis(s, t, P, _u, b);
            double x = 0, y = 0, z = 0, w = 0;
            for (int j = 0; j <= P; j++) { var q = _pts[s - P + j]; double c = (_w != null ? _w[s - P + j] : 1) * b[j]; x += q.X * c; y += q.Y * c; z += q.Z * c; w += c; }
            return w == 0 ? _pts[s] : new V3(x / w, y / w, z / w);
        }
        public List<(double a, double b)> Spans() { var s = new List<(double, double)>(); for (int i = P; i <= N; i++) if (_u[i + 1] > _u[i]) s.Add((_u[i], _u[i + 1])); return s; }
    }
    internal sealed class BSurf
    {
        public readonly int P, Q, Nu, Nv; public readonly V3[,] Pts; readonly double[] _u, _v; readonly double[,] _w;
        public readonly double U0, U1, V0, V1;
        BSurf(int p, int q, V3[,] pts, double[] u, double[] v, double[,] w)
        {
            P = p; Q = q; Pts = pts; _u = u; _v = v; _w = w; Nu = pts.GetLength(0) - 1; Nv = pts.GetLength(1) - 1;
            U0 = u[p]; U1 = u[Nu + 1]; V0 = v[q]; V1 = v[Nv + 1];
        }
        public static BSurf Make(int p, int q, V3[,] pts, double[] u, double[] v, double[,] w)
        {
            int nu = pts.GetLength(0), nv = pts.GetLength(1);
            if (p < 1 || q < 1 || p > 25 || q > 25 || nu < p + 1 || nv < q + 1 || u.Length != nu + p + 1 || v.Length != nv + q + 1) return null;
            if (w != null && (w.GetLength(0) != nu || w.GetLength(1) != nv)) return null;
            return u[nu] > u[p] && v[nv] > v[q] ? new BSurf(p, q, pts, u, v, w) : null;
        }
        public V3 At(double u, double v)
        {
            u = Math.Clamp(u, U0, U1); v = Math.Clamp(v, V0, V1);
            int su = FindSpan(Nu, P, u, _u), sv = FindSpan(Nv, Q, v, _v);
            Span<double> bu = stackalloc double[P + 1], bv = stackalloc double[Q + 1];
            Basis(su, u, P, _u, bu); Basis(sv, v, Q, _v, bv);
            double x = 0, y = 0, z = 0, w = 0;
            for (int a = 0; a <= P; a++)
                for (int b = 0; b <= Q; b++)
                {
                    int i = su - P + a, j = sv - Q + b; double c = bu[a] * bv[b] * (_w != null ? _w[i, j] : 1); var pt = Pts[i, j];
                    x += pt.X * c; y += pt.Y * c; z += pt.Z * c; w += c;
                }
            return w == 0 ? Pts[su, sv] : new V3(x / w, y / w, z / w);
        }
        public int SpansU() { int c = 0; for (int i = P; i <= Nu; i++) if (_u[i + 1] > _u[i]) c++; return c; }
        public int SpansV() { int c = 0; for (int i = Q; i <= Nv; i++) if (_v[i + 1] > _v[i]) c++; return c; }
    }

    // ================= curves =================
    internal abstract class Curve { }
    internal sealed class LineC : Curve { public V3 P0, D; }
    internal sealed class ConicC : Curve
    {
        public Frame F; public double A, B, R;
        public V3 At(double t) => F.O + F.X * (A * Math.Cos(t)) + F.Y * (B * Math.Sin(t));
        public double Param(V3 p) { var q = p - F.O; return Math.Atan2(V3.Dot(q, F.Y) / B, V3.Dot(q, F.X) / A); }
    }
    internal sealed class BsplC : Curve { public BCurve Bc; }
    internal sealed class PolyC : Curve { public List<V3> Pts; }
    /// <summary>A 2D B-spline in a surface's parameter space (Parasolid SP-curve): point = surface(us·x(t), vs·y(t)).</summary>
    internal sealed class SpC : Curve
    {
        public Surf S; public BCurve B; public double Us = 1, Vs = 1;
        public V3 At(double t) { var q = B.At(t); return S.At(q.X * Us, q.Y * Vs); }
    }
    internal static double[] Expand(double[] knots, int[] mults)
    {
        var u = new List<double>();
        for (int i = 0; i < Math.Min(knots.Length, mults.Length); i++) for (int k = 0; k < mults[i] && k < 64; k++) u.Add(knots[i]);
        return u.ToArray();
    }
    internal static BCurve MakeBCurve(int p, V3[] pts, double[] knots, double[] w) => BCurve.Make(p, pts, knots, w);

    /// <summary>Parameter (t0..t1) of the point of a curve closest to p, by sampling and halving the step.</summary>
    static double ClosestParam(Func<double, V3> at, double t0, double t1, int samples, V3 p)
    {
        double best = t0, bd = double.PositiveInfinity;
        for (int k = 0; k <= samples; k++) { double t = t0 + (t1 - t0) * k / samples; double d = (at(t) - p).Len; if (d < bd) { bd = d; best = t; } }
        double h = (t1 - t0) / samples;
        for (int it = 0; it < 50 && h > 1e-13 * Math.Max(1, Math.Abs(t1 - t0)); it++)
        {
            double a = Math.Max(t0, best - h), b = Math.Min(t1, best + h);
            double da = (at(a) - p).Len, db = (at(b) - p).Len;
            if (da < bd) { bd = da; best = a; } else if (db < bd) { bd = db; best = b; } else h /= 2;
        }
        return best;
    }

    /// <summary>Points along a curve from ps to pe; same: the edge runs with the curve's parameter; closed: a whole
    /// period (ps = pe). Both ends are the exact vertex points; the run is thinned to the tolerance.</summary>
    internal List<V3> SampleCurve(Curve c, V3 ps, V3 pe, bool same, bool closed)
    {
        List<V3> pts;
        switch (c)
        {
            case ConicC cc:
                {
                    double ts = cc.Param(ps), te = cc.Param(pe), dt = te - ts;
                    if (same) { while (dt <= 1e-9) dt += Tau; if (closed) dt = Tau; }
                    else { while (dt >= -1e-9) dt -= Tau; if (closed) dt = -Tau; }
                    pts = SampleRange(cc, ts, ts + dt);
                    break;
                }
            case BsplC bsc:
                {
                    var bc = bsc.Bc; double ts, te;
                    if (closed) { ts = same ? bc.T0 : bc.T1; te = same ? bc.T1 : bc.T0; }
                    else { ts = BParam(bc, ps); te = BParam(bc, pe); }
                    pts = SampleRange(bsc, ts, te);
                    break;
                }
            case SpC sp:
                {
                    var bc = sp.B; double ts, te;
                    if (closed) { ts = same ? bc.T0 : bc.T1; te = same ? bc.T1 : bc.T0; }
                    else { int n = Math.Max(16, bc.Spans().Count * 8); ts = ClosestParam(sp.At, bc.T0, bc.T1, n, ps); te = ClosestParam(sp.At, bc.T0, bc.T1, n, pe); }
                    pts = SampleRange(sp, ts, te);
                    break;
                }
            case PolyC pc:
                pts = new List<V3>(pc.Pts);
                if ((pts[0] - ps).Len > (pts[^1] - ps).Len) pts.Reverse();
                break;
            default: pts = new List<V3> { ps, pe }; break;   // a line, or a curve not understood: straight
        }
        if (pts.Count < 2) pts = new List<V3> { ps, pe };
        pts[0] = ps; pts[^1] = pe;
        if (pts.Count > 3) pts = Simplify(pts, _tol);
        return pts;
    }

    /// <summary>Points of a curve between two of its parameters (in that order).</summary>
    internal List<V3> SampleRange(Curve c, double ts, double te)
    {
        var o = new List<V3>();
        switch (c)
        {
            case LineC l: o.Add(l.P0 + l.D * ts); o.Add(l.P0 + l.D * te); break;
            case ConicC cc:
                {
                    double dt = te - ts; int n = Math.Max(1, (int)Math.Ceiling(Math.Abs(dt) / AngStep(cc.R)));
                    for (int i = 0; i <= n; i++) o.Add(cc.At(ts + dt * i / n));
                    break;
                }
            case BsplC or SpC:
                {
                    var bc = c is BsplC b1 ? b1.Bc : ((SpC)c).B;
                    Func<double, V3> at = c is BsplC bb ? bb.Bc.At : ((SpC)c).At;
                    double lo = Math.Min(ts, te), hi = Math.Max(ts, te);
                    var tl = new List<double>();
                    int k = c is SpC ? 8 : bc.P <= 1 ? 1 : Math.Max(2, Math.Min(16, bc.P * 3));
                    foreach (var (a, b) in bc.Spans()) { double a2 = Math.Max(a, lo), b2 = Math.Min(b, hi); if (b2 <= a2) continue; for (int i = 0; i < k; i++) tl.Add(a2 + (b2 - a2) * i / k); }
                    if (tl.Count == 0) tl.Add(lo);
                    tl.Add(hi);
                    if (ts > te) tl.Reverse();
                    foreach (var t in tl) o.Add(at(t));
                    break;
                }
        }
        return o;
    }

    /// <summary>Triangles of a face given its surface (null: flat, from its boundary), its boundary loops and whether
    /// its normal follows the surface's natural normal. Counted like Face().</summary>
    internal FaceMesh FaceFromLoops(Surf s, List<List<V3>> loops, bool same)
    {
        Faces++;
        FaceMesh o = null;
        try
        {
            loops = loops.Where(l => l.Count >= 2).ToList();
            if (s != null && s.Kind != "plane") o = ParamFace(s, loops, same);
            else if (loops.Count > 0) o = PlanarFace(loops, s != null ? s.F.Z * (same ? 1 : -1) : null, s?.F);
        }
        catch (Exception) { o = null; }
        if (o == null || o.Idx.Count == 0) { Failed++; return null; }
        return o;
    }

    Curve CurveOf(object r, int depth = 0)
    {
        int id = RefId(r); if (id == 0) return null;
        if (_curves.TryGetValue(id, out var c)) return c;
        _curves[id] = null;   // a loop of references ends here
        c = MakeCurve(_st.Get(id), depth); _curves[id] = c;
        return c;
    }
    V3[] Pts(object list) => List(list).Select(x => _st.Pt(x)).ToArray();
    Curve MakeCurve(Ent e, int depth)
    {
        if (e == null || depth > 8) return null;
        switch (e.Type)
        {
            case "LINE": { var v = _st.Get(Arg(e, 2)); return new LineC { P0 = _st.Pt(Arg(e, 1)), D = _st.Dir(Arg(v, 1)) }; }
            case "CIRCLE": { double r = Num(Arg(e, 2)) * _st.Scale; return r > 0 ? new ConicC { F = _st.Placement(Arg(e, 1)), A = r, B = r, R = r } : null; }
            case "ELLIPSE": { double a = Num(Arg(e, 2)) * _st.Scale, b = Num(Arg(e, 3)) * _st.Scale; return a > 0 && b > 0 ? new ConicC { F = _st.Placement(Arg(e, 1)), A = a, B = b, R = Math.Max(a, b) } : null; }
            case "B_SPLINE_CURVE_WITH_KNOTS":
                { var bc = BCurve.Make((int)Num(Arg(e, 1)), Pts(Arg(e, 2)), ExpandKnots(List(Arg(e, 7)), List(Arg(e, 6))), null); return bc == null ? null : new BsplC { Bc = bc }; }
            case "COMPLEX":
                {
                    var b = e.Part("B_SPLINE_CURVE"); var k = e.Part("B_SPLINE_CURVE_WITH_KNOTS");
                    if (b == null || k == null) return null;
                    var w = e.Part("RATIONAL_B_SPLINE_CURVE") is { } rw ? List(Arg(rw, 0)).Select(Num).ToArray() : null;
                    var bc = BCurve.Make((int)Num(Arg(b, 0)), Pts(Arg(b, 1)), ExpandKnots(List(Arg(k, 1)), List(Arg(k, 0))), w);
                    return bc == null ? null : new BsplC { Bc = bc };
                }
            case "TRIMMED_CURVE": case "SURFACE_CURVE": case "SEAM_CURVE": case "INTERSECTION_CURVE": case "BOUNDED_SURFACE_CURVE":
                return CurveOf(Arg(e, 1), depth + 1);
            case "POLYLINE": { var p = Pts(Arg(e, 1)).ToList(); return p.Count >= 2 ? new PolyC { Pts = p } : null; }
            default: return null;
        }
    }

    /// <summary>Parameter of the point of a B-spline curve closest to p.</summary>
    static double BParam(BCurve bc, V3 p)
    {
        double best = bc.T0, bd = double.PositiveInfinity;
        var spans = bc.Spans();
        foreach (var (a, b) in spans) for (int k = 0; k <= 8; k++) { double t = a + (b - a) * k / 8; double d = (bc.At(t) - p).Len; if (d < bd) { bd = d; best = t; } }
        double h = (bc.T1 - bc.T0) / Math.Max(8, spans.Count * 8);
        for (int it = 0; it < 40 && h > 1e-12; it++)
        {
            double t1 = Math.Max(bc.T0, best - h), t2 = Math.Min(bc.T1, best + h);
            double d1 = (bc.At(t1) - p).Len, d2 = (bc.At(t2) - p).Len;
            if (d1 < bd) { bd = d1; best = t1; } else if (d2 < bd) { bd = d2; best = t2; } else h /= 2;
        }
        return best;
    }

    /// <summary>Points along an EDGE_CURVE from its start vertex to its end vertex.</summary>
    List<V3> EdgePts(int id)
    {
        if (_edges.TryGetValue(id, out var cached)) return cached;
        var e = _st.Get(id);
        var vs = _st.Get(Arg(e, 1)); var ve = _st.Get(Arg(e, 2));
        V3 ps = _st.Pt(Arg(vs, 1)), pe = _st.Pt(Arg(ve, 1));
        bool same = !IsFalse(Arg(e, 4));
        var c = CurveOf(Arg(e, 3));
        bool closed = (ps - pe).Len < 1e-7;
        List<V3> pts;
        switch (c)
        {
            case ConicC cc:
                {
                    double ts = cc.Param(ps), te = cc.Param(pe), dt = te - ts;
                    if (same) { while (dt <= 1e-9) dt += Tau; if (closed) dt = Tau; }
                    else { while (dt >= -1e-9) dt -= Tau; if (closed) dt = -Tau; }
                    int n = Math.Max(1, (int)Math.Ceiling(Math.Abs(dt) / AngStep(cc.R)));
                    pts = new List<V3>(n + 1) { ps };
                    for (int i = 1; i < n; i++) pts.Add(cc.At(ts + dt * i / n));
                    pts.Add(pe);
                    break;
                }
            case BsplC bsc:
                {
                    var bc = bsc.Bc; double ts, te;
                    if (closed) { ts = same ? bc.T0 : bc.T1; te = same ? bc.T1 : bc.T0; }
                    else { ts = BParam(bc, ps); te = BParam(bc, pe); }
                    double lo = Math.Min(ts, te), hi = Math.Max(ts, te);
                    var tl = new List<double>();
                    int k = bc.P <= 1 ? 1 : Math.Max(2, Math.Min(16, bc.P * 3));
                    foreach (var (a, b) in bc.Spans()) { double a2 = Math.Max(a, lo), b2 = Math.Min(b, hi); if (b2 <= a2) continue; for (int i = 0; i < k; i++) tl.Add(a2 + (b2 - a2) * i / k); }
                    tl.Add(hi);
                    if (ts > te) tl.Reverse();
                    pts = tl.Select(bc.At).ToList();
                    pts[0] = ps; pts[^1] = pe;
                    if (pts.Count < 2) pts = new List<V3> { ps, pe };
                    break;
                }
            case PolyC pc:
                {
                    pts = new List<V3>(pc.Pts);
                    if ((pts[0] - ps).Len > (pts[^1] - ps).Len) pts.Reverse();
                    pts[0] = ps; pts[^1] = pe;
                    break;
                }
            default: pts = new List<V3> { ps, pe }; break;   // a line, or a curve not understood: straight
        }
        if (pts.Count > 3) pts = Simplify(pts, _tol);
        _edges[id] = pts;
        return pts;
    }

    /// <summary>Douglas-Peucker: drops the points within tol of the chord (both faces of an edge get the same points);
    /// tol at most a tenth of the edge's own size, so a thin wire keeps its round section.</summary>
    static List<V3> Simplify(List<V3> pts, double tol)
    {
        double x0 = double.MaxValue, y0 = double.MaxValue, z0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue, z1 = double.MinValue;
        foreach (var p in pts) { x0 = Math.Min(x0, p.X); y0 = Math.Min(y0, p.Y); z0 = Math.Min(z0, p.Z); x1 = Math.Max(x1, p.X); y1 = Math.Max(y1, p.Y); z1 = Math.Max(z1, p.Z); }
        tol = Math.Min(tol, 0.1 * new V3(x1 - x0, y1 - y0, z1 - z0).Len);
        int n = pts.Count;
        var keep = new bool[n]; keep[0] = keep[n - 1] = true;
        var stack = new Stack<(int a, int b)>(); stack.Push((0, n - 1));
        while (stack.Count > 0)
        {
            var (a, b) = stack.Pop(); if (b - a < 2) continue;
            V3 A = pts[a], ab = pts[b] - A; double L = ab.Len, bd = tol; int best = -1;
            for (int i = a + 1; i < b; i++) { var ap = pts[i] - A; double d = L < 1e-12 ? ap.Len : V3.Cross(ab, ap).Len / L; if (d > bd) { bd = d; best = i; } }
            if (best >= 0) { keep[best] = true; stack.Push((a, best)); stack.Push((best, b)); }
        }
        // a closed edge (start = end) keeps two points between, so it stays a loop
        if ((pts[0] - pts[n - 1]).Len < 1e-7) keep[n / 3] = keep[2 * n / 3] = true;
        var o = new List<V3>();
        for (int i = 0; i < n; i++) if (keep[i]) o.Add(pts[i]);
        return o;
    }

    /// <summary>Points of a loop (null for a vertex loop or one not understood).</summary>
    List<V3> LoopPts(object loopRef, out bool vertex)
    {
        vertex = false;
        var l = _st.Get(loopRef); if (l == null) return null;
        if (l.Type == "VERTEX_LOOP") { vertex = true; return null; }
        if (l.Type == "POLY_LOOP") return Pts(Arg(l, 1)).ToList();
        if (l.Type != "EDGE_LOOP") return null;
        var o = new List<V3>();
        foreach (var oeRef in List(Arg(l, 1)))
        {
            var oe = _st.Get(oeRef); if (oe == null) continue;
            int ec = oe.Type == "ORIENTED_EDGE" ? RefId(Arg(oe, 3)) : oe.Id;
            if (ec == 0 || _st.Get(ec) is not { Type: "EDGE_CURVE" }) continue;
            var pts = EdgePts(ec);
            bool rev = oe.Type == "ORIENTED_EDGE" && IsFalse(Arg(oe, 4));
            for (int i = 0; i < pts.Count; i++)
            {
                var q = pts[rev ? pts.Count - 1 - i : i];
                if (o.Count > 0 && i == 0 && (o[^1] - q).Len < 1e-9) continue;
                o.Add(q);
            }
        }
        if (o.Count > 1 && (o[0] - o[^1]).Len < 1e-9) o.RemoveAt(o.Count - 1);
        return o;
    }

    // ================= surfaces =================
    internal sealed class Surf
    {
        public string Kind = "";
        public Frame F;
        public double Up, Vp, Du = double.PositiveInfinity, Dv = double.PositiveInfinity;
        public double? Umin, Umax, Vmin, Vmax, VApex;
        public Func<V3, (double u, double v)> UV;
        public Func<double, double, V3> At, N;
    }

    Surf SurfOf(object r)
    {
        int id = RefId(r); if (id == 0) return null;
        if (_surfs.TryGetValue(id, out var s)) return s;
        try { s = MakeSurf(_st.Get(id)); } catch (Exception) { s = null; }
        _surfs[id] = s;
        return s;
    }

    Surf MakeSurf(Ent e)
    {
        if (e == null) return null;
        switch (e.Type)
        {
            case "PLANE": return PlaneS(_st.Placement(Arg(e, 1)));
            case "CYLINDRICAL_SURFACE": return CylinderS(_st.Placement(Arg(e, 1)), Num(Arg(e, 2)) * _st.Scale);
            case "CONICAL_SURFACE": return ConeS(_st.Placement(Arg(e, 1)), Num(Arg(e, 2)) * _st.Scale, Num(Arg(e, 3)) * _st.Ang);
            case "SPHERICAL_SURFACE": return SphereS(_st.Placement(Arg(e, 1)), Num(Arg(e, 2)) * _st.Scale);
            case "TOROIDAL_SURFACE": case "DEGENERATE_TOROIDAL_SURFACE": return TorusS(_st.Placement(Arg(e, 1)), Num(Arg(e, 2)) * _st.Scale, Num(Arg(e, 3)) * _st.Scale);
            case "B_SPLINE_SURFACE_WITH_KNOTS":
                return BsplineSurf((int)Num(Arg(e, 1)), (int)Num(Arg(e, 2)), List(Arg(e, 3)), List(Arg(e, 10)), List(Arg(e, 8)), List(Arg(e, 11)), List(Arg(e, 9)), null);
            case "COMPLEX":
                {
                    var b = e.Part("B_SPLINE_SURFACE"); var k = e.Part("B_SPLINE_SURFACE_WITH_KNOTS");
                    if (b == null || k == null) return null;
                    return BsplineSurf((int)Num(Arg(b, 0)), (int)Num(Arg(b, 1)), List(Arg(b, 2)), List(Arg(k, 2)), List(Arg(k, 0)), List(Arg(k, 3)), List(Arg(k, 1)),
                        e.Part("RATIONAL_B_SPLINE_SURFACE") is { } rw ? List(Arg(rw, 0)) : null);
                }
            case "SURFACE_OF_LINEAR_EXTRUSION":
                {
                    var c = CurveOf(Arg(e, 1)); if (c == null) return null;
                    var vec = _st.Get(Arg(e, 2)); var d = _st.Dir(Arg(vec, 1)) * (Num(Arg(vec, 2)) * _st.Scale);
                    return d.Len > 1e-12 ? ExtrusionSurf(c, d) : null;
                }
            default: return null;
        }
    }

    // ---------- analytic surfaces (STEP / Parasolid conventions agree: y = z × x, natural normal outward) ----------
    internal Surf PlaneS(Frame f) => new()
    {
        Kind = "plane", F = f,
        UV = p => { var q = p - f.O; return (V3.Dot(q, f.X), V3.Dot(q, f.Y)); },
        At = (u, v) => f.O + f.X * u + f.Y * v,
        N = (u, v) => f.Z,
    };
    internal Surf CylinderS(Frame f, double r) => !(r > 0) ? null : new Surf
    {
        Kind = "cylinder", F = f, Up = Tau, Du = AngStep(r),
        UV = p => { var q = p - f.O; return (Math.Atan2(V3.Dot(q, f.Y), V3.Dot(q, f.X)), V3.Dot(q, f.Z)); },
        At = (u, v) => f.O + f.X * (r * Math.Cos(u)) + f.Y * (r * Math.Sin(u)) + f.Z * v,
        N = (u, v) => f.X * Math.Cos(u) + f.Y * Math.Sin(u),
    };
    /// <summary>STEP cone: radius r at the origin, growing by tan(a) along +z.</summary>
    internal Surf ConeS(Frame f, double r, double a)
    {
        double ta = Math.Tan(a);
        return new Surf
        {
            Kind = "cone", F = f, Up = Tau, Du = AngStep(Math.Max(r, 0.5)), VApex = Math.Abs(ta) > 1e-12 ? -r / ta : null,
            UV = p => { var q = p - f.O; double v = V3.Dot(q, f.Z), rad = r + v * ta; return (Math.Abs(rad) < 1e-9 ? double.NaN : Math.Atan2(V3.Dot(q, f.Y), V3.Dot(q, f.X)), v); },
            At = (u, v) => { double rad = r + v * ta; return f.O + f.X * (rad * Math.Cos(u)) + f.Y * (rad * Math.Sin(u)) + f.Z * v; },
            N = (u, v) => (f.X * Math.Cos(u) + f.Y * Math.Sin(u)) * Math.Cos(a) - f.Z * Math.Sin(a),
        };
    }
    /// <summary>Parasolid cone: R(u,v) = P − vA + (X cos u + Y sin u)(r + v tan a), Y = A × X; its natural normal
    /// (dR/du × dR/dv) points towards the axis.</summary>
    internal Surf XtConeS(V3 p0, V3 axis, V3 x, double r, double sinA, double cosA)
    {
        var A = axis.Norm(); var X = (x - A * V3.Dot(x, A)).Norm(); var Y = V3.Cross(A, X);
        double ta = Math.Abs(cosA) > 1e-12 ? sinA / cosA : 0;
        V3 Su(double u, double v) => (X * -Math.Sin(u) + Y * Math.Cos(u)) * (r + v * ta);
        V3 Sv(double u, double v) => A * -1 + (X * Math.Cos(u) + Y * Math.Sin(u)) * ta;
        return new Surf
        {
            Kind = "cone", F = Frame.Make(p0, A, X), Up = Tau, Du = AngStep(Math.Max(Math.Abs(r), 0.5)), VApex = Math.Abs(ta) > 1e-12 ? -r / ta : null,
            UV = p => { var q = p - p0; double v = -V3.Dot(q, A), rad = r + v * ta; return (Math.Abs(rad) < 1e-9 ? double.NaN : Math.Atan2(V3.Dot(q, Y), V3.Dot(q, X)), v); },
            At = (u, v) => p0 - A * v + (X * Math.Cos(u) + Y * Math.Sin(u)) * (r + v * ta),
            N = (u, v) => { var n = V3.Cross(Su(u, v), Sv(u, v)); return n.Len > 1e-12 ? n : (X * Math.Cos(u) + Y * Math.Sin(u)) * -1; },
        };
    }
    internal Surf SphereS(Frame f, double r) => !(r > 0) ? null : new Surf
    {
        Kind = "sphere", F = f, Up = Tau, Du = AngStep(r), Dv = AngStep(r), Vmin = -Math.PI / 2, Vmax = Math.PI / 2,
        UV = p => { var q = p - f.O; double z = V3.Dot(q, f.Z) / r, v = Math.Asin(Math.Clamp(z, -1, 1)); return (Math.Abs(Math.Abs(z) - 1) < 1e-9 ? double.NaN : Math.Atan2(V3.Dot(q, f.Y), V3.Dot(q, f.X)), v); },
        At = (u, v) => f.O + f.X * (r * Math.Cos(v) * Math.Cos(u)) + f.Y * (r * Math.Cos(v) * Math.Sin(u)) + f.Z * (r * Math.Sin(v)),
        N = (u, v) => f.X * (Math.Cos(v) * Math.Cos(u)) + f.Y * (Math.Cos(v) * Math.Sin(u)) + f.Z * Math.Sin(v),
    };
    internal Surf TorusS(Frame f, double R, double r) => !(r > 0) ? null : new Surf
    {
        Kind = "torus", F = f, Up = Tau, Vp = Tau, Du = AngStep(Math.Abs(R) + r), Dv = AngStep(r),
        UV = p => { var q = p - f.O; double x = V3.Dot(q, f.X), y = V3.Dot(q, f.Y), z = V3.Dot(q, f.Z), rho = Math.Sqrt(x * x + y * y); return (rho < 1e-9 ? double.NaN : Math.Atan2(y, x), Math.Atan2(z, rho - R)); },
        At = (u, v) => { double k = R + r * Math.Cos(v); return f.O + f.X * (k * Math.Cos(u)) + f.Y * (k * Math.Sin(u)) + f.Z * (r * Math.Sin(v)); },
        N = (u, v) => f.X * (Math.Cos(v) * Math.Cos(u)) + f.Y * (Math.Cos(v) * Math.Sin(u)) + f.Z * Math.Sin(v),
    };

    Surf BsplineSurf(int p, int q, List<object> grid, List<object> uk, List<object> um, List<object> vk, List<object> vm, List<object> wgrid)
    {
        int nu = grid.Count, nv = nu > 0 ? List(grid[0]).Count : 0;
        if (nu < 2 || nv < 2) return null;
        var P = new V3[nu, nv];
        for (int i = 0; i < nu; i++) { var row = List(grid[i]); if (row.Count != nv) return null; for (int j = 0; j < nv; j++) P[i, j] = _st.Pt(row[j]); }
        double[,] W = null;
        if (wgrid != null)
        {
            W = new double[nu, nv];
            for (int i = 0; i < nu; i++) { var row = List(Arg(wgrid, i)); for (int j = 0; j < nv; j++) W[i, j] = j < row.Count ? Num(row[j]) : 1; }
        }
        return BSplineS(p, q, P, ExpandKnots(uk, um), ExpandKnots(vk, vm), W);
    }

    /// <summary>A B-spline surface (control points P[i along u, j along v], full knot vectors, optional weights).</summary>
    internal Surf BSplineS(int p, int q, V3[,] P, double[] uKnots, double[] vKnots, double[,] W)
    {
        int nu = P.GetLength(0), nv = P.GetLength(1);
        var bs = BSurf.Make(p, q, P, uKnots, vKnots, W);
        if (bs == null) return null;
        // a surface closed in u (or v) is treated as periodic there, so loops can cross its seam
        static bool Near(V3 a, V3 b) => (a - b).Len < 1e-6;
        bool closedU = new[] { 0, 0.5, 1 }.All(t => Near(bs.At(bs.U0, bs.V0 + (bs.V1 - bs.V0) * t), bs.At(bs.U1, bs.V0 + (bs.V1 - bs.V0) * t)));
        bool closedV = new[] { 0, 0.5, 1 }.All(t => Near(bs.At(bs.U0 + (bs.U1 - bs.U0) * t, bs.V0), bs.At(bs.U0 + (bs.U1 - bs.U0) * t, bs.V1)));
        // cells from how far the control net turns along each direction (radius estimate = length / turn)
        (double turn, double len) Turn(bool alongU)
        {
            double best = 0, bl = 0;
            int rows = alongU ? nv : nu, cols = alongU ? nu : nv;
            for (int r = 0; r < rows; r++)
            {
                double t = 0, l = 0;
                for (int i = 0; i + 1 < cols; i++)
                {
                    V3 a0 = alongU ? P[i, r] : P[r, i], a1 = alongU ? P[i + 1, r] : P[r, i + 1];
                    var a = a1 - a0; l += a.Len;
                    if (i + 2 < cols)
                    {
                        var b = (alongU ? P[i + 2, r] : P[r, i + 2]) - a1; double la = a.Len, lb = b.Len;
                        if (la > 1e-12 && lb > 1e-12) t += Math.Acos(Math.Clamp(V3.Dot(a, b) / (la * lb), -1, 1));
                    }
                }
                best = Math.Max(best, t); bl = Math.Max(bl, l);
            }
            return (best, bl);
        }
        int Cells((double t, double l) x) => x.t < 1e-3 ? 1 : Math.Clamp((int)Math.Ceiling(x.t / AngStep(x.l / x.t)), 1, 32);
        var (tu, tv) = (Turn(true), Turn(false));
        int cu = Cells(tu), cv = Cells(tv);
        // at most ~100 cells on one face (threads and knurls would take tens of thousands of triangles otherwise), but
        // never more than a quarter turn in one cell (a thin tube would become a ribbon)
        if (cu * cv > 100) { double k = Math.Sqrt(100.0 / (cu * cv)); cu = Math.Max(1, (int)Math.Round(cu * k)); cv = Math.Max(1, (int)Math.Round(cv * k)); }
        cu = Math.Max(cu, Math.Min(32, (int)Math.Ceiling(tu.turn / (Math.PI / 2))));
        cv = Math.Max(cv, Math.Min(32, (int)Math.Ceiling(tv.turn / (Math.PI / 2))));
        // samples to start the point inversion from
        int gu = Math.Clamp(bs.SpansU() * 4, 6, 40), gv = Math.Clamp(bs.SpansV() * 4, 6, 40);
        var samples = new (double u, double v, V3 p)[(gu + 1) * (gv + 1)];
        for (int i = 0, k = 0; i <= gu; i++) for (int j = 0; j <= gv; j++, k++) { double u = bs.U0 + (bs.U1 - bs.U0) * i / gu, v = bs.V0 + (bs.V1 - bs.V0) * j / gv; samples[k] = (u, v, bs.At(u, v)); }
        static double Wrap(double x, double a, double b, bool closed) { if (!closed) return x; double per = b - a; return a + ((x - a) % per + per) % per; }
        V3 At(double u, double v) => bs.At(Wrap(u, bs.U0, bs.U1, closedU), Wrap(v, bs.V0, bs.V1, closedV));
        double hU = (bs.U1 - bs.U0) * 1e-5, hV = (bs.V1 - bs.V0) * 1e-5;
        V3 DU(double u, double v) => (At(u + hU, v) - At(u - hU, v)) * (1 / (2 * hU));
        V3 DV(double u, double v) => (At(u, v + hV) - At(u, v - hV)) * (1 / (2 * hV));
        (double, double) Invert(V3 pt)
        {
            double bd = double.PositiveInfinity, u = bs.U0, v = bs.V0;
            foreach (var s in samples) { double d = (s.p - pt).Len; if (d < bd) { bd = d; u = s.u; v = s.v; } }
            for (int it = 0; it < 12; it++)
            {
                var S = At(u, v); var r = pt - S; var su = DU(u, v); var sv = DV(u, v);
                double a = V3.Dot(su, su), b = V3.Dot(su, sv), c = V3.Dot(sv, sv), d1 = V3.Dot(su, r), d2 = V3.Dot(sv, r);
                double det = a * c - b * b; if (Math.Abs(det) < 1e-30) break;
                double du = (c * d1 - b * d2) / det, dv = (a * d2 - b * d1) / det;
                u += du; v += dv;
                if (!closedU) u = Math.Clamp(u, bs.U0, bs.U1);
                if (!closedV) v = Math.Clamp(v, bs.V0, bs.V1);
                if (Math.Abs(du) < 1e-10 * (bs.U1 - bs.U0) && Math.Abs(dv) < 1e-10 * (bs.V1 - bs.V0)) break;
            }
            return (Wrap(u, bs.U0, bs.U1, closedU), Wrap(v, bs.V0, bs.V1, closedV));
        }
        return new Surf
        {
            Kind = "bspline", Up = closedU ? bs.U1 - bs.U0 : 0, Vp = closedV ? bs.V1 - bs.V0 : 0,
            Du = (bs.U1 - bs.U0) / cu, Dv = (bs.V1 - bs.V0) / cv, Umin = bs.U0, Umax = bs.U1, Vmin = bs.V0, Vmax = bs.V1,
            UV = Invert, At = At, N = (u, v) => V3.Cross(DU(u, v), DV(u, v)),
        };
    }

    internal Surf Extrusion(Curve c, V3 d) => ExtrusionSurf(c, d);
    Surf ExtrusionSurf(Curve c, V3 d)
    {
        double dl = d.Len; var dn = d.Norm();
        Func<double, V3> cAt; Func<V3, double> cParam; double du, up = 0;
        switch (c)
        {
            case LineC l: cAt = t => l.P0 + l.D * t; cParam = p => V3.Dot(p - l.P0, l.D); du = double.PositiveInfinity; break;
            case ConicC cc: cAt = cc.At; cParam = p => cc.Param(p - dn * V3.Dot(p - cc.F.O, dn)); du = AngStep(cc.R); up = Tau; break;
            case BsplC bc:
                {
                    var b = bc.Bc; var r0 = b.At(b.T0);
                    cAt = b.At; cParam = p => BParam(b, p - dn * V3.Dot(p - r0, dn));
                    du = (b.T1 - b.T0) / Math.Min(64, b.Spans().Count * Math.Max(1, b.P * 3));
                    break;
                }
            default: return null;
        }
        return new Surf
        {
            Kind = "extrusion", Up = up, Du = du,
            UV = p => { double u = cParam(p); return (u, V3.Dot(p - cAt(u), dn) / dl); },
            At = (u, v) => cAt(u) + d * v,
            N = (u, v) => V3.Cross(cAt(u + 1e-6) - cAt(u - 1e-6), dn),
        };
    }

    // ================= faces =================
    /// <summary>A point in a face's parameter space, with its exact 3D position when it is a boundary point.</summary>
    struct UV
    {
        public double U, V; public V3 P; public bool HasP;
        public UV(double u, double v) { U = u; V = v; P = default; HasP = false; }
        public UV(double u, double v, V3 p) { U = u; V = v; P = p; HasP = true; }
        public double this[int axis] => axis == 0 ? U : V;
        public UV With(int axis, double val) { var q = this; if (axis == 0) q.U = val; else q.V = val; return q; }
        public UV Plus(int axis, double d) => With(axis, this[axis] + d);
    }
    sealed class Ring { public List<UV> Q; public int Wu, Wv, Dir; public double Area; }

    /// <summary>Triangles of a face (ADVANCED_FACE / FACE_SURFACE / FACE), or null.</summary>
    public FaceMesh Face(int id)
    {
        var f = _st.Get(id); if (f == null) return null;
        List<object> bounds; object surfRef = null; bool same = true;
        if (f.Type is "ADVANCED_FACE" or "FACE_SURFACE") { bounds = List(Arg(f, 1)); surfRef = Arg(f, 2); same = !IsFalse(Arg(f, 3)); }
        else if (f.Type == "FACE") bounds = List(Arg(f, 1));
        else return null;
        Faces++;
        FaceMesh o = null;
        try
        {
            var loops = new List<List<V3>>();
            foreach (var bref in bounds)
            {
                var b = _st.Get(bref); if (b == null) continue;
                var pts = LoopPts(Arg(b, 1), out bool vertex);
                if (vertex || pts == null) continue;
                if (IsFalse(Arg(b, 2))) pts.Reverse();
                if (pts.Count >= 2) loops.Add(pts);
            }
            var s = surfRef != null ? SurfOf(surfRef) : null;
            if (s != null && s.Kind != "plane") o = ParamFace(s, loops, same);
            else if (loops.Count > 0) o = PlanarFace(loops, s != null ? s.F.Z * (same ? 1 : -1) : null, s?.F);
        }
        catch (Exception) { o = null; }
        if (o == null || o.Idx.Count == 0) { Failed++; return null; }
        return o;
    }

    FaceMesh PlanarFace(List<List<V3>> loops, V3? normal, Frame? fr)
    {
        if (normal == null)
        {
            // Newell normal of the first loop
            var p = loops[0]; double nx = 0, ny = 0, nz = 0;
            for (int i = 0; i < p.Count; i++)
            {
                var a = p[i]; var b = p[(i + 1) % p.Count];
                nx += (a.Y - b.Y) * (a.Z + b.Z); ny += (a.Z - b.Z) * (a.X + b.X); nz += (a.X - b.X) * (a.Y + b.Y);
            }
            var n = new V3(nx, ny, nz); if (n.Len < 1e-12) return null;
            normal = n.Norm();
        }
        var nn = normal.Value;
        var f = fr is Frame g ? Frame.Make(g.O, nn, g.X) : Frame.Make(loops[0][0], nn, null);
        var rings = new List<Ring>();
        int total = 0;
        foreach (var l in loops)
        {
            if (l.Count < 3) continue;
            total += l.Count; if (total > 40000) return null;   // a face this big would take too long to cut into triangles
            rings.Add(new Ring { Q = l.Select(p => { var q = p - f.O; return new UV(V3.Dot(q, f.X), V3.Dot(q, f.Y), p); }).ToList() });
        }
        if (rings.Count == 0) return null;
        return Triangulate(rings, null, null, double.PositiveInfinity, double.PositiveInfinity, nn);
    }

    static double RingArea(List<UV> q) { double a = 0; for (int i = 0, j = q.Count - 1; i < q.Count; j = i++) a += (q[j].U - q[i].U) * (q[j].V + q[i].V); return a / 2; }
    static bool PointInRing(UV p, List<UV> q)
    {
        bool inside = false;
        for (int i = 0, j = q.Count - 1; i < q.Count; j = i++)
        {
            var a = q[i]; var b = q[j];
            if ((a.V > p.V) != (b.V > p.V) && p.U < (b.U - a.U) * (p.V - a.V) / (b.V - a.V) + a.U) inside = !inside;
        }
        return inside;
    }
    static double Avg(List<UV> q, int axis) { double s = 0; foreach (var p in q) s += p[axis]; return q.Count > 0 ? s / q.Count : 0; }

    /// <summary>Rings (the outer one = largest area) → triangles; cut along a grid of du × dv cells when the surface is
    /// curved; positions from at(u, v) except where a boundary point is known.</summary>
    FaceMesh Triangulate(List<Ring> rings, Func<double, double, V3> at, Func<double, double, V3> nAt, double du, double dv, V3? flatNormal)
    {
        foreach (var r in rings) r.Area = RingArea(r.Q);
        rings.Sort((a, b) => Math.Abs(b.Area).CompareTo(Math.Abs(a.Area)));
        var data = new List<double>(); var holes = new List<int>(); var all = new List<UV>();
        for (int k = 0; k < rings.Count; k++)
        {
            if (k > 0) holes.Add(data.Count / 2);
            foreach (var q in rings[k].Q) { data.Add(q.U); data.Add(q.V); all.Add(q); }
        }
        var tri = Earcut.Triangulate(data, holes);
        var o = new FaceMesh();
        var vmap = new Dictionary<(long, long), int>();
        int Vert(UV q)
        {
            var key = ((long)Math.Round(q.U * 1e9), (long)Math.Round(q.V * 1e9));
            if (vmap.TryGetValue(key, out int i)) return i;
            var p = q.HasP ? q.P : at(q.U, q.V);
            var n = flatNormal ?? nAt(q.U, q.V).Norm();
            i = o.Pos.Count; o.Pos.Add(p); o.Nrm.Add(n); vmap[key] = i;
            return i;
        }
        void Emit(int a, int b, int c)
        {
            // keep the winding consistent with the normal
            var g = V3.Cross(o.Pos[b] - o.Pos[a], o.Pos[c] - o.Pos[a]); if (g.Len < 1e-14) return;
            var n = o.Nrm[a] + o.Nrm[b] + o.Nrm[c];
            if (V3.Dot(g, n) < 0) { o.Idx.Add(a); o.Idx.Add(c); o.Idx.Add(b); } else { o.Idx.Add(a); o.Idx.Add(b); o.Idx.Add(c); }
        }
        bool split = !double.IsPositiveInfinity(du) || !double.IsPositiveInfinity(dv);
        for (int t = 0; t + 2 < tri.Count; t += 3)
        {
            UV a = all[tri[t]], b = all[tri[t + 1]], c = all[tri[t + 2]];
            if (!split) { Emit(Vert(a), Vert(b), Vert(c)); continue; }
            // cut the triangle along the grid lines it crosses (convex clipping, exact)
            foreach (var poly in GridSplit(new List<UV> { a, b, c }, du, dv))
            {
                var v = poly.Select(Vert).ToList();
                for (int k = 1; k + 1 < v.Count; k++) Emit(v[0], v[k], v[k + 1]);
            }
        }
        return o;
    }

    static List<List<UV>> GridSplit(List<UV> tri, double du, double dv)
    {
        var polys = new List<List<UV>> { tri };
        static List<List<UV>> SplitAxis(List<List<UV>> polys, int axis, double step)
        {
            if (double.IsInfinity(step) || !(step > 0)) return polys;
            var o = new List<List<UV>>();
            foreach (var poly in polys)
            {
                double lo = poly.Min(q => q[axis]), hi = poly.Max(q => q[axis]);
                long k0 = (long)Math.Floor(lo / step + 1e-9), k1 = (long)Math.Ceiling(hi / step - 1e-9);
                if (k1 - k0 <= 1 || hi - lo <= step * 1.05 || k1 - k0 > 4096) { o.Add(poly); continue; }
                var rest = poly;
                for (long k = k0 + 1; k < k1 && rest.Count >= 3; k++)
                {
                    var (a, b) = ClipHalf(rest, axis, k * step);
                    if (a.Count >= 3) o.Add(a);
                    rest = b;
                }
                if (rest.Count >= 3) o.Add(rest);
            }
            return o;
        }
        polys = SplitAxis(polys, 0, du);
        polys = SplitAxis(polys, 1, dv);
        return polys;
    }

    /// <summary>A convex polygon split by the line q[axis] = c into (below, above).</summary>
    static (List<UV>, List<UV>) ClipHalf(List<UV> poly, int axis, double c)
    {
        var a = new List<UV>(); var b = new List<UV>();
        for (int i = 0; i < poly.Count; i++)
        {
            var p = poly[i]; var q = poly[(i + 1) % poly.Count];
            double dp = p[axis] - c, dq = q[axis] - c;
            if (dp <= 0) a.Add(p);
            if (dp >= 0) b.Add(p);
            if ((dp < 0 && dq > 0) || (dp > 0 && dq < 0))
            {
                double t = dp / (dp - dq);
                var x = new UV(p.U + (q.U - p.U) * t, p.V + (q.V - p.V) * t).With(axis, c);
                a.Add(x); b.Add(x);
            }
        }
        return (a, b);
    }

    FaceMesh ParamFace(Surf s, List<List<V3>> loops, bool same)
    {
        double up = s.Up, vp = s.Vp;
        // each loop in (u, v), continuous across the periodic seams; poles (u undefined) take the u of their neighbours
        var rings = new List<Ring>();
        foreach (var l in loops)
        {
            var q = new List<UV>(l.Count);
            bool hasPrev = false; double pu = 0, pv = 0;
            for (int i = 0; i < l.Count; i++)
            {
                var (u, v) = s.UV(l[i]);
                if (hasPrev)
                {
                    if (up > 0 && !double.IsNaN(u) && !double.IsNaN(pu)) u += Math.Round((pu - u) / up) * up;
                    if (vp > 0) v += Math.Round((pv - v) / vp) * vp;
                }
                q.Add(new UV(u, v, l[i]));
                if (!double.IsNaN(u)) { pu = u; pv = v; hasPrev = true; } else if (hasPrev) pv = v;
            }
            if (q.Any(x => double.IsNaN(x.U)))
            {
                // a pole: two points, carrying the u of the point before and of the point after
                var o = new List<UV>(); int n = q.Count;
                for (int i = 0; i < n; i++)
                {
                    if (!double.IsNaN(q[i].U)) { o.Add(q[i]); continue; }
                    UV? a = null, b = null;
                    for (int k = 1; k < n && a == null; k++) { var r = q[(i - k + n) % n]; if (!double.IsNaN(r.U)) a = r; }
                    for (int k = 1; k < n && b == null; k++) { var r = q[(i + k) % n]; if (!double.IsNaN(r.U)) b = r; }
                    if (a == null || b == null) continue;
                    double ub = b.Value.U; if (up > 0) ub += Math.Round((a.Value.U - ub) / up) * up;
                    o.Add(new UV(a.Value.U, q[i].V, q[i].P)); o.Add(new UV(ub, q[i].V, q[i].P));
                }
                q = o;
                for (int i = 1; i < q.Count; i++) if (up > 0) q[i] = q[i].With(0, q[i].U + Math.Round((q[i - 1].U - q[i].U) / up) * up);
            }
            if (q.Count < 2) continue;
            // the closing segment back to the first point: how far round the seam the loop went
            double cu = q[0].U - q[^1].U, cv = q[0].V - q[^1].V;
            int wu = up > 0 ? (int)Math.Round(cu / up) : 0, wv = vp > 0 ? (int)Math.Round(cv / vp) : 0;
            rings.Add(new Ring { Q = q, Wu = -wu, Wv = -wv });
        }
        var windU = rings.Where(r => r.Wu != 0).ToList();
        var windV = rings.Where(r => r.Wu == 0 && r.Wv != 0).ToList();
        var plain = rings.Where(r => r.Wu == 0 && r.Wv == 0).ToList();
        var regions = new List<List<UV>>();

        // winding loops (went once round a seam) are closed along the seam or the edge of the domain
        void CloseWinding(List<Ring> wl, double per, int axis)
        {
            int other = 1 - axis; double perO = axis == 0 ? vp : up;
            double c = wl.Min(r => r.Q.Min(x => x[axis]));
            var cut = wl.Select(r => CutAt(r, axis, c, per)).ToList();
            // the face is left of a loop when its normal follows the surface normal: moving +u, left is +v; moving +v, -u
            int Side(Ring A) { int sd = (A.Dir > 0) == same ? 1 : -1; return axis == 1 ? -sd : sd; }
            void Band(Ring A, Ring B)
            {
                if (Math.Sign(A.Dir) == Math.Sign(B.Dir)) { var rq = new List<UV>(B.Q); rq.Reverse(); B = new Ring { Q = rq, Dir = -B.Dir }; }
                if (perO > 0)
                {
                    // the other direction is periodic too (torus): B goes on the face side of A
                    int sa = Side(A); double ma = Avg(A.Q, other), mb = Avg(B.Q, other), sh = 0;
                    for (int g = 0; g < 4 && (mb + sh - ma) * sa < 0; g++) sh += sa * perO;
                    if (sh != 0) B = new Ring { Q = B.Q.Select(x => x.Plus(other, sh)).ToList(), Dir = B.Dir };
                }
                regions.Add(A.Q.Concat(B.Q).ToList());
            }
            if (cut.Count == 1)
            {
                var A = cut[0]; int sa = Side(A);
                // closed by the domain edge on the face side (sphere pole, cone apex, end of a spline)
                double? lim = axis == 0 ? (sa > 0 ? s.Vmax : s.Vmin) : (sa > 0 ? s.Umax : s.Umin);
                lim ??= s.VApex;
                if (lim is not double L) return;
                var e = A.Q[^1]; var b = A.Q[0];
                var r = new List<UV>(A.Q);
                if (axis == 0) { r.Add(new UV(e.U, L)); r.Add(new UV(b.U, L)); } else { r.Add(new UV(L, e.V)); r.Add(new UV(L, b.V)); }
                regions.Add(r);
            }
            else
            {
                cut.Sort((a, b) => Avg(a.Q, other).CompareTo(Avg(b.Q, other)));
                for (int i = 0; i + 1 < cut.Count; i += 2) Band(cut[i], cut[i + 1]);
            }
        }
        if (windU.Count > 0) CloseWinding(windU, up, 0);
        if (windV.Count > 0) CloseWinding(windV, vp, 1);
        if (regions.Count == 0 && plain.Count == 0)
        {
            // no boundary at all (a whole sphere or torus), or only poles
            if (up > 0 && (s.Vmin != null || vp > 0))
            {
                double v0 = s.Vmin ?? 0, v1 = s.Vmax ?? vp;
                regions.Add(new List<UV> { new(0, v0), new(up, v0), new(up, v1), new(0, v1) });
            }
            else return null;
        }
        Ring ShiftInto(Ring h, List<UV> outer)
        {
            var us = up > 0 ? new[] { 0, -up, up, -2 * up, 2 * up } : new[] { 0.0 };
            var vs = vp > 0 ? new[] { 0, -vp, vp } : new[] { 0.0 };
            foreach (var a in us)
                foreach (var b in vs)
                {
                    var q = h.Q.Select(x => { var y = x; y.U += a; y.V += b; return y; }).ToList();
                    if (PointInRing(q[0], outer) || PointInRing(q[q.Count / 2], outer)) return new Ring { Q = q };
                }
            return h;
        }
        FaceMesh res;
        if (regions.Count > 0)
        {
            if (regions.Count > 1)
            {
                // separate bands, each with the holes inside it
                res = new FaceMesh();
                foreach (var r in regions)
                {
                    var rs = new List<Ring> { new() { Q = r } };
                    rs.AddRange(plain.Select(h => ShiftInto(h, r)).Where(h => PointInRing(h.Q[0], r)));
                    res.Add(Triangulate(rs, s.At, s.N, s.Du, s.Dv, null));
                }
            }
            else
            {
                var rs = new List<Ring> { new() { Q = regions[0] } };
                rs.AddRange(plain.Select(h => ShiftInto(h, regions[0])));
                res = Triangulate(rs, s.At, s.N, s.Du, s.Dv, null);
            }
        }
        else
        {
            foreach (var r in plain) r.Area = RingArea(r.Q);
            plain.Sort((a, b) => Math.Abs(b.Area).CompareTo(Math.Abs(a.Area)));
            var rs = new List<Ring> { plain[0] };
            rs.AddRange(plain.Skip(1).Select(h => ShiftInto(h, plain[0].Q)));
            res = Triangulate(rs, s.At, s.N, s.Du, s.Dv, null);
        }
        // normals came from the surface; a reversed face turns them, and its triangles, round
        if (!same && res != null)
        {
            for (int i = 0; i < res.Nrm.Count; i++) res.Nrm[i] = -res.Nrm[i];
            for (int i = 0; i + 2 < res.Idx.Count; i += 3) (res.Idx[i + 1], res.Idx[i + 2]) = (res.Idx[i + 2], res.Idx[i + 1]);
        }
        return res;
    }

    /// <summary>A loop that winds once round a periodic direction, re-sequenced to run from the seam value c to c ± per.</summary>
    static Ring CutAt(Ring r, int axis, double c, double per)
    {
        var uv = r.Q; int n = uv.Count;
        int d = axis == 0 ? r.Wu : r.Wv;   // +1 / -1
        // the run P[0..n]: the loop and its closing segment, which ends d·per further along
        var P = new List<UV>(uv) { uv[0].Plus(axis, d * per) };
        double s0 = P[0][axis];
        double cc = d > 0 ? c + Math.Ceiling((s0 - c) / per - 1e-12) * per : c + Math.Floor((s0 - c) / per + 1e-12) * per;
        int k = 0; double t = 0;
        for (int i = 0; i < n; i++)
        {
            double a = P[i][axis], b = P[i + 1][axis];
            if (a == cc) { k = i; t = 0; break; }
            if ((a - cc) * (b - cc) < 0) { k = i; t = (cc - a) / (b - a); break; }
        }
        var X = t == 0 ? P[k].With(axis, cc) : new UV(P[k].U + (P[k + 1].U - P[k].U) * t, P[k].V + (P[k + 1].V - P[k].V) * t).With(axis, cc);
        var seq = new List<UV>(n + 2) { X };
        for (int i = k + 1; i <= n; i++) seq.Add(P[i]);
        for (int i = 1; i <= k; i++) seq.Add(P[i].Plus(axis, d * per));
        seq.Add(X.Plus(axis, d * per));
        double sh = c - cc;
        for (int i = 0; i < seq.Count; i++) seq[i] = seq[i].Plus(axis, sh);
        return new Ring { Q = seq, Dir = d };
    }
}
