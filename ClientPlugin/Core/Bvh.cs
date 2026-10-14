using System;
using System.Collections.Generic;
using System.Numerics;

namespace SERT.Core;

public sealed class Bvh
{
    public readonly BvhNode[] Nodes;
    public readonly int[] Order;
    public readonly int Depth;
    public Bvh(BvhNode[] nodes, int[] order, int depth) { Nodes = nodes; Order = order; Depth = depth; }

    public static Bvh Build(Bounds[] bounds, int leafSize = 4)
    {
        if (bounds == null) throw new ArgumentNullException(nameof(bounds));
        if (leafSize < 1) throw new ArgumentOutOfRangeException(nameof(leafSize));
        if (bounds.Length == 0) return new Bvh(Array.Empty<BvhNode>(), Array.Empty<int>(), 0);
        var order = new int[bounds.Length];
        for (int i = 0; i < order.Length; i++)
        {
            if (bounds[i].IsEmpty || !Budget.Finite(bounds[i].Min) || !Budget.Finite(bounds[i].Max))
                throw new ArgumentException("Non-finite or empty primitive bounds");
            order[i] = i;
        }
        var nodes = new List<BvhNode>();
        nodes.Add(default);
        int maxDepth = 0;
        BuildNode(0, 0, order.Length, 0);
        return new Bvh(nodes.ToArray(), order, maxDepth);

        void BuildNode(int index, int start, int count, int depth)
        {
            maxDepth = Math.Max(maxDepth, depth);
            var box = Bounds.Empty; var centroids = Bounds.Empty;
            for (int i = start; i < start + count; ++i)
            { box = box.Include(bounds[order[i]]); centroids = centroids.Include(bounds[order[i]].Center); }
            if (count <= leafSize || depth == Budget.MaxDepth)
            { nodes[index] = new BvhNode { Min = box.Min, Max = box.Max, Left = (uint)start, Count = (uint)count }; return; }
            const int bins = 16;
            int bestAxis = -1, bestBin = -1; float best = float.PositiveInfinity;
            for (int axis = 0; axis < 3; ++axis)
            {
                float extent = Component(centroids.Max - centroids.Min, axis);
                if (extent < 1e-8f) continue;
                var bb = new Bounds[bins]; var bc = new int[bins];
                for (int j = 0; j < bins; j++) bb[j] = Bounds.Empty;
                for (int j = start; j < start + count; j++)
                {
                    int bin = Bin(bounds[order[j]].Center, axis, centroids, extent);
                    bc[bin]++; bb[bin] = bb[bin].Include(bounds[order[j]]);
                }
                for (int split = 0; split < bins - 1; split++)
                {
                    int lc = 0, rc = 0; var lb = Bounds.Empty; var rb = Bounds.Empty;
                    for (int j = 0; j <= split; j++) { lc += bc[j]; if (bc[j] != 0) lb = lb.Include(bb[j]); }
                    for (int j = split + 1; j < bins; j++) { rc += bc[j]; if (bc[j] != 0) rb = rb.Include(bb[j]); }
                    if (lc == 0 || rc == 0) continue;
                    float cost = lb.Area * lc + rb.Area * rc;
                    if (cost < best) { best = cost; bestAxis = axis; bestBin = split; }
                }
            }
            int middle = start;
            if (bestAxis >= 0)
            {
                float extent = Component(centroids.Max - centroids.Min, bestAxis);
                for (int i = start; i < start + count; i++)
                    if (Bin(bounds[order[i]].Center, bestAxis, centroids, extent) <= bestBin)
                    { var t = order[i]; order[i] = order[middle]; order[middle++] = t; }
            }
            if (middle == start || middle == start + count) middle = start + count / 2;
            int left = nodes.Count; nodes.Add(default); nodes.Add(default);
            nodes[index] = new BvhNode { Min = box.Min, Max = box.Max, Left = (uint)left };
            BuildNode(left, start, middle - start, depth + 1);
            BuildNode(left + 1, middle, start + count - middle, depth + 1);
        }
        int Bin(Vector3 p, int axis, Bounds c, float extent) => Math.Min(15,
            Math.Max(0, (int)((Component(p, axis) - Component(c.Min, axis)) * 16f / extent)));
    }

    public static float Component(Vector3 v, int axis) => axis == 0 ? v.X : axis == 1 ? v.Y : v.Z;

    public static bool IntersectBox(Ray ray, Vector3 min, Vector3 max, float limit)
    {
        float near = ray.Min, far = Math.Min(ray.Max, limit);
        for (int axis = 0; axis < 3; ++axis)
        {
            float d = Component(ray.Direction, axis), o = Component(ray.Origin, axis);
            float lo = Component(min, axis), hi = Component(max, axis);
            if (Math.Abs(d) < 1e-20f) { if (o < lo || o > hi) return false; continue; }
            float a = (lo - o) / d, b = (hi - o) / d;
            near = Math.Max(near, Math.Min(a, b)); far = Math.Min(far, Math.Max(a, b));
            if (near > far) return false;
        }
        return true;
    }

    public static bool IntersectTriangle(Ray ray, Vector3 a, Vector3 b, Vector3 c, out float distance, out Vector2 barycentric)
    {
        distance = ray.Max; barycentric = default;
        var e1 = b - a; var e2 = c - a; var p = Vector3.Cross(ray.Direction, e2);
        float det = Vector3.Dot(e1, p);
        if (Math.Abs(det) < 1e-9f) return false;
        float inv = 1f / det;
        var s = ray.Origin - a;
        float u = Vector3.Dot(s, p) * inv;
        if (u < 0 || u > 1) return false;
        var q = Vector3.Cross(s, e1);
        float v = Vector3.Dot(ray.Direction, q) * inv;
        if (v < 0 || u + v > 1) return false;
        float t = Vector3.Dot(e2, q) * inv;
        if (t < ray.Min || t > ray.Max || !Budget.Finite(t)) return false;
        distance = t; barycentric = new Vector2(u, v); return true;
    }

    public Hit Trace(Vertex[] vertices, Triangle[] triangles, Ray ray)
    {
        var hit = new Hit { Status = TraceStatus.Miss, Distance = ray.Max };
        if (Nodes.Length == 0) return hit;
        var stack = new int[Budget.StackSize]; int top = 0; stack[top++] = 0;
        while (top != 0)
        {
            int index = stack[--top]; var node = Nodes[index];
            if (!IntersectBox(ray, node.Min, node.Max, hit.Distance)) continue;
            if (!node.IsLeaf)
            {
                if (top + 2 > stack.Length) { hit.Status = TraceStatus.Overflow; return hit; }
                stack[top++] = (int)node.Left + 1; stack[top++] = (int)node.Left; continue;
            }
            for (int i = 0; i < node.Count; ++i)
            {
                int primitive = Order[node.Left + i]; var triangle = triangles[primitive];
                if (IntersectTriangle(ray, vertices[triangle.A].Position, vertices[triangle.B].Position,
                    vertices[triangle.C].Position, out var distance, out var uv) && distance < hit.Distance)
                    hit = new Hit { Status = TraceStatus.Hit, Distance = distance, Barycentric = uv, Triangle = (uint)primitive };
            }
        }
        return hit;
    }
}
