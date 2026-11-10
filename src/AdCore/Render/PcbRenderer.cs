// Herald PCB Reader — Copyright (C) 2026 Johnny.ch.Tsai
// SPDX-License-Identifier: GPL-3.0-only
// Free software under the GNU General Public License version 3 (LICENSE.txt), WITHOUT ANY WARRANTY; with an
// additional permission to combine it with AvalonDock (Ms-PL), see NOTICE.txt.
using AdCore.Pcb;
using SkiaSharp;

namespace AdCore.Render;

public sealed class PcbSelection
{
    public bool IsComp; public int Index; public List<PcbSelection> Items;
    public static PcbSelection Comp(int i) => new() { IsComp = true, Index = i };
    public static PcbSelection Obj(int i) => new() { Index = i };
    public string Key => Items != null ? "multi" : (IsComp ? "c" : "o") + Index;
}

/// <summary>Measure tool: points A / B (or the live end), the snap under the cursor and what it snapped to.</summary>
public sealed class MeasureState
{
    public SKPoint? A, B, Live, Snap; public string SnapKind; public PcbObj SnapObj;
    public string SnapInfo, AInfo, BInfo; public bool Ortho;
}

/// <summary>Per-document view state: transform, layers, highlight, selection, options.</summary>
public sealed class PcbViewState
{
    public readonly PcbScene Scene;
    public double Cx, Cy, S = 1, Mx = 1, My = 1; public int Rot;
    public bool BottomView, Single, ShowOutline = true, ShowNetNames = true, ShowPadNumbers, Mode3D;
    public float Dim = 0.18f;
    public SKColor BoardColor = SKColors.Black, WorkColor = new(0x2E, 0x34, 0x38);
    public readonly Dictionary<int, PcbLayerInfo> LayerMap = new();
    public List<int> Order = new();
    public int Cur;
    public string LayerSet = "all";
    public readonly HashSet<int> HlNets = new(), HlComps = new();
    public PcbSelection Sel;
    public readonly MeasureState Meas = new();
    public readonly HashSet<PickClass> Filter = new(Enum.GetValues<PickClass>());

    static readonly string[] MidC = { "#BC8E00", "#70DBFA", "#00CC66", "#9966FF", "#00FFFF", "#800080", "#FF00FF", "#808000", "#FFFF00", "#808080", "#FFFFFF", "#800080", "#808000", "#C0C0C0", "#008080", "#FFA500" };
    static readonly string[] MechC = { "#FF00FF", "#808000", "#FFFF00", "#C0C0C0", "#800080", "#808000", "#008080", "#FF00FF", "#808080", "#FFFFFF", "#800080", "#808000", "#00A0A0", "#8080FF", "#00CC66", "#FF8080" };

    public static SKColor DefColor(int id)
    {
        var fixedC = new Dictionary<int, string> { [1] = "#FF2A2A", [32] = "#2F6BFF", [33] = "#FFFF00", [34] = "#A0A000", [35] = "#808080", [36] = "#800000", [37] = "#9B30B8", [38] = "#FF00FF",
            [55] = "#800000", [56] = "#FF00FF", [73] = "#FF002A", [74] = "#C0C0C0", [Layers.PadHoles] = "#00B3B3", [Layers.ViaHoles] = "#C9A227" };
        if (fixedC.TryGetValue(id, out var h)) return SKColor.Parse(h);
        if (id >= 2 && id <= 31) return SKColor.Parse(MidC[(id - 2) % MidC.Length]);
        if (id >= 39 && id <= 54) return SKColor.Parse(new[] { "#00A040", "#A00000", "#A000A0", "#A0A000", "#00A0A0", "#606060" }[(id - 39) % 6]);
        if (id >= 57 && id <= 72) return SKColor.Parse(MechC[(id - 57) % 16]);
        return SKColor.Parse(MechC[Math.Abs(id) % 16]);
    }

    public PcbViewState(PcbScene scene)
    {
        Scene = scene;
        foreach (var (id, list) in scene.ByLayer)
            LayerMap[id] = new PcbLayerInfo { Id = id, Name = id >= Layers.PadHoles ? Layers.DefaultName(id) : scene.Board.LayerName(id), Color = DefColor(id), Alpha = id is >= 35 and <= 38 ? .55f : 1, Count = list.Count };
        Cur = LayerMap.ContainsKey(1) ? 1 : LayerMap.Keys.FirstOrDefault();
        ApplyOrder();
    }

    /// <summary>Independent copy for rendering on a background thread.</summary>
    PcbViewState(PcbViewState s)
    {
        Scene = s.Scene; Cx = s.Cx; Cy = s.Cy; S = s.S; Mx = s.Mx; My = s.My; Rot = s.Rot;
        BottomView = s.BottomView; Single = s.Single; ShowOutline = s.ShowOutline; ShowNetNames = s.ShowNetNames; ShowPadNumbers = s.ShowPadNumbers; Mode3D = s.Mode3D;
        Dim = s.Dim; BoardColor = s.BoardColor; WorkColor = s.WorkColor;
        foreach (var (k, l) in s.LayerMap) LayerMap[k] = new PcbLayerInfo { Id = l.Id, Name = l.Name, Color = l.Color, Alpha = l.Alpha, Visible = l.Visible, Count = l.Count };
        Order = new List<int>(s.Order); Cur = s.Cur; LayerSet = s.LayerSet;
        HlNets = new HashSet<int>(s.HlNets); HlComps = new HashSet<int>(s.HlComps);
        Sel = s.Sel is { Items: { } items } ? new PcbSelection { Index = -1, Items = new List<PcbSelection>(items) } : s.Sel;
        Filter = new HashSet<PickClass>(s.Filter);
    }
    public PcbViewState Clone() => new(this);

    // ---------- layer ordering (Altium-like) ----------
    public int Rank(int id)
    {
        var st = Scene.Board.Stack; int ci = st.IndexOf(id);
        if (id == Layers.ViaHoles) return 0; if (id == Layers.PadHoles) return 1; if (id == 74) return 2;
        if (id == 33) return 10; if (id == 37) return 11; if (id == 35) return 12;
        if (ci >= 0) return 20 + ci;
        if (Layers.IsCopper(id) || Layers.IsPlane(id)) return 20 + (id < 39 ? id : id - 39);
        if (id == 36) return 120; if (id == 38) return 121; if (id == 34) return 122; if (id == 56) return 130;
        if (Layers.IsMech(id)) return 140 + (id > 1000 ? id - Layers.V7Mech : id - 56);
        return 300 + id;
    }

    public int TabRank(int id)
    {
        var st = Scene.Board.Stack; int i = st.IndexOf(id);
        if (Layers.IsCopper(id) || Layers.IsPlane(id)) return i >= 0 ? i : 100 + (Layers.IsPlane(id) ? id : id - 60);
        if (Layers.IsMech(id)) return 1000 + (id > Layers.V7Mech ? id - Layers.V7Mech : id - 56);
        return id switch { 33 => 2000, 34 => 2001, 35 => 2002, 36 => 2003, 37 => 2004, 38 => 2005, 55 => 2006, 56 => 2007, 73 => 2008, 74 => 2009, Layers.PadHoles => 2010, Layers.ViaHoles => 2011, _ => 3000 + id };
    }

    public static string Group(int id) =>
        Layers.IsCopper(id) ? "Signal Layers" : Layers.IsPlane(id) ? "Plane Layers" : id is 33 or 34 ? "Silkscreen Layers" : id is >= 35 and <= 38 ? "Mask Layers" : Layers.IsMech(id) ? "Mechanical Layers" : "Other Layers";

    static readonly HashSet<int> TopSide = new() { 1, 33, 35, 37, 74, Layers.PadHoles, Layers.ViaHoles };

    public void ApplyOrder()
    {
        var baseO = LayerMap.Keys.OrderBy(Rank).ToList();
        if (BottomView)
        {
            // flip the stack: bottom-side layers first, top-side last
            int Flip(int id) => id switch { 1 => 32, 32 => 1, 33 => 34, 34 => 33, 35 => 36, 36 => 35, 37 => 38, 38 => 37, _ => id };
            baseO = LayerMap.Keys.OrderBy(id => Layers.IsCopper(id) && id != 1 && id != 32 ? 1000 - Rank(id) : Rank(Flip(id))).ToList();
        }
        var sys = baseO.Where(k => k is Layers.ViaHoles or Layers.PadHoles).ToList();
        Order = LayerMap.ContainsKey(Cur) && !sys.Contains(Cur) ? sys.Append(Cur).Concat(baseO.Where(k => !sys.Contains(k) && k != Cur)).ToList() : baseO;
    }

    public bool IsVisible(int id) => LayerMap.TryGetValue(id, out var l) && (Single ? id == Cur : l.Visible);

    public void SetLayerSet(string set)
    {
        LayerSet = set;
        foreach (var l in LayerMap.Values)
            l.Visible = set switch
            {
                "signal" => Layers.IsSignal(l.Id) || l.Id >= Layers.PadHoles,
                "plane" => Layers.IsPlane(l.Id) || l.Id >= Layers.PadHoles,
                "nonsignal" => !Layers.IsCopper(l.Id),
                "mech" => Layers.IsMech(l.Id),
                "top" => TopSide.Contains(l.Id) || l.Id == 1,
                "bottom" => l.Id is 32 or 34 or 36 or 38 or 74 or Layers.PadHoles or Layers.ViaHoles,
                _ => true,
            };
        Scene.InvalidatePictures();
    }

    // ---------- transform (world mil, Y up → screen) ----------
    public (double a, double b, double c, double d) A()
    {
        double r = Rot * Math.PI / 180, c = Math.Round(Math.Cos(r)), sn = Math.Round(Math.Sin(r));
        return (c * S * Mx, sn * S * My, sn * S * Mx, -c * S * My);
    }
    public SKMatrix Matrix(float w, float h)
    {
        var (a, b, c, d) = A();
        return new SKMatrix((float)a, (float)b, (float)(w / 2 - a * Cx - b * Cy), (float)c, (float)d, (float)(h / 2 - c * Cx - d * Cy), 0, 0, 1);
    }
    public SKPoint ToScreen(double x, double y, float w, float h) { var (a, b, c, d) = A(); double dx = x - Cx, dy = y - Cy; return new SKPoint((float)(w / 2 + a * dx + b * dy), (float)(h / 2 + c * dx + d * dy)); }
    public (double x, double y) ToWorld(double px, double py, float w, float h)
    {
        var (a, b, c, d) = A(); double det = a * d - b * c, u = px - w / 2, v = py - h / 2;
        return (Cx + (d * u - b * v) / det, Cy + (-c * u + a * v) / det);
    }
    public void PanBy(double du, double dv) { var (a, b, c, d) = A(); double det = a * d - b * c; Cx -= (d * du - b * dv) / det; Cy -= (-c * du + a * dv) / det; }
    public void ZoomAt(double factor, double px, double py, float w, float h)
    {
        var (wx, wy) = ToWorld(px, py, w, h);
        S = Math.Clamp(S * factor, 1e-4, 500);
        var (nx, ny) = ToWorld(px, py, w, h);
        Cx += wx - nx; Cy += wy - ny;
    }
    public void Fit(float w, float h, SKRect? bb = null)
    {
        var b = bb ?? Scene.Bounds; if (b.IsEmpty || w < 2 || h < 2) return;
        Cx = b.MidX; Cy = b.MidY;
        bool rot = Rot % 180 != 0;
        double bw = rot ? b.Height : b.Width, bh = rot ? b.Width : b.Height;
        S = Math.Min((w - 40) / Math.Max(bw, 1), (h - 40) / Math.Max(bh, 1));
    }

    // ---------- picking ----------
    static readonly int[] TopSideL = { 1, 33, 35, 37 }, BotSideL = { 32, 34, 36, 38 };

    /// <summary>Is this object selectable with the current filter (a part's own outline / silk / text counts as the part)?</summary>
    public bool Selectable(PcbObj o)
    {
        var c = o.Class;
        if (o.Comp != Layers.None && c != PickClass.Pad && c != PickClass.Via && Filter.Contains(PickClass.Comp)) return true;
        return Filter.Contains(c);
    }
    PcbSelection SelOf(PcbObj o) =>
        o.Comp != Layers.None && o.Comp < Scene.Board.Comps.Count && o.Class is not (PickClass.Pad or PickClass.Via) && Filter.Contains(PickClass.Comp) ? PcbSelection.Comp(o.Comp) : PcbSelection.Obj(o.Index);
    static int ClassPrio(PcbObj o) => o.Kind is PcbObjKind.Pad or PcbObjKind.Via ? 0 : o.Kind is PcbObjKind.Track or PcbObjKind.Arc ? 1 : 2;

    /// <summary>What you see is what you pick. Layers are walked in drawing order (top first):
    /// 1. a pad / via / track / arc right under the cursor, on the topmost layer that has one;
    /// 2. otherwise the nearest such primitive within the tolerance;
    /// 3. otherwise a text box or the body box of a part (smallest wins, parts on the side you look at first),
    ///    then a pour / region / fill — anything drawn under a pour counts as hidden.</summary>
    /// <param name="side">"top" / "bottom": only that side's layers and parts (3D board-face clicks).</param>
    public PcbSelection Pick(double wx, double wy, double tolWorld, string side = null)
    {
        var sideL = side == null ? null : side == "top" ? TopSideL : BotSideL;
        bool Vis(int k) => sideL != null ? (sideL.Contains(k) || k == Layers.MultiLayer) && LayerMap.ContainsKey(k) : IsVisible(k);
        var layers = Order.Where(Vis).ToList();
        PcbObj exact = null, near = null, soft = null, solid = null;
        int exactL = int.MaxValue, softL = int.MaxValue, solidL = int.MaxValue, nearL = int.MaxValue;
        double nearD = double.MaxValue, solidA = double.MaxValue;
        for (int li = 0; li < layers.Count; li++)
        {
            if (!Scene.ByLayer.TryGetValue(layers[li], out var list)) continue;
            for (int i = list.Count - 1; i >= 0; i--)
            {
                var o = list[i];
                if (o.Kind == PcbObjKind.Hole || !Selectable(o)) continue;
                double d = PcbScene.HitDist(o, wx, wy, tolWorld);
                if (d < 0) continue;
                if (o.SolidArea) { double a = Area(o.BB); if (li < solidL || li == solidL && a < solidA) { solid = o; solidL = li; solidA = a; } }
                else if (o.SoftArea) { if (li < softL) { soft = o; softL = li; } }
                else if (d == 0) { if (li < exactL || li == exactL && ClassPrio(o) < ClassPrio(exact)) { exact = o; exactL = li; } }
                else if (d < nearD - 1e-9 || Math.Abs(d - nearD) <= 1e-9 && (li < nearL || li == nearL && ClassPrio(o) < ClassPrio(near))) { near = o; nearD = d; nearL = li; }
            }
        }
        // things on layers drawn under the topmost pour at this point are hidden by it, except through-hole pads
        // and vias (multi-layer): those always stay clickable, also where a pour of their own net covers them
        if (exact != null && (exactL <= solidL || exact.L == Layers.MultiLayer)) return SelOf(exact);
        if (near != null && (nearL <= solidL || near.L == Layers.MultiLayer)) return SelOf(near);
        // the body box of a part counts as drawn at the level of its topmost visible layer: it covers pours on
        // layers under it (inner planes), not the ones above it
        PcbSelection body = null; double bodyA = double.MaxValue; int bodyL = int.MaxValue;
        if (Filter.Contains(PickClass.Comp))
        {
            var idx = new Dictionary<int, int>(); for (int li = 0; li < layers.Count; li++) idx[layers[li]] = li;
            int pref = BottomView ? Layers.Bottom : Layers.Top;
            for (int i = 0; i < Scene.CompArea.Length; i++)
            {
                var b = Scene.CompArea[i]; if (b is not { } r || wx < r.Left || wx > r.Right || wy < r.Top || wy > r.Bottom) continue;
                bool bottom = Scene.Board.Comps[i].Layer == Layers.Bottom;
                if (side != null && bottom != (side == "bottom")) continue;
                int cl = Scene.CompLayers[i].Select(k => idx.TryGetValue(k, out var v) ? v : int.MaxValue).DefaultIfEmpty(int.MaxValue).Min();
                if (cl == int.MaxValue) continue;   // none of its layers is shown
                double a = Area(r) * (Scene.Board.Comps[i].Layer == pref || side != null ? 0.85 : 1);   // prefer parts on the side you look at
                bool vis = cl <= solidL, bestVis = bodyL <= solidL;
                if (body == null || vis && !bestVis || vis == bestVis && a < bodyA) { body = PcbSelection.Comp(i); bodyA = a; bodyL = cl; }
            }
        }
        if (soft != null && softL <= solidL && (body == null || softL <= bodyL)) return SelOf(soft);
        if (body != null && bodyL <= solidL) return body;
        if (soft != null && softL <= solidL) return SelOf(soft);
        if (solid != null) return SelOf(solid);
        return body;
    }

    // ---------- area (window) selection (port of v1) ----------
    static bool SegHitsRect(double ax, double ay, double bx, double by, SKRect r)
    {
        double t0 = 0, t1 = 1, dx = bx - ax, dy = by - ay;
        foreach (var (p, q) in new[] { (-dx, ax - r.Left), (dx, r.Right - ax), (-dy, ay - r.Top), (dy, r.Bottom - ay) })
        {
            if (Math.Abs(p) < 1e-12) { if (q < 0) return false; continue; }
            double t = q / p;
            if (p < 0) { if (t > t1) return false; if (t > t0) t0 = t; } else { if (t < t0) return false; if (t < t1) t1 = t; }
        }
        return true;
    }

    static bool TouchesRect(PcbObj o, SKRect r)
    {
        var b = o.BB; if (b.Right < r.Left || b.Left > r.Right || b.Bottom < r.Top || b.Top > r.Bottom) return false;
        if (o.Src is PcbTrack t) { float h = (float)t.W / 2; return SegHitsRect(t.X1, t.Y1, t.X2, t.Y2, new SKRect(r.Left - h, r.Top - h, r.Right + h, r.Bottom + h)); }
        if (o.Kind == PcbObjKind.Region && o.Src is PcbRegion g)
        {
            foreach (var v in g.Outline) if (v.X >= r.Left && v.X <= r.Right && v.Y >= r.Top && v.Y <= r.Bottom) return true;
            foreach (var (x, y) in new[] { (r.Left, r.Top), (r.Right, r.Top), (r.Right, r.Bottom), (r.Left, r.Bottom), (r.MidX, r.MidY) }) if (o.Path.Contains(x, y)) return true;
            for (int i = 0; i < g.Outline.Count; i++) { var a = g.Outline[i]; var c = g.Outline[(i + 1) % g.Outline.Count]; if (SegHitsRect(a.X, a.Y, c.X, c.Y, r)) return true; }
            return false;
        }
        return true;
    }

    /// <summary>Left→right (touching = false): objects fully inside. Right→left: objects touching the box.
    /// Uses the selection filter; a part's own primitives are represented by the part.</summary>
    public List<PcbSelection> AreaPick(SKRect r, bool touching)
    {
        var found = new List<PcbSelection>();
        bool Inside(SKRect b) => b.Left >= r.Left && b.Right <= r.Right && b.Top >= r.Top && b.Bottom <= r.Bottom;
        if (Filter.Contains(PickClass.Comp))
        {
            var objsOf = touching ? Scene.Objs.Where(o => o.Comp != Layers.None && o.Kind != PcbObjKind.Text && o.Kind != PcbObjKind.Hole && IsVisible(o.L)).ToLookup(o => o.Comp) : null;
            for (int i = 0; i < Scene.Board.Comps.Count; i++)
            {
                if (Scene.CompBox(i) is not { } bb || !Scene.CompLayers[i].Any(IsVisible)) continue;
                bool ok = touching ? bb.IntersectsWith(r) && objsOf[i].Any(o => TouchesRect(o, r)) : Inside(bb);
                if (ok) found.Add(PcbSelection.Comp(i));
            }
        }
        foreach (var k in Order)
        {
            if (!IsVisible(k) || !Scene.ByLayer.TryGetValue(k, out var list)) continue;
            foreach (var o in list)
            {
                if (o.Kind == PcbObjKind.Hole) continue;
                if (o.Comp != Layers.None && Filter.Contains(PickClass.Comp)) continue;   // represented by its part
                if (!Filter.Contains(o.Class)) continue;
                if (touching ? TouchesRect(o, r) : Inside(o.BB)) found.Add(PcbSelection.Obj(o.Index));
            }
        }
        return found;
    }

    static double Area(SKRect b) => Math.Max(b.Width, 1e-3) * Math.Max(b.Height, 1e-3);

    public int SelectedComp => Sel is { IsComp: true } s ? s.Index : -1;
    public PcbObj SelectedObj => Sel is { IsComp: false, Items: null } s && s.Index >= 0 && s.Index < Scene.Objs.Count ? Scene.Objs[s.Index] : null;
}

public static class PcbRenderer
{
    /// <summary>Full vector render (CLI / tests). The view draws the same parts itself: board fill, the layer
    /// stack (from tiles), overlays and NET labels. Uses Concat, so an outer scale (screen DPI) is kept.</summary>
    public static void Render(SKCanvas c, PcbScene scene, PcbViewState st, float w, float h)
    {
        lock (scene.Sync)
        {
            if (scene.Disposed) return;
            c.Clear(st.WorkColor);
            c.Save();
            var m = st.Matrix(w, h); c.Concat(ref m);
            DrawBoardFill(c, scene, st);
            DrawStack(c, LayerStack(scene, st), scene.HasHighlight ? st.Dim : 1);
            DrawOverlays(c, scene, st);
            c.Restore();
            if (st.ShowNetNames || st.ShowPadNumbers) DrawNetLabels(c, scene, st, w, h);
        }
    }

    /// <summary>Board colour inside the outline (world coordinates).</summary>
    public static void DrawBoardFill(SKCanvas c, PcbScene scene, PcbViewState st)
    {
        if (scene.OutlinePath == null) return;
        using var bp = new SKPaint { Color = st.BoardColor, Style = SKPaintStyle.Fill, IsAntialias = true };
        c.DrawPath(scene.OutlinePath, bp);
        bp.Color = st.WorkColor; foreach (var cp in scene.Cutouts) c.DrawPath(cp, bp);
    }

    /// <summary>The visible layers' pictures with their alpha, bottom of the stack first.</summary>
    public static List<(SKPicture pic, float alpha)> LayerStack(PcbScene scene, PcbViewState st)
    {
        var visible = st.Order.Where(st.IsVisible).ToList(); var list = new List<(SKPicture, float)>();
        for (int i = visible.Count - 1; i >= 0; i--) { var l = st.LayerMap[visible[i]]; list.Add((scene.LayerPicture(visible[i], l), l.Alpha)); }
        return list;
    }

    public static void DrawStack(SKCanvas c, IReadOnlyList<(SKPicture pic, float alpha)> stack, float dim = 1)
    {
        foreach (var (pic, a0) in stack)
        {
            float a = a0 * dim;
            if (a >= 0.999f) { c.DrawPicture(pic); continue; }   // opaque: no offscreen layer needed
            using var lp = new SKPaint { Color = SKColors.White.WithAlpha((byte)(255 * a)) };
            c.SaveLayer(lp); c.DrawPicture(pic); c.Restore();
        }
    }

    /// <summary>Highlighted objects, board outline, highlighted parts and the selection (world coordinates).</summary>
    public static void DrawOverlays(SKCanvas c, PcbScene scene, PcbViewState st)
    {
        if (scene.HasHighlight)
        {
            var visible = st.Order.Where(st.IsVisible).ToList();
            for (int i = visible.Count - 1; i >= 0; i--)
            {
                var l = st.LayerMap[visible[i]];
                if (scene.HlPicture(visible[i], l) is not { } hp) continue;
                using var lp2 = new SKPaint { Color = SKColors.White.WithAlpha((byte)(255 * Math.Max(l.Alpha, .85f))) };
                c.SaveLayer(lp2); c.DrawPicture(hp); c.Restore();
            }
        }
        float px = (float)(1 / st.S);
        if (st.ShowOutline && scene.OutlinePath != null)
        {
            using var op = new SKPaint { Color = new SKColor(255, 236, 120, 230), Style = SKPaintStyle.Stroke, StrokeWidth = 1.2f * px, IsAntialias = true };
            c.DrawPath(scene.OutlinePath, op);
        }
        if (st.HlComps.Count > 0)
        {
            using var dp = new SKPaint { Color = new SKColor(255, 255, 255, 216), Style = SKPaintStyle.Stroke, StrokeWidth = px, IsAntialias = true, PathEffect = SKPathEffect.CreateDash(new[] { 4 * px, 3 * px }, 0) };
            foreach (var ci in st.HlComps) if (scene.CompBox(ci) is { } b) { var mm = 6 * px; c.DrawRect(new SKRect(b.Left - mm, b.Top - mm, b.Right + mm, b.Bottom + mm), dp); }
        }
        if (st.Sel != null)
        {
            if (st.Sel.Items != null) foreach (var s in st.Sel.Items) DrawSel(c, scene, s, px); else DrawSel(c, scene, st.Sel, px);
        }
    }

    static void DrawSel(SKCanvas c, PcbScene scene, PcbSelection s, float px)
    {
        if (s.IsComp)
        {
            if (scene.CompBox(s.Index) is { } b)
            {
                float m = 5 * px; var r = new SKRect(b.Left - m, b.Top - m, b.Right + m, b.Bottom + m);
                using var f = new SKPaint { Color = new SKColor(255, 255, 255, 18), Style = SKPaintStyle.Fill }; c.DrawRect(r, f);
                using var p = new SKPaint { Color = SKColors.White, Style = SKPaintStyle.Stroke, StrokeWidth = 2 * px, IsAntialias = true, PathEffect = SKPathEffect.CreateDash(new[] { 6 * px, 4 * px }, 0) };
                c.DrawRect(r, p);
            }
            using var pp = new SKPaint { Color = new SKColor(255, 255, 255, 230), Style = SKPaintStyle.Stroke, StrokeWidth = 1.2f * px, IsAntialias = true };
            foreach (var o in scene.Objs) if (o.Comp == s.Index && o.Kind == PcbObjKind.Pad) c.DrawPath(o.Path, pp);
            return;
        }
        if (s.Index < 0 || s.Index >= scene.Objs.Count) return;
        var ob = scene.Objs[s.Index];
        using var sp = new SKPaint { Color = SKColors.White, Style = SKPaintStyle.Stroke, StrokeWidth = 2 * px, IsAntialias = true, StrokeCap = SKStrokeCap.Round, PathEffect = SKPathEffect.CreateDash(new[] { 5 * px, 3 * px }, 0) };
        if (ob.Kind == PcbObjKind.Text) { using var path = new SKPath(); path.AddPoly(ob.Corners, true); c.DrawPath(path, sp); }
        else if (ob.Mode == PaintMode.Stroke) { sp.PathEffect = null; sp.Color = new SKColor(255, 255, 255, 90); sp.StrokeWidth = Math.Max(ob.W, px) + 3 * px; c.DrawPath(ob.Path, sp); }
        else c.DrawPath(ob.Path, sp);
    }

    /// <summary>Pad numbers are drawn in pale yellow, NET names in white, so the two tell apart on a pad showing both.</summary>
    public static readonly SKColor PadNumberColor = new(0xFF, 0xE9, 0x8A);

    /// <summary>NET names on pads / vias / tracks (ShowNetNames) and pad numbers (ShowPadNumbers) where the text fits
    /// inside the copper (screen space). A pad showing both gets its number above and the NET name below; when the
    /// pad is too small for two lines only the number is kept.</summary>
    public static void DrawNetLabels(SKCanvas c, PcbScene scene, PcbViewState st, float W, float H)
    {
        var nets = scene.Board.Nets;
        bool showNets = st.ShowNetNames && nets.Count > 0, showNums = st.ShowPadNumbers;
        if (!showNets && !showNums) return;
        var c0 = st.ToWorld(0, 0, W, H); var c1 = st.ToWorld(W, H, W, H);
        double vx0 = Math.Min(c0.x, c1.x), vx1 = Math.Max(c0.x, c1.x), vy0 = Math.Min(c0.y, c1.y), vy1 = Math.Max(c0.y, c1.y);
        double s = st.S; bool hl = scene.HasHighlight;
        const double CW = 0.56;
        var tf = TextPainter.Face("Bahnschrift", true, false, SKFontStyleWidth.SemiCondensed);
        using var fillP = new SKPaint { Color = SKColors.White, IsAntialias = true };
        using var strokeP = new SKPaint { Color = new SKColor(0, 0, 0, 158), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeJoin = SKStrokeJoin.Round };
        // screen boxes of the pad / via labels drawn so far: a track's NET name is left out where it would cover one
        var drawn = new List<SKRect>();
        // screen box of one line of text centred on ctr, turned by ang, moved dx along / dy across the text direction
        SKRect Box(float tw2, double fs, SKPoint ctr, double ang, double dx, double dy)
        {
            double x0 = -tw2 / 2 + dx, x1 = tw2 / 2 + dx, y0 = dy - fs * 0.5, y1 = dy + fs * 0.5, ca = Math.Cos(ang), sa = Math.Sin(ang);
            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            foreach (var (px, py) in new[] { (x0, y0), (x1, y0), (x1, y1), (x0, y1) })
            {
                float X = (float)(ctr.X + px * ca - py * sa), Y = (float)(ctr.Y + px * sa + py * ca);
                minX = Math.Min(minX, X); maxX = Math.Max(maxX, X); minY = Math.Min(minY, Y); maxY = Math.Max(maxY, Y);
            }
            return new SKRect(minX, minY, maxX, maxY);
        }
        void Label(string text, double fs, SKPoint ctr, double ang, double dy, SKColor col, byte alpha, double dx = 0, bool track = false)
        {
            float tw2 = TextPainter.Measure(text, tf, (float)fs);
            var box = Box(tw2, fs, ctr, ang, dx, dy);
            if (track) { foreach (var r in drawn) if (r.IntersectsWith(box)) return; }
            else drawn.Add(box);
            fillP.Color = col.WithAlpha(alpha); strokeP.Color = new SKColor(0, 0, 0, (byte)(158 * alpha / 255));
            strokeP.StrokeWidth = (float)Math.Max(1.6, fs * 0.22);
            c.Save(); c.Translate(ctr.X, ctr.Y); if (ang != 0) c.RotateRadians((float)ang);
            float bx = (float)(-tw2 / 2 + dx), by = (float)(fs * 0.36 + dy);
            using (var font = new SKFont(tf, (float)fs) { Subpixel = true, Edging = SKFontEdging.Antialias })
            {
                c.DrawText(text, bx, by, font, strokeP); c.DrawText(text, bx, by, font, fillP);
            }
            c.Restore();
        }
        int n = 0;
        var visCache = new Dictionary<int, bool>();
        foreach (var o in scene.LabelObjs)
        {
            var b = o.BB; if (b.Right < vx0 || b.Left > vx1 || b.Bottom < vy0 || b.Top > vy1) continue;
            if (!visCache.TryGetValue(o.L, out var v)) visCache[o.L] = v = st.IsVisible(o.L);
            if (!v) continue;
            var name = showNets && o.Net != Layers.None && o.Net < nets.Count && !string.IsNullOrEmpty(nets[o.Net]) ? nets[o.Net] : null;
            byte alpha = (byte)(!hl || st.HlNets.Contains(o.Net) || st.HlComps.Contains(o.Comp) ? 255 : 255 * Math.Max(st.Dim, 0.15f));
            double fs; double ang = 0; SKPoint ctr;
            if (o.Kind == PcbObjKind.Track)
            {
                if (name == null) continue;
                var t = (PcbTrack)o.Src; var a = st.ToScreen(t.X1, t.Y1, W, H); var e = st.ToScreen(t.X2, t.Y2, W, H);
                double L = Math.Sqrt((e.X - a.X) * (e.X - a.X) + (e.Y - a.Y) * (e.Y - a.Y)), tw = t.W * s;
                fs = Math.Min(tw * 0.72, 26); if (fs < 7) continue;
                if (L < name.Length * fs * CW + tw * 2 + 10) continue;
                ctr = new SKPoint((a.X + e.X) / 2, (a.Y + e.Y) / 2); ang = Math.Atan2(e.Y - a.Y, e.X - a.X);
                if (ang > Math.PI / 2 + 1e-6) ang -= Math.PI; else if (ang <= -Math.PI / 2 + 1e-6) ang += Math.PI;
                if (ctr.X < -200 || ctr.X > W + 200 || ctr.Y < -200 || ctr.Y > H + 200) continue;
                Label(name, fs, ctr, ang, 0, SKColors.White, alpha, track: true);
            }
            else if (o.Kind == PcbObjKind.Via)
            {
                if (name == null) continue;
                var vv = (PcbVia)o.Src; double d = vv.D * s; fs = Math.Min(d * 0.32, d * 0.86 / (name.Length * CW)); if (fs < 6) continue;
                ctr = st.ToScreen(vv.X, vv.Y, W, H);
                if (ctr.X < -200 || ctr.X > W + 200 || ctr.Y < -200 || ctr.Y > H + 200) continue;
                Label(name, fs, ctr, 0, 0, SKColors.White, alpha);
            }
            else
            {
                var p = (PcbPad)o.Src;
                var num = showNums && !string.IsNullOrEmpty(p.Name) ? p.Name : null;
                if (name == null && num == null) continue;
                var sz = p.L == Layers.Bottom && p.Mode != 0 ? p.Bot : p.Top; double pw = sz.Sx, ph = sz.Sy;
                double a = ((p.Rot % 180) + 180) % 180;
                if (Math.Abs(a - 90) < 15) (pw, ph) = (ph, pw); else if (a > 15 && a < 165) { double mm = Math.Min(pw, ph) * 0.75; pw = ph = mm; }
                if (st.Rot % 180 != 0) (pw, ph) = (ph, pw);
                pw *= s; ph *= s; ctr = st.ToScreen(p.X, p.Y, W, H);
                if (ctr.X < -200 || ctr.X > W + 200 || ctr.Y < -200 || ctr.Y > H + 200) continue;
                bool vert = ph > pw * 1.15; double along = vert ? ph : pw, across = vert ? pw : ph;
                if (vert) ang = -Math.PI / 2;
                if (num != null && name != null)
                {
                    if (along > across * 1.8)
                    {
                        // long pad: "number  NET" on one line
                        double f1 = Math.Min(across * 0.55, Math.Min(along * 0.9 / ((num.Length + name.Length + 1.2) * CW), 32));
                        if (f1 >= 6)
                        {
                            float wN = TextPainter.Measure(num, tf, (float)f1), wT = TextPainter.Measure(name, tf, (float)f1), gap = (float)(f1 * 0.5), tot = wN + gap + wT;
                            Label(num, f1, ctr, ang, 0, PadNumberColor, alpha, -tot / 2 + wN / 2);
                            Label(name, f1, ctr, ang, 0, SKColors.White, alpha, tot / 2 - wT / 2);
                            if (++n > 5000) break;
                            continue;
                        }
                    }
                    else
                    {
                        // squarish pad: number above, NET below
                        double fsN = Math.Min(across * 0.34, Math.Min(along * 0.88 / (num.Length * CW), 30));
                        double fsT = Math.Min(across * 0.27, Math.Min(along * 0.88 / (name.Length * CW), 26));
                        if (fsN >= 6 && fsT >= 6)
                        {
                            Label(num, fsN, ctr, ang, -across * 0.2, PadNumberColor, alpha);
                            Label(name, fsT, ctr, ang, across * 0.22, SKColors.White, alpha);
                            if (++n > 5000) break;
                            continue;
                        }
                    }
                    name = null;   // no room for both: the number alone
                }
                var text = num ?? name;
                fs = Math.Min(across * (num != null ? 0.6 : 0.5), Math.Min(along * 0.88 / (text.Length * CW), num != null ? 40 : 36)); if (fs < 6) continue;
                Label(text, fs, ctr, ang, 0, num != null ? PadNumberColor : SKColors.White, alpha);
            }
            if (++n > 5000) break;
        }
    }

    /// <summary>Distance label of the measure tool (檔案 → 設定).</summary>
    public static float MeasFontSize = 13;
    public static SKColor MeasColor = new(0xFF, 0xE2, 0x7A);

    public static void DrawMeasure(SKCanvas c, PcbViewState st, float W, float H)
    {
        var m = st.Meas;
        if (m.Snap is { } sp)
        {
            var s = st.ToScreen(sp.X, sp.Y, W, H);
            using var p = new SKPaint { Color = m.SnapKind == "edge" ? new SKColor(0xFF, 0xB2, 0x4A) : m.SnapKind == "proj" ? new SKColor(0xC9, 0x8C, 0xFF) : new SKColor(0x5F, 0xE0, 0xF0), Style = SKPaintStyle.Stroke, StrokeWidth = 1.6f, IsAntialias = true };
            if (m.SnapKind is "center" or "proj") { c.DrawCircle(s, 6, p); c.DrawLine(s.X - 11, s.Y, s.X - 3, s.Y, p); c.DrawLine(s.X + 3, s.Y, s.X + 11, s.Y, p); c.DrawLine(s.X, s.Y - 11, s.X, s.Y - 3, p); c.DrawLine(s.X, s.Y + 3, s.X, s.Y + 11, p); }
            else if (m.SnapKind == "edge") { using var path = new SKPath(); path.MoveTo(s.X, s.Y - 7); path.LineTo(s.X + 7, s.Y); path.LineTo(s.X, s.Y + 7); path.LineTo(s.X - 7, s.Y); path.Close(); c.DrawPath(path, p); }
            else c.DrawRect(s.X - 6, s.Y - 6, 12, 12, p);
        }
        if (m.A is not { } a) return;
        var sa = st.ToScreen(a.X, a.Y, W, H);
        void Cross(SKPoint q)
        {
            using var p = new SKPaint { Color = SKColors.White, Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f, IsAntialias = true };
            c.DrawLine(q.X - 7, q.Y, q.X + 7, q.Y, p); c.DrawLine(q.X, q.Y - 7, q.X, q.Y + 7, p);
        }
        Cross(sa);
        var bpt = m.B ?? m.Live;
        if (bpt is not { } b) return;
        var sb = st.ToScreen(b.X, b.Y, W, H);
        using (var dp = new SKPaint { Color = new SKColor(255, 255, 255, 128), Style = SKPaintStyle.Stroke, StrokeWidth = 1, PathEffect = SKPathEffect.CreateDash(new[] { 4f, 4f }, 0) })
        {
            c.DrawLine(sa.X, sa.Y, sb.X, sa.Y, dp); c.DrawLine(sb.X, sa.Y, sb.X, sb.Y, dp);
        }
        using (var lp = new SKPaint { Color = SKColors.White, Style = SKPaintStyle.Stroke, StrokeWidth = 2, IsAntialias = true }) c.DrawLine(sa, sb, lp);
        Cross(sb);
        double d = Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y)), dx = Math.Abs(b.X - a.X), dy = Math.Abs(b.Y - a.Y);
        string label = $"{Units.Fmt(d)}  (ΔX {Units.Fmt(dx)}, ΔY {Units.Fmt(dy)}) {Units.Name}";
        var tf = TextPainter.Face("Consolas");
        float fs = MeasFontSize, tw = TextPainter.Measure(label, tf, fs);
        float mx = (sa.X + sb.X) / 2 + 10, my = (sa.Y + sb.Y) / 2 - 10;
        using (var bg = new SKPaint { Color = new SKColor(10, 12, 13, 216) }) c.DrawRect(mx - 4, my - fs * 1.15f, tw + 8, fs * 1.55f, bg);
        using (var tp = new SKPaint { Color = MeasColor, IsAntialias = true }) TextPainter.Draw(c, label, mx, my, tf, fs, tp);
    }
}

/// <summary>Display units shared by the PCB views (mil internal).</summary>
public static class Units
{
    public static bool Mm = true;
    public static string Name => Mm ? "mm" : "mil";
    public static double Conv(double mil) => Mm ? mil * 0.0254 : mil;
    public static string Fmt(double mil)
    {
        double x = Conv(mil);
        return Mm ? x.ToString(Math.Abs(x) < 10 ? "0.000" : "0.00") : x.ToString(Math.Abs(x) < 100 ? "0.00" : "0.0");
    }
}
