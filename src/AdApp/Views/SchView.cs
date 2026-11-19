// Herald PCB Reader — Copyright (C) 2026 Johnny.ch.Tsai
// SPDX-License-Identifier: GPL-3.0-only
// Free software under the GNU General Public License version 3 (LICENSE.txt), WITHOUT ANY WARRANTY; with an
// additional permission to combine it with AvalonDock (Ms-PL), see NOTICE.txt.
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AdCore.Project;
using AdCore.Render;
using AdCore.Sch;
using SkiaSharp;
using SkiaSharp.Views.Desktop;
using SkiaSharp.Views.WPF;

namespace AdApp.Views;

public sealed class SchSel
{
    public bool IsNet; public string Ref; public string Net; public bool Mask; public ProjectNet PNet;
    /// <summary>Several parts (area selection).</summary>
    public List<string> Refs;
    public static SchSel Comp(string r) => new() { Ref = r };
    public static SchSel NetSel(string n, bool mask) => new() { IsNet = true, Net = n, Mask = mask };
}

/// <summary>A schematic page (one sheet of one file) a selected net runs over.</summary>
public sealed record NetPage(DocItem Doc, int Sheet, string Label)
{
    /// <summary>Every page of the net in file order: the project net's pages across files, or the sheets of this file.</summary>
    public static List<NetPage> Of(DocItem doc, SchSel sel)
    {
        var list = new List<NetPage>();
        if (sel is not { IsNet: true } || doc?.Sch == null) return list;
        if (sel.PNet is { } p && doc.Group != null)
        {
            foreach (var l in p.Locals.OrderBy(l => l.DocIndex).ThenBy(l => l.Sheet))
            {
                var d = doc.Group.CompiledDoc(l.DocIndex);
                if (d?.Sch != null && l.Sheet < d.Sch.Sheets.Count && !list.Any(x => x.Doc == d && x.Sheet == l.Sheet)) list.Add(new(d, l.Sheet, LabelOf(d, l.Sheet)));
            }
        }
        else if (doc.Sch.NetMap.TryGetValue(sel.Net ?? "", out var n))
            foreach (var s in n.Sheets.Distinct().Where(s => s < doc.Sch.Sheets.Count).OrderBy(s => s)) list.Add(new(doc, s, LabelOf(doc, s)));
        return list;
    }

    static string LabelOf(DocItem d, int s) => System.IO.Path.GetFileNameWithoutExtension(d.Name) + (d.Sch.Sheets.Count > 1 ? " › " + d.Sch.Sheets[s].Name : "");

    /// <summary>Page buttons: the page shown now inverted (filled blue, dark bold text), the others outlined in blue.</summary>
    public static Button Button(string text, bool current, double fontSize = double.NaN)
    {
        var b = new Button { Content = text, Padding = new Thickness(6, 1, 6, 1), Margin = new Thickness(0, 1, 4, 1), BorderThickness = new Thickness(1.5) };
        if (!double.IsNaN(fontSize)) b.FontSize = fontSize;
        if (current) { b.Background = AdApp.Panels.Ui.Jump; b.Foreground = CurrentText; b.BorderBrush = CurrentEdge; b.FontWeight = FontWeights.Bold; }
        else { b.Background = OtherFill; b.BorderBrush = AdApp.Panels.Ui.Jump; }
        return b;
    }
    public static readonly Brush CurrentText = Frozen(0x0B, 0x16, 0x22), CurrentEdge = Frozen(0xD6, 0xEA, 0xFF), OtherFill = Frozen(0x24, 0x3B, 0x57);
    static Brush Frozen(byte r, byte g, byte b) { var x = new SolidColorBrush(Color.FromRgb(r, g, b)); x.Freeze(); return x; }
}

/// <summary>Schematic document view (one SchDoc, any number of sheets).</summary>
public sealed class SchView : Grid
{
    public readonly DocItem Doc;
    readonly SKElement _canvas = new();
    readonly Border _pill = new();
    readonly WrapPanel _pillContent = new();
    readonly StackPanel _sheetTabs = new() { Orientation = Orientation.Horizontal };
    public int SheetIndex { get; private set; }
    double _s = 3, _ox, _oy;          // world(mm) → DIP: x*s+ox
    public SchSel Sel { get; private set; }
    HashSet<string> _hlNets;          // local names on the current sheet (project net locals)
    bool _mask;
    public static SchTheme Theme = SchTheme.Altium;
    public static double FontScale = 1.0;
    public static bool ShowGrid;
    /// <summary>Draw component values and parameters (Settings); designators are always drawn.</summary>
    public static bool ShowParams = true;

    public event Action<SchView, SchSel> SelectionChanged;   // user action
    public event Action<SchView, double, double> PointerMoved;
    /// <summary>A page of the selected net in another file was picked (page list).</summary>
    public event Action<DocItem, int> GoToPage;
    /// <summary>Another sheet of this file is shown.</summary>
    public event Action<SchView> SheetShown;
    /// <summary>A part was double-clicked (the window brings up Properties).</summary>
    public event Action<SchView> PartDoubleClicked;

    /// <summary>Document tab colours (bound by the tab header template).</summary>
    public Brush TabBrush => AdApp.Panels.Ui.SchTab;
    public static readonly DependencyProperty TabBorderProperty = DependencyProperty.Register(nameof(TabBorder), typeof(Brush), typeof(SchView), new PropertyMetadata(AdApp.Panels.Ui.SchTab));
    /// <summary>Blue while this sheet is a place the highlighted net continues to, else the tab's own colour.</summary>
    public Brush TabBorder { get => (Brush)GetValue(TabBorderProperty); set => SetValue(TabBorderProperty, value); }

    SchSheet Sheet => Doc.Sch?.Sheets.Count > SheetIndex ? Doc.Sch.Sheets[SheetIndex] : null;

    public SchView(DocItem doc)
    {
        Doc = doc;
        Background = new SolidColorBrush(Color.FromRgb(0xD6, 0xD6, 0xD6));
        RowDefinitions.Add(new RowDefinition());
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _canvas.PaintSurface += OnPaint;
        Children.Add(_canvas);
        Focusable = true; FocusVisualStyle = null;

        _pill.Background = new SolidColorBrush(Color.FromArgb(0xEE, 0x2A, 0x2A, 0x2D));
        _pill.BorderBrush = new SolidColorBrush(Color.FromRgb(0xD3, 0x18, 0x6D)); _pill.BorderThickness = new Thickness(1);
        _pill.CornerRadius = new CornerRadius(3); _pill.Padding = new Thickness(8, 4, 4, 4); _pill.Margin = new Thickness(10);
        _pill.HorizontalAlignment = HorizontalAlignment.Left; _pill.VerticalAlignment = VerticalAlignment.Top;
        _pill.Child = _pillContent; _pill.Visibility = Visibility.Collapsed;
        Children.Add(_pill);

        var tabsHost = new Border { Background = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x37)), Child = _sheetTabs };
        SetRow(tabsHost, 1);
        Children.Add(tabsHost);
        BuildSheetTabs();

        MouseWheel += OnWheel;
        MouseDown += OnDown; MouseMove += OnMove; MouseUp += OnUp;
        KeyDown += OnKey;
        SizeChanged += (_, e) =>
        {
            if (ActualWidth < 20) return;
            if (_pendingZoom is { } z) { _pendingZoom = null; ZoomTo(z); }
            else if (!_userView) Fit();
        };
    }
    bool _userView;   // true once the user zoomed / panned: stop auto-fitting on resize

    void BuildSheetTabs()
    {
        _sheetTabs.Children.Clear();
        var n = Doc.Sch?.Sheets.Count ?? 0;
        ((Border)_sheetTabs.Parent)?.SetValue(VisibilityProperty, n > 1 ? Visibility.Visible : Visibility.Collapsed);
        for (int i = 0; i < n; i++)
        {
            int k = i;
            var b = new System.Windows.Controls.Primitives.ToggleButton { Content = Doc.Sch.Sheets[i].Name, IsChecked = i == SheetIndex, Margin = new Thickness(2), FontSize = 11.5 };
            b.Click += (_, _) => ShowSheet(k);
            _sheetTabs.Children.Add(b);
        }
    }

    public void ShowSheet(int i, bool fit = true)
    {
        if (Doc.Sch == null || i < 0 || i >= Doc.Sch.Sheets.Count) return;
        SheetIndex = i;
        foreach (var (b, k) in _sheetTabs.Children.OfType<System.Windows.Controls.Primitives.ToggleButton>().Select((b, k) => (b, k))) b.IsChecked = k == i;
        if (fit) Fit();
        RefreshHighlight();
        SheetShown?.Invoke(this);
    }

    /// <summary>One page of the selected net (page list): that sheet, with the net highlighted on it.</summary>
    public void ShowNetPage(int sheet, ProjectNet pnet)
    {
        if (sheet != SheetIndex) ShowSheet(sheet);
        if (pnet != null) { ApplyGroupNet(pnet, true, false); SheetShown?.Invoke(this); }
    }

    // ---------- view ----------
    double[] PageRect()
    {
        var sh = Sheet; if (sh == null) return new double[] { 0, 0, 297, 210 };
        return sh.Page != null ? new[] { 0, 0, sh.Page.W, sh.Page.H } : sh.BBox;
    }

    public void Fit()
    {
        var b = PageRect(); double w = Math.Max(10, ActualWidth), h = Math.Max(10, ActualHeight - (_sheetTabs.Children.Count > 1 ? 28 : 0));
        _s = Math.Min((w - 24) / (b[2] - b[0]), (h - 24) / (b[3] - b[1]));
        _ox = (w - (b[2] - b[0]) * _s) / 2 - b[0] * _s; _oy = (h - (b[3] - b[1]) * _s) / 2 - b[1] * _s;
        _canvas.InvalidateVisual();
    }

    double[] _pendingZoom;   // zoom asked for while the view was not laid out (a background tab)

    public void ZoomTo(double[] bb, double minScaleFactor = 0)
    {
        if (ActualWidth < 20 || ActualHeight < 20) { _pendingZoom = bb; _userView = true; return; }
        double w = Math.Max(10, ActualWidth), h = Math.Max(10, ActualHeight);
        double bw = Math.Max(bb[2] - bb[0], 8), bh = Math.Max(bb[3] - bb[1], 8);
        var full = PageRect();
        double fitS = Math.Min((w - 24) / (full[2] - full[0]), (h - 24) / (full[3] - full[1]));
        _s = Math.Min(Math.Min(w / (bw * 2.2), h / (bh * 2.2)), fitS * 6);
        _s = Math.Max(_s, fitS);
        double cx = (bb[0] + bb[2]) / 2, cy = (bb[1] + bb[3]) / 2;
        _ox = w / 2 - cx * _s; _oy = h / 2 - cy * _s; _userView = true;
        _canvas.InvalidateVisual();
    }

    public (double x, double y) ToWorld(Point p) => ((p.X - _ox) / _s, (p.Y - _oy) / _s);
    public Point ToScreen(double x, double y) => new(x * _s + _ox, y * _s + _oy);

    void OnPaint(object s, SKPaintSurfaceEventArgs e)
    {
        var c = e.Surface.Canvas;
        c.Clear(Theme.Workspace);
        var sh = Sheet; if (sh == null) return;
        float dpi = (float)(e.Info.Width / Math.Max(1, _canvas.ActualWidth));
        c.Scale(dpi);
        c.Save();
        c.Translate((float)_ox, (float)_oy);
        c.Scale((float)_s);
        var hlRef = Sel != null && !Sel.IsNet && Sel.Refs == null ? Sel.Ref : null;
        SchRenderer.DrawSheet(c, sh, new SchRenderOptions
        {
            Theme = Theme, FontScale = FontScale, PixelMm = 1 / (_s * dpi), HlNets = _hlNets, HlRef = hlRef, HlRefs = Sel?.Refs is { } rs ? new HashSet<string>(rs) : null,
            Mask = _mask, ShowGrid = ShowGrid, FileName = Doc.Name, ShowParams = ShowParams,
        });
        c.Restore();
        DrawMarquee(c);
    }

    // ---------- area selection (AD: left→right = inside, right→left = touching) ----------
    Point? _mq;
    void DrawMarquee(SKCanvas c)
    {
        if (_mq is not { } q) return;
        bool touch = q.X < _down.X;
        var r = new SKRect((float)Math.Min(_down.X, q.X), (float)Math.Min(_down.Y, q.Y), (float)Math.Max(_down.X, q.X), (float)Math.Max(_down.Y, q.Y));
        using var f = new SKPaint { Color = touch ? new SKColor(40, 180, 90, 34) : new SKColor(40, 110, 230, 34) };
        using var st = new SKPaint { Color = touch ? new SKColor(0x1E, 0x9E, 0x52) : new SKColor(0x2A, 0x6C, 0xE0), Style = SKPaintStyle.Stroke, StrokeWidth = 1.2f, PathEffect = touch ? SKPathEffect.CreateDash(new[] { 6f, 4f }, 0) : null };
        c.DrawRect(r, f); c.DrawRect(r, st);
    }

    void AreaSelect(Point a, Point b)
    {
        var sh = Sheet; if (sh == null) return;
        var (x0, y0) = ToWorld(new Point(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y)));
        var (x1, y1) = ToWorld(new Point(Math.Max(a.X, b.X), Math.Max(a.Y, b.Y)));
        bool touching = b.X < a.X;
        var refs = new List<string>();
        foreach (var c in sh.Comps)
        {
            if (c.Power || c.Virtual || c.Ref == "" || refs.Contains(c.Ref)) continue;
            var bb = c.BBox;
            bool ok = touching ? bb[2] >= x0 && bb[0] <= x1 && bb[3] >= y0 && bb[1] <= y1 : bb[0] >= x0 && bb[2] <= x1 && bb[1] >= y0 && bb[3] <= y1;
            if (ok) refs.Add(c.Ref);
        }
        Select(refs.Count == 0 ? null : refs.Count == 1 ? SchSel.Comp(refs[0]) : new SchSel { Refs = refs }, false, true);
    }

    public void Invalidate() => _canvas.InvalidateVisual();
    /// <summary>Automation (frame-rate test): move the view by a few pixels.</summary>
    public void PanBy(double dx, double dy) { _ox += dx; _oy += dy; _userView = true; Invalidate(); }

    // ---------- selection ----------
    /// <summary>Set selection (from user, cross-probe or panels). focus=zoom to it; navigate=false keeps the sheet
    /// (cross-probe with "jump" off).</summary>
    public void Select(SchSel sel, bool focus, bool raise, bool navigate = true)
    {
        if (sel != null && Doc.Sch != null)
        {
            // jump to the sheet that holds it
            List<int> sheets = null;
            if (sel.Refs != null) sheets = null;
            else if (sel.IsNet) sheets = Doc.Sch.NetMap.TryGetValue(sel.Net ?? "", out var n) ? n.Sheets : null;
            else sheets = Doc.Sch.PartMap.TryGetValue(sel.Ref ?? "", out var p) ? p.Sheets : null;
            if (navigate && sheets is { Count: > 0 } && !sheets.Contains(SheetIndex)) ShowSheet(sheets[0], !focus);
            if (sel.IsNet && sel.PNet == null) sel.PNet = Doc.Group?.PNetFor(Doc, SheetIndex, sel.Net);
        }
        Sel = sel;
        RefreshHighlight();
        if (focus && sel != null && SelBBox() is { } bb) ZoomTo(bb);
        if (raise) SelectionChanged?.Invoke(this, sel);
    }

    /// <summary>Project-net highlight pushed from another sheet of the same group (or cleared).</summary>
    public void ApplyGroupNet(ProjectNet pnet, bool mask, bool navigate = true)
    {
        if (pnet == null) { if (Sel?.IsNet == true) Sel = null; RefreshHighlight(); return; }
        var loc = pnet.Locals.FirstOrDefault(l => Doc.Group?.CompiledDoc(l.DocIndex) == Doc && l.Sheet == SheetIndex)
                  ?? pnet.Locals.FirstOrDefault(l => Doc.Group?.CompiledDoc(l.DocIndex) == Doc);
        if (loc == null) { Sel = null; _hlNets = null; _mask = false; UpdatePill(); Invalidate(); return; }
        if (loc.Sheet != SheetIndex && navigate) ShowSheet(loc.Sheet);
        Sel = new SchSel { IsNet = true, Net = loc.Name, Mask = mask, PNet = pnet };
        RefreshHighlight();
    }

    void RefreshHighlight()
    {
        _hlNets = null; _mask = false;
        if (Sel?.IsNet == true)
        {
            var P = Sel.PNet;
            _hlNets = P != null
                ? new HashSet<string>(P.Locals.Where(l => Doc.Group?.CompiledDoc(l.DocIndex) == Doc && l.Sheet == SheetIndex).Select(l => l.Name))
                : new HashSet<string> { Sel.Net };
            _mask = Sel.Mask;
        }
        else if (Sel != null) _mask = Sel.Mask;
        UpdatePill();
        Invalidate();
    }

    void UpdatePill()
    {
        _pillContent.Children.Clear();
        if (Sel == null || !Sel.Mask || Sel.Refs != null) { _pill.Visibility = Visibility.Collapsed; return; }
        TextBlock T(string t, Brush b, bool bold = false) => new() { Text = t, Foreground = b, FontWeight = bold ? FontWeights.Bold : FontWeights.Normal, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        var dim = new SolidColorBrush(Color.FromRgb(0xB0, 0xB0, 0xB8)); var hl = new SolidColorBrush(Color.FromRgb(0xFF, 0x5C, 0xA8));
        if (Sel.IsNet)
        {
            var P = Sel.PNet;
            bool proj = P != null && P.FileCount > 1;
            var pages = NetPage.Of(Doc, Sel);
            _pillContent.Children.Add(T(proj ? (Doc.Group?.Kind == GroupKind.Project ? "專案網路" : "群組網路") : "只看網路", dim));
            _pillContent.Children.Add(T(TextOrPlain(P?.Name ?? Sel.Net), hl, true));
            int k = 0; var sh = Sheet;
            if (sh != null && _hlNets != null) foreach (var c in sh.Comps) if (!c.Power && !c.Virtual) k += c.Pins.Count(q => q.Net != null && _hlNets.Contains(q.Net));
            _pillContent.Children.Add(T(pages.Count > 1 ? $"本頁 {k} 腳 · 共 {(proj ? P.Members.Count : Doc.Sch.NetMap.TryGetValue(Sel.Net ?? "", out var dn) ? dn.Members.Count : k)} 腳／{pages.Count} 頁" : $"{k} 個接腳", dim));
            // every page the net is on, the one shown now inverted
            if (pages.Count > 1)
                foreach (var pg in pages)
                {
                    bool here = pg.Doc == Doc && pg.Sheet == SheetIndex;
                    var b = NetPage.Button(pg.Label, here);
                    b.ToolTip = here ? "目前頁面：點一下縮放到這個網路" : "切到 " + pg.Label;
                    b.Click += (_, _) =>
                    {
                        if (pg.Doc != Doc) GoToPage?.Invoke(pg.Doc, pg.Sheet);
                        else if (pg.Sheet != SheetIndex) ShowNetPage(pg.Sheet, Sel?.PNet);
                        else if (SelBBox() is { } bb) ZoomTo(bb);
                    };
                    _pillContent.Children.Add(b);
                }
        }
        else { _pillContent.Children.Add(T("只看零件", dim)); _pillContent.Children.Add(T(Sel.Ref, hl, true)); }
        var x = new Button { Content = "取消 Esc", Padding = new Thickness(6, 1, 6, 1), Background = new SolidColorBrush(Color.FromRgb(0x3F, 0x3F, 0x46)) };
        x.Click += (_, _) => { Select(null, false, true); Focus(); };
        _pillContent.Children.Add(x);
        _pill.Visibility = Visibility.Visible;
    }
    static string TextOrPlain(string s) => AdCore.Util.TextUtil.PlainText(s ?? "");

    public void ToggleMask()
    {
        if (Sel == null) return;
        Sel = new SchSel { IsNet = Sel.IsNet, Net = Sel.Net, Ref = Sel.Ref, PNet = Sel.PNet, Mask = !Sel.Mask };
        RefreshHighlight();
        SelectionChanged?.Invoke(this, Sel);
    }

    public double[] SelBBox()
    {
        var sh = Sheet; if (sh == null || Sel == null) return null;
        double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
        void Add(double[] b) { x0 = Math.Min(x0, b[0]); y0 = Math.Min(y0, b[1]); x1 = Math.Max(x1, b[2]); y1 = Math.Max(y1, b[3]); }
        if (Sel.Refs != null) foreach (var c in sh.Comps.Where(c => Sel.Refs.Contains(c.Ref))) Add(c.BBox);
        else if (!Sel.IsNet) foreach (var c in sh.Comps.Where(c => c.Ref == Sel.Ref)) Add(c.BBox);
        else if (_hlNets != null)
        {
            foreach (var w in sh.Wires.Where(w => w.Net != null && _hlNets.Contains(w.Net))) Add(new[] { Math.Min(w.X1, w.X2), Math.Min(w.Y1, w.Y2), Math.Max(w.X1, w.X2), Math.Max(w.Y1, w.Y2) });
            foreach (var c in sh.Comps) foreach (var p in c.Pins.Where(p => p.Net != null && _hlNets.Contains(p.Net))) Add(new[] { p.X, p.Y, p.X, p.Y });
        }
        return x0 == double.MaxValue ? null : new[] { x0, y0, x1, y1 };
    }

    // ---------- hit testing ----------
    static double SegDist(double x1, double y1, double x2, double y2, double x, double y)
    {
        double dx = x2 - x1, dy = y2 - y1, l2 = dx * dx + dy * dy, t = l2 > 0 ? Math.Clamp(((x - x1) * dx + (y - y1) * dy) / l2, 0, 1) : 0;
        return Math.Sqrt(Math.Pow(x1 + t * dx - x, 2) + Math.Pow(y1 + t * dy - y, 2));
    }

    static double BoxDist(double[] b, double x, double y)
    {
        double dx = Math.Max(0, Math.Max(b[0] - x, x - b[2])), dy = Math.Max(0, Math.Max(b[1] - y, y - b[3]));
        return Math.Sqrt(dx * dx + dy * dy);
    }

    // What is under the pointer wins over what is merely near it (2.9.5). Inside a net label's text, or a power port /
    // off-sheet symbol, that net is taken unless a line lies right under the pointer (OnPx); otherwise the nearest wire,
    // junction, label or symbol within NearPx. Before, any wire within 7 px came before the label under the pointer,
    // so a click on the upper half of a label's text in a column of labels 2.54 mm apart could pick the net above.
    const double NearPx = 7, OnPx = 1.5;

    /// <summary>The net a plain click takes at a point, with its score (distance; a box around the point counts as
    /// OnPx, one beside it as OnPx + its distance): wires, junctions, net labels, power ports and off-sheet symbols.</summary>
    (string Net, double D) NetNear(SchSheet sh, double wx, double wy)
    {
        double tol = NearPx / _s, on = OnPx / _s, bd = double.MaxValue; string best = null;
        void Line(double d, string n) { if (n != null && d < tol && d < bd) { bd = d; best = n; } }
        void Box(double[] b, string n) { double d = BoxDist(b, wx, wy); if (n != null && d < tol && on + d < bd) { bd = on + d; best = n; } }
        foreach (var w in sh.Wires) if (w.Kind == WireKind.Wire) Line(SegDist(w.X1, w.Y1, w.X2, w.Y2, wx, wy), w.Net);
        foreach (var j in sh.Junctions) Line(Math.Sqrt((j.X - wx) * (j.X - wx) + (j.Y - wy) * (j.Y - wy)), j.Net);
        foreach (var l in sh.Labels) if (l.Net != null && !l.SkipDraw) Box(SchRenderer.LabelGeom(l, FontScale).BB, l.Net);
        foreach (var c in sh.Comps) if (c.Power || c.Virtual) Box(c.BBox, c.Pins.FirstOrDefault(q => q.Net != null)?.Net);
        return (best, bd);
    }

    /// <summary>The nearest visible component pin within NearPx (Alt takes its net; a plain click on a pin takes the part).</summary>
    string PinNear(SchSheet sh, double wx, double wy)
    {
        double bd = NearPx / _s; string best = null;
        foreach (var c in sh.Comps)
        {
            if (c.Power || c.Virtual) continue;
            foreach (var p in c.Pins) { if (p.Net == null || p.Hidden) continue; var d = SegDist(p.X, p.Y, p.Ex, p.Ey, wx, wy); if (d < bd) { bd = d; best = p.Net; } }
        }
        return best;
    }

    public SchSel HitTest(double wx, double wy)
    {
        var sh = Sheet; if (sh == null) return null;
        if (NetNear(sh, wx, wy).Net is { } n) return SchSel.NetSel(n, false);
        double tol = NearPx / _s; SchComponent bc = null; double ba = double.MaxValue;
        foreach (var c in sh.Comps)
        {
            if (c.Power || c.Virtual || (c.Pins.Count == 0 && !c.Missing)) continue;
            var b = c.BBox;
            if (wx >= b[0] - tol && wx <= b[2] + tol && wy >= b[1] - tol && wy <= b[3] + tol) { var a = (b[2] - b[0]) * (b[3] - b[1]); if (a < ba) { ba = a; bc = c; } }
        }
        return bc == null ? null : SchSel.Comp(bc.Ref);
    }

    /// <summary>Alt / Ctrl click: always the net a plain click takes at that point; only where a plain click finds no
    /// net (it would take the part) the nearest pin's net; else the part, as a plain click. 2.9.5: pins and junctions
    /// were weighed together with the wires, so in a tight column of pins and wires (a connector) a pin or junction of
    /// the row above or below could beat the wire a plain click took.</summary>
    public SchSel HitNet(double wx, double wy)
    {
        var sh = Sheet; if (sh == null) return null;
        if ((NetNear(sh, wx, wy).Net ?? PinNear(sh, wx, wy)) is { } n) return SchSel.NetSel(n, true);
        var h = HitTest(wx, wy); return h is { IsNet: false } ? h : null;
    }

    /// <summary>Tests: at scale s (px per unit), clicks (plain and Alt) inside every net label's text and on every
    /// wire and counts those that pick another net than the one under the pointer; and around wires, labels, pin ends
    /// and power ports counts the points where a plain click takes a net and Alt another (must be none).</summary>
    public object HitScan(double s)
    {
        var sh = Sheet; if (sh == null) return null;
        double keep = _s; _s = s;
        try
        {
            int lp = 0, lPlain = 0, lAlt = 0, wp = 0, wPlain = 0, wAlt = 0, near = 0, apart = 0; var ex = new List<string>();
            void Probe(double x, double y, string net, string what, ref int n, ref int bp, ref int ba)
            {
                n++;
                var a = HitTest(x, y); var b = HitNet(x, y);
                if (a?.Net != net) bp++;
                if (b?.Net != net) { ba++; if (ex.Count < 12) ex.Add($"{what} {net} @{x:0.##},{y:0.##}: plain {a?.Net ?? a?.Ref ?? "-"}, alt {b?.Net ?? b?.Ref ?? "-"}"); }
            }
            void Near(double x, double y)
            {
                near++;
                var a = HitTest(x, y);
                if (a is { IsNet: true } && HitNet(x, y)?.Net is var bn && bn != a.Net) { apart++; if (ex.Count < 12) ex.Add($"apart @{x:0.##},{y:0.##}: plain {a.Net}, alt {bn}"); }
            }
            foreach (var l in sh.Labels)
            {
                if (l.Net == null || l.SkipDraw) continue;
                var bb = SchRenderer.LabelGeom(l, FontScale).BB;
                for (int i = 1; i <= 5; i++) for (int j = 1; j <= 3; j++)
                        Probe(bb[0] + (bb[2] - bb[0]) * i / 6, bb[1] + (bb[3] - bb[1]) * j / 4, l.Net, "label", ref lp, ref lPlain, ref lAlt);
            }
            foreach (var w in sh.Wires)
            {
                if (w.Kind != WireKind.Wire || w.Net == null) continue;
                foreach (var t in new[] { 0.25, 0.5, 0.75 }) Probe(w.X1 + (w.X2 - w.X1) * t, w.Y1 + (w.Y2 - w.Y1) * t, w.Net, "wire", ref wp, ref wPlain, ref wAlt);
            }
            double px = 1 / _s, m = 5 * px;
            foreach (var w in sh.Wires)
            {
                if (w.Kind != WireKind.Wire || w.Net == null) continue;
                double dx = w.X2 - w.X1, dy = w.Y2 - w.Y1, len = Math.Sqrt(dx * dx + dy * dy); if (len < 1e-9) continue;
                double nx = -dy / len, ny = dx / len;
                foreach (var t in new[] { 0.0, 0.5, 1.0 }) foreach (var o in new[] { -4, -2, 2, 4 }) Near(w.X1 + dx * t + nx * o * px, w.Y1 + dy * t + ny * o * px);
            }
            foreach (var l in sh.Labels)
            {
                if (l.Net == null || l.SkipDraw) continue;
                var bb = SchRenderer.LabelGeom(l, FontScale).BB;
                for (int i = 0; i <= 4; i++) for (int j = 0; j <= 4; j++) Near(bb[0] - m + (bb[2] - bb[0] + 2 * m) * i / 4, bb[1] - m + (bb[3] - bb[1] + 2 * m) * j / 4);
            }
            foreach (var c in sh.Comps)
            {
                if (c.Power || c.Virtual) { var b = c.BBox; for (int i = 0; i <= 2; i++) for (int j = 0; j <= 2; j++) Near(b[0] - m + (b[2] - b[0] + 2 * m) * i / 2, b[1] - m + (b[3] - b[1] + 2 * m) * j / 2); continue; }
                foreach (var p in c.Pins) for (int i = -1; i <= 1; i++) for (int j = -1; j <= 1; j++) Near(p.X + i * 4 * px, p.Y + j * 4 * px);
            }
            return new { scale = s, labelPts = lp, labelPlainOff = lPlain, labelAltOff = lAlt, wirePts = wp, wirePlainOff = wPlain, wireAltOff = wAlt, nearPts = near, altApart = apart, examples = ex };
        }
        finally { _s = keep; }
    }

    // ---------- input ----------
    Point _down; bool _panning, _moved, _dbl; double _dox, _doy; MouseButton _btn; bool _netMod;

    void OnWheel(object s, MouseWheelEventArgs e)
    {
        var p = e.GetPosition(this);
        if (Keyboard.Modifiers == ModifierKeys.Shift) { _ox += e.Delta * 0.5; Invalidate(); e.Handled = true; return; }
        double f = Math.Pow(1.0015, e.Delta);
        double ns = Math.Clamp(_s * f, 0.2, 400);
        _ox = p.X - (p.X - _ox) * ns / _s; _oy = p.Y - (p.Y - _oy) * ns / _s; _s = ns; _userView = true;
        Invalidate(); e.Handled = true;
    }

    void OnDown(object s, MouseButtonEventArgs e)
    {
        Focus();
        _down = e.GetPosition(this); _btn = e.ChangedButton; _moved = false; _dox = _ox; _doy = _oy;
        _panning = e.ChangedButton is MouseButton.Right or MouseButton.Middle;   // holding Space no longer pans (2.9.3)
        _netMod = (Keyboard.Modifiers & (ModifierKeys.Alt | ModifierKeys.Control)) != 0;
        CaptureMouse();
        if (e.ChangedButton == MouseButton.Left && e.ClickCount == 1) NoteDown();
        // the double-click zooms to the part: the button release that follows must not click again at the new zoom
        _dbl = e.ClickCount == 2 && e.ChangedButton == MouseButton.Left;
        if (_dbl) DoubleClickAt(_down);
        e.Handled = true;
    }

    /// <summary>How the selection now being raised was made: a plain click (its jump to the PCB tab waits out the
    /// double-click time), the double-click (Properties, no jump) or anything else (panels, keys, cross-probe).</summary>
    public enum SelVia { Other, Click, DoubleClick }
    public SelVia Via { get; private set; }
    /// <summary>When the left button last went down for a first click (Environment.TickCount64): Windows counts the
    /// double-click time from there.</summary>
    public long DownTick { get; private set; }
    public void NoteDown() => DownTick = Environment.TickCount64;

    /// <summary>Automation: the viewport (offset and scale), to tell whether a cross-probe moved the view.</summary>
    public string ViewKey => $"{_ox:0.#},{_oy:0.#},{_s:0.###}";

    /// <summary>A left double-click at a view point: select the part under it and bring up Properties.</summary>
    public void DoubleClickAt(Point p)
    {
        var (wx, wy) = ToWorld(p);
        var h = HitTest(wx, wy); if (h is not { IsNet: false }) return;
        Via = SelVia.DoubleClick;
        try { Select(h, true, true); } finally { Via = SelVia.Other; }
        PartDoubleClicked?.Invoke(this);
    }

    void OnMove(object s, MouseEventArgs e)
    {
        var p = e.GetPosition(this);
        var (wx, wy) = ToWorld(p);
        PointerMoved?.Invoke(this, wx, wy);
        if (!IsMouseCaptured) return;
        double dx = p.X - _down.X, dy = p.Y - _down.Y;
        if (!_moved && Math.Sqrt(dx * dx + dy * dy) > 4) _moved = true;
        if (_moved && _panning) { _ox = _dox + dx; _oy = _doy + dy; _userView = true; Cursor = Cursors.SizeAll; Invalidate(); }
        else if (_moved && _btn == MouseButton.Left && !_netMod) { _mq = p; Invalidate(); }
    }

    void OnUp(object s, MouseButtonEventArgs e)
    {
        if (!IsMouseCaptured) return;
        ReleaseMouseCapture(); Cursor = null;
        if (_mq is { } q) { _mq = null; AreaSelect(_down, q); Invalidate(); e.Handled = true; return; }
        if (_dbl) _dbl = false;
        else if (!_moved && e.ChangedButton == MouseButton.Left && !_panning)
            ClickAt(e.GetPosition(this), _netMod || (Keyboard.Modifiers & (ModifierKeys.Alt | ModifierKeys.Control)) != 0);
        else if (!_moved && e.ChangedButton == MouseButton.Right) ShowContextMenu();
        e.Handled = true;
    }

    /// <summary>A left click at a view point; netMode = Alt / Ctrl held (only-this-net).</summary>
    public void ClickAt(Point p, bool netMode)
    {
        var (wx, wy) = ToWorld(p);
        Via = SelVia.Click;
        try { Select(netMode ? HitNet(wx, wy) : HitTest(wx, wy), false, true); } finally { Via = SelVia.Other; }
    }

    /// <summary>For automation: area selection between two view points.</summary>
    public void DragSelect(Point a, Point b) => AreaSelect(a, b);

    void ShowContextMenu()
    {
        var m = new ContextMenu();
        MenuItem I(string h, Action a, bool en = true) { var i = new MenuItem { Header = h, IsEnabled = en }; i.Click += (_, _) => a(); m.Items.Add(i); return i; }
        I("適合視窗 (F)", Fit);
        I("縮放到所選", () => { if (SelBBox() is { } b) ZoomTo(b); }, Sel != null);
        I(Sel?.Mask == true ? "取消遮罩" : (Sel?.IsNet == true ? "只看此網路" : "只看此零件"), ToggleMask, Sel != null && Sel.Refs == null);
        I("清除選取 (Esc)", () => Select(null, false, true), Sel != null);
        m.Items.Add(new Separator());
        I(ShowGrid ? "隱藏格線 (G)" : "顯示格線 (G)", () => { ShowGrid = !ShowGrid; Invalidate(); });
        m.IsOpen = true;
    }

    void OnKey(object s, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        switch (key)
        {
            case Key.Escape: Select(null, false, true); break;
            case Key.F: case Key.End when Keyboard.Modifiers == ModifierKeys.Control: Fit(); break;
            case Key.PageUp: Zoom(1.25); break;
            case Key.PageDown when Keyboard.Modifiers == ModifierKeys.Control: Fit(); break;
            case Key.PageDown: Zoom(0.8); break;
            case Key.G: ShowGrid = !ShowGrid; Invalidate(); break;
            case Key.LeftAlt: case Key.RightAlt: e.Handled = true; return; // keep focus off the menu bar while Alt-clicking
            default: return;
        }
        e.Handled = true;
    }

    public void Zoom(double f)
    {
        double cx = ActualWidth / 2, cy = ActualHeight / 2, ns = Math.Clamp(_s * f, 0.2, 400);
        _ox = cx - (cx - _ox) * ns / _s; _oy = cy - (cy - _oy) * ns / _s; _s = ns; _userView = true; Invalidate();
    }

    /// <summary>For automation: DIP position (relative to this view) of a component body / a wire of a net.</summary>
    public Point? ScreenPointOfComp(string reference)
    {
        var c = Sheet?.Comps.FirstOrDefault(x => x.Ref == reference); if (c == null) return null;
        return ToScreen((c.BBox[0] + c.BBox[2]) / 2, (c.BBox[1] + c.BBox[3]) / 2);
    }
    public Point? ScreenPointOfNet(string net)
    {
        var w = Sheet?.Wires.Where(x => x.Net == net && x.Kind == WireKind.Wire).OrderByDescending(x => Math.Abs(x.X2 - x.X1) + Math.Abs(x.Y2 - x.Y1)).FirstOrDefault();
        return w == null ? null : ToScreen((w.X1 + w.X2) / 2, (w.Y1 + w.Y2) / 2);
    }
}
