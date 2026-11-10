// Herald PCB Reader — Copyright (C) 2026 Johnny.ch.Tsai
// SPDX-License-Identifier: GPL-3.0-only
// Free software under the GNU General Public License version 3 (LICENSE.txt), WITHOUT ANY WARRANTY; with an
// additional permission to combine it with AvalonDock (Ms-PL), see NOTICE.txt.
using System.Collections.Concurrent;
using AdCore.Pcb;
using AdCore.Sch;
using static AdCore.Util.TextUtil;

namespace AdCore.Project;

public enum DocKind { Sch, Pcb }
public enum GroupKind { Project, User, Free }

public sealed class DocItem
{
    public string Path = "", Name = "";
    public DocKind Kind;
    public ProjectGroup Group;
    public SchDocument Sch;
    public PcbBoard Pcb;
    public string Error;
    public bool Missing;
    /// <summary>Put into a project by the user (not listed in the .PrjPcb): can be moved out again.</summary>
    public bool Added;
    public bool Loaded => Sch != null || Pcb != null;
    public override string ToString() => Name;
}

public sealed class ProjectGroup
{
    public string Name = "";
    public GroupKind Kind;
    public PrjPcbInfo Prj;
    public readonly List<DocItem> Docs = new();
    public int? ModeOverride;
    public CompiledProject Compiled;
    List<DocItem> _compiledDocs = new();

    public IEnumerable<DocItem> SchDocs => Docs.Where(d => d.Kind == DocKind.Sch && d.Sch != null);
    public IEnumerable<DocItem> PcbDocs => Docs.Where(d => d.Kind == DocKind.Pcb && d.Pcb != null);

    public CompileScope Scope =>
        Kind == GroupKind.Project && Prj != null
            ? new CompileScope { Mode = ModeOverride ?? Prj.HierarchyMode, AllowPort = Prj.AllowPortNetNames, AllowEntry = Prj.AllowSheetEntryNetNames }
            : CompileScope.UserGroup;

    /// <summary>Compile cross-sheet nets. The free group never compiles (its files are unrelated).</summary>
    public void Compile()
    {
        _compiledDocs = SchDocs.ToList();
        Compiled = Kind == GroupKind.Free || _compiledDocs.Count == 0 || (Kind == GroupKind.User && _compiledDocs.Count < 2)
            ? null
            : ProjectCompiler.Compile(_compiledDocs.Select(d => (d.Name, d.Sch)).ToList(), Scope);
    }

    public int CompileIndex(DocItem d) => _compiledDocs.IndexOf(d);
    public DocItem CompiledDoc(int i) => i >= 0 && i < _compiledDocs.Count ? _compiledDocs[i] : null;

    public ProjectNet PNetFor(DocItem d, int sheet, string localName)
    {
        if (Compiled == null || localName == null) return null;
        int i = CompileIndex(d);
        return i < 0 ? null : Compiled.For(i, sheet, localName);
    }

    public IEnumerable<string> MissingDocs =>
        Kind == GroupKind.Project ? Docs.Where(d => d.Missing).Select(d => d.Name) : Enumerable.Empty<string>();
}

public sealed class OpenResult
{
    public readonly List<DocItem> Added = new();
    public readonly List<ProjectGroup> Groups = new();
    public DocItem Focus;
    public readonly List<string> Messages = new();
    /// <summary>Where the time went (automation / tuning).</summary>
    public string Timing = "";
}

public sealed class Workspace
{
    public static readonly string[] SchExt = { ".schdoc", ".kicad_sch", ".sch" };
    public static readonly string[] PcbExt = { ".pcbdoc" };
    static readonly HashSet<string> SkipDirs = new(StringComparer.OrdinalIgnoreCase) { ".git", "node_modules", "__History", "__Previews", "History" };

    public readonly List<ProjectGroup> Groups = new();
    public readonly ProjectGroup Free = new() { Name = "Free Documents", Kind = GroupKind.Free };
    public readonly Dictionary<string, KicadLegacy.LibSymbol> LegacyLibs = new(StringComparer.OrdinalIgnoreCase);
    public IEnumerable<ProjectGroup> AllGroups => Groups.Append(Free);
    public IEnumerable<DocItem> AllDocs => AllGroups.SelectMany(g => g.Docs);

    static string Norm(string p) => System.IO.Path.GetFullPath(p).ToLowerInvariant();
    public static bool IsSch(string p) => SchExt.Any(e => p.EndsWith(e, StringComparison.OrdinalIgnoreCase));
    public static bool IsPcb(string p) => PcbExt.Any(e => p.EndsWith(e, StringComparison.OrdinalIgnoreCase));
    public static bool IsPrj(string p) => p.EndsWith(".prjpcb", StringComparison.OrdinalIgnoreCase);
    public static bool IsLib(string p) => p.EndsWith(".lib", StringComparison.OrdinalIgnoreCase);

    public DocItem FindDoc(string path) { var n = Norm(path); return AllDocs.FirstOrDefault(d => !d.Missing && Norm(d.Path) == n); }
    public ProjectGroup GroupOf(DocItem d) => d?.Group;

    /// <summary>The .PrjPcb in the same folder that lists this file (so a single double-clicked file brings its project).</summary>
    public static string ProjectFor(string file)
    {
        if (!(IsSch(file) || IsPcb(file))) return null;
        var dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(file)); var want = Norm(file);
        if (dir == null) return null;
        foreach (var prj in Directory.EnumerateFiles(dir, "*.prjpcb"))
        {
            try
            {
                var info = PrjPcbInfo.Parse(File.ReadAllText(prj), prj);
                if (info.ResolveExisting(info.AllDocs).Any(d => Norm(d) == want)) return prj;
            }
            catch { }
        }
        return null;
    }

    static IEnumerable<string> Scan(string dir, int depth = 0)
    {
        if (depth > 8) yield break;
        IEnumerable<string> files = Array.Empty<string>(), dirs = Array.Empty<string>();
        try { files = Directory.EnumerateFiles(dir).ToList(); dirs = Directory.EnumerateDirectories(dir).ToList(); } catch { }
        foreach (var f in files) yield return f;
        foreach (var d in dirs)
        {
            var n = System.IO.Path.GetFileName(d);
            if (n.StartsWith('.') || SkipDirs.Contains(n) || n.StartsWith("Project Outputs for", StringComparison.OrdinalIgnoreCase) || n.StartsWith("Project Logs for", StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var f in Scan(d, depth + 1)) yield return f;
        }
    }

    /// <summary>Open files / folders / projects. Parses in parallel. Thread-safe to call from a background thread
    /// as long as the UI does not mutate the workspace concurrently.</summary>
    public OpenResult Open(IEnumerable<string> inputPaths, bool expandSingleToProject = false)
    {
        var res = new OpenResult(); var sw = System.Diagnostics.Stopwatch.StartNew();
        var prjPaths = new List<string>(); var loose = new List<string>();
        foreach (var raw in inputPaths)
        {
            string p;
            try { p = System.IO.Path.GetFullPath(raw); } catch { continue; }
            if (Directory.Exists(p))
            {
                var all = Scan(p).ToList();
                var prjs = all.Where(IsPrj).ToList();
                prjPaths.AddRange(prjs);
                var listed = new HashSet<string>();
                foreach (var prj in prjs)
                {
                    try { var info = PrjPcbInfo.Parse(File.ReadAllText(prj), prj); foreach (var d in info.ResolveExisting(info.AllDocs)) listed.Add(Norm(d)); } catch { }
                }
                var rest = all.Where(f => IsSch(f) || IsPcb(f) || IsLib(f)).Where(f => !listed.Contains(Norm(f))).ToList();
                if (prjs.Count > 0) { if (rest.Count > 0) res.Messages.Add($"資料夾內有專案檔，已略過 {rest.Count} 個專案沒有列出的檔案"); }
                else loose.AddRange(rest);
            }
            else if (File.Exists(p))
            {
                if (IsPrj(p)) prjPaths.Add(p);
                else if (expandSingleToProject && ProjectFor(p) is { } prj) { prjPaths.Add(prj); res.Focus = new DocItem { Path = p }; }
                else if (IsSch(p) || IsPcb(p) || IsLib(p)) loose.Add(p);
            }
        }

        var toParse = new List<DocItem>();
        foreach (var prjPath in prjPaths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var g = Groups.FirstOrDefault(x => x.Prj != null && Norm(x.Prj.Path) == Norm(prjPath));
            if (g == null)
            {
                PrjPcbInfo info;
                try { info = PrjPcbInfo.Parse(File.ReadAllText(prjPath), prjPath); }
                catch (Exception e) { res.Messages.Add($"無法讀取專案 {System.IO.Path.GetFileName(prjPath)}：{e.Message}"); continue; }
                g = new ProjectGroup { Name = info.Name, Kind = GroupKind.Project, Prj = info };
                var dir = System.IO.Path.GetDirectoryName(prjPath) ?? "";
                foreach (var rel in info.SchDocs.Concat(info.PcbDocs))
                {
                    var full = System.IO.Path.GetFullPath(System.IO.Path.Combine(dir, rel.Replace('\\', System.IO.Path.DirectorySeparatorChar)));
                    var item = new DocItem { Path = full, Name = System.IO.Path.GetFileName(full), Kind = IsPcb(full) ? DocKind.Pcb : DocKind.Sch, Group = g };
                    if (!File.Exists(full)) item.Missing = true;
                    else
                    {
                        // a file already open as a free document moves into its project
                        var ex = Free.Docs.FirstOrDefault(d => Norm(d.Path) == Norm(full));
                        if (ex != null) { Free.Docs.Remove(ex); item = ex; item.Group = g; }
                        else toParse.Add(item);
                    }
                    g.Docs.Add(item);
                }
                Groups.Add(g);
            }
            res.Groups.Add(g);
        }
        foreach (var f in loose.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (IsLib(f)) { LoadLegacyLib(f, res); continue; }
            if (FindDoc(f) is { } ex) { res.Added.Add(ex); continue; }
            var item = new DocItem { Path = f, Name = System.IO.Path.GetFileName(f), Kind = IsPcb(f) ? DocKind.Pcb : DocKind.Sch, Group = Free };
            Free.Docs.Add(item); toParse.Add(item);
        }

        long tScan = sw.ElapsedMilliseconds;
        // biggest files first (they set how long this takes); below normal priority and two cores left over, so the
        // window being built at the same time (UI and render threads) does not wait for the parsing
        var bySize = toParse.OrderByDescending(d => { try { return new FileInfo(d.Path).Length; } catch (IOException) { return 0; } }).ToList();
        Parallel.ForEach(bySize, new ParallelOptions { MaxDegreeOfParallelism = ParseThreads }, d =>
        {
            var t = Thread.CurrentThread; var pr = t.Priority;
            t.Priority = ThreadPriority.BelowNormal;
            try { Parse(d); } finally { t.Priority = pr; }
        });
        foreach (var d in toParse) { res.Added.Add(d); if (d.Error != null) res.Messages.Add($"{d.Name}：{d.Error}"); }
        long tParse = sw.ElapsedMilliseconds;
        foreach (var g in res.Groups.Concat(toParse.Select(d => d.Group)).Distinct()) g.Compile();
        res.Timing = $"scan {tScan}, parse {tParse - tScan} ({toParse.Count} files), compile {sw.ElapsedMilliseconds - tParse}";
        foreach (var g in res.Groups)
        {
            var c = g.Compiled;
            res.Messages.Add($"已編譯專案「{g.Name}」：{g.SchDocs.Count()} 張電路圖、{g.PcbDocs.Count()} 塊 PCB、{c?.PNets.Count ?? 0} 個網路，其中 {c?.Cross ?? 0} 個跨圖紙（{CompiledProject.ScopeLabel[c?.Eff ?? "auto"].Split('（')[0]}）");
            var miss = g.MissingDocs.ToList();
            if (miss.Count > 0) res.Messages.Add($"專案「{g.Name}」有 {miss.Count} 個檔案找不到：{string.Join("、", miss.Take(6))}{(miss.Count > 6 ? " …" : "")}");
        }
        if (res.Focus != null) res.Focus = FindDoc(res.Focus.Path);
        return res;
    }

    /// <summary>How many files are parsed (and PCB scenes built) at the same time: two cores are left for the window.
    /// (Tried on a 4 P + 10 E core laptop CPU: fewer threads, a higher window-thread priority or EcoQoS for these threads
    /// did not open faster.)</summary>
    public static readonly int ParseThreads = Math.Max(1, Environment.ProcessorCount - 2);

    void LoadLegacyLib(string f, OpenResult res)
    {
        try
        {
            foreach (var kv in KicadLegacy.ParseLib(File.ReadAllText(f))) LegacyLibs[kv.Key] = kv.Value;
            var redo = Free.Docs.Where(d => d.Kind == DocKind.Sch && d.Sch?.Format == "kicad-legacy").ToList();
            foreach (var d in redo) Parse(d);
            res.Messages.Add($"已載入符號庫 {System.IO.Path.GetFileName(f)}" + (redo.Count > 0 ? $"，重新解析 {redo.Count} 張舊版電路圖" : ""));
        }
        catch (Exception e) { res.Messages.Add($"{System.IO.Path.GetFileName(f)}：{e.Message}"); }
    }

    void Parse(DocItem d)
    {
        try
        {
            var bytes = File.ReadAllBytes(d.Path);
            if (d.Kind == DocKind.Pcb) { d.Pcb = AltiumPcbParser.Parse(bytes, d.Name); d.Pcb.SourcePath = d.Path; }
            else d.Sch = SchLoader.Load(bytes, d.Name, LegacyLibs);
            d.Error = null;
        }
        catch (Exception e) { d.Error = e.Message; }
    }

    // ---------- user groups (for loose files: lets a free schematic cross-probe with a free PCB) ----------
    public ProjectGroup NewUserGroup(string name)
    {
        var g = new ProjectGroup { Name = name, Kind = GroupKind.User };
        Groups.Add(g); return g;
    }

    /// <summary>Files listed in a .PrjPcb stay with their project; everything else can change group.</summary>
    public bool CanMove(DocItem d) => d.Group?.Kind != GroupKind.Project || d.Added;

    public void MoveTo(DocItem d, ProjectGroup target)
    {
        if (d.Group == target || !CanMove(d) || d.Missing) return;
        var old = d.Group; old?.Docs.Remove(d);
        target.Docs.Add(d); d.Group = target; d.Added = target.Kind == GroupKind.Project;
        old?.Compile(); target.Compile();
        if (old is { Kind: GroupKind.User } && old.Docs.Count == 0) Groups.Remove(old);
    }

    public void Close(DocItem d)
    {
        var g = d.Group; if (g == null) return;
        if (g.Kind == GroupKind.Project && !d.Added) return; // project docs close with the project
        g.Docs.Remove(d); g.Compile();
        if (g.Kind == GroupKind.User && g.Docs.Count == 0) Groups.Remove(g);
    }

    public void Close(ProjectGroup g)
    {
        if (g.Kind == GroupKind.Free) { g.Docs.Clear(); return; }
        Groups.Remove(g);
    }

    /// <summary>All document paths, for session restore.</summary>
    public List<string> SessionPaths() =>
        Groups.Where(g => g.Kind == GroupKind.Project).Select(g => g.Prj.Path)
            .Concat(AllGroups.SelectMany(g => g.Docs).Where(d => !d.Missing && (d.Group.Kind != GroupKind.Project || d.Added)).Select(d => d.Path)).ToList();

    /// <summary>Who belongs where, beyond what the files say: user groups, and files the user added to a project.</summary>
    public List<SavedGroup> SessionGroups() =>
        Groups.Where(g => g.Kind == GroupKind.User)
            .Select(g => new SavedGroup { Name = g.Name, Docs = g.Docs.Where(d => !d.Missing).Select(d => d.Path).ToList() })
            .Concat(Groups.Where(g => g.Kind == GroupKind.Project && g.Docs.Any(d => d.Added))
                .Select(g => new SavedGroup { Name = g.Name, Project = g.Prj.Path, Docs = g.Docs.Where(d => d.Added).Select(d => d.Path).ToList() }))
            .Where(s => s.Docs.Count > 0).ToList();

    /// <summary>After the session's files are open again: put them back into their groups.</summary>
    public void RestoreGroups(IEnumerable<SavedGroup> saved)
    {
        foreach (var s in saved ?? Enumerable.Empty<SavedGroup>())
        {
            var docs = (s.Docs ?? new()).Select(FindDoc).Where(d => d != null && CanMove(d)).Distinct().ToList();
            if (docs.Count == 0) continue;
            var g = s.Project != null
                ? Groups.FirstOrDefault(x => x.Kind == GroupKind.Project && x.Prj != null && Norm(x.Prj.Path) == Norm(s.Project))
                : NewUserGroup(string.IsNullOrWhiteSpace(s.Name) ? "群組 " + (Groups.Count(x => x.Kind == GroupKind.User) + 1) : s.Name);
            if (g == null) continue;
            foreach (var d in docs) MoveTo(d, g);
        }
    }
}

/// <summary>A user group (Project == null) or the files added to a project, as kept in the settings file.</summary>
public sealed class SavedGroup
{
    public string Name { get; set; } = "";
    public string Project { get; set; }
    public List<string> Docs { get; set; } = new();
}
