// Herald PCB Reader — Copyright (C) 2026 Johnny.ch.Tsai
// SPDX-License-Identifier: GPL-3.0-only
// Free software under the GNU General Public License version 3 (LICENSE.txt), WITHOUT ANY WARRANTY; with an
// additional permission to combine it with AvalonDock (Ms-PL), see NOTICE.txt.
namespace AdCore.Pcb;

// PCB coordinates are mils, Y up (Altium's own coordinate system).

public static class Layers
{
    public const int None = 0xFFFF;
    public const int Top = 1, Bottom = 32, TopOverlay = 33, BottomOverlay = 34, TopPaste = 35, BottomPaste = 36,
        TopSolder = 37, BottomSolder = 38, DrillGuide = 55, KeepOut = 56, DrillDrawing = 73, MultiLayer = 74;
    public const int V7Mech = 0x01020000;
    // pseudo layers used by the renderer
    public const int PadHoles = 1000, ViaHoles = 1001;

    static readonly Dictionary<int, string> Names = new()
    {
        [1] = "Top Layer", [32] = "Bottom Layer", [33] = "Top Overlay", [34] = "Bottom Overlay", [35] = "Top Paste", [36] = "Bottom Paste",
        [37] = "Top Solder", [38] = "Bottom Solder", [55] = "Drill Guide", [56] = "Keep-Out Layer", [73] = "Drill Drawing", [74] = "Multi-Layer",
        [PadHoles] = "Pad Holes", [ViaHoles] = "Via Holes",
    };
    static readonly Dictionary<string, int> NameToId = new();

    static Layers()
    {
        for (int i = 1; i <= 30; i++) Names[i + 1] = "Mid-Layer " + i;
        for (int i = 1; i <= 16; i++) { Names[38 + i] = "Internal Plane " + i; Names[56 + i] = "Mechanical " + i; }
        foreach (var kv in Names) NameToId[Key(kv.Value)] = kv.Key;
        foreach (var (k, v) in new (string, int)[] { ("TOP", 1), ("BOTTOM", 32), ("TOPOVERLAY", 33), ("BOTTOMOVERLAY", 34), ("TOPPASTE", 35), ("BOTTOMPASTE", 36),
            ("TOPSOLDER", 37), ("BOTTOMSOLDER", 38), ("DRILLGUIDE", 55), ("KEEPOUT", 56), ("DRILLDRAWING", 73), ("MULTILAYER", 74) }) NameToId[k] = v;
        for (int i = 1; i <= 30; i++) NameToId["MID" + i] = i + 1;
        for (int i = 1; i <= 16; i++) { NameToId["PLANE" + i] = 38 + i; NameToId["MECHANICAL" + i] = 56 + i; }
    }

    static string Key(string n) => new string(n.ToUpperInvariant().Where(char.IsLetterOrDigit).ToArray());

    public static string DefaultName(int id)
    {
        if (Names.TryGetValue(id, out var n)) return n;
        if (id > V7Mech && id < V7Mech + 0x10000) return "Mechanical " + (id - V7Mech);
        return "Layer " + id;
    }

    /// <summary>V7 mechanical 1..16 are the same layers as legacy 57..72; keep one id per layer.</summary>
    public static int Norm(int id) => id > V7Mech && id <= V7Mech + 16 ? 56 + (id - V7Mech) : id;
    public static int Pick(int v6, int v7) => Norm(v7 > V7Mech && v7 < V7Mech + 0x10000 ? v7 : v6);

    public static int FromName(string n)
    {
        if (string.IsNullOrEmpty(n)) return 0;
        var k = Key(n);
        if (NameToId.TryGetValue(k, out var id)) return id;
        if (k.StartsWith("MECHANICAL") && int.TryParse(k[10..], out var m)) return Norm(V7Mech + m);
        return 0;
    }

    public static bool IsCopper(int id) => id >= 1 && id <= 32;
    public static bool IsPlane(int id) => id >= 39 && id <= 54;
    public static bool IsMech(int id) => (id >= 57 && id <= 72) || id > V7Mech;
    public static bool IsSignal(int id) => IsCopper(id) || id == MultiLayer;
}

public sealed class PcbTrack { public int L, Net, Poly, Comp; public double X1, Y1, X2, Y2, W; public bool Keepout; }
public sealed class PcbArc { public int L, Net, Poly, Comp; public double Cx, Cy, R, Sa, Ea, W; public bool Keepout; }
public sealed class PadShape { public double Sx, Sy; public int Shape, Alt, Cr; } // shape 1 round, 2 rect, 3 octagon; alt 9 roundrect (cr % of half-min)
public sealed class PcbPad
{
    public string Name = "";
    public int L, Net, Comp, HoleShape, Mode;
    public double X, Y, Hole, Slot, SlotRot, Rot, Hox, Hoy;
    public bool Plated;
    public PadShape Top = new(), Mid = new(), Bot = new();
}
public sealed class PcbVia { public int Net, Comp, LStart, LEnd; public double X, Y, D, Hole; }
public sealed class PcbFill { public int L, Net, Comp; public double X1, Y1, X2, Y2, Rot; public bool Keepout; }
public sealed class RegionVertex { public double X, Y, Cx, Cy, R, Sa, Ea; public bool Round; }
public sealed class PcbRegion
{
    public int L, Net, Poly, Comp, Kind; public bool Cutout, Keepout, Ext;
    public List<RegionVertex> Outline = new();
    public List<List<(double X, double Y)>> Holes = new();
}
public sealed class PcbText
{
    public int L, Comp; public double X, Y, H, Rot, StrokeW; public bool Mirror, IsDes, IsComment, Ttf, Bold, Italic, Inverted, Hidden;
    public string Text = "", Font = "Arial";
}
public sealed class PcbComponent
{
    public int Layer; public double X, Y, Rot, Height; public string Des = "", Pattern = "", Comment = "", LibRef = ""; public bool NameOn, CommentOn; public int PadCount;
    public bool Bottom => Layer == Layers.Bottom;
}
public sealed class PcbPolygon { public int L, Net; public string Hatch = ""; }
public sealed class PcbBody
{
    public int Comp, Type; public bool Bottom, Step; public List<(double X, double Y)> Pts = new();
    public double Standoff, Overall, Opacity = 1, X, Y, CylR, CylH, SphR; public int Color; public string Name = "";
    /// <summary>STEP body: the embedded model it shows (MODELID), and how it is placed: rotations about the model's x, y,
    /// z (degrees, applied x first), the 2D rotation, and the height offset (mil).</summary>
    public string ModelId = ""; public double RotX, RotY, RotZ, Rot2D, Dz;
}

public sealed class PcbBoard
{
    public string FileName = "";
    /// <summary>Where the board was read from: its embedded 3D models are read from there when the 3D view needs them.</summary>
    public string SourcePath = "";
    public List<PcbTrack> Tracks = new();
    public List<PcbArc> Arcs = new();
    public List<PcbPad> Pads = new();
    public List<PcbVia> Vias = new();
    public List<PcbFill> Fills = new();
    public List<PcbRegion> Regions = new();
    public List<PcbText> Texts = new();
    public List<PcbComponent> Comps = new();
    public List<string> Nets = new();
    public List<PcbPolygon> Polys = new();
    public List<RegionVertex> Outline = new();
    public List<PcbBody> Bodies = new();
    public Dictionary<int, string> LayerNames = new();
    public List<int> Stack = new();
    public double OriginX, OriginY, Thickness = 62.99;
    public List<string> Warnings = new();
    public IReadOnlyList<string> Streams = Array.Empty<string>();

    public string LayerName(int id) => LayerNames.TryGetValue(id, out var n) && !string.IsNullOrWhiteSpace(n) ? n : Layers.DefaultName(id);
    public string NetName(int i) => i >= 0 && i < Nets.Count ? Nets[i] : "";
    public int FindComp(string des) => Comps.FindIndex(c => c.Des == des);
}
