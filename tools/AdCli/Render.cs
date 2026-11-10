// Herald PCB Reader — Copyright (C) 2026 Johnny.ch.Tsai
// SPDX-License-Identifier: GPL-3.0-only
// Free software under the GNU General Public License version 3 (LICENSE.txt), WITHOUT ANY WARRANTY; with an
// additional permission to combine it with AvalonDock (Ms-PL), see NOTICE.txt.
using AdCore.Pcb;
using AdCore.Render;
using AdCore.Sch;
using SkiaSharp;

namespace AdCli;

static class Render
{
    public static void Run(string input, string output, string opt)
    {
        var bytes = File.ReadAllBytes(input);
        if (input.EndsWith(".pcbdoc", StringComparison.OrdinalIgnoreCase))
        {
            var b = AltiumPcbParser.Parse(bytes, Path.GetFileName(input));
            var scene = new PcbScene(b);
            int W = 2000, H = 1400;
            var st = new PcbViewState(scene);
            st.Fit(W, H);
            if (opt == "hl" && b.Nets.Count > 0) { st.HlNets.Add(b.Nets.FindIndex(n => n == "GND") is var g && g >= 0 ? g : 0); scene.RebuildHighlight(st); }
            using var surf = SKSurface.Create(new SKImageInfo(W, H));
            PcbRenderer.Render(surf.Canvas, scene, st, W, H);
            Save(surf, output);
            return;
        }
        var doc = SchLoader.Load(bytes, Path.GetFileName(input));
        var sh = doc.Sheets[0];
        double pxPerMm = opt != null && double.TryParse(opt, out var v) ? v : 6;
        double x0 = sh.Page != null ? 0 : sh.BBox[0] - 5, y0 = sh.Page != null ? 0 : sh.BBox[1] - 5;
        double w = sh.Page?.W ?? sh.BBox[2] - sh.BBox[0] + 10, h = sh.Page?.H ?? sh.BBox[3] - sh.BBox[1] + 10;
        using (var surf = SKSurface.Create(new SKImageInfo((int)(w * pxPerMm), (int)(h * pxPerMm))))
        {
            var c = surf.Canvas;
            c.Clear(SKColors.LightGray);
            c.Scale((float)pxPerMm);
            c.Translate((float)-x0, (float)-y0);
            SchRenderer.DrawSheet(c, sh, new SchRenderOptions { PixelMm = 1 / pxPerMm, FileName = doc.FileName });
            Save(surf, output);
        }
    }

    static void Save(SKSurface s, string path)
    {
        using var img = s.Snapshot();
        using var data = img.Encode(SKEncodedImageFormat.Png, 90);
        File.WriteAllBytes(path, data.ToArray());
        Console.WriteLine("wrote " + path);
    }
}
