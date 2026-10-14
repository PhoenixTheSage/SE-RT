using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;

namespace SERT.Core;

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct SkinVertex
{
    public Vector3 Position; public float U;
    public Vector3 Normal; public float V;
    public Vector4 Bones, Weights;
    public uint Pose, Padding0, Padding1, Padding2;
}

public sealed class SceneGeometry
{
    public Vertex[] Vertices = Array.Empty<Vertex>();
    public SkinVertex[] Sources = Array.Empty<SkinVertex>();
    public Triangle[] Triangles = Array.Empty<Triangle>();
    public BvhNode[] Nodes = Array.Empty<BvhNode>();
    public Instance[] Instances = Array.Empty<Instance>();
    public Material[] Materials = Array.Empty<Material>();
    public object[][] Textures = Array.Empty<object[]>();
    public object[][] InstanceSources = Array.Empty<object[]>();
    public Matrix4x4[] Poses = Array.Empty<Matrix4x4>();
    public readonly Dictionary<string, int[]> PoseOffsets = new Dictionary<string, int[]>();
    public uint TlasRoot;
    public int[] NodeDepths = Array.Empty<int>();
    public int[] SkinRoots = Array.Empty<int>();
    public ulong Signature;
    public long ByteSize => Sources.LongLength * 80 + Vertices.LongLength * 32 + Triangles.LongLength * 16 +
        Nodes.LongLength * 32 + Instances.LongLength * 176 + Materials.LongLength * 96 + Poses.LongLength * 64;
}

public static class SceneAssembler
{
    public static uint FrameIndex(object[] frame) => (uint)frame[2];
    const long CacheLimit = 128L * 1024 * 1024;
    static readonly object CacheGate = new object();
    static readonly Dictionary<string, Bvh> BlasCache = new Dictionary<string, Bvh>();
    static long cacheBytes;
    static Bvh CachedBlas(string key, Bounds[] bounds)
    {
        lock (CacheGate) if (BlasCache.TryGetValue(key, out var cached)) return cached;
        var built = Bvh.Build(bounds); long bytes = built.Nodes.LongLength * 32 + built.Order.LongLength * 4;
        if (bytes <= CacheLimit) lock (CacheGate)
        {
            if (BlasCache.TryGetValue(key, out var cached)) return cached;
            if (cacheBytes + bytes > CacheLimit) { BlasCache.Clear(); cacheBytes = 0; }
            BlasCache[key] = built; cacheBytes += bytes;
        }
        return built;
    }

    public static ulong Signature(object[] frame)
    {
        ulong hash = 1469598103934665603;
        var meshes = (object[][])frame[4]; var instances = (object[][])frame[5];
        Mix((uint)(int)frame[1]);
        foreach (var mesh in meshes) Mix((uint)(int)mesh[0]);
        foreach (var inst in instances)
        {
            Mix((uint)inst[0]); Mix(unchecked((uint)(int)inst[1]));
            foreach (object[] variant in (object[])inst[3])
            {
                var p = (object[])variant[0]; Mix((uint)(int)p[0]); Mix((uint)(int)p[1]); Mix((uint)(int)p[2]);
                Mix((uint)((float[])variant[3]).Length);
                // Enabling/disabling a material's emission changes catalog membership.
                Mix(((float[])variant[1])[3] > 0 ? 1u : 0u);
                foreach (char c in (string)p[5]) Mix(c);
                Mix(variant.Length > 4 && variant[4] is object[] optical && optical.Length == 7 ? 1u : 0u);
            }
        }
        return hash;
        void Mix(uint v) { hash = (hash ^ v) * 1099511628211; }
    }

    public static SceneGeometry Build(object[] frame)
    {
        var geometry = new SceneGeometry { Signature = Signature(frame) };
        var vertices = new List<Vertex>(); var sources = new List<SkinVertex>(); var triangles = new List<Triangle>();
        var nodes = new List<BvhNode>(); var depths = new List<int>(); var mats = new List<Material>(); var textures = new List<object[]>();
        var instances = new List<Instance>(); var instanceSources = new List<object[]>(); var poses = new List<Matrix4x4>(); var skinRoots = new List<int>();
        var meshes = (object[][])frame[4]; var descriptions = (object[][])frame[5];
        var cache = new Dictionary<string, uint>();
        foreach (var desc in descriptions)
        {
            int meshIndex = (int)desc[1]; var parts = (object[])desc[3]; var box = (float[])desc[4];
            var matrix = Matrix((float[])desc[2]);
            var inst = new Instance { World = matrix, Min = new Vector3(box[0], box[1], box[2]), Max = new Vector3(box[3], box[4], box[5]),
                Identity = (uint)desc[0], Root = uint.MaxValue, MaterialBase = (uint)mats.Count, Revision = meshIndex < 0 ? 0 : (uint)(int)meshes[meshIndex][0] };
            if (!Matrix4x4.Invert(matrix, out inst.Inverse)) meshIndex = -1;
            if (meshIndex >= 0 && parts.Length > 0)
            {
                var mesh = meshes[meshIndex]; var raw = (float[])mesh[1]; var indices = (int[])mesh[2];
                bool skinned = false;
                foreach (object[] part in parts) if (((float[])part[3]).Length != 0) skinned = true;
                string key = meshIndex + ":" + mesh[0] + ":" + PartKey(parts) + (skinned ? ":" + inst.Identity : "");
                foreach (object[] variant in parts)
                {
                    var part = (object[])variant[0]; var color = (float[])variant[1]; string technique = (string)part[5];
                    uint flags = technique.IndexOf("ALPHA", StringComparison.OrdinalIgnoreCase) >= 0 || technique.IndexOf("CUTOUT", StringComparison.OrdinalIgnoreCase) >= 0 ? 1u : 0u;
                    if (technique == "GLASS" || technique == "HOLO" || technique.StartsWith("SHIELD", StringComparison.OrdinalIgnoreCase)) flags |= 2;
                    if ((bool)mesh[4]) flags |= 4;
                    var tint = new Vector3(color[0], color[1], color[2]);
                    if ((flags & 4) != 0 && part.Length > 6) { var scales = (float[])part[6]; tint = new Vector3(scales[0], scales[1], scales[2]); }
                    mats.Add(new Material { Color = tint, Roughness = 0.5f,
                        Emission = new Vector3(color[3]), Texture = (uint)textures.Count, Flags = flags,
                        Revision = inst.Revision, UvTransform = new Vector4(1, 1, 0, 0) });
                    var opticalMaterial = mats[mats.Count - 1];
                    textures.Add(MaterialOptics.Apply(variant, ref opticalMaterial)); mats[mats.Count - 1] = opticalMaterial;
                }
                if (!cache.TryGetValue(key, out inst.Root))
                {
                    int triangleBase = triangles.Count;
                    var poseOffsets = new int[parts.Length];
                    if (raw.Length % 16 != 0) throw new InvalidOperationException("Malformed mesh vertex stride");
                    var vertexMap = new Dictionary<long, uint>();
                    var local = new List<Triangle>();
                    for (int p = 0; p < parts.Length; p++)
                    {
                        var variant = (object[])parts[p]; var part = (object[])variant[0]; var bones = (float[])variant[3];
                        int offset = (int)part[0], count = (int)part[1], basis = (int)part[2]; int partPose = poses.Count;
                        poseOffsets[p] = partPose;
                        if (bones.Length % 16 != 0) throw new InvalidOperationException("Malformed skin palette");
                        for (int b = 0; b + 15 < bones.Length; b += 16) poses.Add(Matrix(bones, b));
                        if (offset < 0 || count < 0 || count % 3 != 0 || (long)offset + count > indices.Length)
                            throw new InvalidOperationException("Malformed mesh index range");
                        for (int t = offset; t + 2 < offset + count; t += 3)
                        {
                            int a = indices[t] + basis, b = indices[t + 1] + basis, c = indices[t + 2] + basis;
                            if (a < 0 || b < 0 || c < 0 || a >= raw.Length / 16 || b >= raw.Length / 16 || c >= raw.Length / 16)
                                throw new InvalidOperationException("Mesh index outside vertex allocation");
                            int palette = bones.Length > 0 ? partPose : -1;
                            local.Add(new Triangle { A = VertexIndex(a, palette), B = VertexIndex(b, palette), C = VertexIndex(c, palette), Material = (uint)p });
                        }
                    }
                    // One source vertex may be shared by parts with different bone remapping.
                    // Duplicate only those assignments; static vertices still share storage.
                    uint VertexIndex(int i, int palette)
                    {
                        long assignment = ((long)palette + 1) << 32 | (uint)i;
                        if (vertexMap.TryGetValue(assignment, out uint existing)) return existing;
                        int r = i * 16;
                        var v = new Vertex { Position = new Vector3(raw[r], raw[r + 1], raw[r + 2]), Normal = new Vector3(raw[r + 3], raw[r + 4], raw[r + 5]), U = raw[r + 6], V = raw[r + 7] };
                        uint index = (uint)vertices.Count;
                        vertices.Add(v);
                        sources.Add(new SkinVertex { Position = v.Position, Normal = v.Normal, U = v.U, V = v.V,
                            Bones = new Vector4(raw[r + 8], raw[r + 9], raw[r + 10], raw[r + 11]),
                            Weights = palette < 0 ? Vector4.Zero : new Vector4(raw[r + 12], raw[r + 13], raw[r + 14], raw[r + 15]),
                            Pose = (uint)Math.Max(0, palette) });
                        vertexMap[assignment] = index; return index;
                    }
                    var bounds = new Bounds[local.Count];
                    for (int i = 0; i < local.Count; i++)
                    { var t = local[i]; bounds[i] = Bounds.Empty.Include(vertices[(int)t.A].Position).Include(vertices[(int)t.B].Position).Include(vertices[(int)t.C].Position); }
                    var bvh = CachedBlas(mesh[0] + ":" + PartKey(parts), bounds);
                    if (bvh.Nodes.Length == 0) inst.Root = uint.MaxValue;
                    else
                    {
                        inst.Root = (uint)nodes.Count;
                        foreach (int i in bvh.Order) triangles.Add(local[i]);
                        AppendNodes(bvh, nodes, depths, triangleBase);
                        if (skinned) skinRoots.Add((int)inst.Root);
                    }
                    cache[key] = inst.Root;
                    if (skinned) geometry.PoseOffsets[inst.Identity + ":" + meshIndex] = poseOffsets;
                }
            }
            instances.Add(inst); instanceSources.Add(desc);
            if ((long)vertices.Count * 112 + (long)triangles.Count * 64 + (long)instances.Count * 256 > Budget.SceneBytes)
                throw new InvalidOperationException("512 MiB scene budget exceeded; retain native lighting");
        }
        var instanceBounds = new Bounds[instances.Count];
        for (int i = 0; i < instances.Count; ++i) instanceBounds[i] = new Bounds(instances[i].Min, instances[i].Max);
        var tlas = Bvh.Build(instanceBounds, 1);
        var ordered = new Instance[instances.Count]; var orderedSources = new object[instances.Count][];
        for (int i = 0; i < tlas.Order.Length; ++i) { ordered[i] = instances[tlas.Order[i]]; orderedSources[i] = instanceSources[tlas.Order[i]]; }
        geometry.TlasRoot = (uint)nodes.Count; AppendNodes(tlas, nodes, depths, 0);
        geometry.Vertices = vertices.ToArray(); geometry.Sources = sources.ToArray(); geometry.Triangles = triangles.ToArray();
        geometry.Nodes = nodes.ToArray(); geometry.NodeDepths = depths.ToArray(); geometry.Instances = ordered;
        geometry.InstanceSources = orderedSources; geometry.Materials = mats.ToArray(); geometry.Textures = textures.ToArray();
        geometry.Poses = poses.ToArray(); geometry.SkinRoots = skinRoots.ToArray();
        if (geometry.ByteSize > Budget.SceneBytes) throw new InvalidOperationException("Scene allocation limit exceeded");
        return geometry;
    }

    // Topology and ordering remain fixed between builds. Only render-thread snapshots are read here.
    public static void Refresh(SceneGeometry geometry, object[] frame)
    {
        if (geometry.Signature != Signature(frame)) throw new InvalidOperationException("Scene topology changed");
        var lookup = new Dictionary<string, object[]>();
        foreach (var d in (object[][])frame[5]) lookup[(uint)d[0] + ":" + d[1]] = d;
        for (int i = 0; i < geometry.Instances.Length; ++i)
        {
            var previous = geometry.InstanceSources[i];
            var d = lookup[(uint)previous[0] + ":" + previous[1]];
            var instance = geometry.Instances[i]; var box = (float[])d[4];
            instance.World = Matrix((float[])d[2]);
            if (!Matrix4x4.Invert(instance.World, out instance.Inverse)) instance.Root = uint.MaxValue;
            instance.Min = new Vector3(box[0], box[1], box[2]); instance.Max = new Vector3(box[3], box[4], box[5]);
            geometry.Instances[i] = instance; geometry.InstanceSources[i] = d;
            var parts = (object[])d[3]; geometry.PoseOffsets.TryGetValue(instance.Identity + ":" + d[1], out var offsets);
            for (int p = 0; p < parts.Length; ++p)
            {
                var variant = (object[])parts[p]; var color = (float[])variant[1];
                int index = checked((int)instance.MaterialBase + p); var material = geometry.Materials[index];
                var rgb = new Vector3(color[0], color[1], color[2]); var emission = new Vector3(color[3]);
                if ((material.Flags & 4) != 0) rgb = material.Color;
                var oldOptical = material.Optical; var oldUv = material.OpticalUv; var oldFlags = material.Flags; var oldOpticalRevision = material.Padding;
                var currentTextures = MaterialOptics.Apply(variant, ref material); var oldTextures = geometry.Textures[index];
                bool textureChanged = currentTextures.Length != oldTextures.Length;
                for (int t = 0; !textureChanged && t < currentTextures.Length; ++t) textureChanged = !ReferenceEquals(currentTextures[t], oldTextures[t]);
                if (material.Color != rgb || material.Emission != emission || textureChanged || material.Optical != oldOptical || material.OpticalUv != oldUv || material.Flags != oldFlags || material.Padding != oldOpticalRevision) material.Revision++;
                material.Color = rgb; material.Emission = emission; geometry.Materials[index] = material;
                geometry.Textures[index] = currentTextures;
                var bones = (float[])variant[3];
                if (offsets != null) for (int b = 0; b + 15 < bones.Length; b += 16)
                    geometry.Poses[offsets[p] + b / 16] = Matrix(bones, b);
            }
        }
    }

    static string PartKey(object[] parts)
    {
        var key = new System.Text.StringBuilder();
        foreach (object[] variant in parts) { var p = (object[])variant[0]; key.Append(p[0]).Append(',').Append(p[1]).Append(',').Append(p[2]).Append(';'); }
        return key.ToString();
    }
    static void AppendNodes(Bvh bvh, List<BvhNode> nodes, List<int> depths, int leafBase)
    {
        int nodeBase = nodes.Count;
        for (int i = 0; i < bvh.Nodes.Length; i++)
        { var node = bvh.Nodes[i]; node.Left += (uint)(node.IsLeaf ? leafBase : nodeBase); nodes.Add(node); depths.Add(0); }
        if (bvh.Nodes.Length == 0) return;
        Visit(0, 0);
        void Visit(int i, int d) { depths[nodeBase + i] = d; var n = bvh.Nodes[i]; if (!n.IsLeaf) { Visit((int)n.Left, d + 1); Visit((int)n.Left + 1, d + 1); } }
    }
    public static Matrix4x4 Matrix(float[] a, int o = 0) => new Matrix4x4(a[o], a[o + 1], a[o + 2], a[o + 3], a[o + 4], a[o + 5], a[o + 6], a[o + 7],
        a[o + 8], a[o + 9], a[o + 10], a[o + 11], a[o + 12], a[o + 13], a[o + 14], a[o + 15]);
}
