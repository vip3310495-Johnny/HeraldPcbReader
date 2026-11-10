// Herald PCB Reader — Copyright (C) 2026 Johnny.ch.Tsai
// SPDX-License-Identifier: GPL-3.0-only
// Free software under the GNU General Public License version 3 (LICENSE.txt), WITHOUT ANY WARRANTY; with an
// additional permission to combine it with AvalonDock (Ms-PL), see NOTICE.txt.
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using AdCore.Project;
using AdCore.Sch;
using static AdCore.Util.TextUtil;

namespace AdApp.Panels;

public sealed class NavComp { public string Ref { get; set; } public string Value { get; set; } public string Footprint { get; set; } public string Where { get; set; } public DocItem Doc; }
public sealed class NavNet
{
    public string Name { get; set; } public int Pins { get; set; } public string Where { get; set; }
    public ProjectNet PNet; public DocItem Doc; public string Local; public int PcbNet = -1;
}
public sealed class BomRow { public string Refs { get; set; } public int Qty { get; set; } public string Value { get; set; } public string Footprint { get; set; } public string Description { get; set; } }

/// <summary>AD "Navigator"-like panel: components / nets / BOM of the active project (or document).</summary>
public sealed class NavigatorPanel : DockPanel
{
    readonly TextBox _q = new() { Margin = new Thickness(6), ToolTip = "搜尋位號、值或網路（Ctrl+F）" };
    readonly TextBlock _scope = Ui.T("", Ui.Dim, 11, false, new Thickness(8, 0, 8, 4));
    readonly TabControl _tabs = new();
    readonly ListView _comps = new(), _nets = new(), _bom = new();
    readonly RadioButton _bomList = new() { Content = "逐顆", IsChecked = false }, _bomGroup = new() { Content = "BOM 合併", IsChecked = true };
    List<NavComp> _allComps = new(); List<NavNet> _allNets = new(); List<SchPart> _parts = new();
    ProjectGroup _group; DocItem _doc;

    public event Action<NavComp> CompPicked;
    public event Action<NavNet> NetPicked;
    public event Action<string> Toast;

    public NavigatorPanel()
    {
        Background = Ui.Bg2;
        var top = new StackPanel(); top.Children.Add(_q); top.Children.Add(_scope);
        DockPanel.SetDock(top, Dock.Top); Children.Add(top);
        _q.TextChanged += (_, _) => Filter();

        Grid(_comps, ("位號", "Ref", 70), ("值", "Value", 110), ("封裝", "Footprint", 120), ("位置", "Where", 140));
        Grid(_nets, ("網路", "Name", 150), ("腳數", "Pins", 46), ("圖紙", "Where", 160));
        Grid(_bom, ("數量", "Qty", 44), ("值", "Value", 110), ("位號", "Refs", 180), ("封裝", "Footprint", 110), ("描述", "Description", 160));
        _comps.SelectionChanged += (_, _) => { if (_comps.SelectedItem is NavComp c) CompPicked?.Invoke(c); };
        _nets.SelectionChanged += (_, _) => { if (_nets.SelectedItem is NavNet n) NetPicked?.Invoke(n); };

        _tabs.Items.Add(new TabItem { Header = "元件", Content = _comps });
        _tabs.Items.Add(new TabItem { Header = "網路", Content = Wrap(_nets, Ui.Btn("複製網路表（Markdown）", CopyNets), Ui.Btn("跨圖紙網路", CopyCross, "複製跨圖紙網路表（Markdown）")) });
        _bomList.Checked += (_, _) => FillBom(); _bomGroup.Checked += (_, _) => FillBom();
        var bomTools = Ui.Row(_bomGroup, _bomList, Ui.Btn("複製 BOM（Markdown）", CopyBom));
        _tabs.Items.Add(new TabItem { Header = "BOM", Content = Wrap(_bom, bomTools) });
        Children.Add(_tabs);
    }

    static DockPanel Wrap(UIElement list, params UIElement[] tools)
    {
        var d = new DockPanel();
        var bar = new WrapPanel { Margin = new Thickness(6, 4, 6, 4) }; foreach (var t in tools) bar.Children.Add(t);
        DockPanel.SetDock(bar, Dock.Top); d.Children.Add(bar); d.Children.Add(list);
        return d;
    }

    static void Grid(ListView lv, params (string h, string p, double w)[] cols)
    {
        var gv = new GridView();
        foreach (var (h, p, w) in cols) gv.Columns.Add(new GridViewColumn { Header = h, Width = w, DisplayMemberBinding = new Binding(p) });
        lv.View = gv;
        VirtualizingPanel.SetIsVirtualizing(lv, true);
    }

    public void Show(ProjectGroup g, DocItem doc)
    {
        _group = g; _doc = doc;
        _allComps = new(); _allNets = new(); _parts = new();
        if (g == null) { _scope.Text = ""; Filter(); FillBom(); return; }
        var schDocs = g.Kind == GroupKind.Free ? (doc?.Sch != null ? new List<DocItem> { doc } : new()) : g.SchDocs.ToList();
        var pcbDoc = g.Kind == GroupKind.Free ? (doc?.Pcb != null ? doc : null) : g.PcbDocs.FirstOrDefault(d => d == doc) ?? g.PcbDocs.FirstOrDefault();
        _scope.Text = g.Kind == GroupKind.Free ? $"範圍：{doc?.Name}" : $"範圍：{(g.Kind == GroupKind.Project ? "專案" : "群組")}「{g.Name}」 · {schDocs.Count} 張電路圖 · {g.PcbDocs.Count()} 塊 PCB";
        var seen = new HashSet<string>();
        foreach (var d in schDocs)
            foreach (var p in d.Sch.Parts)
            {
                if (!seen.Add(p.Ref)) continue;
                _parts.Add(p);
                _allComps.Add(new NavComp { Ref = p.Ref, Value = p.Value, Footprint = p.Footprint, Where = d.Name, Doc = d });
            }
        if (pcbDoc != null)
            foreach (var c in pcbDoc.Pcb.Comps)
                if (c.Des != "" && seen.Add(c.Des)) _allComps.Add(new NavComp { Ref = c.Des, Value = c.Comment, Footprint = c.Pattern, Where = pcbDoc.Name + "（只在 PCB）", Doc = pcbDoc });
        _allComps.Sort((a, b) => NatCmp(a.Ref, b.Ref));

        if (g.Compiled != null)
            foreach (var p in g.Compiled.PNets)
                _allNets.Add(new NavNet { Name = PlainText(p.Name), Pins = p.Members.Count, PNet = p, Where = string.Join("、", p.Locals.Select(l => g.CompiledDoc(l.DocIndex)?.Name).Distinct().Select(n => System.IO.Path.GetFileNameWithoutExtension(n))) });
        else if (doc?.Sch != null)
            foreach (var n in doc.Sch.Nets) _allNets.Add(new NavNet { Name = PlainText(n.Name), Pins = n.Members.Count, Doc = doc, Local = n.Name, Where = string.Join("、", n.Sheets.Select(s => doc.Sch.Sheets[s].Name)) });
        else if (pcbDoc != null)
            for (int i = 0; i < pcbDoc.Pcb.Nets.Count; i++) _allNets.Add(new NavNet { Name = pcbDoc.Pcb.Nets[i], Pins = pcbDoc.Pcb.Pads.Count(p => p.Net == i), PcbNet = i, Doc = pcbDoc, Where = pcbDoc.Name });
        Filter(); FillBom();
    }

    void Filter()
    {
        var q = _q.Text.Trim().ToLowerInvariant();
        _comps.ItemsSource = q == "" ? _allComps : _allComps.Where(c => (c.Ref + " " + c.Value + " " + c.Footprint).ToLowerInvariant().Contains(q)).ToList();
        _nets.ItemsSource = q == "" ? _allNets : _allNets.Where(n => n.Name.ToLowerInvariant().Contains(q)).ToList();
    }

    public void FocusSearch() { _q.Focus(); _q.SelectAll(); }

    List<BomRow> BomRows(bool grouped)
    {
        var parts = _parts.Where(p => p.InBom).ToList();
        if (!grouped) return parts.Select(p => new BomRow { Refs = p.Ref, Qty = 1, Value = p.Value, Footprint = p.Footprint, Description = p.Description }).ToList();
        return parts.GroupBy(p => (p.Value, p.Footprint))
            .Select(g => new BomRow { Refs = string.Join(", ", g.Select(p => p.Ref).OrderBy(r => r, Natural)), Qty = g.Count(), Value = g.Key.Value, Footprint = g.Key.Footprint, Description = g.First().Description })
            .OrderBy(r => r.Refs, Natural).ToList();
    }

    void FillBom() => _bom.ItemsSource = BomRows(_bomGroup.IsChecked == true);

    static string Md(string s) => (s ?? "").Replace("|", "\\|");

    void CopyBom()
    {
        var sb = new StringBuilder("| 數量 | 值 | 位號 | 封裝 | 描述 |\n|---:|---|---|---|---|\n");
        foreach (var r in BomRows(_bomGroup.IsChecked == true)) sb.Append($"| {r.Qty} | {Md(r.Value)} | {Md(r.Refs)} | {Md(r.Footprint)} | {Md(r.Description)} |\n");
        Copy(sb.ToString(), "已複製 BOM（Markdown 表格）");
    }

    void CopyNets()
    {
        var sb = new StringBuilder("| 網路 | 接腳 |\n|---|---|\n");
        foreach (var n in _allNets)
        {
            IEnumerable<string> pins = n.PNet != null ? n.PNet.Members.Select(m => m.Ref + "." + m.Pin)
                : n.Doc?.Sch != null && n.Local != null && n.Doc.Sch.NetMap.TryGetValue(n.Local, out var dn) ? dn.Members.Select(m => m.Ref + "." + m.Pin) : Enumerable.Empty<string>();
            sb.Append($"| {Md(n.Name)} | {Md(string.Join(" ", pins))} |\n");
        }
        Copy(sb.ToString(), "已複製網路表（Markdown 表格）");
    }

    void CopyCross()
    {
        if (_group?.Compiled == null) { Toast?.Invoke("這個範圍沒有專案編譯結果（需要 .PrjPcb 或群組內 2 張以上電路圖）"); return; }
        var sb = new StringBuilder("| 網路 | 圖紙 | 腳數 |\n|---|---|---:|\n");
        foreach (var p in _group.Compiled.PNets.Where(p => p.FileCount > 1))
            sb.Append($"| {Md(PlainText(p.Name))} | {Md(string.Join("、", p.Locals.Select(l => _group.CompiledDoc(l.DocIndex)?.Name).Distinct()))} | {p.Members.Count} |\n");
        Copy(sb.ToString(), "已複製跨圖紙網路表（Markdown 表格）");
    }

    void Copy(string s, string msg) { try { Clipboard.SetText(s); Toast?.Invoke(msg); } catch (Exception e) { Toast?.Invoke("複製失敗：" + e.Message); } }

    /// <summary>Global search results for the toolbar box (components first, then nets).</summary>
    public IEnumerable<object> Search(string q, int max = 40)
    {
        q = q.Trim().ToLowerInvariant(); if (q == "") return Enumerable.Empty<object>();
        var c = _allComps.Where(x => (x.Ref + " " + x.Value + " " + x.Footprint).ToLowerInvariant().Contains(q))
            .OrderBy(x => x.Ref.ToLowerInvariant() == q ? 0 : x.Ref.ToLowerInvariant().StartsWith(q) ? 1 : 2).Cast<object>();
        var n = _allNets.Where(x => x.Name.ToLowerInvariant().Contains(q)).OrderBy(x => x.Name.ToLowerInvariant() == q ? 0 : 1).Cast<object>();
        return c.Concat(n).Take(max);
    }
}
