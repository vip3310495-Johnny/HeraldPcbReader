// Herald PCB Reader — Copyright (C) 2026 Johnny.ch.Tsai
// SPDX-License-Identifier: GPL-3.0-only
// Free software under the GNU General Public License version 3 (LICENSE.txt), WITHOUT ANY WARRANTY; with an
// additional permission to combine it with AvalonDock (Ms-PL), see NOTICE.txt.
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AdCore.Project;

namespace AdApp.Panels;

/// <summary>One search result.</summary>
public sealed class SearchHit
{
    public bool IsNet; public string Label = "", Sub = ""; public DocItem Doc;
    public string Ref; public string Local; public int PcbNet = -1; public int Rank;
}

/// <summary>Search results grouped by file, then 零件 / 網路. Files and groups without results stay folded.</summary>
public sealed class SearchResultsView : TreeView
{
    public event Action<SearchHit> Picked;
    public SearchHit First { get; private set; }

    public SearchResultsView()
    {
        Background = Ui.Bg1; BorderThickness = new Thickness(0);
        MouseLeftButtonUp += (_, e) => { if (FindItem(e.OriginalSource) is { Tag: SearchHit h }) Picked?.Invoke(h); };
        KeyDown += (_, e) => { if (e.Key == Key.Enter && SelectedItem is TreeViewItem { Tag: SearchHit h }) { Picked?.Invoke(h); e.Handled = true; } };
    }

    static TreeViewItem FindItem(object src)
    {
        for (var o = src as DependencyObject; o != null; o = o is Visual ? VisualTreeHelper.GetParent(o) : LogicalTreeHelper.GetParent(o))
            if (o is TreeViewItem t) return t;
        return null;
    }

    public void Show(IReadOnlyList<DocItem> docs, IReadOnlyList<SearchHit> hits, string emptyText = null)
    {
        Items.Clear(); First = null;
        foreach (var d in docs)
        {
            var dh = hits.Where(h => h.Doc == d).ToList();
            var comps = dh.Where(h => !h.IsNet).ToList(); var nets = dh.Where(h => h.IsNet).ToList();
            var fi = new TreeViewItem
            {
                IsExpanded = dh.Count > 0, ToolTip = d.Path,
                Header = Ui.Row(Ui.Badge(d.Kind == DocKind.Pcb ? "pcb" : "sch"), Ui.T(d.Name, dh.Count > 0 ? Ui.Text : Ui.Dim, 12.5, dh.Count > 0),
                    Ui.T($"   零件 {comps.Count} · 網路 {nets.Count}", Ui.Dim, 11.5)),
            };
            foreach (var (label, list, brush) in new[] { ("零件", comps, Ui.Warn), ("網路", nets, Ui.Accent) })
            {
                var gi = new TreeViewItem { IsExpanded = list.Count > 0, Header = Ui.T($"{label}（{list.Count}）", list.Count > 0 ? brush : Ui.Dim, 12, true) };
                foreach (var h in list.Take(300))
                {
                    gi.Items.Add(new TreeViewItem { Tag = h, Header = Ui.Row(Ui.T(h.Label, Ui.Text, 12.5, true), Ui.T("   " + h.Sub, Ui.Dim, 11.5)), ToolTip = "點一下跳過去" });
                    First ??= h;
                }
                if (list.Count > 300) gi.Items.Add(new TreeViewItem { IsEnabled = false, Header = Ui.T($"…另 {list.Count - 300} 筆，請輸入更完整的關鍵字", Ui.Dim, 11.5) });
                fi.Items.Add(gi);
            }
            Items.Add(fi);
        }
        if (docs.Count == 0 && emptyText != null) Items.Add(new TreeViewItem { IsEnabled = false, Header = Ui.T(emptyText, Ui.Dim, 12) });
    }
}

/// <summary>Floating search window (Ctrl+F): text, category, scope and the grouped results; stays open while you jump around.</summary>
public sealed class SearchWindow : Window
{
    public delegate (List<SearchHit> hits, List<DocItem> docs, string label) Searcher(string text, string kind, string scope);
    readonly Searcher _search;
    readonly TextBox _q = new() { FontSize = 15, Padding = new Thickness(6, 4, 6, 4) };
    readonly ComboBox _kind = new() { Width = 78, Margin = new Thickness(6, 0, 0, 0) }, _scope = new() { Width = 110, Margin = new Thickness(6, 0, 0, 0) };
    readonly TextBlock _info = Ui.T("", Ui.Dim, 11.5, false, new Thickness(2, 6, 2, 4));
    readonly SearchResultsView _results = new();

    public SearchWindow(Window owner, Searcher search, Action<SearchHit> pick, string kind, string scope)
    {
        Owner = owner; _search = search;
        Title = "搜尋"; Width = 520; Height = 560; MinWidth = 360; MinHeight = 240;
        WindowStyle = WindowStyle.ToolWindow; ShowInTaskbar = false; ResizeMode = ResizeMode.CanResizeWithGrip;
        Background = Ui.Bg1; Foreground = Ui.Text;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = owner.Left + Math.Max(0, (owner.ActualWidth - Width) / 2); Top = owner.Top + 110;
        foreach (var (k, n) in new[] { ("all", "全部"), ("comp", "零件"), ("net", "NET") }) _kind.Items.Add(new ComboBoxItem { Content = n, Tag = k });
        foreach (var (k, n) in new[] { ("page", "這個頁面"), ("group", "這個群組"), ("all", "全部檔案") }) _scope.Items.Add(new ComboBoxItem { Content = n, Tag = k });
        _kind.SelectedIndex = Math.Max(0, new[] { "all", "comp", "net" }.ToList().IndexOf(kind));
        _scope.SelectedIndex = Math.Max(0, new[] { "page", "group", "all" }.ToList().IndexOf(scope));
        var top = new DockPanel();
        DockPanel.SetDock(_scope, System.Windows.Controls.Dock.Right); DockPanel.SetDock(_kind, System.Windows.Controls.Dock.Right);
        top.Children.Add(_scope); top.Children.Add(_kind); top.Children.Add(_q);
        var root = new DockPanel { Margin = new Thickness(10) };
        DockPanel.SetDock(top, System.Windows.Controls.Dock.Top); DockPanel.SetDock(_info, System.Windows.Controls.Dock.Top);
        root.Children.Add(top); root.Children.Add(_info);
        root.Children.Add(new Border { BorderBrush = Ui.Line, BorderThickness = new Thickness(1), Child = _results });
        Content = root;
        _q.TextChanged += (_, _) => Run();
        _kind.SelectionChanged += (_, _) => Run();
        _scope.SelectionChanged += (_, _) => Run();
        _results.Picked += h => pick(h);
        _q.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && _results.First is { } f) { pick(f); e.Handled = true; }
            else if (e.Key == Key.Down && _results.Items.Count > 0) { (_results.ItemContainerGenerator.ContainerFromIndex(0) as TreeViewItem)?.Focus(); e.Handled = true; }
        };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { Close(); e.Handled = true; } };
        Loaded += (_, _) => { _q.Focus(); Run(); };
    }

    public void Activate(string text)
    {
        if (!string.IsNullOrEmpty(text)) _q.Text = text;
        Show(); Activate(); _q.Focus(); _q.SelectAll(); Run();
    }

    public void Run()
    {
        var (hits, docs, label) = _search(_q.Text, (string)((ComboBoxItem)_kind.SelectedItem).Tag, (string)((ComboBoxItem)_scope.SelectedItem).Tag);
        _info.Text = _q.Text.Trim() == "" ? $"範圍：{label}　輸入位號、值、封裝或網路名稱" : $"範圍：{label}　找到 {hits.Count(h => !h.IsNet)} 個零件、{hits.Count(h => h.IsNet)} 個網路";
        _results.Show(_q.Text.Trim() == "" ? new List<DocItem>() : docs, hits, "沒有可搜尋的檔案");
    }
}
