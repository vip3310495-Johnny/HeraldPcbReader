// Herald PCB Reader — Copyright (C) 2026 Johnny.ch.Tsai
// SPDX-License-Identifier: GPL-3.0-only
// Free software under the GNU General Public License version 3 (LICENSE.txt), WITHOUT ANY WARRANTY; with an
// additional permission to combine it with AvalonDock (Ms-PL), see NOTICE.txt.
using System.Buffers.Binary;
using AdCore.Cfb;
using AdCore.Util;
using SkiaSharp;
using static AdCore.Util.TextUtil;

namespace AdCore.Sch;

public static class AltiumSchParser
{
    static readonly (double w, double h, string n)[] Sheets =
    {
        (1150,760,"A4"),(1550,1110,"A3"),(2230,1570,"A2"),(3150,2230,"A1"),(4460,3150,"A0"),(950,750,"A"),(1500,950,"B"),(2000,1500,"C"),
        (3200,2000,"D"),(4200,3200,"E"),(1100,850,"Letter"),(1400,850,"Legal"),(1700,1100,"Tabloid"),(990,790,"OrCAD A"),(1540,990,"OrCAD B"),
        (2060,1560,"OrCAD C"),(3260,2060,"OrCAD D"),(4280,3280,"OrCAD E")
    };
    static readonly string[] EType = { "input", "bidirectional", "output", "open_collector", "passive", "tri_state", "open_emitter", "power" };
    static readonly HashSet<string> Graph = new() { "4", "5", "6", "7", "8", "9", "10", "11", "12", "13", "14", "30", "211", "225" };
    const double EM = 0.22225; // em height (mm) per Altium font-size unit

    public static List<Dictionary<string, string>> ReadRecords(byte[] bytes, bool ascii)
    {
        var recs = new List<Dictionary<string, string>>();
        if (ascii)
        {
            var acc = new List<byte>();
            int start = 0;
            for (int i = 0; i <= bytes.Length; i++)
            {
                if (i < bytes.Length && bytes[i] != 0x0A) continue;
                int e = i; if (e > start && bytes[e - 1] == 0x0D) e--;
                var ln = bytes.AsSpan(start, e - start); start = i + 1;
                if (ln.Length >= 2 && ln[^2] == 0x7C && ln[^1] == 0x3E) { acc.AddRange(ln[..^2].ToArray()); continue; }
                if (ln.Length > 0 || acc.Count > 0)
                {
                    acc.AddRange(ln.ToArray());
                    if (acc.Count > 0) recs.Add(AltProps(acc.ToArray()));
                    acc.Clear();
                }
            }
            return recs;
        }
        int o = 0;
        while (o + 4 <= bytes.Length)
        {
            int len = bytes[o] | (bytes[o + 1] << 8) | (bytes[o + 2] << 16), ty = bytes[o + 3];
            o += 4;
            if (o + len > bytes.Length) break;
            var body = bytes.AsSpan(o, len); o += len;
            recs.Add(len == 0 ? new() : ty == 0 ? AltProps(body) : BinPin(body));
        }
        return recs;
    }

    static Dictionary<string, string> BinPin(ReadOnlySpan<byte> b)
    {
        var r = new Dictionary<string, string>();
        int o = 0;
        try
        {
            int I32(ReadOnlySpan<byte> s) { var v = BinaryPrimitives.ReadInt32LittleEndian(s[o..]); o += 4; return v; }
            short I16(ReadOnlySpan<byte> s) { var v = BinaryPrimitives.ReadInt16LittleEndian(s[o..]); o += 2; return v; }
            byte U8(ReadOnlySpan<byte> s) => s[o++];
            string Ps(ReadOnlySpan<byte> s) { int n = s[o++]; var t = DecodeLocal(s.Slice(o, n)); o += n; return t; }
            r["RECORD"] = I32(b).ToString(); U8(b);
            r["OWNERPARTID"] = I16(b).ToString(); r["OWNERPARTDISPLAYMODE"] = U8(b).ToString();
            r["SYMBOL_INNEREDGE"] = U8(b).ToString(); r["SYMBOL_OUTEREDGE"] = U8(b).ToString(); U8(b); U8(b);
            r["DESCRIPTION"] = Ps(b); U8(b);
            r["ELECTRICAL"] = U8(b).ToString(); r["PINCONGLOMERATE"] = U8(b).ToString();
            r["PINLENGTH"] = I16(b).ToString(); r["LOCATION.X"] = I16(b).ToString(); r["LOCATION.Y"] = I16(b).ToString(); I32(b);
            r["NAME"] = Ps(b); r["DESIGNATOR"] = Ps(b); r["_BINARY"] = "1";
        }
        catch { }
        return r;
    }

    static SKColor? Hex(Dictionary<string, string> p, string k)
    {
        if (p == null || !p.TryGetValue(k, out var v) || string.IsNullOrEmpty(v)) return null;
        int c = ParseIntLoose(v, -1);
        if (c < 0) return null;
        return new SKColor((byte)(c & 255), (byte)((c >> 8) & 255), (byte)((c >> 16) & 255));
    }

    static FillKind AltFill(Dictionary<string, string> p)
    {
        if (!Bool(p, "ISSOLID")) return FillKind.None;
        int c = Int(p, "AREACOLOR");
        double lum = 0.299 * (c & 255) + 0.587 * ((c >> 8) & 255) + 0.114 * ((c >> 16) & 255);
        return lum < 110 ? FillKind.Fg : FillKind.Bg;
    }

    static List<P2> Bez(List<P2> p)
    {
        var o = new List<P2>();
        for (int k = 0; k <= 16; k++)
        {
            double t = k / 16.0, u = 1 - t;
            o.Add(new P2(u * u * u * p[0].X + 3 * u * u * t * p[1].X + 3 * u * t * t * p[2].X + t * t * t * p[3].X,
                         u * u * u * p[0].Y + 3 * u * u * t * p[1].Y + 3 * u * t * t * p[2].Y + t * t * t * p[3].Y));
        }
        return o;
    }

    public static SchDocument Parse(byte[] buf, string fileName)
    {
        byte[] bytes; bool ascii = false;
        if (CompoundFile.IsCompoundFile(buf))
        {
            var cfb = new CompoundFile(buf);
            bytes = cfb.Read("FileHeader");
            if (bytes == null)
            {
                var names = cfb.RootNames();
                throw new InvalidDataException(names.Count == 0 || names.Any(n => n.StartsWith("Page") || n.StartsWith("Library") || n.StartsWith("Cache"))
                    ? "這是 OLE 格式但不是 Altium 電路圖，可能是 OrCAD .DSN。"
                    : "這個 OLE 檔沒有 Altium 的 FileHeader 資料，可能不是 .SchDoc。");
            }
        }
        else { bytes = buf; ascii = true; }

        var all = ReadRecords(bytes, ascii);
        var head = all.Count > 0 ? all[0] : new();
        var hs = Str(head, "HEADER");
        if (hs.Contains("Schematic Library", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("這是 Altium 元件庫（.SchLib），不是電路圖。");
        if (hs.Contains("PCB", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("這是 Altium 電路板檔（.PcbDoc），不是電路圖。");
        if (!hs.Contains("Schematic Capture", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("看不懂這個 Altium 檔的標頭：" + (hs == "" ? "（空白）" : hs));

        var R = all.Skip(1).ToList();
        double Au(Dictionary<string, string> p, string k) => (Int(p, k) + Int(p, k + "_FRAC") / 100000.0) * 0.254;

        var sheetRec = R.FirstOrDefault(p => Str(p, "RECORD") == "31") ?? new();
        double W, H; string pname;
        if (Bool(sheetRec, "USECUSTOMSHEET")) { W = Au(sheetRec, "CUSTOMX"); H = Au(sheetRec, "CUSTOMY"); pname = "Custom"; }
        else
        {
            int si = Int(sheetRec, "SHEETSTYLE");
            var st = si >= 0 && si < Sheets.Length ? Sheets[si] : Sheets[0];
            W = st.w * 0.254; H = st.h * 0.254; pname = st.n;
            if (Int(sheetRec, "WORKSPACEORIENTATION") == 1) (W, H) = (H, W);
        }
        if (!(W > 0 && H > 0)) { W = 1150 * 0.254; H = 760 * 0.254; }

        var fonts = new Dictionary<int, int>(); var finfo = new Dictionary<int, SchFont>();
        for (int i = 1; i <= Int(sheetRec, "FONTIDCOUNT"); i++)
        {
            fonts[i] = Int(sheetRec, "SIZE" + i, 10);
            finfo[i] = new SchFont { Family = Str(sheetRec, "FONTNAME" + i, "Times New Roman"), Bold = Bool(sheetRec, "BOLD" + i), Italic = Bool(sheetRec, "ITALIC" + i) };
        }
        int sysFont = fonts.ContainsKey(Int(sheetRec, "SYSTEMFONT", 1)) ? Int(sheetRec, "SYSTEMFONT", 1) : 1;
        int Fid(Dictionary<string, string> p, string k = "FONTID") { int v = Int(p, k); return fonts.ContainsKey(v) ? v : sysFont; }
        double Fsz(Dictionary<string, string> p, string k = "FONTID") => (fonts.TryGetValue(Fid(p, k), out var s) ? s : 10) * EM / SchText.FS;
        SchFont Fnt(Dictionary<string, string> p, string k = "FONTID") => finfo.TryGetValue(Fid(p, k), out var f) ? f : SchFont.Default;
        double X(Dictionary<string, string> p, string k) => Au(p, k);
        double Y(Dictionary<string, string> p, string k) => H - Au(p, k);
        P2 PT(Dictionary<string, string> p, string k) => new(X(p, k + ".X"), Y(p, k + ".Y"));
        List<P2> Pts(Dictionary<string, string> p)
        {
            int n = Int(p, "LOCATIONCOUNT"); var o = new List<P2>();
            for (int i = 1; i <= n; i++) o.Add(new P2(X(p, "X" + i), Y(p, "Y" + i)));
            for (int k = 1; k <= Int(p, "EXTRALOCATIONCOUNT"); k++) o.Add(new P2(X(p, "EX" + (n + k)), Y(p, "EY" + (n + k))));
            return o;
        }
        (string hj, string vj) Just(Dictionary<string, string> p)
        {
            int j = Int(p, "JUSTIFICATION");
            return (new[] { "left", "center", "right" }[((j % 3) + 3) % 3], (j / 3) switch { 0 => "bottom", 1 => "center", 2 => "top", _ => "bottom" });
        }
        P2 TLS(double x, double y) => new(x, -y);
        SchText TxtOf(Dictionary<string, string> p, string text, string role)
        {
            var pt = PT(p, "LOCATION"); var j = Just(p);
            var t = SchText.Place(text, pt.X, pt.Y, Int(p, "ORIENTATION") * 90, j.hj, j.vj, Fsz(p), TLS, role);
            t.Font = Fnt(p); return t;
        }

        var kids = new Dictionary<int, List<int>>();
        void AddKid(int owner, int i) { if (!kids.TryGetValue(owner, out var a)) kids[owner] = a = new(); a.Add(i); }
        for (int i = 0; i < R.Count; i++) { int o = Int(R[i], "OWNERINDEX", -1); if (o >= 0) AddKid(o, i); }
        List<int> K(int i) => kids.TryGetValue(i, out var a) ? a : new();

        var sheetParams = new Dictionary<string, string>();
        foreach (var p in R)
            if (Str(p, "RECORD") == "41" && Int(p, "OWNERINDEX", -1) <= 0 && Str(p, "NAME") != "")
                sheetParams[Str(p, "NAME").ToLowerInvariant()] = Str(p, "TEXT");
        string Resolve(string t, Dictionary<string, string> prm)
        {
            t ??= "";
            if (t.Length == 0 || t[0] != '=') return t;
            var k = t[1..].Trim().ToLowerInvariant();
            if (prm != null && prm.TryGetValue(k, out var v)) return v;
            if (sheetParams.TryGetValue(k, out v)) return v;
            return t[1..].Trim();
        }

        var sh = new SchSheet { Name = fileName };
        bool tpl = R.Any(p => Str(p, "RECORD") == "39");
        int zx = Int(sheetRec, "CUSTOMXZONES"), zy = Int(sheetRec, "CUSTOMYZONES");
        double mw = sheetRec.ContainsKey("CUSTOMMARGINWIDTH") ? Au(sheetRec, "CUSTOMMARGINWIDTH") : 5.08;
        sh.Page = new SchPage
        {
            Name = pname, W = W, H = H, NoTitle = !Bool(sheetRec, "TITLEBLOCKON") || tpl, NoBorder = !Bool(sheetRec, "BORDERON", true),
            Frame = new PageFrame { M = 0.2, M2 = mw, Nx = zx != 0 ? zx : Math.Max(2, (int)Math.Round(W / 70)), Ny = zy != 0 ? zy : Math.Max(2, (int)Math.Round(H / 60)) },
            AreaColor = Hex(sheetRec, "AREACOLOR"),
        };
        string Sp(string k) { return sheetParams.TryGetValue(k.ToLowerInvariant(), out var v) && v != "*" ? v : ""; }
        sh.Title["title"] = Sp("Title"); sh.Title["rev"] = Sp("Revision"); sh.Title["date"] = Sp("Date") != "" ? Sp("Date") : Sp("CurrentDate");
        sh.Title["company"] = Sp("CompanyName") != "" ? Sp("CompanyName") : Sp("Organization");
        sh.Title["sheet"] = Sp("SheetNumber") != "" && Sp("SheetTotal") != "" ? Sp("SheetNumber") + "/" + Sp("SheetTotal") : "";
        if (System.Text.RegularExpressions.Regex.Match(Sp("SheetNumber"), @"\d+") is { Success: true } sn && int.TryParse(sn.Value, out var no)) sh.Number = no;
        if (Sp("DocumentNumber") != "") sh.Title["comment1"] = "Doc No. " + Sp("DocumentNumber");
        if (Sp("DrawnBy") != "" || Sp("Author") != "") sh.Title["comment2"] = "Drawn by " + (Sp("DrawnBy") != "" ? Sp("DrawnBy") : Sp("Author"));

        List<SchPrim> Prim(Dictionary<string, string> p, string role)
        {
            var o = new List<SchPrim>();
            switch (Str(p, "RECORD"))
            {
                case "13": o.Add(new SchPoly { Pts = { PT(p, "LOCATION"), PT(p, "CORNER") } }); break;
                case "6": { var q = Pts(p); if (q.Count > 1) o.Add(new SchPoly { Pts = q }); break; }
                case "7": { var q = Pts(p); if (q.Count > 1) { q.Add(q[0]); o.Add(new SchPoly { Pts = q, Fill = AltFill(p) }); } break; }
                case "5":
                    {
                        var q = Pts(p);
                        for (int k = 0; k + 3 < q.Count; k += 3) o.Add(new SchPoly { Pts = Bez(q.GetRange(k, 4)) });
                        if (q.Count < 4 && q.Count > 1) o.Add(new SchPoly { Pts = q });
                        break;
                    }
                case "14":
                case "10":
                    {
                        var a = PT(p, "LOCATION"); var b = PT(p, "CORNER");
                        o.Add(new SchPoly { Pts = { a, new(b.X, a.Y), b, new(a.X, b.Y), a }, Fill = AltFill(p) }); break;
                    }
                case "8":
                    {
                        var c = PT(p, "LOCATION"); double rx = Au(p, "RADIUS"), ry = Au(p, "SECONDARYRADIUS"); if (ry == 0) ry = rx;
                        if (Math.Abs(rx - ry) < 1e-6) o.Add(new SchCircle { Cx = c.X, Cy = c.Y, R = rx, Fill = AltFill(p) });
                        else
                        {
                            var q = new List<P2>();
                            for (int k = 0; k <= 48; k++) { double a = k / 48.0 * 2 * Math.PI; q.Add(new P2(c.X + rx * Math.Cos(a), c.Y - ry * Math.Sin(a))); }
                            o.Add(new SchPoly { Pts = q, Fill = AltFill(p) });
                        }
                        break;
                    }
                case "12":
                case "11":
                case "9":
                    {
                        string rec = Str(p, "RECORD");
                        var c = PT(p, "LOCATION"); double rx = Au(p, "RADIUS"), ry = rec == "11" ? (Au(p, "SECONDARYRADIUS") is var s2 && s2 != 0 ? s2 : rx) : rx;
                        double a0 = Dbl(p, "STARTANGLE"), a1 = Dbl(p, "ENDANGLE");
                        if (Math.Abs(a1 - a0) < 1e-6 || Math.Abs(Math.Abs(a1 - a0) - 360) < 1e-6) { a0 = 0; a1 = 360; }
                        a1 = SymGeo.AtLeast(a1, a0, 360);
                        double Ca(double a) { a = a * Math.PI / 180; return rec == "11" ? Math.Atan2(rx * Math.Sin(a), ry * Math.Cos(a)) : a; }
                        double t0 = Ca(a0), t1 = SymGeo.AtLeast(Ca(a1), t0, 2 * Math.PI); if (a1 - a0 >= 359.999) t1 = t0 + 2 * Math.PI;
                        int n = Math.Max(8, (int)Math.Ceiling((t1 - t0) / (Math.PI / 24)));
                        var q = new List<P2>();
                        for (int k = 0; k <= n; k++) { double t = t0 + (t1 - t0) * k / n; q.Add(new P2(c.X + rx * Math.Cos(t), c.Y - ry * Math.Sin(t))); }
                        if (rec == "9") { q.Add(c); q.Add(q[0]); o.Add(new SchPoly { Pts = q, Fill = AltFill(p) }); }
                        else o.Add(new SchPoly { Pts = q });
                        break;
                    }
                case "4":
                    {
                        if (Bool(p, "ISHIDDEN")) break;
                        var t = Resolve(Str(p, "TEXT"), null);
                        if (t != "") o.Add(TxtOf(p, AltBar(t), role == "gr" ? "ntxt" : "stxt"));
                        break;
                    }
                case "30":
                    {
                        var a = PT(p, "LOCATION"); var b = PT(p, "CORNER");
                        o.Add(new SchPoly { Pts = { a, new(b.X, a.Y), b, new(a.X, b.Y), a, b }, Dash = true }); break;
                    }
                case "225":
                    {
                        var q = Pts(p);
                        if (q.Count < 2) { var a = PT(p, "LOCATION"); var b = PT(p, "CORNER"); q = new() { a, new(b.X, a.Y), b, new(a.X, b.Y) }; }
                        if (q.Count > 1) { q.Add(q[0]); o.Add(new SchPoly { Pts = q, Fill = Bool(p, "ISSOLID") ? FillKind.Bg : FillKind.None, Extra = "blanket", LineStyle = 1 }); }
                        break;
                    }
                case "211":
                    {
                        var a = PT(p, "LOCATION"); var b = PT(p, "CORNER");
                        o.Add(new SchPoly { Pts = { a, new(b.X, a.Y), b, new(a.X, b.Y), a }, Fill = FillKind.Bg, Extra = "cmask" }); break;
                    }
            }
            bool transp = Bool(p, "TRANSPARENT");
            var sc = Hex(p, "COLOR"); var fc = Hex(p, "AREACOLOR");
            string lsRaw = Str(p, "LINESTYLE", null) is { Length: > 0 } l1 ? l1 : Str(p, "LINESTYLEEXT", null);
            int lw = Math.Clamp(Int(p, "LINEWIDTH"), 0, 3);
            foreach (var q in o)
            {
                if (role != null && q is not SchText) q.Role = role;
                if (q is SchText t) { if (sc != null) t.Color = sc; continue; }
                if (sc != null) q.Stroke = sc;
                if (q is SchPoly pl)
                {
                    if (pl.Fill != FillKind.None && fc != null) pl.FillColor = fc;
                    if (transp && pl.Fill != FillKind.None) pl.Transparent = true;
                }
                else if (q is SchCircle ci && ci.Fill != FillKind.None && fc != null) ci.FillColor = fc;
                int ls = !string.IsNullOrEmpty(lsRaw) ? ParseIntLoose(lsRaw) : q.LineStyle;
                q.LineStyle = ls > 0 && ls < 4 ? ls : 0;
                if (lw > 0) q.LineWidth = lw;
            }
            return o;
        }

        var compIdx = new HashSet<int>();
        for (int i = 0; i < R.Count; i++) if (Str(R[i], "RECORD") == "1") compIdx.Add(i);
        bool OwnedByComp(int i)
        {
            int o = Int(R[i], "OWNERINDEX", -1), g = 0;
            while (o >= 0 && g++ < 8) { if (compIdx.Contains(o)) return true; o = o < R.Count ? Int(R[o], "OWNERINDEX", -1) : -1; }
            return false;
        }
        int missingPins = 0, pwr = 0, lastComp = -1;
        for (int i = 0; i < R.Count; i++)
        {
            var p = R[i];
            if (p.ContainsKey("_BINARY") && !p.ContainsKey("OWNERINDEX") && lastComp >= 0) { p["OWNERINDEX"] = lastComp.ToString(); AddKid(lastComp, i); }
            if (Str(p, "RECORD") == "1") lastComp = i;
        }

        var dirs = new (int x, int y)[] { (1, 0), (0, -1), (-1, 0), (0, 1) };
        for (int i = 0; i < R.Count; i++)
        {
            var p = R[i]; var rec = Str(p, "RECORD");
            if (rec == "1")
            {
                int cur = Int(p, "CURRENTPARTID", 1), mode = Int(p, "DISPLAYMODE"), units = Math.Max(1, Int(p, "PARTCOUNT", 2) - 1);
                bool Vis(Dictionary<string, string> c) { int pid = Int(c, "OWNERPARTID", -1); return (pid <= 0 || pid == cur) && Int(c, "OWNERPARTDISPLAYMODE") == mode; }
                var comp = new SchComponent { Unit = cur, Units = units };
                var prm = new Dictionary<string, string>(); string fp = "";
                var ch = K(i);
                foreach (var ci in ch) { var c = R[ci]; if (Str(c, "RECORD") == "41" && Str(c, "NAME") != "") prm[Str(c, "NAME").ToLowerInvariant()] = Str(c, "TEXT"); }
                foreach (var ci in ch)
                {
                    var c = R[ci]; var cr = Str(c, "RECORD");
                    if (Graph.Contains(cr)) { if (Vis(c)) comp.Prims.AddRange(Prim(c, null)); }
                    else if (cr == "2")
                    {
                        if (!Vis(c)) continue;
                        int pc = Int(c, "PINCONGLOMERATE"), or = pc & 3; var dir = dirs[or];
                        var l = PT(c, "LOCATION"); double len = Au(c, "PINLENGTH");
                        int et = Int(c, "ELECTRICAL");
                        comp.Pins.Add(new SchPin
                        {
                            X = l.X + dir.x * len, Y = l.Y + dir.y * len, Ex = l.X, Ey = l.Y, Dx = -dir.x, Dy = -dir.y,
                            Num = Str(c, "DESIGNATOR"), Name = AltBar(Str(c, "NAME")), Type = et >= 0 && et < EType.Length ? EType[et] : "passive",
                            Inv = Int(c, "SYMBOL_OUTEREDGE") == 1, Color = Hex(c, "COLOR"), Hidden = (pc & 4) != 0, ShowName = (pc & 8) != 0, ShowNum = (pc & 16) != 0,
                            GNet = Str(c, "HIDDENNETNAME"), NameSize = Fsz(c, "NAME_CUSTOMFONTID"), NumSize = Fsz(c, "DESIGNATOR_CUSTOMFONTID"),
                            NameFont = Fnt(c, "NAME_CUSTOMFONTID"), NumFont = Fnt(c, "DESIGNATOR_CUSTOMFONTID"),
                        });
                    }
                    else if (cr == "34")
                    {
                        if (Str(c, "TEXT") != "") comp.Ref = Str(c, "TEXT");
                        var l = PT(c, "LOCATION"); var j = Just(c);
                        comp.Fields.Add(new SchField { Key = "Reference", Val = comp.Ref, X = l.X, Y = l.Y, Ang = Int(c, "ORIENTATION") * 90, HJ = j.hj, VJ = j.vj, Size = Fsz(c), Font = Fnt(c), Hidden = Bool(c, "ISHIDDEN"), Color = Hex(c, "COLOR") });
                    }
                    else if (cr == "41")
                    {
                        if (Str(c, "NAME") == "") continue;
                        var l = PT(c, "LOCATION"); var j = Just(c);
                        comp.Fields.Add(new SchField { Key = Str(c, "NAME"), Val = AltBar(Resolve(Str(c, "TEXT"), prm)), X = l.X, Y = l.Y, Ang = Int(c, "ORIENTATION") * 90, HJ = j.hj, VJ = j.vj, Size = Fsz(c), Font = Fnt(c), Hidden = Bool(c, "ISHIDDEN") || Str(c, "TEXT") == "", Color = Hex(c, "COLOR") });
                    }
                    else if (cr == "44")
                    {
                        Dictionary<string, string> best = null;
                        foreach (var ii in K(ci))
                        {
                            var m = R[ii];
                            if (Str(m, "RECORD") == "45" && Str(m, "MODELTYPE").Contains("PCBLIB", StringComparison.OrdinalIgnoreCase))
                                if (best == null || Bool(m, "ISCURRENT")) best = m;
                        }
                        if (best != null) fp = Str(best, "MODELNAME", fp);
                    }
                }
                string Fv(string k) => comp.Fields.FirstOrDefault(x => x.Key.Equals(k, StringComparison.OrdinalIgnoreCase))?.Val ?? "";
                var value = Fv("comment"); if (value == "" || value == "*") value = Fv("value");
                if (Str(p, "COMPONENTDESCRIPTION") != "") { comp.Description = Str(p, "COMPONENTDESCRIPTION"); comp.Fields.Add(new SchField { Key = "Description", Val = comp.Description, Hidden = true }); }
                var cm = comp.Fields.FirstOrDefault(x => x.Key.Equals("comment", StringComparison.OrdinalIgnoreCase)); if (cm != null) cm.Key = "Value";
                if (comp.Pins.Count == 0) missingPins++;
                int kind = Int(p, "COMPONENTKIND");
                comp.Value = value; comp.Footprint = fp; comp.Lib = Str(p, "LIBREFERENCE") != "" ? Str(p, "LIBREFERENCE") : Str(p, "DESIGNITEMID");
                comp.Datasheet = Fv("datasheet") != "" ? Fv("datasheet") : Fv("componentlink1url");
                comp.InBom = kind != 2 && kind != 4 && kind != 5; comp.Kind = kind;
                comp.X = X(p, "LOCATION.X"); comp.Y = Y(p, "LOCATION.Y"); comp.NameOffset = 1.0;
                sh.Comps.Add(comp.Finish(TLS));
                continue;
            }
            if (OwnedByComp(i)) continue;
            switch (rec)
            {
                case "27":
                case "26":
                    {
                        var q = Pts(p); var sc = Hex(p, "COLOR");
                        for (int k = 0; k + 1 < q.Count; k++) sh.Wires.Add(new SchWire { X1 = q[k].X, Y1 = q[k].Y, X2 = q[k + 1].X, Y2 = q[k + 1].Y, Kind = rec == "27" ? WireKind.Wire : WireKind.Bus, Color = sc });
                        break;
                    }
                case "37":
                    {
                        var a = PT(p, "LOCATION"); var b = PT(p, "CORNER");
                        sh.Wires.Add(new SchWire { X1 = a.X, Y1 = a.Y, X2 = b.X, Y2 = b.Y, Kind = WireKind.Entry, Color = Hex(p, "COLOR") }); break;
                    }
                case "29": { var a = PT(p, "LOCATION"); sh.Junctions.Add(new SchJunction { X = a.X, Y = a.Y, Color = Hex(p, "COLOR") }); break; }
                case "22": { var a = PT(p, "LOCATION"); sh.NoConnects.Add(new SchNoConnect { X = a.X, Y = a.Y, Color = Hex(p, "COLOR") }); break; }
                case "25":
                    {
                        var a = PT(p, "LOCATION");
                        sh.Labels.Add(new SchLabel { X = a.X, Y = a.Y, Dir = Int(p, "ORIENTATION") & 3, Text = AltBar(Str(p, "TEXT")), Size = Fsz(p), Font = Fnt(p), Kind = LabelKind.Local, TextColor = Hex(p, "COLOR") });
                        break;
                    }
                case "17":
                    {
                        var a = PT(p, "LOCATION"); double x = a.X, y = a.Y; int o = Int(p, "ORIENTATION") & 3; var d = dirs[o]; var v = (x: -d.y, y: d.x);
                        int st = Int(p, "STYLE"); var text = AltBar(Str(p, "TEXT"));
                        P2 P(double u, double w) => new(x + d.x * u + v.x * w, y + d.y * u + v.y * w);
                        var prims = new List<SchPrim>(); double tip = 2.0;
                        bool offs = Bool(p, "ISCROSSSHEETCONNECTOR");
                        if (offs)
                        {
                            prims.Add(new SchPoly { Pts = { P(0.9, -0.9), P(0, 0), P(0.9, 0.9) } });
                            prims.Add(new SchPoly { Pts = { P(1.8, -0.9), P(0.9, 0), P(1.8, 0.9) } });
                        }
                        else if (st is 4 or 5 or 6 or 8 or 9)
                        {
                            prims.Add(new SchPoly { Pts = { P(0, 0), P(1.27, 0) } });
                            if (st == 5) prims.Add(new SchPoly { Pts = { P(1.27, -1.27), P(1.27, 1.27), P(2.4, 0), P(1.27, -1.27) } });
                            else
                            {
                                prims.Add(new SchPoly { Pts = { P(1.27, -1.27), P(1.27, 1.27) } });
                                prims.Add(new SchPoly { Pts = { P(1.8, -0.85), P(1.8, 0.85) } });
                                prims.Add(new SchPoly { Pts = { P(2.33, -0.42), P(2.33, 0.42) } });
                            }
                            tip = 2.9;
                        }
                        else if (st is 1 or 7)
                        {
                            prims.Add(new SchPoly { Pts = { P(0, 0), P(1.27, 0) } });
                            prims.Add(new SchPoly { Pts = { P(1.27, -0.75), P(2.3, 0), P(1.27, 0.75), P(1.27, -0.75) }, Fill = FillKind.Fg });
                            tip = 2.8;
                        }
                        else if (st == 0)
                        {
                            prims.Add(new SchPoly { Pts = { P(0, 0), P(1.0, 0) } });
                            var cc = P(1.5, 0); prims.Add(new SchCircle { Cx = cc.X, Cy = cc.Y, R = 0.5 });
                            tip = 2.4;
                        }
                        else
                        {
                            prims.Add(new SchPoly { Pts = { P(0, 0), P(1.27, 0) } });
                            prims.Add(new SchPoly { Pts = { P(1.27, -1.27), P(1.27, 1.27) } });
                            tip = 1.9;
                        }
                        var pc = Hex(p, "COLOR");
                        foreach (var q in prims) { q.Role = "sb"; if (pc != null) { q.Stroke = pc; if (q is SchPoly pp && pp.Fill == FillKind.Fg) pp.FillColor = pc; } }
                        var comp = new SchComponent
                        {
                            Ref = "#PWR" + (++pwr), Value = text, Power = true, OffSheet = offs, InBom = false, X = x, Y = y, Prims = prims, ShowNums = false,
                            Pins = { new SchPin { X = x, Y = y, Ex = x, Ey = y, Dx = -d.x, Dy = -d.y, Num = "1", Name = text, Type = "power_in", Hidden = true } }
                        }.Finish(TLS);
                        if (Bool(p, "SHOWNETNAME", true) && text != "")
                        {
                            var tp = P(tip, 0);
                            comp.FTexts.Add(new SchText
                            {
                                X = tp.X, Y = tp.Y, Text = text, Size = Fsz(p), Role = "val", Color = pc, Font = Fnt(p),
                                HA = o == 0 ? HAnchor.Start : o == 2 ? HAnchor.End : HAnchor.Middle,
                                VA = o == 1 ? VAnchor.Bottom : o == 3 ? VAnchor.Top : VAnchor.Center,
                            });
                        }
                        sh.Comps.Add(comp);
                        break;
                    }
                case "18":
                    {
                        var a = PT(p, "LOCATION"); double w = Au(p, "WIDTH"), h = Au(p, "HEIGHT"); if (w == 0) w = 5.08; if (h == 0) h = 2.54;
                        int st = Int(p, "STYLE"), io = Int(p, "IOTYPE"); bool vert = st >= 4;
                        double x2 = vert ? a.X : a.X + w, y2 = vert ? a.Y - w : a.Y; var name = AltBar(Str(p, "NAME"));
                        var port = new PortGeo { X1 = a.X, Y1 = a.Y, X2 = x2, Y2 = y2, H = h, Vert = vert, StartTip = io == 2 || io == 3, EndTip = io == 0 || io == 1 || io == 3 };
                        sh.Labels.Add(new SchLabel { X = a.X, Y = a.Y, Dir = vert ? 1 : 0, Text = name, Size = Fsz(p), Font = Fnt(p), Kind = LabelKind.Port, Port = port, Stroke = Hex(p, "COLOR"), Fill = Hex(p, "AREACOLOR"), TextColor = Hex(p, "TEXTCOLOR") });
                        sh.Labels.Add(new SchLabel { X = x2, Y = y2, Dir = vert ? 1 : 0, Text = name, Size = Fsz(p), Kind = LabelKind.Port, SkipDraw = true });
                        break;
                    }
                case "15":
                    {
                        var a = PT(p, "LOCATION");
                        var b = new SheetBlock { X = a.X, Y = a.Y, W = Au(p, "XSIZE"), H = Au(p, "YSIZE"), Stroke = Hex(p, "COLOR"), Fill = Hex(p, "AREACOLOR") };
                        foreach (var ci in K(i))
                        {
                            var c = R[ci]; var cr = Str(c, "RECORD");
                            if (cr == "32") { b.Name = Str(c, "TEXT"); b.NameColor = Hex(c, "COLOR"); b.NameFont = Fnt(c); b.NameSize = Fsz(c); }
                            else if (cr == "33") { b.File = Str(c, "TEXT"); b.FileColor = Hex(c, "COLOR"); b.FileFont = Fnt(c); b.FileSize = Fsz(c); }
                            else if (cr == "16")
                            {
                                double dd = (Int(c, "DISTANCEFROMTOP") * 10 + Int(c, "DISTANCEFROMTOP_FRAC1") / 100000.0) * 0.254; int side = Int(c, "SIDE");
                                var pos = side == 0 ? new P2(b.X, b.Y + dd) : side == 1 ? new P2(b.X + b.W, b.Y + dd) : side == 2 ? new P2(b.X + dd, b.Y) : new P2(b.X + dd, b.Y + b.H);
                                sh.Labels.Add(new SchLabel
                                {
                                    X = pos.X, Y = pos.Y, Dir = new[] { 0, 2, 3, 1 }[Math.Clamp(side, 0, 3)], Text = AltBar(Str(c, "NAME")), Size = Fsz(c, "TEXTFONTID"), Font = Fnt(c, "TEXTFONTID"),
                                    Kind = LabelKind.SheetPin, Block = b, Stroke = Hex(c, "COLOR"), Fill = Hex(c, "AREACOLOR"), TextColor = Hex(c, "TEXTCOLOR")
                                });
                            }
                        }
                        sh.Blocks.Add(b);
                        break;
                    }
                case "28":
                case "209":
                    {
                        var a = PT(p, "LOCATION"); var b = PT(p, "CORNER");
                        var t = Str(p, "TEXT").Replace("~1", "\n").Replace("~~", "~");
                        var box = new List<P2> { a, new(b.X, a.Y), b, new(a.X, b.Y), a };
                        string lsr = Str(p, "LINESTYLE", null) is { Length: > 0 } l1 ? l1 : Str(p, "LINESTYLEEXT");
                        int lsT = ParseIntLoose(lsr), lwT = Math.Clamp(Int(p, "LINEWIDTH"), 0, 3);
                        if (Bool(p, "ISSOLID") || rec == "209")
                            sh.Graphics.Add(new SchPoly { Pts = box, Fill = FillKind.Bg, Role = "gr", Extra = "tfill", Transparent = Bool(p, "TRANSPARENT"), FillColor = Hex(p, "AREACOLOR") ?? (rec == "209" ? new SKColor(0xFF, 0xFF, 0xD6) : null) });
                        if (rec == "209" || Bool(p, "SHOWBORDER"))
                            sh.Graphics.Add(new SchPoly { Pts = new(box), Role = "gr", Stroke = Hex(p, "COLOR"), LineStyle = lsT > 0 && lsT < 4 ? lsT : 0, LineWidth = lwT });
                        double mg = p.ContainsKey("TEXTMARGIN") || p.ContainsKey("TEXTMARGIN_FRAC") ? Au(p, "TEXTMARGIN") : 0.127;
                        if (t != "")
                            sh.Texts.Add(new SchText
                            {
                                X = Math.Min(a.X, b.X), Y = Math.Min(a.Y, b.Y), Text = t, Size = Fsz(p), Font = Fnt(p), HA = HAnchor.Start, VA = VAnchor.Top, Role = "ntxt",
                                Color = Hex(p, "TEXTCOLOR") ?? Hex(p, "COLOR"),
                                Frame = new TextFrame { X = Math.Min(a.X, b.X), Y = Math.Min(a.Y, b.Y), W = Math.Abs(b.X - a.X), H = Math.Abs(b.Y - a.Y), Margin = mg, Align = Int(p, "ALIGNMENT", 1), Wrap = !p.ContainsKey("WORDWRAP") || Bool(p, "WORDWRAP") }
                            });
                        break;
                    }
                default:
                    if (Graph.Contains(rec))
                        foreach (var q in Prim(p, "gr")) { if (q is SchText t) sh.Texts.Add(t); else sh.Graphics.Add(q); }
                    break;
            }
        }

        Connectivity.ComputeNets(sh, "altium");
        Connectivity.AddAutoJunctions(sh);

        var doc = new SchDocument { FileName = fileName, Format = "altium", FmtLabel = "Altium · .SchDoc" + (ascii ? " ASCII" : ""), Version = hs };
        doc.Sheets.Add(sh);
        if (missingPins > 0) doc.Warnings.Add(missingPins + " 個零件沒有接腳資料（可能是機構件、Logo 或圖框）。");
        if (R.Any(p => Str(p, "RECORD") is "215" or "216" or "217" or "218")) doc.Warnings.Add("檔案含有 Harness（線束）物件，目前只顯示一般導線與網路。");
        return doc;
    }
}
