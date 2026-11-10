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

/// <summary>AD-style Projects panel: each project lists its schematics AND its PCBs under Source Documents.
/// Files can be multi-selected (Ctrl / Shift) and moved between groups from the context menu or by dragging
/// them onto a project, a group, Free Documents or the "new group" row.</summary>
public sealed class ProjectsPanel : DockPanel
{
    readonly TreeView _tree = new();
    readonly TextBlock _empty = Ui.T("尚未開啟檔案。\n檔案 → 開啟專案／檔案／資料夾，或把檔案拖進視窗。", Ui.Dim, 12, false, new Thickness(12));
    Workspace _ws;
    readonly List<DocItem> _sel = new();
    DocItem _anchor;
    static readonly Brush SelBg = new SolidColorBrush(Color.FromArgb(0x70, 0x3D, 0x8E, 0xE0));
    static readonly Brush DropBg = new SolidColorBrush(Color.FromArgb(0x90, 0x2F, 0x8F, 0x4E));
    const string DragFormat = "HeraldPcbReader.Docs";

    public event Action<DocItem> OpenDoc;
    /// <summary>A plain single click on a file (the window shows its tab when it has one).</summary>
    public event Action<DocItem> DocClicked;
    public event Action<DocItem> CloseDoc;
    public event Action<ProjectGroup> CloseGroup;
    public event Action<ProjectGroup> SplitGroup;
    public event Action<ProjectGroup> RenameGroup;
    public event Action<List<DocItem>, ProjectGroup> MoveDocs;
    public event Action<List<DocItem>> NewGroupFor;
    public event Action<ProjectGroup, int?> SetScope;
    public event Action<ProjectGroup> CompileInfo;

    public ProjectsPanel()
    {
        Background = Ui.Bg2;
        _empty.TextWrapping = TextWrapping.Wrap;
        DockPanel.SetDock(_empty, Dock.Top);
        Children.Add(_empty);
        Children.Add(_tree);
        _tree.MouseDoubleClick += (_, e) =>
        {
            if (ItemAt(e.OriginalSource) is { Tag: DocItem d } && !d.Missing) { OpenDoc?.Invoke(d); e.Handled = true; }
        };
        _tree.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && _tree.SelectedItem is TreeViewItem { Tag: DocItem d } && !d.Missing) OpenDoc?.Invoke(d);
            else if (e.Key == Key.F2 && _tree.SelectedItem is TreeViewItem { Tag: ProjectGroup { Kind: GroupKind.User } rg }) { RenameGroup?.Invoke(rg); e.Handled = true; }
            else if (e.Key == Key.A && Keyboard.Modifiers == ModifierKeys.Control) { _sel.Clear(); _sel.AddRange(DocItems().Select(i => (DocItem)i.Tag)); PaintSelection(); e.Handled = true; }
            else if (e.Key == Key.Escape) { _sel.Clear(); PaintSelection(); }
        };
        _tree.PreviewMouseLeftButtonDown += OnPreviewDown;
        _tree.PreviewMouseMove += OnPreviewMove;
        _tree.PreviewMouseLeftButtonUp += (_, e) =>
        {
            // a plain click inside a multi-selection (that did not start a drag) selects just that file
            if (_downItem?.Tag is DocItem d && Keyboard.Modifiers == ModifierKeys.None)
            {
                if (_sel.Count > 1) { _sel.Clear(); _sel.Add(d); _anchor = d; PaintSelection(); }
                // as in AD: one click on a file that has a tab brings that tab up (after the click: it rebuilds the tree)
                Dispatcher.BeginInvoke(() => DocClicked?.Invoke(d), System.Windows.Threading.DispatcherPriority.Input);
            }
            _downItem = null;
        };
        _tree.PreviewMouseRightButtonDown += (_, e) =>
        {
            // right-click outside the selection selects that file alone (its menu then acts on it)
            if (ItemAt(e.OriginalSource) is { Tag: DocItem d } && !_sel.Contains(d)) { _sel.Clear(); _sel.Add(d); _anchor = d; PaintSelection(); }
        };
    }

    static IEnumerable<DependencyObject> Descend(DependencyObject d)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++) { var c = VisualTreeHelper.GetChild(d, i); yield return c; foreach (var x in Descend(c)) yield return x; }
    }

    static TreeViewItem ItemAt(object src)
    {
        for (var o = src as DependencyObject; o != null; o = o is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(o) : LogicalTreeHelper.GetParent(o))
            if (o is TreeViewItem t) return t;
        return null;
    }

    public void Rebuild(Workspace ws, DocItem active, ISet<DocItem> netDocs)
    {
        _ws = ws;
        _here = netDocs != null && active != null && netDocs.Contains(active) ? active : null;
        // the list keeps its scroll position (a click on a file brings its tab up, which rebuilds the list)
        var sv = Descend(_tree).OfType<ScrollViewer>().FirstOrDefault(); double scroll = sv?.VerticalOffset ?? 0;
        if (sv != null && scroll > 0) Dispatcher.BeginInvoke(() => sv.ScrollToVerticalOffset(scroll), System.Windows.Threading.DispatcherPriority.Loaded);
        var expanded = new HashSet<string>(Items().Where(i => i.IsExpanded).Select(i => i.Uid));
        bool first = _tree.Items.Count == 0;
        _tree.Items.Clear();
        var alive = new HashSet<DocItem>(ws.AllDocs);
        _sel.RemoveAll(d => !alive.Contains(d));
        double fs = Ui.Base, small = Ui.F(11);
        int n = 0;
        foreach (var g in ws.AllGroups)
        {
            if (g.Kind == GroupKind.Free && g.Docs.Count == 0) continue;
            n++;
            var gi = new TreeViewItem { Tag = g, Uid = "g:" + (g.Prj?.Path ?? g.Name), IsExpanded = true, AllowDrop = true };
            var kind = g.Kind == GroupKind.Project ? "prj" : g.Kind == GroupKind.User ? "grp" : "free";
            var title = g.Kind == GroupKind.Project ? System.IO.Path.GetFileName(g.Prj.Path) : g.Name;
            var sub = g.Kind == GroupKind.Project && g.Compiled != null ? $"  {g.Compiled.PNets.Count} nets" : "";
            gi.Header = HeaderBorder(Ui.Row(Ui.Badge(kind), Ui.T(title, Ui.Text, fs, true), Ui.T(sub, Ui.Dim, small)));
            gi.ToolTip = (g.Prj?.Path ?? g.Name) + "\n（可以把檔案拖到這裡" + (g.Kind == GroupKind.Free ? "移出群組）" : "加入）");
            gi.ContextMenu = GroupMenu(g);
            HookDrop(gi, g);
            TreeViewItem parent = gi;
            if (g.Kind == GroupKind.Project)
            {
                var src = new TreeViewItem { Uid = gi.Uid + "/src", IsExpanded = true, AllowDrop = true, Header = Ui.Row(Folder(),Ui.T("Source Documents", Ui.Dim, fs - 0.5)) };
                HookDrop(src, g);
                gi.Items.Add(src); parent = src;
            }
            foreach (var d in g.Docs.OrderBy(d => d.Kind).ThenBy(d => g.Kind == GroupKind.Project ? 0 : 1))
            {
                var di = new TreeViewItem { Tag = d, Uid = "d:" + d.Path, AllowDrop = true };
                var name = Ui.T(d.Name, d.Missing ? Ui.Dim : d.Error != null ? Ui.Err : Ui.Text, fs - 0.5, d == active);
                if (d.Missing) name.FontStyle = FontStyles.Italic;
                var row = Ui.Row(Ui.Badge(d.Kind == DocKind.Pcb ? "pcb" : "sch"), name);
                if (d.Added) row.Children.Add(Ui.T("  手動加入", Ui.Dim, small));
                if (d.Missing) row.Children.Add(Ui.T("  找不到", Ui.Warn, small));
                if (d.Error != null) row.Children.Add(Ui.T("  無法開啟", Ui.Err, small));
                bool jump = netDocs != null && netDocs.Contains(d) && d != active, here = d == _here;
                if (jump) row.Children.Add(Ui.T("  可跳轉", Ui.Jump, small, true));
                if (here) { name.Foreground = AdApp.Views.NetPage.CurrentText; row.Children.Add(Ui.T("  目前頁面", AdApp.Views.NetPage.CurrentText, small, true)); }
                var hb = HeaderBorder(row);
                if (jump) { hb.BorderBrush = Ui.Jump; hb.BorderThickness = new Thickness(1.5); hb.ToolTip = "目前標示的網路也在這張圖上：雙擊或在網路標示列點檔名跳過去"; }
                // the page shown now, inverted like in the net's page list (PaintSelection keeps its fill)
                if (here) { hb.BorderBrush = AdApp.Views.NetPage.CurrentEdge; hb.BorderThickness = new Thickness(1.5); hb.ToolTip = "目前標示的網路所在、正在看的這張圖"; }
                di.Header = hb;
                di.ToolTip = d.Error != null ? d.Path + "\n" + d.Error : d.Missing ? "專案列出但檔案不存在：" + d.Path : d.Path + "\nCtrl / Shift + 點選可複選，拖曳到專案或群組可加入";
                di.ContextMenu = DocMenu(d);
                HookDrop(di, g);
                parent.Items.Add(di);
            }
            _tree.Items.Add(gi);
        }
        if (n > 0)
        {
            var ng = new TreeViewItem { Uid = "new-group", AllowDrop = true, Focusable = false, ToolTip = "把檔案拖到這裡，建立新的群組（群組內的電路圖與 PCB 可以互相查詢）" };
            var plus = Ui.Icon("M8,3.5 V12.5 M3.5,8 H12.5", Ui.Dim); plus.Margin = new Thickness(2, 0, 6, 0);
            ng.Header = HeaderBorder(Ui.Row(plus,Ui.T("新群組（拖曳檔案到這裡）", Ui.Dim, fs - 0.5)));
            HookDrop(ng, null);
            _tree.Items.Add(ng);
        }
        _empty.Visibility = n == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (!first) foreach (var i in Items()) i.IsExpanded = expanded.Contains(i.Uid) || i.Tag is ProjectGroup || i.Uid.EndsWith("/src");
        PaintSelection();
    }

    static UIElement Folder() { var f = Ui.Icon("M1.5,3.5 H6 L7.5,5 H14.5 V13 H1.5 Z", Ui.Warn); f.Margin = new Thickness(0, 0, 6, 0); return f; }
    static Border HeaderBorder(UIElement content) => new() { Child = content, Padding = new Thickness(2, 1, 4, 1), CornerRadius = new CornerRadius(2), Background = Brushes.Transparent };

    IEnumerable<TreeViewItem> Items()
    {
        var st = new Stack<TreeViewItem>(_tree.Items.OfType<TreeViewItem>().Reverse());
        while (st.Count > 0) { var i = st.Pop(); yield return i; foreach (var c in i.Items.OfType<TreeViewItem>().Reverse()) st.Push(c); }
    }
    IEnumerable<TreeViewItem> DocItems() => Items().Where(i => i.Tag is DocItem);

    void PaintSelection()
    {
        foreach (var i in DocItems())
            if (i.Header is Border b) b.Background = i.Tag == _here ? Ui.Jump : _sel.Contains((DocItem)i.Tag) ? SelBg : Brushes.Transparent;
    }
    DocItem _here;   // the file shown now, when the highlighted net is on it: inverted

    // ---------- selection + drag ----------
    Point _downPt; TreeViewItem _downItem;

    void OnPreviewDown(object s, MouseButtonEventArgs e)
    {
        _downItem = null;
        if (e.ClickCount > 1 || ItemAt(e.OriginalSource) is not { Tag: DocItem d } item) return;
        _downPt = e.GetPosition(this); _downItem = item;
        var mods = Keyboard.Modifiers;
        if ((mods & ModifierKeys.Control) != 0) { if (!_sel.Remove(d)) _sel.Add(d); _anchor = d; }
        else if ((mods & ModifierKeys.Shift) != 0 && _anchor != null)
        {
            var order = DocItems().Select(i => (DocItem)i.Tag).ToList();
            int a = order.IndexOf(_anchor), b = order.IndexOf(d);
            if (a < 0) a = b;
            _sel.Clear(); _sel.AddRange(order.Skip(Math.Min(a, b)).Take(Math.Abs(a - b) + 1));
        }
        else if (!_sel.Contains(d) || _sel.Count == 1) { _sel.Clear(); _sel.Add(d); _anchor = d; }
        PaintSelection();
    }

    void OnPreviewMove(object s, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _downItem?.Tag is not DocItem d) return;
        var p = e.GetPosition(this);
        if (Math.Abs(p.X - _downPt.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(p.Y - _downPt.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        var docs = (_sel.Contains(d) ? _sel : new List<DocItem> { d }).Where(x => !x.Missing).ToList();
        _downItem = null;
        if (docs.Count == 0) return;
        DragDrop.DoDragDrop(_tree, new DataObject(DragFormat, docs), DragDropEffects.Move);
    }

    /// <summary>Group a drop on <paramref name="item"/> goes to (null = new group).</summary>
    void HookDrop(TreeViewItem item, ProjectGroup target)
    {
        bool Ok(DragEventArgs e) => e.Data.GetData(DragFormat) is List<DocItem> docs && _ws != null && docs.Any(d => _ws.CanMove(d) && d.Group != target);
        item.DragOver += (_, e) =>
        {
            e.Effects = Ok(e) ? DragDropEffects.Move : DragDropEffects.None; e.Handled = true;
            if (item.Header is Border b) b.Background = e.Effects == DragDropEffects.Move ? DropBg : Brushes.Transparent;
        };
        item.DragLeave += (_, e) => { if (item.Header is Border b) b.Background = Brushes.Transparent; PaintSelection(); };
        item.Drop += (_, e) =>
        {
            e.Handled = true;
            if (item.Header is Border b) b.Background = Brushes.Transparent;
            if (!Ok(e) || e.Data.GetData(DragFormat) is not List<DocItem> docs) return;
            var movable = docs.Where(d => _ws.CanMove(d)).ToList();
            if (target == null) NewGroupFor?.Invoke(movable); else MoveDocs?.Invoke(movable, target);
        };
    }

    static MenuItem MI(string h, Action a, bool en = true, string icon = null, Brush ink = null) => Ui.MenuItem(h, a, en, icon, ink);

    ContextMenu GroupMenu(ProjectGroup g)
    {
        var m = new ContextMenu();
        bool hasBoth = g.SchDocs.Any() && g.PcbDocs.Any();
        m.Items.Add(MI("分割檢視：左電路圖／右 PCB", () => SplitGroup?.Invoke(g), hasBoth, Ui.Icons.Split));
        m.Items.Add(MI("開啟全部文件", () => { foreach (var d in g.Docs.Where(d => d.Loaded)) OpenDoc?.Invoke(d); }, true, Ui.Icons.Docs));
        if (g.Kind == GroupKind.User) { var rn = MI("重新命名…", () => RenameGroup?.Invoke(g), true, Ui.Icons.Rename); rn.InputGestureText = "F2"; m.Items.Add(rn); }
        if (g.Kind == GroupKind.Project)
        {
            var scope = MI("網路範圍（Net Identifier Scope）", null, true, Ui.Icons.Scope);
            var cur = g.ModeOverride;
            void Add(string h, int? v) { var i = MI(h, () => SetScope?.Invoke(g, v)); i.IsCheckable = true; i.IsChecked = cur == v; scope.Items.Add(i); }
            Add($"依專案設定（{new[] { "Automatic", "Flat", "Hierarchical", "Global", "Strict Hierarchical" }[Math.Clamp(g.Prj.HierarchyMode, 0, 4)]}）", null);
            Add("Automatic", 0); Add("Flat", 1); Add("Hierarchical", 2); Add("Global", 3); Add("Strict Hierarchical", 4);
            m.Items.Add(scope);
            m.Items.Add(MI("編譯資訊…", () => CompileInfo?.Invoke(g), true, Ui.Icons.Info));
        }
        if (_sel.Count > 0 && _sel.Any(d => d.Group != g && _ws.CanMove(d)))
        {
            m.Items.Add(new Separator());
            bool free = g.Kind == GroupKind.Free;
            m.Items.Add(MI($"把選取的 {_sel.Count} 個檔案{(free ? "移出群組" : "加入這裡")}", () => MoveDocs?.Invoke(_sel.Where(_ws.CanMove).ToList(), g), true,
                free ? Ui.Icons.FolderOut : Ui.Icons.FolderAdd, Ui.Warn));
        }
        m.Items.Add(new Separator());
        m.Items.Add(MI(g.Kind == GroupKind.Free ? "關閉全部自由文件" : g.Kind == GroupKind.User ? "解散群組並關閉文件" : "關閉專案", () => CloseGroup?.Invoke(g), true, Ui.Icons.Close, Ui.Err));
        if (g.Prj != null) m.Items.Add(MI("在檔案總管中顯示", () => Reveal(g.Prj.Path), true, Ui.Icons.Explorer));
        return m;
    }

    ContextMenu DocMenu(DocItem d)
    {
        var m = new ContextMenu();
        m.Opened += (_, _) => FillDocMenu(m, d);
        m.Items.Add(new MenuItem());   // placeholder: filled when opened, for the current selection
        return m;
    }

    void FillDocMenu(ContextMenu m, DocItem d)
    {
        m.Items.Clear();
        var docs = _sel.Contains(d) ? _sel.ToList() : new List<DocItem> { d };
        var movable = docs.Where(x => _ws.CanMove(x) && !x.Missing).ToList();
        string what = docs.Count > 1 ? $"選取的 {docs.Count} 個檔案" : d.Name;
        m.Items.Add(MI("開啟", () => OpenDoc?.Invoke(d), d.Loaded, Ui.Icons.Doc));
        m.Items.Add(new Separator());
        var add = MI($"加入群組（{what}）", null, movable.Count > 0, Ui.Icons.FolderAdd, Ui.Warn);
        foreach (var g in _ws.Groups.Where(x => x.Kind is GroupKind.User or GroupKind.Project))
        {
            bool prj = g.Kind == GroupKind.Project;
            add.Items.Add(MI((prj ? "專案 " : "群組 ") + g.Name, () => MoveDocs?.Invoke(movable, g), movable.Any(x => x.Group != g), prj ? Ui.Icons.Project : Ui.Icons.Folder, prj ? null : Ui.Warn));
        }
        if (add.Items.Count > 0) add.Items.Add(new Separator());
        add.Items.Add(MI("新群組…", () => NewGroupFor?.Invoke(movable), true, Ui.Icons.Plus));
        m.Items.Add(add);
        var outable = movable.Where(x => x.Group.Kind != GroupKind.Free).ToList();
        m.Items.Add(MI($"移出群組（回到 Free Documents）", () => MoveDocs?.Invoke(outable, _ws.Free), outable.Count > 0, Ui.Icons.FolderOut, Ui.Warn));
        if (docs.Count > movable.Count)
            m.Items.Add(new MenuItem { Header = "（.PrjPcb 列出的檔案隨專案開啟，不能移出）", IsEnabled = false });
        m.Items.Add(new Separator());
        if (_ws.CanMove(d)) m.Items.Add(MI(docs.Count > 1 ? $"關閉選取的 {docs.Count} 個檔案" : "關閉", () => { foreach (var x in docs.Where(_ws.CanMove).ToList()) CloseDoc?.Invoke(x); }, true, Ui.Icons.Close, Ui.Err));
        m.Items.Add(MI("複製路徑", () => { try { Clipboard.SetText(string.Join(Environment.NewLine, docs.Select(x => x.Path))); } catch { } }, true, Ui.Icons.Copy));
        m.Items.Add(MI("在檔案總管中顯示", () => Reveal(d.Path), !d.Missing, Ui.Icons.Explorer));
    }

    static void Reveal(string path)
    {
        // the Windows explorer by its full path: not an explorer.exe that might sit in the program or current folder
        try { System.Diagnostics.Process.Start(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), "/select,\"" + path + "\""); } catch { }
    }
}
