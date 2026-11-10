// Herald PCB Reader — Copyright (C) 2026 Johnny.ch.Tsai
// SPDX-License-Identifier: GPL-3.0-only
// Free software under the GNU General Public License version 3 (LICENSE.txt), WITHOUT ANY WARRANTY; with an
// additional permission to combine it with AvalonDock (Ms-PL), see NOTICE.txt.
using System.Diagnostics;
using AdCore.Pcb;
using AdCore.Project;
using AdCore.Sch;

// AdCli stats <path...>          parse files / projects and print counts
// AdCli render <file> <out.png>  render a schematic sheet or PCB to PNG
var cmd = args.Length > 0 ? args[0] : "stats";
if (cmd == "stats")
{
    foreach (var path in args.Skip(1))
    {
        if (path.EndsWith(".prjpcb", StringComparison.OrdinalIgnoreCase))
        {
            var prj = PrjPcbInfo.Parse(File.ReadAllText(path), path);
            var sw = Stopwatch.StartNew();
            var docs = prj.ResolveExisting(prj.SchDocs).Select(f => (Path.GetFileName(f), SchLoader.Load(File.ReadAllBytes(f), Path.GetFileName(f)))).ToList();
            var comp = ProjectCompiler.Compile(docs, new CompileScope { Mode = prj.HierarchyMode, AllowPort = prj.AllowPortNetNames, AllowEntry = prj.AllowSheetEntryNetNames });
            var parts = docs.SelectMany(d => d.Item2.Parts.Select(p => p.Ref)).Distinct().Count();
            Console.WriteLine($"PROJECT {prj.Name}: sch={docs.Count} pcb=[{string.Join(", ", prj.PcbDocs)}] parts={parts} pnets={comp.PNets.Count} cross={comp.Cross} eff={comp.Eff} ({sw.ElapsedMilliseconds} ms)");
            foreach (var (n, d) in docs)
                Console.WriteLine($"  {n}: sheetNo={string.Join(",", d.Sheets.Select(s => s.Number?.ToString() ?? "-"))} comps={d.Sheets[0].Comps.Count} wires={d.Sheets[0].Wires.Count} labels={d.Sheets[0].Labels.Count} nets={d.Nets.Count} parts={d.Parts.Count} warn={string.Join(" | ", d.Warnings)}");
            foreach (var f in prj.ResolveExisting(prj.PcbDocs))
            {
                sw.Restart();
                var b = AltiumPcbParser.Parse(File.ReadAllBytes(f), Path.GetFileName(f));
                Console.WriteLine($"  PCB {Path.GetFileName(f)}: comps={b.Comps.Count} nets={b.Nets.Count} pads={b.Pads.Count} vias={b.Vias.Count} tracks={b.Tracks.Count} arcs={b.Arcs.Count} regions={b.Regions.Count} fills={b.Fills.Count} texts={b.Texts.Count} bodies={b.Bodies.Count} outline={b.Outline.Count} thick={b.Thickness:F1} warn={string.Join(" | ", b.Warnings)} ({sw.ElapsedMilliseconds} ms)");
            }
        }
        else if (path.EndsWith(".pcbdoc", StringComparison.OrdinalIgnoreCase))
        {
            var b = AltiumPcbParser.Parse(File.ReadAllBytes(path), Path.GetFileName(path));
            Console.WriteLine($"PCB {path}: comps={b.Comps.Count} nets={b.Nets.Count} pads={b.Pads.Count} tracks={b.Tracks.Count}");
            foreach (var g in b.Bodies.GroupBy(x => (x.Type, x.Pts.Count >= 3))) Console.WriteLine($"  bodies type={g.Key.Type} outline={g.Key.Item2}: {g.Count()}  e.g. {string.Join(", ", g.Take(4).Select(x => (x.Comp >= 0 && x.Comp < b.Comps.Count ? b.Comps[x.Comp].Des : "-") + ":" + x.Name + $"[{x.Pts.Count}pts r={x.CylR:0.#} h={x.Overall:0.#}]"))}");
        }
        else
        {
            var d = SchLoader.Load(File.ReadAllBytes(path), Path.GetFileName(path));
            Console.WriteLine($"SCH {path}: sheets={d.Sheets.Count} parts={d.Parts.Count} nets={d.Nets.Count} fmt={d.FmtLabel}");
        }
    }
}
else if (cmd == "perf")
{
    var b = AltiumPcbParser.Parse(File.ReadAllBytes(args[1]), "x");
    var sw = Stopwatch.StartNew();
    var scene = new AdCore.Render.PcbScene(b); var st = new AdCore.Render.PcbViewState(scene); st.Fit(1400, 850);
    Console.WriteLine($"scene build {sw.ElapsedMilliseconds} ms, objs {scene.Objs.Count}");
    using var surf = SkiaSharp.SKSurface.Create(new SkiaSharp.SKImageInfo(1400, 850));
    sw.Restart(); AdCore.Render.PcbRenderer.Render(surf.Canvas, scene, st, 1400, 850); surf.Canvas.Flush();
    Console.WriteLine($"first frame (records pictures) {sw.ElapsedMilliseconds} ms");
    for (int k = 0; k < 4; k++) { sw.Restart(); st.ZoomAt(1.6, 700, 400, 1400, 850); AdCore.Render.PcbRenderer.Render(surf.Canvas, scene, st, 1400, 850); surf.Canvas.Flush(); Console.WriteLine($"frame zoom x{Math.Pow(1.6, k + 1):0.0}: {sw.ElapsedMilliseconds} ms"); }
}
else if (cmd == "blur")
{
    // AdCli blur <in.png> <out.png> <sigma> b:x0,y0,x1,y1 ... k:x0,y0,x1,y1 ...
    // blurs the b: rectangles (the design drawn in a screenshot), then puts the k: rectangles (panels over it) back sharp
    using var src = SkiaSharp.SKBitmap.Decode(args[1]);
    float sigma = float.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture);
    SkiaSharp.SKRectI R(string s) { var v = s[2..].Split(',').Select(int.Parse).ToArray(); return new SkiaSharp.SKRectI(v[0], v[1], v[2], v[3]); }
    var rects = args.Skip(4).ToList();
    using var surf = SkiaSharp.SKSurface.Create(new SkiaSharp.SKImageInfo(src.Width, src.Height));
    var c = surf.Canvas;
    using var img = SkiaSharp.SKImage.FromBitmap(src);
    c.DrawImage(img, 0, 0);
    foreach (var r in rects.Where(s => s.StartsWith("b:")).Select(R))
    {
        using var part = img.Subset(r);
        using var f = SkiaSharp.SKImageFilter.CreateBlur(sigma, sigma, SkiaSharp.SKShaderTileMode.Clamp);
        using var p = new SkiaSharp.SKPaint { ImageFilter = f };
        c.Save(); c.ClipRect(r); c.DrawImage(part, r.Left, r.Top, p); c.Restore();
    }
    foreach (var r in rects.Where(s => s.StartsWith("k:")).Select(R))
        c.DrawImage(img, r, r);
    using var outImg0 = surf.Snapshot();
    // o:x0,y0,x1,y1,rx,ry  a see-through panel over the design: its pixels become the panel colour (taken at rx,ry),
    // all but the panel's own grey text and badges (grey and brighter than the 7th number, 95 if left out; 256: none)
    using var res = SkiaSharp.SKBitmap.FromImage(outImg0);
    foreach (var s in rects.Where(s => s.StartsWith("o:")))
    {
        var v = s[2..].Split(',').Select(int.Parse).ToArray();
        var bg = src.GetPixel(v[4], v[5]); int th = v.Length > 6 ? v[6] : 95;
        for (int y = v[1]; y < v[3]; y++)
            for (int x = v[0]; x < v[2]; x++)
            {
                var p = src.GetPixel(x, y);
                int mx = Math.Max(p.Red, Math.Max(p.Green, p.Blue)), mn = Math.Min(p.Red, Math.Min(p.Green, p.Blue));
                res.SetPixel(x, y, mx - mn < 18 && mx > th ? p : bg);
            }
    }
    using var outImg = SkiaSharp.SKImage.FromBitmap(res);
    using var data = outImg.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
    File.WriteAllBytes(args[2], data.ToArray());
    Console.WriteLine($"{args[2]}: {new FileInfo(args[2]).Length} bytes");
}
else if (cmd == "selftest")
{
    // AdCli selftest <a .SchDoc>: damaged files must fail fast (no hang, no huge allocation, no crash)
    var good = File.ReadAllBytes(args[1]);
    void Case(string name, Func<byte[], byte[]> damage)
    {
        var b = damage((byte[])good.Clone()); var sw = Stopwatch.StartNew(); string res;
        var t = Task.Run(() => { try { var d = SchLoader.Load(b, "x.SchDoc"); return $"opened ({d.Parts.Count} parts)"; } catch (Exception e) { return "error: " + e.Message.Split('\n')[0]; } });
        res = t.Wait(5000) ? t.Result : "HANG (over 5 s)";
        Console.WriteLine($"  {name,-34} {sw.ElapsedMilliseconds,5} ms  {res}  (memory {GC.GetTotalMemory(false) / 1048576} MB)");
    }
    uint U32(byte[] b, int o) => BitConverter.ToUInt32(b, o);
    void W32(byte[] b, int o, uint v) => BitConverter.GetBytes(v).CopyTo(b, o);
    int ss = 1 << BitConverter.ToUInt16(good, 30);
    Case("unchanged", b => b);
    Case("sector size 2^30", b => { b[30] = 30; return b; });
    Case("FAT: every sector points to itself", b => { int o = (int)((U32(b, 76) + 1) * ss); for (int i = 0; i < ss / 4; i++) W32(b, o + i * 4, (uint)i); return b; });
    Case("FAT: one chain through all sectors", b => { int o = (int)((U32(b, 76) + 1) * ss); for (int i = 0; i < ss / 4; i++) W32(b, o + i * 4, (uint)(i + 1)); return b; });
    Case("DIFAT: loop, huge count", b => { W32(b, 72, uint.MaxValue); W32(b, 68, 0); W32(b, 44, uint.MaxValue); int o = ss; W32(b, o + ss - 4, 0); return b; });
    Case("directory: entries point at each other", b => { int o = (int)((U32(b, 48) + 1) * ss); for (int e = 0; e < ss / 128; e++) { W32(b, o + e * 128 + 68, 1); W32(b, o + e * 128 + 72, 1); W32(b, o + e * 128 + 76, 1); } return b; });
    Console.WriteLine($"  angles: AtLeast(0, 1e20, 360) = {AdCore.Sch.SymGeo.AtLeast(0, 1e20, 360):G3}, AtLeast(-1e300, 0, 360) = {AdCore.Sch.SymGeo.AtLeast(-1e300, 0, 360):G3}, WrapPi(1e300) = {AdCore.Sch.SymGeo.WrapPi(1e300):G3}, WrapPi(NaN) = {AdCore.Sch.SymGeo.WrapPi(double.NaN)}, WrapPi(-3π) = {AdCore.Sch.SymGeo.WrapPi(-3 * Math.PI):G4}");
}
else if (cmd == "parsetimes")
{
    // AdCli parsetimes <prjpcb...>: read / parse time of every file (one after another), and the project compile
    foreach (var prjPath in args.Skip(1))
    {
        var prj = PrjPcbInfo.Parse(File.ReadAllText(prjPath), prjPath);
        var sw = Stopwatch.StartNew(); long read = 0, parse = 0;
        var docs = new List<(string, AdCore.Sch.SchDocument)>();
        foreach (var f in prj.ResolveExisting(prj.SchDocs).Concat(prj.ResolveExisting(prj.PcbDocs)))
        {
            sw.Restart(); var bytes = File.ReadAllBytes(f); long r = sw.ElapsedMilliseconds; read += r;
            sw.Restart();
            if (f.EndsWith(".pcbdoc", StringComparison.OrdinalIgnoreCase)) AltiumPcbParser.Parse(bytes, Path.GetFileName(f)); else docs.Add((Path.GetFileName(f), SchLoader.Load(bytes, Path.GetFileName(f))));
            long p = sw.ElapsedMilliseconds; parse += p;
            Console.WriteLine($"  {Path.GetFileName(f),-36} {bytes.Length / 1024,7} KB  read {r,4} ms  parse {p,5} ms");
        }
        sw.Restart();
        ProjectCompiler.Compile(docs, new CompileScope { Mode = prj.HierarchyMode, AllowPort = prj.AllowPortNetNames, AllowEntry = prj.AllowSheetEntryNetNames });
        Console.WriteLine($"{prj.Name}: read {read} ms, parse {parse} ms, compile {sw.ElapsedMilliseconds} ms");
    }
}
else if (cmd == "polynets")
{
    // AdCli polynets <pcbdoc>: NET of every pour piece vs the NET of the polygon it belongs to
    var b = AltiumPcbParser.Parse(File.ReadAllBytes(args[1]), "x");
    string N(int n) => n == Layers.None ? "-" : n < b.Nets.Count ? b.Nets[n] : $"#{n}";
    Console.WriteLine($"polys={b.Polys.Count} regions={b.Regions.Count}");
    for (int i = 0; i < b.Polys.Count; i++) Console.WriteLine($"  poly {i}: L={b.Polys[i].L} net={N(b.Polys[i].Net)} hatch={b.Polys[i].Hatch}");
    foreach (var g in b.Regions.GroupBy(r => (r.Poly, r.Net, r.Kind, r.L))) Console.WriteLine($"  region poly={g.Key.Poly} net={N(g.Key.Net)} kind={g.Key.Kind} L={g.Key.L}: {g.Count()}");
    foreach (var g in b.Tracks.Where(t => t.Poly != Layers.None).GroupBy(t => (t.Poly, t.Net))) Console.WriteLine($"  track poly={g.Key.Poly} net={N(g.Key.Net)}: {g.Count()}");
    foreach (var g in b.Arcs.Where(t => t.Poly != Layers.None).GroupBy(t => (t.Poly, t.Net))) Console.WriteLine($"  arc poly={g.Key.Poly} net={N(g.Key.Net)}: {g.Count()}");
}
else if (cmd == "icon") AdCli.Icon.Write(args[1]);
else if (cmd == "docicons") AdCli.Icon.WriteDocs(args[1]);
else if (cmd == "picktest")
{
    // AdCli picktest <pcbdoc> [pxPerMil]: click the centre of every pad and the middle of every track, report what is picked
    var b = AltiumPcbParser.Parse(File.ReadAllBytes(args[1]), "x");
    var scene = new AdCore.Render.PcbScene(b); var st = new AdCore.Render.PcbViewState(scene);
    double S = args.Length > 2 ? double.Parse(args[2]) : 1.0, tol = 5 / S;
    int ok = 0, bad = 0; var why = new Dictionary<string, int>();
    foreach (var o in scene.Objs.Where(o => o.Kind is AdCore.Render.PcbObjKind.Pad or AdCore.Render.PcbObjKind.Track && st.IsVisible(o.L)))
    {
        double x, y;
        if (o.Src is PcbPad pd) { x = pd.X; y = pd.Y; } else { var t = (PcbTrack)o.Src; x = (t.X1 + t.X2) / 2; y = (t.Y1 + t.Y2) / 2; }
        var sel = st.Pick(x, y, tol);
        bool hit = sel != null && !sel.IsComp && sel.Index == o.Index || sel != null && sel.IsComp && o.Comp == sel.Index && o.Kind == AdCore.Render.PcbObjKind.Track;
        if (hit) { ok++; continue; }
        bad++;
        string got = sel == null ? "nothing" : sel.IsComp ? "part" : scene.Objs[sel.Index].Kind + "@" + b.LayerName(scene.Objs[sel.Index].L) + (scene.Objs[sel.Index].Net == o.Net ? "(same net)" : "");
        var k = $"{o.Kind}@{b.LayerName(o.L)} -> {got}"; why[k] = why.GetValueOrDefault(k) + 1;
    }
    Console.WriteLine($"picked itself: {ok}, something else: {bad}");
    foreach (var (k, n) in why.OrderByDescending(kv => kv.Value).Take(15)) Console.WriteLine($"  {n,6}  {k}");
}
else if (cmd == "pick")
{
    // AdCli pick <pcbdoc> <designator> [tolMil]: what lies under the centre of a part
    var b = AltiumPcbParser.Parse(File.ReadAllBytes(args[1]), "x");
    var scene = new AdCore.Render.PcbScene(b); var st = new AdCore.Render.PcbViewState(scene);
    int ci = b.FindComp(args[2]); var bb = scene.CompBox(ci).Value; double tol = args.Length > 3 ? double.Parse(args[3]) : 5;
    Console.WriteLine($"{args[2]} box {bb} centre {bb.MidX:0.#},{bb.MidY:0.#}");
    foreach (var o in scene.Objs.Where(o => o.Kind != AdCore.Render.PcbObjKind.Hole && AdCore.Render.PcbScene.HitObj(o, bb.MidX, bb.MidY, tol)))
        Console.WriteLine($"  {o.Kind} L={b.LayerName(o.L)} comp={(o.Comp >= 0 && o.Comp < b.Comps.Count ? b.Comps[o.Comp].Des : "-")} net={b.NetName(o.Net)}");
    for (int i = 0; i < b.Comps.Count; i++) if (scene.CompArea[i] is { } r && r.Contains(bb.MidX, bb.MidY)) Console.WriteLine($"  area of {b.Comps[i].Des} {r.Width:0}x{r.Height:0}");
    var sel = st.Pick(bb.MidX, bb.MidY, tol);
    Console.WriteLine("  PICK → " + (sel == null ? "none" : sel.IsComp ? "comp " + b.Comps[sel.Index].Des : scene.Objs[sel.Index].Kind + " " + b.LayerName(scene.Objs[sel.Index].L)));
}
else if (cmd == "render")
{
    AdCli.Render.Run(args[1], args[2], args.Length > 3 ? args[3] : null);
}
