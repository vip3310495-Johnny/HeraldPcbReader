// Herald PCB Reader — Copyright (C) 2026 Johnny.ch.Tsai
// SPDX-License-Identifier: GPL-3.0-only
// Free software under the GNU General Public License version 3 (LICENSE.txt), WITHOUT ANY WARRANTY; with an
// additional permission to combine it with AvalonDock (Ms-PL), see NOTICE.txt.
using AdCore.Pcb;
using AdCore.Sch;

namespace AdCore.Project;

/// <summary>Map nets between schematic and PCB. Pins (designator + pad) decide first, names only as a fallback,
/// so auto-named nets (NetR60_1) and nets renamed through ports / sheet entries still find the copper they are on.</summary>
public static class NetMatcher
{
    public static int FindPcbNet(PcbBoard b, IEnumerable<(string Ref, string Pin)> pins, IEnumerable<string> names)
    {
        if (b == null) return -1;
        var byDes = new Dictionary<string, int>();
        for (int i = 0; i < b.Comps.Count; i++) if (b.Comps[i].Des != "") byDes[b.Comps[i].Des] = i;
        var want = new HashSet<string>();
        foreach (var (r, p) in pins ?? Enumerable.Empty<(string, string)>())
            if (r != null && byDes.TryGetValue(r, out var ci)) want.Add(ci + "|" + (p ?? "").ToUpperInvariant());
        if (want.Count > 0)
        {
            var votes = new Dictionary<int, int>();
            foreach (var pd in b.Pads)
            {
                if (pd.Net == Layers.None || pd.Comp == Layers.None) continue;
                if (want.Contains(pd.Comp + "|" + pd.Name.ToUpperInvariant())) votes[pd.Net] = votes.GetValueOrDefault(pd.Net) + 1;
            }
            if (votes.Count > 0) return votes.MaxBy(kv => kv.Value).Key;
        }
        var nl = (names ?? Enumerable.Empty<string>()).Where(n => n != null).ToList();
        foreach (var nm in nl) { int i = b.Nets.IndexOf(nm); if (i >= 0) return i; }
        foreach (var nm in nl) { int i = b.Nets.FindIndex(n => string.Equals(n, nm, StringComparison.OrdinalIgnoreCase)); if (i >= 0) return i; }
        return -1;
    }

    public sealed class SchNetHit { public DocItem Doc; public int Sheet; public string LocalName; public ProjectNet PNet; }

    /// <summary>PCB net → schematic net in the group (prefers a local on the document currently shown).</summary>
    public static SchNetHit FindSchNet(ProjectGroup g, PcbBoard b, int netIndex, DocItem preferDoc)
    {
        if (g == null || b == null || netIndex < 0) return null;
        var pads = new HashSet<string>();
        foreach (var pd in b.Pads)
            if (pd.Net == netIndex && pd.Comp >= 0 && pd.Comp < b.Comps.Count) pads.Add(b.Comps[pd.Comp].Des + "|" + pd.Name.ToUpperInvariant());
        string name = b.NetName(netIndex);
        if (g.Compiled != null)
        {
            ProjectNet best = null; int bv = 0;
            foreach (var p in g.Compiled.PNets)
            {
                int v = p.Members.Count(m => pads.Contains(m.Ref + "|" + (m.Pin ?? "").ToUpperInvariant()));
                if (v > bv) { bv = v; best = p; }
            }
            best ??= g.Compiled.PNets.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
            if (best == null) return null;
            var loc = best.Locals.FirstOrDefault(l => g.CompiledDoc(l.DocIndex) == preferDoc) ?? best.Locals[0];
            return new SchNetHit { Doc = g.CompiledDoc(loc.DocIndex), Sheet = loc.Sheet, LocalName = loc.Name, PNet = best };
        }
        SchNetHit hit = null; int bestV = 0;
        foreach (var d in g.SchDocs)
            for (int si = 0; si < d.Sch.Sheets.Count; si++)
                foreach (var n in d.Sch.Sheets[si].Nets)
                {
                    int v = n.Members.Count(m => pads.Contains(m.Ref + "|" + (m.Pin ?? "").ToUpperInvariant())) * 2 + (d == preferDoc ? 1 : 0);
                    if (v == 0 && string.Equals(n.Name, name, StringComparison.OrdinalIgnoreCase)) v = 1;
                    if (v > bestV) { bestV = v; hit = new SchNetHit { Doc = d, Sheet = si, LocalName = n.Name }; }
                }
        return hit;
    }
}
