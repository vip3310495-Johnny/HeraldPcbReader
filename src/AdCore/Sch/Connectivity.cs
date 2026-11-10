// Herald PCB Reader — Copyright (C) 2026 Johnny.ch.Tsai
// SPDX-License-Identifier: GPL-3.0-only
// Free software under the GNU General Public License version 3 (LICENSE.txt), WITHOUT ANY WARRANTY; with an
// additional permission to combine it with AvalonDock (Ms-PL), see NOTICE.txt.
using static AdCore.Util.TextUtil;

namespace AdCore.Sch;

public static class Connectivity
{
    sealed class UF
    {
        readonly List<int> _p = new();
        public int Make() { _p.Add(_p.Count); return _p.Count - 1; }
        public int Find(int a) { while (_p[a] != a) { _p[a] = _p[_p[a]]; a = _p[a]; } return a; }
        public void Union(int a, int b) { a = Find(a); b = Find(b); if (a != b) _p[b] = a; }
    }

    public static bool OnSeg(SchWire w, double x, double y, double e)
    {
        double dx = w.X2 - w.X1, dy = w.Y2 - w.Y1, l2 = dx * dx + dy * dy;
        if (l2 < 1e-12) return Math.Abs(x - w.X1) < e && Math.Abs(y - w.Y1) < e;
        double t = ((x - w.X1) * dx + (y - w.Y1) * dy) / l2;
        if (t < -1e-6 || t > 1 + 1e-6) return false;
        double px = w.X1 + t * dx - x, py = w.Y1 + t * dy - y;
        return px * px + py * py < e * e;
    }

    static int Prio(LabelKind k) => k switch { LabelKind.Global => 4, LabelKind.Port => 3, LabelKind.Local => 3, LabelKind.Hier => 2, _ => 1 };
    public static string KindKey(LabelKind k) => k switch { LabelKind.Global => "global", LabelKind.Port => "port", LabelKind.Local => "local", LabelKind.Hier => "hier", _ => "sheetpin" };

    /// <summary>Assign net names to every wire / pin / label of a sheet (port of v1 computeNets).</summary>
    public static void ComputeNets(SchSheet sh, string style)
    {
        var uf = new UF();
        string Key(double x, double y) => Math.Round(x * 200) + ":" + Math.Round(y * 200);
        var pts = new Dictionary<string, int>();
        int P(double x, double y) { var k = Key(x, y); if (!pts.TryGetValue(k, out var n)) { n = uf.Make(); pts[k] = n; } return n; }
        var W = sh.Wires.Where(w => w.Kind == WireKind.Wire).ToList();
        const double B = 5;
        var grid = new Dictionary<(int, int), List<SchWire>>();
        foreach (var w in W)
        {
            w.Node = uf.Make(); uf.Union(w.Node, P(w.X1, w.Y1)); uf.Union(w.Node, P(w.X2, w.Y2));
            int i0 = (int)Math.Floor(Math.Min(w.X1, w.X2) / B), i1 = (int)Math.Floor(Math.Max(w.X1, w.X2) / B);
            int j0 = (int)Math.Floor(Math.Min(w.Y1, w.Y2) / B), j1 = (int)Math.Floor(Math.Max(w.Y1, w.Y2) / B);
            for (int i = i0; i <= i1; i++) for (int j = j0; j <= j1; j++)
                {
                    if (!grid.TryGetValue((i, j), out var l)) grid[(i, j)] = l = new();
                    l.Add(w);
                }
        }
        IEnumerable<SchWire> At(double x, double y) =>
            grid.TryGetValue(((int)Math.Floor(x / B), (int)Math.Floor(y / B)), out var l) ? l.Where(w => OnSeg(w, x, y, 0.006)) : Enumerable.Empty<SchWire>();
        foreach (var w in W)
        {
            foreach (var o in At(w.X1, w.Y1)) uf.Union(w.Node, o.Node);
            foreach (var o in At(w.X2, w.Y2)) uf.Union(w.Node, o.Node);
        }
        foreach (var j in sh.Junctions) { j.Node = P(j.X, j.Y); foreach (var o in At(j.X, j.Y)) uf.Union(j.Node, o.Node); }
        var allPins = new List<(SchComponent c, SchPin p)>();
        foreach (var c in sh.Comps) foreach (var p in c.Pins) { p.Node = P(p.X, p.Y); foreach (var o in At(p.X, p.Y)) uf.Union(p.Node, o.Node); allPins.Add((c, p)); }
        foreach (var l in sh.Labels) { l.Node = P(l.X, l.Y); foreach (var o in At(l.X, l.Y)) uf.Union(l.Node, o.Node); }

        bool ci = style == "altium";
        var named = new Dictionary<string, int>();
        int NM(string k) { if (ci) k = k.ToLowerInvariant(); if (!named.TryGetValue(k, out var n)) { n = uf.Make(); named[k] = n; } return n; }
        var nameOf = new List<(int node, int pr, string nm)>();
        foreach (var l in sh.Labels)
        {
            var nm = PlainText(l.Text); if (nm == "") continue;
            if (l.Kind != LabelKind.SheetPin) uf.Union(NM((l.Kind == LabelKind.Global ? "G:" : "L:") + nm), l.Node);
            nameOf.Add((l.Node, Prio(l.Kind), nm));
        }
        foreach (var (c, p) in allPins)
        {
            if (c.Power) { var nm = PlainText(c.Value); if (nm != "" && p.Type == "power_in") { uf.Union(NM("G:" + nm), p.Node); nameOf.Add((p.Node, 5, nm)); } }
            else if (p.Hidden && (p.GNet != "" || (p.Type == "power_in" && p.Name != ""))) { var g = p.GNet != "" ? p.GNet : p.Name; uf.Union(NM("G:" + g), p.Node); nameOf.Add((p.Node, 4, g)); }
        }

        var groups = new Dictionary<int, (List<(SchComponent c, SchPin p)> pins, List<SchWire> wires, List<SchJunction> jn, List<SchLabel> labels, List<(int pr, string nm)> names)>();
        (List<(SchComponent, SchPin)> pins, List<SchWire> wires, List<SchJunction> jn, List<SchLabel> labels, List<(int pr, string nm)> names) G(int r)
        {
            if (!groups.TryGetValue(r, out var g)) groups[r] = g = (new(), new(), new(), new(), new());
            return g;
        }
        foreach (var w in W) G(uf.Find(w.Node)).wires.Add(w);
        foreach (var j in sh.Junctions) G(uf.Find(j.Node)).jn.Add(j);
        foreach (var l in sh.Labels) G(uf.Find(l.Node)).labels.Add(l);
        foreach (var cp in allPins) G(uf.Find(cp.p.Node)).pins.Add(cp);
        foreach (var (n, pr, nm) in nameOf) G(uf.Find(n)).names.Add((pr, nm));

        var nc = new HashSet<string>(sh.NoConnects.Select(q => Key(q.X, q.Y)));
        var used = new HashSet<string>();
        foreach (var (_, p) in allPins) if (nc.Contains(Key(p.X, p.Y))) p.NcFlag = true;
        var ncRoots = new HashSet<int>();
        foreach (var q in sh.NoConnects)
        {
            if (pts.TryGetValue(Key(q.X, q.Y), out var n)) ncRoots.Add(uf.Find(n));
            foreach (var w in At(q.X, q.Y)) ncRoots.Add(uf.Find(w.Node));
        }
        sh.Nets.Clear(); sh.UnconnectedPins.Clear();
        foreach (var (root, g) in groups)
        {
            var real = g.pins.Where(x => !x.Item1.Power).ToList();
            bool gnc = ncRoots.Contains(root);
            if (gnc) foreach (var (_, p) in real) p.NcFlag = true;
            if (g.wires.Count == 0 && g.names.Count == 0 && g.pins.Count == 1)
            {
                var (c, p) = g.pins[0];
                if (!c.Power && !p.Hidden && p.Type != "no_connect" && !nc.Contains(Key(p.X, p.Y))) sh.UnconnectedPins.Add(new Unconnected { Ref = c.Ref, Pin = p.Num, Name = p.Name });
                continue;
            }
            if (real.Count == 0 && g.names.Count == 0) continue;
            string name = null; bool auto = false;
            if (g.names.Count > 0)
            {
                g.names.Sort((a, b) => b.pr != a.pr ? b.pr.CompareTo(a.pr) : NatCmp(a.nm, b.nm));
                name = g.names[0].nm;
            }
            if (name == null)
            {
                real.Sort((a, b) => { int r = NatCmp(a.Item1.Ref, b.Item1.Ref); return r != 0 ? r : NatCmp(a.Item2.Num, b.Item2.Num); });
                var (c, p) = real[0];
                var pn = p.Num != "" ? p.Num : p.Name;
                name = style == "altium" ? "Net" + c.Ref + "_" + pn : "Net-(" + c.Ref + "-" + pn + ")";
                auto = true;
            }
            if (used.Contains(name)) { int k = 2; while (used.Contains(name + " #" + k)) k++; name = name + " #" + k; }
            used.Add(name);
            var net = new SheetNet { Name = name, Auto = auto, Nc = gnc };
            var seen = new HashSet<string>();
            foreach (var (c, p) in real) { if (seen.Add(c.Ref + "." + p.Num)) net.Members.Add(new NetMember { Ref = c.Ref, Pin = p.Num, PinName = p.Name }); }
            foreach (var l in g.labels) { var nm = PlainText(l.Text); if (nm != "") net.Ids.Add(new NetId { K = KindKey(l.Kind), N = nm, Bf = l.Block != null ? BaseName(l.Block.File) : "" }); }
            foreach (var (c, p) in g.pins)
            {
                if (c.Power) { var nm = PlainText(c.Value); if (nm != "" && p.Type == "power_in") net.Ids.Add(new NetId { K = c.OffSheet ? "offsheet" : "power", N = nm }); }
                else if (p.Hidden && (p.GNet != "" || (p.Type == "power_in" && p.Name != ""))) net.Ids.Add(new NetId { K = "hidden", N = p.GNet != "" ? p.GNet : p.Name });
            }
            sh.Nets.Add(net);
            foreach (var w in g.wires) w.Net = name;
            foreach (var j in g.jn) j.Net = name;
            foreach (var l in g.labels) l.Net = name;
            foreach (var (_, p) in g.pins) p.Net = name;
        }
    }

    /// <summary>Altium draws junction dots at T connections even when the file doesn't store them.</summary>
    public static void AddAutoJunctions(SchSheet sh)
    {
        var have = new HashSet<string>(sh.Junctions.Select(j => Math.Round(j.X * 100) + ":" + Math.Round(j.Y * 100)));
        var wl = sh.Wires.Where(w => w.Kind == WireKind.Wire).ToList();
        var cnt = new Dictionary<string, (double x, double y, int n, string net)>();
        void Bump(double x, double y, int n, string net)
        {
            var k = Math.Round(x * 100) + ":" + Math.Round(y * 100);
            cnt[k] = cnt.TryGetValue(k, out var e) ? (e.x, e.y, e.n + n, e.net) : (x, y, n, net);
        }
        foreach (var w in wl) { Bump(w.X1, w.Y1, 1, w.Net); Bump(w.X2, w.Y2, 1, w.Net); }
        foreach (var k in cnt.Keys.ToList())
        {
            var e = cnt[k];
            foreach (var w in wl)
                if (OnSeg(w, e.x, e.y, 0.006) && !(Math.Abs(w.X1 - e.x) < 0.006 && Math.Abs(w.Y1 - e.y) < 0.006) && !(Math.Abs(w.X2 - e.x) < 0.006 && Math.Abs(w.Y2 - e.y) < 0.006))
                    e.n += 2;
            cnt[k] = e;
        }
        foreach (var (k, e) in cnt) if (e.n >= 3 && !have.Contains(k)) sh.Junctions.Add(new SchJunction { X = e.x, Y = e.y, Net = e.net, Auto = true });
    }

    public static void ComputeBBox(SchSheet sh)
    {
        double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
        void Add(double x, double y) { if (!double.IsFinite(x) || !double.IsFinite(y)) return; if (x < x0) x0 = x; if (y < y0) y0 = y; if (x > x1) x1 = x; if (y > y1) y1 = y; }
        foreach (var w in sh.Wires) { Add(w.X1, w.Y1); Add(w.X2, w.Y2); }
        foreach (var c in sh.Comps) { Add(c.BBox[0], c.BBox[1]); Add(c.BBox[2], c.BBox[3]); foreach (var t in c.FTexts) Add(t.X, t.Y); }
        foreach (var l in sh.Labels) Add(l.X, l.Y);
        foreach (var t in sh.Texts) Add(t.X, t.Y);
        foreach (var b in sh.Blocks) { Add(b.X, b.Y); Add(b.X + b.W, b.Y + b.H); }
        foreach (var g in sh.Graphics) { if (g is SchPoly p) foreach (var q in p.Pts) Add(q.X, q.Y); else if (g is SchCircle c) { Add(c.Cx - c.R, c.Cy - c.R); Add(c.Cx + c.R, c.Cy + c.R); } }
        if (x0 == double.MaxValue) { x0 = 0; y0 = 0; x1 = 100; y1 = 70; }
        if (x1 - x0 < 10) { x0 -= 5; x1 += 5; }
        if (y1 - y0 < 10) { y0 -= 5; y1 += 5; }
        sh.BBox = new[] { x0, y0, x1, y1 };
    }

    /// <summary>Document-level net table and part list (port of v1 finalizeDoc).</summary>
    public static SchDocument Finalize(SchDocument doc)
    {
        foreach (var sh in doc.Sheets) ComputeBBox(sh);
        var nets = new Dictionary<string, DocNet>();
        for (int si = 0; si < doc.Sheets.Count; si++)
            foreach (var n in doc.Sheets[si].Nets)
            {
                if (!nets.TryGetValue(n.Name, out var N)) nets[n.Name] = N = new DocNet { Name = n.Name, Auto = n.Auto, Nc = n.Nc };
                foreach (var m in n.Members) if (!N.Members.Any(q => q.Ref == m.Ref && q.Pin == m.Pin)) N.Members.Add(m);
                if (!N.Sheets.Contains(si)) N.Sheets.Add(si);
            }
        doc.Nets = nets.Values.OrderBy(n => n.Auto).ThenBy(n => n.Name, Natural).ToList();
        foreach (var n in doc.Nets) n.Members.Sort((a, b) => { int r = NatCmp(a.Ref, b.Ref); return r != 0 ? r : NatCmp(a.Pin, b.Pin); });
        doc.NetMap = doc.Nets.GroupBy(n => n.Name).ToDictionary(g => g.Key, g => g.First());
        var parts = new Dictionary<string, SchPart>();
        for (int si = 0; si < doc.Sheets.Count; si++)
            foreach (var c in doc.Sheets[si].Comps)
            {
                if (c.Power || c.Virtual || c.Ref.StartsWith('#')) continue;
                if (!parts.TryGetValue(c.Ref, out var p))
                {
                    parts[c.Ref] = p = new SchPart
                    {
                        Ref = c.Ref, Value = PlainText(c.Value), Footprint = c.Footprint ?? "", Lib = c.Lib ?? "", Datasheet = c.Datasheet ?? "", Description = c.Description ?? "",
                        InBom = c.InBom, Dnp = c.Dnp, Missing = c.Missing,
                        Fields = c.Fields.Where(f => f.Key is not ("Reference" or "Value" or "Footprint" or "Datasheet") && f.Val != "").ToList(),
                    };
                }
                if (!p.Sheets.Contains(si)) p.Sheets.Add(si);
                if (c.Units > 1) p.Units.Add(((char)(64 + c.Unit)).ToString());
                foreach (var q in c.Pins)
                    if (!p.Pins.Any(z => z.Num == q.Num && z.Name == q.Name))
                        p.Pins.Add(new PartPin { Num = q.Num, Name = q.Name, Net = q.Net, Hidden = q.Hidden, Type = q.Type, Nc = q.NcFlag });
            }
        doc.Parts = parts.Values.OrderBy(p => p.Ref, Natural).ToList();
        foreach (var p in doc.Parts) p.Pins.Sort((a, b) => NatCmp(a.Num, b.Num));
        doc.PartMap = doc.Parts.ToDictionary(p => p.Ref);
        return doc;
    }
}
