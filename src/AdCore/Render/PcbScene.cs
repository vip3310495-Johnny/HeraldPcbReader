// Herald PCB Reader — Copyright (C) 2026 Johnny.ch.Tsai
// SPDX-License-Identifier: GPL-3.0-only
// Free software under the GNU General Public License version 3 (LICENSE.txt), WITHOUT ANY WARRANTY; with an
// additional permission to combine it with AvalonDock (Ms-PL), see NOTICE.txt.
using AdCore.Pcb;
using SkiaSharp;

namespace AdCore.Render;

public enum PcbObjKind { Track, Arc, Fill, Region, Pad, Via, Hole, Text }
public enum PaintMode { Stroke, Fill, EvenOdd, Outline, Text }
public enum PickClass { Comp, Pad, Via, Track, Arc, Poly, Region, Fill, Text, Keepout }

public sealed class PcbObj
{
    public int Index;
    public PcbObjKind Kind;
    public PaintMode Mode;
    public int L, Net = Layers.None, Comp = Layers.None, Owner = -1;
    public float W;
    public SKPath Path;
    public SKRect BB;
    public object Src;
    public SKPoint[] Snaps = Array.Empty<SKPoint>();
    public SKPoint[] Corners;    // text
    public string[] Lines;       // text

    public PickClass Class => Kind switch
    {
        PcbObjKind.Pad => PickClass.Pad, PcbObjKind.Via => PickClass.Via, PcbObjKind.Text => PickClass.Text,
        PcbObjKind.Fill => ((PcbFill)Src).Keepout ? PickClass.Keepout : PickClass.Fill,
        PcbObjKind.Region => ((PcbRegion)Src).Keepout ? PickClass.Keepout : ((PcbRegion)Src).Poly != Layers.None ? PickClass.Poly : PickClass.Region,
        PcbObjKind.Track => ((PcbTrack)Src).Keepout ? PickClass.Keepout : PickClass.Track,
        PcbObjKind.Arc => ((PcbArc)Src).Keepout ? PickClass.Keepout : PickClass.Arc,
        _ => PickClass.Region,
    };

    /// <summary>Pours, regions and fills: opaque areas that hide what is drawn under them.</summary>
    public bool SolidArea => Kind is PcbObjKind.Fill || Kind == PcbObjKind.Region && Mode != PaintMode.Outline;
    /// <summary>Text boxes and outline-only regions: picked only when nothing more precise is under the cursor.</summary>
    public bool SoftArea => Kind == PcbObjKind.Text || Kind == PcbObjKind.Region && Mode == PaintMode.Outline;
}

public sealed class PcbLayerInfo
{
    public int Id; public string Name; public SKColor Color; public float Alpha = 1; public bool Visible = true; public int Count;
}

/// <summary>Board geometry built once: objects, per-layer lists, component boxes, outline. Pictures per layer are cached.</summary>
public sealed class PcbScene : IDisposable
{
    public readonly PcbBoard Board;
    public readonly List<PcbObj> Objs = new();
    public readonly Dictionary<int, List<PcbObj>> ByLayer = new();
    public SKPath OutlinePath;
    public readonly List<SKPath> Cutouts = new();
    public SKRect Bounds;
    /// <summary>Everything drawn (the board plus drawings, tables or text outside its outline).</summary>
    public SKRect ContentBounds;
    /// <summary>Bumped whenever the layer pictures are re-recorded (colours / text visibility changed).</summary>
    public int PicVersion { get; private set; }
    public SKRect?[] CompBB, CompArea;
    public HashSet<int>[] CompLayers;
    public readonly Dictionary<int, int> NetPadCount = new();
    public readonly List<PcbObj> LabelObjs = new();
    readonly Dictionary<int, SKPicture> _pics = new();
    readonly Dictionary<int, SKPicture> _hlPics = new();
    public bool HasHighlight { get; private set; }
    public SKRect? HlBounds { get; private set; }
    public bool ShowText = true;
    /// <summary>Guards the cached pictures: background renders hold it while drawing.</summary>
    public readonly object Sync = new();

    const float D2R = (float)(Math.PI / 180);

    public PcbScene(PcbBoard b)
    {
        Board = b;
        Build();
    }

    static SKRect BBPts(IEnumerable<SKPoint> pts, float pad = 0)
    {
        float a = float.MaxValue, b = float.MaxValue, c = float.MinValue, d = float.MinValue;
        foreach (var p in pts) { if (p.X < a) a = p.X; if (p.Y < b) b = p.Y; if (p.X > c) c = p.X; if (p.Y > d) d = p.Y; }
        return a == float.MaxValue ? SKRect.Empty : new SKRect(a - pad, b - pad, c + pad, d + pad);
    }

    public static void RRect(SKPath p, float x, float y, float w, float h, float r)
    {
        r = Math.Max(0, Math.Min(r, Math.Min(w / 2, h / 2)));
        if (r <= 0) p.AddRect(new SKRect(x, y, x + w, y + h));
        else p.AddRoundRect(new SKRect(x, y, x + w, y + h), r, r);
    }

    static void Octo(SKPath p, float w, float h)
    {
        float c = Math.Min(w, h) * .25f, x = -w / 2, y = -h / 2;
        p.MoveTo(x + c, y); p.LineTo(x + w - c, y); p.LineTo(x + w, y + c); p.LineTo(x + w, y + h - c);
        p.LineTo(x + w - c, y + h); p.LineTo(x + c, y + h); p.LineTo(x, y + h - c); p.LineTo(x, y + c); p.Close();
    }

    public static SKPath PadShapePath(PadShape sz)
    {
        var p = new SKPath(); float w = (float)Math.Max(sz.Sx, .01), h = (float)Math.Max(sz.Sy, .01);
        if (sz.Shape == 2) { if (sz.Alt == 9) RRect(p, -w / 2, -h / 2, w, h, Math.Min(w, h) / 2 * sz.Cr / 100f); else p.AddRect(new SKRect(-w / 2, -h / 2, w / 2, h / 2)); }
        else if (sz.Shape == 3) Octo(p, w, h);
        else
        {
            if (sz.Alt == 9 && sz.Cr < 100) RRect(p, -w / 2, -h / 2, w, h, Math.Min(w, h) / 2 * sz.Cr / 100f);
            else if (Math.Abs(w - h) < 1e-6) p.AddCircle(0, 0, w / 2);
            else RRect(p, -w / 2, -h / 2, w, h, Math.Min(w, h) / 2);
        }
        return p;
    }

    static SKPath Xform(SKPath local, double x, double y, double rot, double ox = 0, double oy = 0, double rot2 = 0)
    {
        var m = SKMatrix.CreateTranslation((float)x, (float)y)
            .PreConcat(SKMatrix.CreateRotationDegrees((float)rot))
            .PreConcat(SKMatrix.CreateTranslation((float)ox, (float)oy))
            .PreConcat(SKMatrix.CreateRotationDegrees((float)rot2));
        var p = new SKPath(); local.Transform(m, p); return p;
    }

    /// <summary>Region / outline vertices with arc segments (Altium stores the arc per vertex).</summary>
    public static void VertsPath(SKPath p, IEnumerable<RegionVertex> verts)
    {
        bool first = true;
        void Go(float x, float y) { if (first) { p.MoveTo(x, y); first = false; } else p.LineTo(x, y); }
        foreach (var v in verts)
        {
            if (v.Round && v.R > 0)
            {
                double sa = v.Sa * D2R, ea = v.Ea * D2R;
                var s = new SKPoint((float)(v.Cx + Math.Cos(sa) * v.R), (float)(v.Cy + Math.Sin(sa) * v.R));
                var e = new SKPoint((float)(v.Cx + Math.Cos(ea) * v.R), (float)(v.Cy + Math.Sin(ea) * v.R));
                double ds = Math.Sqrt((s.X - v.X) * (s.X - v.X) + (s.Y - v.Y) * (s.Y - v.Y)), de = Math.Sqrt((e.X - v.X) * (e.X - v.X) + (e.Y - v.Y) * (e.Y - v.Y));
                var oval = new SKRect((float)(v.Cx - v.R), (float)(v.Cy - v.R), (float)(v.Cx + v.R), (float)(v.Cy + v.R));
                double sweep = ((v.Ea - v.Sa) % 360 + 360) % 360; if (sweep < 1e-9) sweep = 360;
                if (ds < de) { Go(s.X, s.Y); p.ArcTo(oval, (float)v.Sa, (float)sweep, false); }
                else { Go(e.X, e.Y); p.ArcTo(oval, (float)v.Ea, (float)-sweep, false); }
            }
            else Go((float)v.X, (float)v.Y);
        }
        p.Close();
    }

    static SKPoint Rot(double x, double y, double rot) { double c = Math.Cos(rot * D2R), s = Math.Sin(rot * D2R); return new SKPoint((float)(x * c - y * s), (float)(x * s + y * c)); }

    void Add(PcbObj o) { o.Index = Objs.Count; Objs.Add(o); }

    void Build()
    {
        var B = Board;
        foreach (var t in B.Tracks)
        {
            var p = new SKPath(); p.MoveTo((float)t.X1, (float)t.Y1); p.LineTo((float)t.X2, (float)t.Y2);
            Add(new PcbObj { Kind = PcbObjKind.Track, L = t.L, Net = t.Net, Comp = t.Comp, Mode = PaintMode.Stroke, W = (float)t.W, Path = p, Src = t,
                BB = BBPts(new[] { new SKPoint((float)t.X1, (float)t.Y1), new SKPoint((float)t.X2, (float)t.Y2) }, (float)t.W / 2),
                Snaps = new[] { new SKPoint((float)t.X1, (float)t.Y1), new SKPoint((float)t.X2, (float)t.Y2) } });
        }
        foreach (var a in B.Arcs)
        {
            var p = new SKPath();
            bool full = Math.Abs(a.Ea - a.Sa) >= 359.999 || a.Sa == a.Ea;
            double sweep = full ? 360 : ((a.Ea - a.Sa) % 360 + 360) % 360;
            var oval = new SKRect((float)(a.Cx - a.R), (float)(a.Cy - a.R), (float)(a.Cx + a.R), (float)(a.Cy + a.R));
            if (full) p.AddOval(oval); else p.AddArc(oval, (float)a.Sa, (float)sweep);
            float hw = (float)(a.R + a.W / 2);
            Add(new PcbObj { Kind = PcbObjKind.Arc, L = a.L, Net = a.Net, Comp = a.Comp, Mode = PaintMode.Stroke, W = (float)a.W, Path = p, Src = a,
                BB = new SKRect((float)a.Cx - hw, (float)a.Cy - hw, (float)a.Cx + hw, (float)a.Cy + hw),
                Snaps = new[] { new SKPoint((float)(a.Cx + Math.Cos(a.Sa * D2R) * a.R), (float)(a.Cy + Math.Sin(a.Sa * D2R) * a.R)),
                                new SKPoint((float)(a.Cx + Math.Cos(a.Ea * D2R) * a.R), (float)(a.Cy + Math.Sin(a.Ea * D2R) * a.R)), new SKPoint((float)a.Cx, (float)a.Cy) } });
        }
        foreach (var f in B.Fills)
        {
            double cx = (f.X1 + f.X2) / 2, cy = (f.Y1 + f.Y2) / 2, w = Math.Abs(f.X2 - f.X1), h = Math.Abs(f.Y2 - f.Y1);
            using var l = new SKPath(); l.AddRect(new SKRect((float)-w / 2, (float)-h / 2, (float)w / 2, (float)h / 2));
            var corners = new[] { (-w / 2, -h / 2), (w / 2, -h / 2), (w / 2, h / 2), (-w / 2, h / 2) }.Select(q => { var r = Rot(q.Item1, q.Item2, f.Rot); return new SKPoint(r.X + (float)cx, r.Y + (float)cy); }).ToArray();
            Add(new PcbObj { Kind = PcbObjKind.Fill, L = f.L, Net = f.Net, Comp = f.Comp, Mode = PaintMode.Fill, Path = Xform(l, cx, cy, f.Rot), BB = BBPts(corners), Src = f, Snaps = new[] { new SKPoint((float)cx, (float)cy) } });
        }
        foreach (var g in B.Regions)
        {
            if (g.Kind == 1 && !g.Cutout) continue;          // polygon cutout: removes pour, nothing to paint
            if (g.Outline.Count < 3) continue;
            if (g.Cutout) { var cp = new SKPath(); VertsPath(cp, g.Outline); Cutouts.Add(cp); continue; }
            var p = new SKPath { FillType = SKPathFillType.EvenOdd };
            VertsPath(p, g.Outline);
            foreach (var h in g.Holes) if (h.Count >= 3) VertsPath(p, h.Select(q => new RegionVertex { X = q.X, Y = q.Y }));
            Add(new PcbObj { Kind = PcbObjKind.Region, L = g.L, Net = g.Net, Comp = g.Comp, Mode = g.Kind is 2 or 4 ? PaintMode.Outline : PaintMode.EvenOdd, Path = p, Src = g,
                BB = BBPts(g.Outline.Select(v => new SKPoint((float)v.X, (float)v.Y))) });
        }
        foreach (var pd in B.Pads)
        {
            bool useBot = pd.L == Layers.Bottom && pd.Mode != 0;
            var sz = useBot ? pd.Bot : pd.Top;
            using var local = PadShapePath(sz);
            float rr = (float)(Math.Sqrt(sz.Sx * sz.Sx + sz.Sy * sz.Sy) / 2);
            var o = new PcbObj { Kind = PcbObjKind.Pad, L = pd.L, Net = pd.Net, Comp = pd.Comp, Mode = PaintMode.Fill, Path = Xform(local, pd.X, pd.Y, pd.Rot), Src = pd,
                BB = new SKRect((float)pd.X - rr, (float)pd.Y - rr, (float)pd.X + rr, (float)pd.Y + rr), Snaps = new[] { new SKPoint((float)pd.X, (float)pd.Y) } };
            Add(o);
            if (pd.Hole > 0)
            {
                float hd = (float)pd.Hole; SKPath hl;
                if (pd.HoleShape == 2 && pd.Slot > hd) { using var loc = new SKPath(); RRect(loc, (float)-pd.Slot / 2, -hd / 2, (float)pd.Slot, hd, hd / 2); hl = Xform(loc, pd.X, pd.Y, pd.Rot, pd.Hox, pd.Hoy, pd.SlotRot); }
                else if (pd.HoleShape == 1) { using var loc = new SKPath(); loc.AddRect(new SKRect(-hd / 2, -hd / 2, hd / 2, hd / 2)); hl = Xform(loc, pd.X, pd.Y, pd.Rot, pd.Hox, pd.Hoy); }
                else { var c = Rot(pd.Hox, pd.Hoy, pd.Rot); hl = new SKPath(); hl.AddCircle((float)pd.X + c.X, (float)pd.Y + c.Y, hd / 2); }
                Add(new PcbObj { Kind = PcbObjKind.Hole, L = Layers.PadHoles, Net = pd.Net, Comp = pd.Comp, Mode = PaintMode.Fill, Path = hl, Src = pd, Owner = o.Index,
                    BB = new SKRect((float)pd.X - hd, (float)pd.Y - hd, (float)pd.X + hd, (float)pd.Y + hd) });
            }
            if (pd.Net != Layers.None) NetPadCount[pd.Net] = NetPadCount.GetValueOrDefault(pd.Net) + 1;
            LabelObjs.Add(o);
        }
        foreach (var v in B.Vias)
        {
            var p = new SKPath(); p.AddCircle((float)v.X, (float)v.Y, (float)v.D / 2);
            var bb = new SKRect((float)(v.X - v.D / 2), (float)(v.Y - v.D / 2), (float)(v.X + v.D / 2), (float)(v.Y + v.D / 2));
            var o = new PcbObj { Kind = PcbObjKind.Via, L = Layers.MultiLayer, Net = v.Net, Comp = v.Comp, Mode = PaintMode.Fill, Path = p, BB = bb, Src = v, Snaps = new[] { new SKPoint((float)v.X, (float)v.Y) } };
            Add(o); LabelObjs.Add(o);
            if (v.Hole > 0) { var h = new SKPath(); h.AddCircle((float)v.X, (float)v.Y, (float)v.Hole / 2); Add(new PcbObj { Kind = PcbObjKind.Hole, L = Layers.ViaHoles, Net = v.Net, Comp = v.Comp, Mode = PaintMode.Fill, Path = h, BB = bb, Src = v, Owner = o.Index }); }
        }
        foreach (var t in B.Texts)
        {
            if (string.IsNullOrEmpty(t.Text) || t.Hidden) continue;
            var lines = t.Text.Split('\n');
            double wmax = lines.Max(l => l.Length) * t.H * (t.Ttf ? .55 : .62), hh = t.H * lines.Length * 1.15;
            var corners = new[] { (0.0, 0.0), (wmax, 0.0), (wmax, hh), (0.0, hh) }.Select(q =>
            {
                double x = t.Mirror ? -q.Item1 : q.Item1; var r = Rot(x, q.Item2 - (lines.Length - 1) * t.H * 1.15, t.Rot);
                return new SKPoint(r.X + (float)t.X, r.Y + (float)t.Y);
            }).ToArray();
            Add(new PcbObj { Kind = PcbObjKind.Text, L = t.L, Comp = t.Comp, Mode = PaintMode.Text, BB = BBPts(corners), Corners = corners, Src = t, Lines = lines });
        }
        foreach (var t in Objs.Where(o => o.Kind == PcbObjKind.Track && ((PcbTrack)o.Src).Poly == Layers.None)) LabelObjs.Add(t);   // not a hatched pour's lines

        foreach (var o in Objs) { if (!ByLayer.TryGetValue(o.L, out var l)) ByLayer[o.L] = l = new(); l.Add(o); }

        if (B.Outline.Count >= 3) { OutlinePath = new SKPath(); VertsPath(OutlinePath, B.Outline); }
        var all = OutlinePath != null ? OutlinePath.Bounds : SKRect.Empty;
        if (all.IsEmpty)
        {
            float a = float.MaxValue, b = float.MaxValue, c = float.MinValue, d = float.MinValue;
            foreach (var o in Objs) { if (o.BB.IsEmpty) continue; a = Math.Min(a, o.BB.Left); b = Math.Min(b, o.BB.Top); c = Math.Max(c, o.BB.Right); d = Math.Max(d, o.BB.Bottom); }
            all = a == float.MaxValue ? new SKRect(0, 0, 1000, 1000) : new SKRect(a, b, c, d);
        }
        Bounds = all;
        {
            float a = all.Left, b = all.Top, c = all.Right, d = all.Bottom;
            foreach (var o in Objs) { if (o.BB.IsEmpty) continue; a = Math.Min(a, o.BB.Left); b = Math.Min(b, o.BB.Top); c = Math.Max(c, o.BB.Right); d = Math.Max(d, o.BB.Bottom); }
            ContentBounds = new SKRect(a, b, c, d);
        }

        int n = B.Comps.Count;
        CompBB = new SKRect?[n]; CompArea = new SKRect?[n]; CompLayers = new HashSet<int>[n];
        for (int i = 0; i < n; i++) CompLayers[i] = new();
        foreach (var o in Objs)
        {
            if (o.Comp == Layers.None || o.Comp >= n) continue;
            CompLayers[o.Comp].Add(o.L);
            if (o.Kind == PcbObjKind.Text) continue;
            if (o.Kind != PcbObjKind.Hole) CompArea[o.Comp] = Union(CompArea[o.Comp], o.BB);
            if (o.L == Layers.MultiLayer || Layers.IsCopper(o.L) || o.L == Layers.TopOverlay || o.L == Layers.BottomOverlay) CompBB[o.Comp] = Union(CompBB[o.Comp], o.BB);
        }
    }

    const float StrokeCell = 600;

    static SKRect Union(SKRect? a, SKRect b) => a is { } r ? new SKRect(Math.Min(r.Left, b.Left), Math.Min(r.Top, b.Top), Math.Max(r.Right, b.Right), Math.Max(r.Bottom, b.Bottom)) : b;

    public SKRect? CompBox(int i) => i >= 0 && i < CompBB.Length ? CompBB[i] ?? CompArea[i] : null;

    // ---------- pictures ----------
    public SKPicture LayerPicture(int layer, PcbLayerInfo info)
    {
        lock (Sync)
        {
            if (_pics.TryGetValue(layer, out var p)) return p;
            p = Record(ByLayer.GetValueOrDefault(layer) ?? new(), layer, info.Color);
            _pics[layer] = p; return p;
        }
    }
    public SKPicture HlPicture(int layer, PcbLayerInfo info) => _hlPics.TryGetValue(layer, out var p) ? p : null;
    readonly Dictionary<int, List<PcbObj>> _hlLists = new();

    public void InvalidatePictures()
    {
        lock (Sync)
        {
            // layer pictures may still be playing in a tile worker: drop them and let the finalizer free them
            _pics.Clear();
            foreach (var p in _hlPics.Values) p.Dispose(); _hlPics.Clear();
            _hlColors.Clear();
            PicVersion++;
        }
    }
    readonly Dictionary<int, SKColor> _hlColors = new();

    public void RebuildHighlight(PcbViewState st)
    {
        lock (Sync) RebuildHighlightLocked(st);
    }

    void RebuildHighlightLocked(PcbViewState st)
    {
        foreach (var p in _hlPics.Values) p.Dispose(); _hlPics.Clear(); _hlLists.Clear();
        HasHighlight = st.HlNets.Count > 0 || st.HlComps.Count > 0;
        HlBounds = null;
        if (!HasHighlight) return;
        foreach (var o in Objs)
        {
            if ((o.Net != Layers.None && st.HlNets.Contains(o.Net)) || (o.Comp != Layers.None && st.HlComps.Contains(o.Comp)))
            {
                if (!_hlLists.TryGetValue(o.L, out var l)) _hlLists[o.L] = l = new();
                l.Add(o);
                if (o.Kind != PcbObjKind.Text) HlBounds = Union(HlBounds, o.BB);
            }
        }
        foreach (var (layer, list) in _hlLists)
            if (st.LayerMap.TryGetValue(layer, out var info))
                _hlPics[layer] = Record(list, Layers.IsPlane(layer) ? -1 : layer, info.Color);
    }

    SKPicture Record(List<PcbObj> list, int layer, SKColor color)
    {
        using var rec = new SKPictureRecorder();
        var c = rec.BeginRecording(Bounds.IsEmpty ? new SKRect(-1e6f, -1e6f, 1e6f, 1e6f) : InflateBig(Bounds));
        PaintObjs(c, list, layer, color);
        return rec.EndRecording();
    }
    static SKRect InflateBig(SKRect r) { var w = Math.Max(r.Width, r.Height); return new SKRect(r.Left - w, r.Top - w, r.Right + w, r.Bottom + w); }

    public void PaintObjs(SKCanvas c, IReadOnlyList<PcbObj> list, int layer, SKColor color)
    {
        using var fill = new SKPaint { Color = color, Style = SKPaintStyle.Fill, IsAntialias = true };
        using var stroke = new SKPaint { Color = color, Style = SKPaintStyle.Stroke, IsAntialias = true, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round };
        bool plane = Layers.IsPlane(layer) && OutlinePath != null;
        if (plane)
        {
            c.SaveLayer();
            c.DrawPath(OutlinePath, fill);
            fill.BlendMode = SKBlendMode.DstOut; stroke.BlendMode = SKBlendMode.DstOut;
        }
        // group strokes by width and by area (cells of StrokeCell mil) into one path each: few draw calls, and a
        // tile only pays for the cells it overlaps (the canvas quick-rejects the others)
        var byW = new Dictionary<(float, int, int), SKPath>();
        foreach (var o in list)
        {
            switch (o.Mode)
            {
                case PaintMode.Stroke:
                    var key = (o.W, (int)Math.Floor(o.BB.MidX / StrokeCell), (int)Math.Floor(o.BB.MidY / StrokeCell));
                    if (!byW.TryGetValue(key, out var sp)) byW[key] = sp = new SKPath();
                    sp.AddPath(o.Path); break;
                case PaintMode.Fill: c.DrawPath(o.Path, fill); break;
                case PaintMode.EvenOdd: c.DrawPath(o.Path, fill); break;
                case PaintMode.Outline: stroke.StrokeWidth = 0; c.DrawPath(o.Path, stroke); break;
                case PaintMode.Text: if (ShowText) PaintText(c, (PcbText)o.Src, o.Lines, fill); break;
            }
        }
        foreach (var (k, p) in byW) { stroke.StrokeWidth = k.Item1; c.DrawPath(p, stroke); p.Dispose(); }
        if (plane) c.Restore();
    }

    static void PaintText(SKCanvas c, PcbText t, string[] lines, SKPaint paint)
    {
        c.Save();
        c.Translate((float)t.X, (float)t.Y);
        c.RotateDegrees((float)t.Rot);
        c.Scale(t.Mirror ? -1 : 1, -1);
        float fs = (float)(t.Ttf ? t.H * 0.86 : t.H / 0.72);
        var tf = t.Ttf ? TextPainter.Face(t.Font, t.Bold, t.Italic) : TextPainter.StrokeFace;
        float lh = (float)(t.H * 1.15); int n = lines.Length;
        for (int i = 0; i < n; i++)
        {
            float baseY = -(n - 1 - i) * lh;
            if (t.Ttf) baseY -= fs * 0.2f; // ttf anchors at the bottom of the descender
            TextPainter.Draw(c, lines[i], 0, baseY, tf, fs, paint);
        }
        c.Restore();
    }

    // ---------- hit testing ----------
    static double SegDist(double px, double py, double x1, double y1, double x2, double y2)
    {
        double dx = x2 - x1, dy = y2 - y1, l2 = dx * dx + dy * dy;
        double t = l2 < 1e-12 ? 0 : Math.Clamp(((px - x1) * dx + (py - y1) * dy) / l2, 0, 1);
        double qx = x1 + t * dx - px, qy = y1 + t * dy - py; return Math.Sqrt(qx * qx + qy * qy);
    }

    static bool InPoly(SKPoint[] q, double x, double y)
    {
        bool inside = false;
        for (int i = 0, j = q.Length - 1; i < q.Length; j = i++)
            if ((q[i].Y > y) != (q[j].Y > y) && x < (q[j].X - q[i].X) * (y - q[i].Y) / (q[j].Y - q[i].Y) + q[i].X) inside = !inside;
        return inside;
    }

    /// <summary>Distance from a point to an object's copper / shape (0 = on it), or -1 when farther than tol.
    /// Areas (pours, fills, regions, text boxes) only report direct hits.</summary>
    public static double HitDist(PcbObj o, double x, double y, double tol)
    {
        var b = o.BB;
        if (x < b.Left - tol || x > b.Right + tol || y < b.Top - tol || y > b.Bottom + tol) return -1;
        double d;
        switch (o.Kind)
        {
            case PcbObjKind.Track: { var t = (PcbTrack)o.Src; d = SegDist(x, y, t.X1, t.Y1, t.X2, t.Y2) - t.W / 2; break; }
            case PcbObjKind.Arc:
                {
                    var a = (PcbArc)o.Src; double r = Math.Sqrt((x - a.Cx) * (x - a.Cx) + (y - a.Cy) * (y - a.Cy));
                    double ang = Math.Atan2(y - a.Cy, x - a.Cx) * 180 / Math.PI, sweep = ((a.Ea - a.Sa) % 360 + 360) % 360;
                    bool full = sweep < 1e-6 || Math.Abs(a.Ea - a.Sa) >= 359.999;
                    double rel = ((ang - a.Sa) % 360 + 360) % 360;
                    if (full || rel <= sweep) d = Math.Abs(r - a.R) - a.W / 2;
                    else
                    {
                        double e1 = Math.Sqrt(Math.Pow(x - (a.Cx + Math.Cos(a.Sa * D2R) * a.R), 2) + Math.Pow(y - (a.Cy + Math.Sin(a.Sa * D2R) * a.R), 2));
                        double e2 = Math.Sqrt(Math.Pow(x - (a.Cx + Math.Cos(a.Ea * D2R) * a.R), 2) + Math.Pow(y - (a.Cy + Math.Sin(a.Ea * D2R) * a.R), 2));
                        d = Math.Min(e1, e2) - a.W / 2;
                    }
                    break;
                }
            case PcbObjKind.Via: { var v = (PcbVia)o.Src; d = Math.Sqrt((x - v.X) * (x - v.X) + (y - v.Y) * (y - v.Y)) - v.D / 2; break; }
            case PcbObjKind.Pad:
                {
                    if (o.Path.Contains((float)x, (float)y)) return 0;
                    var p = (PcbPad)o.Src; var sz = p.L == Layers.Bottom && p.Mode != 0 ? p.Bot : p.Top;
                    double c = Math.Cos(-p.Rot * D2R), s = Math.Sin(-p.Rot * D2R), dx = x - p.X, dy = y - p.Y;
                    double lx = dx * c - dy * s, ly = dx * s + dy * c, w = sz.Sx / 2, h = sz.Sy / 2;
                    if (sz.Shape == 1 && !(sz.Alt == 9 && sz.Cr < 100))
                    {
                        // round / oval: capsule
                        double r = Math.Min(w, h), L = Math.Abs(w - h);
                        double qx = w > h ? Math.Clamp(lx, -L, L) : 0, qy = w > h ? 0 : Math.Clamp(ly, -L, L);
                        d = Math.Sqrt((lx - qx) * (lx - qx) + (ly - qy) * (ly - qy)) - r;
                    }
                    else { double ex = Math.Max(Math.Abs(lx) - w, 0), ey = Math.Max(Math.Abs(ly) - h, 0); d = Math.Sqrt(ex * ex + ey * ey); }
                    return d <= tol ? Math.Max(d, 1e-9) : -1;
                }
            case PcbObjKind.Text: return InPoly(o.Corners, x, y) ? 0 : -1;
            default: return o.Path.Contains((float)x, (float)y) ? 0 : -1;
        }
        return d <= 0 ? 0 : d <= tol ? d : -1;
    }

    public static bool HitObj(PcbObj o, double x, double y, double tol)
    {
        var b = o.BB;
        if (x < b.Left - tol || x > b.Right + tol || y < b.Top - tol || y > b.Bottom + tol) return false;
        switch (o.Kind)
        {
            case PcbObjKind.Track: { var t = (PcbTrack)o.Src; return SegDist(x, y, t.X1, t.Y1, t.X2, t.Y2) <= t.W / 2 + tol; }
            case PcbObjKind.Arc:
                {
                    var a = (PcbArc)o.Src; double d = Math.Sqrt((x - a.Cx) * (x - a.Cx) + (y - a.Cy) * (y - a.Cy));
                    if (Math.Abs(d - a.R) > a.W / 2 + tol) return false;
                    double ang = Math.Atan2(y - a.Cy, x - a.Cx) * 180 / Math.PI, sweep = ((a.Ea - a.Sa) % 360 + 360) % 360;
                    if (sweep < 1e-6) return true;
                    double rel = ((ang - a.Sa) % 360 + 360) % 360; return rel <= sweep + 1;
                }
            case PcbObjKind.Text: return InPoly(o.Corners, x, y);
            case PcbObjKind.Region when o.Mode == PaintMode.Outline:
                return o.Path.Contains((float)x, (float)y);
            default:
                return o.Path.Contains((float)x, (float)y) || (o.Kind is PcbObjKind.Via or PcbObjKind.Pad && Math.Abs(x - b.MidX) < tol && Math.Abs(y - b.MidY) < tol);
        }
    }

    public void Dispose()
    {
        lock (Sync)
        {
            InvalidatePictures();
            foreach (var o in Objs) o.Path?.Dispose();
            OutlinePath?.Dispose();
            foreach (var c in Cutouts) c.Dispose();
            Disposed = true;
        }
    }
    public bool Disposed { get; private set; }
}
