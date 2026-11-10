// Herald PCB Reader — Copyright (C) 2026 Johnny.ch.Tsai
// SPDX-License-Identifier: GPL-3.0-only
// Free software under the GNU General Public License version 3 (LICENSE.txt), WITHOUT ANY WARRANTY; with an
// additional permission to combine it with AvalonDock (Ms-PL), see NOTICE.txt.
using AdCore.Sch;
using SkiaSharp;

namespace AdCore.Render;

public enum PdfPaper { Auto, A4, A3, Original }

/// <summary>Vector PDF of schematic sheets (same drawing code as the screen).</summary>
public static class PdfExport
{
    public static int ExportSchematics(IReadOnlyList<(SchSheet sheet, string fileName)> items, string path, PdfPaper paper, bool mono, double fontScale = 1, bool showParams = true)
    {
        if (items.Count == 0) return 0;
        const float PtPerMm = 72f / 25.4f;
        double maxW = items.Max(i => i.sheet.Page?.W ?? i.sheet.BBox[2]), maxH = items.Max(i => i.sheet.Page?.H ?? i.sheet.BBox[3]);
        using var stream = File.Create(path);
        using var doc = SKDocument.CreatePdf(stream, new SKDocumentPdfMetadata { Title = Path.GetFileNameWithoutExtension(path), Creator = "Herald PCB Reader", RasterDpi = 300 });
        foreach (var (sh, fileName) in items)
        {
            double w = sh.Page?.W ?? sh.BBox[2] + 10, h = sh.Page?.H ?? sh.BBox[3] + 10;
            double pw, ph;
            switch (paper)
            {
                case PdfPaper.A4: pw = 297; ph = 210; break;
                case PdfPaper.A3: pw = 420; ph = 297; break;
                case PdfPaper.Original: pw = w; ph = h; break;
                default: pw = Math.Max(maxW, maxH * 0); ph = maxH; pw = maxW; break;
            }
            if (paper is PdfPaper.A4 or PdfPaper.A3 && h > w) (pw, ph) = (ph, pw);
            const double M = 5;
            double s = paper == PdfPaper.Original ? 1 : Math.Min((pw - 2 * M) / w, (ph - 2 * M) / h);
            if (paper == PdfPaper.Auto) s = Math.Min(pw / w, ph / h);
            double ox = (pw - w * s) / 2, oy = (ph - h * s) / 2;
            var c = doc.BeginPage((float)(pw * PtPerMm), (float)(ph * PtPerMm));
            c.Scale(PtPerMm);
            c.Translate((float)ox, (float)oy);
            c.Scale((float)s);
            SchRenderer.DrawSheet(c, sh, new SchRenderOptions { Theme = mono ? SchTheme.Mono : SchTheme.Altium, PixelMm = 0.08, FontScale = fontScale, FileName = fileName, ShowParams = showParams });
            doc.EndPage();
        }
        doc.Close();
        return items.Count;
    }
}
