// Herald PCB Reader — Copyright (C) 2026 Johnny.ch.Tsai
// SPDX-License-Identifier: GPL-3.0-only
// Free software under the GNU General Public License version 3 (LICENSE.txt), WITHOUT ANY WARRANTY; with an
// additional permission to combine it with AvalonDock (Ms-PL), see NOTICE.txt.
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AdApp.Panels;
using SkiaSharp;

namespace AdApp.Views;

/// <summary>"Point finger" (指示): the cursor becomes a pointing hand; a left click leaves a hand on the board with its
/// fingertip on that point, Space turns the hand 45° clockwise about its fingertip, Esc clears the hands. The hands
/// keep their size on screen and stay on their board points through pan, zoom, rotation and flips, so a technician
/// can mark several parts and then find them all at once with the whole board in view.</summary>
public sealed partial class PcbView
{
    /// <summary>A hand on the board: fingertip (mil) and the direction it points (board units, unit length).</summary>
    sealed record Finger(double X, double Y, double Dx, double Dy);
    readonly List<Finger> _fingers = new();
    const double DefaultFingerAngle = -45, FingerBase = 46;   // degrees (0 points up, clockwise); DIP high at 100 %
    double _fingerAngle = DefaultFingerAngle;
    Border _fingerPanel; Slider _fingerSlider; TextBlock _fingerSizeText, _fingerCountText;

    public int FingerCount => _fingers.Count;
    public double FingerAngle => _fingerAngle;
    static double FingerScale => Math.Clamp(Settings.Current.FingerScale, 0.1, 3);
    static double FingerSize => FingerBase * FingerScale;

    double Dpi => VisualTreeHelper.GetDpi(this).DpiScaleX;
    Cursor FingerCursor() => FingerArt.MakeCursor(_fingerAngle, FingerSize * Dpi);

    /// <summary>Entering the tool starts again from the default direction.</summary>
    void FingerToolChanged(bool on)
    {
        if (on) _fingerAngle = DefaultFingerAngle;
        EnsureFingerPanel();
        _fingerPanel.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        SyncFingerPanel();
    }

    void FingerRotate()
    {
        _fingerAngle += 45; if (_fingerAngle > 180) _fingerAngle -= 360;
        if (Tool == "finger") _canvas.Cursor = FingerCursor();
    }

    /// <summary>A click with the tool: a hand pointing the current way, or (Ctrl) take away the hand nearest the click.</summary>
    void FingerClick(Point p, ModifierKeys mods)
    {
        if ((mods & ModifierKeys.Control) != 0)
        {
            int best = -1; double bd = 14;
            for (int i = 0; i < _fingers.Count; i++) { var s = St.ToScreen(_fingers[i].X, _fingers[i].Y, W, H); double d = Math.Sqrt((s.X - p.X) * (s.X - p.X) + (s.Y - p.Y) * (s.Y - p.Y)); if (d < bd) { bd = d; best = i; } }
            if (best >= 0) { _fingers.RemoveAt(best); Invalidate(); SyncFingerPanel(); }
            return;
        }
        var (wx, wy) = St.ToWorld(p.X, p.Y, W, H);
        double a = _fingerAngle * Math.PI / 180;
        var (tx, ty) = St.ToWorld(p.X + Math.Sin(a) * 40, p.Y - Math.Cos(a) * 40, W, H);
        double dx = tx - wx, dy = ty - wy, l = Math.Sqrt(dx * dx + dy * dy);
        if (l < 1e-12) return;
        _fingers.Add(new Finger(wx, wy, dx / l, dy / l));
        Invalidate(); SyncFingerPanel();
    }

    public void ClearFingers() { if (_fingers.Count == 0) return; _fingers.Clear(); Invalidate(); SyncFingerPanel(); }

    /// <summary>The hands, in DIP (the canvas is scaled to DIP when this runs).</summary>
    void DrawFingers(SKCanvas c)
    {
        if (_fingers.Count == 0) return;
        float size = (float)FingerSize;
        foreach (var f in _fingers)
        {
            var s = St.ToScreen(f.X, f.Y, W, H);
            double k = 40 / Math.Max(1e-9, St.S);
            var s2 = St.ToScreen(f.X + f.Dx * k, f.Y + f.Dy * k, W, H);
            float ang = (float)(Math.Atan2(s2.X - s.X, -(s2.Y - s.Y)) * 180 / Math.PI);
            FingerArt.Draw(c, s.X, s.Y, ang, size, backdrop: true);
        }
    }

    void EnsureFingerPanel()
    {
        if (_fingerPanel != null) return;
        var sp = new StackPanel { Width = 238 };
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        _fingerCountText = new TextBlock { Foreground = Ui.Dim, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(_fingerCountText, Dock.Right); head.Children.Add(_fingerCountText);
        head.Children.Add(new TextBlock { Text = "指示標記", FontSize = 13.5, FontWeight = FontWeights.SemiBold, Foreground = Ui.Text });
        sp.Children.Add(head);
        var size = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
        var label = new TextBlock { Text = "大小", Width = 34, Foreground = Ui.Text, VerticalAlignment = VerticalAlignment.Center };
        _fingerSizeText = new TextBlock { Width = 44, TextAlignment = TextAlignment.Right, Foreground = Ui.Dim, FontFamily = new FontFamily("Consolas"), VerticalAlignment = VerticalAlignment.Center };
        _fingerSlider = new Slider { Minimum = 10, Maximum = 300, SmallChange = 5, LargeChange = 25, Value = FingerScale * 100, VerticalAlignment = VerticalAlignment.Center, IsSnapToTickEnabled = true, TickFrequency = 5, ToolTip = "標記與游標的大小（10–300%）：只改大小，位置與方向不變" };
        _fingerSlider.ValueChanged += (_, e) => SetFingerScale(e.NewValue / 100);
        DockPanel.SetDock(label, Dock.Left); DockPanel.SetDock(_fingerSizeText, Dock.Right);
        size.Children.Add(label); size.Children.Add(_fingerSizeText); size.Children.Add(_fingerSlider);
        sp.Children.Add(size);
        sp.Children.Add(new TextBlock
        {
            Text = "左鍵：放置　空白鍵：順時針轉 45°\nCtrl＋左鍵：拿掉一個　Esc：清除全部\n右鍵或中鍵拖曳、左鍵拖曳：平移", FontSize = 11.5, Foreground = Ui.Dim, Margin = new Thickness(0, 4, 0, 6), LineHeight = 17,
        });
        var clear = Ui.Btn("清除全部標記 (Esc)", ClearFingers);
        clear.HorizontalAlignment = HorizontalAlignment.Left;
        sp.Children.Add(clear);
        _fingerPanel = new Border
        {
            Child = sp, Background = new SolidColorBrush(Color.FromArgb(0xE8, 0x1E, 0x21, 0x24)), BorderBrush = new SolidColorBrush(Color.FromArgb(0x50, 0xFF, 0xFF, 0xFF)), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 8, 10, 10), Margin = new Thickness(12),
            // bottom left: the board's top left (and the info card on the right) stay free for the hands
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom, Visibility = Visibility.Collapsed,
        };
        _content.Children.Add(_fingerPanel);
    }

    void SyncFingerPanel()
    {
        if (_fingerPanel == null) return;
        _fingerSizeText.Text = $"{Math.Round(FingerScale * 100)}%";
        _fingerCountText.Text = _fingers.Count == 0 ? "尚未標記" : $"已標記 {_fingers.Count} 個";
    }

    /// <summary>Size of the hands and the cursor, 10–300 %; where they point and in which direction stays.</summary>
    public void SetFingerScale(double s)
    {
        Settings.Current.FingerScale = Math.Clamp(s, 0.1, 3);
        if (_fingerSlider != null && Math.Abs(_fingerSlider.Value - FingerScale * 100) > 0.5) _fingerSlider.Value = FingerScale * 100;
        if (Tool == "finger") _canvas.Cursor = FingerCursor();
        SyncFingerPanel(); Invalidate();
    }

    // ---------- automation ----------
    public void FingerAtView(Point viewPt, ModifierKeys mods) { if (Tool != "finger") SetTool("finger"); FingerClick(TranslatePoint(viewPt, _canvas), mods); }
    public void FingerRotateForTest() => FingerRotate();
    /// <summary>Tests: a key pressed on the view, routed like a real one.</summary>
    public void KeyForTest(Key k) { var src = PresentationSource.FromVisual(this); if (src == null) return; RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, src, 0, k) { RoutedEvent = Keyboard.KeyDownEvent }); }
    public static void SaveFingerCursorPng(string path, double angle, double sizePx) => FingerArt.SavePng(path, angle, sizePx);
}
