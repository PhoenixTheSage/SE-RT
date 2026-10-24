using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using SERT.Core;
using SERT.Integration;
using Vector4 = System.Numerics.Vector4;

namespace SERT.Rendering;

/// <summary>
/// The SoftwareDx11 stack on Anomaly's Vulkan route: the same shaders, resources and pass order, recorded into
/// SE-Vulkan's tenant compute session (RayScene frame[7][1]). DX11's sticky shader-resource, sampler and constant
/// state is kept per stage and copied into each dispatch or draw, including DX11's unbinding of a resource's
/// shader views when it is bound as an output. Renderer images are ids in frame[7]; material and cookie textures
/// are renderer texture ids.
/// With ray query (option C) the same stack traces through a tenant TLAS instead of the software BVH: one BLAS per
/// mesh root over its contiguous triangle range, rebuilt each frame for skinned roots, a unit-box AABB BLAS scaled to
/// each unknown-coverage instance, and a TLAS per frame whose custom index is the instance index. Identify, Trace and
/// Refine compile their SERT_RAY_QUERY variants; every other pass is unchanged.
/// </summary>
public sealed class SoftwareVulkan : IRayTracingBackend, ISoftwareDx11Session
{
    // VkFormat values for the DX11 formats SoftwareDx11 allocates.
    const int Rgba32f = 109, Rgba16f = 97, Rg32ui = 101, R32ui = 98, Rgba32ui = 107, Rgba8ui = 41;
    const uint KindNull = 0, KindImage = 1, KindBuffer = 2, KindUniform = 3, KindRenderer = 4, KindTexture = 5, KindFrame = 6, KindSampler = 7, KindAccel = 8;
    const uint Wrap = 0, Clamp = 1;

    sealed class Buf { public ulong Id; public long Bytes; }
    sealed class Tex { public ulong Id; public int Width, Height, Layers, Bpp; public long Bytes => (long)Width * Height * Layers * Bpp; }
    struct Ref { public uint Kind; public ulong Id; public Ref(uint kind, ulong id) { Kind = kind; Id = id; } public static readonly Ref Null = default; }

    // One frame's sevk_tenant_record batch.
    sealed class Recorder
    {
        readonly List<byte> commands = new List<byte>(), bindings = new List<byte>(), blob = new List<byte>();
        public int Commands => commands.Count / 128;
        public int First => bindings.Count / 32;
        static void W(List<byte> list, uint v) => list.AddRange(BitConverter.GetBytes(v));
        static void W(List<byte> list, ulong v) => list.AddRange(BitConverter.GetBytes(v));
        public void Bind(uint binding, uint kind, ulong id, ulong offset = 0, ulong bytes = 0) { W(bindings, binding); W(bindings, kind); W(bindings, id); W(bindings, offset); W(bindings, bytes); }
        public ulong Data(byte[] data) { ulong at = (ulong)blob.Count; blob.AddRange(data); while (blob.Count % 16 != 0) blob.Add(0); return at; }
        public void Uniform(uint binding, byte[] data) { ulong at = Data(data); Bind(binding, KindUniform, 0, at, (ulong)data.Length); }
        public void Command(uint op, int first, ulong pipeline, uint x, uint y, uint z, ulong[] targets = null, uint[] layers = null, ulong source = 0, ulong offset = 0, ulong blobOffset = 0, ulong bytes = 0,
            uint value = 0, uint value1 = 0, uint flags = 0)
        {
            W(commands, op); W(commands, (uint)first); W(commands, (uint)(op == 1 || op == 2 ? First - first : 0)); W(commands, (uint)(targets?.Length ?? 0));
            W(commands, x); W(commands, y); W(commands, z); W(commands, flags); W(commands, pipeline); W(commands, source);
            for (int i = 0; i < 3; ++i) W(commands, targets != null && i < targets.Length ? targets[i] : 0UL);
            for (int i = 0; i < 3; ++i) W(commands, layers != null && i < layers.Length ? layers[i] : 0u);
            W(commands, 0u); W(commands, offset); W(commands, blobOffset); W(commands, bytes); W(commands, value); W(commands, value1); W(commands, 0u); W(commands, 0u);
        }
        public void Flush(Action<byte[], byte[], byte[]> record)
        {
            if (commands.Count != 0 && record != null) record(commands.ToArray(), bindings.ToArray(), blob.ToArray());
            commands.Clear(); bindings.Clear(); blob.Clear();
        }
    }

    readonly AnomalyHost host;
    readonly Func<string> assets;
    readonly bool collectStatistics, rayQuery;
    // Ray-query scene: outside the shared allocation forecast, released with the scene.
    readonly List<object> rayResources = new List<object>();
    Buf triangleIndices, aabb, instanceBase; ulong tlas, unknownBlas;
    readonly Dictionary<uint, ulong> rootBlas = new Dictionary<uint, ulong>();
    readonly Dictionary<uint, (uint First, uint Count)> rootRanges = new Dictionary<uint, (uint, uint)>();
    readonly HashSet<uint> skinnedRoots = new HashSet<uint>();
    readonly List<object> shaders = new List<object>(), sceneResources = new List<object>(), screenResources = new List<object>();
    readonly Dictionary<string, ulong> compute = new Dictionary<string, ulong>();
    readonly Dictionary<int, Ref> cs = new Dictionary<int, Ref>(), ps = new Dictionary<int, Ref>();
    readonly Recorder recorder = new Recorder();
    IReadOnlyDictionary<string, Delegate> session;
    Func<long, ulong> createBuffer; Func<int, int, int, int, int, ulong> createImage; Func<byte[], string, ulong> createCompute;
    Func<byte[], string, byte[], string, int[], ulong> createRaster; Action<ulong> destroy; Action<byte[], byte[], byte[]> record; Func<bool, ulong> createAccel;
    ulong guides, atlas, composite;
    Task<Dictionary<string, byte[]>> compilation;
    Dictionary<string, byte[]> bytecode;
    string compiledRoot, initializedRoot;
    byte[] parameterBytes = new byte[Marshal.SizeOf<Parameters>()];
    bool csFrame, psFrame;
    Buf vertices, triangles, nodes, instances, materials, sources, poses, skinIndices, lights, tree, stats, vertexErrors;
    Buf[] refitIndices;
    int[] refitCounts;
    int skinCount;
    Tex colorAtlas, extensionAtlas, blueNoise;
    Tex geometryGuide, sourceKey, previousSourceKey, shadowGuide, previousShadowGuide, fast, previousFast, sunFast, previousSunFast, lookup, previousLookup, guideQuality;
    Buf refinementCount, refinementWork, refined, previousRefined;
    int refinementAllocation;
    float allocatedRefinementFraction;
    readonly RefinementBudget refinementBudget = new RefinementBudget();
    Tex position, normal, albedo, identity, previousPosition, previousNormal, previousIdentity;
    Tex noisy, temporal, scratch, filtered, metadata, previousMetadata, moments, previousMoments, sun, sunTemporal, sunScratch, sunFiltered;
    Buf candidate, reused, previousReservoir;
    int width, height, fullWidth, fullHeight, lightCapacity, treeLeaves;
    ulong signature;
    int epoch;
    bool history;
    ShadowSettings shadowSettings = ShadowSettings.Default;
    double[] previousCamera;
    int[] atlasSources;
    int[] atlasOffsets;
    string[] atlasVersions;
    bool[] atlasReady;
    int atlasCursor;
    Light[] lightData;
    ProxyCatalog proxies = new ProxyCatalog();
    int proxyAtlasBase;
    uint[] cookieTiles;
    object[][] cookies = Array.Empty<object[]>();

    public SoftwareVulkan(AnomalyHost host, Func<string> assets, bool collectStatistics = false) : this(host, assets, collectStatistics, false) { }
    public SoftwareVulkan(AnomalyHost host, Func<string> assets, bool collectStatistics, bool rayQuery)
    { this.host = host; this.assets = assets; this.collectStatistics = collectStatistics; this.rayQuery = rayQuery; }

    public bool RayQuery => rayQuery;
    public string Name => rayQuery ? "Vulkan hardware ray query" : "Vulkan software BVH";
    public string Status { get; private set; } = "idle";
    public long SceneBytes { get; private set; }
    public long ReconstructionBytes { get; private set; }
    // The tenant session has no GPU timestamps yet; the adaptive budget holds its current scale.
    public double GpuMilliseconds => double.NaN;
    public bool RefinementExhausted => refinementAllocation == 0 || refinementBudget.Multiplier == 0;
    public void ObserveBudget(double target) { }
    public void ResetBudget() => refinementBudget.Reset();
    public void Poll(object context, Action<double> measured) { }
    public void InvalidateHistory() { history = false; }
    public void SetShadowSettings(ShadowSettings value)
    {
        if (shadowSettings.AllowLightBlockOffsets != value.AllowLightBlockOffsets || shadowSettings.Enabled != value.Enabled) signature = 0;
        if (!shadowSettings.Equals(value)) history = false;
        shadowSettings = value;
    }
    public void FillDiagnostics(RendererDiagnostics data)
    {
        data.Backend = Name; data.Width = width; data.Height = height;
        data.FullWidth = fullWidth; data.FullHeight = fullHeight; data.History = history;
        data.GpuMilliseconds = double.NaN; data.FarSunMilliseconds = double.NaN; data.FarSunCoverage = "native hybrid (Vulkan far-sun product pending)";
        data.RefinementRequested = shadowSettings.RefinementFraction; data.RefinementEffective = width == 0 ? 0 : Math.Min(refinementAllocation, refinementBudget.Capacity(width * height, shadowSettings.RefinementFraction)) / (float)(width * height);
        data.RefinementReason = refinementBudget.Reason;
        data.SceneBytes = SceneBytes; data.ReconstructionBytes = ReconstructionBytes;
        data.LightCapacity = lightData?.Length ?? 0;
        data.AtlasTiles = atlasReady?.Length ?? 0;
        if (atlasReady != null) foreach (bool ready in atlasReady) if (ready) data.ReadyTiles++;
        if (lightData != null)
        {
            int proxyStart = lightData.Length - 1 - ProxyCatalog.Capacity;
            while (data.NativeLights < proxyStart && lightData[data.NativeLights].Kind < 2) data.NativeLights++;
            data.EmissiveLights = Math.Max(0, proxyStart - data.NativeLights);
            for (int i = Math.Max(0, proxyStart); i < lightData.Length - 1; ++i)
            {
                var light = lightData[i];
                if (light.Flags != 0 && light.Radiance.LengthSquared() > 0) data.ActiveProxies++;
            }
        }
    }

    static byte[] Bytes<T>(T[] data) where T : struct
    {
        var bytes = new byte[(long)data.Length * Marshal.SizeOf<T>()];
        var pin = GCHandle.Alloc(data, GCHandleType.Pinned);
        try { Marshal.Copy(pin.AddrOfPinnedObject(), bytes, 0, bytes.Length); } finally { pin.Free(); }
        return bytes;
    }
    Buf Buffer(List<object> owner, int count, int stride) { var b = new Buf { Bytes = checked((long)Math.Max(count, 1) * stride) }; b.Id = createBuffer(b.Bytes); owner.Add(b); return b; }
    Tex Texture(List<object> owner, int w, int h, int format, int bpp, int layers = 1, bool array = false)
    { var t = new Tex { Width = w, Height = h, Layers = layers, Bpp = bpp }; t.Id = createImage(w, h, layers, format, array ? 1 : 0); owner.Add(t); return t; }
    void Upload<T>(Buf buffer, T[] data) where T : struct
    {
        if (data.Length == 0) return;
        var bytes = Bytes(data);
        if (bytes.Length != buffer.Bytes) throw new InvalidOperationException("GPU upload ABI mismatch");
        recorder.Command(7, 0, 0, 0, 0, 0, new[] { buffer.Id }, blobOffset: recorder.Data(bytes), bytes: (ulong)bytes.Length);
    }
    void Clear(Buf buffer) => recorder.Command(3, 0, 0, 0, 0, 0, new[] { buffer.Id });
    void Clear(Tex texture) => recorder.Command(4, 0, 0, 0, 0, 0, new[] { texture.Id });
    void Copy(Tex source, Tex target) => recorder.Command(6, 0, 0, 0, 0, 0, new[] { target.Id }, source: source.Id);
    void Copy(Buf source, Buf target) => recorder.Command(5, 0, 0, 0, 0, 0, new[] { target.Id }, source: source.Id, bytes: (ulong)source.Bytes);
    void Release(List<object> list)
    {
        for (int i = list.Count - 1; i >= 0; --i)
        {
            ulong id = list[i] is Buf b ? b.Id : list[i] is Tex t ? t.Id : (ulong)list[i];
            try { destroy?.Invoke(id); } catch (Exception) { }
        }
        list.Clear();
    }

    static Ref R(Tex t) => t == null ? Ref.Null : new Ref(KindImage, t.Id);
    static Ref R(Buf b) => b == null ? Ref.Null : new Ref(KindBuffer, b.Id);
    // frame[7] renderer ids; 0 is an unpublished image (DX11 null).
    static Ref Renderer(object o) => o is ulong id && id != 0 ? new Ref(KindRenderer, id) : Ref.Null;
    static Ref World(object o) => o is ulong id && id != 0 ? new Ref(KindTexture, id) : Ref.Null;
    static void Set(Dictionary<int, Ref> table, int start, params Ref[] refs) { for (int i = 0; i < refs.Length; ++i) table[start + i] = refs[i]; }
    void Parameters(ref Parameters p) => parameterBytes = Bytes(new[] { p });
    // DX11 unbinds a resource's shader views on a stage when it becomes that stage's output.
    static void Unbind(Dictionary<int, Ref> table, IEnumerable<Ref> outputs)
    {
        foreach (var output in outputs) if (output.Kind != KindNull)
            foreach (var slot in table.Where(e => e.Value.Kind == output.Kind && e.Value.Id == output.Id).Select(e => e.Key).ToList()) table[slot] = Ref.Null;
    }
    void BindTable(Dictionary<int, Ref> table, bool frame)
    {
        foreach (var entry in table) if (entry.Value.Kind != KindNull) recorder.Bind(128u + (uint)entry.Key, entry.Value.Kind, entry.Value.Id);
        if (frame) recorder.Bind(0, KindFrame, 0);
        recorder.Uniform(1, parameterBytes);
    }
    void Dispatch(string name, int x, int y = 1, params Ref[] outputs)
    {
        Unbind(cs, outputs);
        int first = recorder.First; BindTable(cs, csFrame); recorder.Bind(256, KindSampler, Wrap); recorder.Bind(257, KindSampler, Clamp);
        for (int i = 0; i < outputs.Length; ++i) if (outputs[i].Kind != KindNull) recorder.Bind(384u + (uint)i, outputs[i].Kind, outputs[i].Id);
        recorder.Command(1, first, compute[name], (uint)Math.Max(1, x), (uint)Math.Max(1, y), 1);
    }
    void Draw(ulong shader, int w, int h, Tex[] targets, uint[] layers = null, ulong renderer = 0)
    {
        if (targets != null) Unbind(ps, targets.Select(R));
        int first = recorder.First; BindTable(ps, psFrame);
        if (shader == composite) { for (uint i = 0; i < 6; ++i) recorder.Bind(256 + i, KindSampler, i < 3 ? Clamp : Wrap); }
        else { recorder.Bind(256, KindSampler, Wrap); recorder.Bind(257, KindSampler, Clamp); }
        var ids = renderer != 0 ? new[] { renderer } : targets.Select(t => t.Id).ToArray();
        recorder.Command(2, first, shader, (uint)w, (uint)h, 0, ids, layers);
    }
    void ClearState() { cs.Clear(); ps.Clear(); csFrame = psFrame = false; }

    void Bind(object value)
    {
        session = (IReadOnlyDictionary<string, Delegate>)value;
        if (!(session.TryGetValue("ApiVersion", out var version) && version is Func<int> v && v() == 1)) throw new InvalidOperationException("SE-Vulkan tenant session v1 required");
        createBuffer = (Func<long, ulong>)session["CreateBuffer"]; createImage = (Func<int, int, int, int, int, ulong>)session["CreateImage"];
        createCompute = (Func<byte[], string, ulong>)session["CreateCompute"]; createRaster = (Func<byte[], string, byte[], string, int[], ulong>)session["CreateRaster"];
        destroy = (Action<ulong>)session["Destroy"]; record = (Action<byte[], byte[], byte[]>)session["Record"];
        if (rayQuery)
        {
            if (!(session.TryGetValue("RayQuery", out var query) && query is Func<bool> q && q() && session.TryGetValue("CreateAccel", out var accel)))
                throw new InvalidOperationException("SE-Vulkan tenant session lacks ray query");
            createAccel = (Func<bool, ulong>)accel;
        }
    }
    ulong Accel(bool top) { ulong id = createAccel(top); rayResources.Add(id); return id; }
    Buf RayBuffer(int count, int stride) => Buffer(rayResources, count, stride);
    void BuildBlas(uint root) { var r = rootRanges[root]; recorder.Command(9, 0, 0, r.Count, 0, 0, new[] { rootBlas[root], triangleIndices.Id }, source: vertices.Id, offset: (ulong)r.First * 12, value: 32, value1: (uint)Math.Max(1, vertexCount)); }
    int vertexCount;
    // Row-vector camera-relative world (p * World) as VkTransformMatrixKHR rows.
    static float[] Rows(Matrix4x4 m) => new[] { m.M11, m.M21, m.M31, m.M41, m.M12, m.M22, m.M32, m.M42, m.M13, m.M23, m.M33, m.M43 };
    void BuildRayScene(SceneGeometry scene)
    {
        Release(rayResources); rootBlas.Clear(); rootRanges.Clear(); skinnedRoots.Clear(); vertexCount = scene.Vertices.Length;
        var indices = new uint[scene.Triangles.Length * 3];
        for (int i = 0; i < scene.Triangles.Length; ++i) { indices[i * 3] = scene.Triangles[i].A; indices[i * 3 + 1] = scene.Triangles[i].B; indices[i * 3 + 2] = scene.Triangles[i].C; }
        triangleIndices = RayBuffer(indices.Length, 4); Upload(triangleIndices, indices);
        aabb = RayBuffer(6, 4); Upload(aabb, new[] { 0f, 0, 0, 1, 1, 1 });
        unknownBlas = Accel(false); recorder.Command(9, 0, 0, 1, 0, 0, new[] { unknownBlas }, source: aabb.Id, flags: 1);
        foreach (int root in scene.SkinRoots) skinnedRoots.Add((uint)root);
        var bases = new uint[Math.Max(1, scene.Instances.Length)];
        for (int k = 0; k < scene.Instances.Length; ++k)
        {
            uint root = scene.Instances[k].Root; if (root == uint.MaxValue) continue;
            if (!rootRanges.TryGetValue(root, out var range))
            {
                // A mesh root owns one contiguous triangle range: the span of its subtree's leaves.
                uint first = uint.MaxValue, last = 0; var stack = new Stack<uint>(); stack.Push(root);
                while (stack.Count > 0) { var n = scene.Nodes[stack.Pop()]; if (n.IsLeaf) { first = Math.Min(first, n.Left); last = Math.Max(last, n.Left + n.Count); } else { stack.Push(n.Left); stack.Push(n.Left + 1); } }
                range = (first, last - first); rootRanges[root] = range; rootBlas[root] = Accel(false); BuildBlas(root);
            }
            bases[k] = range.First;
        }
        instanceBase = RayBuffer(bases.Length, 4); Upload(instanceBase, bases);
        tlas = Accel(true);
    }
    void BuildTlas(SceneGeometry scene)
    {
        var records = new byte[scene.Instances.Length * 80];
        for (int k = 0; k < scene.Instances.Length; ++k)
        {
            var inst = scene.Instances[k]; float[] rows; ulong blas;
            if (inst.Root != uint.MaxValue) { rows = Rows(inst.World); blas = rootBlas[inst.Root]; }
            else
            {
                var size = Vector3.Max(inst.Max - inst.Min, new Vector3(1e-4f));
                rows = new[] { size.X, 0, 0, inst.Min.X, 0, size.Y, 0, inst.Min.Y, 0, 0, size.Z, inst.Min.Z }; blas = unknownBlas;
            }
            int at = k * 80; System.Buffer.BlockCopy(rows, 0, records, at, 48);
            BitConverter.GetBytes((uint)k).CopyTo(records, at + 48); BitConverter.GetBytes(0xFFu).CopyTo(records, at + 52); BitConverter.GetBytes(1u).CopyTo(records, at + 56);
            BitConverter.GetBytes(blas).CopyTo(records, at + 64);
        }
        recorder.Command(10, 0, 0, (uint)scene.Instances.Length, 0, 0, new[] { tlas }, blobOffset: recorder.Data(records), bytes: (ulong)records.Length);
    }

    void Initialize()
    {
        initializedRoot = compiledRoot;
        ulong Raster(string name, params int[] formats) { ulong id = createRaster(bytecode["Fullscreen"], "__vertex_shader", bytecode[name], "__pixel_shader", formats); shaders.Add(id); return id; }
        guides = Raster("Guides", Rgba32f, Rgba16f, Rgba16f); atlas = Raster("Atlas", Rgba16f, Rgba16f); composite = Raster("Composite", 0);
        foreach (string name in new[] { "Skin", "Refit", "Identify", "LightWeights", "Candidates", "Reuse", "Trace", "Temporal", "Atrous", "Classify", "Refine" })
        { ulong id = createCompute(bytecode[name], "__compute_shader"); shaders.Add(id); compute[name] = id; }
        byte[] noise = File.ReadAllBytes(Path.Combine(initializedRoot, "Sampling", "blue-noise-16x16x8.rgba"));
        if (noise.Length != 8192) throw new InvalidDataException("Invalid SE-RT sampling asset");
        blueNoise = Texture(shaders, 16, 16, Rgba8ui, 4, 8, true);
        for (int i = 0; i < 8; i++)
        {
            var slice = new byte[1024]; Array.Copy(noise, i * 1024, slice, 0, 1024);
            recorder.Command(8, 0, 0, 0, 0, 0, new[] { blueNoise.Id }, new[] { (uint)i }, blobOffset: recorder.Data(slice), bytes: 1024);
        }
    }

    void BuildScene(SceneGeometry scene, object[] frame)
    {
        Release(sceneResources); history = false; signature = scene.Signature; epoch = (int)frame[1];
        cookies = frame.Length > 13 ? (object[][])frame[13] : Array.Empty<object[]>();
        var textureMap = new Dictionary<string, uint>(); var sourceList = new List<int>(); var offsetList = new List<int>();
        for (int i = 0; i < scene.Materials.Length; ++i)
        {
            var material = scene.Materials[i]; int groups = (material.Flags & 4) != 0 ? 6 : 1; var tiles = new uint[groups];
            for (int g = 0; g < groups; ++g)
            {
                string key = TextureKey(TextureGroup(scene.Textures[i], g * 4)) + ((material.Flags & 32) != 0 ? ":optics:" + material.Padding : "");
                if (!textureMap.TryGetValue(key, out uint tile)) { tile = (uint)sourceList.Count; textureMap[key] = tile; sourceList.Add(i); offsetList.Add(g * 4); }
                tiles[g] = tile;
            }
            material.Texture = tiles[0];
            if (groups == 6) { material.UvTransform = new Vector4(tiles[1], tiles[2], tiles[3], tiles[4]); material.Padding = tiles[5]; }
            scene.Materials[i] = material;
        }
        cookieTiles = new uint[((float[])frame[6]).Length / 16]; for (int i = 0; i < cookieTiles.Length; ++i) cookieTiles[i] = uint.MaxValue;
        for (int i = 0; i < cookies.Length; ++i) if (cookies[i][0] != null)
        { cookieTiles[i] = (uint)sourceList.Count; sourceList.Add(-2); offsetList.Add(i); }
        proxyAtlasBase = sourceList.Count;
        for (int i = 0; i < ProxyCatalog.Capacity; ++i) { sourceList.Add(-1); offsetList.Add(i); }
        atlasSources = sourceList.ToArray(); atlasOffsets = offsetList.ToArray(); atlasVersions = new string[atlasSources.Length]; atlasReady = new bool[atlasSources.Length]; atlasCursor = 0;
        lightData = LightCatalog.Build(scene, ShadowSettings.NativeLights(frame, shadowSettings.AllowLightBlockOffsets), epoch);
        var activeNodes = new HashSet<int>();
        for (int i = (int)scene.TlasRoot; i < scene.Nodes.Length; ++i) activeNodes.Add(i);
        foreach (int root in scene.SkinRoots) Visit(root);
        void Visit(int i) { if (!activeNodes.Add(i)) return; var n = scene.Nodes[i]; if (!n.IsLeaf) { Visit((int)n.Left); Visit((int)n.Left + 1); } }
        var skinned = scene.Sources.Select((s, i) => new { Source = s, Index = i }).Where(x => x.Source.Weights.LengthSquared() > 0).Select(x => (uint)x.Index).ToArray();
        long predicted = SoftwareForecast.Scene(scene, atlasSources.Length, lightData.Length, activeNodes.Count, skinned.Length, shadowSettings.Enabled);
        if (predicted > Budget.SceneBytes) throw new InvalidOperationException("Scene, source atlas and shared far-sun allocation forecast exceed 512 MiB");
        vertices = Buffer(sceneResources, scene.Vertices.Length, 32); Upload(vertices, scene.Vertices);
        triangles = Buffer(sceneResources, scene.Triangles.Length, 16); Upload(triangles, scene.Triangles);
        nodes = Buffer(sceneResources, scene.Nodes.Length, 32); Upload(nodes, scene.Nodes);
        instances = Buffer(sceneResources, scene.Instances.Length, 176); Upload(instances, scene.Instances);
        materials = Buffer(sceneResources, scene.Materials.Length, 96);
        vertexErrors = Buffer(sceneResources, scene.Vertices.Length, 4); Clear(vertexErrors);
        sources = Buffer(sceneResources, scene.Sources.Length, 80); Upload(sources, scene.Sources);
        poses = Buffer(sceneResources, scene.Poses.Length, 64);
        int maxDepth = scene.NodeDepths.Max(); refitIndices = new Buf[maxDepth + 1]; refitCounts = new int[maxDepth + 1];
        for (int d = 0; d <= maxDepth; ++d)
        {
            var indices = activeNodes.Where(i => scene.NodeDepths[i] == d).Select(i => (uint)i).ToArray(); refitCounts[d] = indices.Length;
            if (indices.Length > 0) { refitIndices[d] = Buffer(sceneResources, indices.Length, 4); Upload(refitIndices[d], indices); }
        }
        skinCount = skinned.Length; skinIndices = Buffer(sceneResources, skinCount, 4); Upload(skinIndices, skinned);
        int tileCount = Math.Max(atlasSources.Length, 1);
        if (tileCount > 2048) throw new InvalidOperationException("Texture-array material capacity exceeded");
        colorAtlas = Texture(sceneResources, 64, 64, Rgba16f, 8, tileCount, true);
        extensionAtlas = Texture(sceneResources, 64, 64, Rgba16f, 8, tileCount, true);
        lightCapacity = lightData.Length; treeLeaves = 1; while (treeLeaves < lightCapacity) treeLeaves = checked(treeLeaves * 2);
        lights = Buffer(sceneResources, lightCapacity, 80); Upload(lights, lightData);
        tree = Buffer(sceneResources, treeLeaves * 2, 4); stats = Buffer(sceneResources, 8, 4);
        SceneBytes = sceneResources.Sum(x => x is Tex t ? t.Bytes : ((Buf)x).Bytes) + blueNoise.Bytes + Marshal.SizeOf<Parameters>() + (shadowSettings.Enabled ? 2048L * 2048 * 4 + 80 : 0);
        if (SceneBytes != predicted) throw new InvalidOperationException("Scene allocation disagrees with its forecast");
        if (rayQuery) BuildRayScene(scene);
    }
    // Renderer texture ids identify a texture as DX11's view pointers do.
    static string TextureKey(object[] textures) => string.Join(":", textures.Select(x => x is ulong id ? id.ToString() : "0"));
    string AtlasVersion(SceneGeometry scene, int tile, object[] textures) => TextureKey(textures) + (atlasSources[tile] >= 0 && (scene.Materials[atlasSources[tile]].Flags & 32) != 0 ? ":optics:" + scene.Materials[atlasSources[tile]].Padding : "");
    static object[] TextureGroup(object[] textures, int offset) { var result = new object[4]; if (offset + 4 <= textures.Length) Array.Copy(textures, offset, result, 0, 4); return result; }
    object[] AtlasSource(SceneGeometry scene, int tile) => atlasSources[tile] == -2 ? new object[] { cookies[atlasOffsets[tile]][0], null, null, null } :
        atlasSources[tile] == -1 ? new object[] { proxies.Textures[atlasOffsets[tile]], null, null, null } : TextureGroup(scene.Textures[atlasSources[tile]], atlasOffsets[tile]);
    static uint PackNormal(float[] value)
    {
        var n = System.Numerics.Vector3.Normalize(new System.Numerics.Vector3(value[0], value[1], value[2]));
        n /= Math.Abs(n.X) + Math.Abs(n.Y) + Math.Abs(n.Z);
        float x = n.X, y = n.Y; if (n.Z < 0) { x = (1 - Math.Abs(n.Y)) * (n.X >= 0 ? 1 : -1); y = (1 - Math.Abs(n.X)) * (n.Y >= 0 ? 1 : -1); }
        return (uint)Math.Round((x * .5 + .5) * 65535) | ((uint)Math.Round((y * .5 + .5) * 65535) << 16);
    }
    static uint Tile(Material m, int group) => group == 0 ? m.Texture : group == 1 ? (uint)m.UvTransform.X : group == 2 ? (uint)m.UvTransform.Y :
        group == 3 ? (uint)m.UvTransform.Z : group == 4 ? (uint)m.UvTransform.W : m.Padding;

    void BuildScreen(int fw, int fh, float scale)
    {
        Release(screenResources); history = false; fullWidth = fw; fullHeight = fh;
        width = Math.Max(1, (int)Math.Ceiling(fw * scale)); height = Math.Max(1, (int)Math.Ceiling(fh * scale));
        int pixels = checked(width * height); refinementAllocation = SoftwareForecast.PoolCapacity(pixels, shadowSettings.RefinementFraction);
        allocatedRefinementFraction = shadowSettings.RefinementFraction;
        if (SoftwareForecast.Screen(pixels, refinementAllocation) > Budget.ReconstructionBytes) throw new InvalidOperationException("256 MiB reconstruction budget exceeded before allocation");
        Tex T(int f, int bytes, int layers = 1) => Texture(screenResources, width, height, f, bytes, layers);
        position = T(Rgba32f, 16); previousPosition = T(Rgba32f, 16);
        normal = T(Rgba16f, 8); previousNormal = T(Rgba16f, 8);
        albedo = T(Rgba16f, 8);
        identity = T(Rg32ui, 8); previousIdentity = T(Rg32ui, 8);
        noisy = T(Rgba16f, 8, 4); temporal = T(Rgba16f, 8, 4);
        scratch = T(Rgba16f, 8, 4); filtered = T(Rgba16f, 8, 4);
        metadata = T(Rgba32f, 16); previousMetadata = T(Rgba32f, 16);
        moments = T(Rgba32f, 16); previousMoments = T(Rgba32f, 16);
        sun = T(Rgba16f, 8); sunTemporal = T(Rgba16f, 8); sunScratch = T(Rgba16f, 8); sunFiltered = T(Rgba16f, 8);
        geometryGuide = T(Rgba32f, 16); guideQuality = T(R32ui, 4);
        sourceKey = T(Rgba32ui, 16); previousSourceKey = T(Rgba32ui, 16);
        shadowGuide = T(Rgba16f, 8); previousShadowGuide = T(Rgba16f, 8);
        fast = T(Rgba16f, 8); previousFast = T(Rgba16f, 8);
        sunFast = T(Rgba16f, 8); previousSunFast = T(Rgba16f, 8);
        lookup = T(R32ui, 4); previousLookup = T(R32ui, 4);
        refinementCount = Buffer(screenResources, 1, 4);
        refinementWork = Buffer(screenResources, Math.Max(1, refinementAllocation), 8);
        refined = Buffer(screenResources, Math.Max(1, refinementAllocation), Marshal.SizeOf<RefinementSample>()); previousRefined = Buffer(screenResources, Math.Max(1, refinementAllocation), Marshal.SizeOf<RefinementSample>());
        candidate = Buffer(screenResources, width * height, 32); reused = Buffer(screenResources, width * height, 32); previousReservoir = Buffer(screenResources, width * height, 32);
        ReconstructionBytes = screenResources.Sum(x => x is Tex t ? t.Bytes : ((Buf)x).Bytes);
        if (ReconstructionBytes != SoftwareForecast.Screen(pixels, refinementAllocation)) throw new InvalidOperationException("Reconstruction allocation disagrees with its forecast");
        if (ReconstructionBytes > Budget.ReconstructionBytes) throw new InvalidOperationException("256 MiB reconstruction budget exceeded; reduce sampling scale");
    }

    void BindScene()
    {
        Set(cs, 16, R(vertices), R(triangles), R(nodes), R(instances), R(materials), R(lights), R(colorAtlas), R(extensionAtlas));
        Set(cs, 24, R(position), R(normal), R(albedo), R(identity));
        Set(cs, 30, R(previousPosition), R(metadata), R(moments), R(tree), R(sun));
        Set(cs, 36, R(previousNormal), R(previousIdentity), R(filtered));
        Set(cs, 49, R(geometryGuide), R(blueNoise), R(sourceKey), R(previousSourceKey), R(shadowGuide), R(previousShadowGuide), R(fast), R(previousFast), R(sunFast), R(previousSunFast));
        Set(cs, 66, R(guideQuality)); Set(cs, 67, R(vertexErrors));
        if (rayQuery) { Set(cs, 68, new Ref(KindAccel, tlas)); Set(cs, 69, R(instanceBase)); }
    }

    public bool Render(object[] frame, SceneGeometry scene, float scale, float exposure)
    {
        // A failed frame records nothing, so the Reset that follows can retire every id at once.
        bool rendered;
        try { rendered = RenderFrame(frame, scene, scale, exposure); }
        catch { recorder.Flush(null); ClearState(); throw; }
        if (record != null) recorder.Flush(record);
        ClearState();
        return rendered;
    }

    bool RenderFrame(object[] frame, SceneGeometry scene, float scale, float exposure)
    {
        var gpu = (object[])frame[7]; var size = (int[])frame[8];
        if (gpu.Length < 13 || !(gpu[1] is IReadOnlyDictionary<string, Delegate>) || scene.Instances.Length == 0 || scene.Nodes.Length == 0 || gpu[9] == null || gpu[10] == null || gpu[11] == null || gpu[12] == null)
        { Status = "native fallback: missing scene, tenant session, sun coverage, or environment probe"; history = false; return false; }
        string root = assets();
        if (compiledRoot != root)
        {
            compiledRoot = root; bytecode = null;
            compilation = Task.Run(() =>
            {
                var result = new Dictionary<string, byte[]>();
                foreach (string name in new[] { "Fullscreen", "Guides", "Atlas", "Composite", "Skin", "Refit", "Identify", "LightWeights", "Candidates", "Reuse", "Trace", "Temporal", "Atrous", "Classify", "Refine" })
                {
                    bool traced = rayQuery && (name == "Identify" || name == "Trace" || name == "Refine");
                    result[name] = host.Compile(Path.Combine(root, "Shaders", name + (traced ? "RayQuery" : "") + ".hlsl"), name == "Fullscreen" ? 0 : name == "Guides" || name == "Atlas" || name == "Composite" ? 1 : traced ? 4 : 3);
                }
                return result;
            });
        }
        if (bytecode == null)
        {
            if (!compilation.IsCompleted) { Status = "native fallback: compiling shader cache on worker"; return false; }
            bytecode = compilation.GetAwaiter().GetResult();
        }
        // A new session is a new renderer: its ids replace the old renderer's, which are gone with it.
        if (!ReferenceEquals(session, gpu[1])) { Forget(); Bind(gpu[1]); Initialize(); }
        SceneAssembler.Refresh(scene, frame);
        if (SoftwareForecast.Screen(checked((int)Math.Ceiling(size[0] * scale) * (int)Math.Ceiling(size[1] * scale)), 0) > Budget.ReconstructionBytes) scale = .25f;
        // Tenant ids retire when the GPU is done with them, so replacement needs no retirement wait.
        if (initializedRoot != compiledRoot) { Release(screenResources); Release(sceneResources); Release(rayResources); Release(shaders); compute.Clear(); signature = 0; width = 0; Initialize(); }
        if (signature != scene.Signature || epoch != (int)frame[1]) BuildScene(scene, frame);
        if (allocatedRefinementFraction != shadowSettings.RefinementFraction || width == 0 || fullWidth != size[0] || fullHeight != size[1] || width != Math.Max(1, (int)Math.Ceiling(size[0] * scale))) BuildScreen(size[0], size[1], scale);
        cookies = frame.Length > 13 ? (object[][])frame[13] : Array.Empty<object[]>();
        if (cookies.Length != 0 && cookies.Length != cookieTiles.Length) { signature = 0; history = false; Status = "native fallback: spotlight layout changed"; return false; }
        for (int i = 0; i < cookies.Length; ++i) if (cookies[i][0] != null && cookieTiles[i] == uint.MaxValue)
        { signature = 0; history = false; Status = "native fallback: spotlight cookie became available"; return false; }
        var owners = new HashSet<uint>(); foreach (var light in lightData) if (light.Kind < 2) owners.Add(light.Identity);
        proxies.Refresh(frame.Length > 12 ? (object[][])frame[12] : Array.Empty<object[]>(), owners);
        Upload(instances, scene.Instances); Upload(poses, scene.Poses);
        for (int i = 0; i < scene.Materials.Length; ++i)
            for (int g = 0; g < ((scene.Materials[i].Flags & 4) != 0 ? 6 : 1); ++g)
            {
                uint tile = Tile(scene.Materials[i], g);
                if (TextureKey(TextureGroup(scene.Textures[i], g * 4)) != TextureKey(TextureGroup(scene.Textures[atlasSources[tile]], atlasOffsets[tile])))
                { signature = 0; history = false; Status = "native fallback: material texture layout changed"; return false; }
            }
        int baked = 0, startTile = atlasCursor;
        for (int k = 0; k < atlasSources.Length && baked < 32; ++k)
        {
            int tile = (startTile + k) % atlasSources.Length; var textures = AtlasSource(scene, tile);
            if (atlasSources[tile] == -1 && proxies.Lights[atlasOffsets[tile]].Radiance.LengthSquared() <= 0) continue;
            string version = AtlasVersion(scene, tile, textures);
            // Renderer texture ids do not say whether a texture is rendered each frame (DX11 rebakes those); they bake once per version.
            if (atlasReady[tile] && atlasVersions[tile] == version) continue;
            var presence = new Parameters { FullWidth = textures[0] == null ? 0u : 1u, FullHeight = textures[1] == null ? 0u : 1u,
                Width = textures[2] == null ? 0u : 1u, Height = textures[3] == null ? 0u : 1u };
            Parameters(ref presence);
            Set(ps, 0, textures.Select(World).ToArray());
            Draw(atlas, 64, 64, new[] { colorAtlas, extensionAtlas }, new[] { (uint)tile, (uint)tile });
            atlasReady[tile] = true; atlasVersions[tile] = version; baked++; atlasCursor = (tile + 1) % atlasSources.Length;
        }
        int awaiting = 0;
        for (int tile = 0; tile < atlasSources.Length; ++tile)
        {
            if (atlasSources[tile] == -1 && (proxies.Lights[atlasOffsets[tile]].Flags == 0 || proxies.Lights[atlasOffsets[tile]].Radiance.LengthSquared() <= 0)) continue;
            if (!atlasReady[tile] || atlasVersions[tile] != AtlasVersion(scene, tile, AtlasSource(scene, tile))) awaiting++;
        }
        if (awaiting > 0) { history = false; Status = "native fallback: " + awaiting + " source texture tiles awaiting bake"; return false; }
        for (int i = 0; i < scene.Materials.Length; ++i)
        {
            var textures = scene.Textures[i]; var m = scene.Materials[i];
            bool ready = true;
            for (int g = 0; g < ((m.Flags & 4) != 0 ? 6 : 1); ++g)
            { var group = TextureGroup(textures, g * 4); uint tile = Tile(m, g); ready &= atlasReady[tile] && atlasVersions[tile] == AtlasVersion(scene, (int)tile, group) && group[0] != null && ((m.Flags & 32) != 0 || (group[1] != null && group[2] != null)); }
            if (!ready) m.Flags |= 8; else m.Flags &= ~8u;
            if ((m.Flags & 1) != 0 && (textures[3] == null || !ready)) m.Flags |= 16; else m.Flags &= ~16u;
            scene.Materials[i] = m;
        }
        Upload(materials, scene.Materials);
        if (!LightCatalog.Refresh(lightData, ShadowSettings.NativeLights(frame, shadowSettings.AllowLightBlockOffsets), scene, epoch))
        { signature = 0; history = false; Status = "native fallback: light catalog changed; rebuild next frame"; return false; }
        int proxyBase = lightData.Length - 1 - ProxyCatalog.Capacity;
        for (int i = 0; i < cookies.Length; ++i)
        {
            var l = lightData[i];
            if (cookieTiles[i] != uint.MaxValue && cookies[i][0] != null)
            { l.Flags |= 1; l.Padding = cookieTiles[i]; l.Triangle = PackNormal((float[])cookies[i][1]);
                if (!atlasReady[l.Padding] || atlasVersions[l.Padding] != TextureKey(AtlasSource(scene, (int)l.Padding))) l.Radiance = System.Numerics.Vector3.Zero; }
            else l.Flags &= ~1u;
            lightData[i] = l;
        }
        for (int i = 0; i < ProxyCatalog.Capacity; ++i)
        { var l = proxies.Lights[i]; l.Texture = (uint)(proxyAtlasBase + i);
            if (l.Flags != 0 && (!atlasReady[l.Texture] || atlasVersions[l.Texture] != TextureKey(AtlasSource(scene, (int)l.Texture)))) l.Radiance = System.Numerics.Vector3.Zero;
            lightData[proxyBase + i] = l; }
        Upload(lights, lightData);
        var sunData = (float[])frame[9]; var camera = (double[])frame[3];
        // The Vulkan far-sun product is not published yet; local softness stays native hybrid as DX11 without it.
        bool farValid = false;
        var p = new Parameters { FullWidth = (uint)size[0], FullHeight = (uint)size[1], Width = (uint)width, Height = (uint)height,
            Vertices = (uint)scene.Vertices.Length, Triangles = (uint)scene.Triangles.Length, Nodes = (uint)scene.Nodes.Length, Instances = (uint)scene.Instances.Length,
            Materials = (uint)scene.Materials.Length, Lights = (uint)lightCapacity, Tlas = scene.TlasRoot, Frame = SceneAssembler.FrameIndex(frame),
            SunDirection = new Vector4(sunData[0], sunData[1], sunData[2], sunData[3]),
            SunColor = new Vector4(sunData[4] * sunData[3], sunData[5] * sunData[3], sunData[6] * sunData[3], sunData[11]),
            Sky = new Vector4(sunData[8], sunData[9], sunData[10], ((float[])frame[6]).Length / 16), PreviousViewProjection = SceneAssembler.Matrix((float[])frame[11]),
            CameraDelta = previousCamera == null ? Vector4.Zero : new Vector4((float)(camera[0] - previousCamera[0]), (float)(camera[1] - previousCamera[1]), (float)(camera[2] - previousCamera[2]), 0),
            History = history && sunData[7] > 0 && Renderer(gpu[7]).Kind != KindNull ? 1u : 0u, TreeLeaves = (uint)treeLeaves, Poses = (uint)scene.Poses.Length,
            Exposure = exposure, Primary = Budget.PrimaryDistance, Diffuse = Budget.DiffuseDistance, Specular = Budget.SpecularDistance,
            CollectStatistics = collectStatistics ? 1u : 0u, ShadowFlags = shadowSettings.Enabled ? 0u : 1u,
            ShadowBias = shadowSettings.Bias, ShadowDebugView = (uint)shadowSettings.DebugView,
            LampRadius = shadowSettings.LampRadius, SolarRadius = shadowSettings.Extent.SolarRadius, GlassTransmission = shadowSettings.GlassTransmission,
            RefinementFraction = shadowSettings.RefinementFraction * refinementBudget.Multiplier, BiasMode = (uint)shadowSettings.BiasMode,
            SamplingMode = (uint)shadowSettings.Sampling, SamplingSeed = shadowSettings.SamplingSeed, FarSunValid = farValid ? 1u : 0u,
            FarSunMatrix = Matrix4x4.Identity,
            RefinementCapacity = (uint)Math.Min(refinementAllocation, refinementBudget.Capacity(width * height, shadowSettings.RefinementFraction)), RefinementPixels = (uint)refinementAllocation };
        Parameters(ref p);
        psFrame = true;
        Set(ps, 0, Renderer(gpu[3]), Renderer(gpu[4]), Renderer(gpu[5]), Renderer(gpu[6]));
        Draw(guides, width, height, new[] { position, normal, albedo });
        BindScene(); csFrame = true; Set(cs, 59, Ref.Null); Set(cs, 40, R(sources), R(poses), Ref.Null, Renderer(gpu[10]), Renderer(gpu[9]));
        if (skinCount > 0) { p.Pass = (uint)skinCount; Parameters(ref p); Set(cs, 48, R(skinIndices)); Set(cs, 67, Ref.Null); Set(cs, 16, Ref.Null); Dispatch("Skin", (skinCount + 63) / 64, 1, R(vertices), R(vertexErrors)); Set(cs, 16, R(vertices)); Set(cs, 67, R(vertexErrors)); }
        if (rayQuery) { foreach (uint skinned in skinnedRoots) if (rootBlas.ContainsKey(skinned)) BuildBlas(skinned); BuildTlas(scene); }
        else
        {
            Set(cs, 18, Ref.Null);
            for (int d = refitCounts.Length - 1; d >= 0; --d) if (refitCounts[d] > 0)
            { p.Pass = (uint)refitCounts[d]; Parameters(ref p); Set(cs, 42, R(refitIndices[d])); Dispatch("Refit", (refitCounts[d] + 63) / 64, 1, R(nodes)); }
            Set(cs, 18, R(nodes));
        }
        int gx = (width + 7) / 8, gy = (height + 7) / 8;
        Set(cs, 27, Ref.Null); Set(cs, 49, Ref.Null); Set(cs, 24, Ref.Null);
        Dispatch("Identify", gx, gy, R(identity), R(geometryGuide), R(position));
        Set(cs, 27, R(identity)); Set(cs, 49, R(geometryGuide)); Set(cs, 24, R(position));
        p.Pass = 0; Parameters(ref p); Set(cs, 33, Ref.Null); Dispatch("LightWeights", (treeLeaves + 63) / 64, 1, R(tree));
        for (int level = treeLeaves / 2; level > 0; level /= 2) { p.Pass = (uint)level; Parameters(ref p); Dispatch("LightWeights", (level + 63) / 64, 1, R(tree)); }
        Set(cs, 33, R(tree)); Set(cs, 35, Renderer(gpu[7]));
        Dispatch("Candidates", gx, gy, R(candidate)); Set(cs, 28, R(candidate)); Set(cs, 39, R(previousReservoir));
        Dispatch("Reuse", gx, gy, R(reused)); Set(cs, 28, R(reused));
        Clear(stats); Set(cs, 31, Ref.Null); Set(cs, 34, Ref.Null);
        Set(cs, 51, Ref.Null); Set(cs, 53, Ref.Null); Set(cs, 66, Ref.Null);
        Dispatch("Trace", gx, gy, R(noisy), R(sun), R(metadata), R(stats), R(sourceKey), R(shadowGuide), R(guideQuality));
        Set(cs, 51, R(sourceKey)); Set(cs, 53, R(shadowGuide)); Set(cs, 66, R(guideQuality));
        Set(cs, 29, R(noisy)); Set(cs, 31, R(metadata)); Set(cs, 34, R(sun));
        Set(cs, 32, R(previousMoments)); Set(cs, 45, R(sunFiltered));
        Set(cs, 47, R(previousMetadata));
        Set(cs, 55, Ref.Null); Set(cs, 57, Ref.Null);
        Dispatch("Temporal", gx, gy, R(temporal), R(moments), R(sunTemporal), Ref.Null, R(fast), R(sunFast)); Set(cs, 32, R(moments));
        Tex input = temporal, sunInput = sunTemporal;
        for (uint iteration = 0; iteration < 3; ++iteration)
        {
            var output = iteration == 1 ? temporal : scratch; var sunOutput = iteration == 1 ? sunTemporal : sunScratch;
            p.Pass = iteration; Parameters(ref p); Set(cs, 29, R(input)); Set(cs, 34, R(sunInput));
            Dispatch("Atrous", gx, gy, R(output), R(sunOutput)); input = output; sunInput = sunOutput;
        }
        Clear(lookup);
        Clear(refinementCount);
        if (p.RefinementCapacity > 0)
        {
            Set(cs, 0, Renderer(gpu[3]), Renderer(gpu[4]), Renderer(gpu[5]), Renderer(gpu[6]));
            p.QualityPadding0 = 0; Parameters(ref p); Dispatch("Classify", gx, gy, R(refinementCount), R(refinementWork), R(lookup));
            p.QualityPadding0 = 1; Parameters(ref p); Dispatch("Classify", gx, gy, R(refinementCount), R(refinementWork), R(lookup));
            Set(cs, 60, R(refinementCount), R(refinementWork)); Set(cs, 64, R(previousRefined)); Set(cs, 65, R(previousLookup));
            Dispatch("Refine", ((int)p.RefinementCapacity + 63) / 64, 1, R(refined), Ref.Null, Ref.Null, R(stats));
        }
        ClearState(); Parameters(ref p); psFrame = true;
        Set(ps, 0, Renderer(gpu[3]), Renderer(gpu[4]), Renderer(gpu[5]), Renderer(gpu[6]));
        Set(ps, 11, Renderer(gpu[10])); Set(ps, 17, Renderer(gpu[11])); Set(ps, 15, Renderer(gpu[12]));
        Set(ps, 24, R(position), R(normal), R(albedo), R(identity));
        Set(ps, 29, R(input)); Set(ps, 31, R(metadata)); Set(ps, 34, R(sunInput));
        Set(ps, 53, R(shadowGuide)); Set(ps, 66, R(guideQuality)); Set(ps, 59, Ref.Null); Set(ps, 62, R(refined), R(lookup));
        if (host.Bound && !host.Commit()) { ClearState(); history = false; Status = "native fallback: ownership changed before composition"; return false; }
        Draw(composite, fullWidth, fullHeight, null, renderer: (ulong)gpu[2]);
        ClearState(); Copy(input, filtered); Copy(sunInput, sunFiltered);
        Copy(position, previousPosition); Copy(normal, previousNormal); Copy(identity, previousIdentity);
        Copy(sourceKey, previousSourceKey); Copy(shadowGuide, previousShadowGuide);
        Copy(fast, previousFast); Copy(sunFast, previousSunFast);
        Copy(refined, previousRefined); Copy(lookup, previousLookup);
        Copy(moments, previousMoments); Copy(reused, previousReservoir);
        Copy(metadata, previousMetadata);
        previousCamera = (double[])camera.Clone(); history = true;
        Status = width + "×" + height + ", " + lightCapacity + " light candidates, " + SceneBytes / 1048576 + " MiB scene, " + ReconstructionBytes / 1048576 + " MiB reconstruction";
        return true;
    }

    // Drops ids of a renderer that no longer exists.
    void Forget()
    {
        screenResources.Clear(); sceneResources.Clear(); rayResources.Clear(); shaders.Clear(); compute.Clear(); session = null; record = null;
        width = height = fullWidth = fullHeight = 0; signature = 0; history = false; previousCamera = null; initializedRoot = null;
        proxies = new ProxyCatalog(); SceneBytes = ReconstructionBytes = 0; lightData = null; atlasReady = null;
    }
    public void Reset()
    {
        Release(screenResources); Release(sceneResources); Release(rayResources); Release(shaders); compute.Clear();
        width = height = fullWidth = fullHeight = 0; signature = 0; history = false; previousCamera = null; initializedRoot = null;
        proxies = new ProxyCatalog(); SceneBytes = ReconstructionBytes = 0; lightData = null; atlasReady = null; refinementBudget.Reset();
        session = null; record = null;
    }
    public void Dispose() => Reset();
}
