// Herald PCB Reader — Copyright (C) 2026 Johnny.ch.Tsai
// SPDX-License-Identifier: GPL-3.0-only
// Free software under the GNU General Public License version 3 (LICENSE.txt), WITHOUT ANY WARRANTY; with an
// additional permission to combine it with AvalonDock (Ms-PL), see NOTICE.txt.
using System.Buffers.Binary;
using System.IO.Compression;
using AdCore.Cfb;
using static AdCore.Util.TextUtil;

namespace AdCore.Step;

/// <summary>Triangles of one colour: positions and normals (mm, model space, x y z …) and indices. HasColour false: the
/// STEP file gave this part no colour (the body colour Altium shows is used).</summary>
public sealed class StepMeshPart
{
    public float R, G, B; public bool HasColour;
    public float[] Pos, Nrm; public int[] Idx;
}

public sealed class StepMesh
{
    public readonly List<StepMeshPart> Parts = new();
    public int Faces, Failed, Triangles;
}

public static class StepModel
{
    /// <summary>A STEP file → its solids as coloured triangles, in model space (mm). A model that comes out heavy (over
    /// 8000 triangles) is done again with a coarser tolerance, kept when that is lighter.</summary>
    public static StepMesh Tessellate(string text)
    {
        var st = new StepFile(text);
        var m = Tessellate(st, 0.03, 20);
        if (m.Triangles > 8000) { var c = Tessellate(st, 0.075, 35); if (c.Triangles < m.Triangles) m = c; }
        return m;
    }

    static StepMesh Tessellate(StepFile st, double tol, double maxAngDeg, int maxTriangles = 600_000)
    {
        var ts = new StepTess(st, tol, maxAngDeg);
        var cols = StepShapes.Colours(st);
        var faceCache = new Dictionary<int, FaceMesh>();
        var groups = new Dictionary<(int, int, int, bool), (List<float> pos, List<float> nrm, List<int> idx, (float r, float g, float b) c)>();
        var mesh = new StepMesh();
        int tris = 0;
        foreach (var (shape, m) in StepShapes.Instances(st))
        {
            var shells = new List<int>();
            var faces = StepShapes.Faces(st, shape, shells);
            (float, float, float)? shapeCol = cols.TryGetValue(shape, out var sc) ? sc : null;
            foreach (var sh in shells) if (shapeCol == null && cols.TryGetValue(sh, out var c2)) shapeCol = c2;
            foreach (var fid in faces)
            {
                if (!faceCache.TryGetValue(fid, out var f)) { f = ts.Face(fid); faceCache[fid] = f; }
                if (f == null) continue;
                (float r, float g, float b)? col = cols.TryGetValue(fid, out var fc) ? fc : shapeCol;
                var cc = col ?? (0.6f, 0.6f, 0.62f);
                var key = ((int)Math.Round(cc.r * 255), (int)Math.Round(cc.g * 255), (int)Math.Round(cc.b * 255), col != null);
                if (!groups.TryGetValue(key, out var gr)) groups[key] = gr = (new List<float>(), new List<float>(), new List<int>(), cc);
                int o = gr.pos.Count / 3;
                foreach (var p in f.Pos) { var q = m.Ap(p); gr.pos.Add((float)q.X); gr.pos.Add((float)q.Y); gr.pos.Add((float)q.Z); }
                foreach (var n in f.Nrm) { var q = m.ApV(n); gr.nrm.Add((float)q.X); gr.nrm.Add((float)q.Y); gr.nrm.Add((float)q.Z); }
                foreach (var i in f.Idx) gr.idx.Add(i + o);
                tris += f.Idx.Count / 3;
                if (tris > maxTriangles) break;
            }
            if (tris > maxTriangles) break;
        }
        foreach (var (key, g) in groups)
            mesh.Parts.Add(new StepMeshPart { R = g.c.r, G = g.c.g, B = g.c.b, HasColour = key.Item4, Pos = g.pos.ToArray(), Nrm = g.nrm.ToArray(), Idx = g.idx.ToArray() });
        mesh.Faces = ts.Faces; mesh.Failed = ts.Failed; mesh.Triangles = tris;
        return mesh;
    }

    /// <summary>The 3D models embedded in a .PcbDoc (Models/Data names them, Models/&lt;n&gt; hold them zlib-compressed):
    /// those asked for, by model ID, tessellated on a few threads. Models that cannot be read are left out.</summary>
    public static Dictionary<string, StepMesh> LoadEmbedded(string pcbPath, ICollection<string> ids, int threads, CancellationToken ct = default)
    {
        var res = new Dictionary<string, StepMesh>(StringComparer.OrdinalIgnoreCase);
        if (ids.Count == 0 || !File.Exists(pcbPath)) return res;
        var cfb = new CompoundFile(File.ReadAllBytes(pcbPath));
        var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var d = cfb.Read("Models", "Data");
        if (d == null) return res;
        for (int p = 0, i = 0; p + 4 <= d.Length; i++)
        {
            int l = (int)(BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(p)) & 0xffffff);
            if (l < 0 || p + 4 + l > d.Length) break;
            var text = Latin1.GetString(d, p + 4, l);
            p += 4 + l;
            foreach (var kv in text.Split('|'))
            {
                int eq = kv.IndexOf('='); if (eq < 0) continue;
                if (kv[..eq].Trim().Equals("ID", StringComparison.OrdinalIgnoreCase)) { index.TryAdd(kv[(eq + 1)..].Trim().TrimEnd('\0'), i); break; }
            }
        }
        var work = ids.Where(index.ContainsKey).Select(id => (id, n: index[id])).ToList();
        var streams = work.Select(w => (w.id, data: cfb.Read("Models", w.n.ToString(System.Globalization.CultureInfo.InvariantCulture)))).Where(w => w.data != null).ToList();
        var lk = new object();
        Parallel.ForEach(streams, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, threads), CancellationToken = ct }, w =>
        {
            try
            {
                string text;
                using (var z = new ZLibStream(new MemoryStream(w.data), CompressionMode.Decompress))
                using (var ms = new MemoryStream())
                {
                    z.CopyTo(ms);
                    if (ms.Length > 200_000_000) return;
                    text = Latin1.GetString(ms.GetBuffer(), 0, (int)ms.Length);
                }
                StepMesh mesh; string kind;
                if (text.Contains("ISO-10303-21", StringComparison.Ordinal)) { kind = "STEP"; mesh = Tessellate(text); }
                else if (text.Contains("**PARASOLID", StringComparison.Ordinal)) { kind = "Parasolid"; mesh = XtModel.Tessellate(text); }   // .x_t (text) only
                else { Note(w.id, "not STEP or Parasolid"); return; }
                Note(w.id, mesh == null ? kind + ": not read" : $"{kind}: {mesh.Faces} faces, {mesh.Failed} failed, {mesh.Triangles} triangles");
                if (mesh != null && mesh.Triangles > 0) lock (lk) res[w.id] = mesh;
            }
            catch (Exception e) { Note(w.id, e.GetType().Name + ": " + e.Message + " @ " + string.Join(" / ", (e.StackTrace ?? "").Split('\n').Take(3).Select(l => l.Trim()))); }
        });
        return res;
    }

    /// <summary>What happened to each model read last (for tests and support).</summary>
    public static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> LastLog = new();
    static void Note(string id, string s) => LastLog[id] = s;
}
