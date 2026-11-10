// Herald PCB Reader — Copyright (C) 2026 Johnny.ch.Tsai
// SPDX-License-Identifier: GPL-3.0-only
// Free software under the GNU General Public License version 3 (LICENSE.txt), WITHOUT ANY WARRANTY; with an
// additional permission to combine it with AvalonDock (Ms-PL), see NOTICE.txt.
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using AdApp.Panels;
using AdCore.Pcb;
using AdCore.Render;
using AdCore.Step;
using HelixToolkit.Wpf;
using SkiaSharp;
using static AdCore.Util.TextUtil;

namespace AdApp.Views;

public sealed class Pcb3DOptions
{
    public static Color MaskColor = Color.FromRgb(0x1E, 0x6B, 0x35);
    public static Color FinishColor = Color.FromRgb(0xD4, 0xAF, 0x37);
    public static Color Background = Color.FromRgb(0x2E, 0x34, 0x38);
    public static double BodyOpacity = 1.0;
    public static bool ShowBodies = true, BoxForBodyless = true, ShowSilk = true;
    /// <summary>Draw STEP bodies from the models embedded in the board (off: from their outlines, as before 2.9).</summary>
    public static bool UseStepModels = true;
    public static readonly (string name, Color c)[] Masks =
    {
        ("綠色", Color.FromRgb(0x1E, 0x6B, 0x35)), ("黑色", Color.FromRgb(0x18, 0x18, 0x1A)), ("藍色", Color.FromRgb(0x1C, 0x3F, 0x8C)),
        ("紅色", Color.FromRgb(0x9E, 0x1B, 0x1B)), ("白色", Color.FromRgb(0xE8, 0xE8, 0xE4)), ("紫色", Color.FromRgb(0x4B, 0x23, 0x6E)),
    };
    public static readonly (string name, Color c)[] Finishes =
    {
        ("化金 ENIG", Color.FromRgb(0xD4, 0xAF, 0x37)), ("噴錫 HASL", Color.FromRgb(0xC8, 0xC8, 0xC8)), ("OSP 裸銅", Color.FromRgb(0xC8, 0x7A, 0x4A)),
    };
    public static readonly (string name, Color c)[] Backgrounds =
    {
        ("深灰", Color.FromRgb(0x2E, 0x34, 0x38)), ("黑色", Color.FromRgb(0x10, 0x10, 0x12)), ("灰色", Color.FromRgb(0x6A, 0x70, 0x76)), ("白色", Color.FromRgb(0xF2, 0xF2, 0xF2)),
    };
}

/// <summary>3D board: extruded outline with rendered top/bottom textures + component bodies (extruded outlines,
/// cylinders, spheres; boxes for parts without a body). Tool column on the right, view bar bottom-left,
/// parts list (show / hide each part) top-left, floating info card shared with 2D.</summary>
public sealed class Pcb3DView : Grid
{
    readonly PcbView _owner;
    readonly HelixViewport3D _vp = new();
    readonly ModelVisual3D _root = new(), _boardVis = new(), _partsVis = new(), _lightsVis = new();
    readonly Model3DGroup _lights = new();
    readonly Dictionary<GeometryModel3D, int> _modelComp = new();
    readonly Dictionary<int, List<ModelVisual3D>> _parts = new();
    readonly List<(GeometryModel3D gm, int comp, Color col, double alpha)> _mats = new();
    readonly HashSet<int> _hidden = new();
    bool _boardHidden, _built;
    GeometryModel3D _topFace, _botFace;
    HashSet<int>[] _compNets = Array.Empty<HashSet<int>>();
    int _nStep, _nBox;
    const double K = 0.0254; // mil → mm
    const double Fov = 32;
    double _t;               // board thickness (mm)
    Point3D _c;              // board centre (board coordinates, mm)
    Matrix3D _m = Matrix3D.Identity; bool _flip;
    string _mode = "orbit";
    readonly Dictionary<string, ToggleButton> _toolBtn = new();
    Point _down, _last; bool _axisDrag;

    PcbViewState St => _owner.St;
    PerspectiveCamera Cam => (PerspectiveCamera)_vp.Camera;

    public Pcb3DView(PcbView owner)
    {
        _owner = owner;
        _vp.Background = new SolidColorBrush(Pcb3DOptions.Background);
        _vp.ShowCoordinateSystem = true; _vp.ShowViewCube = false; _vp.ZoomExtentsWhenLoaded = false;
        _vp.IsInertiaEnabled = false; _vp.ModelUpDirection = new Vector3D(0, 0, 1);
        _vp.Camera = new PerspectiveCamera { FieldOfView = Fov, UpDirection = new Vector3D(0, 0, 1) };
        _lightsVis.Content = _lights;
        _vp.Children.Add(_lightsVis);
        _vp.Children.Add(_root);
        Children.Add(_vp);
        ApplyLights();
        SetMode("orbit");

        _vp.PreviewMouseLeftButtonDown += (_, e) =>
        {
            _down = _last = e.GetPosition(_vp);
            if (_mode is "rx" or "ry" or "rz") { _axisDrag = true; _vp.CaptureMouse(); e.Handled = true; }
        };
        _vp.PreviewMouseMove += (_, e) =>
        {
            if (!_axisDrag) return;
            var p = e.GetPosition(_vp); double dx = p.X - _last.X, dy = p.Y - _last.Y; _last = p;
            const double k = 180 / 260.0;
            if (_mode == "rx") RotateLocal(new Vector3D(1, 0, 0), dy * k);
            else if (_mode == "ry") RotateLocal(new Vector3D(0, 1, 0), dx * k);
            else RotateLocal(new Vector3D(0, 0, 1), dx * k);
            e.Handled = true;
        };
        _vp.PreviewMouseLeftButtonUp += (_, e) =>
        {
            var p = e.GetPosition(_vp);
            if (_axisDrag) { _axisDrag = false; _vp.ReleaseMouseCapture(); e.Handled = true; }
            if ((p - _down).Length <= 4) OnClick(p);
        };

        Children.Add(BuildTools());
        Children.Add(BuildViewBar());
        Children.Add(BuildPartsPanel());
        Children.Add(_stepNote);
    }

    readonly Border _stepNote = new()
    {
        Child = new TextBlock { Text = "正在讀取元件的 3D 模型（STEP）…先以外框顯示", Foreground = Brushes.White, FontSize = 12.5 },
        Background = new SolidColorBrush(Color.FromArgb(0xD8, 0x18, 0x1B, 0x1D)), CornerRadius = new CornerRadius(6), Padding = new Thickness(10, 5, 10, 5),
        Margin = new Thickness(0, 64, 0, 0), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top,
        Visibility = Visibility.Collapsed, IsHitTestVisible = false,
    };

    // ================= scene =================
    public void Build()
    {
        _root.Children.Clear(); _boardVis.Children.Clear();
        var scene = _owner.Scene; var b = scene.Board;
        _t = b.Thickness * K;
        var bounds = scene.Bounds;
        _c = new Point3D(bounds.MidX * K, bounds.MidY * K, _t / 2);
        _compNets = new HashSet<int>[b.Comps.Count];
        foreach (var p in b.Pads) if (p.Comp >= 0 && p.Comp < b.Comps.Count && p.Net != Layers.None) (_compNets[p.Comp] ??= new()).Add(p.Net);

        var outline = OutlinePoints(scene);
        var tri = CuttingEarsTriangulator.Triangulate(outline.Select(p => new Point(p.X, p.Y)).ToList());
        var side = new MeshBuilder(false, false);
        for (int i = 0; i < outline.Count; i++)
        {
            var a = outline[i]; var c = outline[(i + 1) % outline.Count];
            side.AddQuad(new Point3D(a.X, a.Y, 0), new Point3D(c.X, c.Y, 0), new Point3D(c.X, c.Y, _t), new Point3D(a.X, a.Y, _t));
        }
        _boardVis.Children.Add(new ModelVisual3D { Content = new GeometryModel3D(side.ToMesh(), Mat(Color.FromRgb(0xB8, 0xA0, 0x6A))) { BackMaterial = Mat(Color.FromRgb(0xB8, 0xA0, 0x6A)) } });
        _topFace = _botFace = null;
        if (tri != null)
        {
            _topFace = Face(outline, tri, _t + 0.002, bounds, false);
            _botFace = Face(outline, tri, -0.002, bounds, true);
            _boardVis.Children.Add(new ModelVisual3D { Content = _topFace });
            _boardVis.Children.Add(new ModelVisual3D { Content = _botFace });
            RefreshTextures();
        }
        _root.Children.Add(_boardVis);
        _root.Children.Add(_partsVis);
        BuildParts();
        ApplyTransform();
        if (!_built) { _built = true; Home(); }
    }

    List<Point> OutlinePoints(PcbScene scene)
    {
        var pts = new List<Point>();
        if (scene.OutlinePath != null)
        {
            using var meas = new SKPathMeasure(scene.OutlinePath, true);
            float len = meas.Length, step = Math.Max(len / 2000f, 2f);
            for (float d = 0; d < len; d += step) if (meas.GetPosition(d, out var p)) pts.Add(new Point(p.X * K, p.Y * K));
        }
        if (pts.Count < 3)
        {
            var r = scene.Bounds;
            pts = new() { new(r.Left * K, r.Top * K), new(r.Right * K, r.Top * K), new(r.Right * K, r.Bottom * K), new(r.Left * K, r.Bottom * K) };
        }
        double area = 0; for (int i = 0; i < pts.Count; i++) { var a = pts[i]; var c = pts[(i + 1) % pts.Count]; area += a.X * c.Y - c.X * a.Y; }
        if (area < 0) pts.Reverse();
        return pts;
    }

    static GeometryModel3D Face(List<Point> outline, IList<int> tri, double z, SKRect bounds, bool bottom)
    {
        var mesh = new MeshGeometry3D();
        foreach (var p in outline)
        {
            mesh.Positions.Add(new Point3D(p.X, p.Y, z));
            mesh.TextureCoordinates.Add(new Point((p.X / K - bounds.Left) / bounds.Width, 1 - (p.Y / K - bounds.Top) / bounds.Height));
            mesh.Normals.Add(new Vector3D(0, 0, bottom ? -1 : 1));
        }
        for (int i = 0; i + 2 < tri.Count; i += 3)
        {
            if (bottom) { mesh.TriangleIndices.Add(tri[i]); mesh.TriangleIndices.Add(tri[i + 2]); mesh.TriangleIndices.Add(tri[i + 1]); }
            else { mesh.TriangleIndices.Add(tri[i]); mesh.TriangleIndices.Add(tri[i + 1]); mesh.TriangleIndices.Add(tri[i + 2]); }
        }
        return new GeometryModel3D(mesh, new DiffuseMaterial(Brushes.Gray));
    }

    void RefreshTextures()
    {
        if (_topFace != null) _topFace.Material = FaceMat(RenderSide(false));
        if (_botFace != null) _botFace.Material = FaceMat(RenderSide(true));
    }
    static Material FaceMat(BitmapSource bmp)
    {
        var g = new MaterialGroup();
        g.Children.Add(new DiffuseMaterial(new ImageBrush(bmp) { ViewportUnits = BrushMappingMode.Absolute, TileMode = TileMode.None }));
        g.Children.Add(new SpecularMaterial(new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)), 40));
        return g;
    }

    /// <summary>Texture for one side: mask, copper under mask, exposed pads in the finish colour, silkscreen, holes.</summary>
    BitmapSource RenderSide(bool bottom)
    {
        var scene = _owner.Scene; var bnd = scene.Bounds;
        int maxPx = 3072; float sc = maxPx / Math.Max(bnd.Width, bnd.Height);
        int w = Math.Max(16, (int)(bnd.Width * sc)), h = Math.Max(16, (int)(bnd.Height * sc));
        var info = new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var surf = SKSurface.Create(info);
        var c = surf.Canvas;
        var mask = Pcb3DOptions.MaskColor; var fin = Pcb3DOptions.FinishColor;
        c.Clear(new SKColor(mask.R, mask.G, mask.B));
        c.Translate(0, h); c.Scale(sc, -sc); c.Translate(-bnd.Left, -bnd.Top);
        int copper = bottom ? Layers.Bottom : Layers.Top, solder = bottom ? Layers.BottomSolder : Layers.TopSolder, silk = bottom ? Layers.BottomOverlay : Layers.TopOverlay;
        var lighter = new SKColor((byte)Math.Min(255, mask.R + 22), (byte)Math.Min(255, mask.G + 30), (byte)Math.Min(255, mask.B + 18));
        Paint(c, scene, copper, lighter);
        var finC = new SKColor(fin.R, fin.G, fin.B);
        Paint(c, scene, solder, finC);
        var pads = scene.Objs.Where(o => o.Kind == PcbObjKind.Pad && (o.L == copper || o.L == Layers.MultiLayer)).ToList();
        scene.PaintObjs(c, pads, copper, finC);
        if (Pcb3DOptions.ShowSilk) Paint(c, scene, silk, new SKColor(0xF2, 0xF2, 0xEE));
        if (St.HlNets.Count > 0)
        {
            var hl = scene.Objs.Where(o => o.Net != Layers.None && St.HlNets.Contains(o.Net) && (o.L == copper || o.L == Layers.MultiLayer)).ToList();
            scene.PaintObjs(c, hl, copper, new SKColor(0xFF, 0x4F, 0xA0));
        }
        var holes = scene.Objs.Where(o => o.Kind == PcbObjKind.Hole).ToList();
        scene.PaintObjs(c, holes, Layers.PadHoles, new SKColor(0x10, 0x10, 0x10));
        var bmp = new WriteableBitmap(w, h, 96, 96, PixelFormats.Pbgra32, null);
        bmp.Lock();
        surf.ReadPixels(info, bmp.BackBuffer, bmp.BackBufferStride, 0, 0);
        bmp.AddDirtyRect(new Int32Rect(0, 0, w, h)); bmp.Unlock(); bmp.Freeze();
        return bmp;
    }

    static void Paint(SKCanvas c, PcbScene scene, int layer, SKColor col)
    {
        if (scene.ByLayer.TryGetValue(layer, out var list)) scene.PaintObjs(c, list, layer, col);
    }

    static Material Mat(Color c, double opacity = 1, Color? emissive = null)
    {
        var g = new MaterialGroup();
        g.Children.Add(new DiffuseMaterial(new SolidColorBrush(Color.FromArgb((byte)(255 * Math.Clamp(opacity, 0.03, 1)), c.R, c.G, c.B))));
        g.Children.Add(new SpecularMaterial(new SolidColorBrush(Color.FromArgb(60, 255, 255, 255)), 30));
        if (emissive is { } e) g.Children.Add(new EmissiveMaterial(new SolidColorBrush(e)));
        return g;
    }

    static Color Bgr(int c)
    {
        if (c < 0 || c == 0x808080) return Color.FromRgb(0x44, 0x49, 0x4F);
        return Color.FromRgb((byte)(c & 255), (byte)((c >> 8) & 255), (byte)((c >> 16) & 255));
    }

    // ---------- STEP models embedded in the board ----------
    /// <summary>Per board: its STEP models as triangles (read and tessellated in the background the first time a 3D
    /// view of it is built), and the WPF meshes made from them, shared by every part (and view) that shows a model.</summary>
    sealed class StepState
    {
        public Dictionary<string, StepMesh> Meshes;
        public Task Loading;
        public long LoadMs;
        public readonly Dictionary<StepMeshPart, MeshGeometry3D> Wpf = new();
    }
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<PcbBoard, StepState> StepCache = new();
    bool _stepWaiting;

    /// <summary>Tests: how the STEP models of a board were read (null: not asked for yet).</summary>
    public static object StepInfo(PcbBoard b) => StepCache.TryGetValue(b, out var s)
        ? new { loading = s.Meshes == null, ms = s.LoadMs, models = s.Meshes?.Count ?? 0, faces = s.Meshes?.Values.Sum(m => m.Faces) ?? 0, failed = s.Meshes?.Values.Sum(m => m.Failed) ?? 0, triangles = s.Meshes?.Values.Sum(m => m.Triangles) ?? 0 }
        : null;
    int _nStepOutline;

    /// <summary>The STEP models of a board: null when it has none; Meshes null while they are being read.</summary>
    StepState StepFor(PcbBoard b)
    {
        var ids = b.Bodies.Where(x => x.Step && x.ModelId != "").Select(x => x.ModelId).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (ids.Count == 0 || string.IsNullOrEmpty(b.SourcePath)) return null;
        var s = StepCache.GetValue(b, _ => new StepState());
        if (s.Meshes == null && s.Loading == null)
        {
            string path = b.SourcePath;
            s.Loading = Task.Run(() =>
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                // a few threads: STEP text is large while it is read, and the memory it took is handed back right after
                try { s.Meshes = StepModel.LoadEmbedded(path, ids, Math.Min(4, AdCore.Project.Workspace.ParseThreads)); }
                catch (Exception) { s.Meshes = new(); }
                s.LoadMs = sw.ElapsedMilliseconds;
                GC.Collect(2, GCCollectionMode.Aggressive, true, true);
            });
        }
        if (s.Meshes == null && !_stepWaiting)
        {
            // parts are drawn from their outlines meanwhile; when the models are ready, again from the models
            _stepWaiting = true;
            s.Loading.ContinueWith(_ => Dispatcher.BeginInvoke(new Action(() => { _stepWaiting = false; if (_built && _owner.Scene.Board == b) BuildParts(); })), TaskScheduler.Default);
        }
        return s;
    }

    static MeshGeometry3D WpfMesh(StepState s, StepMeshPart p)
    {
        if (s.Wpf.TryGetValue(p, out var m)) return m;
        int nv = p.Pos.Length / 3;
        var pos = new Point3DCollection(nv); var nrm = new Vector3DCollection(nv);
        for (int i = 0; i < nv; i++)
        {
            pos.Add(new Point3D(p.Pos[i * 3], p.Pos[i * 3 + 1], p.Pos[i * 3 + 2]));
            nrm.Add(new Vector3D(p.Nrm[i * 3], p.Nrm[i * 3 + 1], p.Nrm[i * 3 + 2]));
        }
        m = new MeshGeometry3D { Positions = pos, Normals = nrm, TriangleIndices = new Int32Collection(p.Idx) };
        m.Freeze();
        s.Wpf[p] = m;
        p.Pos = p.Nrm = null; p.Idx = null;   // the WPF mesh holds them now
        return m;
    }

    /// <summary>Where Altium puts a STEP model: rotated about its own x, then y, then z (MODEL.3D.ROTX/Y/Z) and by the 2D
    /// rotation, its origin at MODEL.2D.X/Y, MODEL.3D.DZ above the top face; a bottom-side model is turned over about x
    /// (y mirrored about the origin) and hangs DZ below the bottom face. Checked against the outlines Altium stores.</summary>
    Matrix3D StepPlacement(PcbBody body)
    {
        var r = Xf.Mul(Xf.RotZ(body.Rot2D), Xf.Mul(Xf.RotZ(body.RotZ), Xf.Mul(Xf.RotY(body.RotY), Xf.RotX(body.RotX))));
        double ox = body.X * K, oy = body.Y * K, dz = body.Dz * K;
        var t = body.Bottom ? Xf.Mul(Xf.Tr(ox, oy, 0), Xf.Mul(Xf.RotX(180), Xf.Mul(Xf.Tr(0, 0, dz), r))) : Xf.Mul(Xf.Tr(ox, oy, _t + dz), r);
        var a = t.A;   // column vectors → WPF's row vectors
        return new Matrix3D(a[0], a[4], a[8], 0, a[1], a[5], a[9], 0, a[2], a[6], a[10], 0, a[3], a[7], a[11], 1);
    }

    void AddStepPart(StepState s, StepMesh sm, PcbBody body, int ci)
    {
        var tf = new MatrixTransform3D(StepPlacement(body)); tf.Freeze();
        var baseCol = Bgr(body.Color); double alpha = body.Opacity > 0 ? body.Opacity : 1;
        var group = new Model3DGroup { Transform = tf };
        foreach (var part in sm.Parts)
        {
            var col = part.HasColour ? Color.FromRgb((byte)Math.Clamp(part.R * 255, 0, 255), (byte)Math.Clamp(part.G * 255, 0, 255), (byte)Math.Clamp(part.B * 255, 0, 255)) : baseCol;
            var gm = new GeometryModel3D(WpfMesh(s, part), Mat(col, alpha));
            gm.BackMaterial = gm.Material;
            group.Children.Add(gm);
            _modelComp[gm] = ci;
            _mats.Add((gm, ci, col, alpha));
        }
        if (!_parts.TryGetValue(ci, out var l)) _parts[ci] = l = new();
        l.Add(new ModelVisual3D { Content = group });
    }

    void BuildParts()
    {
        _partsVis.Children.Clear(); _parts.Clear(); _mats.Clear(); _modelComp.Clear(); _nStep = _nBox = _nStepOutline = 0;
        if (!Pcb3DOptions.ShowBodies) { _stepNote.Visibility = Visibility.Collapsed; RefreshPartList(); return; }
        var b = _owner.Scene.Board; var scene = _owner.Scene;
        var step = Pcb3DOptions.UseStepModels ? StepFor(b) : null;
        _stepNote.Visibility = step is { Meshes: null } ? Visibility.Visible : Visibility.Collapsed;
        var with = new HashSet<int>();
        foreach (var body in b.Bodies)
        {
            int ci = body.Comp >= 0 && body.Comp < b.Comps.Count ? body.Comp : -1;
            if (body.Step && step?.Meshes != null && Pcb3DOptions.UseStepModels && step.Meshes.TryGetValue(body.ModelId, out var sm))
            {
                AddStepPart(step, sm, body, ci); _nStep++;
                if (ci >= 0) with.Add(ci);
                continue;
            }
            // through-hole parts often carry a negative standoff (pins below the board): start the body at the surface
            double so = Math.Max(0, body.Standoff), h = Math.Max(body.Overall - so, 0.4);
            var mb = new MeshBuilder(false, false); bool ok;
            Transform3D placed = null;
            if (body.Type == 2 && body.CylR > 0)
            {
                // Altium's cylinder: base circle at the model origin, axis +z, then rotated and placed like a STEP model
                // (a varistor disc or a fuse lies on its side through MODEL.3D.ROTX / ROTY)
                double ch = body.CylH > 0 ? body.CylH : h;
                mb.AddCylinder(new Point3D(0, 0, 0), new Point3D(0, 0, ch * K), body.CylR * K, 36, true, true); ok = true;
                placed = new MatrixTransform3D(StepPlacement(body));
            }
            else if (body.Type == 3 && body.SphR > 0)
            {
                mb.AddSphere(new Point3D(0, 0, 0), body.SphR * K, 28, 16); ok = true;
                placed = new MatrixTransform3D(StepPlacement(body));
            }
            else if (body.Pts.Count >= 3) ok = Extrude(mb, body.Pts.Select(p => new Point(p.X * K, p.Y * K)).ToList(), so * K, (so + h) * K);
            else ok = false;
            if (!ok) continue;
            if (body.Step) _nStepOutline++;
            AddPart(mb, ci, body.Bottom, Bgr(body.Color), body.Opacity > 0 ? body.Opacity : 1, placed);
            if (ci >= 0) with.Add(ci);
        }
        if (Pcb3DOptions.BoxForBodyless)
        {
            for (int i = 0; i < b.Comps.Count; i++)
            {
                if (with.Contains(i) || scene.CompBB[i] is not { } r) continue;
                if (b.Pads.Count(p => p.Comp == i) < 2) continue;
                var comp = b.Comps[i]; double hh = (comp.Height > 0 ? comp.Height : 31.5) * K;
                var mb = new MeshBuilder(false, false);
                mb.AddBox(new Point3D(r.MidX * K, r.MidY * K, hh / 2), r.Width * K * 0.92, r.Height * K * 0.92, hh);
                AddPart(mb, i, comp.Bottom, Color.FromRgb(0x8E, 0x96, 0x9D), 0.55);
                _nBox++;
            }
        }
        UpdateMaterials(); ApplyHidden(); RefreshPartList();
    }

    void AddPart(MeshBuilder mb, int ci, bool bottom, Color col, double alpha, Transform3D placed = null)
    {
        // modelled on the board surface at z = 0 upwards; top parts sit on z = t, bottom parts are mirrored below z = 0
        // (placed: already in board space, e.g. a rotated cylinder)
        var gm = new GeometryModel3D(mb.ToMesh(), Mat(col, alpha))
        {
            Transform = placed ?? (bottom ? new ScaleTransform3D(1, 1, -1) : new TranslateTransform3D(0, 0, _t)),
        };
        gm.BackMaterial = gm.Material;
        var vis = new ModelVisual3D { Content = gm };
        _modelComp[gm] = ci;
        _mats.Add((gm, ci, col, alpha));
        if (!_parts.TryGetValue(ci, out var l)) _parts[ci] = l = new();
        l.Add(vis);
    }

    static bool Extrude(MeshBuilder mb, List<Point> poly, double z0, double z1)
    {
        var clean = new List<Point>();
        foreach (var p in poly) if (clean.Count == 0 || (p - clean[^1]).Length > 1e-6) clean.Add(p);
        if (clean.Count > 2 && (clean[0] - clean[^1]).Length < 1e-6) clean.RemoveAt(clean.Count - 1);
        if (clean.Count < 3) return false;
        double area = 0; for (int i = 0; i < clean.Count; i++) { var a = clean[i]; var c = clean[(i + 1) % clean.Count]; area += a.X * c.Y - c.X * a.Y; }
        if (Math.Abs(area) < 1e-9) return false;
        if (area < 0) clean.Reverse();
        for (int i = 0; i < clean.Count; i++)
        {
            var a = clean[i]; var c = clean[(i + 1) % clean.Count];
            mb.AddQuad(new Point3D(a.X, a.Y, z0), new Point3D(c.X, c.Y, z0), new Point3D(c.X, c.Y, z1), new Point3D(a.X, a.Y, z1));
        }
        var tri = CuttingEarsTriangulator.Triangulate(clean);
        if (tri != null)
        {
            int baseTop = mb.Positions.Count;
            foreach (var p in clean) mb.Positions.Add(new Point3D(p.X, p.Y, z1));
            for (int i = 0; i + 2 < tri.Count; i += 3) { mb.TriangleIndices.Add(baseTop + tri[i]); mb.TriangleIndices.Add(baseTop + tri[i + 1]); mb.TriangleIndices.Add(baseTop + tri[i + 2]); }
            int baseBot = mb.Positions.Count;
            foreach (var p in clean) mb.Positions.Add(new Point3D(p.X, p.Y, z0));
            for (int i = 0; i + 2 < tri.Count; i += 3) { mb.TriangleIndices.Add(baseBot + tri[i]); mb.TriangleIndices.Add(baseBot + tri[i + 2]); mb.TriangleIndices.Add(baseBot + tri[i + 1]); }
        }
        return true;
    }

    /// <summary>Selection (blue), highlighted parts (orange) and dimming of the rest, without rebuilding geometry.</summary>
    void UpdateMaterials()
    {
        var sel = new HashSet<int>();
        if (St.Sel is { Items: { } items }) foreach (var i in items.Where(i => i.IsComp)) sel.Add(i.Index);
        else if (St.SelectedComp >= 0) sel.Add(St.SelectedComp);
        bool any = St.HlComps.Count + St.HlNets.Count > 0;
        foreach (var (gm, ci, col, alpha) in _mats)
        {
            bool s = sel.Contains(ci);
            bool lit = any && ci >= 0 && (St.HlComps.Contains(ci) || (_compNets.Length > ci && _compNets[ci] != null && _compNets[ci].Overlaps(St.HlNets)));
            double a = alpha * Pcb3DOptions.BodyOpacity; if (any && !lit && !s) a = Math.Min(a, 0.22);
            Color? em = s ? Color.FromArgb(150, 0x2D, 0x5B, 0xFF) : lit ? Color.FromArgb(140, 0xC0, 0x62, 0x1A) : null;
            gm.Material = Mat(col, a, em); gm.BackMaterial = gm.Material;
        }
    }

    public void RefreshHighlight() { if (!_built) return; RefreshTextures(); UpdateMaterials(); }
    public void SelectionUpdated() { if (!_built) return; UpdateMaterials(); SyncPartSelection(); }

    // ---------- visibility of parts ----------
    public bool HasPart(int ci) => _parts.ContainsKey(ci);
    public bool IsHidden(int ci) => _hidden.Contains(ci);
    public void SetHidden(int ci, bool hide)
    {
        if (hide) _hidden.Add(ci); else _hidden.Remove(ci);
        ApplyHidden(); RefreshPartList(); _owner.RefreshCard();
    }

    void ApplyHidden()
    {
        _partsVis.Children.Clear();
        foreach (var (ci, list) in _parts) if (!_hidden.Contains(ci)) foreach (var v in list) _partsVis.Children.Add(v);
        if (_boardHidden) _root.Children.Remove(_boardVis); else if (!_root.Children.Contains(_boardVis)) _root.Children.Insert(0, _boardVis);
    }

    // ================= transform / camera =================
    Matrix3D Total
    {
        get
        {
            var f = Matrix3D.Identity;
            if (_flip) f.RotateAt(new Quaternion(new Vector3D(0, 1, 0), 180), _c);
            return f * _m;
        }
    }
    void ApplyTransform() => _root.Transform = new MatrixTransform3D(Total);

    /// <summary>Turn the board about one of its own axes through its centre (X / Y / Z tools).</summary>
    void RotateLocal(Vector3D axis, double deg)
    {
        var r = Matrix3D.Identity; r.RotateAt(new Quaternion(axis, deg), _c);
        _m = r * _m; ApplyTransform();
    }

    Point3D Center => Total.Transform(_c);
    double FitDistance()
    {
        var r = _owner.Scene.Bounds; double span = Math.Max(r.Width, r.Height) * K;
        return span / (2 * Math.Tan(Fov / 2 * Math.PI / 180)) * 1.3 + _t * 4;
    }

    void SetView(Vector3D toCam, Vector3D up, string name = null)
    {
        double d = FitDistance(); toCam.Normalize();
        var c = Center; var cam = Cam;
        cam.FieldOfView = Fov; cam.Position = c + toCam * d; cam.LookDirection = -toCam * d; cam.UpDirection = up;
        cam.NearPlaneDistance = Math.Max(0.01, d / 2000); cam.FarPlaneDistance = d * 30;
        _lastView = name; SyncViewBar();
    }
    string _lastView = "iso";

    public void IsoView() => SetView(new Vector3D(0.42, -0.78, 0.62), new Vector3D(0, 0, 1), "iso");
    public void TopView() => SetView(new Vector3D(0, 0, 1), new Vector3D(0, 1, 0), "top");
    public void BottomView() => SetView(new Vector3D(0, 0, -1), new Vector3D(0, 1, 0), "bottom");
    public void SideView() => SetView(new Vector3D(0, -1.05, 0.04), new Vector3D(0, 0, 1), "side");
    public void FlipView(bool bottom) { _flip = bottom; ApplyTransform(); SyncViewBar(); }

    /// <summary>Default view: isometric, top up, board not rotated.</summary>
    public void Home()
    {
        _m = Matrix3D.Identity;
        if (St.BottomView) _owner.SetBottomView(false);
        _flip = false; ApplyTransform();
        SetMode("orbit"); IsoView();
    }

    public void FitView()
    {
        var cam = Cam; var dir = cam.LookDirection; dir.Normalize(); double d = FitDistance(); var c = Center;
        cam.Position = c - dir * d; cam.LookDirection = dir * d;
    }

    public void FocusOn(SKRect bb)
    {
        var cam = Cam; var dir = cam.LookDirection; dir.Normalize();
        var c = Total.Transform(new Point3D(bb.MidX * K, bb.MidY * K, _t));
        double size = Math.Max(Math.Max(bb.Width, bb.Height) * K, 2), d = size * 4 / (2 * Math.Tan(Fov / 2 * Math.PI / 180));
        cam.Position = c - dir * d; cam.LookDirection = dir * d;
    }

    void Dolly(double f) { var cam = Cam; cam.Position = cam.Position + cam.LookDirection * (1 - f); cam.LookDirection *= f; }

    // ================= lights =================
    public void ApplyLights()
    {
        var cfg = Settings.Current; double I = cfg.LightIntensity;
        static Color Gray(double v) { byte b = (byte)Math.Clamp(v * 255, 0, 255); return Color.FromRgb(b, b, b); }
        double az = cfg.LightAzimuth * Math.PI / 180, el = cfg.LightElevation * Math.PI / 180;
        var dir = new Vector3D(-Math.Cos(el) * Math.Cos(az), -Math.Cos(el) * Math.Sin(az), -Math.Sin(el));
        _lights.Children.Clear();
        _lights.Children.Add(new AmbientLight(Gray(cfg.LightAmbient * I)));
        _lights.Children.Add(new DirectionalLight(Gray(0.85 * I), dir));
        _lights.Children.Add(new DirectionalLight(Gray(0.32 * I), new Vector3D(-dir.X, -dir.Y, dir.Z)));
        _lights.Children.Add(new DirectionalLight(Gray(0.45 * I), new Vector3D(0.2, 0.3, 1)));   // from below, for the bottom side
    }

    // ================= input =================
    void OnClick(Point p)
    {
        if (!_built) return;
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0, shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        PcbSelection sel = null;
        foreach (var h in Viewport3DHelper.FindHits(_vp.Viewport, p))
        {
            if (h.Model is not GeometryModel3D gm) continue;
            if (_modelComp.TryGetValue(gm, out var ci)) { if (ci >= 0 && St.Filter.Contains(PickClass.Comp)) { sel = PcbSelection.Comp(ci); break; } continue; }
            if (gm == _topFace || gm == _botFace)
            {
                var inv = Total; inv.Invert(); var lp = inv.Transform(h.Position);
                double mmPx = h.Distance * 2 * Math.Tan(Fov / 2 * Math.PI / 180) / Math.Max(1, _vp.ActualHeight);
                sel = St.Pick(lp.X / K, lp.Y / K, 4 * mmPx / K, gm == _topFace ? "top" : "bottom");
            }
            break;
        }
        if (sel != null && ctrl)
        {
            if (sel.IsComp) _owner.HighlightComp(sel.Index, shift);
            else { var ob = _owner.Scene.Objs[sel.Index]; if (ob.Net != Layers.None) _owner.HighlightNet(ob.Net, shift); else if (ob.Comp != Layers.None) _owner.HighlightComp(ob.Comp, shift); }
        }
        _owner.SetSelection(sel, true);
    }

    public bool HandleKey(Key key, ModifierKeys mod)
    {
        if (mod != ModifierKeys.None && mod != ModifierKeys.Shift) return false;
        switch (key)
        {
            case Key.H when St.SelectedComp >= 0 && HasPart(St.SelectedComp): SetHidden(St.SelectedComp, true); return true;
            case Key.Home: Home(); return true;
            case Key.D0: case Key.NumPad0: TopView(); return true;
            case Key.I: IsoView(); return true;
            case Key.S when mod == ModifierKeys.None: SideView(); return true;
            case Key.O: SetMode("orbit"); return true;
            case Key.P: SetMode("pan"); return true;
            case Key.Z: SetMode("zoom"); return true;
            case Key.A: ToggleAxes(); return true;
            case Key.Add: case Key.OemPlus: Dolly(0.8); return true;
            case Key.Subtract: case Key.OemMinus: Dolly(1.25); return true;
        }
        return false;
    }

    static readonly MouseGesture None = new(MouseAction.WheelClick, ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift | ModifierKeys.Windows);

    void SetMode(string m)
    {
        _mode = m;
        var L = new MouseGesture(MouseAction.LeftClick); var R = new MouseGesture(MouseAction.RightClick); var Mid = new MouseGesture(MouseAction.MiddleClick);
        _vp.RotateGesture = m == "orbit" ? L : Mid; _vp.RotateGesture2 = None;
        _vp.PanGesture = m == "pan" ? L : R; _vp.PanGesture2 = m == "pan" ? R : None;
        _vp.ZoomGesture = m == "zoom" ? L : None; _vp.ZoomGesture2 = None;
        _vp.Cursor = m switch { "pan" => Cursors.SizeAll, "zoom" => Cursors.SizeNS, "rx" => Cursors.SizeNS, "ry" or "rz" => Cursors.SizeWE, _ => null };
        foreach (var (k, b) in _toolBtn) if (k is "orbit" or "pan" or "zoom" or "rx" or "ry" or "rz") b.IsChecked = k == m;
    }

    void ToggleAxes() { _vp.ShowCoordinateSystem = !_vp.ShowCoordinateSystem; if (_toolBtn.TryGetValue("axes", out var b)) b.IsChecked = _vp.ShowCoordinateSystem; }

    // ================= overlays =================
    static readonly Brush OverlayBg = new SolidColorBrush(Color.FromArgb(0xD8, 0x18, 0x1B, 0x1D));
    static readonly Brush OverlayLine = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF));

    UIElement BuildTools()
    {
        var col = new StackPanel();
        ToggleButton T(string key, string glyph, string text, string tip, Action a, Brush glyphBrush = null, bool mdl = true)
        {
            var sp = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
            sp.Children.Add(new TextBlock { Text = glyph, FontFamily = mdl ? new FontFamily("Segoe MDL2 Assets") : new FontFamily("Consolas"), FontSize = mdl ? 18 : 16, FontWeight = mdl ? FontWeights.Normal : FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, Foreground = glyphBrush ?? Ui.Text });
            sp.Children.Add(new TextBlock { Text = text, FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center, Foreground = Ui.Text, Margin = new Thickness(0, 2, 0, 0) });
            var b = new ToggleButton { Content = sp, Width = 58, Padding = new Thickness(2, 5, 2, 5), Margin = new Thickness(0, 1, 0, 1), ToolTip = tip };
            b.Click += (_, _) => { a(); if (key is not ("orbit" or "pan" or "zoom" or "rx" or "ry" or "rz" or "axes" or "parts")) b.IsChecked = false; _owner.Focus(); };
            _toolBtn[key] = b; col.Children.Add(b); return b;
        }
        void Hr() => col.Children.Add(new Border { Height = 1, Margin = new Thickness(8, 4, 8, 4), Background = OverlayLine });
        T("orbit", "", "旋轉", "自由旋轉：左鍵拖曳繞中心旋轉 (O)", () => SetMode("orbit"));
        T("pan", "", "平移", "平移：左鍵拖曳移動畫面 (P)", () => SetMode("pan"));
        T("zoom", "", "縮放", "縮放：左鍵上下拖曳 (Z)", () => SetMode("zoom"));
        Hr();
        T("rx", "X", "X 軸", "繞板子的 X 軸旋轉：左鍵上下拖曳", () => SetMode("rx"), new SolidColorBrush(Color.FromRgb(0xFF, 0x7A, 0x6B)), false);
        T("ry", "Y", "Y 軸", "繞板子的 Y 軸旋轉：左鍵左右拖曳", () => SetMode("ry"), new SolidColorBrush(Color.FromRgb(0x7F, 0xE0, 0x8A)), false);
        T("rz", "Z", "Z 軸", "繞板子的 Z 軸旋轉：左鍵左右拖曳", () => SetMode("rz"), new SolidColorBrush(Color.FromRgb(0x7F, 0xB0, 0xFF)), false);
        Hr();
        T("axes", "", "座標軸", "顯示 / 隱藏左下角座標軸 (A)", ToggleAxes).IsChecked = true;
        T("parts", "", "部件", "顯示 / 隱藏部件清單（逐一控制每個元件顯示或隱藏）", () => { _partsUserSet = true; ShowParts(_toolBtn["parts"].IsChecked == true); }).IsChecked = true;
        T("light", "", "燈光", "調整燈光方向與亮度", () => ShowLightPopup(_toolBtn["light"]));
        T("disp", "", "顯示", "防焊顏色、表面處理、元件本體與背景", () => ShowDisplayPopup(_toolBtn["disp"]));
        Hr();
        T("fit", "", "全圖", "整塊板子放進畫面 (F)", FitView);
        T("home", "", "回預設", "回到預設視角：等角、頂面朝上、未旋轉 (Home)", Home);
        return new Border
        {
            Child = new ScrollViewer { Content = col, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }, Background = OverlayBg, BorderBrush = OverlayLine, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10), Padding = new Thickness(4), Margin = new Thickness(0, 12, 12, 12), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center,
        };
    }

    readonly Dictionary<string, ToggleButton> _viewBtn = new();
    UIElement BuildViewBar()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        void V(string key, string text, string tip, Action a)
        {
            var b = new ToggleButton { Content = text, Padding = new Thickness(10, 4, 10, 4), FontSize = 13, Margin = new Thickness(1, 0, 1, 0), ToolTip = tip };
            b.Click += (_, _) => { a(); SyncViewBar(); _owner.Focus(); };
            _viewBtn[key] = b; row.Children.Add(b);
        }
        V("top", "頂視", "從上方看 (0)", TopView);
        V("bottom", "底視", "從下方看", BottomView);
        V("iso", "等角", "等角視角 (I)", IsoView);
        V("side", "側視", "從側面看 (S)", SideView);
        row.Children.Add(new Border { Width = 1, Margin = new Thickness(4, 3, 4, 3), Background = OverlayLine });
        V("flip", "翻面", "把板子翻過來看底面 (B)", () => _owner.SetBottomView(!St.BottomView));
        V("home", "回預設", "回到預設視角 (Home)", Home);
        return new Border
        {
            Child = row, Background = OverlayBg, BorderBrush = OverlayLine, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(4),
            Margin = new Thickness(100, 0, 12, 12), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom,
        };
    }

    void SyncViewBar()
    {
        foreach (var (k, b) in _viewBtn) b.IsChecked = k == "flip" ? _flip : k != "home" && k == _lastView;
    }

    // ---------- parts list ----------
    Border _partsPanel;
    bool _partsUserSet;
    void ShowParts(bool on)
    {
        _partsPanel.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        if (_toolBtn.TryGetValue("parts", out var b)) b.IsChecked = on;
    }
    readonly ListBox _partList = new() { Background = Brushes.Transparent, BorderThickness = new Thickness(0) };
    readonly TextBox _partQ = new() { Margin = new Thickness(0, 4, 0, 4), ToolTip = "篩選部件：位號、型號、封裝" };
    readonly Dictionary<string, ToggleButton> _partTabs = new();
    string _partTab = "all";

    UIElement BuildPartsPanel()
    {
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        var close = new Button { Content = "✕", Width = 24, Height = 22, Padding = new Thickness(0), Foreground = Ui.Dim, ToolTip = "收起（右側工具列「部件」可再打開）" };
        close.Click += (_, _) => { _partsUserSet = true; ShowParts(false); };
        DockPanel.SetDock(close, Dock.Right); head.Children.Add(close);
        head.Children.Add(new TextBlock { Text = "部件顯示", FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Ui.Text, VerticalAlignment = VerticalAlignment.Center });
        var tabs = new UniformGrid { Rows = 1 };
        foreach (var (k, n) in new[] { ("all", "全部"), ("shown", "顯示中"), ("hidden", "已隱藏") })
        {
            var b = new ToggleButton { Content = n, Padding = new Thickness(4, 3, 4, 3), FontSize = 12, IsChecked = k == "all" };
            b.Click += (_, _) => { _partTab = k; foreach (var (kk, bb) in _partTabs) bb.IsChecked = kk == k; RefreshPartList(); };
            _partTabs[k] = b; tabs.Children.Add(b);
        }
        _partQ.TextChanged += (_, _) => RefreshPartList();
        var acts = new UniformGrid { Rows = 1, Margin = new Thickness(0, 4, 0, 0) };
        var showAll = Ui.Btn("全部顯示", () => { _hidden.Clear(); _boardHidden = false; ApplyHidden(); RefreshPartList(); _owner.RefreshCard(); });
        var hideAll = Ui.Btn("全部隱藏", () => { foreach (var k in _parts.Keys) _hidden.Add(k); ApplyHidden(); RefreshPartList(); _owner.RefreshCard(); });
        acts.Children.Add(showAll); acts.Children.Add(hideAll);
        var dock = new DockPanel();
        foreach (var e in new UIElement[] { head, tabs, _partQ }) { DockPanel.SetDock(e, Dock.Top); dock.Children.Add(e); }
        DockPanel.SetDock(acts, Dock.Bottom); dock.Children.Add(acts);
        dock.Children.Add(_partList);
        _partsPanel = new Border
        {
            Child = dock, Background = OverlayBg, BorderBrush = OverlayLine, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(8),
            Width = 270, Margin = new Thickness(12, 12, 12, 64), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, MaxHeight = 560,
        };
        SizeChanged += (_, _) =>
        {
            _partsPanel.MaxHeight = Math.Max(200, ActualHeight - 90);
            // narrow pane (e.g. split view): keep the board visible unless the user opened the list on purpose
            if (!_partsUserSet) ShowParts(ActualWidth >= 900);
        };
        return _partsPanel;
    }

    void RefreshPartList()
    {
        var b = _owner.Scene.Board;
        var rows = new List<(int key, string des, string sub, string side)> { (-2, "PCB 板", "板體、防焊、絲印", "") };
        foreach (var ci in _parts.Keys.Where(k => k >= 0).OrderBy(k => b.Comps[k].Des, Natural))
        {
            var c = b.Comps[ci];
            rows.Add((ci, c.Des == "" ? "#" + ci : c.Des, string.Join(" · ", new[] { c.Comment, c.Pattern }.Where(x => x != "")), c.Bottom ? "B" : "T"));
        }
        bool Hidden(int k) => k == -2 ? _boardHidden : _hidden.Contains(k);
        int nh = rows.Count(r => Hidden(r.key));
        _partTabs["all"].Content = $"全部 {rows.Count}"; _partTabs["shown"].Content = $"顯示中 {rows.Count - nh}"; _partTabs["hidden"].Content = $"已隱藏 {nh}";
        var q = _partQ.Text.Trim().ToLowerInvariant();
        _partList.Items.Clear();
        foreach (var r in rows)
        {
            if (_partTab == "shown" && Hidden(r.key) || _partTab == "hidden" && !Hidden(r.key)) continue;
            if (q != "" && !(r.des + " " + r.sub).ToLowerInvariant().Contains(q)) continue;
            int key = r.key;
            var cb = new CheckBox { IsChecked = !Hidden(key), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0), ToolTip = "顯示 / 隱藏" };
            cb.Click += (_, _) =>
            {
                bool show = cb.IsChecked == true;
                if (key == -2) { _boardHidden = !show; ApplyHidden(); RefreshPartList(); }
                else SetHidden(key, !show);
            };
            var name = new StackPanel();
            name.Children.Add(new TextBlock { Text = r.des, FontWeight = FontWeights.SemiBold, FontSize = 12.5, Foreground = Hidden(key) ? Ui.Dim : Ui.Text, TextTrimming = TextTrimming.CharacterEllipsis });
            if (r.sub != "") name.Children.Add(new TextBlock { Text = r.sub, FontSize = 11, Foreground = Ui.Dim, TextTrimming = TextTrimming.CharacterEllipsis });
            var row = new DockPanel { Tag = key, LastChildFill = true };
            DockPanel.SetDock(cb, Dock.Left); row.Children.Add(cb);
            if (r.side != "")
            {
                var sb = new Border { BorderBrush = Ui.Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3), Padding = new Thickness(4, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center, ToolTip = r.side == "B" ? "底面" : "頂面",
                    Child = new TextBlock { Text = r.side, FontFamily = new FontFamily("Consolas"), FontSize = 10.5, Foreground = Ui.Dim } };
                DockPanel.SetDock(sb, Dock.Right); row.Children.Add(sb);
            }
            row.Children.Add(name);
            if (key >= 0)
                name.MouseLeftButtonUp += (_, _) =>
                {
                    if (_hidden.Contains(key)) SetHidden(key, false);
                    _owner.SetSelection(PcbSelection.Comp(key), true);
                    if (_owner.Scene.CompBox(key) is { } bb) FocusOn(bb);
                };
            _partList.Items.Add(new ListBoxItem { Content = row, Padding = new Thickness(2, 2, 2, 2), HorizontalContentAlignment = HorizontalAlignment.Stretch });
        }
        if (_partList.Items.Count == 0)
            _partList.Items.Add(new ListBoxItem { IsEnabled = false, Content = new TextBlock { Text = _partTab == "hidden" ? "目前沒有隱藏的部件。取消勾選，或選取元件後按 H 隱藏。" : "沒有符合的部件。", TextWrapping = TextWrapping.Wrap, Foreground = Ui.Dim, FontSize = 12 } });
        SyncPartSelection();
    }

    void SyncPartSelection()
    {
        int sc = St.SelectedComp;
        foreach (var it in _partList.Items.OfType<ListBoxItem>())
            it.Background = it.Content is DockPanel { Tag: int k } && k == sc && sc >= 0 ? new SolidColorBrush(Color.FromArgb(0x60, 0x3D, 0x8E, 0xE0)) : Brushes.Transparent;
    }

    // ---------- popups ----------
    static Popup Pop(UIElement anchor, UIElement content)
    {
        var pop = new Popup
        {
            PlacementTarget = anchor, Placement = PlacementMode.Left, StaysOpen = false, AllowsTransparency = true, HorizontalOffset = -6,
            Child = new Border { Background = Ui.Bg2, BorderBrush = Ui.Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(12), Width = 280, Child = content },
        };
        pop.IsOpen = true;
        return pop;
    }

    static FrameworkElement SliderRow(string label, double min, double max, double val, string fmt, Action<double> set)
    {
        var g = new DockPanel { Margin = new Thickness(0, 3, 0, 3) };
        var v = new TextBlock { Width = 44, TextAlignment = TextAlignment.Right, Foreground = Ui.Dim, FontFamily = new FontFamily("Consolas"), VerticalAlignment = VerticalAlignment.Center, Text = val.ToString(fmt) };
        var s = new Slider { Minimum = min, Maximum = max, Value = val, VerticalAlignment = VerticalAlignment.Center };
        s.ValueChanged += (_, e) => { v.Text = e.NewValue.ToString(fmt); set(e.NewValue); };
        var l = new TextBlock { Text = label, Width = 66, Foreground = Ui.Text, VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(l, Dock.Left); DockPanel.SetDock(v, Dock.Right);
        g.Children.Add(l); g.Children.Add(v); g.Children.Add(s);
        return g;
    }

    void ShowLightPopup(UIElement anchor)
    {
        var cfg = Settings.Current;
        var sp = new StackPanel();
        sp.Children.Add(new TextBlock { Text = "燈光", FontSize = 14, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6) });
        sp.Children.Add(SliderRow("整體亮度", 0.2, 2.0, cfg.LightIntensity, "0.00", x => { cfg.LightIntensity = x; ApplyLights(); }));
        sp.Children.Add(SliderRow("環境光", 0, 1, cfg.LightAmbient, "0.00", x => { cfg.LightAmbient = x; ApplyLights(); }));
        sp.Children.Add(SliderRow("光源方位", 0, 360, cfg.LightAzimuth, "0°", x => { cfg.LightAzimuth = x; ApplyLights(); }));
        sp.Children.Add(SliderRow("光源仰角", 5, 90, cfg.LightElevation, "0°", x => { cfg.LightElevation = x; ApplyLights(); }));
        var head = new CheckBox { Content = "頭燈（跟著視角照亮看到的那一面）", IsChecked = _vp.IsHeadLightEnabled, Margin = new Thickness(0, 6, 0, 0) };
        head.Click += (_, _) => _vp.IsHeadLightEnabled = head.IsChecked == true;
        sp.Children.Add(head);
        Popup pop = null;
        var reset = Ui.Btn("恢復預設燈光", () => { cfg.LightIntensity = 1; cfg.LightAmbient = 0.35; cfg.LightAzimuth = 225; cfg.LightElevation = 55; ApplyLights(); pop.IsOpen = false; });
        reset.Margin = new Thickness(0, 10, 0, 0); reset.HorizontalAlignment = HorizontalAlignment.Left;
        sp.Children.Add(reset);
        pop = Pop(anchor, sp);
    }

    void ShowDisplayPopup(UIElement anchor)
    {
        var sp = new StackPanel();
        sp.Children.Add(new TextBlock { Text = "3D 顯示", FontSize = 14, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6) });
        UIElement Swatches(string label, (string name, Color c)[] items, Color cur, Action<Color> set)
        {
            var w = new WrapPanel { Margin = new Thickness(0, 2, 0, 6) };
            foreach (var (n, c) in items)
            {
                var b = new Button { Width = 26, Height = 26, Margin = new Thickness(0, 0, 6, 0), Padding = new Thickness(0), ToolTip = n, Background = new SolidColorBrush(c), BorderBrush = c == cur ? Ui.Accent : Ui.Line, BorderThickness = new Thickness(c == cur ? 3 : 1) };
                b.Click += (_, _) => { set(c); foreach (var x in w.Children.OfType<Button>()) { x.BorderBrush = Ui.Line; x.BorderThickness = new Thickness(1); } b.BorderBrush = Ui.Accent; b.BorderThickness = new Thickness(3); };
                w.Children.Add(b);
            }
            var g = new StackPanel(); g.Children.Add(new TextBlock { Text = label, Foreground = Ui.Dim, FontSize = 12 }); g.Children.Add(w); return g;
        }
        sp.Children.Add(Swatches("防焊顏色", Pcb3DOptions.Masks, Pcb3DOptions.MaskColor, c => { Pcb3DOptions.MaskColor = c; RefreshTextures(); }));
        sp.Children.Add(Swatches("表面處理", Pcb3DOptions.Finishes, Pcb3DOptions.FinishColor, c => { Pcb3DOptions.FinishColor = c; RefreshTextures(); }));
        sp.Children.Add(Swatches("背景", Pcb3DOptions.Backgrounds, Pcb3DOptions.Background, c => { Pcb3DOptions.Background = c; _vp.Background = new SolidColorBrush(c); }));
        sp.Children.Add(SliderRow("元件透明", 0.1, 1, Pcb3DOptions.BodyOpacity, "0.00", x => { Pcb3DOptions.BodyOpacity = x; UpdateMaterials(); }));
        CheckBox C(string t, bool v, Action<bool> set) { var c = new CheckBox { Content = t, IsChecked = v, Margin = new Thickness(0, 3, 0, 3) }; c.Click += (_, _) => set(c.IsChecked == true); sp.Children.Add(c); return c; }
        C("顯示元件 3D 本體", Pcb3DOptions.ShowBodies, b => { Pcb3DOptions.ShowBodies = b; BuildParts(); });
        C("使用元件的 STEP 3D 模型（關閉：以外框擠出，較省記憶體）", Pcb3DOptions.UseStepModels, b => { Pcb3DOptions.UseStepModels = b; if (!b) StepCache.Remove(_owner.Scene.Board); BuildParts(); });
        C("無本體的元件以方塊顯示", Pcb3DOptions.BoxForBodyless, b => { Pcb3DOptions.BoxForBodyless = b; BuildParts(); });
        C("顯示絲印", Pcb3DOptions.ShowSilk, b => { Pcb3DOptions.ShowSilk = b; RefreshTextures(); });
        var b = _owner.Scene.Board;
        sp.Children.Add(new TextBlock { Text = $"板厚 {Units.Fmt(b.Thickness)} {Units.Name} · 3D 本體 {b.Bodies.Count}（STEP 模型 {_nStep}" + (_nStepOutline > 0 ? $"，STEP 以外框擠出 {_nStepOutline}" : "") + $"）· 方塊替代 {_nBox}", Foreground = Ui.Dim, FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) });
        Pop(anchor, sp);
    }
}
