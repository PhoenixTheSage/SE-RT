using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using SERT.Core;
using SERT.Integration;
using SharpDX;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using Buffer = SharpDX.Direct3D11.Buffer;
using Device = SharpDX.Direct3D11.Device;
using Vector4 = System.Numerics.Vector4;

namespace SERT.Rendering;

[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct Parameters
{
    public uint FullWidth, FullHeight, Width, Height;
    public uint Vertices, Triangles, Instances, Nodes;
    public uint Lights, Materials, Tlas, Frame;
    public Vector4 SunDirection, SunColor, Sky;
    public Matrix4x4 PreviousViewProjection;
    public Vector4 CameraDelta;
    public uint History, Pass, TreeLeaves, Poses;
    public float Exposure, Primary, Diffuse, Specular;
    public uint CollectStatistics, ShadowFlags;
    public float ShadowBias;
    public uint ShadowDebugView;
    public float LampRadius, SolarRadius, GlassTransmission, RefinementFraction;
    public uint BiasMode, SamplingMode, SamplingSeed, FarSunValid;
    public Matrix4x4 FarSunMatrix;
    public uint RefinementCapacity, RefinementPixels, QualityPadding0, QualityPadding1;
}

public sealed class SoftwareDx11 : IRayTracingBackend, ISoftwareDx11Session
{
    readonly AnomalyHost host;
    readonly Func<string> assets;
    readonly bool collectStatistics;
    readonly List<IDisposable> shaders = new List<IDisposable>();
    readonly List<IDisposable> sceneResources = new List<IDisposable>();
    readonly List<IDisposable> screenResources = new List<IDisposable>();
    Device device;
    VertexShader fullscreen;
    PixelShader guides, atlas, composite;
    readonly Dictionary<string, ComputeShader> compute = new Dictionary<string, ComputeShader>();
    Task<Dictionary<string, byte[]>> compilation;
    Dictionary<string, byte[]> bytecode;
    string compiledRoot, initializedRoot;
    Buffer parameters;
    SamplerState wrap, clamp;
    RasterizerState raster;
    DepthStencilState depth;
    BlendState blend;
    GpuBuffer vertices, triangles, nodes, instances, materials, sources, poses, skinIndices, lights, tree, stats, vertexErrors;
    GpuBuffer[] refitIndices;
    int[] refitCounts;
    int skinCount;
    GpuTexture colorAtlas, extensionAtlas, blueNoise;
    GpuTexture geometryGuide, sourceKey, previousSourceKey, shadowGuide, previousShadowGuide, fast, previousFast, sunFast, previousSunFast, lookup, previousLookup, guideQuality;
    GpuBuffer refinementCount, refinementWork, refined, previousRefined;
    int refinementAllocation;
    float allocatedRefinementFraction;
    readonly RefinementBudget refinementBudget = new RefinementBudget();
    public double FarSunMilliseconds { get; private set; } = double.NaN;
    public double CombinedMilliseconds => GpuMilliseconds + (double.IsNaN(FarSunMilliseconds) ? 0 : FarSunMilliseconds);
    public bool RefinementExhausted => refinementAllocation == 0 || refinementBudget.Multiplier == 0;
    public bool RefinementThrottled => refinementBudget.Multiplier < 1;
    public void ObserveBudget(double target) => refinementBudget.Observe(CombinedMilliseconds, target);
    public void ResetBudget() => refinementBudget.Reset();
    public string FarSunStatus { get; private set; } = "native hybrid";
    GpuTexture position, normal, albedo, identity, previousPosition, previousNormal, previousIdentity;
    GpuTexture noisy, temporal, scratch, filtered, metadata, previousMetadata, moments, previousMoments, sun, sunTemporal, sunScratch, sunFiltered;
    GpuBuffer candidate, reused, previousReservoir;
    int width, height, fullWidth, fullHeight, lightCapacity, treeLeaves;
    ulong signature;
    int epoch;
    bool history;
    ShadowSettings shadowSettings = ShadowSettings.Default;
    public void SetShadowSettings(ShadowSettings value)
    {
        if (shadowSettings.AllowLightBlockOffsets != value.AllowLightBlockOffsets || shadowSettings.Enabled != value.Enabled) signature = 0;
        if (!shadowSettings.Equals(value)) history = false;
        shadowSettings = value;
    }
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
    GpuTimer timer;
    Query retirement;
    bool retirementPending, retirementReady;
    public double GpuMilliseconds => timer?.Milliseconds ?? double.NaN;
    public long Measurements => timer?.Measurements ?? 0;
    public void Poll(object context) => Poll(context, null);
    public void Poll(object context, Action<double> measured)
    {
        if (!(context is DeviceContext rc) || rc.TypeInfo != DeviceContextType.Immediate) return;
        timer?.Poll(rc, measured == null ? (Action<double>)null : ms => measured(ms + (double.IsNaN(FarSunMilliseconds) ? 0 : FarSunMilliseconds)));
        if (retirementPending && rc.GetData(retirement, AsynchronousFlags.DoNotFlush, out int complete) && complete != 0) retirementReady = true;
    }
    public void InvalidateHistory() { history = false; }
    public string Name => "DX11 software BVH";
    public string Status { get; private set; } = "idle";
    public long SceneBytes { get; private set; }
    public long ReconstructionBytes { get; private set; }
    public void FillDiagnostics(RendererDiagnostics data)
    {
        data.Backend = Name; data.Width = width; data.Height = height;
        data.FullWidth = fullWidth; data.FullHeight = fullHeight; data.History = history;
        data.GpuMilliseconds = GpuMilliseconds; data.FarSunMilliseconds = FarSunMilliseconds; data.FarSunCoverage = FarSunStatus; data.RefinementRequested = shadowSettings.RefinementFraction; data.RefinementEffective = width == 0 ? 0 : Math.Min(refinementAllocation, refinementBudget.Capacity(width * height, shadowSettings.RefinementFraction)) / (float)(width * height); data.RefinementReason = refinementAllocation < Math.Floor(width * (double)height * shadowSettings.RefinementFraction) ? "reconstruction memory cap; " + refinementBudget.Reason : refinementBudget.Reason; data.Measurements = Measurements;
        data.SceneBytes = SceneBytes; data.ReconstructionBytes = ReconstructionBytes;
        data.LightCapacity = lightData?.Length ?? 0;
        data.AtlasTiles = atlasReady?.Length ?? 0;
        if (atlasReady != null) foreach (bool ready in atlasReady) if (ready) data.ReadyTiles++;
        if (lightData != null)
        {
            int proxyStart = lightData.Length - 1 - ProxyCatalog.Capacity;
            // Catalog order is native, emissive triangles, fixed proxy slots, environment.
            // Count the potentially huge triangle range arithmetically instead of walking it on Update.
            while (data.NativeLights < proxyStart && lightData[data.NativeLights].Kind < 2) data.NativeLights++;
            data.EmissiveLights = Math.Max(0, proxyStart - data.NativeLights);
            for (int i = Math.Max(0, proxyStart); i < lightData.Length - 1; ++i)
            {
                var light = lightData[i];
                if (light.Flags != 0 && light.Radiance.LengthSquared() > 0) data.ActiveProxies++;
            }
        }
    }
    public SoftwareDx11(AnomalyHost host, Func<string> assets, bool collectStatistics = false)
    { this.host = host; this.assets = assets; this.collectStatistics = collectStatistics; }

    T Scene<T>(T resource) where T : IDisposable { sceneResources.Add(resource); return resource; }
    T Screen<T>(T resource) where T : IDisposable { screenResources.Add(resource); return resource; }
    T Shader<T>(T resource) where T : IDisposable { shaders.Add(resource); return resource; }

    void Initialize(Device current, DeviceContext rc)
    {
        device = current;
        initializedRoot = compiledRoot;
        timer = Shader(new GpuTimer(device));
        retirement = Shader(new Query(device, new QueryDescription { Type = QueryType.Event }));
        byte[] Compile(string file, int profile) => bytecode[file];
        ShaderAbi.Verify(bytecode["Trace"]); ShaderAbi.Verify(bytecode["Refine"]);
        fullscreen = Shader(new VertexShader(device, Compile("Fullscreen", 0)));
        guides = Shader(new PixelShader(device, Compile("Guides", 1)));
        atlas = Shader(new PixelShader(device, Compile("Atlas", 1)));
        composite = Shader(new PixelShader(device, Compile("Composite", 1)));
        foreach (string name in new[] { "Skin", "Refit", "Identify", "LightWeights", "Candidates", "Reuse", "Trace", "Temporal", "Atrous", "Classify", "Refine" })
            compute[name] = Shader(new ComputeShader(device, Compile(name, 3)));
        byte[] noise = File.ReadAllBytes(Path.Combine(initializedRoot, "Sampling", "blue-noise-16x16x8.rgba"));
        if (noise.Length != 8192) throw new InvalidDataException("Invalid SE-RT sampling asset");
        blueNoise = Shader(new GpuTexture(device, 16, 16, Format.R8G8B8A8_UInt, 4, 8, false, false, true));
        var pins = GCHandle.Alloc(noise, GCHandleType.Pinned);
        try { for (int i = 0; i < 8; i++) MyUpload(rc, blueNoise.Texture, i, IntPtr.Add(pins.AddrOfPinnedObject(), i * 1024)); }
        finally { pins.Free(); }
        parameters = Shader(new Buffer(device, Utilities.SizeOf<Parameters>(), ResourceUsage.Default, BindFlags.ConstantBuffer, CpuAccessFlags.None, ResourceOptionFlags.None, 0));
        var sd = new SamplerStateDescription { Filter = Filter.MinMagMipLinear, AddressU = TextureAddressMode.Wrap, AddressV = TextureAddressMode.Wrap,
            AddressW = TextureAddressMode.Wrap, MaximumLod = float.MaxValue, ComparisonFunction = Comparison.Never };
        wrap = Shader(new SamplerState(device, sd)); sd.AddressU = sd.AddressV = sd.AddressW = TextureAddressMode.Clamp; clamp = Shader(new SamplerState(device, sd));
        raster = Shader(new RasterizerState(device, new RasterizerStateDescription { CullMode = CullMode.None, FillMode = FillMode.Solid, IsDepthClipEnabled = true }));
        depth = Shader(new DepthStencilState(device, new DepthStencilStateDescription { IsDepthEnabled = false, IsStencilEnabled = false }));
        var bd = new BlendStateDescription(); bd.RenderTarget[0].RenderTargetWriteMask = ColorWriteMaskFlags.All;
        blend = Shader(new BlendState(device, bd));
    }

    void BuildScene(DeviceContext rc, SceneGeometry scene, object[] frame)
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
        long predicted = SceneForecast(scene, atlasSources.Length, lightData.Length, activeNodes.Count, skinned.Length, shadowSettings.Enabled);
        if (predicted > Budget.SceneBytes) throw new InvalidOperationException("Scene, source atlas and shared far-sun allocation forecast exceed 512 MiB");
        vertices = Scene(new GpuBuffer(device, scene.Vertices.Length, 32, true)); vertices.Upload(rc, scene.Vertices);
        triangles = Scene(new GpuBuffer(device, scene.Triangles.Length, 16)); triangles.Upload(rc, scene.Triangles);
        nodes = Scene(new GpuBuffer(device, scene.Nodes.Length, 32, true)); nodes.Upload(rc, scene.Nodes);
        instances = Scene(new GpuBuffer(device, scene.Instances.Length, 176)); instances.Upload(rc, scene.Instances);
        materials = Scene(new GpuBuffer(device, scene.Materials.Length, 96));
        vertexErrors = Scene(new GpuBuffer(device, scene.Vertices.Length, 4, true)); rc.ClearUnorderedAccessView(vertexErrors.Uav, new SharpDX.Mathematics.Interop.RawInt4());
        sources = Scene(new GpuBuffer(device, scene.Sources.Length, 80)); sources.Upload(rc, scene.Sources);
        poses = Scene(new GpuBuffer(device, scene.Poses.Length, 64));
        int maxDepth = scene.NodeDepths.Max(); refitIndices = new GpuBuffer[maxDepth + 1]; refitCounts = new int[maxDepth + 1];
        for (int d = 0; d <= maxDepth; ++d)
        {
            var indices = activeNodes.Where(i => scene.NodeDepths[i] == d).Select(i => (uint)i).ToArray(); refitCounts[d] = indices.Length;
            if (indices.Length > 0) { refitIndices[d] = Scene(new GpuBuffer(device, indices.Length, 4)); refitIndices[d].Upload(rc, indices); }
        }
        skinCount = skinned.Length; skinIndices = Scene(new GpuBuffer(device, skinCount, 4)); skinIndices.Upload(rc, skinned);
        int tileCount = Math.Max(atlasSources.Length, 1);
        if (tileCount > 2048) throw new InvalidOperationException("D3D11 texture-array material capacity exceeded");
        colorAtlas = Scene(new GpuTexture(device, 64, 64, Format.R16G16B16A16_Float, 8, tileCount, true, false, true));
        extensionAtlas = Scene(new GpuTexture(device, 64, 64, Format.R16G16B16A16_Float, 8, tileCount, true, false, true));
        CreateLights(rc, scene);
        SceneBytes = sceneResources.Sum(x => x is GpuTexture t ? t.Bytes : ((GpuBuffer)x).Bytes) + blueNoise.Bytes + Marshal.SizeOf<Parameters>() + (shadowSettings.Enabled ? 2048L*2048*4+80 : 0);
        if (SceneBytes != predicted) throw new InvalidOperationException("Scene allocation disagrees with its forecast");
        if (SceneBytes > Budget.SceneBytes) throw new InvalidOperationException("512 MiB scene budget exceeded");
    }
    static string TextureKey(object[] textures) => string.Join(":", textures.Select(x => x is ShaderResourceView s ? s.NativePointer.ToInt64().ToString() : "0"));
    string AtlasVersion(SceneGeometry scene,int tile,object[] textures) => TextureKey(textures) + (atlasSources[tile] >= 0 && (scene.Materials[atlasSources[tile]].Flags & 32) != 0 ? ":optics:" + scene.Materials[atlasSources[tile]].Padding : "");
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
    static bool DynamicTexture(object[] textures)
    {
        foreach (var value in textures) if (value is ShaderResourceView srv)
            using (var texture = srv.ResourceAs<Texture2D>()) if ((texture.Description.BindFlags & BindFlags.RenderTarget) != 0) return true;
        return false;
    }
    void CreateLights(DeviceContext rc, SceneGeometry scene)
    {
        lightCapacity = lightData.Length; treeLeaves = 1; while (treeLeaves < lightCapacity) treeLeaves = checked(treeLeaves * 2);
        lights = Scene(new GpuBuffer(device, lightCapacity, 80)); lights.Upload(rc, lightData);
        tree = Scene(new GpuBuffer(device, treeLeaves * 2, 4, true)); stats = Scene(new GpuBuffer(device, 8, 4, true));
    }

    static void MyUpload(DeviceContext rc, Texture2D texture, int slice, IntPtr data) => rc.UpdateSubresource(new DataBox(data, 64, 1024), texture, slice);
    static long BufferBytes(int count,int stride) => (long)Math.Max(1,count)*stride;
    public static long SceneForecast(SceneGeometry scene,int tiles,int lightCount,int refitEntries,int skinnedCount,bool farSun)
    {
        int leaves=1;while(leaves<lightCount)leaves=checked(leaves*2);
        return checked(BufferBytes(scene.Vertices.Length,32)+BufferBytes(scene.Triangles.Length,16)+BufferBytes(scene.Nodes.Length,32)+
            BufferBytes(scene.Instances.Length,176)+BufferBytes(scene.Materials.Length,96)+BufferBytes(scene.Sources.Length,80)+BufferBytes(scene.Poses.Length,64)+
            BufferBytes(scene.Vertices.Length,4)+Math.Max(1,tiles)*65536L+BufferBytes(lightCount,80)+leaves*8L+32+
            BufferBytes(skinnedCount,4)+refitEntries*4L+8192+Marshal.SizeOf<Parameters>()+(farSun?2048L*2048*4+80:0));
    }
    public static long ScreenForecast(int pixels, int capacity) => checked((long)pixels * 500 + Math.Max(1, capacity) * (2L * Marshal.SizeOf<RefinementSample>() + 8) + 4);
    static int PoolCapacity(int pixels, float fraction)
    {
        long available = Budget.ReconstructionBytes - (long)pixels * 500 - 4;
        return Math.Max(0, (int)Math.Min(Math.Floor(pixels * (double)fraction), Math.Max(0, available / (2L * Marshal.SizeOf<RefinementSample>() + 8))));
    }
    void BuildScreen(int fw, int fh, float scale)
    {
        Release(screenResources); history = false; fullWidth = fw; fullHeight = fh;
        width = Math.Max(1, (int)Math.Ceiling(fw * scale)); height = Math.Max(1, (int)Math.Ceiling(fh * scale));
        int pixels = checked(width * height); refinementAllocation = PoolCapacity(pixels, shadowSettings.RefinementFraction);
        allocatedRefinementFraction = shadowSettings.RefinementFraction;
        if (ScreenForecast(pixels, refinementAllocation) > Budget.ReconstructionBytes) throw new InvalidOperationException("256 MiB reconstruction budget exceeded before allocation");
        GpuTexture Texture(Format f, int bytes, int layers = 1, bool rtv = false) => Screen(new GpuTexture(device, width, height, f, bytes, layers, rtv));
        position = Texture(Format.R32G32B32A32_Float, 16, rtv: true); previousPosition = Texture(Format.R32G32B32A32_Float, 16);
        normal = Texture(Format.R16G16B16A16_Float, 8, rtv: true); previousNormal = Texture(Format.R16G16B16A16_Float, 8);
        albedo = Texture(Format.R16G16B16A16_Float, 8, rtv: true);
        identity = Texture(Format.R32G32_UInt, 8); previousIdentity = Texture(Format.R32G32_UInt, 8);
        noisy = Texture(Format.R16G16B16A16_Float, 8, 4); temporal = Texture(Format.R16G16B16A16_Float, 8, 4);
        scratch = Texture(Format.R16G16B16A16_Float, 8, 4); filtered = Texture(Format.R16G16B16A16_Float, 8, 4);
        metadata = Texture(Format.R32G32B32A32_Float, 16);
        previousMetadata = Texture(Format.R32G32B32A32_Float, 16);
        moments = Texture(Format.R32G32B32A32_Float, 16); previousMoments = Texture(Format.R32G32B32A32_Float, 16);
        sun = Texture(Format.R16G16B16A16_Float, 8); sunTemporal = Texture(Format.R16G16B16A16_Float, 8); sunScratch = Texture(Format.R16G16B16A16_Float, 8); sunFiltered = Texture(Format.R16G16B16A16_Float, 8);
        // Keep numerical bounds and geometric normals in FP32; history/filter
        // guides may use FP16, but ray-origin uncertainty must not be quantized down.
        geometryGuide = Texture(Format.R32G32B32A32_Float, 16); guideQuality = Texture(Format.R32_UInt, 4);
        sourceKey = Texture(Format.R32G32B32A32_UInt, 16); previousSourceKey = Texture(Format.R32G32B32A32_UInt, 16);
        shadowGuide = Texture(Format.R16G16B16A16_Float, 8); previousShadowGuide = Texture(Format.R16G16B16A16_Float, 8);
        fast = Texture(Format.R16G16B16A16_Float, 8); previousFast = Texture(Format.R16G16B16A16_Float, 8);
        sunFast = Texture(Format.R16G16B16A16_Float, 8); previousSunFast = Texture(Format.R16G16B16A16_Float, 8);
        lookup = Texture(Format.R32_UInt, 4); previousLookup = Texture(Format.R32_UInt, 4);
        refinementCount = Screen(new GpuBuffer(device, 1, 4, true));
        refinementWork = Screen(new GpuBuffer(device, Math.Max(1, refinementAllocation), 8, true));
        refined = Screen(new GpuBuffer(device, Math.Max(1, refinementAllocation), Marshal.SizeOf<RefinementSample>(), true)); previousRefined = Screen(new GpuBuffer(device, Math.Max(1, refinementAllocation), Marshal.SizeOf<RefinementSample>(), true));
        candidate = Screen(new GpuBuffer(device, width * height, 32, true)); reused = Screen(new GpuBuffer(device, width * height, 32, true)); previousReservoir = Screen(new GpuBuffer(device, width * height, 32, true));
        ReconstructionBytes = screenResources.Sum(x => x is GpuTexture t ? t.Bytes : ((GpuBuffer)x).Bytes);
        if (ReconstructionBytes != ScreenForecast(pixels, refinementAllocation)) throw new InvalidOperationException("Reconstruction allocation disagrees with its forecast");
        if (ReconstructionBytes > Budget.ReconstructionBytes) throw new InvalidOperationException("256 MiB reconstruction budget exceeded; reduce sampling scale");
    }

    void UpdateParameters(DeviceContext rc, ref Parameters p)
    {
        rc.UpdateSubresource(ref p, parameters);
        rc.ComputeShader.SetConstantBuffer(1, parameters); rc.PixelShader.SetConstantBuffer(1, parameters);
    }
    void BindScene(DeviceContext rc)
    {
        rc.ComputeShader.SetShaderResources(16, vertices.Srv, triangles.Srv, nodes.Srv, instances.Srv, materials.Srv, lights.Srv, colorAtlas.Srv, extensionAtlas.Srv);
        rc.ComputeShader.SetShaderResources(24, position.Srv, normal.Srv, albedo.Srv, identity.Srv);
        rc.ComputeShader.SetShaderResources(30, previousPosition.Srv, metadata.Srv, moments.Srv, tree.Srv, sun.Srv);
        rc.ComputeShader.SetShaderResources(36, previousNormal.Srv, previousIdentity.Srv, filtered.Srv);
        rc.ComputeShader.SetShaderResources(49, geometryGuide.Srv, blueNoise.Srv, sourceKey.Srv, previousSourceKey.Srv, shadowGuide.Srv, previousShadowGuide.Srv, fast.Srv, previousFast.Srv, sunFast.Srv, previousSunFast.Srv);
        rc.ComputeShader.SetShaderResource(66, guideQuality.Srv); rc.ComputeShader.SetShaderResource(67, vertexErrors.Srv);
        rc.ComputeShader.SetSamplers(0, wrap, clamp);
    }
    void Dispatch(DeviceContext rc, string name, int x, int y = 1, params UnorderedAccessView[] outputs)
    {
        rc.ComputeShader.Set(compute[name]); rc.ComputeShader.SetUnorderedAccessViews(0, outputs); rc.Dispatch(Math.Max(1, x), Math.Max(1, y), 1);
        rc.ComputeShader.SetUnorderedAccessViews(0, new UnorderedAccessView[outputs.Length]);
    }
    void Draw(DeviceContext rc, PixelShader shader, int w, int h, params RenderTargetView[] targets)
    {
        rc.OutputMerger.SetTargets(null, targets.Length, targets); rc.OutputMerger.SetBlendState(blend); rc.OutputMerger.SetDepthStencilState(depth);
        rc.Rasterizer.State = raster; rc.Rasterizer.SetViewport(0, 0, w, h); rc.InputAssembler.InputLayout = null;
        rc.InputAssembler.PrimitiveTopology = PrimitiveTopology.TriangleList; rc.VertexShader.Set(fullscreen); rc.PixelShader.Set(shader);
        if (shader == composite) rc.PixelShader.SetSamplers(0, clamp, clamp, clamp, wrap, wrap, wrap);
        else rc.PixelShader.SetSamplers(0, wrap, clamp);
        rc.Draw(3, 0); rc.OutputMerger.SetRenderTargets((RenderTargetView)null);
    }

    public bool Render(object[] frame, SceneGeometry scene, float scale, float exposure)
    {
        var gpu = (object[])frame[7]; var currentDevice = (Device)gpu[0]; var rc = (DeviceContext)gpu[1]; var size = (int[])frame[8];
        if (gpu.Length < 13 || scene.Instances.Length == 0 || scene.Nodes.Length == 0 || gpu[9] == null || gpu[10] == null || gpu[11] == null || gpu[12] == null)
        { Status = "native fallback: missing scene, sun coverage, or environment probe"; history = false; return false; }
        string root = assets();
        if (compiledRoot != root)
        {
            compiledRoot = root; bytecode = null;
            compilation = Task.Run(() =>
            {
                var result = new Dictionary<string, byte[]>();
                foreach (string name in new[] { "Fullscreen", "Guides", "Atlas", "Composite", "Skin", "Refit", "Identify", "LightWeights", "Candidates", "Reuse", "Trace", "Temporal", "Atrous", "Classify", "Refine" })
                    result[name] = host.Compile(Path.Combine(root, "Shaders", name + ".hlsl"), name == "Fullscreen" ? 0 : name == "Guides" || name == "Atlas" || name == "Composite" ? 1 : 3);
                return result;
            });
        }
        if (bytecode == null)
        {
            if (!compilation.IsCompleted) { Status = "native fallback: compiling shader cache on worker"; return false; }
            bytecode = compilation.GetAwaiter().GetResult();
        }
        if (device == null || device.NativePointer != currentDevice.NativePointer) { Reset(); Initialize(currentDevice, rc); }
        // Worker results contain texture-view placeholders from an earlier callback.
        // Replace them with this frame's borrowed views before deduplicating or binding.
        SceneAssembler.Refresh(scene, frame);
        if (ScreenForecast(checked((int)Math.Ceiling(size[0] * scale) * (int)Math.Ceiling(size[1] * scale)), 0) > Budget.ReconstructionBytes) scale = .25f;
        bool replacing = allocatedRefinementFraction != shadowSettings.RefinementFraction || initializedRoot != compiledRoot || signature != scene.Signature || epoch != (int)frame[1] || fullWidth != size[0] || fullHeight != size[1] || width != Math.Max(1, (int)Math.Ceiling(size[0] * scale));
        if (retirementPending || (replacing && (sceneResources.Count > 0 || screenResources.Count > 0)))
        {
            if (!retirementPending) { rc.End(retirement); retirementPending = true; retirementReady = false; history = false; }
            if (!retirementReady) { Status = "native fallback: waiting for GPU resource retirement"; return false; }
            retirementPending = retirementReady = false;
        }
        if (initializedRoot != compiledRoot) { Reset(); Initialize(currentDevice, rc); }
        if (signature != scene.Signature || epoch != (int)frame[1]) BuildScene(rc, scene, frame);
        if (allocatedRefinementFraction != shadowSettings.RefinementFraction || width == 0 || fullWidth != size[0] || fullHeight != size[1] || width != Math.Max(1, (int)Math.Ceiling(size[0] * scale))) BuildScreen(size[0], size[1], scale);
        timer.Begin(rc);
        cookies = frame.Length > 13 ? (object[][])frame[13] : Array.Empty<object[]>();
        if (cookies.Length != 0 && cookies.Length != cookieTiles.Length) { signature = 0; history = false; timer.End(rc); Status = "native fallback: spotlight layout changed"; return false; }
        for (int i = 0; i < cookies.Length; ++i) if (cookies[i][0] != null && cookieTiles[i] == uint.MaxValue)
        { signature = 0; history = false; timer.End(rc); Status = "native fallback: spotlight cookie became available"; return false; }
        var owners = new HashSet<uint>(); foreach (var light in lightData) if (light.Kind < 2) owners.Add(light.Identity);
        proxies.Refresh(frame.Length > 12 ? (object[][])frame[12] : Array.Empty<object[]>(), owners);
        instances.Upload(rc, scene.Instances); poses.Upload(rc, scene.Poses);
        for (int i = 0; i < scene.Materials.Length; ++i)
            for (int g = 0; g < ((scene.Materials[i].Flags & 4) != 0 ? 6 : 1); ++g)
            {
                uint tile = Tile(scene.Materials[i], g);
                if (TextureKey(TextureGroup(scene.Textures[i], g * 4)) != TextureKey(TextureGroup(scene.Textures[atlasSources[tile]], atlasOffsets[tile])))
                { signature = 0; history = false; timer.End(rc); Status = "native fallback: material texture layout changed"; return false; }
            }
        int baked = 0, startTile = atlasCursor;
        for (int k = 0; k < atlasSources.Length && baked < 32; ++k)
        {
            int tile = (startTile + k) % atlasSources.Length; var textures = AtlasSource(scene, tile);
            if (atlasSources[tile] == -1 && proxies.Lights[atlasOffsets[tile]].Radiance.LengthSquared() <= 0) continue;
            string version = AtlasVersion(scene, tile, textures);
            if (atlasReady[tile] && atlasVersions[tile] == version && !DynamicTexture(textures)) continue;
            var presence = new Parameters { FullWidth = textures[0] == null ? 0u : 1u, FullHeight = textures[1] == null ? 0u : 1u,
                Width = textures[2] == null ? 0u : 1u, Height = textures[3] == null ? 0u : 1u };
            rc.UpdateSubresource(ref presence, parameters); rc.PixelShader.SetConstantBuffer(1, parameters);
            rc.PixelShader.SetShaderResources(0, textures.Select(x => x as ShaderResourceView).ToArray());
            Draw(rc, atlas, 64, 64, colorAtlas.Slices[tile], extensionAtlas.Slices[tile]);
            atlasReady[tile] = true; atlasVersions[tile] = version; baked++; atlasCursor = (tile + 1) % atlasSources.Length;
        }
        int awaiting = 0;
        for (int tile = 0; tile < atlasSources.Length; ++tile)
        {
            if (atlasSources[tile] == -1 && (proxies.Lights[atlasOffsets[tile]].Flags == 0 || proxies.Lights[atlasOffsets[tile]].Radiance.LengthSquared() <= 0)) continue;
            if (!atlasReady[tile] || atlasVersions[tile] != AtlasVersion(scene, tile, AtlasSource(scene, tile))) awaiting++;
        }
        if (awaiting > 0) { timer.End(rc); history = false; Status = "native fallback: " + awaiting + " source texture tiles awaiting bake"; return false; }
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
        materials.Upload(rc, scene.Materials);
        if (!LightCatalog.Refresh(lightData, ShadowSettings.NativeLights(frame, shadowSettings.AllowLightBlockOffsets), scene, epoch))
        { signature = 0; history = false; timer.End(rc); Status = "native fallback: light catalog changed; rebuild next frame"; return false; }
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
        lights.Upload(rc, lightData);
        var sunData = (float[])frame[9]; var camera = (double[])frame[3];
        object[] far = frame.Length > 15 ? frame[15] as object[] : null;
        bool farValid = far != null && far.Length == 7 && far[0] is int v && v == 1 && far[1] is uint f && f == SceneAssembler.FrameIndex(frame) && far[2] is ShaderResourceView && far[3] is float[] matrix && matrix.Length == 16 && Array.TrueForAll(matrix, Budget.Finite);
        FarSunMilliseconds = far != null && far.Length == 7 && far[4] is double ms && !double.IsNaN(ms) && !double.IsInfinity(ms) && ms >= 0 ? ms : double.NaN;
        FarSunStatus = farValid ? "shared far-only coverage" : "native hybrid (local softness constrained): " + (far != null && far.Length == 7 ? far[6] : "optional Anomaly product unavailable");
        var p = new Parameters { FullWidth = (uint)size[0], FullHeight = (uint)size[1], Width = (uint)width, Height = (uint)height,
            Vertices = (uint)scene.Vertices.Length, Triangles = (uint)scene.Triangles.Length, Nodes = (uint)scene.Nodes.Length, Instances = (uint)scene.Instances.Length,
            Materials = (uint)scene.Materials.Length, Lights = (uint)lightCapacity, Tlas = scene.TlasRoot, Frame = SceneAssembler.FrameIndex(frame),
            SunDirection = new Vector4(sunData[0], sunData[1], sunData[2], sunData[3]),
            SunColor = new Vector4(sunData[4] * sunData[3], sunData[5] * sunData[3], sunData[6] * sunData[3], sunData[11]),
            Sky = new Vector4(sunData[8], sunData[9], sunData[10], ((float[])frame[6]).Length / 16), PreviousViewProjection = SceneAssembler.Matrix((float[])frame[11]),
            CameraDelta = previousCamera == null ? Vector4.Zero : new Vector4((float)(camera[0] - previousCamera[0]), (float)(camera[1] - previousCamera[1]), (float)(camera[2] - previousCamera[2]), 0),
            History = history && sunData[7] > 0 && gpu[7] != null ? 1u : 0u, TreeLeaves = (uint)treeLeaves, Poses = (uint)scene.Poses.Length,
            Exposure = exposure, Primary = Budget.PrimaryDistance, Diffuse = Budget.DiffuseDistance, Specular = Budget.SpecularDistance,
            CollectStatistics = collectStatistics ? 1u : 0u, ShadowFlags = shadowSettings.Enabled ? 0u : 1u,
            ShadowBias = shadowSettings.Bias, ShadowDebugView = (uint)shadowSettings.DebugView,
            LampRadius = shadowSettings.LampRadius, SolarRadius = shadowSettings.Extent.SolarRadius, GlassTransmission = shadowSettings.GlassTransmission,
            RefinementFraction = shadowSettings.RefinementFraction * refinementBudget.Multiplier, BiasMode = (uint)shadowSettings.BiasMode,
            SamplingMode = (uint)shadowSettings.Sampling, SamplingSeed = shadowSettings.SamplingSeed, FarSunValid = farValid ? 1u : 0u,
            FarSunMatrix = farValid ? SceneAssembler.Matrix((float[])far[3]) : Matrix4x4.Identity,
            RefinementCapacity = (uint)Math.Min(refinementAllocation, refinementBudget.Capacity(width * height, shadowSettings.RefinementFraction)), RefinementPixels = (uint)refinementAllocation };
        UpdateParameters(rc, ref p);
        rc.PixelShader.SetConstantBuffer(0, (Buffer)gpu[8]);
        rc.PixelShader.SetShaderResources(0, (ShaderResourceView)gpu[3], (ShaderResourceView)gpu[4], (ShaderResourceView)gpu[5], (ShaderResourceView)gpu[6]);
        Draw(rc, guides, width, height, position.Rtv, normal.Rtv, albedo.Rtv);
        BindScene(rc); rc.ComputeShader.SetConstantBuffer(0, (Buffer)gpu[8]); rc.ComputeShader.SetShaderResource(59, farValid ? (ShaderResourceView)far[2] : null); rc.ComputeShader.SetShaderResources(40, sources.Srv, poses.Srv, null, (ShaderResourceView)gpu[10], (ShaderResourceView)gpu[9]);
        if (skinCount > 0) { p.Pass = (uint)skinCount; UpdateParameters(rc, ref p); rc.ComputeShader.SetShaderResource(48, skinIndices.Srv); rc.ComputeShader.SetShaderResource(67, null); rc.ComputeShader.SetShaderResource(16, null); Dispatch(rc, "Skin", (skinCount + 63) / 64, 1, vertices.Uav, vertexErrors.Uav); rc.ComputeShader.SetShaderResource(16, vertices.Srv); rc.ComputeShader.SetShaderResource(67, vertexErrors.Srv); }
        rc.ComputeShader.SetShaderResource(18, null);
        for (int d = refitCounts.Length - 1; d >= 0; --d) if (refitCounts[d] > 0)
        { p.Pass = (uint)refitCounts[d]; UpdateParameters(rc, ref p); rc.ComputeShader.SetShaderResource(42, refitIndices[d].Srv); Dispatch(rc, "Refit", (refitCounts[d] + 63) / 64, 1, nodes.Uav); }
        rc.ComputeShader.SetShaderResource(18, nodes.Srv);
        int gx = (width + 7) / 8, gy = (height + 7) / 8;
        rc.ComputeShader.SetShaderResource(27, null); rc.ComputeShader.SetShaderResource(49, null); rc.ComputeShader.SetShaderResource(24, null);
        Dispatch(rc, "Identify", gx, gy, identity.Uav, geometryGuide.Uav, position.Uav);
        rc.ComputeShader.SetShaderResource(27, identity.Srv); rc.ComputeShader.SetShaderResource(49, geometryGuide.Srv); rc.ComputeShader.SetShaderResource(24, position.Srv);
        p.Pass = 0; UpdateParameters(rc, ref p); rc.ComputeShader.SetShaderResource(33, null); Dispatch(rc, "LightWeights", (treeLeaves + 63) / 64, 1, tree.Uav);
        for (int level = treeLeaves / 2; level > 0; level /= 2) { p.Pass = (uint)level; UpdateParameters(rc, ref p); Dispatch(rc, "LightWeights", (level + 63) / 64, 1, tree.Uav); }
        rc.ComputeShader.SetShaderResource(33, tree.Srv); rc.ComputeShader.SetShaderResource(35, gpu[7] as ShaderResourceView);
        Dispatch(rc, "Candidates", gx, gy, candidate.Uav); rc.ComputeShader.SetShaderResource(28, candidate.Srv); rc.ComputeShader.SetShaderResource(39, previousReservoir.Srv);
        Dispatch(rc, "Reuse", gx, gy, reused.Uav); rc.ComputeShader.SetShaderResource(28, reused.Srv);
        rc.ClearUnorderedAccessView(stats.Uav, new SharpDX.Mathematics.Interop.RawInt4()); rc.ComputeShader.SetShaderResource(31, null); rc.ComputeShader.SetShaderResource(34, null);
        rc.ComputeShader.SetShaderResource(51, null); rc.ComputeShader.SetShaderResource(53, null); rc.ComputeShader.SetShaderResource(66, null);
        Dispatch(rc, "Trace", gx, gy, noisy.Uav, sun.Uav, metadata.Uav, stats.Uav, sourceKey.Uav, shadowGuide.Uav, guideQuality.Uav);
        rc.ComputeShader.SetShaderResource(51, sourceKey.Srv); rc.ComputeShader.SetShaderResource(53, shadowGuide.Srv); rc.ComputeShader.SetShaderResource(66, guideQuality.Srv);
        rc.ComputeShader.SetShaderResource(29, noisy.Srv); rc.ComputeShader.SetShaderResource(31, metadata.Srv); rc.ComputeShader.SetShaderResource(34, sun.Srv);
        rc.ComputeShader.SetShaderResource(32, previousMoments.Srv); rc.ComputeShader.SetShaderResource(45, sunFiltered.Srv);
        rc.ComputeShader.SetShaderResource(47, previousMetadata.Srv);
        rc.ComputeShader.SetShaderResource(55, null); rc.ComputeShader.SetShaderResource(57, null);
        Dispatch(rc, "Temporal", gx, gy, temporal.Uav, moments.Uav, sunTemporal.Uav, null, fast.Uav, sunFast.Uav); rc.ComputeShader.SetShaderResource(32, moments.Srv);
        GpuTexture input = temporal, sunInput = sunTemporal;
        for (uint iteration = 0; iteration < 3; ++iteration)
        {
            var output = iteration == 1 ? temporal : scratch; var sunOutput = iteration == 1 ? sunTemporal : sunScratch;
            p.Pass = iteration; UpdateParameters(rc, ref p); rc.ComputeShader.SetShaderResource(29, input.Srv); rc.ComputeShader.SetShaderResource(34, sunInput.Srv);
            Dispatch(rc, "Atrous", gx, gy, output.Uav, sunOutput.Uav); input = output; sunInput = sunOutput;
        }
        rc.ClearUnorderedAccessView(lookup.Uav, new SharpDX.Mathematics.Interop.RawInt4());
        rc.ClearUnorderedAccessView(refinementCount.Uav, new SharpDX.Mathematics.Interop.RawInt4());
        if (p.RefinementCapacity > 0) {
            rc.ComputeShader.SetShaderResources(0, (ShaderResourceView)gpu[3], (ShaderResourceView)gpu[4], (ShaderResourceView)gpu[5], (ShaderResourceView)gpu[6]);
            p.QualityPadding0 = 0; UpdateParameters(rc, ref p); Dispatch(rc, "Classify", gx, gy, refinementCount.Uav, refinementWork.Uav, lookup.Uav);
            p.QualityPadding0 = 1; UpdateParameters(rc, ref p); Dispatch(rc, "Classify", gx, gy, refinementCount.Uav, refinementWork.Uav, lookup.Uav);
            rc.ComputeShader.SetShaderResources(60, refinementCount.Srv, refinementWork.Srv); rc.ComputeShader.SetShaderResource(64, previousRefined.Srv); rc.ComputeShader.SetShaderResource(65, previousLookup.Srv);
            Dispatch(rc, "Refine", ((int)p.RefinementCapacity + 63) / 64, 1, refined.Uav, null, null, stats.Uav);
        }
        rc.ClearState(); UpdateParameters(rc, ref p); rc.PixelShader.SetConstantBuffer(0, (Buffer)gpu[8]);
        rc.PixelShader.SetShaderResources(0, (ShaderResourceView)gpu[3], (ShaderResourceView)gpu[4], (ShaderResourceView)gpu[5], (ShaderResourceView)gpu[6]);
        rc.PixelShader.SetShaderResource(11, (ShaderResourceView)gpu[10]); rc.PixelShader.SetShaderResource(17, (ShaderResourceView)gpu[11]); rc.PixelShader.SetShaderResource(15, (ShaderResourceView)gpu[12]);
        rc.PixelShader.SetShaderResources(24, position.Srv, normal.Srv, albedo.Srv, identity.Srv);
        rc.PixelShader.SetShaderResource(29, input.Srv); rc.PixelShader.SetShaderResource(31, metadata.Srv); rc.PixelShader.SetShaderResource(34, sunInput.Srv);
        rc.PixelShader.SetShaderResource(53, shadowGuide.Srv); rc.PixelShader.SetShaderResource(66, guideQuality.Srv); rc.PixelShader.SetShaderResource(59, farValid ? (ShaderResourceView)far[2] : null); rc.PixelShader.SetShaderResources(62, refined.Srv, lookup.Srv);
        if (host.Bound && !host.Commit()) { rc.ClearState(); timer.End(rc); history = false; Status = "native fallback: ownership changed before composition"; return false; }
        Draw(rc, composite, fullWidth, fullHeight, (RenderTargetView)gpu[2]);
        rc.ClearState(); rc.CopyResource(input.Texture, filtered.Texture); rc.CopyResource(sunInput.Texture, sunFiltered.Texture);
        rc.CopyResource(position.Texture, previousPosition.Texture); rc.CopyResource(normal.Texture, previousNormal.Texture); rc.CopyResource(identity.Texture, previousIdentity.Texture);
        rc.CopyResource(sourceKey.Texture, previousSourceKey.Texture); rc.CopyResource(shadowGuide.Texture, previousShadowGuide.Texture);
        rc.CopyResource(fast.Texture, previousFast.Texture); rc.CopyResource(sunFast.Texture, previousSunFast.Texture);
        rc.CopyResource(refined.Buffer, previousRefined.Buffer); rc.CopyResource(lookup.Texture, previousLookup.Texture);
        rc.CopyResource(moments.Texture, previousMoments.Texture); rc.CopyResource(reused.Buffer, previousReservoir.Buffer);
        rc.CopyResource(metadata.Texture, previousMetadata.Texture);
        previousCamera = (double[])camera.Clone(); history = true;
        timer.End(rc);
        Status = width + "×" + height + ", " + lightCapacity + " light candidates, " + SceneBytes / 1048576 + " MiB scene, " + ReconstructionBytes / 1048576 + " MiB reconstruction";
        return true;
    }
    public void Reset()
    {
        Release(screenResources); Release(sceneResources); Release(shaders); compute.Clear(); device = null;
        width = height = fullWidth = fullHeight = 0; signature = 0; history = false; previousCamera = null; timer = null; retirement = null; retirementPending = retirementReady = false;
        proxies = new ProxyCatalog(); SceneBytes = ReconstructionBytes = 0;
        lightData = null; atlasReady = null; refinementBudget.Reset(); FarSunMilliseconds = double.NaN; FarSunStatus = "native hybrid";
    }
    static void Release(List<IDisposable> list) { for (int i = list.Count - 1; i >= 0; --i) list[i].Dispose(); list.Clear(); }
    public void Dispose() => Reset();
}
