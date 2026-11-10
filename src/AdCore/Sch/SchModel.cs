// Herald PCB Reader — Copyright (C) 2026 Johnny.ch.Tsai
// SPDX-License-Identifier: GPL-3.0-only
// Free software under the GNU General Public License version 3 (LICENSE.txt), WITHOUT ANY WARRANTY; with an
// additional permission to combine it with AvalonDock (Ms-PL), see NOTICE.txt.
using SkiaSharp;

namespace AdCore.Sch;

// All schematic coordinates are millimetres on the sheet, Y pointing down (sheet top-left = 0,0).
// "Size" of text follows the convention: em height (mm) = Size * SchText.FS.

public record struct P2(double X, double Y);

public enum FillKind { None, Fg, Bg }
public enum HAnchor { Start, Middle, End }
public enum VAnchor { Top, Center, Bottom }
public enum WireKind { Wire, Bus, Entry }
public enum LabelKind { Local, Global, Port, SheetPin, Hier }

public sealed class SchFont
{
    public string Family = "Times New Roman";
    public bool Bold, Italic;
    public static readonly SchFont Default = new();
}

public abstract class SchPrim
{
    public SKColor? Stroke;
    public int LineWidth;      // 0 smallest .. 3 large (Altium)
    public int LineStyle;      // 0 solid, 1 dash, 2 dot, 3 dash-dot
    public string Role = "sb"; // sb symbol body, gr free graphic, ...
}

public sealed class SchPoly : SchPrim
{
    public List<P2> Pts = new();
    public FillKind Fill;
    public SKColor? FillColor;
    public bool Transparent, Dash;
    public string Extra; // blanket / cmask / tfill
}

public sealed class SchCircle : SchPrim
{
    public double Cx, Cy, R;
    public FillKind Fill;
    public SKColor? FillColor;
}

public sealed class TextFrame
{
    public double X, Y, W, H, Margin;
    public int Align = 1; // 1 left, 2 centre, 3 right
    public bool Wrap = true;
}

public sealed class SchText : SchPrim
{
    public const double FS = 1.4;
    public double X, Y, Size;
    public string Text = "";
    public bool Rot90;           // true = reads bottom-to-top
    public HAnchor HA;
    public VAnchor VA = VAnchor.Bottom;
    public SchFont Font;
    public SKColor? Color;
    public TextFrame Frame;
    public SchText() { Role = "stxt"; }

    /// <summary>Text placement that keeps the string readable like EDA tools do (port of the v1 rule).</summary>
    public static SchText Place(string text, double x, double y, double angDeg, string hj, string vj, double size,
        Func<double, double, P2> tl, string role)
    {
        double a = angDeg * Math.PI / 180, c = Math.Cos(a), s = Math.Sin(a);
        var d = tl(c, s); var p = tl(-s, c);
        bool rot, fh, fv;
        if (Math.Abs(d.X) >= Math.Abs(d.Y)) { rot = false; fh = d.X < 0; fv = p.Y > 0; }
        else { rot = true; fh = d.Y > 0; fv = p.X > 0; }
        var ha = hj == "left" ? (fh ? HAnchor.End : HAnchor.Start) : hj == "right" ? (fh ? HAnchor.Start : HAnchor.End) : HAnchor.Middle;
        var va = vj == "top" ? (fv ? VAnchor.Bottom : VAnchor.Top) : vj == "bottom" ? (fv ? VAnchor.Top : VAnchor.Bottom) : VAnchor.Center;
        return new SchText { Text = text ?? "", X = x, Y = y, Size = size, Rot90 = rot, HA = ha, VA = va, Role = role };
    }
}

public sealed class SchWire
{
    public double X1, Y1, X2, Y2;
    public WireKind Kind;
    public SKColor? Color;
    public string Net;
    internal int Node;
}

public sealed class SchJunction { public double X, Y; public string Net; public SKColor? Color; public bool Auto; internal int Node; }
public sealed class SchNoConnect { public double X, Y; public SKColor? Color; }

public sealed class PortGeo { public double X1, Y1, X2, Y2, H; public bool Vert, StartTip, EndTip; }

public sealed class SheetBlock
{
    public double X, Y, W, H;
    public string Name = "", File = "";
    public SKColor? Stroke, Fill, NameColor, FileColor;
    public SchFont NameFont, FileFont;
    public double NameSize = 1.27 / SchText.FS * 1.4, FileSize = 1.1;
}

public sealed class SchLabel
{
    public double X, Y;
    public int Dir;            // 0 right, 1 up, 2 left, 3 down (direction the flag points away from the anchor)
    public string Text = "";
    public double Size = 1.27;
    public SchFont Font;
    public LabelKind Kind;
    public PortGeo Port;
    public SheetBlock Block;
    public string Net;
    public bool SkipDraw;
    public SKColor? Stroke, Fill, TextColor;
    internal int Node;
}

public sealed class SchPin
{
    public double X, Y, Ex, Ey, Dx, Dy; // X,Y = electrical end; Ex,Ey = body end; Dx,Dy = unit vector from end towards body
    public string Num = "", Name = "", Type = "passive", GNet = "";
    public bool Inv, Hidden, NcFlag;
    public bool? ShowName, ShowNum;
    public double NameSize = 1.27, NumSize = 1.27;
    public SchFont NameFont, NumFont;
    public SKColor? Color;
    public string Net;
    internal int Node;
}

public sealed class SchField
{
    public string Key = "", Val = "";
    public double X, Y, Ang, Size = 1.27;
    public string HJ = "left", VJ = "bottom";
    public SchFont Font;
    public bool Hidden;
    public SKColor? Color;
}

public sealed class SchComponent
{
    public string Ref = "?", Value = "", Footprint = "", Lib = "", Datasheet = "";
    public int Unit = 1, Units = 1, Kind;
    public bool Power, OffSheet, Virtual, Missing, InBom = true, Dnp;
    public double X, Y;
    public List<SchField> Fields = new();
    public List<SchPrim> Prims = new();
    public List<SchPin> Pins = new();
    public List<SchText> FTexts = new();
    public bool ShowNames = true, ShowNums = true;
    public double NameOffset = 0.508;
    public double[] BBox = new double[4];
    public string Description = "";

    /// <summary>Build field texts and bounding box (port of v1 mkComp). tl = local-to-sheet direction transform.</summary>
    public SchComponent Finish(Func<double, double, P2> tl = null)
    {
        tl ??= (x, y) => new P2(x, -y);
        string sfx = Units > 1 ? ((char)(64 + Unit)).ToString() : "";
        foreach (var f in Fields)
        {
            if (f.Hidden || string.IsNullOrEmpty(f.Val)) continue;
            string text = f.Val, role = "fld";
            if (f.Key == "Reference") { text = Ref + sfx; role = "ref"; }
            else if (f.Key == "Value") role = "val";
            var ft = SchText.Place(text, f.X, f.Y, f.Ang, f.HJ, f.VJ, f.Size, tl, role);
            ft.Color = f.Color; ft.Font = f.Font;
            FTexts.Add(ft);
        }
        double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
        void Add(double x, double y) { if (x < x0) x0 = x; if (y < y0) y0 = y; if (x > x1) x1 = x; if (y > y1) y1 = y; }
        foreach (var p in Prims)
        {
            if (p is SchPoly q) foreach (var pt in q.Pts) Add(pt.X, pt.Y);
            else if (p is SchCircle c) { Add(c.Cx - c.R, c.Cy - c.R); Add(c.Cx + c.R, c.Cy + c.R); }
        }
        foreach (var p in Pins) if (!p.Hidden) { Add(p.X, p.Y); Add(p.Ex, p.Ey); }
        if (x0 == double.MaxValue) { x0 = X - 1.27; y0 = Y - 1.27; x1 = X + 1.27; y1 = Y + 1.27; }
        BBox = new[] { x0, y0, x1, y1 };
        return this;
    }
}

public sealed class PageFrame { public double M = 0.2, M2 = 5.08; public int Nx, Ny; }

public sealed class SchPage
{
    public string Name = "A4";
    public double W, H;
    public bool NoTitle, NoBorder;
    public PageFrame Frame = new();
    public SKColor? AreaColor;
}

public sealed class NetMember { public string Ref, Pin, PinName; }
public sealed class NetId { public string K, N, Bf = ""; } // k: local/global/port/sheetpin/hier/power/offsheet/hidden

public sealed class SheetNet
{
    public string Name;
    public bool Auto, Nc;
    public List<NetMember> Members = new();
    public List<NetId> Ids = new();
}

public sealed class Unconnected { public string Ref, Pin, Name; }

public sealed class SchSheet
{
    public string Name;
    public SchPage Page;
    public Dictionary<string, string> Title = new();
    /// <summary>The sheet's number in its title block (Altium SheetNumber, KiCad "Sheet n m"): PDF pages follow it.</summary>
    public int? Number;
    public List<SchWire> Wires = new();
    public List<SchJunction> Junctions = new();
    public List<SchNoConnect> NoConnects = new();
    public List<SchLabel> Labels = new();
    public List<SchText> Texts = new();
    public List<SchPrim> Graphics = new();
    public List<SchComponent> Comps = new();
    public List<SheetBlock> Blocks = new();
    public List<SheetNet> Nets = new();
    public List<Unconnected> UnconnectedPins = new();
    public double[] BBox = { 0, 0, 100, 70 };
}

public sealed class DocNet
{
    public string Name;
    public bool Auto, Nc;
    public List<NetMember> Members = new();
    public List<int> Sheets = new();
}

public sealed class PartPin { public string Num, Name, Net, Type; public bool Hidden, Nc; }

public sealed class SchPart
{
    public string Ref, Value = "", Footprint = "", Lib = "", Datasheet = "", Description = "";
    public bool InBom = true, Dnp, Missing;
    public List<SchField> Fields = new();
    public List<int> Sheets = new();
    public List<PartPin> Pins = new();
    public List<string> Units = new();
}

public sealed class SchDocument
{
    public string FileName, Format, FmtLabel, Version = "";
    public List<SchSheet> Sheets = new();
    public List<string> Warnings = new();
    public List<DocNet> Nets = new();
    public Dictionary<string, DocNet> NetMap = new();
    public List<SchPart> Parts = new();
    public Dictionary<string, SchPart> PartMap = new();
}
