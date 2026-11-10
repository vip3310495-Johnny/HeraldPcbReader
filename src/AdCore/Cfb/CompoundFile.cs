// Herald PCB Reader — Copyright (C) 2026 Johnny.ch.Tsai
// SPDX-License-Identifier: GPL-3.0-only
// Free software under the GNU General Public License version 3 (LICENSE.txt), WITHOUT ANY WARRANTY; with an
// additional permission to combine it with AvalonDock (Ms-PL), see NOTICE.txt.
using System.Buffers.Binary;
using System.Text;

namespace AdCore.Cfb;

/// <summary>Read-only reader for OLE2 / Compound File Binary (the container of .SchDoc and .PcbDoc).</summary>
public sealed class CompoundFile
{
    const uint End = 0xFFFFFFFA;

    sealed class Entry
    {
        public string Name;
        public byte Type;            // 1 storage, 2 stream, 5 root
        public uint Left, Right, Child, Start;
        public long Size;
    }

    readonly byte[] _buf;
    readonly int _ss, _mss;
    readonly uint _sectors;   // whole sectors in the file (after the header)
    readonly uint _cutoff;
    readonly uint[] _fat;
    uint[] _miniFat;
    byte[] _mini;
    readonly uint _miniFatStart;
    readonly List<Entry> _entries = new();

    public static bool IsCompoundFile(byte[] b) =>
        b.Length >= 8 && b[0] == 0xD0 && b[1] == 0xCF && b[2] == 0x11 && b[3] == 0xE0;

    public CompoundFile(byte[] buf)
    {
        if (buf.Length < 512 || !IsCompoundFile(buf)) throw new InvalidDataException("不是 OLE 複合檔案（.SchDoc / .PcbDoc）");
        _buf = buf;
        // a damaged or crafted file must not make the reader loop or allocate more than the file holds: sector sizes
        // are the two the format allows, and every count and chain is bounded by the sectors really in the file
        int shift = U16(30), miniShift = U16(32);
        if (shift is not (9 or 12) || miniShift != 6) throw new InvalidDataException("檔案損毀：磁區大小不正確");
        _ss = 1 << shift;
        _mss = 1 << miniShift;
        _sectors = (uint)(buf.Length / _ss);
        uint nFat = Math.Min(U32(44), _sectors), dirStart = U32(48);
        _cutoff = U32(56) == 0 ? 4096 : U32(56);
        _miniFatStart = U32(60);
        uint difStart = U32(68), nDif = Math.Min(U32(72), _sectors);

        var fatSecs = new List<uint>();
        for (int i = 0; i < 109 && fatSecs.Count < nFat; i++) { uint s = U32(76 + i * 4); if (s < End) fatSecs.Add(s); }
        uint d = difStart; var seenDif = new HashSet<uint>();
        for (uint k = 0; k < nDif && d < End && seenDif.Add(d); k++)
        {
            long o = Off(d);
            if (o + _ss > buf.Length) break;
            for (int i = 0; i < _ss / 4 - 1 && fatSecs.Count < nFat; i++) fatSecs.Add(U32(o + i * 4));
            d = U32(o + _ss - 4);
        }
        int per = _ss / 4;
        _fat = new uint[fatSecs.Count * per];
        for (int i = 0; i < fatSecs.Count; i++)
        {
            long o = Off(fatSecs[i]);
            for (int j = 0; j < per && o + j * 4 + 4 <= buf.Length; j++) _fat[i * per + j] = U32(o + j * 4);
        }

        var dir = ReadBig(dirStart, -1);
        for (int o = 0; o + 128 <= dir.Length; o += 128)
        {
            int nl = dir[o + 64] | (dir[o + 65] << 8);
            int chars = Math.Max(0, Math.Min(nl, 64) / 2 - 1);
            _entries.Add(new Entry
            {
                Name = Encoding.Unicode.GetString(dir, o, chars * 2),
                Type = dir[o + 66],
                Left = BinaryPrimitives.ReadUInt32LittleEndian(dir.AsSpan(o + 68)),
                Right = BinaryPrimitives.ReadUInt32LittleEndian(dir.AsSpan(o + 72)),
                Child = BinaryPrimitives.ReadUInt32LittleEndian(dir.AsSpan(o + 76)),
                Start = BinaryPrimitives.ReadUInt32LittleEndian(dir.AsSpan(o + 116)),
                Size = BinaryPrimitives.ReadUInt32LittleEndian(dir.AsSpan(o + 120)),
            });
        }
    }

    ushort U16(long o) => BinaryPrimitives.ReadUInt16LittleEndian(_buf.AsSpan((int)o));
    uint U32(long o) => o + 4 <= _buf.Length ? BinaryPrimitives.ReadUInt32LittleEndian(_buf.AsSpan((int)o)) : End;
    long Off(uint s) => (long)(s + 1) * _ss;

    /// <summary>The sectors of a chain: it ends at the end mark, at a sector the file does not have (limit), or where
    /// it would come round to a sector it already passed (a loop in a damaged file).</summary>
    static List<uint> Chain(uint s, uint[] table, uint limit)
    {
        var o = new List<uint>();
        var seen = new bool[Math.Min((uint)table.Length, limit)];
        while (s < End && s < seen.Length && !seen[s]) { seen[s] = true; o.Add(s); s = table[s]; }
        return o;
    }

    byte[] ReadBig(uint start, long size)
    {
        var secs = Chain(start, _fat, _sectors);
        var o = new byte[secs.Count * _ss];
        for (int i = 0; i < secs.Count; i++)
        {
            long off = Off(secs[i]);
            int n = (int)Math.Min(_ss, Math.Max(0, _buf.Length - off));
            if (n > 0) Buffer.BlockCopy(_buf, (int)off, o, i * _ss, n);
        }
        if (size < 0 || size >= o.Length) return o;
        Array.Resize(ref o, (int)size);
        return o;
    }

    byte[] ReadSmall(uint start, long size)
    {
        if (_mini == null)
        {
            var root = _entries.Count > 0 ? _entries[0] : null;
            _mini = root == null ? Array.Empty<byte>() : ReadBig(root.Start, root.Size);
            var mf = ReadBig(_miniFatStart, -1);
            _miniFat = new uint[mf.Length / 4];
            for (int i = 0; i < _miniFat.Length; i++) _miniFat[i] = BinaryPrimitives.ReadUInt32LittleEndian(mf.AsSpan(i * 4));
        }
        var secs = Chain(start, _miniFat, (uint)(_mini.Length / _mss));
        var o = new byte[secs.Count * _mss];
        for (int i = 0; i < secs.Count; i++)
        {
            long off = (long)secs[i] * _mss;
            int n = (int)Math.Min(_mss, Math.Max(0, _mini.Length - off));
            if (n > 0) Buffer.BlockCopy(_mini, (int)off, o, i * _mss, n);
        }
        if (size >= o.Length) return o;
        Array.Resize(ref o, (int)size);
        return o;
    }

    IEnumerable<int> Children(int idx)
    {
        var res = new List<int>();
        if (idx < 0 || idx >= _entries.Count) return res;
        var stack = new Stack<uint>(); var seen = new HashSet<uint>();
        stack.Push(_entries[idx].Child);
        while (stack.Count > 0)
        {
            uint i = stack.Pop();
            if (i >= _entries.Count || !seen.Add(i)) continue;   // a loop in the directory tree is followed once
            res.Add((int)i);
            stack.Push(_entries[(int)i].Left);
            stack.Push(_entries[(int)i].Right);
        }
        return res;
    }

    Entry Find(params string[] path)
    {
        int idx = 0;
        foreach (var part in path)
        {
            int hit = -1;
            foreach (var c in Children(idx))
                if (string.Equals(_entries[c].Name, part, StringComparison.OrdinalIgnoreCase)) { hit = c; break; }
            if (hit < 0) return null;
            idx = hit;
        }
        return _entries[idx];
    }

    /// <summary>Read a stream by path, e.g. Read("FileHeader") or Read("Tracks6", "Data"). Null when missing.</summary>
    public byte[] Read(params string[] path)
    {
        var e = Find(path);
        if (e == null || e.Type != 2) return null;
        return e.Size < _cutoff ? ReadSmall(e.Start, e.Size) : ReadBig(e.Start, e.Size);
    }

    public IReadOnlyList<string> RootNames() => Children(0).Select(i => _entries[i].Name).ToList();
}
