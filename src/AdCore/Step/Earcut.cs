// Herald PCB Reader — Copyright (C) 2026 Johnny.ch.Tsai
// SPDX-License-Identifier: GPL-3.0-only
// Free software under the GNU General Public License version 3 (LICENSE.txt), WITHOUT ANY WARRANTY; with an
// additional permission to combine it with AvalonDock (Ms-PL), see NOTICE.txt.
//
// Polygon (with holes) triangulation by ear clipping, ported from the earcut algorithm by Mapbox
// (https://github.com/mapbox/earcut 3.2.4, ISC licence, Copyright (c) Mapbox; licence text in
// tools/licenses/earcut-LICENSE.txt and THIRD-PARTY-NOTICES.txt);
// this version leaves out the z-order hashing.
namespace AdCore.Step;

public static class Earcut
{
    sealed class Node
    {
        public int I; public double X, Y; public Node Prev, Next; public bool Steiner;
        public Node(int i, double x, double y) { I = i; X = x; Y = y; }
    }

    /// <summary>Triangles (vertex indices) of the polygon in data (x, y pairs): the outer ring first, each hole starting at
    /// the vertex index given in holes.</summary>
    public static List<int> Triangulate(IList<double> data, IList<int> holes)
    {
        var tris = new List<int>();
        bool hasHoles = holes != null && holes.Count > 0;
        int outerLen = hasHoles ? holes[0] * 2 : data.Count;
        var outer = LinkedList(data, 0, outerLen, true);
        if (outer == null || outer.Next == outer.Prev) return tris;
        if (hasHoles) outer = EliminateHoles(data, holes, outer);
        long guard = 0;
        EarcutLinked(outer, tris, 0, ref guard);
        return tris;
    }

    static Node LinkedList(IList<double> data, int start, int end, bool clockwise)
    {
        Node last = null;
        if (clockwise == (SignedArea(data, start, end) > 0)) for (int i = start; i < end; i += 2) last = InsertNode(i / 2, data[i], data[i + 1], last);
        else for (int i = end - 2; i >= start; i -= 2) last = InsertNode(i / 2, data[i], data[i + 1], last);
        if (last != null && Same(last, last.Next)) { RemoveNode(last); last = last.Next; }
        return last;
    }

    static Node FilterPoints(Node start, Node end = null)
    {
        if (start == null) return start;
        end ??= start;
        var p = start; bool again;
        do
        {
            again = false;
            if (!p.Steiner && (Same(p, p.Next) || Area(p.Prev, p, p.Next) == 0))
            {
                RemoveNode(p); p = end = p.Prev;
                if (p == p.Next) break;
                again = true;
            }
            else p = p.Next;
        } while (again || p != end);
        return end;
    }

    static void EarcutLinked(Node ear, List<int> tris, int pass, ref long guard)
    {
        if (ear == null) return;
        var stop = ear;
        while (ear.Prev != ear.Next)
        {
            if (++guard > 20_000_000) return;
            var prev = ear.Prev; var next = ear.Next;
            if (IsEar(ear))
            {
                tris.Add(prev.I); tris.Add(ear.I); tris.Add(next.I);
                RemoveNode(ear);
                ear = next.Next; stop = next.Next;
                continue;
            }
            ear = next;
            if (ear == stop)
            {
                if (pass == 0) EarcutLinked(FilterPoints(ear), tris, 1, ref guard);
                else if (pass == 1) { ear = CureLocalIntersections(FilterPoints(ear), tris); EarcutLinked(ear, tris, 2, ref guard); }
                else if (pass == 2) SplitEarcut(ear, tris, ref guard);
                break;
            }
        }
    }

    static bool IsEar(Node ear)
    {
        Node a = ear.Prev, b = ear, c = ear.Next;
        if (Area(a, b, c) >= 0) return false;
        double x0 = Math.Min(a.X, Math.Min(b.X, c.X)), y0 = Math.Min(a.Y, Math.Min(b.Y, c.Y)), x1 = Math.Max(a.X, Math.Max(b.X, c.X)), y1 = Math.Max(a.Y, Math.Max(b.Y, c.Y));
        var p = c.Next;
        while (p != a)
        {
            if (p.X >= x0 && p.X <= x1 && p.Y >= y0 && p.Y <= y1 && PointInTriangle(a.X, a.Y, b.X, b.Y, c.X, c.Y, p.X, p.Y) && Area(p.Prev, p, p.Next) >= 0) return false;
            p = p.Next;
        }
        return true;
    }

    static Node CureLocalIntersections(Node start, List<int> tris)
    {
        var p = start;
        do
        {
            Node a = p.Prev, b = p.Next.Next;
            if (!Same(a, b) && Intersects(a, p, p.Next, b) && LocallyInside(a, b) && LocallyInside(b, a))
            {
                tris.Add(a.I); tris.Add(p.I); tris.Add(b.I);
                RemoveNode(p); RemoveNode(p.Next);
                p = start = b;
            }
            p = p.Next;
        } while (p != start);
        return FilterPoints(p);
    }

    static void SplitEarcut(Node start, List<int> tris, ref long guard)
    {
        var a = start;
        do
        {
            var b = a.Next.Next;
            while (b != a.Prev)
            {
                if (a.I != b.I && IsValidDiagonal(a, b))
                {
                    var c = SplitPolygon(a, b);
                    a = FilterPoints(a, a.Next); c = FilterPoints(c, c.Next);
                    EarcutLinked(a, tris, 0, ref guard); EarcutLinked(c, tris, 0, ref guard);
                    return;
                }
                b = b.Next;
            }
            a = a.Next;
        } while (a != start);
    }

    static Node EliminateHoles(IList<double> data, IList<int> holes, Node outer)
    {
        var queue = new List<Node>();
        for (int i = 0; i < holes.Count; i++)
        {
            int start = holes[i] * 2, end = i < holes.Count - 1 ? holes[i + 1] * 2 : data.Count;
            var list = LinkedList(data, start, end, false);
            if (list == null) continue;
            if (list == list.Next) list.Steiner = true;
            queue.Add(GetLeftmost(list));
        }
        queue.Sort((a, b) => a.X.CompareTo(b.X));
        foreach (var h in queue) outer = EliminateHole(h, outer);
        return outer;
    }

    static Node EliminateHole(Node hole, Node outer)
    {
        var bridge = FindHoleBridge(hole, outer);
        if (bridge == null) return outer;
        var bridgeReverse = SplitPolygon(bridge, hole);
        FilterPoints(bridgeReverse, bridgeReverse.Next);
        return FilterPoints(bridge, bridge.Next);
    }

    static Node FindHoleBridge(Node hole, Node outer)
    {
        var p = outer; double hx = hole.X, hy = hole.Y, qx = double.NegativeInfinity; Node m = null;
        do
        {
            if (hy <= p.Y && hy >= p.Next.Y && p.Next.Y != p.Y)
            {
                double x = p.X + (hy - p.Y) * (p.Next.X - p.X) / (p.Next.Y - p.Y);
                if (x <= hx && x > qx) { qx = x; m = p.X < p.Next.X ? p : p.Next; if (x == hx) return m; }
            }
            p = p.Next;
        } while (p != outer);
        if (m == null) return null;
        var stop = m; double mx = m.X, my = m.Y, tanMin = double.PositiveInfinity;
        p = m;
        do
        {
            if (hx >= p.X && p.X >= mx && hx != p.X && PointInTriangle(hy < my ? hx : qx, hy, mx, my, hy < my ? qx : hx, hy, p.X, p.Y))
            {
                double tan = Math.Abs(hy - p.Y) / (hx - p.X);
                if (LocallyInside(p, hole) && (tan < tanMin || (tan == tanMin && (p.X > m.X || (p.X == m.X && SectorContainsSector(m, p)))))) { m = p; tanMin = tan; }
            }
            p = p.Next;
        } while (p != stop);
        return m;
    }

    static bool SectorContainsSector(Node m, Node p) => Area(m.Prev, m, p.Prev) < 0 && Area(p.Next, m, m.Next) < 0;
    static Node GetLeftmost(Node start) { Node p = start, l = start; do { if (p.X < l.X || (p.X == l.X && p.Y < l.Y)) l = p; p = p.Next; } while (p != start); return l; }
    static bool PointInTriangle(double ax, double ay, double bx, double by, double cx, double cy, double px, double py) =>
        (cx - px) * (ay - py) >= (ax - px) * (cy - py) && (ax - px) * (by - py) >= (bx - px) * (ay - py) && (bx - px) * (cy - py) >= (cx - px) * (by - py);
    static bool IsValidDiagonal(Node a, Node b) =>
        a.Next.I != b.I && a.Prev.I != b.I && !IntersectsPolygon(a, b) &&
        (LocallyInside(a, b) && LocallyInside(b, a) && MiddleInside(a, b) && (Area(a.Prev, a, b.Prev) != 0 || Area(a, b.Prev, b) != 0) ||
         Same(a, b) && Area(a.Prev, a, a.Next) > 0 && Area(b.Prev, b, b.Next) > 0);
    static double Area(Node p, Node q, Node r) => (q.Y - p.Y) * (r.X - q.X) - (q.X - p.X) * (r.Y - q.Y);
    static bool Same(Node a, Node b) => a.X == b.X && a.Y == b.Y;
    static int Sign(double n) => n > 0 ? 1 : n < 0 ? -1 : 0;
    static bool OnSegment(Node p, Node q, Node r) => q.X <= Math.Max(p.X, r.X) && q.X >= Math.Min(p.X, r.X) && q.Y <= Math.Max(p.Y, r.Y) && q.Y >= Math.Min(p.Y, r.Y);
    static bool Intersects(Node p1, Node q1, Node p2, Node q2)
    {
        int o1 = Sign(Area(p1, q1, p2)), o2 = Sign(Area(p1, q1, q2)), o3 = Sign(Area(p2, q2, p1)), o4 = Sign(Area(p2, q2, q1));
        if (o1 != o2 && o3 != o4) return true;
        if (o1 == 0 && OnSegment(p1, p2, q1)) return true;
        if (o2 == 0 && OnSegment(p1, q2, q1)) return true;
        if (o3 == 0 && OnSegment(p2, p1, q2)) return true;
        if (o4 == 0 && OnSegment(p2, q1, q2)) return true;
        return false;
    }
    static bool IntersectsPolygon(Node a, Node b)
    {
        var p = a;
        do { if (p.I != a.I && p.Next.I != a.I && p.I != b.I && p.Next.I != b.I && Intersects(p, p.Next, a, b)) return true; p = p.Next; } while (p != a);
        return false;
    }
    static bool LocallyInside(Node a, Node b) =>
        Area(a.Prev, a, a.Next) < 0 ? Area(a, b, a.Next) >= 0 && Area(a, a.Prev, b) >= 0 : Area(a, b, a.Prev) < 0 || Area(a, a.Next, b) < 0;
    static bool MiddleInside(Node a, Node b)
    {
        var p = a; bool inside = false; double px = (a.X + b.X) / 2, py = (a.Y + b.Y) / 2;
        do
        {
            if (((p.Y > py) != (p.Next.Y > py)) && p.Next.Y != p.Y && (px < (p.Next.X - p.X) * (py - p.Y) / (p.Next.Y - p.Y) + p.X)) inside = !inside;
            p = p.Next;
        } while (p != a);
        return inside;
    }
    static Node SplitPolygon(Node a, Node b)
    {
        Node a2 = new(a.I, a.X, a.Y), b2 = new(b.I, b.X, b.Y), an = a.Next, bp = b.Prev;
        a.Next = b; b.Prev = a; a2.Next = an; an.Prev = a2; b2.Next = a2; a2.Prev = b2; bp.Next = b2; b2.Prev = bp;
        return b2;
    }
    static Node InsertNode(int i, double x, double y, Node last)
    {
        var p = new Node(i, x, y);
        if (last == null) { p.Prev = p; p.Next = p; }
        else { p.Next = last.Next; p.Prev = last; last.Next.Prev = p; last.Next = p; }
        return p;
    }
    static void RemoveNode(Node p) { p.Next.Prev = p.Prev; p.Prev.Next = p.Next; }
    static double SignedArea(IList<double> data, int start, int end)
    {
        double sum = 0;
        for (int i = start, j = end - 2; i < end; i += 2) { sum += (data[j] - data[i]) * (data[i + 1] + data[j + 1]); j = i; }
        return sum;
    }
}
