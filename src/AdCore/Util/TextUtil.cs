// Herald PCB Reader — Copyright (C) 2026 Johnny.ch.Tsai
// SPDX-License-Identifier: GPL-3.0-only
// Free software under the GNU General Public License version 3 (LICENSE.txt), WITHOUT ANY WARRANTY; with an
// additional permission to combine it with AvalonDock (Ms-PL), see NOTICE.txt.
using System.Text;
using System.Text.RegularExpressions;

namespace AdCore.Util;

public static class TextUtil
{
    static TextUtil() { Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); }

    public static readonly Encoding Latin1 = Encoding.Latin1;
    static readonly Encoding Utf8Strict = new UTF8Encoding(false, true);
    static Encoding _big5, _gbk, _cp1252;
    static Encoding Big5 => _big5 ??= Strict(950);
    static Encoding Gbk => _gbk ??= Strict(936);
    static Encoding Cp1252 => _cp1252 ??= Encoding.GetEncoding(1252);
    static Encoding Strict(int cp) => Encoding.GetEncoding(cp, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);

    /// <summary>Decode bytes stored in the local ANSI code page of whoever saved the file (Altium): try UTF-8, Big5, GBK.</summary>
    public static string DecodeLocal(ReadOnlySpan<byte> b)
    {
        bool hi = false;
        foreach (var x in b) if (x >= 0x80) { hi = true; break; }
        if (!hi) return Latin1.GetString(b);
        foreach (var enc in new[] { Utf8Strict, Big5, Gbk })
        {
            try { return enc.GetString(b); } catch (DecoderFallbackException) { }
        }
        return Cp1252.GetString(b);
    }

    /// <summary>Parse an Altium "|KEY=VALUE|KEY=VALUE" record. Keys upper-cased; %UTF8% variants win.</summary>
    public static Dictionary<string, string> AltProps(ReadOnlySpan<byte> b, bool decodeLocal = true)
    {
        int end = b.Length;
        while (end > 0 && b[end - 1] == 0) end--;
        var props = new Dictionary<string, string>(StringComparer.Ordinal);
        var utf = new Dictionary<string, string>(StringComparer.Ordinal);
        int i = 0;
        while (i < end)
        {
            int s = i; if (b[s] == 0x7C) s++;
            int e = s; while (e < end && b[e] != 0x7C) e++;
            int q = s; while (q < e && b[q] != 0x3D) q++;
            if (q < e)
            {
                var key = Latin1.GetString(b[s..q]).Trim().ToUpperInvariant();
                var vb = b[(q + 1)..e];
                if (key.StartsWith("%UTF8%")) utf[key[6..]] = Encoding.UTF8.GetString(vb);
                else if (!props.ContainsKey(key)) props[key] = decodeLocal ? DecodeLocal(vb) : Latin1.GetString(vb);
            }
            i = e > i ? e : i + 1;
        }
        foreach (var kv in utf) props[kv.Key] = kv.Value;
        return props;
    }

    /// <summary>Natural sort: R2 &lt; R10, case-insensitive.</summary>
    public static int NatCmp(string a, string b)
    {
        a ??= ""; b ??= "";
        int i = 0, j = 0;
        while (i < a.Length && j < b.Length)
        {
            char ca = a[i], cb = b[j];
            if (char.IsDigit(ca) && char.IsDigit(cb))
            {
                int si = i, sj = j;
                while (i < a.Length && char.IsDigit(a[i])) i++;
                while (j < b.Length && char.IsDigit(b[j])) j++;
                var na = a[si..i].TrimStart('0'); var nb = b[sj..j].TrimStart('0');
                if (na.Length != nb.Length) return na.Length.CompareTo(nb.Length);
                int c = string.CompareOrdinal(na, nb);
                if (c != 0) return c;
            }
            else
            {
                int c = char.ToUpperInvariant(ca).CompareTo(char.ToUpperInvariant(cb));
                if (c != 0) return c;
                i++; j++;
            }
        }
        return (a.Length - i).CompareTo(b.Length - j);
    }

    public static readonly Comparer<string> Natural = Comparer<string>.Create(NatCmp);

    /// <summary>KiCad escapes: {slash} {lt} ...</summary>
    public static string UnescapeK(string s)
    {
        if (string.IsNullOrEmpty(s) || s.IndexOf('{') < 0) return s ?? "";
        return Regex.Replace(s, @"\{(slash|backslash|lt|gt|dblquote|quote|comma|colon|space|tab|return|brl|brr|dollar|hash|tilde|plus)\}", m => m.Groups[1].Value switch
        {
            "slash" => "/", "backslash" => "\\", "lt" => "<", "gt" => ">", "dblquote" => "\"", "quote" => "'", "comma" => ",",
            "colon" => ":", "space" => " ", "tab" => "\t", "return" => "\n", "brl" => "[", "brr" => "]", "dollar" => "$",
            "hash" => "#", "tilde" => "~", "plus" => "+", _ => m.Value
        });
    }

    /// <summary>Strip overbar markup ~{...} to plain text (net names compare on this).</summary>
    public static string PlainText(string s) => Regex.Replace(UnescapeK(s), @"~\{([^}]*)\}", "$1");

    /// <summary>Altium overbar notation "R\S\T\" → internal "~{RST}".</summary>
    public static string AltBar(string s)
    {
        s ??= "";
        if (s.IndexOf('\\') < 0) return s;
        var o = new StringBuilder(); var bar = new StringBuilder();
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '\\') continue;
            if (i + 1 < s.Length && s[i + 1] == '\\') bar.Append(c);
            else
            {
                if (bar.Length > 0) { o.Append("~{").Append(bar).Append('}'); bar.Clear(); }
                o.Append(c);
            }
        }
        if (bar.Length > 0) o.Append("~{").Append(bar).Append('}');
        return o.ToString();
    }

    public static string BaseName(string p) => (p ?? "").Split('\\', '/').Last().ToLowerInvariant();

    public static int Int(Dictionary<string, string> p, string k, int def = 0) =>
        p != null && p.TryGetValue(k, out var v) ? ParseIntLoose(v, def) : def;

    /// <summary>Like JavaScript parseInt: leading sign and digits, rest ignored.</summary>
    public static int ParseIntLoose(string v, int def = 0)
    {
        if (string.IsNullOrEmpty(v)) return def;
        int i = 0; while (i < v.Length && char.IsWhiteSpace(v[i])) i++;
        bool neg = false;
        if (i < v.Length && (v[i] == '-' || v[i] == '+')) { neg = v[i] == '-'; i++; }
        long n = 0; int start = i;
        while (i < v.Length && v[i] >= '0' && v[i] <= '9' && n < int.MaxValue) { n = n * 10 + (v[i] - '0'); i++; }
        if (i == start) return def;
        return (int)Math.Clamp(neg ? -n : n, int.MinValue, int.MaxValue);
    }

    public static double Dbl(Dictionary<string, string> p, string k, double def = 0) =>
        p != null && p.TryGetValue(k, out var v) && double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var r) ? r : def;

    public static bool Bool(Dictionary<string, string> p, string k, bool def = false) =>
        p != null && p.TryGetValue(k, out var v) ? v.Length > 0 && (v[0] == 'T' || v[0] == 't') : def;

    public static string Str(Dictionary<string, string> p, string k, string def = "") =>
        p != null && p.TryGetValue(k, out var v) ? v : def;
}
