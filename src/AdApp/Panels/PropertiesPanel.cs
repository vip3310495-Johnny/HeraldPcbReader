// Herald PCB Reader — Copyright (C) 2026 Johnny.ch.Tsai
// SPDX-License-Identifier: GPL-3.0-only
// Free software under the GNU General Public License version 3 (LICENSE.txt), WITHOUT ANY WARRANTY; with an
// additional permission to combine it with AvalonDock (Ms-PL), see NOTICE.txt.
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AdCore.Project;
using AdCore.Sch;
using AdApp.Views;
using static AdCore.Util.TextUtil;

namespace AdApp.Panels;

/// <summary>AD "Properties" panel for schematics: the selected part / net / parts, or the document summary.
/// (PCB information is shown in the card on the PCB itself.)</summary>
public sealed class PropertiesPanel : ScrollViewer
{
    readonly StackPanel _p = new() { Margin = new Thickness(12, 8, 12, 12) };
    public event Action<string> GoRef;                       // select component in current schematic
    public event Action<string> GoNet;                       // select local net in current schematic
    public event Action<DocItem, string> GoDocRef;           // open doc and select ref (null = just open)
    public event Action<DocItem, int> GoPage;                // a page of the selected net (file, sheet)
    public event Action ToggleMask, ZoomSel;

    /// <summary>Font sizes (Settings): the big title, and everything else.</summary>
    public static double TitleSize = 24, BodySize = 16;
    static double Small => Math.Max(9, BodySize - 2);
    Action _last;

    public PropertiesPanel() { Background = Ui.Bg2; VerticalScrollBarVisibility = ScrollBarVisibility.Auto; Content = _p; }

    /// <summary>Show the last content again (after a font-size change).</summary>
    public void Refresh() => _last?.Invoke();

    // Readability: names in a light slate, values in white semibold (both easy to read, told apart at a glance),
    // striped rows, blue underlined section headers, brighter links and notes than the general Dim grey.
    static readonly Brush KeyBrush = Ui.Br(0xB3, 0xBE, 0xCC), ValueBrush = Ui.Br(0xFF, 0xFF, 0xFF), RowAlt = Ui.Br(0x34, 0x34, 0x3A),
        HeadBrush = Ui.Br(0x7D, 0xB9, 0xF7), NoteBrush = Ui.Br(0xB8, 0xB8, 0xC0), LinkBrush = Ui.Br(0x6C, 0xB6, 0xFF);

    void Clear() => _p.Children.Clear();
    void Add(UIElement e) => _p.Children.Add(e);
    void Title(string main, string kind)
    {
        var t = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
        t.Inlines.Add(new System.Windows.Documents.Run(main) { FontSize = TitleSize, FontWeight = FontWeights.SemiBold, Foreground = Ui.TitleBlue });
        if (!string.IsNullOrEmpty(kind)) t.Inlines.Add(new System.Windows.Documents.Run("  " + kind) { FontSize = Small, Foreground = NoteBrush });
        Add(t);
    }
    void Header(string s) => Add(new Border
    {
        BorderBrush = Ui.Line, BorderThickness = new Thickness(0, 0, 0, 1), Margin = new Thickness(0, 12, 0, 4), Padding = new Thickness(0, 0, 0, 3),
        Child = new TextBlock { Text = s, Foreground = HeadBrush, FontSize = Small + 0.5, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap },
    });

    /// <summary>Striped name / value rows; values stay selectable (copy a part number or a description).</summary>
    void KV(IEnumerable<(string, string)> rows)
    {
        var g = new Grid { Margin = new Thickness(0, 2, 0, 8) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(96, BodySize * 7)) });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        int r = 0;
        foreach (var (k, v) in rows)
        {
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Stripe(g, r, 2);
            var kt = new TextBlock { Text = k, Foreground = KeyBrush, FontSize = BodySize - 1, Margin = new Thickness(6, 3, 8, 3), TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Top };
            Grid.SetRow(kt, r); g.Children.Add(kt);
            var vt = new TextBox { Text = v ?? "", IsReadOnly = true, BorderThickness = new Thickness(0), Background = Brushes.Transparent, Foreground = ValueBrush, FontWeight = FontWeights.SemiBold,
                Padding = new Thickness(0, 3, 6, 3), FontSize = BodySize, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Top };
            Grid.SetRow(vt, r); Grid.SetColumn(vt, 1); g.Children.Add(vt);
            r++;
        }
        Add(g);
    }
    static void Stripe(Grid g, int row, int cols)
    {
        if (row % 2 == 0) return;
        var bg = new Border { Background = RowAlt, CornerRadius = new CornerRadius(3) };
        Grid.SetRow(bg, row); Grid.SetColumnSpan(bg, cols); g.Children.Add(bg);
    }
    WrapPanel Chips(IEnumerable<(string label, Action a)> items, int max = 60) => Ui.Chips(items, max, Math.Max(9, BodySize - 1.5));
    StackPanel Buttons(params (string label, Action a)[] items)
    {
        var r = Ui.Row(items.Select(i => { var b = Ui.Btn(i.label, i.a); b.FontSize = Math.Max(9, BodySize - 1.5); return (UIElement)b; }).ToArray());
        r.Margin = new Thickness(0, 0, 0, 6); return r;
    }
    TextBlock Note(string s, Brush b = null) => new() { Text = s, Foreground = b ?? NoteBrush, FontSize = Small, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 2) };

    public void ShowNothing(string msg = "選取物件以檢視屬性")
    {
        _last = () => ShowNothing(msg);
        Clear(); Add(Note(msg));
    }

    // ---------- schematic ----------
    public void ShowSchDoc(DocItem d, int sheet)
    {
        _last = () => ShowSchDoc(d, sheet);
        Clear(); if (d?.Sch == null) { Add(Note("選取物件以檢視屬性")); return; }
        var doc = d.Sch; var sh = doc.Sheets[Math.Clamp(sheet, 0, doc.Sheets.Count - 1)];
        Title(System.IO.Path.GetFileNameWithoutExtension(d.Name), "電路圖");
        KV(new[] { ("格式", doc.FmtLabel), ("檔案", d.Path), ("群組", d.Group?.Name), ("圖紙", $"{doc.Sheets.Count}（目前：{sh.Name}）"), ("紙張", sh.Page != null ? $"{sh.Page.Name}  {sh.Page.W:0.#} × {sh.Page.H:0.#} mm" : "—"),
            ("零件", doc.Parts.Count.ToString()), ("網路", doc.Nets.Count.ToString()) });
        var tk = sh.Title.Where(kv => !string.IsNullOrWhiteSpace(kv.Value)).ToList();
        if (tk.Count > 0)
        {
            var nm = new Dictionary<string, string> { ["title"] = "標題", ["date"] = "日期", ["rev"] = "版次", ["company"] = "公司", ["sheet"] = "頁次" };
            Header("標題欄"); KV(tk.Select(kv => (nm.TryGetValue(kv.Key, out var n) ? n : "註解", kv.Value)));
        }
        if (sh.UnconnectedPins.Count > 0)
        {
            Header($"未連接的接腳（{sh.UnconnectedPins.Count}）");
            Add(Chips(sh.UnconnectedPins.Select(u => ($"{u.Ref}.{u.Pin}", (Action)(() => GoRef?.Invoke(u.Ref))))));
        }
        if (doc.Warnings.Count > 0) { Header("提示"); foreach (var w in doc.Warnings) Add(Note("• " + w, Ui.Warn)); }
    }

    public void ShowSchSel(DocItem d, SchSel sel, int sheet)
    {
        if (sel == null || d?.Sch == null) { ShowSchDoc(d, sheet); return; }
        _last = () => ShowSchSel(d, sel, sheet);
        Clear();
        var doc = d.Sch;
        if (sel.Refs != null)
        {
            var parts = sel.Refs.Select(r => doc.PartMap.TryGetValue(r, out var p) ? p : null).Where(p => p != null).ToList();
            Title($"{sel.Refs.Count} 個零件", "框選");
            Add(Buttons(("縮放到所選", () => ZoomSel?.Invoke())));
            Add(Note("左→右拖曳：完全在框內的零件　右→左拖曳：碰到框的零件"));
            Add(Chips(sel.Refs.OrderBy(r => r, Natural).Select(r => (r, (Action)(() => GoRef?.Invoke(r)))), 200));
            var byVal = parts.GroupBy(p => (p.Value, p.Footprint)).OrderByDescending(g => g.Count()).Take(30).ToList();
            if (byVal.Count > 0) { Header("依值統計"); KV(byVal.Select(g => ($"{g.Count()} ×", $"{g.Key.Value}  {g.Key.Footprint}".Trim()))); }
            return;
        }
        if (!sel.IsNet)
        {
            if (!doc.PartMap.TryGetValue(sel.Ref, out var p)) { ShowSchDoc(d, sheet); return; }
            Title(p.Ref, "零件");
            Add(Buttons(("定位", () => ZoomSel?.Invoke()), (sel.Mask ? "取消只看" : "只看此零件", () => ToggleMask?.Invoke())));
            var rows = new List<(string, string)> { ("值", p.Value), ("封裝", p.Footprint), ("元件庫", p.Lib) };
            if (p.Description != "") rows.Add(("描述", p.Description));
            if (p.Datasheet != "") rows.Add(("規格書", p.Datasheet));
            if (p.Units.Count > 0) rows.Add(("單元", string.Join("、", p.Units.Distinct())));
            if (doc.Sheets.Count > 1) rows.Add(("頁面", string.Join("、", p.Sheets.Select(s => doc.Sheets[s].Name))));
            if (!p.InBom) rows.Add(("BOM", "不列入"));
            if (p.Dnp) rows.Add(("DNP", "不上件"));
            KV(rows);
            if (p.Fields.Count > 0) { Header("參數"); KV(p.Fields.Where(f => f.Key != "Description").Select(f => (f.Key, PlainText(f.Val)))); }
            var pins = p.Pins.Where(q => !q.Hidden || q.Net != null).ToList();
            if (pins.Count > 0)
            {
                Header($"接腳（{pins.Count}）");
                var g = new Grid();
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(BodySize * 3.4) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.3, GridUnitType.Star) });
                int r = 0; double fs = Math.Max(9, BodySize - 1);
                foreach (var q in pins)
                {
                    g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                    Stripe(g, r, 3);
                    var a = Ui.T(q.Num, KeyBrush, fs, false, new Thickness(6, 2, 4, 2)); Grid.SetRow(a, r); g.Children.Add(a);
                    var b = Ui.T(PlainText(q.Name) is { Length: > 0 } nm ? nm : "—", ValueBrush, fs, true, new Thickness(0, 2, 4, 2)); Grid.SetRow(b, r); Grid.SetColumn(b, 1); g.Children.Add(b);
                    UIElement c;
                    if (q.Net != null) { var btn = new Button { Content = PlainText(q.Net), Padding = new Thickness(3, 0, 3, 0), HorizontalAlignment = HorizontalAlignment.Left, Foreground = LinkBrush, FontSize = fs }; var net = q.Net; btn.Click += (_, _) => GoNet?.Invoke(net); c = btn; }
                    else c = Ui.T(q.Nc ? "NC 標記" : q.Type == "no_connect" ? "NC 腳" : "未連接", NoteBrush, fs, false, new Thickness(3, 2, 0, 2));
                    Grid.SetRow(c, r); Grid.SetColumn(c, 2); g.Children.Add(c);
                    r++;
                }
                Add(g);
            }
            return;
        }
        if (!doc.NetMap.TryGetValue(sel.Net ?? "", out var n)) { ShowSchDoc(d, sheet); return; }
        var P = sel.PNet; var G = d.Group;
        Title(PlainText(n.Name), "網路");
        Add(Buttons(("定位", () => ZoomSel?.Invoke()), (sel.Mask ? "取消只看" : "只看此網路", () => ToggleMask?.Invoke())));
        KV(new[] { ("節點", $"{n.Members.Count} 個接腳") });
        if (n.Members.Count > 0) Add(Chips(n.Members.Select(m => ($"{m.Ref}.{m.Pin}" + (m.PinName != null && m.PinName != m.Pin && m.PinName != "" ? " " + PlainText(m.PinName) : ""), (Action)(() => GoRef?.Invoke(m.Ref))))));
        // every page the net is on, the one shown now inverted
        var pages = NetPage.Of(d, sel);
        if (pages.Count > 1)
        {
            Header($"頁面（{pages.Count}）");
            var wp = new WrapPanel();
            foreach (var pg in pages)
            {
                bool here = pg.Doc == d && pg.Sheet == sheet;
                var b = NetPage.Button(pg.Label, here, Math.Max(9, BodySize - 2));
                b.ToolTip = here ? "目前頁面" : "切到 " + pg.Label;
                b.Click += (_, _) => { if (here) ZoomSel?.Invoke(); else GoPage?.Invoke(pg.Doc, pg.Sheet); };
                wp.Children.Add(b);
            }
            Add(wp);
        }
        if (P != null && P.FileCount <= 1 && G?.Kind == GroupKind.Project && G.Compiled != null)
        {
            var want = new HashSet<string>(new[] { n.Name.ToLowerInvariant(), P.Name.ToLowerInvariant() });
            var hits = G.SchDocs.Where(x => x != d && x.Sch.Nets.Any(nn => want.Contains(nn.Name.ToLowerInvariant()))).Select(x => x.Name).Distinct().ToList();
            if (hits.Count > 0)
            {
                var eff = G.Compiled.Eff;
                var why = eff is "hier" or "strict" ? "網路標籤只在單一圖紙有效，跨圖紙要用 Port 對應上層 Sheet Entry" + (eff == "hier" ? "，或用電源埠" : "") + "。"
                    : eff == "flat" ? "網路標籤只在單一圖紙有效，跨圖紙要用同名 Port 或電源埠。" : "請確認兩邊是同一類網路識別（網路標籤、Port、電源埠）。";
                Header("為什麼沒有連到其他圖紙");
                Add(Note($"{string.Join("、", hits)} 也有同名網路，但依專案的網路範圍「{CompiledProject.ScopeLabel[eff].Split('（')[0]}」不相連：{why}這與 Altium 編譯的結果一致；可在 Projects 面板對專案按右鍵改「網路範圍」試看看。", Ui.Warn));
            }
        }
        if (P != null && P.FileCount > 1)
        {
            Header($"{(G?.Kind == GroupKind.Project ? "專案網路" : "群組網路")} {PlainText(P.Name)}：{P.Members.Count} 腳 · {P.FileCount} 張電路圖");
            foreach (var grp in P.Locals.GroupBy(l => l.DocIndex).OrderBy(g => g.Key))
            {
                var od = G.CompiledDoc(grp.Key); if (od == null) continue;
                bool here = od == d;
                UIElement head;
                if (here)
                    head = new Border { Background = Ui.Jump, CornerRadius = new CornerRadius(2), Padding = new Thickness(4, 0, 4, 1), HorizontalAlignment = HorizontalAlignment.Left,
                        Child = Ui.T(od.Name + "（目前）", NetPage.CurrentText, Math.Max(9, BodySize - 1), true) };
                else
                {
                    var link = new Button { Content = od.Name, Foreground = LinkBrush, Padding = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Left, FontSize = Math.Max(9, BodySize - 1) };
                    var first = grp.Min(l => l.Sheet);
                    link.Click += (_, _) => GoPage?.Invoke(od, first);
                    head = link;
                }
                Add(Ui.Row(head, Ui.T("  " + string.Join("、", grp.Select(l => l.Name).Distinct()), NoteBrush, Small)));
                Add(Chips(P.Members.Where(m => m.DocIndex == grp.Key).Select(m => ($"{m.Ref}.{m.Pin}", (Action)(() => { if (here) GoRef?.Invoke(m.Ref); else GoDocRef?.Invoke(od, m.Ref); }))), 40));
            }
        }
    }
}
