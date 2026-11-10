// Herald PCB Reader — Copyright (C) 2026 Johnny.ch.Tsai
// SPDX-License-Identifier: GPL-3.0-only
// Free software under the GNU General Public License version 3 (LICENSE.txt), WITHOUT ANY WARRANTY; with an
// additional permission to combine it with AvalonDock (Ms-PL), see NOTICE.txt.
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using AdApp.Panels;
using AdApp.Views;
using AdCore.Pcb;
using AdCore.Project;
using AdCore.Render;
using AdCore.Sch;
using AvalonDock.Layout;
using Microsoft.Win32;
using static AdCore.Util.TextUtil;

namespace AdApp;

public partial class MainWindow : Window
{
    public const string Product = "Herald PCB Reader";
    public readonly Workspace Ws = _early?.Ws ?? new();
    public readonly Settings Cfg = _early?.Cfg ?? Settings.Load();
    readonly ProjectsPanel _projects = new();
    /// <summary>Navigator starts closed (檢視 → 面板 → Navigator): it is built, and filled for the active project, only
    /// while it is open — about 50 ms less at start-up, and no lists rebuilt for a closed panel at every tab change.</summary>
    NavigatorPanel _nav;
    (ProjectGroup G, DocItem D) _navFor; bool _navStale = true;
    readonly PropertiesPanel _props = new();
    readonly ListBox _msgs = new() { FontSize = 12 };
    public readonly Dictionary<DocItem, LayoutDocument> DocsOpen = new();
    public bool Split { get; private set; }
    public FrameworkElement ActiveView { get; private set; }
    ProjectNet _groupNet; ProjectGroup _groupNetGroup;
    ToggleButton _splitBtn, _jumpBtn;
    MenuItem _recentMenu, _jumpMenu, _autoSplitMenu;
    readonly List<string> _startPaths; readonly bool _fresh;
    bool _propsAutoHidden;
    internal AutoHideFlyout Flyout { get; private set; }
    /// <summary>Automation: where the last Open spent its time.</summary>
    public string LastOpenTiming { get; private set; }
    public string LastStatus => StatusText.Text;
    public string LastProbe => StatusProbe.Text;

    /// <summary>Files being read before the window exists: what was asked for and the work under way.</summary>
    sealed record EarlyOpen(Workspace Ws, Settings Cfg, List<string> List, bool Expand, bool Restoring, Task<OpenResult> Work);
    static EarlyOpen _early;

    /// <summary>App start: read the files to open (or the last session) right away on a worker, while the window is
    /// built — building and showing the window is most of the start-up. The PCB views about to open get their scenes
    /// built there as well. The window picks up this workspace and settings and waits for the work when it is loaded.</summary>
    public static void BeginEarlyOpen(List<string> paths, bool fresh)
    {
        var cfg = Settings.Load(); var ws = new Workspace();
        bool restoring = paths.Count == 0;
        var list = !restoring ? paths : fresh ? new List<string>() : cfg.Session.Where(p => File.Exists(p) || Directory.Exists(p)).ToList();
        Task<OpenResult> work = null;
        if (list.Count > 0)
        {
            var groups = restoring ? cfg.Groups : null;
            var tabs = restoring && cfg.OpenDocs != null ? new HashSet<string>(cfg.OpenDocs, StringComparer.OrdinalIgnoreCase) : null;
            work = Task.Run(() =>
            {
                App.Mark("worker");
                OpenResult r;
                lock (ws) { r = ws.Open(list, !restoring); ws.RestoreGroups(groups); }
                App.Mark("parsed");
                PrepareScenes(r, tabs);
                App.Mark("scenes");
                return r;
            });
        }
        _early = new EarlyOpen(ws, cfg, list, !restoring, restoring, work);
    }

    /// <summary>The scenes of the PCBs that will get a tab (all of them, or the tabs of the session), built in parallel.</summary>
    static void PrepareScenes(OpenResult r, HashSet<string> tabs)
    {
        var pcbs = r.Groups.SelectMany(g => g.Docs).Concat(r.Added).Distinct()
            .Where(d => d.Loaded && d.Kind == DocKind.Pcb && d.Pcb != null && (tabs == null || tabs.Contains(d.Path))).ToList();
        Parallel.ForEach(pcbs, new ParallelOptions { MaxDegreeOfParallelism = Workspace.ParseThreads }, d =>
        {
            var t = System.Threading.Thread.CurrentThread; var pr = t.Priority;
            t.Priority = System.Threading.ThreadPriority.BelowNormal;
            try { PcbView.Prepared[d] = new PcbScene(d.Pcb); } catch (Exception) { } finally { t.Priority = pr; }
        });
    }

    /// <summary>Every document tab header (MainWindow.xaml) when it is made: style its tab.</summary>
    void DocHeader_Loaded(object s, RoutedEventArgs e)
    {
        for (var d = s as DependencyObject; d != null; d = VisualTreeHelper.GetParent(d)) if (d is TabItem t) { StyleDocTab(t); break; }
    }

    static readonly Brush CloseInk = Ui.Br(0x1E, 0x1E, 0x1E);

    /// <summary>AD19-style document tabs (each time the theme makes one): no padding or reserved room around the coloured
    /// header, so the tabs touch; the ✕ takes no room of its own but sits over the tab's right end (over the end of a
    /// long name, on the tab's colour) when it shows — on the tab in front and under the mouse.</summary>
    internal static void StyleDocTab(TabItem t)
    {
        if (Ui.Descend(t).OfType<AvalonDock.Controls.LayoutDocumentTabItem>().FirstOrDefault() is not { } ti) return;
        t.Padding = new Thickness(0); t.Margin = new Thickness(0);
        var header = Ui.Descend(ti).OfType<Border>().FirstOrDefault(b => b.Name == "Header");
        if (header == null) return;
        header.Padding = new Thickness(0); header.BorderThickness = new Thickness(0); header.Margin = new Thickness(0, 0, 0, 2);
        if (header.Child is not Grid g) return;
        if (g.Children.OfType<ContentPresenter>().FirstOrDefault() is { } cp) cp.Margin = new Thickness(0);
        if (g.Children.OfType<Button>().FirstOrDefault(b => b.Name == "DocumentCloseButton") is { } close)
        {
            Grid.SetColumn(close, 0); close.HorizontalAlignment = HorizontalAlignment.Right; close.VerticalAlignment = VerticalAlignment.Center;
            close.Margin = new Thickness(0, 0, 3, 0);
            if (t.DataContext is LayoutDocument { Content: SchView or PcbView } ld) close.Background = ld.Content is SchView sv ? sv.TabBrush : ((PcbView)ld.Content).TabBrush;
            foreach (var p in Ui.Descend(close).OfType<System.Windows.Shapes.Path>()) p.Fill = CloseInk;
        }
    }

    public MainWindow(List<string> paths, bool fresh)
    {
        App.Mark("ctor");
        InitializeComponent();
        App.Mark("xaml");
        _startPaths = paths; _fresh = fresh;
        Units.Mm = Cfg.UnitsMm;
        SchView.Theme = Cfg.SchTheme switch { "Dark" => SchTheme.Dark, "Mono" => SchTheme.Mono, _ => SchTheme.Altium };
        SchView.FontScale = Cfg.FontScale;
        RestorePlacement();
        SourceInitialized += (_, _) => KeepOnScreen();

        ProjectsAnch.Content = _projects; PropAnch.Content = _props;
        NavAnch.IsVisibleChanged += (_, _) => SyncNav();   // Navigator is built when it is first opened
        _msgs.Background = Ui.Bg2; MsgAnch.Content = _msgs;
        foreach (var a in Anchs) WatchAutoHide(a);
        ApplyDisplaySettings(); App.Mark("display");
        _props.ShowNothing("開啟 .PrjPcb 專案、.SchDoc、.PcbDoc 或整個資料夾開始使用。\n按 F1 看使用說明。");

        WirePanels();
        BuildMenu();
        BuildToolbar();
        BuildUnitSwitch();
        UpdateUnitLabel(); App.Mark("menus");
        ContentRendered += (_, _) => App.Mark("rendered");
        EventHandler first = null; first = (_, _) => { App.Mark("frame"); System.Windows.Media.CompositionTarget.Rendering -= first; };
        System.Windows.Media.CompositionTarget.Rendering += first;
        Dock.ActiveContentChanged += (_, _) => OnActiveChanged();
        Drop += OnDrop;
        DragOver += (_, e) => { e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; };
        Loaded += async (_, _) =>
        {
            App.Mark("loaded");
            RestorePanels(); App.Mark("panels");
            // after the DockingManager's own Loaded, which sets up its auto-hide internals
            _ = Dispatcher.BeginInvoke(() => Flyout = AutoHideFlyout.TryCreate(Dock, FlyoutFx), System.Windows.Threading.DispatcherPriority.Background);
            if (_early is { Work: { } work } e) { _early = null; await Open(e.List, e.Expand, e.Restoring, work); }
            else if (_startPaths.Count > 0) await Open(_startPaths, true);
            else if (!_fresh && Cfg.Session.Count > 0) await Open(Cfg.Session.Where(p => File.Exists(p) || Directory.Exists(p)).ToList(), false, true);
        };
        Closing += (_, _) => SaveSettings();
        PreviewKeyDown += OnGlobalKey;
    }

    void SaveSettings()
    {
        Cfg.Session = Ws.SessionPaths(); Cfg.Groups = Ws.SessionGroups();
        // the tabs as they are now, so the next start shows the same pages (not every file of every project)
        var tabs = Panes().SelectMany(p => p.Children.OfType<LayoutDocument>())
            .Select(ld => DocsOpen.FirstOrDefault(kv => kv.Value == ld).Key).Where(d => d != null).ToList();
        Cfg.OpenDocs = tabs.Select(d => d.Path).ToList();
        Cfg.ShownDocs = tabs.Where(d => DocsOpen[d].IsSelected).Select(d => d.Path).ToList();
        Cfg.ActiveDocPath = ActiveDoc?.Path; Cfg.SessionSplit = Split;
        SavePanels();
        Cfg.UnitsMm = Units.Mm; Cfg.FontScale = SchView.FontScale; Cfg.SchTheme = SchView.Theme.Name;
        Cfg.Maximized = WindowState == WindowState.Maximized;
        if (WindowState == WindowState.Normal) { Cfg.Left = Left; Cfg.Top = Top; Cfg.Width = Width; Cfg.Height = Height; }
        else { var r = RestoreBounds; if (!r.IsEmpty) { Cfg.Left = r.Left; Cfg.Top = r.Top; Cfg.Width = r.Width; Cfg.Height = r.Height; } }
        Cfg.Save();
    }

    // ================= docking panels: auto-hide and sizes =================
    LayoutAnchorable[] Anchs => new[] { ProjectsAnch, NavAnch, PropAnch, MsgAnch };

    /// <summary>Per panel: the pane it docks into (where it is, or goes back to from auto-hide) and whether it was on
    /// auto-hide before it was hidden (AvalonDock keeps both internal).</summary>
    readonly Dictionary<LayoutAnchorable, (LayoutAnchorablePane Pane, bool AutoHidden)> _dockOf = new();
    LayoutAnchorablePane DockPane(LayoutAnchorable a) => a.Parent as LayoutAnchorablePane ?? (_dockOf.TryGetValue(a, out var d) && d.Pane?.Root != null ? d.Pane : null);
    bool IsAutoHiddenState(LayoutAnchorable a) => a.IsAutoHidden || a.Parent is LayoutRoot && _dockOf.TryGetValue(a, out var d) && d.AutoHidden;

    /// <summary>The element whose DockWidth (horizontal) or DockHeight sets the pane's size: the pane, or the
    /// group holding it, as a child of a panel running that way.</summary>
    static ILayoutElement SizedBy(ILayoutElement e, Orientation o)
    {
        for (; e?.Parent is ILayoutElement p; e = p)
            if (p is ILayoutOrientableGroup og && og.Orientation == o) return e is LayoutAnchorablePane or LayoutAnchorablePaneGroup ? e : null;
        return null;
    }
    static GridLength? GetDock(ILayoutElement e, bool width) => e switch
    {
        LayoutAnchorablePane p => width ? p.DockWidth : p.DockHeight,
        LayoutAnchorablePaneGroup g => width ? g.DockWidth : g.DockHeight,
        _ => null,
    };
    static void SetDock(ILayoutElement e, bool width, double v)
    {
        if (GetDock(e, width) is not { IsAbsolute: true }) return;   // proportional sizes stay proportional
        var g = new GridLength(v);
        if (e is LayoutAnchorablePane p) { if (width) p.DockWidth = g; else p.DockHeight = g; }
        else if (e is LayoutAnchorablePaneGroup pg) { if (width) pg.DockWidth = g; else pg.DockHeight = g; }
    }
    /// <summary>Docked size in pixels; 0 when it is proportional (left to the window size).</summary>
    static double DockSize(ILayoutElement e, bool width) => GetDock(e, width) is { IsAbsolute: true } g ? g.Value : 0;

    /// <summary>A panel put on auto-hide flies out as wide as it was docked (AvalonDock's default is its 100 px minimum).</summary>
    void WatchAutoHide(LayoutAnchorable a)
    {
        double w = 0, h = 0;
        _dockOf[a] = (a.Parent as LayoutAnchorablePane, a.IsAutoHidden);
        a.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(a.Parent)) return;
            if (a.Parent is LayoutAnchorablePane p) _dockOf[a] = (p, false);
            else if (a.Parent is LayoutAnchorGroup) _dockOf[a] = (_dockOf[a].Pane, true);
        };
        a.PropertyChanging += (_, e) =>
        {
            if (e.PropertyName != nameof(a.Parent) || a.Parent is not LayoutAnchorablePane p) return;
            // the pane's size, as shown by its front tab (the others are not laid out)
            var fe = p.Children.Select(c => c.Content as FrameworkElement).Where(c => c != null).OrderByDescending(c => c.ActualWidth).FirstOrDefault();
            w = fe?.ActualWidth ?? 0; h = fe?.ActualHeight ?? 0;
        };
        a.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(a.Parent) || a.Parent == null) return;
            if (a.Parent is LayoutAnchorGroup)
            {
                var pane = DockPane(a);
                if (w < 120) w = DockSize(SizedBy(pane, Orientation.Horizontal), true);
                if (h < 80) h = DockSize(SizedBy(pane, Orientation.Vertical), false);
                if (w >= 120) a.AutoHideWidth = w; else if (a.AutoHideWidth < 120) a.AutoHideWidth = 300;
                if (h >= 80) a.AutoHideHeight = h; else if (a.AutoHideHeight < 80) a.AutoHideHeight = 200;
            }
            w = h = 0;
        };
    }

    /// <summary>Automation: docked width as a splitter drag leaves it.</summary>
    public void SetPanelWidth(string id, double w) { var a = Anchs.First(x => x.ContentId == id); SetDock(SizedBy(DockPane(a), Orientation.Horizontal), true, w); }

    void SavePanels()
    {
        Cfg.Panels = Anchs.Select(a =>
        {
            var pane = DockPane(a);
            return new PanelState
            {
                Id = a.ContentId,
                // held open by a double-click: the user's setting is still auto-hide
                AutoHidden = IsAutoHiddenState(a) || a == PropAnch && _propsHeld != null,
                DockWidth = DockSize(SizedBy(pane, Orientation.Horizontal), true),
                DockHeight = DockSize(SizedBy(pane, Orientation.Vertical), false),
                AutoHideWidth = a.AutoHideWidth, AutoHideHeight = a.AutoHideHeight,
                Closed = a == PropAnch ? null : a.IsHidden,
            };
        }).ToList();
    }

    /// <summary>Panels that start closed until the user opens them (檢視 → 面板).</summary>
    bool ClosedByDefault(LayoutAnchorable a) => a == NavAnch || a == MsgAnch;

    void RestorePanels()
    {
        foreach (var s in Cfg.Panels ?? new())
        {
            var a = Anchs.FirstOrDefault(x => x.ContentId == s.Id); if (a == null) continue;
            if (IsAutoHiddenState(a) != s.AutoHidden && !a.IsHidden) a.ToggleAutoHide();   // a pane's panels go together
            var pane = DockPane(a);
            double maxW = Math.Max(200, Width * 0.45), maxH = Math.Max(120, Height * 0.6);
            if (s.DockWidth >= 80) SetDock(SizedBy(pane, Orientation.Horizontal), true, Math.Min(s.DockWidth, maxW));
            if (s.DockHeight >= 60) SetDock(SizedBy(pane, Orientation.Vertical), false, Math.Min(s.DockHeight, maxH));
            if (s.AutoHideWidth >= 120) a.AutoHideWidth = Math.Min(s.AutoHideWidth, maxW);
            if (s.AutoHideHeight >= 80) a.AutoHideHeight = Math.Min(s.AutoHideHeight, maxH);
        }
        // then the open / closed state (after the auto-hide moves above, so a panel opened again comes back where it was)
        foreach (var a in Anchs.Where(x => x != PropAnch))
        {
            bool closed = Cfg.Panels?.FirstOrDefault(s => s.Id == a.ContentId)?.Closed ?? ClosedByDefault(a);
            if (closed && !a.IsHidden) a.Hide(); else if (!closed && a.IsHidden) a.Show();
        }
    }

    // ================= window placement =================
    /// <summary>Size and position from the last session, kept inside the screen (another PC may have a smaller
    /// screen or a larger scaling: a window taller than the screen would put the title bar out of reach).</summary>
    void RestorePlacement()
    {
        var wa = SystemParameters.WorkArea;
        double w = Cfg.Width > 300 ? Cfg.Width : 1600, h = Cfg.Height > 200 ? Cfg.Height : 980;
        Width = Math.Min(w, wa.Width); Height = Math.Min(h, wa.Height);
        WindowStartupLocation = WindowStartupLocation.Manual;
        double vl = SystemParameters.VirtualScreenLeft, vt = SystemParameters.VirtualScreenTop;
        double vr = vl + SystemParameters.VirtualScreenWidth, vb = vt + SystemParameters.VirtualScreenHeight;
        bool ok = !double.IsNaN(Cfg.Left) && !double.IsNaN(Cfg.Top) && Cfg.Top >= vt && Cfg.Top <= vb - 80 && Cfg.Left + Width >= vl + 160 && Cfg.Left <= vr - 160;
        if (ok) { Left = Cfg.Left; Top = Cfg.Top; }
        else { Left = wa.Left + (wa.Width - Width) / 2; Top = wa.Top + (wa.Height - Height) / 2; }
        if (Cfg.Maximized) WindowState = WindowState.Maximized;
    }

    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr h, uint flags);
    [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr m, ref MONITORINFO mi);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);

    /// <summary>Final check in device pixels against the work area of the monitor the window landed on.</summary>
    void KeepOnScreen()
    {
        var h = new WindowInteropHelper(this).Handle;
        if (h == IntPtr.Zero || !GetWindowRect(h, out var r)) return;
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(MonitorFromWindow(h, 2), ref mi)) return;
        var wa = mi.rcWork;
        int w = Math.Min(r.Right - r.Left, wa.Right - wa.Left), hh = Math.Min(r.Bottom - r.Top, wa.Bottom - wa.Top);
        int x = Math.Clamp(r.Left, wa.Left, wa.Right - w), y = Math.Clamp(r.Top, wa.Top, wa.Bottom - hh);
        if (x != r.Left || y != r.Top || w != r.Right - r.Left || hh != r.Bottom - r.Top)
            SetWindowPos(h, IntPtr.Zero, x, y, w, hh, 0x0004 | 0x0010);
    }

    // ================= status / log =================
    public void SetStatus(string s) => StatusText.Text = s;
    void Probe(string s) { StatusProbe.Text = s; SetStatus(s); }
    public void Log(string s)
    {
        _msgs.Items.Add(new TextBlock { Text = $"{DateTime.Now:HH:mm:ss}  {s}", Foreground = Ui.Text, TextWrapping = TextWrapping.Wrap });
        _msgs.ScrollIntoView(_msgs.Items[^1]);
    }
    // mm | mil switch at the right end of the status bar
    Border _unitMm, _unitMil;
    void BuildUnitSwitch()
    {
        Border Seg(string t, bool mm)
        {
            var b = new Border { Padding = new Thickness(9, 0, 9, 1), Cursor = Cursors.Hand, ToolTip = "長度單位（Q 切換；檔案 → 設定 也可以改）", Child = new TextBlock { Text = t, FontSize = 11.5 } };
            b.MouseLeftButtonUp += (_, _) => SetUnits(mm);
            return b;
        }
        _unitMm = Seg("mm", true); _unitMil = Seg("mil", false);
        UnitSwitch.Child = Ui.Row(_unitMm, _unitMil);
    }
    /// <summary>Automation: a click on the mm or mil segment.</summary>
    public void ClickUnitSwitch(bool mm) => (mm ? _unitMm : _unitMil).RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonUpEvent });
    public bool UnitsMm => Units.Mm;
    void UpdateUnitLabel()
    {
        foreach (var (b, on) in new[] { (_unitMm, Units.Mm), (_unitMil, !Units.Mm) })
        {
            b.Background = on ? Ui.Accent : Brushes.Transparent;
            ((TextBlock)b.Child).Foreground = on ? Brushes.White : Ui.Dim;
            ((TextBlock)b.Child).FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal;
        }
    }

    // ================= open =================
    public async void OpenFromOutside(List<string> paths)
    {
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        await Open(paths, true);
    }

    /// <param name="started">the same open already running (started at App start, BeginEarlyOpen)</param>
    public async Task Open(IEnumerable<string> paths, bool expandSingle, bool restoring = false, Task<OpenResult> started = null)
    {
        var list = paths.Where(p => !string.IsNullOrWhiteSpace(p)).ToList(); if (list.Count == 0) return;
        SetStatus("解析中…"); Mouse.OverrideCursor = Cursors.Wait;
        OpenResult res;
        var groups = restoring ? Cfg.Groups : null;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        App.Mark("open");
        try { res = started != null ? await started : await Task.Run(() => { lock (Ws) { var r = Ws.Open(list, expandSingle); Ws.RestoreGroups(groups); return r; } }); }
        finally { Mouse.OverrideCursor = null; }
        long tParse = sw.ElapsedMilliseconds; App.Mark("resumed");
        foreach (var m in res.Messages) Log(m);
        if (!restoring) { Cfg.AddRecent(list); RebuildRecent(); }
        var toOpen = new List<DocItem>();
        foreach (var g in res.Groups) toOpen.AddRange(g.Docs.Where(d => d.Loaded));
        toOpen.AddRange(res.Added.Where(d => d.Loaded && !toOpen.Contains(d)));
        // restoring: only the tabs that were open last time, in their order; the other files stay listed in Projects
        bool keepTabs = restoring && Cfg.OpenDocs != null;
        if (keepTabs)
        {
            var order = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in Cfg.OpenDocs) order.TryAdd(p, order.Count);
            toOpen = toOpen.Where(d => order.ContainsKey(d.Path)).OrderBy(d => order[d.Path]).ToList();
        }
        var tViews = new List<string>();
        foreach (var d in toOpen) { long t0 = sw.ElapsedMilliseconds; OpenDocView(d, false); tViews.Add($"{d.Name} {sw.ElapsedMilliseconds - t0}"); }
        long tTabs = sw.ElapsedMilliseconds; App.Mark("tabs");
        foreach (var k in PcbView.Prepared.Keys.ToList()) if (PcbView.Prepared.TryRemove(k, out var unused)) unused.Dispose();   // built for a tab that did not open
        var grp = res.Focus?.Group ?? res.Groups.FirstOrDefault() ?? res.Added.FirstOrDefault()?.Group;
        if (keepTabs)
        {
            if (Cfg.SessionSplit) SetSplit(true);
            foreach (var p in Cfg.ShownDocs ?? new()) if (Ws.FindDoc(p) is { } sd && DocsOpen.TryGetValue(sd, out var sl)) sl.IsSelected = true;
            var act = Cfg.ActiveDocPath != null ? Ws.FindDoc(Cfg.ActiveDocPath) : null;
            var first = act != null && DocsOpen.ContainsKey(act) ? act : toOpen.FirstOrDefault();
            if (first != null)
            {
                ActivateDoc(first);
                // the other pane's view takes the focus when it is laid out: give it back once the layout has settled
                Dispatcher.BeginInvoke(() => { if (DocsOpen.ContainsKey(first)) ActivateDoc(first); }, System.Windows.Threading.DispatcherPriority.ContextIdle);
            }
        }
        else
        {
            if (Cfg.AutoSplit && grp != null && grp.Kind != GroupKind.Free && grp.SchDocs.Any() && grp.PcbDocs.Any()) SetSplit(true, grp);
            var focus = res.Focus is { Loaded: true } ? res.Focus : toOpen.FirstOrDefault(d => d.Kind == DocKind.Sch) ?? toOpen.FirstOrDefault();
            if (focus != null) ActivateDoc(focus);
            if (Split && grp != null) SelectInPane(grp.PcbDocs.FirstOrDefault(d => d == focus) ?? grp.PcbDocs.FirstOrDefault());
        }
        RefreshProjects(); App.Mark("open done");
        int nTabs = toOpen.Count; bool atStart = started != null;
        Dispatcher.BeginInvoke(() =>
        {
            App.Mark("idle");
            // the files opened with the program (double-click, last session): how long until they were on screen
            if (atStart) Log($"開啟完成：{App.SinceStart:0.00} 秒（從程式啟動到 {nTabs} 個分頁顯示出來）");
        }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        LastOpenTiming = $"open {tParse} ms ({res.Timing}), tabs {tTabs - tParse} ms [{string.Join(", ", tViews)}], total {sw.ElapsedMilliseconds} ms, since start {(DateTime.Now - System.Diagnostics.Process.GetCurrentProcess().StartTime).TotalMilliseconds:0} ms";
        if (keepTabs)
        {
            SetStatus($"已恢復上次的工作階段：{toOpen.Count} 個分頁（其他檔案在 Projects 面板，點兩下開啟）");
            if (res.Added.Any(d => d.Error != null)) MsgAnch.Show();
            return;
        }
        int nSch = toOpen.Count(d => d.Kind == DocKind.Sch), nPcb = toOpen.Count(d => d.Kind == DocKind.Pcb);
        var errs = res.Added.Count(d => d.Error != null);
        SetStatus(toOpen.Count == 0 && errs == 0 ? "沒有找到可開啟的檔案（支援 .PrjPcb / .SchDoc / .PcbDoc / KiCad / Eagle）" :
            $"已開啟：{(res.Groups.Count > 0 ? res.Groups.Count + " 個專案 · " : "")}{nSch} 份電路圖 · {nPcb} 份 PCB{(errs > 0 ? $" · {errs} 個檔案無法開啟（見 Messages）" : "")}");
        if (errs > 0) MsgAnch.Show();
    }

    void OnDrop(object s, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files) _ = Open(files, files.Length == 1);
    }

    // ================= documents / panes =================
    List<LayoutDocumentPane> Panes() => Dock.Layout.Descendents().OfType<LayoutDocumentPane>().ToList();

    LayoutDocumentPane MainDocPane()
    {
        var p = Panes();
        if (p.Count > 0) return p[0];
        var pane = new LayoutDocumentPane();
        var grp = Dock.Layout.Descendents().OfType<LayoutDocumentPaneGroup>().FirstOrDefault();
        if (grp != null) grp.Children.Add(pane); else Dock.Layout.RootPanel.Children.Insert(Math.Min(1, Dock.Layout.RootPanel.ChildrenCount), pane);
        return pane;
    }

    (LayoutDocumentPane left, LayoutDocumentPane right) SplitPanes()
    {
        var left = MainDocPane();
        var grp = left.Parent as LayoutDocumentPaneGroup;
        if (grp == null)
        {
            var parent = (ILayoutContainer)left.Parent; var panel = parent as LayoutPanel;
            grp = new LayoutDocumentPaneGroup { Orientation = Orientation.Horizontal };
            if (panel != null) { int i = panel.Children.IndexOf(left); panel.RemoveChildAt(i); grp.Children.Add(left); panel.InsertChildAt(i, grp); }
        }
        grp.Orientation = Orientation.Horizontal;
        var right = grp.Children.OfType<LayoutDocumentPane>().FirstOrDefault(p => p != left);
        if (right == null) { right = new LayoutDocumentPane(); grp.Children.Add(right); }
        return (left, right);
    }

    LayoutDocumentPane TargetPane(DocItem d) => Split ? (d.Kind == DocKind.Sch ? SplitPanes().left : SplitPanes().right) : MainDocPane();

    public FrameworkElement ViewOf(DocItem d) => d != null && DocsOpen.TryGetValue(d, out var ld) ? ld.Content as FrameworkElement : null;

    public FrameworkElement OpenDocView(DocItem d, bool activate)
    {
        if (d == null || !d.Loaded) return null;
        if (DocsOpen.TryGetValue(d, out var ld)) { if (activate) ld.IsActive = true; return (FrameworkElement)ld.Content; }
        FrameworkElement view;
        try { view = d.Kind == DocKind.Sch ? MakeSch(d) : MakePcb(d); }
        catch (Exception e) { Log($"{d.Name}：無法建立檢視 {e.Message}"); return null; }
        ld = new LayoutDocument { Title = d.Name, ContentId = d.Path, Content = view, ToolTip = d.Path };
        TargetPane(d).Children.Add(ld);
        DocsOpen[d] = ld;
        // a file opened while a project net is highlighted (Projects: 可跳轉) shows that net too
        if (view is SchView nv && _groupNet != null && _groupNetGroup == d.Group) nv.ApplyGroupNet(_groupNet, true);
        ld.Closed += (_, _) => { DocsOpen.Remove(d); if (view is PcbView pv) pv.Dispose(); RefreshProjects(); CheckPropsHold(); };
        if (activate) ld.IsActive = true;
        return view;
    }

    public void ActivateDoc(DocItem d) { var v = OpenDocView(d, true); if (v != null && DocsOpen.TryGetValue(d, out var ld)) { ld.IsSelected = true; ld.IsActive = true; } }
    void SelectInPane(DocItem d) { if (d != null && OpenDocView(d, false) != null) DocsOpen[d].IsSelected = true; }
    bool IsVisibleDoc(DocItem d) => DocsOpen.TryGetValue(d, out var ld) && ld.IsSelected;

    SchView MakeSch(DocItem d)
    {
        var v = new SchView(d);
        v.SelectionChanged += OnSchSelection;
        v.GoToPage += (od, sheet) => GoToNetPage(od, sheet, v.Sel?.PNet);
        v.SheetShown += _ => { if (ActiveView == v) _props.ShowSchSel(v.Doc, v.Sel, v.SheetIndex); };
        v.PartDoubleClicked += HoldProps;
        v.PointerMoved += (_, x, y) =>
        {
            var h = v.Doc.Sch.Sheets[v.SheetIndex].Page?.H ?? 0;
            double X = x, Y = h - y;
            StatusXY.Text = Units.Mm ? $"X {X,8:0.00}  Y {Y,8:0.00}" : $"X {X / 0.0254,8:0}  Y {Y / 0.0254,8:0}";
        };
        return v;
    }

    /// <summary>A page of the highlighted net (page lists of the schematic and Properties): open that file on that
    /// sheet with the net highlighted, and show it in Properties (a file that was not open had no highlight yet).</summary>
    void GoToNetPage(DocItem od, int sheet, ProjectNet pnet)
    {
        ActivateDoc(od);
        if (ViewOf(od) is not SchView sv) return;
        sv.ShowNetPage(sheet, pnet ?? _groupNet);
        _props.ShowSchSel(sv.Doc, sv.Sel, sv.SheetIndex);
        RefreshProjects();
    }

    PcbView MakePcb(DocItem d)
    {
        var v = new PcbView(d);
        v.SelectionChanged += OnPcbSelection;
        v.HighlightChanged += OnPcbHighlight;
        v.UnitsChanged += () => { UpdateUnitLabel(); foreach (var pv in DocsOpen.Values.Select(l => l.Content).OfType<PcbView>()) pv.RefreshCard(); };
        v.PointerMoved += (_, x, y) => StatusXY.Text = $"X {Units.Fmt(x - d.Pcb.OriginX),9}  Y {Units.Fmt(y - d.Pcb.OriginY),9}";
        return v;
    }

    public void SetSplit(bool on, ProjectGroup g = null)
    {
        Split = on;
        if (on)
        {
            var (L, R) = SplitPanes();
            foreach (var (d, ld) in DocsOpen.ToList())
            {
                var target = d.Kind == DocKind.Sch ? L : R;
                if (ld.Parent != target) { (ld.Parent as LayoutDocumentPane)?.Children.Remove(ld); target.Children.Add(ld); }
            }
            if (g != null)
            {
                var cur = ActiveView is SchView sv && sv.Doc.Group == g ? sv.Doc : g.SchDocs.FirstOrDefault();
                SelectInPane(cur); SelectInPane(g.PcbDocs.FirstOrDefault());
            }
        }
        else
        {
            var main = MainDocPane();
            foreach (var ld in DocsOpen.Values.ToList()) if (ld.Parent != main) { (ld.Parent as LayoutDocumentPane)?.Children.Remove(ld); main.Children.Add(ld); }
        }
        if (_splitBtn != null) _splitBtn.IsChecked = on;
        SetStatus(on ? "分割檢視：左邊電路圖、右邊 PCB（可拖曳分頁到任一邊）" : "單一檢視");
        UpdateRightPanel();
    }

    void CloseWorkspaceDoc(DocItem d)
    {
        if (DocsOpen.TryGetValue(d, out var ld)) ld.Close();
        Ws.Close(d); RefreshProjects();
    }

    // ================= active view =================
    void OnActiveChanged()
    {
        if (Dock.ActiveContent is not (SchView or PcbView)) return;
        ActiveView = (FrameworkElement)Dock.ActiveContent;
        var doc = ActiveDoc;
        if (ActiveView is SchView sv) _props.ShowSchSel(sv.Doc, sv.Sel, sv.SheetIndex);
        ShowNav(doc?.Group, doc);
        Title = doc != null ? $"{doc.Name} — {(doc.Group?.Kind == GroupKind.Project ? doc.Group.Name + " — " : "")}{Product}" : Product;
        RefreshProjects();
        CheckPropsHold();
        UpdateRightPanel();
    }

    /// <summary>The PCB shows its information in a card on the board itself, so a PCB on its own gets the whole
    /// width: the right-hand Properties panel steps aside and comes back with a schematic.</summary>
    void UpdateRightPanel()
    {
        bool pcbOnly = ActiveView is PcbView && !Split;
        if (pcbOnly) ReleaseProps();
        // an auto-hidden panel takes no room: leave it alone (hiding it while its flyout is open is what AvalonDock trips over)
        if (pcbOnly && PropAnch.IsVisible && !PropAnch.IsAutoHidden) { _propsAutoHidden = true; PropAnch.Hide(); }
        else if (!pcbOnly && _propsAutoHidden) { _propsAutoHidden = false; PropAnch.Show(); }
    }

    /// <summary>Properties held open by a double-click while the user keeps the panel on auto-hide: the schematic view
    /// it was opened in. An auto-hide flyout closes as soon as the canvas takes the focus back, so the panel is docked
    /// while parts stay selected there (a click on another part shows that one) and goes back to auto-hide when the
    /// selection is cleared.</summary>
    SchView _propsHeld;

    void HoldProps(SchView v)
    {
        if (v.Sel is not { IsNet: false, Ref: not null } || ActiveView != v) return;
        if (_propsAutoHidden || !PropAnch.IsVisible) { _propsAutoHidden = false; PropAnch.Show(); }
        if (PropAnch.IsAutoHidden)
        {
            PropAnch.ToggleAutoHide();
            _propsHeld = v;
            // the canvas just got narrower: bring the part back to the middle once the new width is laid out
            Dispatcher.BeginInvoke(() => { if (v.SelBBox() is { } b) v.ZoomTo(b); }, System.Windows.Threading.DispatcherPriority.Background);
        }
        else if (_propsHeld != null) _propsHeld = v;
        PropAnch.IsSelected = true;
        _props.ShowSchSel(v.Doc, v.Sel, v.SheetIndex);
    }

    /// <summary>No part selected any more in the view Properties was held for (cleared, a net instead, another
    /// schematic in front, the view closed): back to auto-hide.</summary>
    void CheckPropsHold()
    {
        if (_propsHeld is not { } v) return;
        bool still = DocsOpen.ContainsKey(v.Doc) && v.Sel is { IsNet: false } && !(ActiveView is SchView av && av != v);
        if (!still) ReleaseProps();
    }

    void ReleaseProps()
    {
        if (_propsHeld == null) return;
        _propsHeld = null;
        if (PropAnch.IsVisible && !PropAnch.IsAutoHidden && PropAnch.Parent is LayoutAnchorablePane) PropAnch.ToggleAutoHide();
    }

    public DocItem ActiveDoc => (ActiveView as SchView)?.Doc ?? (ActiveView as PcbView)?.Doc;

    PcbView VisiblePcb() => DocsOpen.Where(kv => kv.Key.Kind == DocKind.Pcb && kv.Value.IsSelected).Select(kv => kv.Value.Content as PcbView).FirstOrDefault();

    void RefreshProjects()
    {
        HashSet<DocItem> netDocs = null;
        if (_groupNet != null && _groupNetGroup != null)
            netDocs = new HashSet<DocItem>(_groupNet.Locals.Select(l => _groupNetGroup.CompiledDoc(l.DocIndex)).Where(d => d != null));
        _projects.Rebuild(Ws, ActiveDoc, netDocs);
        foreach (var (d, ld) in DocsOpen)
            if (ld.Content is SchView sv) sv.TabBorder = netDocs != null && netDocs.Contains(d) && d != ActiveDoc ? Ui.Jump : sv.TabBrush;
    }

    void WirePanels()
    {
        _projects.OpenDoc += d => ActivateDoc(d);
        _projects.DocClicked += d => { if (DocsOpen.ContainsKey(d) && ActiveDoc != d) ActivateDoc(d); };
        _projects.CloseDoc += CloseWorkspaceDoc;
        _projects.CloseGroup += g =>
        {
            foreach (var d in g.Docs.ToList()) if (DocsOpen.TryGetValue(d, out var ld)) ld.Close();
            Ws.Close(g); RefreshProjects();
        };
        _projects.SplitGroup += g => { foreach (var d in g.Docs.Where(d => d.Loaded)) OpenDocView(d, false); SetSplit(true, g); };
        _projects.MoveDocs += MoveDocs;
        _projects.NewGroupFor += docs =>
        {
            var name = Prompt("新群組名稱", "群組 " + (Ws.Groups.Count(x => x.Kind == GroupKind.User) + 1));
            if (string.IsNullOrWhiteSpace(name)) return;
            MoveDocs(docs, Ws.NewUserGroup(name));
        };
        _projects.RenameGroup += g => RenameGroup(g, Prompt("重新命名群組", g.Name));
        _projects.SetScope += (g, mode) =>
        {
            g.ModeOverride = mode; g.Compile();
            Log($"專案「{g.Name}」改用網路範圍 {CompiledProject.ScopeLabel[g.Compiled?.Eff ?? "auto"]}：{g.Compiled?.PNets.Count} 個網路，{g.Compiled?.Cross} 個跨圖紙");
            foreach (var v in DocsOpen.Values.Select(l => l.Content).OfType<SchView>().Where(v => v.Doc.Group == g)) v.Select(null, false, false);
            _groupNet = null; OnActiveChanged();
        };
        _projects.CompileInfo += ShowCompileInfo;

        _props.GoRef += r => { if (ActiveView is SchView v) { v.Select(SchSel.Comp(r), true, true); } };
        _props.GoNet += n => { if (ActiveView is SchView v) { v.Select(SchSel.NetSel(n, true), true, true); } };
        _props.GoDocRef += (d, r) =>
        {
            ActivateDoc(d);
            if (ViewOf(d) is SchView sv) { if (r != null) sv.Select(SchSel.Comp(r), true, true); else if (_groupNet != null) sv.ApplyGroupNet(_groupNet, true); }
        };
        _props.GoPage += (od, s) => GoToNetPage(od, s, (ActiveView as SchView)?.Sel?.PNet);
        _props.ToggleMask += () => (ActiveView as SchView)?.ToggleMask();
        _props.ZoomSel += () => { if (ActiveView is SchView v && v.SelBBox() is { } b) v.ZoomTo(b); };
    }

    void ShowNav(ProjectGroup g, DocItem d) { _navFor = (g, d); _navStale = true; SyncNav(); }

    void SyncNav()
    {
        if (NavAnch.IsHidden || !NavAnch.IsVisible) return;
        if (_nav == null)
        {
            _nav = new NavigatorPanel();
            _nav.SetValue(System.Windows.Documents.TextElement.FontSizeProperty, Cfg.PanelFontSize);
            _nav.CompPicked += c =>
            {
                if (ActiveView is SchView sv && sv.Doc.Sch.PartMap.ContainsKey(c.Ref)) sv.Select(SchSel.Comp(c.Ref), true, true);
                else if (ActiveView is PcbView pv && pv.SelectByDes(c.Ref, true)) OnPcbSelection(pv, pv.St.Sel);
                else PickComp(c.Doc, c.Ref);
            };
            _nav.NetPicked += PickNet;
            _nav.Toast += SetStatus;
            NavAnch.Content = _nav;
        }
        if (_navStale) { _navStale = false; _nav.Show(_navFor.G, _navFor.D); }
    }

    /// <summary>Projects: 右鍵 → 重新命名 (or F2) on a group made by the user. The name is kept with the session.</summary>
    public void RenameGroup(ProjectGroup g, string name)
    {
        name = name?.Trim();
        if (string.IsNullOrEmpty(name) || name == g.Name) return;
        if (Ws.Groups.Any(x => x != g && x.Kind == GroupKind.User && x.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) { SetStatus($"已經有名為「{name}」的群組"); return; }
        var old = g.Name; g.Name = name;
        RefreshProjects(); ShowNav(ActiveDoc?.Group, ActiveDoc);
        SetStatus($"群組「{old}」已重新命名為「{name}」"); Log($"群組「{old}」重新命名為「{name}」");
    }

    /// <summary>Automation: put open files into a new user group, as 右鍵 → 加入群組 → 新群組 does.</summary>
    public void GroupDocs(IEnumerable<string> names, string group) =>
        MoveDocs(Ws.AllDocs.Where(d => names.Contains(d.Name, StringComparer.OrdinalIgnoreCase)).ToList(),
                 Ws.Groups.FirstOrDefault(g => g.Name == group) ?? Ws.NewUserGroup(group));

    void MoveDocs(List<DocItem> docs, ProjectGroup g)
    {
        var moved = new List<string>();
        foreach (var d in docs)
        {
            if (d.Group == g || !Ws.CanMove(d) || d.Missing) continue;
            Ws.MoveTo(d, g); moved.Add(d.Name);
        }
        int skipped = docs.Count(d => !Ws.CanMove(d));
        RefreshProjects(); OnActiveChanged();
        if (moved.Count > 0) Log($"{string.Join("、", moved)} 已{(g.Kind == GroupKind.Free ? "移出群組（回到 Free Documents）" : $"加入「{g.Name}」")}");
        SetStatus(moved.Count > 0 ? $"已移動 {moved.Count} 個檔案到「{g.Name}」" + (skipped > 0 ? $"，{skipped} 個專案內的檔案隨專案開啟，不能移動" : "")
            : skipped > 0 ? "專案內的檔案隨專案開啟，不能移到其他群組" : "沒有檔案需要移動");
    }

    // ================= cross probe =================
    IEnumerable<DocItem> PcbTargets(DocItem from)
    {
        var g = from.Group;
        var list = g != null && g.Kind != GroupKind.Free ? g.PcbDocs.ToList() : new List<DocItem>();
        if (list.Count == 0) list = Ws.AllDocs.Where(d => d.Kind == DocKind.Pcb && d.Pcb != null && (d.Group.Kind == GroupKind.Free || d.Group == g)).ToList();
        // visible / open first
        return list.OrderBy(d => IsVisibleDoc(d) ? 0 : DocsOpen.ContainsKey(d) ? 1 : 2);
    }

    /// <summary>Bring a cross-probe target into view only when the user wants that ("Cross Probe 跳轉"). With it off
    /// nothing moves: no tab, sheet or zoom changes, in split view either — only the selection / highlight is updated.</summary>
    void RevealTarget(DocItem t)
    {
        if (!Cfg.CrossProbeJump || IsVisibleDoc(t) || !DocsOpen.TryGetValue(t, out var ld)) return;
        if (!Split) { ActivateDoc(t); return; }
        // another pane (split view): show it there without taking the focus away
        if (ActiveDoc == null || !DocsOpen.TryGetValue(ActiveDoc, out var al) || al.Parent != ld.Parent) ld.IsSelected = true;
    }

    [DllImport("user32.dll")] static extern uint GetDoubleClickTime();

    /// <summary>A schematic click's jump to the PCB tab (single view) waiting out the double-click time.</summary>
    System.Windows.Threading.DispatcherTimer _revealTimer;
    (SchView View, SchSel Sel, DocItem Target)? _revealPending;

    /// <summary>Cross Probe from a schematic selection. A plain click switches to the PCB tab only once the Windows
    /// double-click time has passed: a second click makes it a double-click, which stays on the schematic and brings up
    /// Properties (the first click of it must not have left the schematic already). Split view shows the PCB in the
    /// other pane without moving the focus, so nothing waits there.</summary>
    void RevealFromSch(SchView v, DocItem t)
    {
        CancelReveal();
        if (v.Via == SchView.SelVia.DoubleClick) return;
        long wait = v.Via != SchView.SelVia.Click || Split || !Cfg.CrossProbeJump || IsVisibleDoc(t) ? 0
            : GetDoubleClickTime() - (Environment.TickCount64 - v.DownTick) + 20;
        if (wait <= 0) { RevealTarget(t); return; }
        _revealPending = (v, v.Sel, t);
        if (_revealTimer == null)
        {
            _revealTimer = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Input, Dispatcher);
            _revealTimer.Tick += (_, _) =>
            {
                var p = _revealPending; CancelReveal();
                // still the same part, shown in the same view
                if (p is { } r && ActiveView == r.View && r.View.Sel == r.Sel && DocsOpen.ContainsKey(r.Target)) RevealTarget(r.Target);
            };
        }
        _revealTimer.Interval = TimeSpan.FromMilliseconds(Math.Min(wait, 2000));
        _revealTimer.Start();
    }

    void CancelReveal() { _revealTimer?.Stop(); _revealPending = null; }

    /// <summary>The PCB view whose parts a schematic click lit up (Settings: 從電路圖點選元件時，同時點亮 PCB 上的元件).</summary>
    PcbView _probeLit;

    /// <summary>Settings: Cross Probe from the schematic also lights the part(s) up on the PCB, the rest dimmed. One way
    /// only: a click on the PCB does not mask the schematic.</summary>
    void LightParts(PcbView pv, DocItem t, IEnumerable<string> refs)
    {
        if (!Cfg.ProbeLightPart) return;
        var idx = refs.Select(r => t.Pcb.FindComp(r)).Where(i => i >= 0).ToList();
        if (idx.Count == 0) return;
        pv.HighlightComp(idx[0]); foreach (var i in idx.Skip(1)) pv.HighlightComp(i, true);
        _probeLit = pv;
    }

    void OnSchSelection(SchView v, SchSel sel)
    {
        if (ActiveView == v) _props.ShowSchSel(v.Doc, sel, v.SheetIndex);
        CheckPropsHold();
        CancelReveal();   // a jump still waiting is for the selection this one replaces
        var g = v.Doc.Group;
        // project-net highlight shared by every sheet of the group
        var pnet = sel?.IsNet == true ? sel.PNet : null;
        if (sel?.IsNet == true && sel.Mask || sel == null)
        {
            _groupNet = sel == null ? null : pnet; _groupNetGroup = g;
            foreach (var other in DocsOpen.Values.Select(l => l.Content).OfType<SchView>().Where(o => o != v && o.Doc.Group == g && g?.Kind != GroupKind.Free))
                other.ApplyGroupNet(_groupNet, true);
            RefreshProjects();
        }
        if (sel == null)
        {
            foreach (var pv in PcbTargets(v.Doc).Select(ViewOf).OfType<PcbView>()) if (pv.St.HlNets.Count > 0 || pv == _probeLit) pv.ClearHighlight();
            _probeLit = null; StatusProbe.Text = ""; return;
        }

        if (sel.Refs != null)
        {
            foreach (var t in PcbTargets(v.Doc))
            {
                if (!sel.Refs.Any(r => t.Pcb.FindComp(r) >= 0)) continue;
                var pv = (PcbView)OpenDocView(t, false);
                RevealFromSch(v, t);
                int n = pv.SelectMany(sel.Refs); if (Cfg.CrossProbeJump) pv.ZoomToSelection();
                LightParts(pv, t, sel.Refs);
                Probe($"Cross Probe：電路圖 {sel.Refs.Count} 個零件 → {t.Name} 選取 {n} 個");
                return;
            }
            Probe(PcbTargets(v.Doc).Any() ? "PCB 找不到這些位號" : "這個群組沒有開啟 PCB");
            return;
        }
        if (!sel.IsNet)
        {
            foreach (var t in PcbTargets(v.Doc))
            {
                if (t.Pcb.FindComp(sel.Ref) < 0) continue;
                var pv = (PcbView)OpenDocView(t, false);
                RevealFromSch(v, t);
                pv.SelectByDes(sel.Ref, Cfg.CrossProbeJump);
                LightParts(pv, t, new[] { sel.Ref });
                Probe($"Cross Probe：電路圖 {sel.Ref} → {t.Name}");
                return;
            }
            Probe(PcbTargets(v.Doc).Any() ? $"PCB 找不到位號 {sel.Ref}" : "這個群組沒有開啟 PCB");
            return;
        }
        if (!Cfg.AltNetSchToPcb) { StatusProbe.Text = ""; return; }
        // net → PCB: pins first, names as fallback; highlight only (view stays where it is)
        var doc = v.Doc.Sch;
        IEnumerable<(string, string)> pins = pnet != null ? pnet.Members.Select(m => (m.Ref, m.Pin))
            : doc.NetMap.TryGetValue(sel.Net, out var dn) ? dn.Members.Select(m => (m.Ref, m.Pin)) : Enumerable.Empty<(string, string)>();
        var names = new List<string> { sel.Net }; if (pnet != null) { names.Add(pnet.Name); names.AddRange(pnet.Locals.Select(l => l.Name)); }
        names = names.Select(PlainText).Distinct().ToList();
        foreach (var t in PcbTargets(v.Doc))
        {
            int idx = NetMatcher.FindPcbNet(t.Pcb, pins.ToList(), names);
            if (idx < 0) continue;
            var pv = (PcbView)OpenDocView(t, false);
            if (!IsVisibleDoc(t) && Split && Cfg.CrossProbeJump) DocsOpen[t].IsSelected = true;
            pv.HighlightNet(idx);
            Probe($"Cross Probe：電路圖 NET {PlainText(sel.Net)} → PCB 點亮 {t.Pcb.NetName(idx)}");
            return;
        }
        Probe(PcbTargets(v.Doc).Any() ? $"PCB 找不到 NET {PlainText(sel.Net)}" : "");
    }

    void OnPcbSelection(PcbView v, PcbSelection sel)
    {
        if (v.Is3D) return;   // 3D: the info card shows the part; no jump to the schematic
        // a component, or a pad of one (on small parts most clicks land on a pad)
        int ci = sel is { IsComp: true } ? sel.Index : sel is { Items: null } && v.Scene.Objs[sel.Index] is { Kind: PcbObjKind.Pad } po && po.Comp >= 0 && po.Comp < v.Doc.Pcb.Comps.Count ? po.Comp : -1;
        if (ci < 0) return;
        var des = v.Doc.Pcb.Comps[ci].Des; if (des == "") return;
        var g = v.Doc.Group;
        var cands = (g != null && g.Kind != GroupKind.Free ? g.SchDocs : Enumerable.Empty<DocItem>()).Where(d => d.Sch.PartMap.ContainsKey(des)).ToList();
        if (cands.Count == 0) cands = Ws.AllDocs.Where(d => d.Sch != null && (d.Group.Kind == GroupKind.Free || d.Group == g) && d.Sch.PartMap.ContainsKey(des)).ToList();
        var target = cands.OrderBy(d => IsVisibleDoc(d) ? 0 : DocsOpen.ContainsKey(d) ? 1 : 2).FirstOrDefault();
        if (target == null) { Probe($"電路圖找不到位號 {des}"); return; }
        var sv = (SchView)OpenDocView(target, false);
        RevealTarget(target);
        // jump off: the schematic keeps its sheet and zoom (the part is highlighted if it is on the shown sheet)
        sv.Select(SchSel.Comp(des), Cfg.CrossProbeJump, false, Cfg.CrossProbeJump);
        CheckPropsHold();
        Probe($"Cross Probe：PCB {des} → {target.Name}");
    }

    void OnPcbHighlight(PcbView v, bool query)
    {
        // PCB NET → schematic: Alt+click, when enabled in Settings (experimental); only into schematic views already open
        if (!query || !Cfg.AltNetPcbToSch || v.St.HlNets.Count != 1) return;
        var hit = NetMatcher.FindSchNet(v.Doc.Group, v.Doc.Pcb, v.St.HlNets.First(), (ActiveView as SchView)?.Doc ?? DocsOpen.Keys.FirstOrDefault(d => d.Kind == DocKind.Sch && IsVisibleDoc(d) && d.Group == v.Doc.Group));
        if (hit?.Doc == null || ViewOf(hit.Doc) is not SchView sv) { Probe($"電路圖找不到 PCB NET {v.Doc.Pcb.NetName(v.St.HlNets.First())}"); return; }
        if (hit.PNet != null)
        {
            _groupNet = hit.PNet; _groupNetGroup = v.Doc.Group;
            foreach (var o in DocsOpen.Values.Select(l => l.Content).OfType<SchView>().Where(o => o.Doc.Group == v.Doc.Group)) o.ApplyGroupNet(hit.PNet, true, Cfg.CrossProbeJump);
        }
        else sv.Select(new SchSel { IsNet = true, Net = hit.LocalName, Mask = true }, false, false, Cfg.CrossProbeJump);
        if (Split && Cfg.CrossProbeJump && !IsVisibleDoc(hit.Doc)) DocsOpen[hit.Doc].IsSelected = true;
        CheckPropsHold();
        RefreshProjects();
        Probe($"Cross Probe：PCB NET {v.Doc.Pcb.NetName(v.St.HlNets.First())} → 電路圖 {PlainText(hit.PNet?.Name ?? hit.LocalName)}");
    }

    // ================= navigator / search picks =================
    void PickComp(DocItem doc, string reference)
    {
        if (doc == null) return;
        ActivateDoc(doc);
        if (ViewOf(doc) is SchView sv) sv.Select(SchSel.Comp(reference), true, true);
        else if (ViewOf(doc) is PcbView pv && pv.SelectByDes(reference, true)) OnPcbSelection(pv, pv.St.Sel);
    }

    void PickNet(NavNet n)
    {
        if (n.PcbNet >= 0 && n.Doc != null) { ActivateDoc(n.Doc); if (ViewOf(n.Doc) is PcbView pv) { pv.HighlightNet(n.PcbNet); pv.ZoomToHighlight(); } return; }
        DocItem doc; string local;
        if (n.PNet != null)
        {
            var g = ActiveDoc?.Group;
            var loc = n.PNet.Locals.FirstOrDefault(l => g?.CompiledDoc(l.DocIndex) == ActiveDoc) ?? n.PNet.Locals.First();
            doc = g?.CompiledDoc(loc.DocIndex); local = loc.Name;
        }
        else { doc = n.Doc; local = n.Local; }
        if (doc == null) return;
        if (ActiveDoc != doc) ActivateDoc(doc);
        if (ViewOf(doc) is SchView sv) sv.Select(new SchSel { IsNet = true, Net = local, Mask = true, PNet = n.PNet }, true, true);
    }

    // ================= menus / toolbar =================
    static MenuItem MI(string header, Action a, string gesture = null, bool check = false, bool isChecked = false)
    {
        var m = new MenuItem { Header = header, InputGestureText = gesture ?? "", IsCheckable = check, IsChecked = isChecked };
        m.Click += (_, _) => a();
        return m;
    }

    void BuildMenu()
    {
        var file = new MenuItem { Header = "檔案(_F)" };
        file.Items.Add(MI("開啟專案 / 檔案…", OpenFilesDialog, "Ctrl+O"));
        file.Items.Add(MI("開啟資料夾…", OpenFolderDialog, "Ctrl+Shift+O"));
        _recentMenu = new MenuItem { Header = "最近開啟" }; file.Items.Add(_recentMenu); RebuildRecent();
        file.Items.Add(MI("重新載入全部檔案", ReloadAll));
        file.Items.Add(new Separator());
        file.Items.Add(MI("匯出電路圖 PDF…", ExportPdf, "Ctrl+P"));
        file.Items.Add(new Separator());
        file.Items.Add(MI("設定…", ShowSettings, "Ctrl+,"));
        file.Items.Add(new Separator());
        file.Items.Add(MI("關閉文件分頁", () => { if (ActiveDoc is { } d && DocsOpen.TryGetValue(d, out var ld)) ld.Close(); }, "Ctrl+W"));
        file.Items.Add(MI("關閉全部", CloseAll));
        file.Items.Add(new Separator());
        file.Items.Add(MI("結束", Close, "Alt+F4"));

        var view = new MenuItem { Header = "檢視(_V)" };
        view.Items.Add(MI("適合視窗", FitActive, "F"));
        view.Items.Add(MI("分割檢視（左電路圖／右 PCB）", () => ToggleSplit(), "F12"));
        view.Items.Add(new Separator());
        foreach (var (t, a) in new (string, LayoutAnchorable)[] { ("Projects", ProjectsAnch), ("Navigator", NavAnch), ("Properties", PropAnch), ("Messages", MsgAnch) })
            view.Items.Add(MI("面板：" + t, () => { if (a == PropAnch) _propsAutoHidden = false; a.Show(); a.IsActive = true; }));
        view.Items.Add(new Separator());
        var theme = new MenuItem { Header = "電路圖配色" };
        foreach (var (n, th) in new[] { ("Altium（檔案原色）", SchTheme.Altium), ("黑色", SchTheme.Dark), ("黑白", SchTheme.Mono) })
            theme.Items.Add(MI(n, () => { SchView.Theme = th; InvalidateSch(); }));
        view.Items.Add(theme);
        var font = new MenuItem { Header = "電路圖字體大小" };
        foreach (var pct in new[] { 80, 90, 100, 115, 130, 150, 200 }) font.Items.Add(MI(pct + "%", () => { SchView.FontScale = Cfg.FontScale = pct / 100.0; InvalidateSch(); }));
        view.Items.Add(font);
        view.Items.Add(MI("單位 mm ⇄ mil", ToggleUnits, "Q"));

        var tools = new MenuItem { Header = "工具(_T)" };
        _jumpMenu = MI("Cross Probe 時切換到對應文件", () => SetJump(!Cfg.CrossProbeJump), null, true, Cfg.CrossProbeJump);
        tools.Items.Add(_jumpMenu);
        _autoSplitMenu = MI("開啟專案時自動分割檢視", () => Cfg.AutoSplit = !Cfg.AutoSplit, null, true, Cfg.AutoSplit);
        tools.Items.Add(_autoSplitMenu);
        tools.Items.Add(MI("清除所有點亮", ClearAllHighlights, "Shift+C"));
        tools.Items.Add(MI("搜尋元件 / 網路…", OpenSearchWindow, "Ctrl+F"));
        tools.Items.Add(new Separator());
        tools.Items.Add(MI("編譯資訊（目前專案）", () => { if (ActiveDoc?.Group is { } g) ShowCompileInfo(g); }));

        var help = new MenuItem { Header = "說明(_H)" };
        help.Items.Add(MI("使用說明", ShowHelp, "F1"));
        help.Items.Add(MI("快捷鍵", ShowShortcuts));
        help.Items.Add(new Separator());
        help.Items.Add(MI("授權條款（GPL-3.0）", () => ShowText("NOTICE.txt", "授權條款")));
        help.Items.Add(MI("GPL-3.0 全文", () => ShowText("LICENSE.txt", "GPL-3.0 全文")));
        help.Items.Add(MI("授權與第三方元件", () => ShowText("THIRD-PARTY-NOTICES.txt", "授權與第三方元件")));
        help.Items.Add(new Separator());
        // the GPL's "Appropriate Legal Notices": copyright, no warranty, may be redistributed under the GPL, where the licence is
        help.Items.Add(MI("關於 " + Product, () => MessageBox.Show(this, $"{Product}  {typeof(MainWindow).Assembly.GetName().Version?.ToString(3)}\n\nAltium Designer .PrjPcb / .SchDoc / .PcbDoc 檢視器（另支援 KiCad、Eagle 電路圖）。\n電路圖 ⇄ PCB 雙向 Cross Probe、Alt+點選網路同步點亮 PCB。\n\nCopyright © 2026 Johnny.ch.Tsai\n本程式是自由軟體，依 GNU 通用公共授權條款第 3 版（GPL-3.0）發佈，可以自由使用、修改與散布（散布時同樣以 GPL-3.0 提供並附上原始碼），另允許與 AvalonDock（Ms-PL）結合散布。本程式不提供任何擔保。\n授權條款：說明 → 授權條款（GPL-3.0）、GPL-3.0 全文；原始碼附在每個版本的發行 zip 裡，也公開在 https://github.com/vip3310495-Johnny/HeraldPcbReader 。\n\n使用的第三方元件（.NET、AvalonDock、Helix Toolkit、SkiaSharp、earcut 等）依各自的授權提供：說明 → 授權與第三方元件。\nAltium、Altium Designer、KiCad、EAGLE 等名稱為其各自所有者的商標；本程式為獨立開發的檢視工具，與這些公司及專案無關，也未經其認可。", "關於", MessageBoxButton.OK, MessageBoxImage.Information)));
        foreach (var m in new[] { file, view, tools, help }) MainMenu.Items.Add(m);
    }

    void SetJump(bool on)
    {
        Cfg.CrossProbeJump = on;
        if (_jumpBtn != null) _jumpBtn.IsChecked = on;
        if (_jumpMenu != null) _jumpMenu.IsChecked = on;
    }

    void RebuildRecent()
    {
        if (_recentMenu == null) return;
        _recentMenu.Items.Clear();
        foreach (var r in Cfg.Recent) { var p = r; _recentMenu.Items.Add(MI(p, () => _ = Open(new[] { p }, false))); }
        if (Cfg.Recent.Count > 0) { _recentMenu.Items.Add(new Separator()); _recentMenu.Items.Add(MI("清除記錄", () => { Cfg.Recent.Clear(); RebuildRecent(); })); }
        _recentMenu.IsEnabled = Cfg.Recent.Count > 0;
    }

    TextBox _searchBox; Popup _searchPop; SearchResultsView _searchResults; ComboBox _searchKind, _searchScope;

    void BuildToolbar()
    {
        UIElement Btn(string icon, string text, string tip, Action a, bool toggle = false, bool isChecked = false)
        {
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            var ic = Ui.Icon(icon); ic.Margin = new Thickness(0, 0, 6, 0); content.Children.Add(ic);
            content.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
            ButtonBase b = toggle ? new ToggleButton { IsChecked = isChecked } : new Button();
            b.Content = content; b.ToolTip = tip; b.Click += (_, _) => a();
            return b;
        }
        UIElement Sep() => new Border { Width = 1, Margin = new Thickness(6, 3, 6, 3), Background = Ui.Line };
        _splitBtn = (ToggleButton)Btn(Ui.Icons.Split, "分割檢視", "左邊電路圖、右邊 PCB（F12）", () => ToggleSplit(), true);
        ToolLeft.Children.Add(_splitBtn);
        _jumpBtn = (ToggleButton)Btn(Ui.Icons.CrossProbe, "Cross Probe 跳轉", "點選元件時切換到另一邊的文件（分割檢視時兩邊都看得到，不需要切換）", () => SetJump(_jumpBtn.IsChecked == true), true, Cfg.CrossProbeJump);
        ToolLeft.Children.Add(_jumpBtn);
        ToolLeft.Children.Add(Sep());
        ToolLeft.Children.Add(Btn(Ui.Icons.Fit, "適合視窗", "適合視窗（F）", FitActive));
        ToolLeft.Children.Add(Btn(Ui.Icons.Clear, "清除點亮", "清除所有點亮與遮罩（Shift+C）", ClearAllHighlights));
        ToolLeft.Children.Add(Btn(Ui.Icons.Pdf, "匯出 PDF", "把電路圖匯出成向量 PDF（Ctrl+P）", ExportPdf));

        // search: category + scope + text; results grouped by file in a drop-down
        _searchKind = new ComboBox { Width = 76, Margin = new Thickness(0, 0, 4, 0), ToolTip = "搜尋分類" };
        foreach (var (k, n) in new[] { ("all", "全部"), ("comp", "零件"), ("net", "NET") }) _searchKind.Items.Add(new ComboBoxItem { Content = n, Tag = k });
        _searchKind.SelectedIndex = 0;
        _searchScope = new ComboBox { Width = 108, Margin = new Thickness(0, 0, 4, 0), ToolTip = "搜尋範圍" };
        foreach (var (k, n) in new[] { ("page", "這個頁面"), ("group", "這個群組"), ("all", "全部檔案") }) _searchScope.Items.Add(new ComboBoxItem { Content = n, Tag = k });
        _searchScope.SelectedIndex = 1;
        _searchBox = new TextBox { Width = 220, ToolTip = "搜尋位號、值、封裝或網路（Ctrl+F 開啟搜尋視窗）", Margin = new Thickness(0, 0, 4, 0) };
        var hint = new TextBlock { Text = "搜尋…", Foreground = Ui.Dim, IsHitTestVisible = false, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        var host = new Grid(); host.Children.Add(_searchBox); host.Children.Add(hint);
        _searchResults = new SearchResultsView { MaxHeight = 460, Width = 440 };
        var popInfo = Ui.T("", Ui.Dim, 11.5, false, new Thickness(8, 5, 8, 5));
        var popRoot = new DockPanel { Background = Ui.Bg1 }; DockPanel.SetDock(popInfo, System.Windows.Controls.Dock.Top); popRoot.Children.Add(popInfo); popRoot.Children.Add(_searchResults);
        _searchPop = new Popup { PlacementTarget = _searchBox, Placement = PlacementMode.Bottom, StaysOpen = false, Child = new Border { BorderBrush = Ui.Line, BorderThickness = new Thickness(1), Child = popRoot } };
        void Run()
        {
            hint.Visibility = _searchBox.Text == "" ? Visibility.Visible : Visibility.Collapsed;
            var (hits, docs, label) = Search(_searchBox.Text, SearchKind, SearchScope);
            popInfo.Text = $"範圍：{label}　零件 {hits.Count(h => !h.IsNet)} · 網路 {hits.Count(h => h.IsNet)}";
            _searchResults.Show(docs, hits, "沒有可搜尋的檔案");
            _searchPop.IsOpen = _searchBox.Text.Trim() != "";
        }
        _searchBox.TextChanged += (_, _) => Run();
        _searchKind.SelectionChanged += (_, _) => { if (_searchBox.Text != "") Run(); };
        _searchScope.SelectionChanged += (_, _) => { if (_searchBox.Text != "") Run(); };
        _searchResults.Picked += PickHit;
        _searchBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Down && _searchResults.Items.Count > 0) { (_searchResults.ItemContainerGenerator.ContainerFromIndex(0) as TreeViewItem)?.Focus(); e.Handled = true; }
            else if (e.Key == Key.Enter && _searchResults.First is { } f) { PickHit(f); e.Handled = true; }
            else if (e.Key == Key.Escape) { _searchPop.IsOpen = false; (ActiveView as UIElement)?.Focus(); }
        };
        ToolRight.Children.Add(_searchKind);
        ToolRight.Children.Add(_searchScope);
        ToolRight.Children.Add(host);
    }

    string SearchKind => (string)((ComboBoxItem)_searchKind.SelectedItem).Tag;
    string SearchScope => (string)((ComboBoxItem)_searchScope.SelectedItem).Tag;

    SearchWindow _searchWin;
    public void OpenSearchWindowWith(string text) { OpenSearchWindow(); _searchWin?.Activate(text); }
    /// <summary>Ctrl+F: the floating search window (same search as the toolbar box).</summary>
    void OpenSearchWindow()
    {
        if (_searchWin == null)
        {
            _searchWin = new SearchWindow(this, Search, PickHit, SearchKind, SearchScope);
            _searchWin.Closed += (_, _) => _searchWin = null;
        }
        _searchWin.Activate(null);
    }

    /// <summary>Search. kind: all / comp / net. scope: page (the sheet or PCB on screen), group (its project or
    /// group), all (every open file). Only files of the same kind as the one on screen: from a schematic only
    /// schematics are searched, from a PCB only PCBs. Returns the hits and every file searched (for the grouping).</summary>
    (List<SearchHit> hits, List<DocItem> docs, string label) Search(string text, string kind, string scope)
    {
        var q = text.Trim().ToLowerInvariant(); var hits = new List<SearchHit>();
        var act = ActiveDoc;
        var kindName = act == null ? "" : act.Kind == DocKind.Pcb ? "PCB" : "電路圖";
        string label = scope switch
        {
            "page" => act == null ? "目前頁面" : act.Kind == DocKind.Sch && ViewOf(act) is SchView sv0 && act.Sch.Sheets.Count > 1 ? $"{act.Name} · {act.Sch.Sheets[sv0.SheetIndex].Name}" : act.Name,
            "group" => (act?.Group is { Kind: not GroupKind.Free } g0 ? g0.Name : act?.Name ?? "目前群組") + (kindName != "" ? $" 的{kindName}" : ""),
            _ => "全部" + (kindName != "" ? kindName : "檔案"),
        };
        List<DocItem> docs = scope switch
        {
            "page" => act != null ? new() { act } : new(),
            "group" => act == null ? new() : act.Group is { Kind: not GroupKind.Free } g ? g.Docs.Where(d => d.Loaded).ToList() : new() { act },
            _ => Ws.AllDocs.Where(d => d.Loaded).ToList(),
        };
        if (act != null) docs = docs.Where(d => d.Kind == act.Kind).ToList();
        if (q == "") return (hits, docs, label);
        int Rank(string s) { s = s.ToLowerInvariant(); return s == q ? 0 : s.StartsWith(q) ? 1 : 2; }
        bool Has(params string[] f) => f.Any(x => !string.IsNullOrEmpty(x) && x.ToLowerInvariant().Contains(q));
        foreach (var d in docs)
        {
            int sheet = scope == "page" && d.Sch != null && ViewOf(d) is SchView sv ? sv.SheetIndex : -1;
            if (d.Sch != null)
            {
                if (kind != "net")
                    foreach (var p in d.Sch.Parts)
                        if ((sheet < 0 || p.Sheets.Contains(sheet)) && Has(p.Ref, p.Value, p.Footprint))
                            hits.Add(new SearchHit { Label = p.Ref, Sub = $"{p.Value}  {p.Footprint}", Doc = d, Ref = p.Ref, Rank = Rank(p.Ref) });
                if (kind != "comp")
                    foreach (var n in d.Sch.Nets)
                    {
                        var name = PlainText(n.Name);
                        if ((sheet < 0 || n.Sheets.Contains(sheet)) && Has(name))
                            hits.Add(new SearchHit { IsNet = true, Label = name, Sub = $"{n.Members.Count} 腳", Doc = d, Local = n.Name, Rank = Rank(name) });
                    }
            }
            else if (d.Pcb != null)
            {
                if (kind != "net")
                    foreach (var c in d.Pcb.Comps)
                        if (c.Des != "" && Has(c.Des, c.Comment, c.Pattern))
                            hits.Add(new SearchHit { Label = c.Des, Sub = $"{c.Comment}  {c.Pattern}", Doc = d, Ref = c.Des, Rank = Rank(c.Des) });
                if (kind != "comp")
                    for (int i = 0; i < d.Pcb.Nets.Count; i++)
                        if (Has(d.Pcb.Nets[i]))
                            hits.Add(new SearchHit { IsNet = true, Label = d.Pcb.Nets[i], Sub = "", Doc = d, PcbNet = i, Rank = Rank(d.Pcb.Nets[i]) });
            }
        }
        return (hits.OrderBy(h => h.Rank).ThenBy(h => h.Label, Natural).ToList(), docs, label);
    }

    void PickHit(SearchHit h)
    {
        if (!h.IsNet) { PickComp(h.Doc, h.Ref); return; }
        ActivateDoc(h.Doc);
        if (ViewOf(h.Doc) is SchView sv) sv.Select(new SchSel { IsNet = true, Net = h.Local, Mask = true }, true, true);
        else if (ViewOf(h.Doc) is PcbView pv && h.PcbNet >= 0) { pv.HighlightNet(h.PcbNet); pv.ZoomToHighlight(); }
    }

    void OnGlobalKey(object s, KeyEventArgs e)
    {
        var mod = Keyboard.Modifiers; var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.O && mod == ModifierKeys.Control) { OpenFilesDialog(); e.Handled = true; }
        else if (key == Key.O && mod == (ModifierKeys.Control | ModifierKeys.Shift)) { OpenFolderDialog(); e.Handled = true; }
        else if (key == Key.P && mod == ModifierKeys.Control) { ExportPdf(); e.Handled = true; }
        else if (key == Key.F && mod == ModifierKeys.Control) { OpenSearchWindow(); e.Handled = true; }
        else if (key == Key.OemComma && mod == ModifierKeys.Control) { ShowSettings(); e.Handled = true; }
        else if (key == Key.W && mod == ModifierKeys.Control) { if (ActiveDoc is { } d && DocsOpen.TryGetValue(d, out var ld)) ld.Close(); e.Handled = true; }
        else if (key == Key.F12) { ToggleSplit(); e.Handled = true; }
        else if (key == Key.F1 && mod == ModifierKeys.None) { ShowHelp(); e.Handled = true; }
        else if (key == Key.Q && mod == ModifierKeys.None && Keyboard.FocusedElement is not TextBox) { ToggleUnits(); e.Handled = true; }
        else if (key == Key.C && mod == ModifierKeys.Shift && Keyboard.FocusedElement is not TextBox && ActiveView is SchView) { ClearAllHighlights(); e.Handled = true; }
    }

    void ToggleSplit()
    {
        var g = ActiveDoc?.Group;
        if (!Split && g != null) foreach (var d in g.Docs.Where(d => d.Loaded)) OpenDocView(d, false);
        SetSplit(!Split, g);
    }

    void ToggleUnits() => SetUnits(!Units.Mm);

    /// <summary>Length unit for PCB coordinates, measurements and the info card (status bar switch, Settings, Q).</summary>
    public void SetUnits(bool mm)
    {
        Units.Mm = Cfg.UnitsMm = mm; UpdateUnitLabel();
        foreach (var pv in DocsOpen.Values.Select(l => l.Content).OfType<PcbView>()) { pv.Invalidate(); pv.RefreshCard(); }
        SetStatus("單位：" + Units.Name);
    }

    void FitActive() { if (ActiveView is SchView s) s.Fit(); else if (ActiveView is PcbView p) p.Fit(); }

    void InvalidateSch() { foreach (var v in DocsOpen.Values.Select(l => l.Content).OfType<SchView>()) v.Invalidate(); }

    void ClearAllHighlights()
    {
        _groupNet = null;
        foreach (var c in DocsOpen.Values.Select(l => l.Content))
        {
            if (c is SchView sv) sv.Select(null, false, false);
            else if (c is PcbView pv) { pv.ClearHighlight(); pv.SetSelection(null, false); }
        }
        StatusProbe.Text = ""; RefreshProjects(); OnActiveChanged();
    }

    void CloseAll()
    {
        foreach (var ld in DocsOpen.Values.ToList()) ld.Close();
        foreach (var g in Ws.AllGroups.ToList()) Ws.Close(g);
        _groupNet = null; RefreshProjects(); ShowNav(null, null); _props.ShowNothing();
    }

    async void ReloadAll()
    {
        var paths = Ws.SessionPaths(); CloseAll(); await Open(paths, false, true);
    }

    void OpenFilesDialog()
    {
        var dlg = new OpenFileDialog
        {
            Multiselect = true, Title = "開啟 Altium 專案或檔案",
            Filter = "Altium / KiCad / Eagle|*.PrjPcb;*.SchDoc;*.PcbDoc;*.kicad_sch;*.sch;*.lib|Altium 專案 (*.PrjPcb)|*.PrjPcb|電路圖|*.SchDoc;*.kicad_sch;*.sch|PCB (*.PcbDoc)|*.PcbDoc|所有檔案|*.*",
        };
        if (dlg.ShowDialog(this) == true) _ = Open(dlg.FileNames, dlg.FileNames.Length == 1);
    }

    void OpenFolderDialog()
    {
        var dlg = new OpenFolderDialog { Title = "選擇專案資料夾" };
        if (dlg.ShowDialog(this) == true) _ = Open(new[] { dlg.FolderName }, false);
    }

    void ShowCompileInfo(ProjectGroup g)
    {
        var c = g.Compiled;
        var msg = c == null ? $"「{g.Name}」沒有編譯結果（需要至少一張電路圖；自訂群組需要兩張以上）。"
            : $"專案「{g.Name}」\n電路圖：{g.SchDocs.Count()} 張　PCB：{g.PcbDocs.Count()} 塊\n網路：{c.PNets.Count} 個，跨圖紙 {c.Cross} 個\n網路範圍：{CompiledProject.ScopeLabel[c.Eff]}{(c.Set == "auto" ? "（Automatic 自動判定）" : "")}"
              + (g.MissingDocs.Any() ? "\n\n找不到的檔案：\n" + string.Join("\n", g.MissingDocs) : "");
        Log(msg.Replace("\n", "　"));
        MessageBox.Show(this, msg, "編譯資訊", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    /// <summary>說明 → 使用說明 (F1): the HTML guide in the default browser.</summary>
    void ShowHelp()
    {
        try { HelpDoc.Open(); SetStatus("已在瀏覽器開啟使用說明"); }
        catch (Exception e) { MessageBox.Show(this, "無法開啟使用說明：" + e.Message, "使用說明", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    /// <summary>說明 → 授權條款 (NOTICE.txt), GPL-3.0 全文 (LICENSE.txt), 授權與第三方元件 (THIRD-PARTY-NOTICES.txt).</summary>
    void ShowText(string file, string title)
    {
        try { HelpDoc.OpenText(file); }
        catch (Exception e) { MessageBox.Show(this, "無法開啟授權說明：" + e.Message, title, MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    void ShowShortcuts() => MessageBox.Show(this,
        "通用\n  Ctrl+O 開啟　Ctrl+Shift+O 開啟資料夾　Ctrl+P 匯出 PDF　Ctrl+F 搜尋　Ctrl+, 設定　Ctrl+W 關閉分頁　F12 分割檢視　Q 切換單位\n\n" +
        "電路圖\n  點選 選取零件/網路（自動 Cross Probe 到 PCB）\n  左鍵拖曳 框選零件（左→右：完全在框內；右→左：碰到就選）\n  Alt / Ctrl + 點選 只看同一網路（同專案其他圖紙與 PCB 一起點亮）\n  滾輪 縮放　右鍵/中鍵拖曳 平移　F 適合視窗　PgUp/PgDn 縮放　G 格線　Esc 取消\n\n" +
        "PCB 2D\n  點選 選取（元件會 Cross Probe 到電路圖）　Ctrl+點選 點亮 NET/元件（Shift 可加選）　Alt+點選 點亮 NET 並標到電路圖（設定中可關）\n  雙擊 點亮　滾輪 縮放　右鍵/中鍵拖曳 平移　F 全圖　M 量測　B 翻面　X/Y 鏡射　R 旋轉　N NET 名稱\n  Shift+S 單層模式　+ / - 切換目前層　2 / 3 切換 2D/3D　Shift+C 清除點亮　Esc 取消\n\n" +
        "PCB 3D\n  左鍵 依右側工具旋轉／平移／縮放／繞軸旋轉　右鍵 平移　滾輪 縮放　點元件 顯示資訊\n  H 隱藏所選元件　Home 回預設　0 頂視　I 等角　S 側視　O/P/Z 旋轉/平移/縮放　A 座標軸",
        "快捷鍵", MessageBoxButton.OK, MessageBoxImage.Information);

    string Prompt(string title, string def)
    {
        var w = new Window { Title = title, Width = 360, Height = 140, WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = this, Background = Ui.Bg1, ResizeMode = ResizeMode.NoResize };
        var tb = new TextBox { Text = def, Margin = new Thickness(12, 12, 12, 6) }; string result = null;
        var ok = Ui.Btn("確定", () => { result = tb.Text; w.Close(); });
        var sp = new StackPanel(); sp.Children.Add(tb); var r = Ui.Row(ok, Ui.Btn("取消", w.Close)); r.Margin = new Thickness(12, 0, 0, 0); sp.Children.Add(r);
        w.Content = sp; tb.SelectAll(); tb.Focus(); tb.KeyDown += (_, e) => { if (e.Key == Key.Enter) { result = tb.Text; w.Close(); } };
        w.ShowDialog(); return result;
    }

    // ================= settings =================
    public void ApplyDisplaySettings()
    {
        Ui.Base = Cfg.PanelFontSize;
        _projects.SetValue(System.Windows.Documents.TextElement.FontSizeProperty, Cfg.PanelFontSize);
        _nav?.SetValue(System.Windows.Documents.TextElement.FontSizeProperty, Cfg.PanelFontSize);
        _msgs.FontSize = Cfg.PanelFontSize;
        PropertiesPanel.TitleSize = Cfg.PropTitleSize; PropertiesPanel.BodySize = Cfg.PropBodySize;
        SchView.ShowParams = Cfg.SchShowParams;
        SchView.FontScale = Math.Clamp(Cfg.FontScale, 0.3, 3);
        PcbRenderer.MeasFontSize = (float)Math.Clamp(Cfg.MeasFontSize, 8, 40);
        PcbRenderer.MeasColor = SkiaSharp.SKColor.TryParse(Cfg.MeasColor, out var mc) ? mc : new SkiaSharp.SKColor(0xFF, 0xE2, 0x7A);
        foreach (var pv in DocsOpen.Values.Select(l => l.Content).OfType<PcbView>()) pv.Invalidate();
        InvalidateSch();
    }

    public void ShowSettings()
    {
        var w = new Window { Title = "設定", Width = 520, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = this, Background = Ui.Bg1, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false };
        var sp = new StackPanel { Margin = new Thickness(18, 12, 18, 14) };
        TextBlock H(string s) => new() { Text = s, FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = Ui.Text, Margin = new Thickness(0, 12, 0, 6) };
        (FrameworkElement row, Slider s) Num(string label, double min, double max, double val, string note = null, string unit = " pt", double step = 0.5)
        {
            var d = new DockPanel { Margin = new Thickness(0, 3, 0, 3) };
            var v = new TextBlock { Width = 46, TextAlignment = TextAlignment.Right, Foreground = Ui.Text, VerticalAlignment = VerticalAlignment.Center, Text = $"{val:0.#}{unit}" };
            var s = new Slider { Minimum = min, Maximum = max, Value = val, TickFrequency = step, IsSnapToTickEnabled = true, VerticalAlignment = VerticalAlignment.Center };
            s.ValueChanged += (_, e) => v.Text = $"{e.NewValue:0.#}{unit}";
            var l = new TextBlock { Text = label, Width = 170, Foreground = Ui.Text, VerticalAlignment = VerticalAlignment.Center, ToolTip = note };
            DockPanel.SetDock(l, System.Windows.Controls.Dock.Left); DockPanel.SetDock(v, System.Windows.Controls.Dock.Right);
            d.Children.Add(l); d.Children.Add(v); d.Children.Add(s);
            return (d, s);
        }
        CheckBox C(string text, bool val, string note = null)
        {
            var c = new CheckBox { IsChecked = val, Margin = new Thickness(0, 4, 0, 4) };
            var t = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = Ui.Text };
            if (note != null) t.Inlines.Add(new System.Windows.Documents.Run("  " + note) { Foreground = Ui.Warn });
            c.Content = t; return c;
        }

        sp.Children.Add(H("字體大小"));
        var (r1, panelFont) = Num("面板預設字體", 10, 20, Cfg.PanelFontSize, "Projects、Navigator、Messages 面板"); sp.Children.Add(r1);
        var (r2, propTitle) = Num("Properties 標題", 12, 40, Cfg.PropTitleSize); sp.Children.Add(r2);
        var (r3, propBody) = Num("Properties 內容", 10, 28, Cfg.PropBodySize); sp.Children.Add(r3);
        var (r5, schFont) = Num("電路圖文字大小", 50, 250, Math.Round(SchView.FontScale * 100 / 5) * 5, "零件位號、值、參數、接腳、網路標籤等電路圖上的文字（也可以從 檢視 → 電路圖字體大小 選）", "%", 5); sp.Children.Add(r5);

        sp.Children.Add(H("單位"));
        RadioButton U(string t, bool on) => new() { Content = t, GroupName = "units", IsChecked = on, Foreground = Ui.Text, Margin = new Thickness(0, 2, 24, 2) };
        var uMm = U("mm", Units.Mm); var uMil = U("mil", !Units.Mm);
        sp.Children.Add(Ui.Row(Ui.T("PCB 長度單位", Ui.Text, 12.5, false, new Thickness(0, 0, 24, 0)), uMm, uMil));
        sp.Children.Add(new TextBlock { Text = "座標、量測與資訊小卡使用；也可以按視窗右下角的 mm / mil 或 Q 鍵切換。", Foreground = Ui.Dim, FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });

        // measure label: size, colour (preset swatches or #RRGGBB) and a live sample on the PCB background
        sp.Children.Add(H("PCB 量測"));
        var (r4, measSize) = Num("量測文字大小", 9, 32, Cfg.MeasFontSize); sp.Children.Add(r4);
        string measColor = Cfg.MeasColor;
        var sample = new TextBlock { FontFamily = new FontFamily("Consolas"), Text = "12.70  (ΔX 10.16, ΔY 7.62) mm", VerticalAlignment = VerticalAlignment.Center };
        var sampleBox = new Border { Background = new SolidColorBrush(Color.FromRgb(0x0A, 0x0C, 0x0D)), Padding = new Thickness(6, 2, 6, 3), HorizontalAlignment = HorizontalAlignment.Left, Child = sample };
        var hex = new TextBox { Width = 76, Text = measColor, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0), ToolTip = "自訂顏色：#RRGGBB" };
        var swatches = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        string[] presets = { "#FFE27A", "#FFFFFF", "#5FE0F0", "#7CFC7C", "#FFB24A", "#FF7AD9", "#FF5A5A", "#4DA3FF" };
        void ShowMeas()
        {
            Color col; try { col = (Color)ColorConverter.ConvertFromString(measColor); } catch { col = Color.FromRgb(0xFF, 0xE2, 0x7A); }
            sample.Foreground = new SolidColorBrush(col); sample.FontSize = measSize.Value;
            foreach (Border b in swatches.Children) b.BorderBrush = string.Equals((string)b.Tag, measColor, StringComparison.OrdinalIgnoreCase) ? Brushes.White : Ui.Line;
        }
        foreach (var pc in presets)
        {
            var b = new Border { Width = 20, Height = 20, Margin = new Thickness(0, 0, 5, 0), BorderThickness = new Thickness(2), CornerRadius = new CornerRadius(3), Cursor = Cursors.Hand, Tag = pc,
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(pc)), ToolTip = pc };
            b.MouseLeftButtonUp += (_, _) => { measColor = pc; hex.Text = pc; ShowMeas(); };
            swatches.Children.Add(b);
        }
        hex.TextChanged += (_, _) =>
        {
            var t = hex.Text.Trim(); if (!t.StartsWith('#')) t = "#" + t;
            if (t.Length == 7 && int.TryParse(t[1..], System.Globalization.NumberStyles.HexNumber, null, out _)) { measColor = t.ToUpperInvariant(); ShowMeas(); }
        };
        measSize.ValueChanged += (_, _) => ShowMeas();
        var colRow = new DockPanel { Margin = new Thickness(0, 4, 0, 4) };
        var colLabel = new TextBlock { Text = "量測文字顏色", Width = 170, Foreground = Ui.Text, VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(colLabel, System.Windows.Controls.Dock.Left); colRow.Children.Add(colLabel);
        colRow.Children.Add(Ui.Row(swatches, hex));
        sp.Children.Add(colRow);
        sampleBox.Margin = new Thickness(170, 4, 0, 0); sp.Children.Add(sampleBox);
        ShowMeas();

        sp.Children.Add(H("Cross Probe"));
        var altSch = C("Alt＋左鍵點電路圖的 NET 時，點亮 PCB 上相同的 NET", Cfg.AltNetSchToPcb); sp.Children.Add(altSch);
        var altPcb = C("Alt＋左鍵點 PCB 的 NET 時，標示電路圖上相同的 NET", Cfg.AltNetPcbToSch, "（功能實驗中）"); sp.Children.Add(altPcb);
        var jump = C("Cross Probe 時切換到對應的文件分頁", Cfg.CrossProbeJump); sp.Children.Add(jump);
        var lightPart = C("從電路圖點選元件時，同時點亮 PCB 上的元件", Cfg.ProbeLightPart); sp.Children.Add(lightPart);
        var auto = C("開啟專案時自動分割檢視（左電路圖／右 PCB）", Cfg.AutoSplit); sp.Children.Add(auto);

        sp.Children.Add(H("電路圖"));
        var pars = C("顯示元件的值（Value）與參數", Cfg.SchShowParams); sp.Children.Add(pars);
        sp.Children.Add(new TextBlock { Text = "套用到所有電路圖；位號一律顯示，不影響 PCB。", Foreground = Ui.Dim, FontSize = 11.5, Margin = new Thickness(26, 0, 0, 0) });

        bool ok = false;
        var btns = new DockPanel { Margin = new Thickness(0, 18, 0, 0), LastChildFill = false };
        var reset = Ui.Btn("恢復預設", () =>
        {
            panelFont.Value = 12.5; propTitle.Value = 24; propBody.Value = 16; schFont.Value = 100; uMm.IsChecked = true;
            measSize.Value = 13; measColor = presets[0]; hex.Text = presets[0]; ShowMeas();
            altSch.IsChecked = true; altPcb.IsChecked = true; jump.IsChecked = true; lightPart.IsChecked = false; auto.IsChecked = false; pars.IsChecked = true;
        });
        DockPanel.SetDock(reset, System.Windows.Controls.Dock.Left); btns.Children.Add(reset);
        var cancel = Ui.Btn("取消", w.Close); DockPanel.SetDock(cancel, System.Windows.Controls.Dock.Right); btns.Children.Add(cancel);
        var okB = Ui.Btn("確定", () => { ok = true; w.Close(); }); okB.IsDefault = true; DockPanel.SetDock(okB, System.Windows.Controls.Dock.Right); btns.Children.Add(okB);
        sp.Children.Add(btns);
        w.Content = sp;
        w.ShowDialog();
        if (!ok) return;

        Cfg.PanelFontSize = panelFont.Value; Cfg.PropTitleSize = propTitle.Value; Cfg.PropBodySize = propBody.Value;
        Cfg.AltNetSchToPcb = altSch.IsChecked == true; Cfg.AltNetPcbToSch = altPcb.IsChecked == true; Cfg.ProbeLightPart = lightPart.IsChecked == true;
        SetJump(jump.IsChecked == true);
        Cfg.AutoSplit = auto.IsChecked == true; if (_autoSplitMenu != null) _autoSplitMenu.IsChecked = Cfg.AutoSplit;
        Cfg.SchShowParams = pars.IsChecked == true;
        Cfg.MeasFontSize = measSize.Value; Cfg.MeasColor = measColor;
        Cfg.FontScale = schFont.Value / 100;
        if ((uMm.IsChecked == true) != Units.Mm) SetUnits(uMm.IsChecked == true);
        Cfg.Save();
        ApplyDisplaySettings();
        InvalidateSch();
        RefreshProjects();
        ShowNav(ActiveDoc?.Group, ActiveDoc);
        if (ActiveView is SchView sv) _props.ShowSchSel(sv.Doc, sv.Sel, sv.SheetIndex);
        else _props.Refresh();
        SetStatus("設定已套用");
    }

    // ================= PDF =================
    /// <summary>The sheets to print: scope "sheet" (the one shown), "doc" (all of this file) or "group" (the project /
    /// group). Pages follow the sheet numbers of the title blocks (頁次), not the order the project lists its files;
    /// sheets without a number keep their order after the numbered ones.</summary>
    public List<(SchSheet sheet, string file)> PdfPages(SchView sv, string scope)
    {
        var items = new List<(SchSheet sheet, string file)>();
        if (scope == "sheet") items.Add((sv.Doc.Sch.Sheets[sv.SheetIndex], sv.Doc.Name));
        else if (scope == "doc") items.AddRange(sv.Doc.Sch.Sheets.Select(s => (s, sv.Doc.Name)));
        else foreach (var d in sv.Doc.Group.SchDocs) items.AddRange(d.Sch.Sheets.Select(s => (s, d.Name)));
        return items.OrderBy(it => it.sheet.Number ?? int.MaxValue).ToList();
    }

    public void ExportPdf()
    {
        var sv = ActiveView as SchView ?? DocsOpen.Values.Select(l => l.Content).OfType<SchView>().FirstOrDefault(v => IsVisibleDoc(v.Doc));
        if (sv == null) { SetStatus("請先切到一份電路圖"); return; }
        var w = new Window { Title = "匯出 PDF", Width = 420, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = this, Background = Ui.Bg1, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false };
        var sp = new StackPanel { Margin = new Thickness(14) };
        RadioButton R(string t, string grp, bool c) => new() { Content = t, GroupName = grp, IsChecked = c };
        var sSheet = R("目前圖紙", "s", true); var sDoc = R("此電路圖全部圖紙", "s", false); var sGrp = R($"整個{(sv.Doc.Group?.Kind == GroupKind.Project ? "專案" : "群組")}（{sv.Doc.Group?.SchDocs.Count()} 張）", "s", false);
        sGrp.IsEnabled = sv.Doc.Group != null && sv.Doc.Group.Kind != GroupKind.Free;
        var pAuto = R("自動統一", "p", true); var pA4 = R("A4", "p", false); var pA3 = R("A3", "p", false); var pOrig = R("各頁原尺寸", "p", false);
        var mColor = R("彩色", "m", true); var mMono = R("黑白", "m", false);
        sp.Children.Add(Ui.Header("範圍")); sp.Children.Add(Ui.Row(sSheet, sDoc)); sp.Children.Add(sGrp);
        sp.Children.Add(Ui.Header("紙張")); sp.Children.Add(Ui.Row(pAuto, pA4, pA3, pOrig));
        sp.Children.Add(Ui.Header("色彩")); sp.Children.Add(Ui.Row(mColor, mMono));
        var openAfter = new CheckBox { Content = "完成後開啟PDF", IsChecked = Cfg.PdfOpenAfter, Margin = new Thickness(0, 12, 0, 0) };
        sp.Children.Add(openAfter);
        bool go = false;
        var btns = Ui.Row(Ui.Btn("產生 PDF…", () => { go = true; w.Close(); }), Ui.Btn("取消", w.Close)); btns.Margin = new Thickness(0, 14, 0, 0);
        sp.Children.Add(btns); w.Content = sp; w.ShowDialog();
        if (!go) return;
        Cfg.PdfOpenAfter = openAfter.IsChecked == true;
        var items = PdfPages(sv, sSheet.IsChecked == true ? "sheet" : sDoc.IsChecked == true ? "doc" : "group");
        var dlg = new SaveFileDialog { Filter = "PDF|*.pdf", FileName = Path.GetFileNameWithoutExtension(sGrp.IsChecked == true ? sv.Doc.Group.Name : sv.Doc.Name) + ".pdf" };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            var paper = pA4.IsChecked == true ? PdfPaper.A4 : pA3.IsChecked == true ? PdfPaper.A3 : pOrig.IsChecked == true ? PdfPaper.Original : PdfPaper.Auto;
            int n = PdfExport.ExportSchematics(items, dlg.FileName, paper, mMono.IsChecked == true, SchView.FontScale, SchView.ShowParams);
            SetStatus($"已匯出 {n} 頁 PDF：{dlg.FileName}");
            if (Cfg.PdfOpenAfter) try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(dlg.FileName) { UseShellExecute = true }); } catch { }
        }
        catch (Exception e) { MessageBox.Show(this, e.Message, "匯出失敗", MessageBoxButton.OK, MessageBoxImage.Error); }
    }
}
