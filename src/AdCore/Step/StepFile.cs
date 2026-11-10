// Herald PCB Reader — Copyright (C) 2026 Johnny.ch.Tsai
// SPDX-License-Identifier: GPL-3.0-only
// Free software under the GNU General Public License version 3 (LICENSE.txt), WITHOUT ANY WARRANTY; with an
// additional permission to combine it with AvalonDock (Ms-PL), see NOTICE.txt.
using System.Globalization;
using System.Text;

namespace AdCore.Step;

// ---------------- values of a STEP (ISO 10303-21) record ----------------
public readonly record struct Ref(int Id);
public sealed class En { public readonly string Name; public En(string n) { Name = n; } }
public sealed class Typed { public string Type = ""; public List<object> Args = Ent.Empty; }
public sealed class Ent
{
    public static readonly List<object> Empty = new();
    public int Id;
    public string Type = "";
    public List<object> Args = Empty;
    /// <summary>A complex entity "( A(...) B(...) )": its parts by name; null for a simple one.</summary>
    public Dictionary<string, List<object>> Parts;
    public List<object> Part(string name) => Parts != null && Parts.TryGetValue(name, out var a) ? a : null;
    public bool Has(string name) => Parts != null && Parts.ContainsKey(name);
}

/// <summary>A STEP file: records indexed in one pass and parsed when first asked for; the length unit (to mm) and the
/// angle unit (to radians); points, directions and placements.</summary>
public sealed class StepFile
{
    readonly string _s;
    readonly Dictionary<int, (int a, int b)> _raw = new();
    readonly Dictionary<int, Ent> _ents = new();
    readonly Dictionary<string, List<int>> _byType = new();
    /// <summary>mm per length unit, radians per angle unit.</summary>
    public readonly double Scale = 1, Ang = 1;

    public StepFile(string text)
    {
        _s = text;
        Index();
        (Scale, Ang) = Units();
    }

    void Index()
    {
        var s = _s; int n = s.Length;
        int i = s.IndexOf("DATA;", StringComparison.Ordinal); i = i < 0 ? 0 : i + 5;
        while (i < n)
        {
            int h = s.IndexOf('#', i); if (h < 0) break;
            int j = h + 1; while (j < n && char.IsAsciiDigit(s[j])) j++;
            if (j == h + 1 || j - h > 10) { i = h + 1; continue; }
            int k = j; while (k < n && s[k] <= ' ') k++;
            if (k >= n || s[k] != '=') { i = j; continue; }
            int id = int.Parse(s.AsSpan(h + 1, j - h - 1), CultureInfo.InvariantCulture);
            int e = k + 1; bool str = false;
            for (; e < n; e++) { char c = s[e]; if (c == '\'') str = !str; else if (c == ';' && !str) break; }
            _raw[id] = (k + 1, e);
            int t = k + 1; while (t < e && s[t] <= ' ') t++;
            string type;
            if (t < e && s[t] == '(') type = "COMPLEX";
            else { int t2 = t; while (t2 < e && (char.IsAsciiLetterOrDigit(s[t2]) || s[t2] == '_')) t2++; type = s.Substring(t, t2 - t).ToUpperInvariant(); }
            if (!_byType.TryGetValue(type, out var l)) _byType[type] = l = new();
            l.Add(id);
            i = e + 1;
        }
    }

    public Ent Get(int id)
    {
        if (_ents.TryGetValue(id, out var e)) return e;
        if (!_raw.TryGetValue(id, out var r)) return null;
        e = Parse(r.a, r.b); e.Id = id; _ents[id] = e;
        return e;
    }
    public Ent Get(object v) => v is Ref r ? Get(r.Id) : null;
    public IReadOnlyList<int> Ids(string type) => _byType.TryGetValue(type, out var l) ? l : Array.Empty<int>();
    bool RawHas(int id, string what) => _raw.TryGetValue(id, out var r) && _s.IndexOf(what, r.a, r.b - r.a, StringComparison.Ordinal) >= 0;

    Ent Parse(int a, int b)
    {
        int p = a;
        void Ws() { while (p < b && _s[p] <= ' ') p++; }
        object Val()
        {
            Ws(); if (p >= b) return null;
            char c = _s[p];
            if (c == '#')
            {
                int j = p + 1; while (j < b && char.IsAsciiDigit(_s[j])) j++;
                int.TryParse(_s.AsSpan(p + 1, j - p - 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out int id); p = j;
                return new Ref(id);
            }
            if (c == '\'')
            {
                var sb = new StringBuilder(); int j = p + 1;
                while (j < b) { if (_s[j] == '\'') { if (j + 1 < b && _s[j + 1] == '\'') { sb.Append('\''); j += 2; continue; } break; } sb.Append(_s[j++]); }
                p = j + 1; return sb.ToString();
            }
            if (c == '.')
            {
                int j = _s.IndexOf('.', p + 1); if (j < 0 || j > b) j = b;
                var en = new En(_s.Substring(p + 1, Math.Max(0, j - p - 1)).ToUpperInvariant()); p = j + 1; return en;
            }
            if (c == '*' || c == '$') { p++; return null; }
            if (c == '(')
            {
                p++; var list = new List<object>(); Ws();
                if (p < b && _s[p] == ')') { p++; return list; }
                while (p < b)
                {
                    list.Add(Val()); Ws();
                    if (p < b && _s[p] == ',') { p++; continue; }
                    if (p < b && _s[p] == ')') p++;
                    break;
                }
                return list;
            }
            if (char.IsAsciiDigit(c) || c == '-' || c == '+')
            {
                int j = p + 1; while (j < b && (char.IsAsciiDigit(_s[j]) || _s[j] is '.' or 'e' or 'E' or '+' or '-')) j++;
                double.TryParse(_s.AsSpan(p, j - p), NumberStyles.Float, CultureInfo.InvariantCulture, out var d); p = j;
                return d;
            }
            {
                int j = p; while (j < b && (char.IsAsciiLetterOrDigit(_s[j]) || _s[j] == '_')) j++;
                var name = _s.Substring(p, j - p).ToUpperInvariant(); p = j; Ws();
                if (name.Length > 0 && p < b && _s[p] == '(') return new Typed { Type = name, Args = Val() as List<object> ?? Ent.Empty };
                if (name.Length == 0) p++;   // an unexpected character: step over it
                return new Typed { Type = name };
            }
        }
        Ws();
        var ent = new Ent();
        if (p < b && _s[p] == '(')
        {
            p++; ent.Type = "COMPLEX"; ent.Parts = new();
            while (p < b)
            {
                Ws(); if (p >= b || _s[p] == ')') break;
                if (Val() is Typed t && t.Type.Length > 0) ent.Parts[t.Type] = t.Args; else break;
            }
            return ent;
        }
        if (Val() is Typed top) { ent.Type = top.Type; ent.Args = top.Args; } else ent.Type = "?";
        return ent;
    }

    // ---------------- small helpers for record arguments ----------------
    public static object Arg(Ent e, int i) => e != null && i < e.Args.Count ? e.Args[i] : null;
    public static object Arg(List<object> a, int i) => a != null && i < a.Count ? a[i] : null;
    public static List<object> List(object o) => o as List<object> ?? Ent.Empty;
    public static double Num(object o) => o is double d ? d : o is Typed t && t.Args.Count > 0 && t.Args[0] is double d2 ? d2 : 0;
    public static bool IsNum(object o) => o is double || o is Typed { Args.Count: > 0 } t && t.Args[0] is double;
    public static bool IsFalse(object o) => o is En e && e.Name == "F";
    public static bool IsTrue(object o) => o is En e && e.Name == "T";
    public static int RefId(object o) => o is Ref r ? r.Id : 0;

    public V3 Pt(object r)
    {
        var c = List(Arg(Get(r), 1));
        return new V3(Num(Arg(c, 0)) * Scale, Num(Arg(c, 1)) * Scale, Num(Arg(c, 2)) * Scale);
    }
    public V3 Dir(object r)
    {
        var c = List(Arg(Get(r), 1));
        return new V3(Num(Arg(c, 0)), Num(Arg(c, 1)), Num(Arg(c, 2))).Norm();
    }

    /// <summary>AXIS2_PLACEMENT_3D (or _2D) → origin and axes.</summary>
    public Frame Placement(object r)
    {
        var e = Get(r);
        if (e == null) return Frame.Make(default, new V3(0, 0, 1), null);
        if (e.Type == "AXIS2_PLACEMENT_3D")
        {
            var o = Pt(Arg(e, 1));
            var z = Arg(e, 2) is Ref ? Dir(Arg(e, 2)) : new V3(0, 0, 1);
            V3? x = Arg(e, 3) is Ref ? Dir(Arg(e, 3)) : null;
            return Frame.Make(o, z, x);
        }
        if (e.Type == "AXIS2_PLACEMENT_2D") return Frame.Make(Pt(Arg(e, 1)), new V3(0, 0, 1), Arg(e, 2) is Ref ? Dir(Arg(e, 2)) : null);
        return Frame.Make(default, new V3(0, 0, 1), null);
    }

    // ---------------- units ----------------
    (double len, double ang) Units()
    {
        double len = double.NaN, ang = double.NaN;
        void Take(int id)
        {
            var e = Get(id); if (e?.Parts == null) return;
            if (e.Has("LENGTH_UNIT") && double.IsNaN(len)) len = UnitFactor(e, 1000, 0) ?? double.NaN;
            if (e.Has("PLANE_ANGLE_UNIT") && double.IsNaN(ang)) ang = UnitFactor(e, 1, 0) ?? double.NaN;
        }
        // the units of the first representation context, else any found
        foreach (var id in Ids("COMPLEX"))
        {
            if (!RawHas(id, "GLOBAL_UNIT_ASSIGNED_CONTEXT")) continue;
            foreach (var u in List(Arg(Get(id).Part("GLOBAL_UNIT_ASSIGNED_CONTEXT"), 0))) if (u is Ref r) Take(r.Id);
            if (!double.IsNaN(len)) break;
        }
        if (double.IsNaN(len) || double.IsNaN(ang))
            foreach (var id in Ids("COMPLEX")) if (RawHas(id, "_UNIT")) Take(id);
        return (double.IsNaN(len) || len <= 0 ? 1 : len, double.IsNaN(ang) || ang <= 0 ? 1 : ang);
    }

    /// <summary>A unit → mm (length; baseFactor 1000 per metre) or radians (angle; 1 per radian).</summary>
    double? UnitFactor(Ent e, double baseFactor, int depth)
    {
        if (e?.Parts == null || depth > 5) return null;
        if (e.Part("SI_UNIT") is { } si)
        {
            double f = (Arg(si, 0) as En)?.Name switch { "MILLI" => 1e-3, "CENTI" => 1e-2, "DECI" => 0.1, "KILO" => 1e3, "MICRO" => 1e-6, "NANO" => 1e-9, _ => 1 };
            return baseFactor * f;
        }
        if (e.Part("CONVERSION_BASED_UNIT") is { } cb)
        {
            var meas = Get(Arg(cb, 1));
            var ma = meas == null ? null : meas.Parts != null ? meas.Part("MEASURE_WITH_UNIT") ?? meas.Parts.Values.FirstOrDefault(x => x.Count >= 2) : meas.Args;
            if (ma != null && IsNum(Arg(ma, 0)) && Get(Arg(ma, 1)) is { } unit && UnitFactor(unit, baseFactor, depth + 1) is double uf) return Num(Arg(ma, 0)) * uf;
            return ((Arg(cb, 0) as string) ?? "").ToUpperInvariant() switch
            {
                "INCH" => 25.4, "FOOT" => 304.8, "MILLIMETRE" or "MILLIMETER" => 1, "METRE" or "METER" => 1000, "DEGREE" or "DEGREES" => Math.PI / 180, _ => null,
            };
        }
        return null;
    }
}

// ---------------- vectors, frames, transforms ----------------
public readonly struct V3
{
    public readonly double X, Y, Z;
    public V3(double x, double y, double z) { X = x; Y = y; Z = z; }
    public static V3 operator +(V3 a, V3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static V3 operator -(V3 a, V3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static V3 operator -(V3 a) => new(-a.X, -a.Y, -a.Z);
    public static V3 operator *(V3 a, double s) => new(a.X * s, a.Y * s, a.Z * s);
    public static double Dot(V3 a, V3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
    public static V3 Cross(V3 a, V3 b) => new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
    public double Len => Math.Sqrt(X * X + Y * Y + Z * Z);
    public V3 Norm() { double l = Len; return l > 0 ? new V3(X / l, Y / l, Z / l) : this; }
}

public readonly struct Frame
{
    public readonly V3 O, X, Y, Z;
    Frame(V3 o, V3 x, V3 y, V3 z) { O = o; X = x; Y = y; Z = z; }
    /// <summary>Origin, axis (z) and a reference direction for x (projected square to z; any when missing or parallel).</summary>
    public static Frame Make(V3 o, V3 z, V3? xr)
    {
        z = z.Norm(); if (z.Len < 1e-12) z = new V3(0, 0, 1);
        V3 x = default; bool ok = false;
        if (xr is V3 r) { x = r - z * V3.Dot(r, z); ok = x.Len > 1e-9; }
        if (!ok) { x = Math.Abs(z.X) < 0.9 ? new V3(1, 0, 0) : new V3(0, 1, 0); x -= z * V3.Dot(x, z); }
        x = x.Norm();
        return new Frame(o, x, V3.Cross(z, x), z);
    }
}

/// <summary>Affine transform, 3 rows × 4 columns, for column vectors: p' = R·p + t.</summary>
public sealed class Xf
{
    public readonly double[] A = new double[12];
    public static Xf I() { var m = new Xf(); m.A[0] = m.A[5] = m.A[10] = 1; return m; }
    public static Xf Of(params double[] a) { var m = new Xf(); Array.Copy(a, m.A, 12); return m; }
    public static Xf FromFrame(Frame f) => Of(f.X.X, f.Y.X, f.Z.X, f.O.X, f.X.Y, f.Y.Y, f.Z.Y, f.O.Y, f.X.Z, f.Y.Z, f.Z.Z, f.O.Z);
    public static Xf Mul(Xf a, Xf b)
    {
        var r = new Xf(); var x = a.A; var y = b.A;
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 4; j++)
                r.A[i * 4 + j] = x[i * 4] * y[j] + x[i * 4 + 1] * y[4 + j] + x[i * 4 + 2] * y[8 + j] + (j == 3 ? x[i * 4 + 3] : 0);
        return r;
    }
    /// <summary>Inverse of a rigid transform (orthonormal rotation).</summary>
    public Xf InvRigid()
    {
        var a = A;
        var r = Of(a[0], a[4], a[8], 0, a[1], a[5], a[9], 0, a[2], a[6], a[10], 0);
        for (int i = 0; i < 3; i++) r.A[i * 4 + 3] = -(r.A[i * 4] * a[3] + r.A[i * 4 + 1] * a[7] + r.A[i * 4 + 2] * a[11]);
        return r;
    }
    public V3 Ap(V3 p) { var a = A; return new V3(a[0] * p.X + a[1] * p.Y + a[2] * p.Z + a[3], a[4] * p.X + a[5] * p.Y + a[6] * p.Z + a[7], a[8] * p.X + a[9] * p.Y + a[10] * p.Z + a[11]); }
    public V3 ApV(V3 p) { var a = A; return new V3(a[0] * p.X + a[1] * p.Y + a[2] * p.Z, a[4] * p.X + a[5] * p.Y + a[6] * p.Z, a[8] * p.X + a[9] * p.Y + a[10] * p.Z); }
    public static Xf RotX(double deg) { double t = deg * Math.PI / 180, c = Math.Cos(t), s = Math.Sin(t); return Of(1, 0, 0, 0, 0, c, -s, 0, 0, s, c, 0); }
    public static Xf RotY(double deg) { double t = deg * Math.PI / 180, c = Math.Cos(t), s = Math.Sin(t); return Of(c, 0, s, 0, 0, 1, 0, 0, -s, 0, c, 0); }
    public static Xf RotZ(double deg) { double t = deg * Math.PI / 180, c = Math.Cos(t), s = Math.Sin(t); return Of(c, -s, 0, 0, s, c, 0, 0, 0, 0, 1, 0); }
    public static Xf Tr(double x, double y, double z) => Of(1, 0, 0, x, 0, 1, 0, y, 0, 0, 1, z);
    public string Key() => string.Join(",", A.Select(v => v.ToString("F4", CultureInfo.InvariantCulture)));
}
