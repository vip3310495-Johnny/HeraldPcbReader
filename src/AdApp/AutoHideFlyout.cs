// Herald PCB Reader — Copyright (C) 2026 Johnny.ch.Tsai
// SPDX-License-Identifier: GPL-3.0-only
// Free software under the GNU General Public License version 3 (LICENSE.txt), WITHOUT ANY WARRANTY; with an
// additional permission to combine it with AvalonDock (Ms-PL), see NOTICE.txt.
using System.ComponentModel;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AvalonDock;
using AvalonDock.Controls;
using AvalonDock.Layout;

namespace AdApp;

/// <summary>Closes an auto-hide panel's flyout as soon as it is left and slides it back into its side bar.
/// AvalonDock looks only every 1.5 s (a flyout lingered up to 3 s), only once the panel has lost the keyboard, and
/// drops it without animation: its timer is parked, and this one looks every 50 ms while a flyout is open. A click
/// anywhere else in the window closes it too. The flyout is a window of its own that WPF cannot animate, so a
/// snapshot of it slides over the dock area while the real one is already gone.</summary>
sealed class AutoHideFlyout
{
    const double SlideMs = 375, GraceMs = 100;
    const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
    static readonly FieldInfo MgrField = typeof(DockingManager).GetField("_autoHideWindowManager", Any);
    static readonly FieldInfo Presenter = typeof(LayoutAutoHideWindowControl).GetField("_internalHostPresenter", Any);
    readonly DockingManager _dock; readonly Canvas _fx;
    readonly DispatcherTimer _tick = new(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(50) };
    LayoutAutoHideWindowControl _win;
    DateTime? _leftAt;
    bool _wasActive;            // this flyout had the focus since it opened
    bool _clickedAway;          // a click in the window outside the flyout
    LayoutContent _clickedDoc;  // the document that click went to
    FrameworkElement _slide;    // the snapshot while it slides out
    bool _reopen;               // its tab clicked while it slides out
    bool _activeAtClose;        // it still had the keyboard when it started to slide out
    DateTime _slideFrom;

    /// <summary>Automation: from leaving the flyout to the end of its slide, last time.</summary>
    public double LastCloseMs { get; private set; }
    public bool Sliding => _slide != null;
    /// <summary>Automation: the steps of the last close with their times.</summary>
    public string Trace { get; private set; } = "";
    void Tr(string what) => Trace += $"{what}@{(DateTime.Now - _slideFrom).TotalMilliseconds:0} ";

    AutoHideFlyout(DockingManager dock, Canvas fx)
    {
        _dock = dock; _fx = fx;
        _tick.Tick += (_, _) => Tick();
        // AvalonDock makes a new flyout window (and manager) whenever the DockingManager is loaded again
        DependencyPropertyDescriptor.FromProperty(DockingManager.AutoHideWindowProperty, typeof(DockingManager)).AddValueChanged(dock, (_, _) => Attach());
        Attach();
        Window.GetWindow(dock)?.AddHandler(UIElement.PreviewMouseDownEvent, new MouseButtonEventHandler(OnWindowMouseDown), true);
    }

    /// <summary>Null (AvalonDock keeps its own timer) when this AvalonDock version has other internals.</summary>
    public static AutoHideFlyout TryCreate(DockingManager dock, Canvas fx) =>
        dock.AutoHideWindow != null && Timer(dock) != null && HideMethod(dock) != null ? new AutoHideFlyout(dock, fx) : null;

    static object Mgr(DockingManager d) => MgrField?.GetValue(d);
    static DispatcherTimer Timer(DockingManager d) => Mgr(d) is { } m ? m.GetType().GetField("_closeTimer", Any)?.GetValue(m) as DispatcherTimer : null;
    static MethodInfo HideMethod(DockingManager d) => Mgr(d)?.GetType().GetMethod("HideAutoWindow", Any) is { } h && h.GetParameters().Length == 1 ? h : null;

    void Attach()
    {
        if (Timer(_dock) is { } t) t.Interval = TimeSpan.FromDays(1);   // parked: Tick closes it
        if (_win == _dock.AutoHideWindow) return;
        if (_win != null) _win.IsVisibleChanged -= OnVisible;
        _win = _dock.AutoHideWindow;
        if (_win != null) _win.IsVisibleChanged += OnVisible;
    }

    void OnVisible(object s, DependencyPropertyChangedEventArgs e)
    {
        _leftAt = null; _wasActive = _clickedAway = _reopen = false; _clickedDoc = null;
        if (!_win.IsVisible) { _tick.Stop(); return; }
        Attach(); EndSlide(); _tick.Start();   // opened again while the old one still slides out
    }

    void OnWindowMouseDown(object s, MouseButtonEventArgs e)
    {
        if (_win is not { IsVisible: true }) return;
        // clicks inside the flyout (and in its context menus) reach the window too, through the logical tree: they
        // come from another window (HwndSource) and are not a click away
        if (e.OriginalSource is Visual v && PresentationSource.FromVisual(v) is { } src && src != PresentationSource.FromVisual(_dock)) return;
        if (Up<LayoutAutoHideWindowControl>(e.OriginalSource as DependencyObject) != null) return;
        bool onTab = Up<LayoutAnchorControl>(e.OriginalSource as DependencyObject) != null;
        if (_slide != null) { _reopen |= onTab; return; }
        if (onTab) return;   // a side bar tab: AvalonDock keeps this flyout or shows that one
        _clickedAway = true;
        _clickedDoc = Up<LayoutDocumentControl>(e.OriginalSource as DependencyObject)?.Model as LayoutContent;
    }

    static T Up<T>(DependencyObject d) where T : DependencyObject
    {
        for (; d != null; d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
            if (d is T t) return t;
        return null;
    }

    void Tick()
    {
        var w = _dock.AutoHideWindow;
        if (w is not { IsVisible: true } || w.Model is not LayoutAnchorable a) { _tick.Stop(); return; }
        if (_slide != null)
        {
            // its tab clicked again (or the panel called up) while it slides out: it stays
            if (_reopen || a.IsActive && !_activeAtClose) { _reopen = false; EndSlide(); ShowWindow(w.Handle, SW_SHOWNA); }
            return;
        }
        // stays while a button is down (resizing it, dragging, the click away not released yet) and, until clicked away
        // from, while it has the focus, is under the mouse, or the mouse is on the side bar it opens from
        if (a.IsActive) _wasActive = true;
        var bar = Side(a) switch { AnchorSide.Left => _dock.LeftSidePanel, AnchorSide.Top => _dock.TopSidePanel, AnchorSide.Bottom => _dock.BottomSidePanel, _ => _dock.RightSidePanel };
        bool keep = Mouse.LeftButton == MouseButtonState.Pressed || !_clickedAway && (a.IsActive || CursorIn(w) || CursorIn(bar));
        if (keep) { _leftAt = null; return; }
        _leftAt ??= DateTime.Now;
        // clicked open and then away: go now; opened by hovering: give the mouse a moment between tab and flyout
        if (_wasActive || _clickedAway || (DateTime.Now - _leftAt.Value).TotalMilliseconds >= GraceMs) Close(w, a);
    }

    static AnchorSide Side(LayoutAnchorable a) => (a.Parent?.Parent as LayoutAnchorSide)?.Side ?? AnchorSide.Right;

    void Close(LayoutAutoHideWindowControl w, LayoutAnchorable a)
    {
        EndSlide();
        _slideFrom = _leftAt ?? DateTime.Now; _leftAt = null; Trace = ""; Tr("close");
        var doc = _clickedAway ? _clickedDoc : null; _clickedAway = false; _clickedDoc = null; _activeAtClose = a.IsActive;
        var shot = Snapshot(w); Tr("shot");
        if (shot == null || PresentationSource.FromVisual(_fx) == null) { Hide(a, doc); return; }
        var r = w.TransformToVisual(_fx).TransformBounds(new Rect(w.RenderSize));
        var side = Side(a);
        var tt = new TranslateTransform();
        var box = new Border { Width = r.Width, Height = r.Height, ClipToBounds = true, Child = new Image { Source = shot, Width = r.Width, Height = r.Height, Stretch = Stretch.Fill, RenderTransform = tt } };
        Canvas.SetLeft(box, r.X); Canvas.SetTop(box, r.Y);
        _fx.Children.Add(box); _slide = box;
        bool horiz = side is AnchorSide.Left or AnchorSide.Right;
        double to = side switch { AnchorSide.Left => -r.Width, AnchorSide.Top => -r.Height, AnchorSide.Bottom => r.Height, _ => r.Width };
        var anim = new DoubleAnimation(0, to, TimeSpan.FromMilliseconds(SlideMs)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut } };
        anim.Completed += (_, _) => Finish(box, w, a, doc, "anim");
        tt.BeginAnimation(horiz ? TranslateTransform.XProperty : TranslateTransform.YProperty, anim);
        // the flyout's window goes once the snapshot under it is on screen. AvalonDock's own hide re-lays out the dock
        // (a few hundred ms on the UI thread), which would freeze the slide halfway: it waits for the end of the slide
        After(30, () => { if (_slide == box) { ShowWindow(w.Handle, SW_HIDE); Tr("hwnd"); } });
        After(SlideMs + 300, () => Finish(box, w, a, doc, "timer"));   // no animation runs while the screen is locked
    }

    void Finish(FrameworkElement box, LayoutAutoHideWindowControl w, LayoutAnchorable a, LayoutContent doc, string by)
    {
        if (_slide != box) return;
        Tr(by); EndSlide();
        if (w.Model == a) Hide(a, doc);
    }

    /// <summary>AvalonDock's hide. A panel that kept the keyboard through the click away hands it to the document
    /// clicked (its shortcuts work right away).</summary>
    void Hide(LayoutAnchorable a, LayoutContent doc)
    {
        Tr("hide");
        try { HideMethod(_dock)?.Invoke(Mgr(_dock), new object[] { null }); } catch (TargetInvocationException) { }
        if (a.IsActive && doc is { IsActive: false }) doc.IsActive = true;
        Tr("hidden");
    }

    void EndSlide()
    {
        if (_slide == null) return;
        _fx.Children.Remove(_slide); _slide = null;
        LastCloseMs = (DateTime.Now - _slideFrom).TotalMilliseconds;
    }

    BitmapSource Snapshot(LayoutAutoHideWindowControl w)
    {
        if (Presenter?.GetValue(w) is not FrameworkElement p || p.ActualWidth < 1 || p.ActualHeight < 1) return null;
        var dpi = VisualTreeHelper.GetDpi(p);
        var bmp = new RenderTargetBitmap((int)Math.Ceiling(p.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(p.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bmp.Render(p); bmp.Freeze();
        return bmp;
    }

    void After(double ms, Action a)
    {
        var t = new DispatcherTimer(DispatcherPriority.Normal, _dock.Dispatcher) { Interval = TimeSpan.FromMilliseconds(ms) };
        t.Tick += (_, _) => { t.Stop(); a(); };
        t.Start();
    }

    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
    const int SW_HIDE = 0, SW_SHOWNA = 8;

    /// <summary>Automation: the flyout window is on screen.</summary>
    public bool WindowShown => _dock.AutoHideWindow is { Handle: var h } && h != IntPtr.Zero && IsWindowVisible(h);

    static bool CursorIn(FrameworkElement e)
    {
        if (e is not { IsVisible: true } || e.ActualWidth < 1 || e.ActualHeight < 1 || PresentationSource.FromVisual(e) == null || !GetCursorPos(out var c)) return false;
        var a = e.PointToScreen(new Point(0, 0)); var b = e.PointToScreen(new Point(e.ActualWidth, e.ActualHeight));
        return c.X >= Math.Min(a.X, b.X) && c.X < Math.Max(a.X, b.X) && c.Y >= Math.Min(a.Y, b.Y) && c.Y < Math.Max(a.Y, b.Y);
    }
}
