// Herald PCB Reader — Copyright (C) 2026 Johnny.ch.Tsai
// SPDX-License-Identifier: GPL-3.0-only
// Free software under the GNU General Public License version 3 (LICENSE.txt), WITHOUT ANY WARRANTY; with an
// additional permission to combine it with AvalonDock (Ms-PL), see NOTICE.txt.
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using AdCore.Cfb;
using static AdCore.Util.TextUtil;

namespace AdCore.Pcb;

public static class AltiumPcbParser
{
    sealed class Rd
    {
        public readonly byte[] B; public int P, End;
        public Rd(byte[] b) { B = b; }
        public int Left => B.Length - P;
        public int SubLeft => End - P;
        public byte U8() => B[P++];
        public ushort U16() { var v = BinaryPrimitives.ReadUInt16LittleEndian(B.AsSpan(P)); P += 2; return v; }
        public uint U32() { var v = BinaryPrimitives.ReadUInt32LittleEndian(B.AsSpan(P)); P += 4; return v; }
        public int I32() { var v = BinaryPrimitives.ReadInt32LittleEndian(B.AsSpan(P)); P += 4; return v; }
        public double F64() { var v = BinaryPrimitives.ReadDoubleLittleEndian(B.AsSpan(P)); P += 8; return v; }
        public double Mil() => I32() / 10000.0;
        public (double, double) Pt() => (Mil(), Mil());
        public void Skip(int n) => P += n;
        public int Sub() { int l = (int)U32(); End = P + l; return l; }
        public void SkipSub() => P = End;
        public string PStr() { int n = U8(); var s = Latin1.GetString(B, P, Math.Min(n, B.Length - P)); P += n; return s; }
        public Dictionary<string, string> Props()
        {
            uint len = U32(); bool bin = (len & 0xff000000) != 0; int l = (int)(len & 0xffffff);
            int a = P; P += l;
            if (bin || l == 0) return new();
            int b = a + l; if (b > 0 && B[b - 1] == 0) b--;
            return Scan(a, b);
        }

        /// <summary>"KEY=value|KEY=value…" straight from the bytes (Latin-1): keys trimmed and upper case, values trimmed,
        /// the first of a repeated key kept; "%UTF8%KEY" values are UTF-8 and replace the plain ones.</summary>
        Dictionary<string, string> Scan(int a, int b)
        {
            static bool Ws(byte c) => c == 0x20 || c >= 0x09 && c <= 0x0D || c == 0x85 || c == 0xA0;   // char.IsWhiteSpace over Latin-1
            var kv = new Dictionary<string, string>();
            for (int i = a; i < b;)
            {
                int end = Array.IndexOf(B, (byte)'|', i, b - i); if (end < 0) end = b;
                int eq = Array.IndexOf(B, (byte)'=', i, end - i);
                if (eq >= 0)
                {
                    int ks = i, ke = eq; while (ks < ke && Ws(B[ks])) ks++; while (ke > ks && Ws(B[ke - 1])) ke--;
                    var k = Latin1.GetString(B, ks, ke - ks).ToUpperInvariant();
                    if (k.StartsWith("%UTF8%")) kv[k[6..]] = Encoding.UTF8.GetString(B, eq + 1, end - eq - 1).Trim();
                    else if (!kv.ContainsKey(k))
                    {
                        int vs = eq + 1, ve = end; while (vs < ve && Ws(B[vs])) vs++; while (ve > vs && Ws(B[ve - 1])) ve--;
                        kv[k] = Latin1.GetString(B, vs, ve - vs);
                    }
                }
                i = end + 1;
            }
            return kv;
        }
    }

    static string Utf16(byte[] b, int a, int e)
    {
        var sb = new StringBuilder();
        for (int i = a; i + 1 < e && i + 1 < b.Length; i += 2) { int c = b[i] | (b[i + 1] << 8); if (c == 0) break; sb.Append((char)c); }
        return sb.ToString();
    }

    /// <summary>"12.5mil" → 12.5 (mil).</summary>
    public static double PMil(string v)
    {
        if (string.IsNullOrEmpty(v)) return 0;
        var m = Regex.Match(v.Trim(), @"^([-+0-9.eE]+)\s*mil$", RegexOptions.IgnoreCase);
        var s = m.Success ? m.Groups[1].Value : v;
        var m2 = Regex.Match(s.Trim(), @"^[-+]?[0-9]*\.?[0-9]+([eE][-+]?[0-9]+)?");
        return m2.Success && double.TryParse(m2.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;
    }
    static string G(Dictionary<string, string> p, string k) => p.TryGetValue(k, out var v) ? v : null;

    public static PcbBoard Parse(byte[] buf, string fileName)
    {
        var cfb = new CompoundFile(buf);
        var B = new PcbBoard { FileName = fileName };
        byte[] S(string n) => cfb.Read(n, "Data");
        int Each(string name, Action<Rd> fn)
        {
            var d = S(name); if (d == null) return 0;
            var r = new Rd(d); int n = 0;
            try { while (r.Left >= 4) { int start = r.P; fn(r); n++; if (r.P <= start) break; } }
            catch (Exception e) { B.Warnings.Add($"{name}: {e.Message}（已讀 {n} 筆）"); }
            return n;
        }

        var b6 = S("Board6") ?? throw new InvalidDataException("找不到 Board6 串流，這可能不是 PCB 檔（或是 PcbLib）。");
        var bp = new Rd(b6).Props();
        for (int i = 1; i < 100; i++) { var nm = G(bp, "LAYER" + i + "NAME"); if (nm == null) break; B.LayerNames[i] = nm; }
        for (int i = 0; i < 1000; i++)
        {
            var nm = G(bp, "LAYERV7_" + i + "NAME"); if (nm == null) break;
            int id = Layers.Norm(ParseIntLoose(G(bp, "LAYERV7_" + i + "LAYERID")));
            if (id != 0 && !B.LayerNames.ContainsKey(id)) B.LayerNames[id] = nm;
        }
        {
            var seen = new HashSet<int>(); int cur = 1;
            while (cur != 0 && !seen.Contains(cur) && cur < 100) { seen.Add(cur); B.Stack.Add(cur); if (cur == 32) break; cur = ParseIntLoose(G(bp, "LAYER" + cur + "NEXT")); }
            if (!B.Stack.Contains(32)) B.Stack.Add(32);
        }
        for (int i = 0; i < 100000; i++)
        {
            if (G(bp, "VX" + i) == null) break;
            B.Outline.Add(new RegionVertex
            {
                X = PMil(G(bp, "VX" + i)), Y = PMil(G(bp, "VY" + i)), Round = ParseIntLoose(G(bp, "KIND" + i)) != 0,
                Cx = PMil(G(bp, "CX" + i)), Cy = PMil(G(bp, "CY" + i)), R = PMil(G(bp, "R" + i)), Sa = Dbl(bp, "SA" + i), Ea = Dbl(bp, "EA" + i)
            });
        }
        B.OriginX = PMil(G(bp, "ORIGINX")); B.OriginY = PMil(G(bp, "ORIGINY"));
        {
            double t = 0; bool ok = true;
            for (int i = 0; i < B.Stack.Count; i++)
            {
                int L = B.Stack[i]; var ct = G(bp, "LAYER" + L + "COPTHICK"); var dh = G(bp, "LAYER" + L + "DIELHEIGHT");
                if (ct == null) ok = false;
                t += PMil(ct ?? "1.4mil"); if (i < B.Stack.Count - 1) t += PMil(dh ?? "0mil");
            }
            B.Thickness = ok && t >= 8 && t <= 400 ? t : 62.99;
        }

        var ws = new Dictionary<uint, string>();
        {
            var d = S("WideStrings6");
            if (d != null)
            {
                var r = new Rd(d);
                while (r.Left >= 8) { uint idx = r.U32(), len = r.U32(); if (len > r.Left) break; ws[idx] = len <= 2 ? "" : Utf16(d, r.P, r.P + (int)len - 2); r.Skip((int)len); }
            }
        }

        Each("Nets6", r => { var p = r.Props(); B.Nets.Add(G(p, "NAME") ?? ""); });
        Each("Components6", r =>
        {
            var p = r.Props();
            B.Comps.Add(new PcbComponent
            {
                Layer = Layers.FromName(G(p, "LAYER")), X = PMil(G(p, "X")), Y = PMil(G(p, "Y")), Rot = Dbl(p, "ROTATION"), Des = G(p, "SOURCEDESIGNATOR") ?? "",
                Pattern = G(p, "PATTERN") ?? "", LibRef = G(p, "SOURCELIBREFERENCE") ?? "", Height = PMil(G(p, "HEIGHT")),
                NameOn = !(G(p, "NAMEON") ?? "TRUE").StartsWith("F", StringComparison.OrdinalIgnoreCase),
                CommentOn = (G(p, "COMMENTON") ?? "FALSE").StartsWith("T", StringComparison.OrdinalIgnoreCase),
            });
        });
        Each("Polygons6", r =>
        {
            var p = r.Props();
            B.Polys.Add(new PcbPolygon { L = Layers.Pick(Layers.FromName(G(p, "LAYER")), Layers.FromName(G(p, "LAYER_V7"))), Net = G(p, "NET") != null ? ParseIntLoose(G(p, "NET")) : Layers.None, Hatch = G(p, "HATCHSTYLE") ?? "" });
        });
        Each("ComponentBodies6", r =>
        {
            if (r.U8() != 12) throw new InvalidDataException("記錄類型錯誤");
            r.Sub(); r.Skip(7); int comp = r.U16(); r.Skip(9);
            var p = r.Props();
            var body = new PcbBody { Comp = comp };
            if (r.SubLeft >= 4) { uint n = r.U32(); for (int i = 0; i < n && r.SubLeft >= 16; i++) { double x = r.F64() / 10000, y = r.F64() / 10000; body.Pts.Add((x, y)); } }
            r.SkipSub();
            body.Type = ParseIntLoose(G(p, "MODEL.MODELTYPE")); body.Bottom = ParseIntLoose(G(p, "BODYPROJECTION")) == 1;
            body.Standoff = PMil(G(p, "STANDOFFHEIGHT")); body.Overall = PMil(G(p, "OVERALLHEIGHT")); body.Color = ParseIntLoose(G(p, "BODYCOLOR3D"), 0x808080);
            body.Opacity = G(p, "BODYOPACITY3D") != null ? Dbl(p, "BODYOPACITY3D", 1) : 1;
            body.X = PMil(G(p, "MODEL.2D.X")); body.Y = PMil(G(p, "MODEL.2D.Y")); body.CylR = PMil(G(p, "MODEL.CYLINDER.RADIUS")); body.CylH = PMil(G(p, "MODEL.CYLINDER.HEIGHT"));
            body.SphR = PMil(G(p, "MODEL.SPHERE.RADIUS")); body.Name = G(p, "MODEL.NAME") ?? ""; body.Step = body.Type == 1;
            body.ModelId = G(p, "MODELID") ?? ""; body.RotX = Dbl(p, "MODEL.3D.ROTX"); body.RotY = Dbl(p, "MODEL.3D.ROTY"); body.RotZ = Dbl(p, "MODEL.3D.ROTZ");
            body.Rot2D = Dbl(p, "MODEL.2D.ROTATION"); body.Dz = PMil(G(p, "MODEL.3D.DZ"));
            B.Bodies.Add(body);
        });
        Each("Tracks6", r =>
        {
            if (r.U8() != 4) throw new InvalidDataException("記錄類型錯誤");
            r.Sub(); int l6 = r.U8(); r.U8(); int f2 = r.U8();
            int net = r.U16(), poly = r.U16(), comp = r.U16(); r.Skip(4);
            var a = r.Pt(); var b = r.Pt(); double w = r.Mil(); r.Skip(3);
            int l7 = 0; if (r.SubLeft >= 9) { r.Skip(5); l7 = (int)r.U32(); }
            B.Tracks.Add(new PcbTrack { L = Layers.Pick(l6, l7), Net = net, Poly = poly, Comp = comp, X1 = a.Item1, Y1 = a.Item2, X2 = b.Item1, Y2 = b.Item2, W = w, Keepout = f2 == 2 });
            r.SkipSub();
        });
        Each("Arcs6", r =>
        {
            if (r.U8() != 1) throw new InvalidDataException("記錄類型錯誤");
            r.Sub(); int l6 = r.U8(); r.U8(); int f2 = r.U8();
            int net = r.U16(), poly = r.U16(), comp = r.U16(); r.Skip(4);
            var c = r.Pt(); double rad = r.Mil(), sa = r.F64(), ea = r.F64(), w = r.Mil(); r.Skip(2);
            int l7 = 0; if (r.SubLeft >= 9) { r.Skip(5); l7 = (int)r.U32(); }
            B.Arcs.Add(new PcbArc { L = Layers.Pick(l6, l7), Net = net, Poly = poly, Comp = comp, Cx = c.Item1, Cy = c.Item2, R = rad, Sa = sa, Ea = ea, W = w, Keepout = f2 == 2 });
            r.SkipSub();
        });
        Each("Pads6", r =>
        {
            if (r.U8() != 2) throw new InvalidDataException("記錄類型錯誤");
            r.Sub(); var name = r.PStr(); r.SkipSub();
            r.Sub(); r.SkipSub(); r.Sub(); r.SkipSub(); r.Sub(); r.SkipSub();
            r.Sub();
            int l6 = r.U8(); r.U8(); r.U8();
            int net = r.U16(); r.Skip(2); int comp = r.U16(); r.Skip(4);
            var pos = r.Pt(); var ts = r.Pt(); var ms = r.Pt(); var bs = r.Pt(); double hole = r.Mil();
            int tsh = r.U8(), msh = r.U8(), bsh = r.U8();
            double dir = r.F64(); bool plated = r.U8() != 0; r.Skip(1); int mode = r.U8();
            r.SkipSub();
            int holeShape = 0, alt0 = 0, cr0 = 0, altB = 0, crB = 0; double slot = 0, slotRot = 0, hox = 0, hoy = 0;
            int len6 = r.Sub();
            if (len6 >= 596)
            {
                r.Skip(29 * 4 * 2 + 29 + 1);
                holeShape = r.U8(); slot = r.Mil(); slotRot = r.F64();
                hox = r.Mil(); r.Skip(31 * 4); hoy = r.Mil(); r.Skip(31 * 4); r.Skip(1);
                var alt = new int[32]; for (int i = 0; i < 32; i++) alt[i] = r.U8();
                var cr = new int[32]; for (int i = 0; i < 32; i++) cr[i] = r.U8();
                alt0 = alt[0]; cr0 = cr[0]; altB = alt[31]; crB = cr[31];
            }
            r.SkipSub();
            B.Pads.Add(new PcbPad
            {
                Name = name, L = l6, Net = net, Comp = comp, X = pos.Item1, Y = pos.Item2,
                Top = new PadShape { Sx = ts.Item1, Sy = ts.Item2, Shape = tsh, Alt = alt0, Cr = cr0 },
                Mid = new PadShape { Sx = ms.Item1, Sy = ms.Item2, Shape = msh },
                Bot = new PadShape { Sx = bs.Item1, Sy = bs.Item2, Shape = bsh, Alt = altB, Cr = crB },
                Hole = hole, HoleShape = holeShape, Slot = slot, SlotRot = slotRot, Plated = plated, Rot = dir, Mode = mode, Hox = hox, Hoy = hoy,
            });
        });
        Each("Vias6", r =>
        {
            if (r.U8() != 3) throw new InvalidDataException("記錄類型錯誤");
            r.Sub(); r.Skip(1); r.U8(); r.U8();
            int net = r.U16(); r.Skip(2); int comp = r.U16(); r.Skip(4);
            var pos = r.Pt(); double d = r.Mil(), hole = r.Mil(); int ls = r.U8(), le = r.U8();
            B.Vias.Add(new PcbVia { Net = net, Comp = comp, X = pos.Item1, Y = pos.Item2, D = d, Hole = hole, LStart = ls, LEnd = le });
            r.SkipSub();
        });
        Each("Fills6", r =>
        {
            if (r.U8() != 6) throw new InvalidDataException("記錄類型錯誤");
            r.Sub(); int l6 = r.U8(); r.U8(); int f2 = r.U8();
            int net = r.U16(); r.Skip(2); int comp = r.U16(); r.Skip(4);
            var a = r.Pt(); var b = r.Pt(); double rot = r.F64();
            int l7 = 0; if (r.SubLeft >= 9) { r.Skip(5); l7 = (int)r.U32(); }
            B.Fills.Add(new PcbFill { L = Layers.Pick(l6, l7), Net = net, Comp = comp, X1 = a.Item1, Y1 = a.Item2, X2 = b.Item1, Y2 = b.Item2, Rot = rot, Keepout = f2 == 2 });
            r.SkipSub();
        });
        Action<Rd> Region(bool ext) => r =>
        {
            if (r.U8() != 11) throw new InvalidDataException("記錄類型錯誤");
            r.Sub(); int l6 = r.U8(); r.U8(); int f2 = r.U8();
            int net = r.U16(), poly = r.U16(), comp = r.U16(); r.Skip(5);
            int holes = r.U16(); r.Skip(2);
            var p = r.Props();
            int l7 = Layers.FromName(G(p, "V7_LAYER"));
            int kind = ParseIntLoose(G(p, "KIND")); bool cutout = (G(p, "ISBOARDCUTOUT") ?? "").StartsWith("T", StringComparison.OrdinalIgnoreCase);
            long n = r.U32(); if (ext) n++;
            var reg = new PcbRegion { L = Layers.Pick(l6, l7), Net = net, Poly = poly, Comp = comp, Kind = kind, Cutout = cutout, Keepout = f2 == 2, Ext = ext };
            for (long i = 0; i < n; i++)
            {
                if (ext)
                {
                    bool round = r.U8() != 0; var q = r.Pt(); var c = r.Pt(); double rad = r.Mil(), a1 = r.F64(), a2 = r.F64();
                    reg.Outline.Add(new RegionVertex { X = q.Item1, Y = q.Item2, Round = round, Cx = c.Item1, Cy = c.Item2, R = rad, Sa = a1, Ea = a2 });
                }
                else { double x = r.F64() / 10000, y = r.F64() / 10000; reg.Outline.Add(new RegionVertex { X = x, Y = y }); }
            }
            for (int k = 0; k < holes; k++)
            {
                uint m = r.U32(); var h = new List<(double, double)>();
                for (int i = 0; i < m; i++) h.Add((r.F64() / 10000, r.F64() / 10000));
                reg.Holes.Add(h);
            }
            B.Regions.Add(reg);
            r.SkipSub();
        };
        int nSB = Each("ShapeBasedRegions6", Region(true));
        int sbCount = B.Regions.Count;
        Each("Regions6", Region(false));
        if (nSB > 0)
        {
            var polysInR6 = new HashSet<int>(B.Regions.Skip(sbCount).Select(g => g.Poly));
            B.Regions = B.Regions.Where((g, i) => i < sbCount ? (g.Poly == Layers.None || !polysInR6.Contains(g.Poly)) : g.Poly != Layers.None).ToList();
        }
        // the pieces of a pour (regions, hatch tracks / arcs) are stored without a NET: only their polygon has one
        int PolyNet(int net, int poly, int l) => net == Layers.None && poly != Layers.None && poly < B.Polys.Count && B.Polys[poly].L == l ? B.Polys[poly].Net : net;
        foreach (var g in B.Regions) g.Net = PolyNet(g.Net, g.Poly, g.L);
        foreach (var t in B.Tracks) t.Net = PolyNet(t.Net, t.Poly, t.L);
        foreach (var a in B.Arcs) a.Net = PolyNet(a.Net, a.Poly, a.L);

        Each("Texts6", r =>
        {
            if (r.U8() != 5) throw new InvalidDataException("記錄類型錯誤");
            int len1 = r.Sub(); int l6 = r.U8(); r.Skip(6); int comp = r.U16(); r.Skip(4);
            var pos = r.Pt(); double h = r.Mil(); r.U16(); double rot = r.F64(); bool mir = r.U8() != 0; double sw = r.Mil();
            bool isComment = false, isDes = false, bold = false, italic = false, inverted = false; int ftype = 0, l7 = 0; long wsi = -1; string font = "";
            if (len1 >= 123)
            {
                isComment = r.U8() != 0; isDes = r.U8() != 0; r.Skip(1); ftype = r.U8(); bold = r.U8() != 0; italic = r.U8() != 0;
                font = Utf16(r.B, r.P, r.P + 64); r.Skip(64); inverted = r.U8() != 0; r.Skip(4); wsi = r.U32(); r.Skip(4);
                r.Skip(1 + 8 + 1 + 4);
                if (r.SubLeft >= 93) { r.Skip(8 + 8 + 4 + 1 + 1 + 1); ftype = r.U8(); r.Skip(64 + 1); l7 = (int)r.U32(); }
            }
            r.SkipSub();
            r.Sub(); string text = wsi >= 0 && ws.TryGetValue((uint)wsi, out var wv) ? wv : r.PStr(); r.SkipSub();
            B.Texts.Add(new PcbText
            {
                L = Layers.Pick(l6, l7), Comp = comp, X = pos.Item1, Y = pos.Item2, H = h, Rot = rot, Mirror = mir, StrokeW = sw, Text = text.Replace("\r\n", "\n"),
                IsDes = isDes, IsComment = isComment, Ttf = ftype == 1, Bold = bold, Italic = italic, Font = font == "" ? "Arial" : font, Inverted = inverted
            });
        });

        foreach (var t in B.Texts)
        {
            var c = t.Comp >= 0 && t.Comp < B.Comps.Count ? B.Comps[t.Comp] : null;
            if (c != null)
            {
                if (t.IsDes) { t.Text = c.Des != "" ? c.Des : t.Text; t.Hidden = !c.NameOn; }
                if (t.IsComment) { c.Comment = t.Text; t.Hidden = !c.CommentOn; }
            }
            if (t.Text == ".Designator" && c != null) t.Text = c.Des;
        }
        foreach (var p in B.Pads) if (p.Comp >= 0 && p.Comp < B.Comps.Count) B.Comps[p.Comp].PadCount++;
        B.Streams = cfb.RootNames();
        return B;
    }
}
