// Herald PCB Reader — Copyright (C) 2026 Johnny.ch.Tsai
// SPDX-License-Identifier: GPL-3.0-only
// Free software under the GNU General Public License version 3 (LICENSE.txt), WITHOUT ANY WARRANTY; with an
// additional permission to combine it with AvalonDock (Ms-PL), see NOTICE.txt.
using System.Globalization;

namespace AdCore.Step;

/// <summary>One node of a Parasolid transmit file: its type number, index and fields by name.</summary>
public sealed class XtNode
{
    public int Type, Index;
    public string Name = "";
    public readonly Dictionary<string, object> F = new();
    public int P(string k) => F.TryGetValue(k, out var v) && v is int i ? i : 0;
    public double D(string k) => F.TryGetValue(k, out var v) && v is double d ? d : double.NaN;
    public char C(string k) => F.TryGetValue(k, out var v) && v is char c ? c : ' ';
    public double[] Ds(string k) => F.TryGetValue(k, out var v) ? v as double[] : null;
    public int[] Ps(string k) => F.TryGetValue(k, out var v) ? v as int[] : null;
    public string S(string k) => F.TryGetValue(k, out var v) ? v as string : null;
    public V3 Vec(string k) { var a = Ds(k); return a != null && a.Length >= 3 ? new V3(a[0], a[1], a[2]) : default; }
}

/// <summary>Parasolid XT transmit files as Altium embeds them, text (.x_t) or neutral binary (.x_b): the nodes, read
/// with the field layouts of the XT Format Reference. Text files written with schema 28 and later have a few extra or
/// reordered fields; the reader tries that layout first and then the older one. Files that carry their schema's
/// differences from the base schema (SCH_…_13006, e.g. most binary files) are read by applying those differences.</summary>
public sealed class XtFile
{
    public string Schema = "";
    public readonly Dictionary<int, XtNode> Nodes = new();
    public XtNode this[int i] => i != 0 && Nodes.TryGetValue(i, out var n) ? n : null;

    // field specs: name:type; d n u w p integers, f double (? = unset), c l char, v h 3 doubles, i 2, b 6;
    // name:t9 a fixed array, name:t* the variable-length last field (its length comes before the index)
    static readonly Dictionary<int, string> Common = new()
    {
        [10] = "ASSEMBLY|highest_node_id:d attributes_groups:p attribute_chains:p list:p surface:p curve:p point:p key:p res_size:f res_linear:f ref_instance:p next:p previous:p state:u owner:p type:u sub_instance:p",
        [11] = "INSTANCE|node_id:d attributes_groups:p type:u part:p transform:p assembly:p next_in_part:p prev_in_part:p next_of_part:p prev_of_part:p",
        [13] = "SHELL|node_id:d attributes_groups:p body:p next:p face:p edge:p vertex:p region:p front_face:p",
        [14] = "FACE|node_id:d attributes_groups:p tolerance:f next:p previous:p loop:p shell:p surface:p sense:c next_on_surface:p previous_on_surface:p next_front:p previous_front:p front_shell:p",
        [15] = "LOOP|node_id:d attributes_groups:p fin:p face:p next:p",
        [17] = "FIN|attributes_groups:p loop:p forward:p backward:p vertex:p other:p edge:p curve:p next_at_vx:p sense:c",
        [18] = "VERTEX|node_id:d attributes_groups:p fin:p previous:p next:p point:p tolerance:f owner:p",
        [29] = "POINT|node_id:d attributes_groups:p owner:p next:p previous:p pvec:v",
        [30] = "LINE|node_id:d attributes_groups:p owner:p next:p previous:p geometric_owner:p sense:c pvec:v direction:v",
        [31] = "CIRCLE|node_id:d attributes_groups:p owner:p next:p previous:p geometric_owner:p sense:c centre:v normal:v x_axis:v radius:f",
        [32] = "ELLIPSE|node_id:d attributes_groups:p owner:p next:p previous:p geometric_owner:p sense:c centre:v normal:v x_axis:v major_radius:f minor_radius:f",
        [38] = "INTERSECTION|node_id:d attributes_groups:p owner:p next:p previous:p geometric_owner:p sense:c surface:p2 chart:p start:p end:p",
        [40] = "CHART|base_parameter:f base_scale:f chart_count:d chordal_error:f angular_error:f parameter_error:f2 hvec:h*",
        [41] = "LIMIT|type:c hvec:h*",
        [45] = "BSPLINE_VERTICES|vertices:f*",
        [50] = "PLANE|node_id:d attributes_groups:p owner:p next:p previous:p geometric_owner:p sense:c pvec:v normal:v x_axis:v",
        [51] = "CYLINDER|node_id:d attributes_groups:p owner:p next:p previous:p geometric_owner:p sense:c pvec:v axis:v radius:f x_axis:v",
        [52] = "CONE|node_id:d attributes_groups:p owner:p next:p previous:p geometric_owner:p sense:c pvec:v axis:v radius:f sin_half_angle:f cos_half_angle:f x_axis:v",
        [53] = "SPHERE|node_id:d attributes_groups:p owner:p next:p previous:p geometric_owner:p sense:c centre:v radius:f axis:v x_axis:v",
        [54] = "TORUS|node_id:d attributes_groups:p owner:p next:p previous:p geometric_owner:p sense:c centre:v axis:v major_radius:f minor_radius:f x_axis:v",
        [56] = "BLENDED_EDGE|node_id:d attributes_groups:p owner:p next:p previous:p geometric_owner:p sense:c blend_type:c surface:p2 spine:p range:f2 thumb_weight:f2 boundary:p2 start:p end:p",
        [59] = "BLEND_BOUND|node_id:d attributes_groups:p owner:p next:p previous:p geometric_owner:p sense:c boundary:n blend:p",
        [60] = "OFFSET_SURF|node_id:d attributes_groups:p owner:p next:p previous:p geometric_owner:p sense:c check:c true_offset:l surface:p offset:f scale:f",
        [67] = "SWEPT_SURF|node_id:d attributes_groups:p owner:p next:p previous:p geometric_owner:p sense:c section:p sweep:v scale:f",
        [68] = "SPUN_SURF|node_id:d attributes_groups:p owner:p next:p previous:p geometric_owner:p sense:c profile:p base:v axis:v start:v end:v start_param:f end_param:f x_axis:v scale:f",
        [79] = "ATT_DEF_ID|string:c*",
        [81] = "ATTRIBUTE|node_id:d definition:p owner:p next:p previous:p next_of_type:p previous_of_type:p fields:p*",
        [82] = "INT_VALUES|values:d*", [83] = "REAL_VALUES|values:f*", [84] = "CHAR_VALUES|values:c*",
        [85] = "POINT_VALUES|values:v*", [86] = "VECTOR_VALUES|values:v*", [87] = "AXIS_VALUES|values:v*", [89] = "DIRECTION_VALUES|values:v*",
        [90] = "GROUP|node_id:d attributes_groups:p owner:p next:p previous:p type:u first_member:p",
        [91] = "MEMBER_OF_GROUP|dummy_node_id:d owning_group:p owner:p next:p previous:p next_member:p previous_member:p",
        [98] = "UNICODE_VALUES|values:w*",
        [124] = "B_SURFACE|node_id:d attributes_groups:p owner:p next:p previous:p geometric_owner:p sense:c nurbs:p data:p",
        [125] = "SURFACE_DATA|original_uint:i original_vint:i extended_uint:i extended_vint:i self_int:u original_u_start:c original_u_end:c original_v_start:c original_v_end:c extended_u_start:c extended_u_end:c extended_v_start:c extended_v_end:c analytic_form_type:c swept_form_type:c spun_form_type:c blend_form_type:c analytic_form:p swept_form:p spun_form:p blend_form:p",
        [126] = "NURBS_SURF|u_periodic:l v_periodic:l u_degree:n v_degree:n n_u_vertices:d n_v_vertices:d u_knot_type:u v_knot_type:u n_u_knots:d n_v_knots:d rational:l u_closed:l v_closed:l surface_form:u vertex_dim:n bspline_vertices:p u_knot_mult:p v_knot_mult:p u_knots:p v_knots:p",
        [127] = "KNOT_MULT|mult:n*", [128] = "KNOT_SET|knots:f*",
        [133] = "TRIMMED_CURVE|node_id:d attributes_groups:p owner:p next:p previous:p geometric_owner:p sense:c basis_curve:p point_1:v point_2:v parm_1:f parm_2:f",
        [134] = "B_CURVE|node_id:d attributes_groups:p owner:p next:p previous:p geometric_owner:p sense:c nurbs:p data:p",
        [135] = "CURVE_DATA|self_int:u analytic_form:p",
        [136] = "NURBS_CURVE|degree:n n_vertices:d vertex_dim:n n_knots:d knot_type:u periodic:l closed:l rational:l curve_form:u bspline_vertices:p knot_mult:p knots:p",
        [137] = "SP_CURVE|node_id:d attributes_groups:p owner:p next:p previous:p geometric_owner:p sense:c surface:p b_curve:p original:p tolerance_to_original:f",
        [141] = "GEOMETRIC_OWNER|owner:p next:p previous:p shared_geometry:p",
    };
    // schema 28 and later (checked against a SolidWorks 2022 / Parasolid 33 file)
    static readonly Dictionary<int, string> New = new()
    {
        [12] = "BODY|highest_node_id:d attributes_groups:p attribute_chains:p surface:p curve:p point:p key:p res_size:f res_linear:f ref_instance:p next:p previous:p state:u owner:p body_type:u nom_geom_state:u shell:p boundary_surface:p boundary_curve:p boundary_point:p region:p edge:p vertex:p index_map_offset:d index_map:p node_id_index_map:p schema_embedding_map:p x1:p x2:p x3:p",
        [16] = "EDGE|node_id:d attributes_groups:p tolerance:f fin:p previous:p next:p curve:p next_on_curve:p owner:p x1:p",
        [19] = "REGION|node_id:d attributes_groups:p body:p next:p previous:p shell:p type:c x1:p",
        [70] = "LIST|node_id:d list_type:d notransmit:l owner:p next:p previous:p list_length:d block_length:d size_of_entry:d block:p finger_block:p",
        [74] = "POINTER_LIS_BLOCK|n_entries:d next_block:p x1:p entries:p*",
        [80] = "ATTRIB_DEF|next:p identifier:p type_id:d actions:u9 legal_owners:l14 fields:u*",
        [100] = "TRANSFORM|node_id:d owner:p next:p previous:p rotation_matrix:f9 translation_vector:v scale:f flag:d x1:f",
    };
    // the XT Format Reference (2006 / 2008)
    static readonly Dictionary<int, string> Old = new()
    {
        [12] = "BODY|highest_node_id:d attributes_groups:p attribute_chains:p surface:p curve:p point:p key:p res_size:f res_linear:f ref_instance:p next:p previous:p state:u owner:p body_type:u nom_geom_state:u shell:p boundary_surface:p boundary_curve:p boundary_point:p region:p edge:p vertex:p index_map_offset:d index_map:p node_id_index_map:p schema_embedding_map:p",
        [16] = "EDGE|node_id:d attributes_groups:p tolerance:f fin:p previous:p next:p curve:p next_on_curve:p owner:p",
        [19] = "REGION|node_id:d attributes_groups:p body:p next:p previous:p shell:p type:c",
        [70] = "LIST|node_id:d owner:p next:p previous:p list_type:d list_length:d block_length:d size_of_entry:d block:p finger_block:p finger_index:d notransmit:l",
        [74] = "POINTER_LIS_BLOCK|n_entries:d next_block:p entries:p*",
        [80] = "ATTRIB_DEF|next:p identifier:p type_id:d actions:u8 legal_owners:l14 fields:u*",
        [100] = "TRANSFORM|node_id:d owner:p next:p previous:p rotation_matrix:f9 translation_vector:v scale:f flag:d perspective_vector:v",
    };

    sealed record Field(string Name, char T, int K);   // K: 0 scalar, n fixed array, -1 variable
    sealed record Spec(string Name, Field[] Fields, bool Variable);
    static Dictionary<int, Spec> Build(Dictionary<int, string> extra)
    {
        var o = new Dictionary<int, Spec>();
        foreach (var src in new[] { Common, extra })
            foreach (var (k, v) in src)
            {
                var bar = v.IndexOf('|');
                var fields = v[(bar + 1)..].Split(' ').Select(s =>
                {
                    var c = s.IndexOf(':'); var t = s[(c + 1)..];
                    return new Field(s[..c], t[0], t.Length == 1 ? 0 : t[1..] == "*" ? -1 : int.Parse(t[1..], CultureInfo.InvariantCulture));
                }).ToArray();
                o[k] = new Spec(v[..bar], fields, fields.Any(f => f.K == -1));
            }
        return o;
    }
    static readonly Dictionary<int, Spec> NewSpecs = Build(New), OldSpecs = Build(Old);
    /// <summary>The base schema (SCH_13006) that files carrying their own schema differences are written against:
    /// the reference's layouts, with what came later taken out and what the reference leaves out put in (checked
    /// against a Parasolid 24 binary file).</summary>
    static readonly Dictionary<int, Spec> BaseSpecs = Build(new Dictionary<int, string>(Old)
    {
        [12] = "BODY|highest_node_id:d attributes_groups:p attribute_chains:p surface:p curve:p point:p key:p res_size:f res_linear:f ref_instance:p next:p previous:p state:u owner:p body_type:u nom_geom_state:u shell:p boundary_surface:p boundary_curve:p boundary_point:p region:p edge:p vertex:p",
        [16] = "EDGE|node_id:d attributes_groups:p tolerance:f fin:p previous:p next:p curve:p next_on_curve:p owner:p x1:p",
        [70] = "LIST|node_id:d owner:p next:p previous:p list_type_old:d list_length:d block_length:d size_of_entry:d block:p",
        [80] = "ATTRIB_DEF|next:p identifier:p type_id:d actions:u8 legal_owners:l16 fields:u*",
    });

    /// <summary>The nodes of a transmit file (text .x_t or neutral binary .x_b, as Altium embeds them), or null when
    /// it cannot be read.</summary>
    public static XtFile Parse(byte[] data)
    {
        int h = IndexOf(data, "**END_OF_HEADER"u8);
        if (h < 0) return null;
        int nl = Array.IndexOf(data, (byte)'\n', h); if (nl < 0) return null;
        int start = nl + 1;
        if (data.Length - start > 4 && data[start] == 'P' && data[start + 1] == 'S' && data[start + 2] == 0 && data[start + 3] == 0)
        {
            try { var x = new XtFile(); if (x.Read(new BinSrc(data, start + 4), null)) return x; }
            catch (Exception) { }
            return null;
        }
        var sb = new System.Text.StringBuilder(data.Length - start);
        for (int i = start; i < data.Length; i++) { byte c = data[i]; if (c != '\r' && c != '\n') sb.Append((char)c); }
        var body = sb.ToString();
        if (body.Length < 2 || body[0] != 'T') return null;   // bare or typed binary: not read
        foreach (var specs in new[] { NewSpecs, OldSpecs })
        {
            try { var x = new XtFile(); if (x.Read(new TextSrc(body, 1), specs)) return x; }
            catch (Exception) { }
        }
        return null;
    }
    public static XtFile Parse(string text) => Parse(TextUtilLatin1(text));
    static byte[] TextUtilLatin1(string s) => System.Text.Encoding.Latin1.GetBytes(s);
    static int IndexOf(byte[] a, ReadOnlySpan<byte> what) => a.AsSpan().IndexOf(what);

    // ---------------- the two encodings ----------------
    abstract class Src
    {
        public abstract int Type();               // node type
        public abstract int Len();                // length of a variable node
        public abstract int Index();              // node index / pointer
        public abstract int D(); public abstract int N(); public abstract int U();
        public abstract double F(); public abstract char C(); public abstract char L();
        public abstract string ShortString();
        public abstract string Chars(int n);      // a variable char field
        public abstract string Header();          // modeller / schema strings at the start
        public abstract int UserFieldSize();
        public abstract int MaxTypes();
        public abstract int PosInt();
    }
    sealed class TextSrc : Src
    {
        readonly string _s; int _p;
        public TextSrc(string s, int p) { _s = s; _p = p; }
        void Ws() { while (_p < _s.Length && _s[_p] == ' ') _p++; }
        int Int()
        {
            Ws(); int j = _p; if (j < _s.Length && (_s[j] == '-' || _s[j] == '+')) j++;
            while (j < _s.Length && char.IsAsciiDigit(_s[j])) j++;
            if (j == _p || (j == _p + 1 && !char.IsAsciiDigit(_s[_p]))) throw new FormatException("integer expected at " + _p);
            int v = int.Parse(_s.AsSpan(_p, j - _p), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture); _p = j; return v;
        }
        public override int Type() => Int(); public override int Len() => Int(); public override int Index() => Int();
        public override int D() => Int(); public override int N() => Int(); public override int U() => Int(); public override int PosInt() => Int();
        public override double F()
        {
            Ws(); if (_p < _s.Length && _s[_p] == '?') { _p++; return double.NaN; }
            int j = _p; if (j < _s.Length && (_s[j] == '-' || _s[j] == '+')) j++;
            int d0 = j; while (j < _s.Length && char.IsAsciiDigit(_s[j])) j++;
            if (j < _s.Length && _s[j] == '.') { j++; while (j < _s.Length && char.IsAsciiDigit(_s[j])) j++; }
            if (j == d0) throw new FormatException("number expected at " + _p);
            if (j < _s.Length && (_s[j] == 'e' || _s[j] == 'E')) { int k = j + 1; if (k < _s.Length && (_s[k] == '-' || _s[k] == '+')) k++; if (k < _s.Length && char.IsAsciiDigit(_s[k])) { j = k; while (j < _s.Length && char.IsAsciiDigit(_s[j])) j++; } }
            double v = double.Parse(_s.AsSpan(_p, j - _p), NumberStyles.Float, CultureInfo.InvariantCulture); _p = j; return v;
        }
        public override char C() { Ws(); if (_p >= _s.Length) throw new FormatException("end of data"); return _s[_p++]; }
        public override char L() => C();
        public override string Chars(int n) { _p++; if (n < 0 || _p + n > _s.Length) throw new FormatException("string"); var r = _s.Substring(_p, n); _p += n; return r; }
        public override string ShortString() { int n = Int(); return Chars(n); }
        public override string Header() => ShortString();
        public override int UserFieldSize() => Int();
        public override int MaxTypes() => Int();
    }
    /// <summary>Neutral binary: big-endian, IEEE doubles; pointers as index + 1 in a short (two shorts when large).</summary>
    sealed class BinSrc : Src
    {
        readonly byte[] _b; int _p;
        public BinSrc(byte[] b, int p) { _b = b; _p = p; }
        void Need(int n) { if (_p + n > _b.Length) throw new FormatException("end of data"); }
        short I16() { Need(2); var v = System.Buffers.Binary.BinaryPrimitives.ReadInt16BigEndian(_b.AsSpan(_p)); _p += 2; return v; }
        int I32() { Need(4); var v = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(_b.AsSpan(_p)); _p += 4; return v; }
        int Ptr() { int r = I16(), q = 0; if (r < 0) { q = I16(); r = -r; } return q * 32767 + r - 1; }
        public override int Type() => I16(); public override int Len() => I32(); public override int Index() => Ptr();
        public override int D() => I32(); public override int N() => I16(); public override int U() { Need(1); return _b[_p++]; }
        public override int PosInt() => Ptr();
        public override double F() { Need(8); var v = System.Buffers.Binary.BinaryPrimitives.ReadDoubleBigEndian(_b.AsSpan(_p)); _p += 8; return v == -3.14158e13 ? double.NaN : v; }
        public override char C() { Need(1); return (char)_b[_p++]; }
        public override char L() { Need(1); return _b[_p++] != 0 ? 'T' : 'F'; }
        public override string Chars(int n) { if (n < 0) throw new FormatException("string"); Need(n); var r = System.Text.Encoding.Latin1.GetString(_b, _p, n); _p += n; return r; }
        public override string ShortString() => Chars(U());
        public override string Header() { int n = I16(); return Chars(n); }
        public override int UserFieldSize() => I32();
        public override int MaxTypes() => I16();
        public string SchemaString() { int n = I32(); return Chars(n); }
    }

    bool Read(Src src, Dictionary<int, Spec> specs)
    {
        object Val(char t) => t switch
        {
            'd' => src.D(), 'n' or 'w' => src.N(), 'u' => src.U(), 'p' => src.Index(),
            'f' => src.F(), 'c' => src.C(), 'l' => src.L(),
            'v' or 'h' => new[] { src.F(), src.F(), src.F() },
            'i' => new[] { src.F(), src.F() },
            'b' => new[] { src.F(), src.F(), src.F(), src.F(), src.F(), src.F() },
            _ => throw new FormatException("field type " + t),
        };
        object Arr(char t, int n)
        {
            if (n < 0 || n > 50_000_000) throw new FormatException("bad length");
            switch (t)
            {
                case 'd' or 'n' or 'u' or 'w' or 'p': { var a = new int[n]; for (int i = 0; i < n; i++) a[i] = (int)Val(t); return a; }
                case 'f': { var a = new double[n]; for (int i = 0; i < n; i++) a[i] = src.F(); return a; }
                case 'v' or 'h': { var a = new double[n * 3]; for (int i = 0; i < n * 3; i++) a[i] = src.F(); return a; }
                case 'c' or 'l': { var a = new char[n]; for (int i = 0; i < n; i++) a[i] = t == 'c' ? src.C() : src.L(); return new string(a); }
                default: throw new FormatException("array type " + t);
            }
        }
        src.Header();                                               // ": TRANSMIT FILE created by modeller version …"
        Schema = src is BinSrc bs ? bs.SchemaString() : src.Header();   // SCH_<version>_<schema>[_<base schema>]
        bool embedded = Schema.Count(c => c == '_') >= 3;
        if (embedded) { specs = BaseSpecs; src.MaxTypes(); }
        else if (specs == null) specs = OldSpecs;
        int usfld = src.UserFieldSize();
        var layout = new Dictionary<int, Spec>();
        Field FieldDef()
        {
            var name = src.ShortString(); int ptrClass = src.N(); int n = src.PosInt();
            char t = ptrClass != 0 ? 'p' : (src.ShortString() is { Length: > 0 } ts ? ts[0] : 'd');
            if (n == 1) src.L();   // transmit flag, only for variable-length fields
            return new Field(name, t, n == 0 ? 0 : n == 1 ? -1 : n);
        }
        for (int guard = 0; guard < 10_000_000; guard++)
        {
            int type = src.Type();
            if (type == 1) { src.Index(); return Nodes.Count > 0; }   // terminator
            if (!layout.TryGetValue(type, out var spec))
            {
                if (!embedded) { if (!specs.TryGetValue(type, out spec)) return false; }
                else if (!specs.TryGetValue(type, out var bspec))
                {
                    // a node type the base schema does not have: its whole layout follows
                    int nf = src.U(); var nm = src.ShortString(); src.ShortString();
                    var fs = new Field[nf]; for (int i = 0; i < nf; i++) fs[i] = FieldDef();
                    spec = new Spec(nm, fs, fs.Any(f => f.K == -1));
                }
                else
                {
                    int b = src.U();
                    if (b == 255) spec = bspec;
                    else
                    {
                        // the base layout edited: Copy, Delete, Insert, Append, then Z
                        var fs = new List<Field>(); int bi = 0;
                        for (int g = 0; g < 1000; g++)
                        {
                            char ins = src.C();
                            if (ins == 'C') { if (bi >= bspec.Fields.Length) return false; fs.Add(bspec.Fields[bi++]); }
                            else if (ins == 'D') bi++;
                            else if (ins == 'I' || ins == 'A') fs.Add(FieldDef());
                            else if (ins == 'Z') break;
                            else return false;
                        }
                        if (fs.Count != b) return false;
                        spec = new Spec(bspec.Name, fs.ToArray(), fs.Any(f => f.K == -1));
                    }
                }
                layout[type] = spec;
            }
            int len = spec.Variable ? src.Len() : 0;
            int index = src.Index();
            var node = new XtNode { Type = type, Index = index, Name = spec.Name };
            foreach (var f in spec.Fields)
            {
                if (f.K == 0) node.F[f.Name] = Val(f.T);
                else if (f.K > 0) node.F[f.Name] = Arr(f.T, f.K);
                else if (f.T == 'c') node.F[f.Name] = src.Chars(len);
                else node.F[f.Name] = Arr(f.T, len);
            }
            for (int k = 0; k < usfld; k++) src.D();
            Nodes[index] = node;
        }
        return false;
    }
}
