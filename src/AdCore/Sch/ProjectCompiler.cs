// Herald PCB Reader — Copyright (C) 2026 Johnny.ch.Tsai
// SPDX-License-Identifier: GPL-3.0-only
// Free software under the GNU General Public License version 3 (LICENSE.txt), WITHOUT ANY WARRANTY; with an
// additional permission to combine it with AvalonDock (Ms-PL), see NOTICE.txt.
using static AdCore.Util.TextUtil;

namespace AdCore.Sch;

public sealed class CompileScope
{
    public int Mode;                 // 0 auto, 1 flat, 2 hier, 3 global, 4 strict
    public bool AllowPort, AllowEntry = true, ByName;
    public static readonly CompileScope UserGroup = new() { Mode = 3, AllowPort = true, AllowEntry = true, ByName = true };
}

public sealed class PNetLocal { public int DocIndex; public int Sheet; public string Name; }
public sealed class PNetMember { public int DocIndex; public string File, Ref, Pin, PinName; }

public sealed class ProjectNet
{
    public int Id;
    public string Name;
    public bool Auto;
    public List<PNetMember> Members = new();
    public List<PNetLocal> Locals = new();
    public int FileCount;
}

public sealed class CompiledProject
{
    public string Set, Eff;
    public List<ProjectNet> PNets = new();
    public Dictionary<(int doc, int sheet, string name), int> Lookup = new();
    public int Cross;
    public ProjectNet For(int doc, int sheet, string name) => name != null && Lookup.TryGetValue((doc, sheet, name), out var id) ? PNets[id] : null;

    public static readonly Dictionary<string, string> ScopeLabel = new()
    {
        ["byname"] = "群組（同名網路相連）", ["auto"] = "Automatic", ["flat"] = "Flat（只有 Port 全域）",
        ["hier"] = "Hierarchical（Sheet Entry ↔ Port，電源埠全域）", ["strict"] = "Strict Hierarchical（電源埠只在本頁）", ["global"] = "Global（網路標籤與 Port 全域）",
    };
}

/// <summary>Cross-sheet net compilation following Altium's net identifier scope (port of v1 compileProject).</summary>
public static class ProjectCompiler
{
    public static CompiledProject Compile(IReadOnlyList<(string name, SchDocument doc)> entries, CompileScope info)
    {
        var sets = new[] { "auto", "flat", "hier", "global", "strict" };
        string set = info.Mode >= 0 && info.Mode < sets.Length ? sets[info.Mode] : "auto";
        bool anyEntry = false, anyPort = false;
        foreach (var e in entries) foreach (var sh in e.doc.Sheets) foreach (var l in sh.Labels)
                { if (l.Kind == LabelKind.SheetPin) anyEntry = true; else if (l.Kind == LabelKind.Port) anyPort = true; }
        string eff = set == "auto" ? (anyEntry ? "hier" : anyPort ? "flat" : "global") : set;

        var par = new List<int>();
        int Fd(int a) { while (par[a] != a) { par[a] = par[par[a]]; a = par[a]; } return a; }
        void Un(int a, int b) { a = Fd(a); b = Fd(b); if (a != b) par[b] = a; }
        var virt = new Dictionary<string, int>();
        int V(string k) { k = k.ToLowerInvariant(); if (!virt.TryGetValue(k, out var n)) { n = par.Count; par.Add(n); virt[k] = n; } return n; }
        var nodes = new List<(int id, int doc, int si, SheetNet n)>();
        for (int di = 0; di < entries.Count; di++)
        {
            var (name, doc) = entries[di]; var fb = BaseName(name);
            for (int si = 0; si < doc.Sheets.Count; si++)
                foreach (var n in doc.Sheets[si].Nets)
                {
                    int id = par.Count; par.Add(id); nodes.Add((id, di, si, n));
                    foreach (var t in n.Ids)
                    {
                        if (info.ByName) { Un(id, V("N:" + t.N)); continue; }
                        switch (t.K)
                        {
                            case "hidden": Un(id, V("P:" + t.N)); break;
                            case "power": if (eff != "strict") Un(id, V("P:" + t.N)); break;
                            case "offsheet": if (eff is "flat" or "global") Un(id, V("F:" + t.N)); break;
                            case "global": Un(id, V("L:" + t.N)); break;
                            case "local": if (eff == "global") Un(id, V("L:" + t.N)); break;
                            case "port": if (eff is "flat" or "global") Un(id, V("O:" + t.N)); else Un(id, V("H:" + fb + "|" + t.N)); break;
                            case "hier": Un(id, V("H:" + fb + "|" + t.N)); break;
                            case "sheetpin": if (eff is "hier" or "strict") Un(id, V("H:" + t.Bf + "|" + t.N)); break;
                        }
                    }
                }
        }
        var groups = new Dictionary<int, List<(int id, int doc, int si, SheetNet n)>>();
        foreach (var nd in nodes) { int r = Fd(nd.id); if (!groups.TryGetValue(r, out var g)) groups[r] = g = new(); g.Add(nd); }
        var comp = new CompiledProject { Set = set, Eff = info.ByName ? "byname" : eff };
        var tmp = new List<(ProjectNet p, List<PNetLocal> locals)>();
        foreach (var g in groups.Values)
        {
            var cand = new List<(int pr, string n)>(); var members = new List<PNetMember>(); var seen = new HashSet<string>(); var locals = new List<PNetLocal>(); var files = new HashSet<int>();
            foreach (var nd in g)
            {
                locals.Add(new PNetLocal { DocIndex = nd.doc, Sheet = nd.si, Name = nd.n.Name }); files.Add(nd.doc);
                foreach (var t in nd.n.Ids)
                {
                    int pr = t.K switch
                    {
                        "local" or "global" => 4, "power" or "hidden" or "offsheet" => 3, "port" => info.AllowPort ? 2 : 0,
                        "sheetpin" => info.AllowEntry ? 1 : 0, "hier" => 2, _ => 0
                    };
                    if (pr > 0) cand.Add((pr, t.N));
                }
                foreach (var m in nd.n.Members)
                    if (seen.Add(nd.doc + "|" + m.Ref + "|" + m.Pin))
                        members.Add(new PNetMember { DocIndex = nd.doc, File = entries[nd.doc].name, Ref = m.Ref, Pin = m.Pin, PinName = m.PinName });
            }
            members.Sort((a, b) => { int r = NatCmp(a.Ref, b.Ref); return r != 0 ? r : NatCmp(a.Pin, b.Pin); });
            string name; bool auto = false;
            if (cand.Count > 0) { cand.Sort((a, b) => b.pr != a.pr ? b.pr.CompareTo(a.pr) : NatCmp(a.n, b.n)); name = cand[0].n; }
            else if (members.Count > 0) { name = "Net" + members[0].Ref + "_" + members[0].Pin; auto = true; }
            else { name = g[0].n.Name; auto = g[0].n.Auto; }
            var pn = new ProjectNet { Name = name, Auto = auto, Members = members, Locals = locals, FileCount = files.Count };
            tmp.Add((pn, locals));
        }
        var ordered = tmp.OrderBy(t => t.p.Auto).ThenBy(t => t.p.Name, Natural).ToList();
        for (int i = 0; i < ordered.Count; i++)
        {
            var p = ordered[i].p; p.Id = i; comp.PNets.Add(p);
            foreach (var l in p.Locals) comp.Lookup[(l.DocIndex, l.Sheet, l.Name)] = i;
        }
        comp.Cross = comp.PNets.Count(p => p.FileCount > 1);
        return comp;
    }
}
