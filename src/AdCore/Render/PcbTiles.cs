// Herald PCB Reader — Copyright (C) 2026 Johnny.ch.Tsai
// SPDX-License-Identifier: GPL-3.0-only
// Free software under the GNU General Public License version 3 (LICENSE.txt), WITHOUT ANY WARRANTY; with an
// additional permission to combine it with AvalonDock (Ms-PL), see NOTICE.txt.
using SkiaSharp;

namespace AdCore.Render;

/// <summary>The board's layers rendered once into 512-px tiles at power-of-two zoom levels. The view only moves
/// and scales finished tiles; the shallow levels (whole board up to about 4000 px) are rendered right away and
/// kept, deeper levels are rendered the first time they are seen and then cached. Highlight, selection, NET
/// names and the measure tool are drawn on top every frame, so they never invalidate tiles.
/// Tiles are rendered by background workers shared by all open boards.</summary>
public sealed class PcbTiles : IDisposable
{
    public const int T = 512;
    const long MaxBytes = 320L << 20;
    public readonly SKRect Content;   // world area covered (Y up): tile (0,0) is its top-left corner
    public readonly double S0;        // level-0 scale (pixels per mil): the whole content fits one tile
    public readonly int MaxLevel;
    public int PreLevel { get; private set; }

    readonly object _lock = new();
    readonly Dictionary<long, Tile> _tiles = new();
    long _bytes, _clock;
    Spec _spec;                        // what the current tiles show
    bool _disposed;
    int _running;
    public event Action TileReady;     // raised on a worker thread

    sealed class Tile { public SKImage Img; public long Used; public bool Pinned; }
    sealed class Spec { public int Gen; public IReadOnlyList<(SKPicture pic, float alpha)> Stack; }

    public PcbTiles(SKRect content)
    {
        var c = content; if (c.IsEmpty || c.Width < 1 || c.Height < 1) c = new SKRect(0, 0, 1000, 1000);
        float pad = Math.Max(c.Width, c.Height) * 0.01f + 10;
        Content = new SKRect(c.Left - pad, c.Top - pad, c.Right + pad, c.Bottom + pad);
        S0 = T / Math.Max(Content.Width, Content.Height);
        MaxLevel = Math.Clamp((int)Math.Ceiling(Math.Log2(1200 / S0)), 0, 24);   // ~1200 px per mil at the deepest
    }

    public static long Key(int z, int tx, int ty) => ((long)z << 48) | ((long)tx << 24) | (uint)ty;
    public double Scale(int z) => S0 * Math.Pow(2, z);
    public (int nx, int ny) Count(int z) { double s = Scale(z); return (Math.Max(1, (int)Math.Ceiling(Content.Width * s / T)), Math.Max(1, (int)Math.Ceiling(Content.Height * s / T))); }

    /// <summary>Show a new layer stack (layers / colours / order changed): drops every tile and starts again.</summary>
    public void SetStack(IReadOnlyList<(SKPicture pic, float alpha)> stack)
    {
        lock (_lock)
        {
            int gen = (_spec?.Gen ?? 0) + 1;
            _spec = new Spec { Gen = gen, Stack = stack };
            foreach (var t in _tiles.Values) t.Img.Dispose();
            _tiles.Clear(); _bytes = 0;
            TileWorkers.Cancel(this);
            // pre-render the whole board at the shallow levels (up to ~64 tiles per level)
            PreLevel = 0;
            for (int z = 0; z <= MaxLevel; z++) { var (nx, ny) = Count(z); if (nx * ny > 64) break; PreLevel = z; }
            for (int z = 0; z <= PreLevel; z++)
            {
                var (nx, ny) = Count(z);
                for (int ty = 0; ty < ny; ty++) for (int tx = 0; tx < nx; tx++) TileWorkers.Enqueue(this, gen, z, tx, ty, 10 + z);
            }
        }
    }

    /// <summary>A finished tile, or null (then <see cref="Request"/> it).</summary>
    public SKImage Get(int z, int tx, int ty)
    {
        lock (_lock)
        {
            if (!_tiles.TryGetValue(Key(z, tx, ty), out var t)) return null;
            t.Used = ++_clock; return t.Img;
        }
    }

    public void Request(int z, int tx, int ty)
    {
        lock (_lock) { if (_spec == null || _disposed || _tiles.ContainsKey(Key(z, tx, ty))) return; TileWorkers.Enqueue(this, _spec.Gen, z, tx, ty, 0); }
    }

    /// <summary>World rectangle of a tile (plus a 1 px border that is rendered for seamless sampling).</summary>
    public SKRect TileWorld(int z, int tx, int ty, double border = 0)
    {
        double s = Scale(z);
        double x0 = Content.Left + (tx * T - border) / s, x1 = Content.Left + ((tx + 1) * T + border) / s;
        double yTop = Content.Bottom - (ty * T - border) / s, yBot = Content.Bottom - ((ty + 1) * T + border) / s;
        return new SKRect((float)x0, (float)yBot, (float)x1, (float)yTop);
    }

    internal SKImage Render(int gen, int z, int tx, int ty)
    {
        Spec spec;
        lock (_lock) { if (_disposed || _spec == null || _spec.Gen != gen || _tiles.ContainsKey(Key(z, tx, ty))) return null; spec = _spec; _running++; }
        try
        {
            double s = Scale(z);
            using var surf = SKSurface.Create(new SKImageInfo(T + 2, T + 2, SKColorType.Bgra8888, SKAlphaType.Premul));
            if (surf == null) return null;
            var c = surf.Canvas; c.Clear(SKColors.Transparent);
            // world → tile pixel: px = (wx − left)·s − tx·T + 1, py = (bottom − wy)·s − ty·T + 1
            c.SetMatrix(new SKMatrix((float)s, 0, (float)(-Content.Left * s - tx * T + 1), 0, (float)-s, (float)(Content.Bottom * s - ty * T + 1), 0, 0, 1));
            c.ClipRect(TileWorld(z, tx, ty, 1.5));
            PcbRenderer.DrawStack(c, spec.Stack);
            return surf.Snapshot();
        }
        catch { return null; }
        finally { lock (_lock) _running--; }
    }

    internal void Store(int gen, int z, int tx, int ty, SKImage img)
    {
        lock (_lock)
        {
            if (_disposed || _spec == null || _spec.Gen != gen || _tiles.ContainsKey(Key(z, tx, ty))) { img.Dispose(); return; }
            _tiles[Key(z, tx, ty)] = new Tile { Img = img, Used = ++_clock, Pinned = z <= PreLevel };
            _bytes += (long)img.Width * img.Height * 4;
            if (_bytes > MaxBytes)
            {
                // evict the least recently used deep tiles (the pre-rendered levels stay)
                foreach (var (k, t) in _tiles.Where(kv => !kv.Value.Pinned).OrderBy(kv => kv.Value.Used).ToList())
                {
                    if (_bytes <= MaxBytes * 3 / 4) break;
                    _tiles.Remove(k); _bytes -= (long)t.Img.Width * t.Img.Height * 4; t.Img.Dispose();
                }
            }
        }
        TileReady?.Invoke();
    }

    internal bool Alive(int gen) { lock (_lock) return !_disposed && _spec != null && _spec.Gen == gen; }

    public void Dispose()
    {
        lock (_lock) { _disposed = true; TileWorkers.Cancel(this); }
        // wait for a tile that is being rendered right now
        for (int i = 0; i < 400; i++) { lock (_lock) if (_running == 0) break; Thread.Sleep(5); }
        lock (_lock) { foreach (var t in _tiles.Values) t.Img.Dispose(); _tiles.Clear(); }
    }
}

/// <summary>Background threads that render tiles for every open board; visible tiles first.</summary>
static class TileWorkers
{
    sealed class Job { public PcbTiles Owner; public int Gen, Z, X, Y, Prio; public long Seq; }
    static readonly List<Job> Queue = new();
    static readonly HashSet<(PcbTiles, long)> Queued = new();
    static long _seq;
    static bool _started;
    static readonly object Lock = new();

    static void Start()
    {
        int n = Math.Clamp(Environment.ProcessorCount - 1, 1, 6);
        for (int i = 0; i < n; i++) new Thread(Run) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "PcbTile" + i }.Start();
        _started = true;
    }

    public static void Enqueue(PcbTiles owner, int gen, int z, int x, int y, int prio)
    {
        lock (Lock)
        {
            if (!_started) Start();
            var key = (owner, PcbTiles.Key(z, x, y));
            if (Queued.Contains(key))
            {
                // already waiting: a visible request moves it to the front
                if (prio == 0) foreach (var j in Queue) if (j.Owner == owner && j.Z == z && j.X == x && j.Y == y) { j.Prio = 0; j.Seq = ++_seq; j.Gen = gen; }
                return;
            }
            Queued.Add(key);
            Queue.Add(new Job { Owner = owner, Gen = gen, Z = z, X = x, Y = y, Prio = prio, Seq = ++_seq });
            Monitor.Pulse(Lock);
        }
    }

    public static void Cancel(PcbTiles owner)
    {
        lock (Lock) { Queue.RemoveAll(j => j.Owner == owner); Queued.RemoveWhere(k => k.Item1 == owner); }
    }

    static void Run()
    {
        while (true)
        {
            Job job;
            lock (Lock)
            {
                while (Queue.Count == 0) Monitor.Wait(Lock);
                // lowest priority number first; among visible requests the newest first (what is on screen now)
                job = Queue[0];
                foreach (var j in Queue) if (j.Prio < job.Prio || j.Prio == job.Prio && (j.Prio == 0 ? j.Seq > job.Seq : j.Seq < job.Seq)) job = j;
                Queue.Remove(job); Queued.Remove((job.Owner, PcbTiles.Key(job.Z, job.X, job.Y)));
            }
            if (!job.Owner.Alive(job.Gen)) continue;
            var img = job.Owner.Render(job.Gen, job.Z, job.X, job.Y);
            if (img != null) job.Owner.Store(job.Gen, job.Z, job.X, job.Y, img);
        }
    }
}
