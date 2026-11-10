// Herald PCB Reader — Copyright (C) 2026 Johnny.ch.Tsai
// SPDX-License-Identifier: GPL-3.0-only
// Free software under the GNU General Public License version 3 (LICENSE.txt), WITHOUT ANY WARRANTY; with an
// additional permission to combine it with AvalonDock (Ms-PL), see NOTICE.txt.
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using AdApp.Panels;
using AdCore.Pcb;
using AdCore.Project;
using AdCore.Render;
using SkiaSharp;
using SkiaSharp.Views.Desktop;
using SkiaSharp.Views.WPF;

namespace AdApp.Views;

/// <summary>PCB document view: 2D canvas (Skia) + 3D (Helix), AD-like Active Bar, info card and layer tabs.</summary>
public sealed partial class PcbView : Grid
{
    public readonly DocItem Doc;
    public readonly PcbScene Scene;
    public readonly PcbViewState St;
    readonly SKElement _canvas = new();
    readonly Grid _content = new();
    readonly StackPanel _tabs = new() { Orientation = Orientation.Horizontal };
    readonly StackPanel _bar = new() { Orientation = Orientation.Horizontal };
    readonly ComboBox _ls = new() { Width = 168, Margin = new Thickness(0, 2, 6, 2), FontSize = 12.5 };
    readonly PcbInfoCard _card;
    Pcb3DView _view3;
    /// <summary>Tests: the 3D view (null until 3D was first shown).</summary>
    public Pcb3DView View3D => _view3;
    public string Tool { get; private set; } = "select";
    public bool Is3D => St.Mode3D;

    public event Action<PcbView, PcbSelection> SelectionChanged;
    /// <summary>Highlight changed; the flag is true when the user asked to look the NET up in the schematic (Alt+click).</summary>
    public event Action<PcbView, bool> HighlightChanged;
    public event Action<PcbView, double, double> PointerMoved;
    public event Action UnitsChanged;

    static Brush B(byte r, byte g, byte b) => new SolidColorBrush(Color.FromRgb(r, g, b));

    /// <summary>Document tab colours (bound by the tab header template).</summary>
    public Brush TabBrush => Ui.PcbTab;
    public Brush TabBorder => Ui.PcbTab;   // the tab's own colour: no dark line inside the tab

    /// <summary>Scenes built ahead on a worker (a session being restored), taken by the view of that file.</summary>
    public static readonly System.Collections.Concurrent.ConcurrentDictionary<DocItem, PcbScene> Prepared = new();

    public PcbView(DocItem doc)
    {
        Doc = doc;
        Scene = Prepared.TryRemove(doc, out var ps) ? ps : new PcbScene(doc.Pcb);
        St = new PcbViewState(Scene);
        St.Filter.Clear(); foreach (var f in FilterFromSettings()) St.Filter.Add(f);
        _tiles = new PcbTiles(Scene.ContentBounds);
        _tiles.TileReady += OnTileReady;
        Background = B(0x2E, 0x34, 0x38);
        RowDefinitions.Add(new RowDefinition());
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _canvas.PaintSurface += OnPaint;
        _content.Children.Add(_canvas);
        Children.Add(_content);

        var barHost = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xF0, 0x33, 0x33, 0x37)), BorderBrush = B(0x4A, 0x4A, 0x50), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4), Padding = new Thickness(4), Margin = new Thickness(0, 6, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Child = _bar,
        };
        // shrinks (never grows) to fit a narrow pane, e.g. the right half of split view
        Children.Add(new Viewbox { Child = barHost, StretchDirection = StretchDirection.DownOnly, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(8, 0, 8, 0) });
        BuildBar();

        _card = new PcbInfoCard(this);
        Children.Add(_card);

        // bottom: LS (layer set) + Altium-style layer tabs
        var bottom = new DockPanel { Background = B(0x33, 0x33, 0x37) };
        var lsHost = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 0, 4, 0) };
        lsHost.Children.Add(new TextBlock { Text = "LS", FontFamily = new FontFamily("Consolas"), FontWeight = FontWeights.Bold, Foreground = B(0x9A, 0x9A, 0xA0), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0), ToolTip = "圖層組合 Layer Set" });
        _ls.VerticalAlignment = VerticalAlignment.Center;
        lsHost.Children.Add(_ls);
        foreach (var (k, n) in LayerSets) _ls.Items.Add(new ComboBoxItem { Content = n, Tag = k, IsEnabled = k != "custom" });
        _ls.SelectionChanged += (_, _) => { if (!_syncLs && _ls.SelectedItem is ComboBoxItem { Tag: string k } && k != "custom") { St.SetLayerSet(k); AfterLayers(); } };
        DockPanel.SetDock(lsHost, Dock.Left);
        bottom.Children.Add(lsHost);
        bottom.Children.Add(new ScrollViewer { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = _tabs });
        _tabsHost = new Border { BorderBrush = B(0x4A, 0x4A, 0x50), BorderThickness = new Thickness(0, 1, 0, 0), Child = bottom };
        SetRow(_tabsHost, 1);
        Children.Add(_tabsHost);
        BuildTabs();

        Focusable = true; FocusVisualStyle = null;
        _canvas.MouseWheel += OnWheel;
        _canvas.MouseDown += OnDown; _canvas.MouseMove += OnMove; _canvas.MouseUp += OnUp;
        KeyDown += OnKey;
        KeyUp += (_, e) =>
        {
            var k = e.Key == Key.System ? e.SystemKey : e.Key;
            if (k is Key.LeftAlt or Key.RightAlt) e.Handled = true;
            if (k is Key.LeftShift or Key.RightShift or Key.LeftCtrl or Key.RightCtrl) MeasModsChanged();
        };
        SizeChanged += (_, _) =>
        {
            if (_canvas.ActualWidth <= 10) return;
            if (_pendingBox is { } pb) { _pendingBox = null; St.Fit(W, H, pb); }
            else if (!_userView) St.Fit(W, H);
            Redraw();
        };
    }
    bool _userView;   // true once the user (or a cross-probe zoom) moved the view
    SKRect? _pendingBox;   // zoom asked for before the view was laid out (a tab in the background)
    readonly Border _tabsHost;

    public static readonly (string k, string n)[] LayerSets =
    {
        ("all", "全部 All Layers"), ("signal", "訊號層 Signal Layers"), ("plane", "內電層 Plane Layers"), ("nonsignal", "非訊號層 NonSignal Layers"),
        ("mech", "機構層 Mechanical Layers"), ("top", "頂面 Top Side"), ("bottom", "底面 Bottom Side"), ("custom", "自訂"),
    };

    float W => (float)Math.Max(1, _canvas.ActualWidth);
    float H => (float)Math.Max(1, _canvas.ActualHeight);

    // ---------- Active Bar ----------
    readonly Dictionary<string, ToggleButton> _barBtn = new();
    readonly Dictionary<string, (System.Windows.Shapes.Path Icon, TextBlock Text)> _barIcon = new();
    static class BarIcons
    {
        public const string Select = "M4,1.8 V13 L6.9,10.4 L9,14.6 L10.9,13.7 L8.8,9.6 L12.6,9.4 Z";
        public const string Measure = "M1.8,10.6 L10.6,1.8 L14.2,5.4 L5.4,14.2 Z M4.4,8 L5.9,9.5 M6.4,6 L8.6,8.2 M8.4,4 L9.9,5.5";
        public const string Filter = "M1.8,2.5 H14.2 L9.4,8.3 V13.6 L6.6,12.2 V8.3 Z";
        // a hand pointing up-left, the index finger out
        public const string Finger = "M5.2,1.6 L9.8,6.2 L11.9,5.8 L13.6,7.5 L14.4,9.4 L13.4,13.4 L10.6,14.6 L6.4,13.2 L3.4,9.6 L4.6,8.6 L6.4,9.6 L7,8.8 L3.2,5 Z";
        public const string Display = "M8,1.8 L14.3,5 L8,8.2 L1.7,5 Z M1.7,8.1 L8,11.3 L14.3,8.1 M1.7,11.2 L8,14.4 L14.3,11.2";
    }
    void BuildBar()
    {
        _bar.Children.Clear(); _barBtn.Clear(); _barIcon.Clear();
        ToggleButton T(string key, string text, string tip, Action click, string icon = null)
        {
            var b = new ToggleButton { Content = text, ToolTip = tip, Padding = new Thickness(10, 4, 10, 4), FontSize = 14, MinWidth = 34 };
            if (icon != null)
            {
                // blue line icon before the label (as on the main toolbar); turns light on the pressed button
                var ic = Ui.Icon(icon); ic.Margin = new Thickness(0, 0, 6, 0);
                var tx = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center };
                b.Content = Ui.Row(ic, tx); _barIcon[key] = (ic, tx);
            }
            b.Click += (_, _) => { click(); SyncBar(); if (key is not ("disp" or "filter")) Focus(); };
            _bar.Children.Add(b); _barBtn[key] = b; return b;
        }
        void Sep() => _bar.Children.Add(new Border { Width = 1, Margin = new Thickness(5, 3, 5, 3), Background = B(0x55, 0x55, 0x5A) });
        T("select", "選取", "選取工具 (Esc)", () => SetTool("select"), BarIcons.Select);
        T("measure", "量測", "量測距離 (M)：Shift 鎖定 0°/45°/90°，Ctrl 吸附銅箔邊緣", () => SetTool("measure"), BarIcons.Measure);
        T("finger", "指示", "指示標記 (P)：游標變成手指，左鍵在板上留下同方向的手指標記，空白鍵轉 45°，Esc 清除全部；縮成全圖也看得清楚", () => SetTool(Tool == "finger" ? "select" : "finger"), BarIcons.Finger);
        T("filter", "篩選", "選取篩選：決定點擊或框選時可以選到哪些物件", ShowFilterPopup, BarIcons.Filter);
        Sep();
        T("2d", "2D", "2D 檢視 (2)", () => Set3D(false));
        T("3d", "3D", "3D 檢視 (3)", () => Set3D(true));
        Sep();
        T("fit", "全圖", "整塊板子放進畫面 (F)", Fit);
        T("flip", "翻面", "翻到底面：左右鏡射並反轉圖層順序 (B)", () => SetBottomView(!St.BottomView));
        T("mx", "⇋", "左右鏡射 (X)", () => { St.Mx = -St.Mx; Redraw(); });
        T("my", "⇅", "上下鏡射 (Y)", () => { St.My = -St.My; Redraw(); });
        T("rot", "⟳", "向右旋轉 90° (R)", () => { St.Rot = (St.Rot + 90) % 360; Redraw(); });
        Sep();
        T("single", "單層", "只顯示目前層 (Shift+S)", () => { St.Single = !St.Single; AfterLayers(); });
        T("nets", "NET", "在焊墊、導通孔、導線上標示 NET 名稱 (N)", () => { St.ShowNetNames = !St.ShowNetNames; Invalidate(); });
        T("clear", "清除點亮", "清除點亮 (Shift+C)", ClearHighlight);
        Sep();
        T("disp", "顯示…", "圖層、顏色、焊墊編號、NET 名稱與其他顯示設定", ShowDisplayPopup, BarIcons.Display);
        SyncBar();
    }

    void SyncBar()
    {
        _barBtn["select"].IsChecked = Tool == "select"; _barBtn["measure"].IsChecked = Tool == "measure"; _barBtn["finger"].IsChecked = Tool == "finger";
        _barBtn["2d"].IsChecked = !St.Mode3D; _barBtn["3d"].IsChecked = St.Mode3D;
        _barBtn["flip"].IsChecked = St.BottomView; _barBtn["mx"].IsChecked = St.Mx < 0; _barBtn["my"].IsChecked = St.My < 0; _barBtn["rot"].IsChecked = St.Rot != 0;
        _barBtn["single"].IsChecked = St.Single; _barBtn["nets"].IsChecked = St.ShowNetNames;
        _barBtn["fit"].IsChecked = false; _barBtn["clear"].IsChecked = false; _barBtn["disp"].IsChecked = _dispPop?.IsOpen == true;
        int on = Filters.Count(f => St.Filter.Contains(f.k));
        _barBtn["filter"].IsChecked = _filterPop?.IsOpen == true || on < Filters.Length;
        _barIcon["filter"].Text.Text = on < Filters.Length ? $"篩選 {on}/{Filters.Length}" : "篩選";
        _barBtn["clear"].IsEnabled = St.HlNets.Count + St.HlComps.Count > 0;
        foreach (var k in new[] { "select", "measure", "finger", "filter", "flip", "mx", "my", "rot", "single", "nets", "disp" }) _barBtn[k].IsEnabled = !St.Mode3D || k is "flip";
        foreach (var (k, (ic, _)) in _barIcon)
        {
            var b = _barBtn[k];
            ic.Stroke = !b.IsEnabled ? Ui.Dim : b.IsChecked == true ? BarIconOn : Ui.TitleBlue;
        }
    }
    static readonly Brush BarIconOn = Ui.Br(0xD6, 0xEA, 0xFF);

    // ---------- selection filter (as in v1) ----------
    public static readonly (PickClass k, string zh, string en)[] Filters =
    {
        (PickClass.Comp, "元件", "Components"), (PickClass.Pad, "焊墊", "Pads"), (PickClass.Via, "導通孔", "Vias"), (PickClass.Track, "導線", "Tracks"), (PickClass.Arc, "圓弧", "Arcs"),
        (PickClass.Poly, "鋪銅", "Polygons"), (PickClass.Region, "區域", "Regions"), (PickClass.Fill, "填充", "Fills"), (PickClass.Text, "文字", "Texts"), (PickClass.Keepout, "禁布", "Keepouts"),
    };

    static IEnumerable<PickClass> FilterFromSettings()
    {
        var saved = Settings.Current.PickFilter;
        return saved == null ? Filters.Select(f => f.k) : Filters.Select(f => f.k).Where(k => saved.Contains(k.ToString()));
    }

    Popup _filterPop;
    void ShowFilterPopup()
    {
        var sp = new StackPanel { Width = 250 };
        sp.Children.Add(new TextBlock { Text = "點擊時可選取", FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Ui.Dim, Margin = new Thickness(0, 0, 0, 6) });
        var boxes = new List<CheckBox>();
        void Apply()
        {
            St.Filter.Clear(); foreach (var (b, f) in boxes.Zip(Filters)) if (b.IsChecked == true) St.Filter.Add(f.k);
            Settings.Current.PickFilter = St.Filter.Select(k => k.ToString()).ToList();
            // drop selected objects the filter no longer allows
            if (St.Sel != null)
            {
                var cur = St.Sel.Items ?? new List<PcbSelection> { St.Sel };
                var keep = cur.Where(x => x.IsComp ? St.Filter.Contains(PickClass.Comp) : St.Filter.Contains(Scene.Objs[x.Index].Class)).ToList();
                if (keep.Count != cur.Count) SetSelection(Many(keep), true);
            }
            SyncBar();
        }
        foreach (var f in Filters)
        {
            var row = new DockPanel();
            var en = new TextBlock { Text = f.en, Foreground = Ui.Dim, FontFamily = new FontFamily("Consolas"), FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(en, Dock.Right); row.Children.Add(en);
            row.Children.Add(new TextBlock { Text = f.zh, FontSize = 13.5, VerticalAlignment = VerticalAlignment.Center });
            var cb = new CheckBox { Content = row, IsChecked = St.Filter.Contains(f.k), Margin = new Thickness(0, 3, 0, 3), HorizontalContentAlignment = HorizontalAlignment.Stretch };
            cb.Click += (_, _) =>
            {
                if ((Keyboard.Modifiers & ModifierKeys.Alt) != 0) foreach (var b in boxes) b.IsChecked = b == cb;   // Alt+click: only this one
                Apply();
            };
            boxes.Add(cb); sp.Children.Add(cb);
        }
        var all = Ui.Btn("全選", () => { foreach (var b in boxes) b.IsChecked = true; Apply(); });
        var none = Ui.Btn("全不選", () => { foreach (var b in boxes) b.IsChecked = false; Apply(); });
        var btns = new UniformGrid { Rows = 1, Margin = new Thickness(0, 6, 0, 6) }; btns.Children.Add(all); btns.Children.Add(none); sp.Children.Add(btns);
        sp.Children.Add(new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Ui.Dim, FontSize = 11.5, Text = "Alt+點項目：只選這一類。「元件」開啟時，點元件外框、絲印或焊墊之間的空白處會選取整個元件；關閉後可單獨選到元件內的導線或文字。框選同樣依照這裡的設定。" });
        _filterPop = new Popup
        {
            PlacementTarget = _barBtn["filter"], Placement = PlacementMode.Bottom, StaysOpen = false, AllowsTransparency = true,
            Child = new Border { Background = Ui.Bg2, BorderBrush = Ui.Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(12), Child = sp },
        };
        _filterPop.Closed += (_, _) => { SyncBar(); Focus(); };
        _filterPop.IsOpen = true;
        SyncBar();
    }

    Popup _dispPop;
    void ShowDisplayPopup()
    {
        var panel = new LayersPanel { Width = 330, MaxHeight = Math.Max(300, ActualHeight - 80) };
        panel.UnitsChanged += () => { UnitsChanged?.Invoke(); RefreshCard(); };
        panel.Bind(this);
        _dispPop = new Popup
        {
            PlacementTarget = _barBtn["disp"], Placement = PlacementMode.Bottom, StaysOpen = false, AllowsTransparency = true,
            Child = new Border { Background = Ui.Bg2, BorderBrush = Ui.Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), Child = panel },
        };
        _dispPop.Closed += (_, _) => SyncBar();
        _dispPop.IsOpen = true;
    }

    public void SetTool(string t)
    {
        bool wasFinger = Tool == "finger";
        Tool = t; var m = St.Meas; m.A = m.B = m.Live = m.Snap = null; m.SnapInfo = m.AInfo = m.BInfo = null; m.Ortho = false;
        if (wasFinger || t == "finger") FingerToolChanged(t == "finger");
        SetMeasCursor(false); SyncBar(); Redraw(); RefreshCard();
    }

    // ---------- layer tabs (Altium style) ----------
    bool _syncLs;
    public void BuildTabs()
    {
        _tabs.Children.Clear();
        foreach (var id in St.LayerMap.Keys.OrderBy(St.TabRank))
        {
            var l = St.LayerMap[id];
            var sw = new Border { Width = 11, Height = 11, Background = new SolidColorBrush(Color.FromRgb(l.Color.Red, l.Color.Green, l.Color.Blue)), Margin = new Thickness(0, 0, 5, 0), BorderBrush = Brushes.Black, BorderThickness = new Thickness(1) };
            var tx = new TextBlock { Text = l.Name, FontSize = 12, Foreground = l.Visible ? B(0xE2, 0xE2, 0xE2) : B(0x77, 0x77, 0x7D), VerticalAlignment = VerticalAlignment.Center };
            var sp = new StackPanel { Orientation = Orientation.Horizontal }; sp.Children.Add(sw); sp.Children.Add(tx);
            bool cur = id == St.Cur;
            var tab = new Border
            {
                Child = sp, Padding = new Thickness(7, 4, 9, 4), Margin = new Thickness(0, 0, 1, 0), Cursor = Cursors.Hand, Tag = id,
                Background = cur ? B(0x50, 0x50, 0x58) : B(0x2B, 0x2B, 0x2E),
                BorderBrush = cur ? new SolidColorBrush(Color.FromRgb(l.Color.Red, l.Color.Green, l.Color.Blue)) : Brushes.Transparent, BorderThickness = new Thickness(0, 2, 0, 0),
                ToolTip = $"{l.Name}（{l.Count} 個物件）\n左鍵：設為目前層　右鍵：選單",
            };
            tab.MouseLeftButtonUp += (_, _) => SetCurrentLayer(id);
            tab.MouseRightButtonUp += (_, e) => { LayerMenu(id).IsOpen = true; e.Handled = true; };
            _tabs.Children.Add(tab);
        }
        _syncLs = true;
        _ls.SelectedIndex = Math.Max(0, Array.FindIndex(LayerSets, s => s.k == St.LayerSet));
        _syncLs = false;
    }

    ContextMenu LayerMenu(int id)
    {
        var l = St.LayerMap[id]; var m = new ContextMenu();
        MenuItem I(string h, Action a) { var i = new MenuItem { Header = h }; i.Click += (_, _) => { a(); AfterLayers(); }; m.Items.Add(i); return i; }
        I("設為目前層", () => SetCurrentLayer(id));
        I(l.Visible ? "隱藏此層" : "顯示此層", () => { l.Visible = !l.Visible; St.LayerSet = "custom"; });
        I("只顯示此層", () => { foreach (var x in St.LayerMap.Values) x.Visible = x.Id == id; St.Cur = id; St.ApplyOrder(); St.LayerSet = "custom"; });
        I("全部顯示", () => St.SetLayerSet("all"));
        return m;
    }

    public void AfterLayers() { BuildTabs(); Invalidate(); SyncBar(); }

    public void SetCurrentLayer(int id)
    {
        if (!St.LayerMap.TryGetValue(id, out var l)) return;
        if (!l.Visible) { l.Visible = true; St.LayerSet = "custom"; }
        St.Cur = id; St.ApplyOrder(); AfterLayers();
    }

    public void CycleLayer(int dir)
    {
        var ids = St.LayerMap.Keys.Where(id => St.LayerMap[id].Visible || St.Single).OrderBy(St.TabRank).ToList();
        if (ids.Count == 0) return;
        int i = ids.IndexOf(St.Cur); i = ((i < 0 ? 0 : i + dir) % ids.Count + ids.Count) % ids.Count;
        SetCurrentLayer(ids[i]);
    }

    public void SetBottomView(bool on)
    {
        St.BottomView = on; St.Mx = on ? -1 : 1;
        if (on && St.LayerMap.ContainsKey(Layers.Bottom) && St.Cur == Layers.Top) St.Cur = Layers.Bottom;
        else if (!on && St.LayerMap.ContainsKey(Layers.Top) && St.Cur == Layers.Bottom) St.Cur = Layers.Top;
        St.ApplyOrder(); AfterLayers();
        if (St.Mode3D) _view3?.FlipView(on);
    }

    // ---------- drawing ----------
    // The layers are rendered once into tiles (PcbTiles, background threads, all zoom levels of the whole board
    // up to ~4000 px right away); a frame only moves / scales finished tiles, then draws the board fill under
    // them and the highlight, selection, NET names and measure tool on top. While a deeper tile is still being
    // rendered its coarser parent is shown in its place.
    PcbTiles _tiles;
    string _sig;
    bool _redrawQueued;
    float _dpi = 1;

    /// <summary>Paint statistics (automation): paints, slowest paint, tiles drawn as a coarser stand-in.</summary>
    public double MaxPaintMs; public int SyncRenders, AsyncRenders, Paints;

    void OnTileReady()
    {
        if (_redrawQueued) return;
        _redrawQueued = true;
        Dispatcher.BeginInvoke(new Action(() => { _redrawQueued = false; AsyncRenders++; Invalidate(); }), System.Windows.Threading.DispatcherPriority.Render);
    }

    // While measuring, only the measure line and the snap marker move with the cursor; the board under them stays
    // the same. So with the measure tool the last frame is kept, and a redraw for the measure tool alone draws
    // that frame back instead of painting the board again (a lit NET with large pours, or pad numbers and NET
    // names, made every mouse move a full repaint: up to ~65 ms on a large board). Any other redraw (view,
    // layers, highlight, selection, a finished tile, a new size) paints in full and keeps the new frame.
    SKImage _frame;
    bool _frameStale = true;
    /// <summary>Paints done by drawing the kept frame back (automation).</summary>
    public int FramePaints; public double MaxFramePaintMs;

    /// <summary>What the tiles depend on: visible layers in drawing order with colour and alpha, picture version.</summary>
    string Signature()
    {
        var sb = new System.Text.StringBuilder().Append(Scene.PicVersion).Append('|');
        foreach (var k in St.Order) if (St.IsVisible(k)) { var l = St.LayerMap[k]; sb.Append(k).Append(':').Append((uint)l.Color).Append(':').Append(l.Alpha).Append(','); }
        return sb.ToString();
    }

    void OnPaint(object s, SKPaintSurfaceEventArgs e)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew(); Paints++; int kept = FramePaints;
        try { Paint(e); }
        finally { double ms = sw.Elapsed.TotalMilliseconds; MaxPaintMs = Math.Max(MaxPaintMs, ms); if (FramePaints != kept) MaxFramePaintMs = Math.Max(MaxFramePaintMs, ms); }
    }

    void Paint(SKPaintSurfaceEventArgs e)
    {
        var c = e.Surface.Canvas;
        _dpi = (float)(e.Info.Width / Math.Max(1, _canvas.ActualWidth));
        bool keep = Tool == "measure" && !St.Mode3D;
        if (!keep) { _frame?.Dispose(); _frame = null; }
        if (keep && !_frameStale && _frame is { } f && f.Width == e.Info.Width && f.Height == e.Info.Height && !Scene.Disposed)
        {
            FramePaints++;
            c.DrawImage(f, 0, 0);
            c.Save(); c.Scale(_dpi); PcbRenderer.DrawMeasure(c, St, W, H); c.Restore();
            return;
        }
        c.Clear(St.WorkColor);
        if (Scene.Disposed) return;
        var sig = Signature();
        if (sig != _sig) { _sig = sig; _tiles.SetStack(PcbRenderer.LayerStack(Scene, St)); }
        // world → device pixels (the DIP view matrix scaled by the screen DPI)
        var m = SKMatrix.CreateScale(_dpi, _dpi).PreConcat(St.Matrix(W, H));
        c.Save(); c.SetMatrix(m); PcbRenderer.DrawBoardFill(c, Scene, St); c.Restore();
        DrawTiles(c);
        c.Save(); c.SetMatrix(m); PcbRenderer.DrawOverlays(c, Scene, St); c.Restore();
        c.Save(); c.Scale(_dpi);
        if (St.ShowNetNames || St.ShowPadNumbers) PcbRenderer.DrawNetLabels(c, Scene, St, W, H);
        DrawMarquee(c);
        DrawFingers(c);
        // (not while dragging to pan: those frames are replaced right away; the next measure move paints in full)
        if (keep && !_canvas.IsMouseCaptured) { c.Flush(); _frame?.Dispose(); _frame = e.Surface.Snapshot(); _frameStale = false; }
        PcbRenderer.DrawMeasure(c, St, W, H);
        c.Restore();
        SyncFingerFx();
    }

    /// <summary>Tile pixel → device pixel, computed in double so deep zoom stays exact.</summary>
    SKMatrix TileMatrix(int z, int tx, int ty)
    {
        double s = _tiles.Scale(z), dpi = _dpi; var (a, b, cc, d) = St.A();
        double ux = _tiles.Content.Left + tx * PcbTiles.T / s - St.Cx, uy = _tiles.Content.Bottom - ty * PcbTiles.T / s - St.Cy;
        return new SKMatrix((float)(dpi * a / s), (float)(-dpi * b / s), (float)(dpi * (a * ux + b * uy + W / 2)),
                            (float)(dpi * cc / s), (float)(-dpi * d / s), (float)(dpi * (cc * ux + d * uy + H / 2)), 0, 0, 1);
    }

    void DrawTile(SKCanvas c, SKImage img, int z, int tx, int ty, SKRect src, SKPaint p)
    {
        c.Save(); c.SetMatrix(TileMatrix(z, tx, ty));
        c.DrawImage(img, new SKRect(src.Left + 1, src.Top + 1, src.Right + 1, src.Bottom + 1), src, p);   // tiles carry a 1 px border
        c.Restore();
    }

    void DrawTiles(SKCanvas c)
    {
        const int T = PcbTiles.T;
        double devS = St.S * _dpi;
        int z = Math.Clamp((int)Math.Ceiling(Math.Log2(devS / _tiles.S0) - 1e-6), 0, _tiles.MaxLevel);   // tiles at least as sharp as the screen
        var (nx, ny) = _tiles.Count(z); double s = _tiles.Scale(z); var C = _tiles.Content;
        double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
        foreach (var (px, py) in new[] { (0f, 0f), (W, 0f), (0f, H), (W, H) })
        {
            var (wx, wy) = St.ToWorld(px, py, W, H);
            x0 = Math.Min(x0, wx); x1 = Math.Max(x1, wx); y0 = Math.Min(y0, wy); y1 = Math.Max(y1, wy);
        }
        if (x1 < C.Left || x0 > C.Right || y1 < C.Top || y0 > C.Bottom) return;
        int tx0 = Math.Clamp((int)Math.Floor((x0 - C.Left) * s / T), 0, nx - 1), tx1 = Math.Clamp((int)Math.Floor((x1 - C.Left) * s / T), 0, nx - 1);
        int ty0 = Math.Clamp((int)Math.Floor((C.Bottom - y1) * s / T), 0, ny - 1), ty1 = Math.Clamp((int)Math.Floor((C.Bottom - y0) * s / T), 0, ny - 1);
        using var p = new SKPaint { FilterQuality = SKFilterQuality.Low, IsAntialias = false, Color = SKColors.White.WithAlpha((byte)(255 * (Scene.HasHighlight ? St.Dim : 1))) };
        var full = new SKRect(0, 0, T, T);
        for (int ty = ty0; ty <= ty1; ty++)
            for (int tx = tx0; tx <= tx1; tx++)
            {
                var img = _tiles.Get(z, tx, ty);
                if (img != null) { DrawTile(c, img, z, tx, ty, full, p); continue; }
                _tiles.Request(z, tx, ty);
                SyncRenders++;
                // stand-in: the nearest coarser tile that covers this one, else the four finer ones
                bool done = false;
                for (int up = 1; up <= z && !done; up++)
                {
                    int px = tx >> up, py = ty >> up; var pimg = _tiles.Get(z - up, px, py); if (pimg == null) continue;
                    float sz = (float)T / (1 << up), sx = (tx - (px << up)) * sz, sy = (ty - (py << up)) * sz;
                    DrawTile(c, pimg, z - up, px, py, new SKRect(sx, sy, sx + sz, sy + sz), p); done = true;
                }
                if (!done && z < _tiles.MaxLevel)
                    for (int k = 0; k < 4; k++) { int cx = tx * 2 + (k & 1), cy = ty * 2 + (k >> 1); if (_tiles.Get(z + 1, cx, cy) is { } ci) DrawTile(c, ci, z + 1, cx, cy, full, p); }
            }
    }

    /// <summary>Redraw (highlight, selection, options or the view changed). Layer content changes are picked up
    /// by the tile signature on the next paint.</summary>
    public void Invalidate() { _frameStale = true; _canvas.InvalidateVisual(); }
    void Redraw() => Invalidate();
    /// <summary>Redraw for the measure tool alone (cursor, snap, live end): the board frame is drawn back as it was.</summary>
    void RedrawMeasure() => _canvas.InvalidateVisual();

    /// <summary>Stop the tile workers and free the board (the document was closed).</summary>
    public void Dispose() { _tiles.TileReady -= OnTileReady; _tiles.Dispose(); Scene.Dispose(); _frame?.Dispose(); _frame = null; }

    void HighlightUpdated() { Scene.RebuildHighlight(St); Invalidate(); SyncBar(); RefreshCard(); if (St.Mode3D) _view3?.RefreshHighlight(); }

    public void Fit() { if (St.Mode3D) { _view3?.FitView(); return; } St.Fit(W, H); Redraw(); }

    public void Set3D(bool on)
    {
        if (on == St.Mode3D) return;
        St.Mode3D = on;
        if (on)
        {
            if (Tool is "measure" or "finger") SetTool("select");
            _view3 ??= new Pcb3DView(this);
            if (!_content.Children.Contains(_view3)) _content.Children.Add(_view3);
            _view3.Visibility = Visibility.Visible; _canvas.Visibility = Visibility.Collapsed;
            _view3.Build();
        }
        else { if (_view3 != null) _view3.Visibility = Visibility.Collapsed; _canvas.Visibility = Visibility.Visible; Invalidate(); }
        _card.Margin = new Thickness(12, 58, on ? 86 : 12, 12);
        _tabsHost.Visibility = on ? Visibility.Collapsed : Visibility.Visible;   // layers mean nothing in 3D
        SyncBar(); RefreshCard(); SyncFingerFx();
    }
    public Pcb3DView View3 => _view3;

    public void RefreshCard() => _card.Refresh();
    public PcbInfoCard Card => _card;

    // ---------- selection / highlight API ----------
    public bool SelectByDes(string des, bool zoom)
    {
        int idx = Doc.Pcb.FindComp(des);
        if (idx < 0) return false;
        St.Sel = PcbSelection.Comp(idx);
        if (zoom && Scene.CompBox(idx) is { } bb)
        {
            float m = Math.Max(bb.Width, bb.Height) * 1.5f + 30;
            var box = new SKRect(bb.Left - m, bb.Top - m, bb.Right + m, bb.Bottom + m);
            if (St.Mode3D) _view3?.FocusOn(bb); else if (_canvas.ActualWidth > 10) St.Fit(W, H, box); else _pendingBox = box;
            _userView = true;
        }
        Invalidate(); SyncBar(); RefreshCard(); _view3?.SelectionUpdated();
        return true;
    }

    /// <summary>Select several components (cross-probe of a schematic area selection).</summary>
    public int SelectMany(IEnumerable<string> refs)
    {
        var items = refs.Select(Doc.Pcb.FindComp).Where(i => i >= 0).Distinct().Select(PcbSelection.Comp).ToList();
        St.Sel = items.Count == 0 ? null : items.Count == 1 ? items[0] : new PcbSelection { Index = -1, Items = items };
        Invalidate(); RefreshCard(); _view3?.SelectionUpdated();
        return items.Count;
    }

    public void HighlightNet(int net, bool add = false)
    {
        if (!add) { St.HlNets.Clear(); St.HlComps.Clear(); }
        if (net >= 0) St.HlNets.Add(net);
        HighlightUpdated();
    }

    public void HighlightComp(int comp, bool add = false)
    {
        if (!add) { St.HlNets.Clear(); St.HlComps.Clear(); }
        if (comp >= 0) St.HlComps.Add(comp);
        HighlightUpdated();
    }

    /// <summary>Highlight from the info card / menus: tells the window, never queries the schematic.</summary>
    public void UserHighlightNet(int net) { HighlightNet(net); HighlightChanged?.Invoke(this, false); }
    public void UserHighlightComp(int comp) { HighlightComp(comp); HighlightChanged?.Invoke(this, false); }

    public void ClearHighlight() { St.HlNets.Clear(); St.HlComps.Clear(); HighlightUpdated(); HighlightChanged?.Invoke(this, false); }

    public void ZoomToHighlight()
    {
        if (Scene.HlBounds is not { } b) return;
        if (St.Mode3D) { _view3?.FocusOn(b); return; }
        float m = Math.Max(b.Width, b.Height) * 0.08f + 20;
        St.Fit(W, H, new SKRect(b.Left - m, b.Top - m, b.Right + m, b.Bottom + m)); _userView = true; Redraw();
    }

    public SKRect? SelectionBox()
    {
        var s = St.Sel; if (s == null) return null;
        IEnumerable<PcbSelection> items = s.Items ?? new List<PcbSelection> { s };
        SKRect? r = null;
        foreach (var it in items)
        {
            SKRect? bb = it.IsComp ? Scene.CompBox(it.Index) : it.Index >= 0 && it.Index < Scene.Objs.Count ? Scene.Objs[it.Index].BB : null;
            if (bb is { } b) r = r is { } a ? new SKRect(Math.Min(a.Left, b.Left), Math.Min(a.Top, b.Top), Math.Max(a.Right, b.Right), Math.Max(a.Bottom, b.Bottom)) : b;
        }
        return r;
    }

    public void ZoomToSelection()
    {
        if (SelectionBox() is not { } b) return;
        if (St.Mode3D) { _view3?.FocusOn(b); return; }
        float m = St.Sel?.Items != null ? Math.Max(b.Width, b.Height) * 0.1f + 20 : Math.Max(b.Width, b.Height) * 1.5f + 30;
        var box = new SKRect(b.Left - m, b.Top - m, b.Right + m, b.Bottom + m);
        if (_canvas.ActualWidth > 10) St.Fit(W, H, box); else _pendingBox = box;
        _userView = true; Redraw();
    }

    public void SetSelection(PcbSelection s, bool raise)
    {
        St.Sel = s; Invalidate(); RefreshCard(); _view3?.SelectionUpdated();
        if (raise) SelectionChanged?.Invoke(this, s);
    }

    // ---------- input ----------
    Point _down, _start; bool _moved, _pan; MouseButton _btn; ModifierKeys _mods;
    Point? _mq;          // rubber-band corner while dragging with the select tool
    Point? _lastMouse;   // for re-evaluating the measure snap when Shift / Ctrl change

    /// <summary>Pick tolerance: 5 screen pixels.</summary>
    double Tol => 5 / St.S;

    void OnWheel(object s, MouseWheelEventArgs e) { WheelAt(e.GetPosition(_canvas), e.Delta); e.Handled = true; }

    public void WheelAt(Point p, int delta)
    {
        St.ZoomAt(Math.Pow(1.0015, delta), p.X, p.Y, W, H); _userView = true;
        Redraw();
    }
    public Point CanvasCenter => new(W / 2, H / 2);

    void OnDown(object s, MouseButtonEventArgs e)
    {
        Focus();
        _down = _start = e.GetPosition(_canvas); _btn = e.ChangedButton; _moved = false; _mq = null; _mods = Keyboard.Modifiers;
        // pan: right or middle drag (and a left drag with the finger tool); holding Space no longer pans (2.9.3: it was
        // easy to set off by mistake)
        _pan = e.ChangedButton is MouseButton.Right or MouseButton.Middle || (Tool == "finger" && e.ChangedButton == MouseButton.Left);
        _canvas.CaptureMouse();
        if (e.ClickCount == 2 && e.ChangedButton == MouseButton.Left && Tool == "select")
        {
            var (wx, wy) = St.ToWorld(_down.X, _down.Y, W, H);
            var o = St.Pick(wx, wy, Tol);
            if (o != null)
            {
                if (o.IsComp) HighlightComp(o.Index);
                else { var ob = Scene.Objs[o.Index]; if (ob.Net != Layers.None) HighlightNet(ob.Net); else if (ob.Comp != Layers.None) HighlightComp(ob.Comp); }
                HighlightChanged?.Invoke(this, false);
            }
            _dbl = true;
        }
        e.Handled = true;
    }
    bool _dbl;

    void OnMove(object s, MouseEventArgs e)
    {
        var p = e.GetPosition(_canvas); _lastMouse = p;
        var (wx, wy) = St.ToWorld(p.X, p.Y, W, H);
        PointerMoved?.Invoke(this, wx, wy);
        if (Tool == "measure" && !_canvas.IsMouseCaptured) MeasHover(wx, wy, Keyboard.Modifiers);
        if (!_canvas.IsMouseCaptured) return;
        double dx = p.X - _down.X, dy = p.Y - _down.Y;
        if (!_moved && Math.Sqrt(dx * dx + dy * dy) > 4) _moved = true;
        if (_moved && _pan) { St.PanBy(dx, dy); _down = p; _userView = true; _canvas.Cursor = Cursors.SizeAll; Redraw(); }
        else if (_moved && _btn == MouseButton.Left && Tool == "select") { _mq = p; Redraw(); }
    }

    void OnUp(object s, MouseButtonEventArgs e)
    {
        if (!_canvas.IsMouseCaptured) return;
        _canvas.ReleaseMouseCapture(); SetMeasCursor(_centerCursor);
        if (_dbl) { _dbl = false; e.Handled = true; return; }
        var mods = _mods | Keyboard.Modifiers;
        if (_mq is { } q) { _mq = null; AreaSelect(_start, q, mods); e.Handled = true; return; }
        var p = e.GetPosition(_canvas);
        var (wx, wy) = St.ToWorld(p.X, p.Y, W, H);
        if (!_moved && e.ChangedButton == MouseButton.Left && Tool == "finger") FingerClick(p, mods);
        else if (!_moved && e.ChangedButton == MouseButton.Left && !_pan)
        {
            if (Tool == "measure") MeasClick(wx, wy, mods);
            else ClickAt(p, mods);
        }
        else if (!_moved && e.ChangedButton == MouseButton.Right) ShowContextMenu(wx, wy);
        e.Handled = true;
    }

    /// <summary>A left click with the select tool at a canvas point. Plain: select. Shift: add / remove this object.
    /// Ctrl: highlight its NET / part. Alt: highlight the NET and look it up in the schematic.</summary>
    public void ClickAt(Point p, ModifierKeys mods)
    {
        var (wx, wy) = St.ToWorld(p.X, p.Y, W, H);
        var o = St.Pick(wx, wy, Tol);
        bool ctrl = (mods & ModifierKeys.Control) != 0, alt = (mods & ModifierKeys.Alt) != 0, shift = (mods & ModifierKeys.Shift) != 0;
        if (shift && !ctrl && !alt)
        {
            if (o == null) return;
            var cur = St.Sel == null ? new List<PcbSelection>() : St.Sel.Items ?? new List<PcbSelection> { St.Sel };
            int i = cur.FindIndex(x => x.Key == o.Key);
            if (i >= 0) cur.RemoveAt(i); else cur.Add(o);
            SetSelection(Many(cur), false);
            return;
        }
        if ((ctrl || alt) && o != null)
        {
            bool isNet = false;
            if (o.IsComp) HighlightComp(o.Index, shift);
            else { var ob = Scene.Objs[o.Index]; if (ob.Net != Layers.None) { HighlightNet(ob.Net, shift); isNet = true; } else if (ob.Comp != Layers.None) HighlightComp(ob.Comp, shift); }
            HighlightChanged?.Invoke(this, alt && isNet);
        }
        SetSelection(o, !(ctrl || alt));   // Ctrl / Alt click = highlight; no part cross-probe on top of it
    }
    /// <summary>For automation: click at a point given relative to this view.</summary>
    public void ClickAtView(Point viewPt, ModifierKeys mods) => ClickAt(TranslatePoint(viewPt, _canvas), mods);

    static PcbSelection Many(List<PcbSelection> items) => items.Count == 0 ? null : items.Count == 1 ? items[0] : new PcbSelection { Index = -1, Items = items };

    /// <summary>Left→right: objects fully inside. Right→left: objects touching the box (selection filter applies).
    /// Shift adds to the selection, Ctrl / Alt removes from it.</summary>
    void AreaSelect(Point a, Point b, ModifierKeys mods)
    {
        var w0 = St.ToWorld(a.X, a.Y, W, H); var w1 = St.ToWorld(b.X, b.Y, W, H);
        var r = new SKRect((float)Math.Min(w0.x, w1.x), (float)Math.Min(w0.y, w1.y), (float)Math.Max(w0.x, w1.x), (float)Math.Max(w0.y, w1.y));
        var found = St.AreaPick(r, b.X < a.X);
        var cur = St.Sel == null ? new List<PcbSelection>() : St.Sel.Items ?? new List<PcbSelection> { St.Sel };
        List<PcbSelection> items;
        if ((mods & ModifierKeys.Shift) != 0) { var keys = new HashSet<string>(cur.Select(x => x.Key)); items = cur.Concat(found.Where(x => keys.Add(x.Key))).ToList(); }
        else if ((mods & (ModifierKeys.Control | ModifierKeys.Alt)) != 0) { var rm = new HashSet<string>(found.Select(x => x.Key)); items = cur.Where(x => !rm.Contains(x.Key)).ToList(); }
        else items = found;
        SetSelection(Many(items), true);
    }

    void DrawMarquee(SKCanvas c)
    {
        if (_mq is not { } q) return;
        bool touch = q.X < _start.X;
        var r = new SKRect((float)Math.Min(_start.X, q.X), (float)Math.Min(_start.Y, q.Y), (float)Math.Max(_start.X, q.X), (float)Math.Max(_start.Y, q.Y));
        using var f = new SKPaint { Color = touch ? new SKColor(90, 220, 130, 26) : new SKColor(90, 155, 255, 30) };
        using var s = new SKPaint { Color = touch ? new SKColor(0x5A, 0xDC, 0x82) : new SKColor(0x5A, 0x9B, 0xFF), Style = SKPaintStyle.Stroke, StrokeWidth = 1.2f, PathEffect = touch ? SKPathEffect.CreateDash(new[] { 6f, 4f }, 0) : null };
        c.DrawRect(r, f); c.DrawRect(r, s);
    }

    // ---------- measure (port of v1): plain = centre snap, Shift = lock 0/45/90°, Ctrl = copper edge,
    // Ctrl+Shift = the edge crossed by the locked line ----------
    SKPoint Constrain45(SKPoint a, SKPoint p)
    {
        // in screen space, so "horizontal" is horizontal on screen even when the view is rotated or mirrored
        var A = St.ToScreen(a.X, a.Y, W, H); var B = St.ToScreen(p.X, p.Y, W, H); double dx = B.X - A.X, dy = B.Y - A.Y;
        double ang = Math.Round(Math.Atan2(dy, dx) / (Math.PI / 4)) * (Math.PI / 4), ux = Math.Cos(ang), uy = Math.Sin(ang), d = dx * ux + dy * uy;
        var (wx, wy) = St.ToWorld(A.X + ux * d, A.Y + uy * d, W, H);
        return new SKPoint((float)wx, (float)wy);
    }

    void MeasHover(double wx, double wy, ModifierKeys mods)
    {
        var m = St.Meas; bool shift = (mods & ModifierKeys.Shift) != 0, ctrl = (mods & ModifierKeys.Control) != 0;
        bool placingB = m.A != null && m.B == null; double R = 12 / St.S;
        var cs = PcbEdges.FindSnap(Scene, St, wx, wy, 10 / St.S);
        SKPoint? p = null; string kind = null, info = ""; PcbObj obj = null;
        if (placingB && shift)
        {
            var q = Constrain45(m.A.Value, cs?.p ?? new SKPoint((float)wx, (float)wy));
            if (ctrl) { if (PcbEdges.EdgeOnRay(Scene, St, m.A.Value, q, R) is { } e) { q = e.p; kind = "edge"; obj = e.o; info = PcbEdges.EdgeName(e.o); } }
            else if (cs is { } c1) { kind = "proj"; obj = c1.o; info = PcbEdges.CenterName(c1.o) + "（投影）"; }
            p = q;
        }
        else if (ctrl)
        {
            if (PcbEdges.NearestEdge(Scene, St, wx, wy, R) is { } e) { p = e.p; kind = "edge"; obj = e.o; info = PcbEdges.EdgeName(e.o); }
            else if (cs is { } c2) { p = c2.p; kind = "center"; obj = c2.o; info = PcbEdges.CenterName(c2.o); }
        }
        else if (cs is { } c3) { p = c3.p; kind = "center"; obj = c3.o; info = PcbEdges.CenterName(c3.o); }
        var before = (m.Snap, m.SnapKind, m.SnapInfo, m.Live, m.Ortho, m.BInfo);
        m.Snap = p; m.SnapKind = kind; m.SnapObj = obj; m.SnapInfo = info;
        if (placingB) { m.Live = p ?? new SKPoint((float)wx, (float)wy); m.Ortho = shift; m.BInfo = info; }
        SetMeasCursor(kind == "center" && obj?.Kind is PcbObjKind.Pad or PcbObjKind.Via);
        // the card's measure numbers are updated in place and only the measure line is redrawn (rebuilding the
        // whole card, with a long pad → NET list, and repainting the board on every mouse move made it stutter)
        if (before == (m.Snap, m.SnapKind, m.SnapInfo, m.Live, m.Ortho, m.BInfo)) return;
        _card.RefreshMeasure();
        RedrawMeasure();
    }

    void MeasClick(double wx, double wy, ModifierKeys mods)
    {
        MeasHover(wx, wy, mods);
        var m = St.Meas;
        var p = m.Snap ?? (m.A != null && m.B == null ? m.Live : null) ?? new SKPoint((float)wx, (float)wy);
        if (m.A == null || m.B != null) { m.A = p; m.B = null; m.Live = null; m.Ortho = false; m.AInfo = m.SnapInfo; m.BInfo = ""; }
        else { m.B = p; m.Ortho = (mods & ModifierKeys.Shift) != 0; m.BInfo = m.SnapInfo; }
        Redraw(); RefreshCard();
    }

    bool _centerCursor;
    void SetMeasCursor(bool onCenter)
    {
        _centerCursor = onCenter && Tool == "measure";
        _canvas.Cursor = Tool == "finger" ? FingerCursor() : Tool != "measure" ? null : _centerCursor ? CenterCursor.Value : Cursors.Cross;
    }

    /// <summary>Crosshair with a ring, shown while the measure tool sits on a pad / via centre.</summary>
    static readonly Lazy<Cursor> CenterCursor = new(() =>
    {
        const int N = 32;
        using var surf = SKSurface.Create(new SKImageInfo(N, N, SKColorType.Bgra8888, SKAlphaType.Premul));
        var c = surf.Canvas; c.Clear(SKColors.Transparent);
        void Shape(SKPaint p) { c.DrawLine(16, 1, 16, 10, p); c.DrawLine(16, 22, 16, 31, p); c.DrawLine(1, 16, 10, 16, p); c.DrawLine(22, 16, 31, 16, p); c.DrawCircle(16, 16, 6.5f, p); }
        using (var o = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 3.4f, Color = new SKColor(0, 0, 0, 190) }) Shape(o);
        using (var w = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f, Color = new SKColor(0x5F, 0xE0, 0xF0) }) Shape(w);
        using (var d = new SKPaint { IsAntialias = true, Color = SKColors.White }) c.DrawCircle(16, 16, 1.6f, d);
        // .cur file: ICONDIR + entry (hot spot 16,16) + 32-bit DIB (XOR, bottom-up) + AND mask
        using var img = surf.Snapshot(); using var px = img.PeekPixels();
        var ms = new System.IO.MemoryStream(); var bw = new System.IO.BinaryWriter(ms);
        int xor = N * N * 4, and = N * 4, size = 40 + xor + and;
        bw.Write((short)0); bw.Write((short)2); bw.Write((short)1);
        bw.Write((byte)N); bw.Write((byte)N); bw.Write((byte)0); bw.Write((byte)0); bw.Write((short)16); bw.Write((short)16); bw.Write(size); bw.Write(22);
        bw.Write(40); bw.Write(N); bw.Write(N * 2); bw.Write((short)1); bw.Write((short)32); bw.Write(0); bw.Write(xor + and); bw.Write(0); bw.Write(0); bw.Write(0); bw.Write(0);
        for (int y = N - 1; y >= 0; y--) for (int x = 0; x < N; x++) { var col = px.GetPixelColor(x, y); bw.Write(col.Blue); bw.Write(col.Green); bw.Write(col.Red); bw.Write(col.Alpha); }
        for (int i = 0; i < and; i++) bw.Write((byte)0);
        ms.Position = 0;
        try { return new Cursor(ms); } catch { return Cursors.Cross; }
    });

    void ShowContextMenu(double wx, double wy)
    {
        var m = new ContextMenu();
        MenuItem I(string h, Action a, bool en = true) { var i = new MenuItem { Header = h, IsEnabled = en }; i.Click += (_, _) => a(); m.Items.Add(i); return i; }
        var o = St.Pick(wx, wy, Tol);
        if (o != null && St.Sel?.Key != o.Key) SetSelection(o, true);
        var ob = o is { IsComp: false } ? Scene.Objs[o.Index] : null;
        if (o?.IsComp == true)
        {
            var des = Doc.Pcb.Comps[o.Index].Des;
            I($"點亮元件 {des}", () => UserHighlightComp(o.Index));
            I("複製位號", () => { try { Clipboard.SetText(des); } catch { } });
        }
        if (ob != null && ob.Net != Layers.None) I($"點亮 NET {Doc.Pcb.NetName(ob.Net)}", () => UserHighlightNet(ob.Net));
        I("縮放到所選", ZoomToSelection, St.Sel != null);
        I("縮放到點亮物件", ZoomToHighlight, Scene.HlBounds != null);
        I("清除點亮", ClearHighlight, St.HlNets.Count + St.HlComps.Count > 0);
        if (_fingers.Count > 0) I($"清除指示標記（{_fingers.Count} 個）", ClearFingers);
        m.Items.Add(new Separator());
        I("全圖 (F)", Fit);
        I(St.BottomView ? "回到頂面 (B)" : "翻到底面 (B)", () => SetBottomView(!St.BottomView));
        m.IsOpen = true;
    }

    void OnKey(object s, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var mod = Keyboard.Modifiers;
        if (St.Mode3D && _view3 != null && _view3.HandleKey(key, mod)) { e.Handled = true; return; }
        if (key is Key.LeftShift or Key.RightShift or Key.LeftCtrl or Key.RightCtrl) { MeasModsChanged(); return; }
        switch (key)
        {
            case Key.Space when Tool == "finger" && !St.Mode3D: if (!e.IsRepeat) FingerRotate(); break;
            case Key.Escape when Tool == "finger":
                if (_fingers.Count > 0) ClearFingers(); else SetTool("select");
                break;
            case Key.P when !St.Mode3D && mod == ModifierKeys.None: SetTool(Tool == "finger" ? "select" : "finger"); break;
            case Key.Escape:
                if (Tool == "measure" && St.Meas.A != null) { St.Meas.A = St.Meas.B = St.Meas.Live = null; Redraw(); RefreshCard(); }
                else if (Tool == "measure") SetTool("select");
                else if (St.HlNets.Count + St.HlComps.Count > 0) ClearHighlight();
                else SetSelection(null, true);
                break;
            case Key.F: Fit(); break;
            case Key.M when !St.Mode3D: SetTool(Tool == "measure" ? "select" : "measure"); break;
            case Key.C when mod == ModifierKeys.Shift: ClearHighlight(); break;
            case Key.S when mod == ModifierKeys.Shift && !St.Mode3D: St.Single = !St.Single; AfterLayers(); break;
            case Key.B: SetBottomView(!St.BottomView); break;
            case Key.X when !St.Mode3D: St.Mx = -St.Mx; Redraw(); SyncBar(); break;
            case Key.Y when !St.Mode3D: St.My = -St.My; Redraw(); SyncBar(); break;
            case Key.R when !St.Mode3D: St.Rot = (St.Rot + 90) % 360; Redraw(); SyncBar(); break;
            case Key.N when !St.Mode3D: St.ShowNetNames = !St.ShowNetNames; Invalidate(); SyncBar(); break;
            case Key.D2: case Key.NumPad2: Set3D(false); break;
            case Key.D3: case Key.NumPad3: Set3D(true); break;
            case Key.Multiply: case Key.Add: CycleLayer(1); break;
            case Key.Subtract: CycleLayer(-1); break;
            case Key.PageUp when !St.Mode3D: St.ZoomAt(1.25, W / 2, H / 2, W, H); Redraw(); break;
            case Key.PageDown when !St.Mode3D: St.ZoomAt(0.8, W / 2, H / 2, W, H); Redraw(); break;
            case Key.LeftAlt: case Key.RightAlt: break;
            default: return;
        }
        e.Handled = true;
    }

    /// <summary>For automation: hover (and optionally click) the measure tool at a point relative to this view.</summary>
    public void MeasureAtView(Point viewPt, ModifierKeys mods, bool click)
    {
        if (Tool != "measure") SetTool("measure");
        var p = TranslatePoint(viewPt, _canvas); _lastMouse = p; var (wx, wy) = St.ToWorld(p.X, p.Y, W, H);
        if (click) MeasClick(wx, wy, mods); else MeasHover(wx, wy, mods);
    }
    public bool CenterCursorShown => _centerCursor;

    void MeasModsChanged()
    {
        if (Tool != "measure" || _lastMouse is not { } lm || St.Mode3D) return;
        var (wx, wy) = St.ToWorld(lm.X, lm.Y, W, H); MeasHover(wx, wy, Keyboard.Modifiers);
    }

    /// <summary>For automation: DIP position (relative to this view) of a component centre.</summary>
    public Point? ScreenPointOfPad(string des, string pad)
    {
        int i = Doc.Pcb.FindComp(des); var p = Doc.Pcb.Pads.FirstOrDefault(x => x.Comp == i && x.Name == pad); if (p == null) return null;
        var s = St.ToScreen(p.X, p.Y, W, H);
        return _canvas.TranslatePoint(new Point(s.X, s.Y), this);
    }

    public Point? ScreenPointOfComp(string des)
    {
        int i = Doc.Pcb.FindComp(des); if (i < 0 || Scene.CompBox(i) is not { } b) return null;
        var p = St.ToScreen(b.MidX, b.MidY, W, H);
        return _canvas.TranslatePoint(new Point(p.X, p.Y), this);
    }

    /// <summary>For automation: DIP position of the middle of the longest track of a NET.</summary>
    public Point? ScreenPointOfNet(string net)
    {
        int n = Doc.Pcb.Nets.IndexOf(net); if (n < 0) return null;
        var t = Doc.Pcb.Tracks.Where(x => x.Net == n && St.IsVisible(x.L)).OrderBy(x => x.L == St.Cur ? 0 : 1).ThenByDescending(x => Math.Abs(x.X2 - x.X1) + Math.Abs(x.Y2 - x.Y1)).FirstOrDefault();
        if (t == null) return null;
        var p = St.ToScreen((t.X1 + t.X2) / 2, (t.Y1 + t.Y2) / 2, W, H);
        return _canvas.TranslatePoint(new Point(p.X, p.Y), this);
    }
}
