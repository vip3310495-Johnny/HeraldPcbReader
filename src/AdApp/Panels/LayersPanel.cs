// Herald PCB Reader — Copyright (C) 2026 Johnny.ch.Tsai
// SPDX-License-Identifier: GPL-3.0-only
// Free software under the GNU General Public License version 3 (LICENSE.txt), WITHOUT ANY WARRANTY; with an
// additional permission to combine it with AvalonDock (Ms-PL), see NOTICE.txt.
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using AdCore.Render;
using AdApp.Views;
using SkiaSharp;

namespace AdApp.Panels;

/// <summary>AD "View Configuration"-like settings for a PCB view (opened from the Active Bar's 「顯示…」):
/// layers, colours, display options, units and the selection filter.</summary>
public sealed class LayersPanel : ScrollViewer
{
    readonly StackPanel _p = new() { Margin = new Thickness(8, 6, 8, 10) };
    PcbView _v;
    public event Action UnitsChanged;
    static readonly string[] Palette = { "#FF2A2A", "#2F6BFF", "#FFFF00", "#A0A000", "#00CC66", "#00FFFF", "#FF00FF", "#FFA500", "#BC8E00", "#9966FF", "#70DBFA", "#808080", "#C0C0C0", "#FFFFFF", "#800000", "#008080" };

    public LayersPanel() { Background = Ui.Bg2; VerticalScrollBarVisibility = ScrollBarVisibility.Auto; Content = _p; Bind(null); }

    public void Bind(PcbView v)
    {
        _v = v; _p.Children.Clear();
        if (v == null) return;
        var st = v.St;
        _p.Children.Add(Ui.T(v.Doc.Name, Ui.Text, 13, true, new Thickness(0, 0, 0, 6)));

        // layer set
        var sets = new (string k, string n)[] { ("all", "全部 All Layers"), ("signal", "訊號層 Signal"), ("plane", "內電層 Plane"), ("nonsignal", "非訊號層 Non-Signal"), ("mech", "機構層 Mechanical"), ("top", "頂面 Top Side"), ("bottom", "底面 Bottom Side"), ("custom", "自訂") };
        var combo = new ComboBox { Margin = new Thickness(0, 0, 0, 6) };
        foreach (var (k, n) in sets) combo.Items.Add(new ComboBoxItem { Content = n, Tag = k });
        combo.SelectedIndex = Math.Max(0, Array.FindIndex(sets, s => s.k == st.LayerSet));
        combo.SelectionChanged += (_, _) => { if (combo.SelectedItem is ComboBoxItem { Tag: string k } && k != "custom" && k != st.LayerSet) { st.SetLayerSet(k); Changed(); } };
        _p.Children.Add(Ui.Row(Ui.T("圖層組合  ", Ui.Dim, 11.5), combo));
        _p.Children.Add(Ui.Row(Ui.Btn("全部顯示", () => { st.SetLayerSet("all"); Changed(); }), Ui.Btn("全部隱藏", () => { foreach (var l in st.LayerMap.Values) l.Visible = false; st.LayerSet = "custom"; Changed(); })));

        foreach (var grp in st.LayerMap.Values.GroupBy(l => PcbViewState.Group(l.Id)).OrderBy(g => g.Min(l => st.TabRank(l.Id))))
        {
            _p.Children.Add(Ui.Header(grp.Key));
            foreach (var l in grp.OrderBy(l => st.TabRank(l.Id))) _p.Children.Add(LayerRow(l));
        }

        _p.Children.Add(Ui.Header("顯示"));
        CB("單層模式（Shift+S）", st.Single, b => st.Single = b);
        CB("在焊墊/導線上顯示 NET 名稱（N）", st.ShowNetNames, b => st.ShowNetNames = b);
        CB("在焊墊上顯示 pin 腳編號", st.ShowPadNumbers, b => st.ShowPadNumbers = b);
        CB("顯示板框線", st.ShowOutline, b => st.ShowOutline = b);
        CB("顯示文字", v.Scene.ShowText, b => { v.Scene.ShowText = b; v.Scene.InvalidatePictures(); v.Scene.RebuildHighlight(st); });
        var dim = new Slider { Minimum = 0.03, Maximum = 0.8, Value = st.Dim, Width = 150 };
        dim.ValueChanged += (_, e) => { st.Dim = (float)e.NewValue; v.Invalidate(); };
        _p.Children.Add(Ui.Row(Ui.T("點亮時其他物件亮度  ", Ui.Dim, 11.5), dim));
        _p.Children.Add(ColorRow("板面顏色", st.BoardColor, c => { st.BoardColor = c; v.Invalidate(); }, "#000000", "#0B1F12", "#101820", "#1A1A1A"));
        _p.Children.Add(ColorRow("背景顏色", st.WorkColor, c => { st.WorkColor = c; v.Invalidate(); }, "#2E3438", "#000000", "#202020", "#3A3A3A", "#F0F0F0"));
        var mm = new RadioButton { Content = "mm", IsChecked = Units.Mm, GroupName = "u" }; var mil = new RadioButton { Content = "mil", IsChecked = !Units.Mm, GroupName = "u" };
        mm.Checked += (_, _) => { Units.Mm = true; UnitsChanged?.Invoke(); v.Invalidate(); }; mil.Checked += (_, _) => { Units.Mm = false; UnitsChanged?.Invoke(); v.Invalidate(); };
        _p.Children.Add(Ui.Row(Ui.T("單位  ", Ui.Dim, 11.5), mm, mil));


    }

    void Changed() { _v.Scene.InvalidatePictures(); _v.Scene.RebuildHighlight(_v.St); _v.AfterLayers(); Bind(_v); }

    void CB(string text, bool val, Action<bool> set)
    {
        var c = new CheckBox { Content = text, IsChecked = val };
        c.Click += (_, _) => { set(c.IsChecked == true); _v.Invalidate(); };
        _p.Children.Add(c);
    }
    UIElement LayerRow(PcbLayerInfo l)
    {
        var st = _v.St;
        var cb = new CheckBox { IsChecked = l.Visible, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
        cb.Click += (_, _) => { l.Visible = cb.IsChecked == true; st.LayerSet = "custom"; _v.BuildTabs(); _v.Invalidate(); };
        var sw = new Button { Width = 16, Height = 12, Padding = new Thickness(0), Margin = new Thickness(0, 0, 6, 0), Background = new SolidColorBrush(Ui.ToColor(l.Color)), BorderBrush = Brushes.Black, ToolTip = "變更顏色" };
        sw.Click += (_, _) => PalettePopup(sw, c => { l.Color = c; _v.Scene.InvalidatePictures(); _v.Scene.RebuildHighlight(st); _v.BuildTabs(); _v.Invalidate(); sw.Background = new SolidColorBrush(Ui.ToColor(c)); });
        var name = new Button { Content = l.Name, Padding = new Thickness(2, 0, 2, 0), Foreground = l.Id == st.Cur ? Ui.Accent : Ui.Text, FontWeight = l.Id == st.Cur ? FontWeights.SemiBold : FontWeights.Normal, ToolTip = "設為目前層（畫在最上面）" };
        name.Click += (_, _) => { _v.SetCurrentLayer(l.Id); Bind(_v); };
        return Ui.Row(cb, sw, name, Ui.T("  " + l.Count, Ui.Dim, 10.5));
    }

    void PalettePopup(FrameworkElement anchor, Action<SKColor> pick)
    {
        var pop = new Popup { PlacementTarget = anchor, Placement = PlacementMode.Bottom, StaysOpen = false, AllowsTransparency = true };
        var wrap = new WrapPanel { Width = 8 * 22, Background = Ui.Bg1 };
        foreach (var h in Palette)
        {
            var c = SKColor.Parse(h);
            var b = new Button { Width = 20, Height = 20, Margin = new Thickness(1), Padding = new Thickness(0), Background = new SolidColorBrush(Ui.ToColor(c)) };
            b.Click += (_, _) => { pick(c); pop.IsOpen = false; };
            wrap.Children.Add(b);
        }
        pop.Child = new Border { BorderBrush = Ui.Line, BorderThickness = new Thickness(1), Child = wrap, Padding = new Thickness(3), Background = Ui.Bg1 };
        pop.IsOpen = true;
    }

    UIElement ColorRow(string label, SKColor cur, Action<SKColor> set, params string[] options)
    {
        var r = Ui.Row(Ui.T(label + "  ", Ui.Dim, 11.5));
        foreach (var h in options)
        {
            var c = SKColor.Parse(h);
            var b = new Button { Width = 18, Height = 14, Margin = new Thickness(1, 2, 3, 2), Padding = new Thickness(0), Background = new SolidColorBrush(Ui.ToColor(c)), BorderBrush = c == cur ? Ui.Accent : Brushes.Gray, BorderThickness = new Thickness(c == cur ? 2 : 1) };
            b.Click += (_, _) => { set(c); Bind(_v); };
            r.Children.Add(b);
        }
        return r;
    }
}
