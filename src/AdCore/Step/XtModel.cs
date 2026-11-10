// Herald PCB Reader — Copyright (C) 2026 Johnny.ch.Tsai
// SPDX-License-Identifier: GPL-3.0-only
// Free software under the GNU General Public License version 3 (LICENSE.txt), WITHOUT ANY WARRANTY; with an
// additional permission to combine it with AvalonDock (Ms-PL), see NOTICE.txt.
namespace AdCore.Step;

/// <summary>A Parasolid part (XT text, metres) → coloured triangles in mm, through the same tessellator as STEP:
/// bodies (and their places in an assembly), faces with their loops of fins, edges sampled once along their curves
/// (lines, circles, ellipses, B-curves, trimmed curves, SP-curves, intersection charts), faces on planes, cylinders,
/// cones, spheres, tori, B-surfaces, offset and swept surfaces; other surfaces (blends) are filled flat from their
/// boundary. Colours come from the SDL/TYSA_COLOUR attributes of faces and bodies.</summary>
public static class XtModel
{
    const double U = 1000;   // metres → mm

    public static StepMesh Tessellate(string text)
    {
        var x = XtFile.Parse(text);
        if (x == null) return null;
        var m = new Builder(x, 0.03, 20).Run();
        if (m.Triangles > 8000) { var c = new Builder(x, 0.075, 35).Run(); if (c.Triangles < m.Triangles) m = c; }
        return m.Triangles > 0 ? m : null;
    }

    sealed class Builder
    {
        readonly XtFile _x;
        readonly StepTess _ts;
        readonly Dictionary<int, StepTess.Surf> _surfs = new();
        readonly Dictionary<int, (double us, double vs)> _scale = new();
        readonly Dictionary<int, List<V3>> _edges = new();
        readonly Dictionary<int, (float, float, float)> _colour = new();

        public Builder(XtFile x, double tol, double ang) { _x = x; _ts = new StepTess(tol, ang); }
        XtNode N(int i) => _x[i];

        public StepMesh Run()
        {
            Colours();
            var groups = new Dictionary<(int, int, int, bool), (List<float> pos, List<float> nrm, List<int> idx, (float r, float g, float b) c)>();
            var mesh = new StepMesh();
            int tris = 0;
            // faces by body (through their shells)
            var faces = new Dictionary<int, List<XtNode>>();
            foreach (var f in _x.Nodes.Values)
            {
                if (f.Type != 14) continue;
                int body = N(f.P("shell"))?.P("body") ?? 0;
                if (!faces.TryGetValue(body, out var l)) faces[body] = l = new();
                l.Add(f);
            }
            foreach (var (body, list) in faces)
            {
                (float, float, float)? bodyCol = _colour.TryGetValue(body, out var bc) ? bc : null;
                foreach (var m in Placements(body))
                    foreach (var f in list)
                    {
                        var fm = Face(f);
                        if (fm == null) continue;
                        (float r, float g, float b)? col = _colour.TryGetValue(f.Index, out var fc) ? fc : bodyCol;
                        var cc = col ?? (0.6f, 0.6f, 0.62f);
                        var key = ((int)Math.Round(cc.r * 255), (int)Math.Round(cc.g * 255), (int)Math.Round(cc.b * 255), col != null);
                        if (!groups.TryGetValue(key, out var gr)) groups[key] = gr = (new(), new(), new(), cc);
                        int o = gr.pos.Count / 3;
                        foreach (var p in fm.Pos) { var q = m.Ap(p); gr.pos.Add((float)q.X); gr.pos.Add((float)q.Y); gr.pos.Add((float)q.Z); }
                        foreach (var n in fm.Nrm) { var q = m.ApV(n); gr.nrm.Add((float)q.X); gr.nrm.Add((float)q.Y); gr.nrm.Add((float)q.Z); }
                        foreach (var i in fm.Idx) gr.idx.Add(i + o);
                        tris += fm.Idx.Count / 3;
                        if (tris > 600_000) break;
                    }
            }
            foreach (var (key, g) in groups)
                mesh.Parts.Add(new StepMeshPart { R = g.c.r, G = g.c.g, B = g.c.b, HasColour = key.Item4, Pos = g.pos.ToArray(), Nrm = g.nrm.ToArray(), Idx = g.idx.ToArray() });
            mesh.Faces = _ts.Faces; mesh.Failed = _ts.Failed; mesh.Triangles = tris;
            return mesh;
        }

        /// <summary>SDL/TYSA_COLOUR attributes: owner → (r, g, b).</summary>
        void Colours()
        {
            var defs = new HashSet<int>();
            foreach (var n in _x.Nodes.Values)
                if (n.Type == 80 && N(n.P("identifier"))?.S("string") is string id && id.Contains("COLOUR", StringComparison.OrdinalIgnoreCase)) defs.Add(n.Index);
            foreach (var n in _x.Nodes.Values)
            {
                if (n.Type != 81 || !defs.Contains(n.P("definition"))) continue;
                var fields = n.Ps("fields"); if (fields == null || fields.Length == 0) continue;
                var v = N(fields[0])?.Ds("values");
                if (v != null && v.Length >= 3) _colour[n.P("owner")] = ((float)Math.Clamp(v[0], 0, 1), (float)Math.Clamp(v[1], 0, 1), (float)Math.Clamp(v[2], 0, 1));
            }
        }

        /// <summary>Where a body goes: through the instances that use it (and the instances of their assemblies).</summary>
        List<Xf> Placements(int part, int depth = 0)
        {
            var o = new List<Xf>();
            if (depth < 10)
                foreach (var n in _x.Nodes.Values)
                {
                    if (n.Type != 11 || n.P("part") != part) continue;
                    var t = Transform(N(n.P("transform")));
                    foreach (var up in Placements(n.P("assembly"), depth + 1)) o.Add(Xf.Mul(up, t));
                }
            if (o.Count == 0) o.Add(Xf.I());
            return o;
        }
        static Xf Transform(XtNode t)
        {
            if (t == null) return Xf.I();
            var r = t.Ds("rotation_matrix"); var tr = t.Ds("translation_vector");
            if (r == null || r.Length < 9) return Xf.I();
            double sc = double.IsNaN(t.D("scale")) || t.D("scale") == 0 ? 1 : t.D("scale");
            return Xf.Of(r[0] * sc, r[1] * sc, r[2] * sc, (tr?[0] ?? 0) * U, r[3] * sc, r[4] * sc, r[5] * sc, (tr?[1] ?? 0) * U, r[6] * sc, r[7] * sc, r[8] * sc, (tr?[2] ?? 0) * U);
        }

        FaceMesh Face(XtNode f)
        {
            var sn = N(f.P("surface"));
            var s = sn != null ? Surf(sn.Index) : null;
            bool same = (f.C("sense") != '-') == (sn == null || sn.C("sense") != '-');
            var loops = new List<List<V3>>();
            for (var l = N(f.P("loop")); l != null && loops.Count < 10000; l = N(l.P("next")))
            {
                var pts = LoopPts(l);
                if (pts != null && pts.Count >= 2) loops.Add(pts);
                if (l.P("next") == l.Index) break;
            }
            if (loops.Count == 0) return null;
            // B-surface vertices: v fastest is the documented order; when the boundary does not lie on the surface, try u fastest
            if (s != null && s.Kind == "bspline" && sn.Type == 124 && Off(s, loops) > 0.05)
            {
                var t = BSurface(N(sn.P("nurbs")), true);
                if (t != null && Off(t, loops) < Off(s, loops)) { _surfs[sn.Index] = t; s = t; }
            }
            // a surface not understood: flat, from the boundary
            return _ts.FaceFromLoops(s, loops, s != null ? same : true);
        }

        List<V3> LoopPts(XtNode loop)
        {
            var start = N(loop.P("fin")); if (start == null) return null;
            var o = new List<V3>();
            var fin = start;
            for (int g = 0; g < 100000 && fin != null; g++)
            {
                var pts = EdgePts(fin.P("edge"));
                if (pts != null)
                {
                    bool rev = fin.C("sense") == '-';
                    for (int i = 0; i < pts.Count; i++)
                    {
                        var q = pts[rev ? pts.Count - 1 - i : i];
                        if (o.Count > 0 && i == 0 && (o[^1] - q).Len < 1e-6) continue;
                        o.Add(q);
                    }
                }
                fin = N(fin.P("forward"));
                if (fin == null || fin.Index == start.Index) break;
            }
            if (o.Count > 1 && (o[0] - o[^1]).Len < 1e-6) o.RemoveAt(o.Count - 1);
            return o;
        }

        V3? VertexPt(int v) { var vx = N(v); var pt = vx != null ? N(vx.P("point")) : null; return pt != null ? pt.Vec("pvec") * U : null; }

        /// <summary>Points along an edge from its start to its end vertex: the end vertex is that of the edge's
        /// (positive) fin, the start that of the fin's partner.</summary>
        List<V3> EdgePts(int e)
        {
            if (_edges.TryGetValue(e, out var c)) return c;
            _edges[e] = null;
            var edge = N(e); if (edge == null) return null;
            var fin = N(edge.P("fin")); var other = fin != null ? N(fin.P("other")) : null;
            V3? pe = fin != null ? VertexPt(fin.P("vertex")) : null, ps = other != null ? VertexPt(other.P("vertex")) : null;
            int curve = edge.P("curve");
            if (curve == 0 && fin != null) curve = fin.P("curve");
            if (curve == 0 && other != null) curve = other.P("curve");
            var pts = CurvePts(N(curve), ps, pe);
            if (pts == null && ps is V3 a && pe is V3 b) pts = new List<V3> { a, b };
            _edges[e] = pts;
            return pts;
        }

        List<V3> CurvePts(XtNode cn, V3? ps0, V3? pe0)
        {
            if (cn == null) return null;
            bool ring = ps0 == null || pe0 == null;
            if (cn.Type == 133)   // trimmed curve: the basis between parm_1 and parm_2 (always in that direction)
            {
                var basis = N(cn.P("basis_curve")); var bc = Curve(basis); if (bc == null) return null;
                double s = basis.Type == 30 ? U : 1;
                var pts = _ts.SampleRange(bc, cn.D("parm_1") * s, cn.D("parm_2") * s);
                return Finish(pts, ps0, pe0);
            }
            if (cn.Type == 38)   // intersection: the chart's points
            {
                var h = N(cn.P("chart"))?.Ds("hvec"); if (h == null || h.Length < 6) return null;
                var pts = new List<V3>(); for (int i = 0; i + 2 < h.Length; i += 3) pts.Add(new V3(h[i], h[i + 1], h[i + 2]) * U);
                return Finish(pts, ps0, pe0);
            }
            var cv = Curve(cn); if (cv == null) return null;
            bool same = cn.C("sense") != '-';
            V3 ps, pe;
            if (ring)
            {
                // a ring edge (no vertices): the whole closed curve, from its parameter start
                ps = pe = cv switch { StepTess.ConicC cc => cc.At(0), StepTess.BsplC b => b.Bc.At(b.Bc.T0), StepTess.SpC sp => sp.At(sp.B.T0), _ => default };
            }
            else { ps = ps0.Value; pe = pe0.Value; }
            bool closed = ring || (ps - pe).Len < 1e-6;
            if (cv is StepTess.LineC) return new List<V3> { ps, pe };
            return _ts.SampleCurve(cv, ps, pe, same, closed);
        }

        /// <summary>Points sampled along a curve, turned to run from ps to pe and ending on the exact vertices.</summary>
        List<V3> Finish(List<V3> pts, V3? ps, V3? pe)
        {
            if (pts.Count < 2) return null;
            if (ps is V3 a && pe is V3 b)
            {
                if ((pts[0] - a).Len + (pts[^1] - b).Len > (pts[0] - b).Len + (pts[^1] - a).Len) pts.Reverse();
                pts[0] = a; pts[^1] = b;
            }
            return pts;
        }

        StepTess.Curve Curve(XtNode n)
        {
            if (n == null) return null;
            switch (n.Type)
            {
                case 30: return new StepTess.LineC { P0 = n.Vec("pvec") * U, D = n.Vec("direction").Norm() };
                case 31: { double r = n.D("radius") * U; return r > 0 ? new StepTess.ConicC { F = Frame.Make(n.Vec("centre") * U, n.Vec("normal"), n.Vec("x_axis")), A = r, B = r, R = r } : null; }
                case 32: { double a = n.D("major_radius") * U, b = n.D("minor_radius") * U; return a > 0 && b > 0 ? new StepTess.ConicC { F = Frame.Make(n.Vec("centre") * U, n.Vec("normal"), n.Vec("x_axis")), A = a, B = b, R = Math.Max(a, b) } : null; }
                case 134: { var bc = BCurve(N(n.P("nurbs")), true); return bc != null ? new StepTess.BsplC { Bc = bc } : null; }
                case 137:
                    {
                        var sn = N(n.P("surface")); var s = sn != null ? Surf(sn.Index) : null;
                        var b2 = N(n.P("b_curve")); var bc = b2 != null ? BCurve(N(b2.P("nurbs")), false) : null;
                        if (s == null || bc == null) return null;
                        var (us, vs) = _scale.TryGetValue(sn.Index, out var sc) ? sc : (1.0, 1.0);
                        return new StepTess.SpC { S = s, B = bc, Us = us, Vs = vs };
                    }
                case 133: return Curve(N(n.P("basis_curve")));
                default: return null;
            }
        }

        /// <summary>A NURBS curve: 3D (positions in metres → mm) or 2D (surface parameters, as they are).</summary>
        StepTess.BCurve BCurve(XtNode nc, bool space)
        {
            if (nc == null) return null;
            int deg = nc.P("degree"), nv = nc.P("n_vertices"), dim = nc.P("vertex_dim");
            bool rational = nc.C("rational") == 'T';
            var v = N(nc.P("bspline_vertices"))?.Ds("vertices"); var kn = N(nc.P("knots"))?.Ds("knots"); var mu = N(nc.P("knot_mult"))?.Ps("mult");
            if (v == null || kn == null || mu == null || dim < 2 || v.Length < nv * dim) return null;
            int cd = rational ? dim - 1 : dim;
            var pts = new V3[nv]; double[] w = rational ? new double[nv] : null;
            for (int i = 0; i < nv; i++)
            {
                double wi = rational ? v[i * dim + dim - 1] : 1; if (rational) w[i] = wi; if (wi == 0) wi = 1;
                double x = v[i * dim] / wi, y = cd > 1 ? v[i * dim + 1] / wi : 0, z = cd > 2 ? v[i * dim + 2] / wi : 0;
                pts[i] = space ? new V3(x, y, z) * U : new V3(x, y, z);
            }
            return StepTess.MakeBCurve(deg, pts, StepTess.Expand(kn, mu), w);
        }

        StepTess.Surf Surf(int idx)
        {
            if (_surfs.TryGetValue(idx, out var s)) return s;
            _surfs[idx] = null;   // a loop of references ends here
            try { s = MakeSurf(N(idx)); } catch (Exception) { s = null; }
            _surfs[idx] = s;
            return s;
        }

        StepTess.Surf MakeSurf(XtNode n)
        {
            if (n == null) return null;
            switch (n.Type)
            {
                case 50: _scale[n.Index] = (U, U); return _ts.PlaneS(Frame.Make(n.Vec("pvec") * U, n.Vec("normal"), n.Vec("x_axis")));
                case 51: _scale[n.Index] = (1, U); return _ts.CylinderS(Frame.Make(n.Vec("pvec") * U, n.Vec("axis"), n.Vec("x_axis")), n.D("radius") * U);
                case 52: _scale[n.Index] = (1, U); return _ts.XtConeS(n.Vec("pvec") * U, n.Vec("axis"), n.Vec("x_axis"), n.D("radius") * U, n.D("sin_half_angle"), n.D("cos_half_angle"));
                case 53: return _ts.SphereS(Frame.Make(n.Vec("centre") * U, n.Vec("axis"), n.Vec("x_axis")), n.D("radius") * U);
                case 54: return _ts.TorusS(Frame.Make(n.Vec("centre") * U, n.Vec("axis"), n.Vec("x_axis")), n.D("major_radius") * U, n.D("minor_radius") * U);
                case 124: return BSurface(N(n.P("nurbs")));
                case 60:
                    {
                        // offset surface: the underlying surface moved along its natural normal
                        var bn = N(n.P("surface")); var b = bn != null ? Surf(bn.Index) : null; if (b == null || b.Kind == "plane") return OffsetPlane(bn, n);
                        double d = n.D("offset") * U * (bn.C("sense") == '-' ? -1 : 1);
                        if (_scale.TryGetValue(bn.Index, out var sc)) _scale[n.Index] = sc;
                        return new StepTess.Surf
                        {
                            Kind = b.Kind + "-offset", F = b.F, Up = b.Up, Vp = b.Vp, Du = b.Du, Dv = b.Dv, Umin = b.Umin, Umax = b.Umax, Vmin = b.Vmin, Vmax = b.Vmax, VApex = null,
                            UV = b.UV, N = b.N, At = (u, v) => b.At(u, v) + b.N(u, v).Norm() * d,
                        };
                    }
                case 67:
                    {
                        var sec = Curve(N(n.P("section"))); var d = n.Vec("sweep") * U;
                        return sec != null && d.Len > 1e-9 ? _ts.Extrusion(sec, d) : null;
                    }
                default: return null;
            }
        }

        StepTess.Surf OffsetPlane(XtNode bn, XtNode n)
        {
            if (bn == null || bn.Type != 50) return null;
            double d = n.D("offset") * U * (bn.C("sense") == '-' ? -1 : 1);
            var nrm = bn.Vec("normal").Norm();
            _scale[n.Index] = (U, U);
            return _ts.PlaneS(Frame.Make(bn.Vec("pvec") * U + nrm * d, nrm, bn.Vec("x_axis")));
        }

        /// <summary>How far boundary points lie from a surface (a few of them).</summary>
        static double Off(StepTess.Surf s, List<List<V3>> loops)
        {
            double worst = 0; int n = 0;
            foreach (var l in loops) for (int i = 0; i < l.Count && n < 12; i += Math.Max(1, l.Count / 4), n++) { var (u, v) = s.UV(l[i]); worst = Math.Max(worst, (s.At(u, v) - l[i]).Len); }
            return worst;
        }

        StepTess.Surf BSurface(XtNode ns, bool uFastest = false)
        {
            if (ns == null) return null;
            int p = ns.P("u_degree"), q = ns.P("v_degree"), nu = ns.P("n_u_vertices"), nv = ns.P("n_v_vertices"), dim = ns.P("vertex_dim");
            bool rational = ns.C("rational") == 'T';
            var v = N(ns.P("bspline_vertices"))?.Ds("vertices");
            var uk = N(ns.P("u_knots"))?.Ds("knots"); var vk = N(ns.P("v_knots"))?.Ds("knots");
            var um = N(ns.P("u_knot_mult"))?.Ps("mult"); var vm = N(ns.P("v_knot_mult"))?.Ps("mult");
            if (v == null || uk == null || vk == null || um == null || vm == null || v.Length < nu * nv * dim || dim < 3) return null;
            // vertices: v varies fastest (index = i·nv + j, i along u)
            var P = new V3[nu, nv]; double[,] W = rational ? new double[nu, nv] : null;
            for (int i = 0; i < nu; i++)
                for (int j = 0; j < nv; j++)
                {
                    int k = (uFastest ? j * nu + i : i * nv + j) * dim;
                    double w = rational ? v[k + dim - 1] : 1; if (rational) W[i, j] = w; if (w == 0) w = 1;
                    P[i, j] = new V3(v[k] / w, v[k + 1] / w, v[k + 2] / w) * U;
                }
            return _ts.BSplineS(p, q, P, StepTess.Expand(uk, um), StepTess.Expand(vk, vm), W);
        }
    }
}
