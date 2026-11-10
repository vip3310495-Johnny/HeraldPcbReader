// Herald PCB Reader — Copyright (C) 2026 Johnny.ch.Tsai
// SPDX-License-Identifier: GPL-3.0-only
// Free software under the GNU General Public License version 3 (LICENSE.txt), WITHOUT ANY WARRANTY; with an
// additional permission to combine it with AvalonDock (Ms-PL), see NOTICE.txt.
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace AdApp.Panels;

/// <summary>Small helpers for building panel UI in code.</summary>
public static class Ui
{
    public static readonly Brush Text = Br(0xE2, 0xE2, 0xE2), Dim = Br(0x9A, 0x9A, 0xA0), Accent = Br(0x3D, 0x8E, 0xE0), Hl = Br(0xFF, 0x5C, 0xA8),
        Warn = Br(0xE8, 0xB3, 0x4A), Err = Br(0xF0, 0x6A, 0x6A), Bg2 = Br(0x2B, 0x2B, 0x2E), Bg1 = Br(0x33, 0x33, 0x37), Bg3 = Br(0x3F, 0x3F, 0x46), Line = Br(0x4A, 0x4A, 0x50),
        TitleBlue = Br(0x4D, 0xA3, 0xFF), Jump = Br(0x3D, 0x9B, 0xFF);
    /// <summary>Document tab colours: schematics yellow, PCBs green (black text).</summary>
    public static readonly Brush SchTab = Br(0xF2, 0xC9, 0x4C), PcbTab = Br(0x6F, 0xCF, 0x73);
    public static SolidColorBrush Br(byte r, byte g, byte b) { var s = new SolidColorBrush(Color.FromRgb(r, g, b)); s.Freeze(); return s; }

    /// <summary>Panel font size (Settings); F scales a size designed for the 12.5 pt default.</summary>
    public static double Base = 12.5;
    public static double F(double designSize) => Math.Round(designSize * Base / 12.5 * 2) / 2;

    /// <summary>Toolbar icon: a 16×16 stroked vector path (drawn, so it needs no icon font).</summary>
    public static System.Windows.Shapes.Path Icon(string data, Brush b = null) => new()
    {
        Data = Geometry.Parse(data), Stroke = b ?? TitleBlue, StrokeThickness = 1.5, StrokeLineJoin = PenLineJoin.Round,
        StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, Width = 16, Height = 16, Stretch = Stretch.None, VerticalAlignment = VerticalAlignment.Center,
    };
    /// <summary>Path data for <see cref="Icon"/> (16×16 grid).</summary>
    public static class Icons
    {
        public const string Split = "M1.5,2.5 H14.5 V13.5 H1.5 Z M8,2.5 V13.5 M3.5,5 H6 M10,5 H12.5";
        public const string CrossProbe = "M2.5,5.5 H13 M10,2.5 L13,5.5 L10,8.5 M13.5,10.5 H3 M6,7.5 L3,10.5 L6,13.5";
        public const string Fit = "M1.5,5 V1.5 H5 M11,1.5 H14.5 V5 M14.5,11 V14.5 H11 M5,14.5 H1.5 V11 M5.5,5.5 H10.5 V10.5 H5.5 Z";
        public const string Clear = "M8.5,2.5 L13.5,7.5 L8,13 H5 L2.5,10.5 Z M5.5,5.5 L10.5,10.5 M8,13 H14";
        public const string Pdf = "M3.5,1.5 H9.5 L12.5,4.5 V14.5 H3.5 Z M9.5,1.5 V4.5 H12.5 M8,6.5 V12 M5.8,9.8 L8,12 L10.2,9.8";
        // context menus (Projects)
        public const string Doc = "M3.5,1.5 H9.5 L12.5,4.5 V14.5 H3.5 Z M9.5,1.5 V4.5 H12.5 M5.5,8 H10.5 M5.5,11 H10.5";
        public const string Docs = "M6,1.5 H10.5 L13.5,4.5 V12 H6 Z M10.5,1.5 V4.5 H13.5 M3.5,4.5 V14.5 H10.5";
        public const string Rename = "M10.5,2.5 L13.5,5.5 L6,13 L2.5,13.5 L3,10 Z M9,4 L12,7";
        public const string Scope = "M6,1.5 H10 V4.5 H6 Z M8,4.5 V7.5 M3.5,7.5 H12.5 M3.5,7.5 V10.5 M12.5,7.5 V10.5 M1.5,10.5 H5.5 V13.5 H1.5 Z M10.5,10.5 H14.5 V13.5 H10.5 Z";
        public const string Info = "M1.5,8 A6.5,6.5 0 1,0 14.5,8 A6.5,6.5 0 1,0 1.5,8 Z M8,7.5 V11.5 M8,4.6 V4.7";
        public const string Folder = "M1.5,3.5 H6 L7.5,5 H14.5 V13 H1.5 Z";
        public const string FolderAdd = "M1.5,3.5 H6 L7.5,5 H14.5 V13 H1.5 Z M8,7 V11 M6,9 H10";
        public const string FolderOut = "M1.5,3.5 H6 L7.5,5 H14.5 V13 H1.5 Z M8,11.5 V7 M6,9 L8,7 L10,9";
        public const string Project = "M4.5,4.5 H11.5 V11.5 H4.5 Z M6.5,2 V4.5 M9.5,2 V4.5 M6.5,11.5 V14 M9.5,11.5 V14 M2,6.5 H4.5 M2,9.5 H4.5 M11.5,6.5 H14 M11.5,9.5 H14";
        public const string Plus = "M8,3.5 V12.5 M3.5,8 H12.5";
        public const string Close = "M4,4 L12,12 M12,4 L4,12";
        public const string Copy = "M5.5,5.5 H13.5 V14.5 H5.5 Z M10.5,3 V1.5 H2.5 V11 H4";
        public const string Explorer = "M1.5,3.5 H6 L7.5,5 H14.5 V13 H1.5 Z M7.2,8.8 A1.8,1.8 0 1,0 10.8,8.8 A1.8,1.8 0 1,0 7.2,8.8 Z M10.3,10.3 L12,12";
    }

    /// <summary>A context-menu item with an <see cref="Icon"/> in front (greyed with the item when it is off). The text
    /// is shown as it is: an underscore in a file name stays (WPF would take it for an access-key mark and drop it).</summary>
    public static MenuItem MenuItem(string header, Action click, bool enabled = true, string icon = null, Brush ink = null)
    {
        var i = new MenuItem { Header = header?.Replace("_", "__"), IsEnabled = enabled };
        if (icon != null) { var p = Icon(icon, ink); p.Opacity = enabled ? 1 : 0.4; i.Icon = p; }
        if (click != null) i.Click += (_, _) => click();
        return i;
    }

    public static TextBlock T(string s, Brush b = null, double size = 12, bool bold = false, Thickness? m = null) =>
        new() { Text = s ?? "", Foreground = b ?? Text, FontSize = size, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, Margin = m ?? new Thickness(0), TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };

    /// <summary>Document type badge like AD's file icons.</summary>
    public static Border Badge(string kind)
    {
        var (txt, bg) = kind switch
        {
            "sch" => ("SCH", Color.FromRgb(0xC9, 0x9A, 0x1E)), "pcb" => ("PCB", Color.FromRgb(0x2F, 0x8F, 0x4E)), "prj" => ("PRJ", Color.FromRgb(0x3D, 0x6E, 0xB8)),
            "grp" => ("GRP", Color.FromRgb(0x7A, 0x52, 0xB3)), "free" => ("DOC", Color.FromRgb(0x6A, 0x6A, 0x70)), "dir" => ("▸", Color.FromRgb(0x8A, 0x7A, 0x40)), _ => ("?", Colors.Gray),
        };
        return new Border
        {
            Background = new SolidColorBrush(bg), CornerRadius = new CornerRadius(2), Padding = new Thickness(3, 0, 3, 0), Margin = new Thickness(0, 1, 6, 1), MinWidth = 28,
            Child = new TextBlock { Text = txt, FontSize = 9, FontWeight = FontWeights.Bold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center },
        };
    }

    public static StackPanel Row(params UIElement[] els) { var s = new StackPanel { Orientation = Orientation.Horizontal }; foreach (var e in els) s.Children.Add(e); return s; }

    public static Grid KV(IEnumerable<(string k, string v)> rows, double size = 12, double keyWidth = 0)
    {
        var g = new Grid { Margin = new Thickness(0, 2, 0, 6) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(keyWidth > 0 ? keyWidth : Math.Max(96, size * 7.5)) });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        int r = 0;
        foreach (var (k, v) in rows)
        {
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var kt = T(k, Dim, size - 0.5, false, new Thickness(0, 1, 6, 1)); kt.VerticalAlignment = VerticalAlignment.Top; Grid.SetRow(kt, r); g.Children.Add(kt);
            var vt = new TextBox { Text = v ?? "", IsReadOnly = true, BorderThickness = new Thickness(0), Background = Brushes.Transparent, Foreground = Text, Padding = new Thickness(0, 1, 0, 1), FontSize = size, TextWrapping = TextWrapping.Wrap };
            Grid.SetRow(vt, r); Grid.SetColumn(vt, 1); g.Children.Add(vt);
            r++;
        }
        return g;
    }

    public static TextBlock Header(string s, double size = 11) => new() { Text = s, Foreground = Dim, FontSize = size, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 3), TextWrapping = TextWrapping.Wrap };

    /// <summary>Wrapping row of small buttons (pins, NETs, designators).</summary>
    public static WrapPanel Chips(IEnumerable<(string label, Action a)> items, int max = 60, double size = 11.5)
    {
        var list = items.ToList();
        var w = new WrapPanel { Margin = new Thickness(0, 2, 0, 6) }; int n = 0;
        foreach (var (l, a) in list)
        {
            if (n++ >= max) { w.Children.Add(T($"…另 {list.Count - max} 個", Dim, size)); break; }
            var b = new Button { Content = l, Padding = new Thickness(5, 1, 5, 1), Margin = new Thickness(0, 0, 4, 4), Background = Bg3, FontSize = size };
            b.Click += (_, _) => a(); w.Children.Add(b);
        }
        return w;
    }

    public static Button Btn(string s, Action a, string tip = null)
    {
        var b = new Button { Content = s, Style = (Style)Application.Current.FindResource("BoxButton"), Margin = new Thickness(0, 2, 6, 2), Padding = new Thickness(8, 2, 8, 2), ToolTip = tip };
        b.Click += (_, _) => a();
        return b;
    }

    public static Color ToColor(SkiaSharp.SKColor c) => Color.FromRgb(c.Red, c.Green, c.Blue);

    /// <summary>Every visual under d, depth first.</summary>
    public static IEnumerable<DependencyObject> Descend(DependencyObject d)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++) { var c = VisualTreeHelper.GetChild(d, i); yield return c; foreach (var x in Descend(c)) yield return x; }
    }
}
