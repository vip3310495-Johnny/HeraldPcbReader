// Herald PCB Reader — Copyright (C) 2026 Johnny.ch.Tsai
// SPDX-License-Identifier: GPL-3.0-only
// Free software under the GNU General Public License version 3 (LICENSE.txt), WITHOUT ANY WARRANTY; with an
// additional permission to combine it with AvalonDock (Ms-PL), see NOTICE.txt.
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using AdApp.Panels;
using AdCore.Pcb;
using AdCore.Render;
using SkiaSharp;
using static AdCore.Util.TextUtil;

namespace AdApp.Views;

/// <summary>Floating card in the PCB view's top-right corner (as in v1): measurement, highlighted NETs / parts
/// and the selected object. Hidden when there is nothing to show.</summary>
public sealed class PcbInfoCard : Border
{
    readonly PcbView _v;
    readonly StackPanel _p = new();
    const double Body = 13.5;
    bool _collapsed;   // only the section titles (to see the board under the card)
    public bool Collapsed { get => _collapsed; set { _collapsed = value; Refresh(); } }

    public PcbInfoCard(PcbView v)
    {
        _v = v;
        Width = 320; HorizontalAlignment = HorizontalAlignment.Right; VerticalAlignment = VerticalAlignment.Top; Margin = new Thickness(12, 58, 12, 12);
        Background = new SolidColorBrush(Color.FromArgb(0xF2, 0x26, 0x26, 0x29)); BorderBrush = Ui.Line; BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(8); Visibility = Visibility.Collapsed;
        Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 3, Opacity = 0.45 };
        Child = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _p, MaxHeight = 640 };
        v.SizeChanged += (_, _) => { ((ScrollViewer)Child).MaxHeight = Math.Max(160, v.ActualHeight - 110); Width = Math.Clamp(v.ActualWidth * 0.5, 230, 320); };
    }

    PcbBoard Bd => _v.Doc.Pcb;
    string XY(double x, double y) => $"{Units.Fmt(x - Bd.OriginX)}, {Units.Fmt(y - Bd.OriginY)} {Units.Name}";
    static string U(double mil) => Units.Fmt(mil) + " " + Units.Name;

    void Section(string title, Action close, params UIElement[] body)
    {
        if (_p.Children.Count > 0) _p.Children.Add(new Border { Height = 1, Background = Ui.Line });
        bool first = _p.Children.Count == 0;
        var head = new DockPanel { Margin = new Thickness(12, 8, 6, 2) };
        if (close != null)
        {
            var x = new Button { Content = "✕", Width = 26, Height = 24, Padding = new Thickness(0), Foreground = Ui.Dim, ToolTip = "關閉" };
            x.Click += (_, _) => close(); DockPanel.SetDock(x, Dock.Right); head.Children.Add(x);
        }
        if (first)
        {
            var fold = new Button { Content = _collapsed ? "▾" : "▴", Width = 26, Height = 24, Padding = new Thickness(0), Foreground = Ui.Dim, ToolTip = _collapsed ? "展開" : "收合（只留標題）" };
            fold.Click += (_, _) => { _collapsed = !_collapsed; Refresh(); }; DockPanel.SetDock(fold, Dock.Right); head.Children.Add(fold);
        }
        head.Children.Add(new TextBlock { Text = title, Foreground = Ui.Dim, FontSize = 12, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
        _p.Children.Add(head);
        if (_collapsed) { head.Margin = new Thickness(12, 6, 6, 6); return; }
        var sp = new StackPanel { Margin = new Thickness(12, 0, 12, 10) };
        foreach (var e in body) if (e != null) sp.Children.Add(e);
        _p.Children.Add(sp);
    }

    static TextBlock Title(string main, string sub)
    {
        var t = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) };
        t.Inlines.Add(new System.Windows.Documents.Run(main) { FontSize = 20, FontWeight = FontWeights.Bold, Foreground = Ui.Text });
        if (!string.IsNullOrEmpty(sub)) { t.Inlines.Add(new System.Windows.Documents.LineBreak()); t.Inlines.Add(new System.Windows.Documents.Run(sub) { FontSize = 12.5, Foreground = Ui.Dim }); }
        return t;
    }

    static WrapPanel Acts(params (string label, Action a)[] items)
    {
        var w = new WrapPanel { Margin = new Thickness(0, 2, 0, 0) };
        foreach (var (l, a) in items) if (l != null) { var b = Ui.Btn(l, a); b.FontSize = 12.5; w.Children.Add(b); }
        return w;
    }

    static TextBlock Muted(string s) => new() { Text = s, Foreground = Ui.Dim, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 4) };

    /// <summary>Everything the card says, one line per text (automation).</summary>
    public string Text()
    {
        var o = new List<string>();
        void Walk(object x)
        {
            if (x is UIElement { Visibility: not Visibility.Visible }) return;
            if (x is TextBlock tb) { o.Add(tb.Inlines.Count > 0 ? string.Concat(tb.Inlines.Select(i => i is System.Windows.Documents.Run run ? run.Text : " ")) : tb.Text); return; }
            if (x is TextBox tx) { o.Add(tx.Text); return; }
            if (x is DependencyObject d) foreach (var c in LogicalTreeHelper.GetChildren(d)) Walk(c);
        }
        Walk(_p);
        return string.Join("\n", o);
    }

    public void Refresh()
    {
        _p.Children.Clear(); _measShape = null;
        var st = _v.St;
        if (_v.Tool == "measure" && !_v.Is3D) AddMeasure(st);
        if (st.HlNets.Count + st.HlComps.Count > 0) AddHighlight(st);
        var s = st.Sel;
        if (s?.Items != null) AddMulti(s);
        else if (s is { IsComp: true }) AddComp(s.Index);
        else if (s != null && s.Index >= 0 && s.Index < _v.Scene.Objs.Count) AddObj(_v.Scene.Objs[s.Index]);
        Visibility = _p.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // The measure section is built once and then updated in place while the cursor moves (RefreshMeasure):
    // rebuilding the whole card on every mouse move, with its read-only text boxes and the selected part's
    // pad → NET buttons, took 40–110 ms per move on a large board and made measuring stutter. While the cursor
    // moves the section also keeps the same lines (no line comes and goes with the snap or with Shift), so the
    // card does not change height: a long card would otherwise toggle its scroll bar and be laid out again.
    string _measShape;                   // what the section is made of; null: no measure section in the card
    System.Windows.Documents.Run _measNum;
    Grid _measKv;
    TextBlock _measSnap;

    string MeasShape(MeasureState m) =>
        $"{m.A != null && (m.B ?? m.Live) != null}|{m.B == null}|{m.AInfo is { Length: > 0 }}|{m.B != null && m.BInfo is { Length: > 0 }}|{m.B != null && m.Ortho}|{Units.Name}|{_collapsed}";

    /// <summary>Distance and the rows of the card. While the end point follows the cursor, 終點吸附 is always
    /// there (— when nothing is snapped) and the Shift lock is shown in the 角度 row.</summary>
    (string num, List<(string k, string v)> rows) MeasText(MeasureState m, SKPoint pa, SKPoint pb)
    {
        double dx = pb.X - pa.X, dy = pb.Y - pa.Y, d = Math.Sqrt(dx * dx + dy * dy);
        bool live = m.B == null;
        var rows = new List<(string, string)> { ("ΔX", U(Math.Abs(dx))), ("ΔY", U(Math.Abs(dy))),
            ("角度", $"{Math.Atan2(dy, dx) * 180 / Math.PI:0.00}°" + (live && m.Ortho ? "（Shift 鎖定）" : "")), ("起點", XY(pa.X, pa.Y)), ("終點", XY(pb.X, pb.Y)),
            ("換算", Units.Mm ? $"{d:0.00} mil" : $"{d * 0.0254:0.0000} mm") };
        if (m.AInfo is { Length: > 0 }) rows.Add(("起點吸附", m.AInfo));
        if (live || m.BInfo is { Length: > 0 }) rows.Add(("終點吸附", m.BInfo is { Length: > 0 } ? m.BInfo : "—"));
        return (Units.Fmt(d), rows);
    }

    void AddMeasure(PcbViewState st)
    {
        var m = st.Meas; var a = m.A; var b = m.B ?? m.Live;
        _measShape = MeasShape(m); _measNum = null; _measKv = null;
        _measSnap = Muted("目前吸附：" + (m.SnapInfo is { Length: > 0 } si ? si : "—"));
        if (a is not { } pa || b is not { } pb)
        {
            Section("量測距離", () => _v.SetTool("select"),
                Muted("點第一點，再點第二點。游標會吸附焊墊、導通孔中心與導線端點；停在焊墊中心時游標會變成圓圈十字。\nShift：鎖定 0° / 45° / 90°\nCtrl：吸附銅箔邊緣（焊墊、導線、鋪銅）\nCtrl+Shift：在鎖定方向上吸附邊緣\nEsc 清除。"),
                _measSnap);
            return;
        }
        var (num, rows) = MeasText(m, pa, pb);
        var big = new TextBlock { FontFamily = new FontFamily("Consolas"), FontSize = 26, Foreground = Ui.Text, Margin = new Thickness(0, 0, 0, 4) };
        _measNum = new System.Windows.Documents.Run(num);
        big.Inlines.Add(_measNum); big.Inlines.Add(new System.Windows.Documents.Run(" " + Units.Name) { FontSize = 13, Foreground = Ui.Dim });
        _measKv = m.B == null ? LiveKV(rows) : Ui.KV(rows, Body, 76);   // copyable text boxes once the end point is fixed
        int lockAng = (int)((Math.Round(Math.Atan2(pb.Y - pa.Y, pb.X - pa.X) * 180 / Math.PI) % 180 + 180) % 180);
        Section("量測距離", () => _v.SetTool("select"), big, _measKv,
            m.B != null && m.Ortho ? Muted($"Shift 鎖定：{lockAng}° 方向") : null,
            m.B == null ? Muted("點擊以固定終點。Shift 鎖定方向，Ctrl 吸附邊緣，可同時按。") : null);
    }

    /// <summary>The cursor moved with the measure tool: update the measure numbers in place; the rest of the
    /// card stays as it is. Falls back to a full Refresh when the section has to change its make-up.</summary>
    public void RefreshMeasure()
    {
        var m = _v.St.Meas;
        if (_v.Tool != "measure" || _v.Is3D || _measShape == null || _measShape != MeasShape(m)) { Refresh(); return; }
        _measSnap.Text = "目前吸附：" + (m.SnapInfo is { Length: > 0 } si ? si : "—");
        if (m.A is not { } pa || (m.B ?? m.Live) is not { } pb || _measKv == null) return;
        var (num, rows) = MeasText(m, pa, pb);
        _measNum.Text = num;
        for (int i = 0; i < rows.Count && 2 * i + 1 < _measKv.Children.Count; i++)
            if (_measKv.Children[2 * i + 1] is TextBlock tb && tb.Text != rows[i].v) tb.Text = rows[i].v;
    }

    /// <summary>Ui.KV with plain text for the values: text that changes on every mouse move is much cheaper in a
    /// TextBlock than in a read-only TextBox.</summary>
    static Grid LiveKV(List<(string k, string v)> rows)
    {
        var g = new Grid { Margin = new Thickness(0, 2, 0, 6) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(76) }); g.ColumnDefinitions.Add(new ColumnDefinition());
        for (int r = 0; r < rows.Count; r++)
        {
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var kt = new TextBlock { Text = rows[r].k, Foreground = Ui.Dim, FontSize = Body - 0.5, Margin = new Thickness(0, 1, 6, 1), VerticalAlignment = VerticalAlignment.Top };
            var vt = new TextBlock { Text = rows[r].v, Foreground = Ui.Text, FontSize = Body, Padding = new Thickness(2, 1, 2, 1), TextWrapping = TextWrapping.Wrap };
            Grid.SetRow(kt, r); Grid.SetRow(vt, r); Grid.SetColumn(vt, 1); g.Children.Add(kt); g.Children.Add(vt);
        }
        return g;
    }

    void AddHighlight(PcbViewState st)
    {
        var chips = st.HlNets.Select(n => (Bd.NetName(n), (Action)(() => _v.UserHighlightNet(n))))
            .Concat(st.HlComps.Select(c => (Bd.Comps[c].Des, (Action)(() => _v.UserHighlightComp(c)))));
        Section("點亮中", _v.ClearHighlight, Ui.Chips(chips, 40, 12), Muted("Ctrl+Shift+點選可加入更多 NET／元件"),
            Acts(("縮放到點亮物件", _v.ZoomToHighlight), ("清除", _v.ClearHighlight)));
    }

    void AddMulti(PcbSelection s)
    {
        var comps = s.Items.Where(i => i.IsComp).Select(i => i.Index).OrderBy(i => Bd.Comps[i].Des, Natural).ToList();
        var nets = comps.SelectMany(c => Bd.Pads.Where(p => p.Comp == c && p.Net != Layers.None).Select(p => p.Net))
            .Concat(s.Items.Where(i => !i.IsComp && i.Index >= 0).Select(i => _v.Scene.Objs[i.Index].Net).Where(n => n != Layers.None)).Distinct().OrderBy(n => Bd.NetName(n), Natural).ToList();
        var objs = s.Items.Where(i => !i.IsComp && i.Index >= 0).Select(i => _v.Scene.Objs[i.Index]).ToList();
        var body = new List<UIElement> { Title($"{s.Items.Count} 個物件", "Shift 拖曳加選 · Ctrl 拖曳減選 · Shift+點擊單一物件") };
        var kv = new List<(string, string)>();
        if (comps.Count > 0) kv.Add(("元件", comps.Count.ToString()));
        foreach (var f in PcbView.Filters.Where(f => f.k != PickClass.Comp)) { int n = objs.Count(o => o.Class == f.k); if (n > 0) kv.Add((f.zh, n.ToString())); }
        double len = objs.Sum(o => o.Src switch { PcbTrack t => Math.Sqrt((t.X2 - t.X1) * (t.X2 - t.X1) + (t.Y2 - t.Y1) * (t.Y2 - t.Y1)), PcbArc a => a.R * ((((a.Ea - a.Sa) % 360 + 360) % 360) is var sw && sw < 1e-9 ? 360 : sw) * Math.PI / 180, _ => 0 });
        if (len > 0) kv.Add(("導線總長", U(len)));
        kv.Add(("NET", nets.Count.ToString()));
        body.Add(Ui.KV(kv, Body, 76));
        if (comps.Count > 0) { body.Add(Ui.Header($"元件 {comps.Count}", 12)); body.Add(Ui.Chips(comps.Select(i => (Bd.Comps[i].Des, (Action)(() => { _v.SetSelection(PcbSelection.Comp(i), true); _v.ZoomToSelection(); }))), 80, 12)); }
        if (nets.Count > 0) { body.Add(Ui.Header($"NET {nets.Count}", 12)); body.Add(Ui.Chips(nets.Select(n => (Bd.NetName(n), (Action)(() => _v.UserHighlightNet(n)))), 40, 12)); }
        body.Add(Acts(("點亮所選", () => { _v.St.HlNets.Clear(); _v.St.HlComps.Clear(); foreach (var c in comps) _v.St.HlComps.Add(c); foreach (var o in objs) if (o.Net != Layers.None) _v.St.HlNets.Add(o.Net); else if (o.Comp != Layers.None) _v.St.HlComps.Add(o.Comp); _v.HighlightComp(-1, true); }),
            ("縮放到所選", _v.ZoomToSelection),
            comps.Count > 0 ? ("複製位號", () => { try { Clipboard.SetText(string.Join(", ", comps.Select(i => Bd.Comps[i].Des))); } catch { } }) : (null, null)));
        Section("多重選取", () => _v.SetSelection(null, true), body.ToArray());
    }

    void AddComp(int ci)
    {
        var c = Bd.Comps[ci];
        var pads = Bd.Pads.Where(p => p.Comp == ci).OrderBy(p => p.Name, Natural).ToList();
        var nets = pads.Where(p => p.Net != Layers.None).Select(p => p.Net).Distinct().OrderBy(n => Bd.NetName(n), Natural).ToList();
        var rows = new List<(string, string)> { ("面", c.Bottom ? "Bottom" : "Top"), ("位置", XY(c.X, c.Y)), ("旋轉", $"{c.Rot:0.##}°") };
        if (c.Comment != "") rows.Add(("Comment", c.Comment));
        rows.Add(("封裝", c.Pattern));
        if (c.LibRef != "" && c.LibRef != c.Pattern) rows.Add(("Lib Ref", c.LibRef));
        rows.Add(("焊墊", pads.Count.ToString()));
        if (_v.Scene.CompBox(ci) is { } bb) rows.Add(("外框", $"{Units.Fmt(bb.Width)} × {Units.Fmt(bb.Height)} {Units.Name}"));
        if (c.Height > 0) rows.Add(("高度", U(c.Height)));
        rows.Add(("NET", nets.Count.ToString()));
        var body = new List<UIElement> { Title(c.Des == "" ? "#" + ci : c.Des, string.Join(" · ", new[] { c.Comment, c.Pattern }.Where(x => x != ""))), Ui.KV(rows, Body, 70) };
        if (pads.Count > 0) { body.Add(Ui.Header($"焊墊 → NET（點選可點亮）", 12)); body.Add(Ui.Chips(pads.Select(p => ($"{p.Name}: {(p.Net != Layers.None ? Bd.NetName(p.Net) : "—")}", (Action)(() => { if (p.Net != Layers.None) _v.UserHighlightNet(p.Net); }))), 60, 12)); }
        var v3 = _v.Is3D ? _v.View3 : null;
        body.Add(Acts(("點亮元件", () => _v.UserHighlightComp(ci)), ("縮放到元件", _v.ZoomToSelection), ("複製位號", () => { try { Clipboard.SetText(c.Des); } catch { } }),
            v3 != null && v3.HasPart(ci) ? (v3.IsHidden(ci) ? "顯示此元件" : "隱藏此元件 (H)", () => { v3.SetHidden(ci, !v3.IsHidden(ci)); Refresh(); }) : (null, null)));
        Section("元件 Component", () => _v.SetSelection(null, true), body.ToArray());
    }

    void AddObj(PcbObj o)
    {
        string net = o.Net != Layers.None ? Bd.NetName(o.Net) : null;
        var comp = o.Comp != Layers.None && o.Comp < Bd.Comps.Count ? Bd.Comps[o.Comp] : null;
        var rows = new List<(string, string)>();
        string kind;
        switch (o.Src)
        {
            case PcbPad p:
                kind = "焊墊 " + p.Name; var sz = p.L == Layers.Bottom && p.Mode != 0 ? p.Bot : p.Top;
                rows.AddRange(new[] { ("位置", XY(p.X, p.Y)), ("尺寸", $"{Units.Fmt(sz.Sx)} × {Units.Fmt(sz.Sy)} {Units.Name}"),
                    ("形狀", sz.Alt == 9 ? $"圓角矩形 {sz.Cr}%" : sz.Shape switch { 1 => "圓形", 2 => "矩形", 3 => "八角形", _ => sz.Shape.ToString() }), ("旋轉", $"{p.Rot:0.##}°"),
                    ("孔徑", p.Hole > 0 ? U(p.Hole) + (p.HoleShape == 2 ? $"（槽孔 {Units.Fmt(p.Slot)}）" : "") + (p.Plated ? " · PTH" : " · NPTH") : "SMD") });
                break;
            case PcbVia vv: kind = "導通孔 Via"; rows.AddRange(new[] { ("位置", XY(vv.X, vv.Y)), ("直徑", U(vv.D)), ("孔徑", U(vv.Hole)), ("層跨", $"{Bd.LayerName(vv.LStart)} → {Bd.LayerName(vv.LEnd)}") }); break;
            case PcbTrack t:
                kind = "導線 Track"; double len = Math.Sqrt((t.X2 - t.X1) * (t.X2 - t.X1) + (t.Y2 - t.Y1) * (t.Y2 - t.Y1));
                rows.AddRange(new[] { ("起點", XY(t.X1, t.Y1)), ("終點", XY(t.X2, t.Y2)), ("線寬", U(t.W)), ("長度", U(len)) }); break;
            case PcbArc a: kind = "圓弧 Arc"; rows.AddRange(new[] { ("圓心", XY(a.Cx, a.Cy)), ("半徑", U(a.R)), ("線寬", U(a.W)), ("角度", $"{a.Sa:0.#}° → {a.Ea:0.#}°") }); break;
            case PcbFill f: kind = "填充 Fill"; rows.AddRange(new[] { ("角 1", XY(f.X1, f.Y1)), ("角 2", XY(f.X2, f.Y2)), ("旋轉", $"{f.Rot:0.#}°") }); break;
            case PcbRegion g: kind = g.Poly != Layers.None ? "鋪銅 Polygon" : "區域 Region"; rows.AddRange(new[] { ("頂點", g.Outline.Count.ToString()), ("挖空", g.Holes.Count.ToString()) }); if (g.Keepout) rows.Add(("Keepout", "是")); break;
            case PcbText tx: kind = "文字 Text"; rows.AddRange(new[] { ("內容", tx.Text), ("位置", XY(tx.X, tx.Y)), ("字高", U(tx.H)), ("旋轉", $"{tx.Rot:0.#}°"), ("字型", tx.Ttf ? tx.Font : "Stroke") }); if (tx.Mirror) rows.Add(("鏡射", "是")); break;
            default: kind = o.Kind.ToString(); break;
        }
        if (net != null) rows.Add(("NET", net));
        if (comp != null) { rows.Add(("元件", comp.Des + (comp.Comment != "" ? " · " + comp.Comment : ""))); rows.Add(("封裝", comp.Pattern)); }
        Section(Bd.LayerName(o.L), () => _v.SetSelection(null, true),
            Title(kind, comp != null ? comp.Des + " · " + comp.Pattern : null),
            Ui.KV(rows, Body, 70),
            Acts(net != null ? ("點亮 NET", () => _v.UserHighlightNet(o.Net)) : (null, null),
                 comp != null ? ($"點亮元件 {comp.Des}", () => _v.UserHighlightComp(o.Comp)) : (null, null),
                 _v.Is3D ? (null, null) : ("設為目前層", () => _v.SetCurrentLayer(o.L)),
                 ("縮放到所選", _v.ZoomToSelection)));
    }
}
