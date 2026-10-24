using System;
using System.IO;
using System.Reflection;
using System.ComponentModel;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using ClientPlugin.Settings;
using ClientPlugin.Settings.Layouts;
using Sandbox.Graphics.GUI;
using SERT.Core;
using SERT.Integration;
using SERT.Rendering;
using VRage.Plugins;
using VRage.Utils;
#if !LOCAL_BUILD
[assembly: AssemblyVersion("1.0.2.0")]
[assembly: AssemblyFileVersion("1.0.2.0")]
#endif
namespace ClientPlugin;
public class Plugin : IPlugin
{
    public const string Name = "SERT";
    public static Plugin Instance { get; private set; }
    readonly object renderGate = new object();
    readonly AnomalyHost host = new AnomalyHost();
    SettingsGenerator settings;
    IRayTracingBackend backend;
    Task<SceneGeometry> build;
    SceneGeometry scene;
    string assetFolder;
    bool disposed;
    float lastExposure;
    readonly AdaptiveBudget adaptive = new AdaptiveBudget();
    AnomalyTerminalHook terminal;
    int historyReset;
    long settingsRevision, synchronizedRevision;
    long settingsChangedAt, lastFrameAt, buildTicks, nextDiagnostics;
    long frames, composed, fallbacks, resets, builds;
    uint lastFrame;
    int epoch;
    string lastError = "none", lastReset = "none", hardwareStatus = "hardware acceleration off";
    bool rayQuery, vulkanRayQuery;
    volatile RendererDiagnostics diagnostics = new RendererDiagnostics();
    public string Status { get; private set; } = "starting";
    public void Init(object gameInstance)
    {
        Instance = this; settings = new SettingsGenerator();
        terminal = new AnomalyTerminalHook(() => diagnostics, ShowStatus, RequestHistoryReset, LogStatus);
        Config.Current.PropertyChanged += ConfigChanged;
        if (string.IsNullOrEmpty(assetFolder))
        {
            string location = Assembly.GetExecutingAssembly().Location;
            string directory = string.IsNullOrEmpty(location) ? AppDomain.CurrentDomain.BaseDirectory : Path.GetDirectoryName(location);
            assetFolder = Path.Combine(directory, "Assets");
        }
        ApplyRoute();
        PublishDiagnostics(); terminal.Update(true, true);
    }
    void ApplyRoute()
    {
        if (AnomalyRenderer.LinuxRequiresGuest)
        {
            if (backend is HardwareVulkan) { backend.Dispose(); backend = null; }
            hardwareStatus = "Linux stays idle until Anomaly's Vulkan guest bus is open";
            Status = hardwareStatus;
            return;
        }
        var caps = VulkanRayApi.Query();
        rayQuery = caps.RayQuery != 0;
        var decision = HardwareChoice.Select(Config.Current.HardwareAcceleration, AnomalyRenderer.Active, rayQuery, enabled: Config.Current.Enabled);
        hardwareStatus = decision.Status;
        // Hardware tracing on Vulkan runs the same stack through ray query on the tenant session.
        if ((decision.Kind == BackendKind.SoftwareDx11 || decision.Kind == BackendKind.SoftwareVulkan || decision.Kind == BackendKind.HardwareVulkan) && decision.Trace)
        {
            bool vulkan = decision.Kind != BackendKind.SoftwareDx11; vulkanRayQuery = decision.Kind == BackendKind.HardwareVulkan;
            if (backend is HardwareVulkan || (backend is SoftwareVulkan) != vulkan || (backend is SoftwareVulkan sv && sv.RayQuery != vulkanRayQuery)) { backend?.Dispose(); backend = null; }
            if (vulkan) VulkanRayApi.Disable();
            if (!host.Bound) BindRayDevice();
            return;
        }
        if (backend is ISoftwareDx11Session) { backend.Dispose(); backend = null; }
        if (host.Bound) host.Unbind();
        if (decision.Trace)
        {
            if (!(backend is HardwareVulkan)) backend = new HardwareVulkan();
            SubmitHardware((HardwareVulkan)backend);
            Status = backend.Status;
            return;
        }
        if (backend is HardwareVulkan) { backend.Dispose(); backend = null; }
        Status = decision.Status;
    }
    void SubmitHardware(HardwareVulkan hardware)
    {
        var config = Config.Current.Snapshot();
        hardware.Apply(config.Enabled && config.HardwareAcceleration, config.RayTracedShadows, config.StorageExposure, config.ShadowBias, config.SolarAngularRadius * (float)(Math.PI / 180.0));
        hardware.Render(null, null, config.SamplingScale, config.StorageExposure);
    }
    void BindRayDevice()
    {
        // DX11 tenants share the game's device; Vulkan tenants record into the renderer's tenant compute session.
        if (!AnomalyRenderer.AllowDx11Device && AnomalyRenderer.Active != AnomalyRenderer.Vulkan)
        {
            Status = AnomalyRenderer.Decided
                ? "DX11 ray backend closed (" + AnomalyRenderer.ActiveName + ")"
                : "waiting for Anomaly renderer route";
            return;
        }
        try { host.TryBind(Render, Completed); host.SetFrameEnabled(Config.Current.Enabled); host.SetFarSunEnabled(Config.Current.Enabled && Config.Current.RayTracedShadows); Status = host.Status; }
        catch (Exception e) { Status = "Anomaly binding: " + e.GetBaseException().Message; }
    }
    public void LoadAssets(string folder) { lock (renderGate) assetFolder = folder; }
    public void Update()
    {
        if (disposed) return;
        lock (renderGate)
        {
            ApplyRoute();
            if (backend is HardwareVulkan hardware) { SubmitHardware(hardware); Status = hardware.Status; }
            else
            {
            host.SetFrameEnabled(Config.Current.Enabled); host.SetFarSunEnabled(Config.Current.Enabled && Config.Current.RayTracedShadows);
            if (!Config.Current.Enabled) { host.Release(); backend?.InvalidateHistory(); Status = "disabled"; }
            const string waiting = "; hardware acceleration waits for the Vulkan renderer";
            if (AnomalyRenderer.UseDx11Device && Config.Current.HardwareAcceleration && Status.IndexOf(waiting, StringComparison.Ordinal) < 0)
                Status += waiting;
            }
            if (Interlocked.Exchange(ref historyReset, 0) != 0)
            {
                backend?.InvalidateHistory(); (backend as ISoftwareDx11Session)?.ResetBudget(); adaptive.Reset(); resets++;
                lastReset = "settings or user request";
            }
        }
        ConfigStorage.FlushPending();
        long revision = Interlocked.Read(ref settingsRevision);
        bool refreshSettings = revision != synchronizedRevision && DateTime.UtcNow.Ticks - Interlocked.Read(ref settingsChangedAt) >= TimeSpan.FromMilliseconds(400).Ticks;
        if (refreshSettings) { settings.RefreshValues(); synchronizedRevision = revision; }
        bool refreshStatus = DateTime.UtcNow.Ticks >= nextDiagnostics;
        if (refreshStatus) { nextDiagnostics = DateTime.UtcNow.AddSeconds(1).Ticks; PublishDiagnostics(); }
        terminal.Update(refreshSettings, refreshStatus);
    }
    void Render(object[] frame)
    {
        lock (renderGate)
        {
            if (disposed) return;
            frames++; lastFrameAt = DateTime.UtcNow.Ticks; lastFrame = SceneAssembler.FrameIndex(frame); epoch = (int)frame[1];
            var config = Config.Current.Snapshot();
            if (!config.Enabled) { backend?.InvalidateHistory(); Status = "disabled"; return; }
            if (!host.Claim()) { fallbacks++; backend?.InvalidateHistory(); Status = "native fallback: opaque lighting ownership conflict"; return; }
            bool rendered = false;
            try
            {
                if (!config.AllowLightBlockOffsets && ((float[])frame[6]).Length > 0 && !ShadowSettings.HasPhysicalOrigins(frame))
                {
                    backend?.InvalidateHistory();
                    Status = "native fallback: update Anomaly for physical light origins, or allow light block offsets";
                    return;
                }
                ulong signature = SceneAssembler.Signature(frame);
                if (build != null && build.IsCompleted)
                {
                    var completed = build; build = null;
                    if (completed.IsFaulted) throw completed.Exception.GetBaseException();
                    var result = completed.Result; if (result.Signature == signature) scene = result;
                }
                if (scene == null || scene.Signature != signature)
                {
                    if (build == null)
                    {
                        builds++;
                        build = Task.Run(() =>
                        {
                            var watch = Stopwatch.StartNew();
                            try { return SceneAssembler.Build(frame); }
                            finally { Interlocked.Exchange(ref buildTicks, watch.ElapsedTicks); }
                        });
                    }
                    Status = "native fallback: building scene acceleration"; return;
                }
                if (!Directory.Exists(Path.Combine(assetFolder, "Shaders"))) { Status = "native fallback: shader assets unavailable"; return; }
                bool vulkan = frame[7] is object[] devices && devices.Length > 0 && "vulkan".Equals(devices[0]);
                if (backend != null && (backend is SoftwareVulkan) != vulkan) { backend.Dispose(); backend = null; }
                if (backend == null) backend = vulkan ? new SoftwareVulkan(host, () => assetFolder, false, vulkanRayQuery) : SoftwareBackend.TryCreate(host, () => assetFolder);
                if (backend == null) { Status = "native fallback: DX11 ray backend unavailable"; return; }
                backend.SetShadowSettings(config.Shadows);
                if (lastExposure != config.StorageExposure) { backend.InvalidateHistory(); lastExposure = config.StorageExposure; }
                rendered = backend.Render(frame, scene, Math.Min(config.SamplingScale, adaptive.Scale), config.StorageExposure);
                if (rendered) composed++;
                Status = backend.Status;
            }
            catch (Exception e)
            {
                backend?.Reset(); build = null; scene = null;
                host.Release();
                string message = e.GetBaseException().Message;
                lastError = message; resets++; lastReset = "render exception";
                if (Status != "native fallback: " + message) MyLog.Default.Warning("SE-RT: " + message);
                Status = "native fallback: " + message;
                if (IsDeviceRemoved(e)) throw;
            }
            finally { if (!rendered) fallbacks++; }
        }
    }
    void Completed(object context)
    {
        lock (renderGate)
        {
            if (context == null) { backend?.Reset(); scene = null; adaptive.Reset(); resets++; lastReset = "Anomaly session/device lifecycle"; lastFrameAt = 0; Status = "waiting for a new Anomaly frame"; return; }
            if (backend is ISoftwareDx11Session software)
            {
                float target=Config.Current.TargetMilliseconds;
                software.Poll(context, combined => { software.ObserveBudget(target); if (software.RefinementExhausted || combined < target) adaptive.Observe(combined, target); });
            }
        }
    }
    public void Dispose()
    {
        Config.Current.PropertyChanged -= ConfigChanged;
        terminal?.Dispose();
        lock (renderGate) { disposed = true; host.Dispose(); backend?.Dispose(); backend = null; scene = null; Instance = null; }
        SatelliteAssembly.Reset();
        ConfigStorage.FlushPending(true);
    }
    public void OpenConfigDialog() { settings.SetLayout<Simple>(); MyGuiSandbox.AddScreen(settings.Dialog); }
    void ConfigChanged(object sender, PropertyChangedEventArgs e)
    {
        Interlocked.Exchange(ref settingsChangedAt, DateTime.UtcNow.Ticks);
        Interlocked.Increment(ref settingsRevision);
        Interlocked.Exchange(ref historyReset, 1);
    }
    public void RequestHistoryReset() => Interlocked.Exchange(ref historyReset, 1);
    public void ShowStatus() => MyGuiSandbox.AddScreen(new StatusScreen(() => CaptureDiagnostics().ToReport()));
    public void LogStatus() => MyLog.Default.WriteLine(CaptureDiagnostics().ToReport());
    void PublishDiagnostics() => diagnostics = CaptureDiagnostics();
    RendererDiagnostics CaptureDiagnostics()
    {
        lock (renderGate)
        {
            var config = Config.Current.Snapshot();
            var data = new RendererDiagnostics
            {
                State = Status, Host = host.Status, Terminal = terminal?.Status ?? "not initialized", Persistence = ConfigStorage.Status,
                Enabled = config.Enabled, RequestedScale = config.SamplingScale, AdaptiveScale = adaptive.Scale,
                Exposure = config.StorageExposure, Target = config.TargetMilliseconds,
                Shadows = config.RayTracedShadows, AllowLightBlockOffsets = config.AllowLightBlockOffsets, ShadowBias = config.ShadowBias, ShadowDebugView = config.ShadowDebugView, LampRadius = config.LampRadius, SolarDegrees = config.SolarAngularRadius, GlassTransmission = config.GlassTransmission, BiasMode = config.ShadowBiasMode, SamplingMode = config.ShadowSamplingMode,
                HardwareAcceleration = config.HardwareAcceleration, Route = AnomalyRenderer.ActiveName, RayQuery = rayQuery, HardwareStatus = hardwareStatus, RaySupported = rayQuery ? 1u : 0u,
                Frame = lastFrame, Epoch = epoch, Frames = frames, Composed = composed, Fallbacks = fallbacks,
                Resets = resets, LastReset = lastReset, LastError = lastError, Builds = builds,
                BuildState = build == null ? scene == null ? "not ready" : "ready" : build.IsCompleted ? "awaiting acceptance" : "running",
                BuildMilliseconds = Interlocked.Read(ref buildTicks) == 0 ? double.NaN : Interlocked.Read(ref buildTicks) * 1000d / Stopwatch.Frequency,
                FrameAgeSeconds = lastFrameAt == 0 ? double.NaN : (DateTime.UtcNow.Ticks - lastFrameAt) / (double)TimeSpan.TicksPerSecond,
                Vertices = scene?.Vertices.Length ?? 0, Triangles = scene?.Triangles.Length ?? 0, Nodes = scene?.Nodes.Length ?? 0,
                Instances = scene?.Instances.Length ?? 0, Materials = scene?.Materials.Length ?? 0, Poses = scene?.Poses.Length ?? 0
            };
            (backend as ISoftwareDx11Session)?.FillDiagnostics(data);
            return data;
        }
    }

    static bool IsDeviceRemoved(Exception e)
    {
        if ((e.GetType().FullName ?? "") != "SharpDX.SharpDXException")
            return false;
        var code = e.GetType().GetProperty("ResultCode")?.GetValue(e);
        var value = code?.GetType().GetProperty("Code")?.GetValue(code);
        return value is int n && (n == unchecked((int)0x887A0005) || n == unchecked((int)0x887A0006));
    }
}

static class SoftwareBackend
{
    public static IRayTracingBackend TryCreate(AnomalyHost host, Func<string> assets)
    {
        if (!AnomalyRenderer.AllowDx11Device)
            return null;
        string path = SatelliteAssembly.Find("SERT.Dx11.dll", typeof(SoftwareBackend).Assembly);
        if (!File.Exists(path))
            return null;
        var type = SatelliteAssembly.Load(path, typeof(SoftwareBackend).Assembly).GetType("SERT.Rendering.SoftwareDx11");
        return type == null ? null : (IRayTracingBackend)Activator.CreateInstance(type, host, assets, false);
    }
}
