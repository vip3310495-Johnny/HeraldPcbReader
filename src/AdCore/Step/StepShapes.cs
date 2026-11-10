// Herald PCB Reader — Copyright (C) 2026 Johnny.ch.Tsai
// SPDX-License-Identifier: GPL-3.0-only
// Free software under the GNU General Public License version 3 (LICENSE.txt), WITHOUT ANY WARRANTY; with an
// additional permission to combine it with AvalonDock (Ms-PL), see NOTICE.txt.
using static AdCore.Step.StepFile;

namespace AdCore.Step;

/// <summary>What a STEP file holds as solids: every solid / shell model with its placement in the top assembly
/// (sub-assemblies and mapped items followed), the faces of each, and the colours given to faces and solids.</summary>
public static class StepShapes
{
    static readonly string[] RepTypes =
    {
        "SHAPE_REPRESENTATION", "ADVANCED_BREP_SHAPE_REPRESENTATION", "MANIFOLD_SURFACE_SHAPE_REPRESENTATION", "FACETED_BREP_SHAPE_REPRESENTATION",
        "GEOMETRICALLY_BOUNDED_SURFACE_SHAPE_REPRESENTATION", "GEOMETRICALLY_BOUNDED_WIREFRAME_SHAPE_REPRESENTATION", "EDGE_BASED_WIREFRAME_SHAPE_REPRESENTATION",
    };
    static readonly string[] ShapeTypes = { "MANIFOLD_SOLID_BREP", "BREP_WITH_VOIDS", "SHELL_BASED_SURFACE_MODEL", "FACETED_BREP" };

    /// <summary>(solid or shell model id, transform to the top assembly) for every placed occurrence.</summary>
    public static List<(int Shape, Xf M)> Instances(StepFile st)
    {
        // representations and the items in them
        var reps = new Dictionary<int, HashSet<int>>();
        HashSet<int> Items(List<object> l) => new(l.OfType<Ref>().Select(r => r.Id));
        foreach (var t in RepTypes) foreach (var id in st.Ids(t)) reps[id] = Items(List(Arg(st.Get(id), 1)));
        foreach (var id in st.Ids("COMPLEX"))
        {
            var e = st.Get(id);
            if (!(e.Has("SHAPE_REPRESENTATION") || e.Has("ADVANCED_BREP_SHAPE_REPRESENTATION"))) continue;
            var a = e.Part("REPRESENTATION") is { Count: > 1 } r ? r : e.Part("SHAPE_REPRESENTATION");
            if (a != null && a.Count > 1) reps[id] = Items(List(a[1]));
        }

        // representations linked without a transformation are one group (union-find)
        var par = new Dictionary<int, int>();
        int Find(int a) { int g = 0; while (par.TryGetValue(a, out var p) && p != a && g++ < 100000) a = p; return a; }
        void Union(int a, int b) { a = Find(a); b = Find(b); if (a != b) par[a] = b; }
        foreach (var t in new[] { "SHAPE_REPRESENTATION_RELATIONSHIP", "REPRESENTATION_RELATIONSHIP" })
            foreach (var id in st.Ids(t)) { var e = st.Get(id); int a = RefId(Arg(e, 2)), b = RefId(Arg(e, 3)); if (a != 0 && b != 0) Union(a, b); }

        // which side is the parent: from the product structure (CDSR → NAUO relating / related product definitions)
        var pdRep = new Dictionary<int, int>();
        foreach (var id in st.Ids("SHAPE_DEFINITION_REPRESENTATION"))
        {
            var e = st.Get(id); var pds = st.Get(Arg(e, 0)); int rep = RefId(Arg(e, 1));
            int pd = RefId(Arg(pds, 2)); if (pd != 0 && rep != 0) pdRep[pd] = rep;
        }
        var relDir = new Dictionary<int, (int parent, int child)>();
        foreach (var id in st.Ids("CONTEXT_DEPENDENT_SHAPE_REPRESENTATION"))
        {
            var e = st.Get(id); int rel = RefId(Arg(e, 0)); var pds = st.Get(Arg(e, 1)); var nauo = st.Get(Arg(pds, 2));
            if (rel == 0 || nauo == null) continue;
            pdRep.TryGetValue(RefId(Arg(nauo, 3)), out int parent); pdRep.TryGetValue(RefId(Arg(nauo, 4)), out int child);
            if (parent != 0 || child != 0) relDir[rel] = (parent, child);
        }

        var links = new List<(int child, int parent, Xf m)>();
        foreach (var id in st.Ids("COMPLEX"))
        {
            var e = st.Get(id);
            var rr = e.Part("REPRESENTATION_RELATIONSHIP"); if (rr == null) continue;
            int rep1 = RefId(Arg(rr, 2)), rep2 = RefId(Arg(rr, 3)); if (rep1 == 0 || rep2 == 0) continue;
            var t = st.Get(Arg(e.Part("REPRESENTATION_RELATIONSHIP_WITH_TRANSFORMATION"), 0));
            if (t == null || t.Type != "ITEM_DEFINED_TRANSFORMATION") { Union(rep1, rep2); continue; }
            // the standard has rep_1 as the component; some exporters (SolidWorks) write the assembly first
            int child = rep1, parent = rep2;
            if (relDir.TryGetValue(id, out var d))
            {
                int f1 = Find(rep1), f2 = Find(rep2);
                if ((d.child != 0 && Find(d.child) == f2) || (d.parent != 0 && Find(d.parent) == f1)) { child = rep2; parent = rep1; }
            }
            // item_1 / item_2: the one placed in the parent representation is where the component's frame goes
            int i1 = RefId(Arg(t, 2)), i2 = RefId(Arg(t, 3));
            int pi = parent == rep2 ? i2 : i1, ci = parent == rep2 ? i1 : i2;
            if (reps.TryGetValue(parent, out var rp) && reps.TryGetValue(child, out var rc) && !rp.Contains(pi) && rp.Contains(ci) && rc.Contains(pi)) (pi, ci) = (ci, pi);
            var m = Xf.Mul(Xf.FromFrame(st.Placement(new Ref(pi))), Xf.FromFrame(st.Placement(new Ref(ci))).InvRigid());
            links.Add((child, parent, m));
        }
        // mapped items: REPRESENTATION_MAP(origin, rep) placed at MAPPED_ITEM's target, inside the representation holding it
        foreach (var id in st.Ids("MAPPED_ITEM"))
        {
            var e = st.Get(id); var map = st.Get(Arg(e, 1)); if (map == null) continue;
            int mrep = RefId(Arg(map, 1)); if (mrep == 0) continue;
            var m = Xf.Mul(Xf.FromFrame(st.Placement(Arg(e, 2))), Xf.FromFrame(st.Placement(Arg(map, 0))).InvRigid());
            foreach (var (rid, items) in reps) if (items.Contains(id)) links.Add((mrep, rid, m));
        }

        var childLinks = new Dictionary<int, List<(int p, Xf m)>>();
        foreach (var (c, p, m) in links)
        {
            int g = Find(c);
            if (!childLinks.TryGetValue(g, out var l)) childLinks[g] = l = new();
            l.Add((Find(p), m));
        }
        var memo = new Dictionary<int, List<Xf>>();
        List<Xf> Inst(int g, int depth)
        {
            if (memo.TryGetValue(g, out var r)) return r;
            if (depth > 30) return new List<Xf> { Xf.I() };
            memo[g] = new List<Xf> { Xf.I() };   // a loop in a damaged file ends here
            List<Xf> o;
            if (!childLinks.TryGetValue(g, out var ls) || ls.Count == 0) o = new List<Xf> { Xf.I() };
            else
            {
                o = new();
                foreach (var (p, m) in ls) foreach (var pm in Inst(p, depth + 1)) { o.Add(Xf.Mul(pm, m)); if (o.Count >= 2000) break; }
            }
            memo[g] = o;
            return o;
        }

        var res = new List<(int, Xf)>();
        var seen = new HashSet<string>();
        foreach (var (rid, items) in reps)
            foreach (var it in items)
            {
                var e = st.Get(it); if (e == null) continue;
                if (!(ShapeTypes.Contains(e.Type) || e.Parts != null && ShapeTypes.Any(e.Has))) continue;
                foreach (var m in Inst(Find(rid), 0)) if (seen.Add(it + ":" + m.Key())) res.Add((it, m));
            }
        return res;
    }

    /// <summary>Face ids of a solid / shell model, and the shells (colours are sometimes given to a shell).</summary>
    public static List<int> Faces(StepFile st, int id, List<int> shells = null)
    {
        var e = st.Get(id); var o = new List<int>();
        void Shell(object sh)
        {
            var s = st.Get(sh); if (s == null) return;
            shells?.Add(s.Id);
            if (s.Type == "ORIENTED_CLOSED_SHELL") { var b = st.Get(Arg(s, 2)); if (b != null) { shells?.Add(b.Id); foreach (var f in List(Arg(b, 1))) if (f is Ref r) o.Add(r.Id); } return; }
            foreach (var f in List(Arg(s, 1))) if (f is Ref r) o.Add(r.Id);
        }
        if (e == null) return o;
        switch (e.Type)
        {
            case "MANIFOLD_SOLID_BREP": case "FACETED_BREP": Shell(Arg(e, 1)); break;
            case "BREP_WITH_VOIDS": Shell(Arg(e, 1)); foreach (var v in List(Arg(e, 2))) Shell(v); break;
            case "SHELL_BASED_SURFACE_MODEL": foreach (var sh in List(Arg(e, 1))) Shell(sh); break;
            case "COMPLEX": { var a = e.Part("MANIFOLD_SOLID_BREP") ?? e.Part("BREP_WITH_VOIDS"); if (a != null) Shell(Arg(a, 1)); break; }
        }
        return o;
    }

    static readonly Dictionary<string, (float, float, float)> Named = new()
    {
        ["red"] = (1, 0, 0), ["green"] = (0, 1, 0), ["blue"] = (0, 0, 1), ["yellow"] = (1, 1, 0),
        ["magenta"] = (1, 0, 1), ["cyan"] = (0, 1, 1), ["black"] = (0.1f, 0.1f, 0.1f), ["white"] = (1, 1, 1),
    };

    /// <summary>Item (face, solid, shell) → colour (0..1), from STYLED_ITEM / OVER_RIDING_STYLED_ITEM.</summary>
    public static Dictionary<int, (float R, float G, float B)> Colours(StepFile st)
    {
        var col = new Dictionary<int, (float, float, float)>();
        (float, float, float)? Colour(object x)
        {
            var c = st.Get(x); if (c == null) return null;
            if (c.Type == "COLOUR_RGB") return ((float)Num(Arg(c, 1)), (float)Num(Arg(c, 2)), (float)Num(Arg(c, 3)));
            if (c.Type == "DRAUGHTING_PRE_DEFINED_COLOUR" && Named.TryGetValue(((Arg(c, 0) as string) ?? "").ToLowerInvariant(), out var n)) return n;
            return null;
        }
        (float, float, float)? FromStyle(object s, int depth)
        {
            if (depth > 8) return null;
            var e = st.Get(s); if (e == null) return null;
            switch (e.Type)
            {
                case "PRESENTATION_STYLE_ASSIGNMENT": foreach (var x in List(Arg(e, 0))) if (FromStyle(x, depth + 1) is { } a) return a; return null;
                case "SURFACE_STYLE_USAGE": return FromStyle(Arg(e, 1), depth + 1);
                case "SURFACE_SIDE_STYLE": foreach (var x in List(Arg(e, 1))) if (FromStyle(x, depth + 1) is { } b) return b; return null;
                case "SURFACE_STYLE_FILL_AREA": return FromStyle(Arg(e, 0), depth + 1);
                case "FILL_AREA_STYLE": foreach (var x in List(Arg(e, 1))) if (FromStyle(x, depth + 1) is { } c) return c; return null;
                case "FILL_AREA_STYLE_COLOUR": return Colour(Arg(e, 1));
                default: return null;
            }
        }
        foreach (var t in new[] { "STYLED_ITEM", "OVER_RIDING_STYLED_ITEM" })
            foreach (var id in st.Ids(t))
            {
                var e = st.Get(id); (float, float, float)? c = null;
                foreach (var s in List(Arg(e, 1))) { c = FromStyle(s, 0); if (c != null) break; }
                int item = RefId(Arg(e, 2));
                if (c is { } v && item != 0 && (t == "OVER_RIDING_STYLED_ITEM" || !col.ContainsKey(item))) col[item] = v;
            }
        return col;
    }
}
