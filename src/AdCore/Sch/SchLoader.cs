// Herald PCB Reader — Copyright (C) 2026 Johnny.ch.Tsai
// SPDX-License-Identifier: GPL-3.0-only
// Free software under the GNU General Public License version 3 (LICENSE.txt), WITHOUT ANY WARRANTY; with an
// additional permission to combine it with AvalonDock (Ms-PL), see NOTICE.txt.
using System.Text;
using AdCore.Cfb;

namespace AdCore.Sch;

public static class SchLoader
{
    public static readonly string[] Extensions = { ".schdoc", ".sch", ".kicad_sch" };

    /// <summary>Detect the schematic format from content, parse, and build doc-level nets/parts.</summary>
    public static SchDocument Load(byte[] buf, string fileName, IReadOnlyDictionary<string, KicadLegacy.LibSymbol> legacyLibs = null)
    {
        SchDocument doc;
        string head = Encoding.Latin1.GetString(buf, 0, Math.Min(buf.Length, 400)).TrimStart('﻿', ' ', '\r', '\n', '\t', 'ï', '»', '¿');
        if (CompoundFile.IsCompoundFile(buf) || head.StartsWith("|HEADER=", StringComparison.OrdinalIgnoreCase))
            doc = AltiumSchParser.Parse(buf, fileName);
        else if (head.StartsWith("(kicad_sch"))
            doc = KicadSch.Parse(Encoding.UTF8.GetString(buf), fileName);
        else if (head.StartsWith("EESchema Schematic"))
            doc = KicadLegacy.Parse(Encoding.UTF8.GetString(buf), fileName, legacyLibs);
        else if (head.StartsWith("<?xml") || head.StartsWith("<eagle") || head.Contains("<!DOCTYPE eagle"))
            doc = EagleSch.Parse(Encoding.UTF8.GetString(buf), fileName);
        else
            throw new InvalidDataException("看不懂這個檔案的格式（支援 Altium .SchDoc、KiCad .kicad_sch / .sch、Eagle .sch）。");
        return Connectivity.Finalize(doc);
    }
}
