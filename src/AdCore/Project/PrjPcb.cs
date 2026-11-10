// Herald PCB Reader — Copyright (C) 2026 Johnny.ch.Tsai
// SPDX-License-Identifier: GPL-3.0-only
// Free software under the GNU General Public License version 3 (LICENSE.txt), WITHOUT ANY WARRANTY; with an
// additional permission to combine it with AvalonDock (Ms-PL), see NOTICE.txt.
using System.Text.RegularExpressions;
using static AdCore.Util.TextUtil;

namespace AdCore.Project;

/// <summary>.PrjPcb is an INI text file; [DocumentN] DocumentPath= lists the project's files relative to the project.</summary>
public sealed class PrjPcbInfo
{
    public string Name = "", Path = "";
    public List<string> AllDocs = new();   // as written (relative)
    public List<string> SchDocs = new();
    public List<string> PcbDocs = new();
    public int HierarchyMode;               // 0 auto, 1 flat, 2 hierarchical, 3 global, 4 strict
    public bool AllowPortNetNames, AllowSheetEntryNetNames = true;

    public static PrjPcbInfo Parse(string text, string path = "")
    {
        var info = new PrjPcbInfo { Path = path, Name = System.IO.Path.GetFileNameWithoutExtension(path) };
        text = (text ?? "").TrimStart('﻿');
        string sec = "";
        var design = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in Regex.Split(text, @"\r*\n"))
        {
            var ln = raw.Trim(); if (ln == "") continue;
            var m = Regex.Match(ln, @"^\[(.+)\]$");
            if (m.Success) { sec = m.Groups[1].Value.Trim().ToLowerInvariant(); continue; }
            var kv = Regex.Match(ln, @"^([^=]+)=(.*)$"); if (!kv.Success) continue;
            string k = kv.Groups[1].Value.Trim(), v = kv.Groups[2].Value.Trim();
            if (Regex.IsMatch(sec, @"^document\d+$") && k.Equals("DocumentPath", StringComparison.OrdinalIgnoreCase) && v != "") info.AllDocs.Add(v);
            else if (sec == "design") design[k] = v;
        }
        info.SchDocs = info.AllDocs.Where(d => d.EndsWith(".schdoc", StringComparison.OrdinalIgnoreCase)).ToList();
        info.PcbDocs = info.AllDocs.Where(d => d.EndsWith(".pcbdoc", StringComparison.OrdinalIgnoreCase)).ToList();
        info.HierarchyMode = design.TryGetValue("HierarchyMode", out var hm) ? ParseIntLoose(hm) : 0;
        info.AllowPortNetNames = design.TryGetValue("AllowPortNetNames", out var ap) && ap == "1";
        info.AllowSheetEntryNetNames = !design.TryGetValue("AllowSheetEntryNetNames", out var ae) || ae != "0";
        return info;
    }

    /// <summary>Resolve DocumentPath entries to absolute paths that exist on disk.</summary>
    public IEnumerable<string> ResolveExisting(IEnumerable<string> docs)
    {
        var dir = System.IO.Path.GetDirectoryName(Path) ?? "";
        foreach (var d in docs)
        {
            var rel = d.Replace('\\', System.IO.Path.DirectorySeparatorChar);
            var full = System.IO.Path.GetFullPath(System.IO.Path.IsPathRooted(rel) ? rel : System.IO.Path.Combine(dir, rel));
            if (File.Exists(full)) yield return full;
        }
    }
}
