using System;
using System.Linq;
using System.Reflection;
using SERT.Rendering;
using VRage.Utils;

namespace SERT.Integration;

/// <summary>Uses Anomaly's terminal contract without a second Rich HUD client or assembly dependency.</summary>
internal sealed class AnomalyTerminalHook : IDisposable
{
    const string Folder = "SE-RT";
    MethodInfo request, unregister;
    PropertyInfo hudAvailable, registryError;
    object settings, status;
    long nextAttempt;
    string lastReport;
    bool pendingSettingsRefresh;
    readonly Func<RendererDiagnostics> diagnostics;
    readonly Action show, reset, log;
    public bool Installed => settings != null && status != null;
    public string Status { get; private set; } = "waiting for Anomaly terminal registry";

    public AnomalyTerminalHook(Func<RendererDiagnostics> diagnostics, Action show, Action reset, Action log)
    { this.diagnostics = diagnostics; this.show = show; this.reset = reset; this.log = log; }

    public void Update(bool refreshSettings, bool refreshStatus)
    {
        pendingSettingsRefresh |= refreshSettings;
        try
        {
            if (!Installed)
            {
                if (DateTime.UtcNow.Ticks < nextAttempt) return;
                nextAttempt = DateTime.UtcNow.AddSeconds(1).Ticks;
                if (request == null)
                {
                    foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        var registry = assembly.GetType("ClientPlugin.RichHud.TerminalConfigRegistry", false);
                        if (registry == null) continue;
                        request = registry.GetMethod("RequestFolderPage", new[] { typeof(string), typeof(string) });
                        unregister = registry.GetMethod("UnregisterPage", new[] { typeof(string) });
                        if (request != null && unregister != null)
                        {
                            hudAvailable = assembly.GetType("ClientPlugin.RichHud.RichHudSupport", false)?.GetProperty("Available", BindingFlags.Public | BindingFlags.Static);
                            registryError = registry.GetProperty("LastError", BindingFlags.Public | BindingFlags.Static);
                            break;
                        }
                        request = null;
                    }
                }
                if (request == null) return;
                settings = request.Invoke(null, new object[] { Folder, "Settings" });
                status = request.Invoke(null, new object[] { Folder, "Status" });
                if (!Installed) { RemovePages(); Status = "terminal pages unavailable; retrying"; return; }
                PopulateSettings(); PopulateStatus();
                pendingSettingsRefresh = refreshStatus = true;
            }
            if (pendingSettingsRefresh) { Call(settings, "Refresh"); pendingSettingsRefresh = false; }
            if (refreshStatus)
            {
                string error = registryError?.GetValue(null) as string;
                Status = !string.IsNullOrEmpty(error) ? "Anomaly terminal: " + error : hudAvailable?.GetValue(null) is false
                    ? "pages queued; waiting for world / Rich HUD Master (1965654081)"
                    : "registered: Anomaly Shaders / SE-RT";
                var data = diagnostics();
                string report = data.RendererText + data.TimingText + data.SceneText + data.LightsText + data.ReconstructionText + data.ShadowText + data.LifecycleText + data.IntegrationText;
                if (report != lastReport) { Call(status, "Refresh"); lastReport = report; }
            }
        }
        catch (Exception e)
        {
            string message = "terminal hook: " + e.GetBaseException().Message;
            if (Status != message) VRage.Utils.MyLog.Default.Warning("SE-RT " + message);
            Status = message;
            // A partial registration must be removed before retrying; otherwise controls duplicate.
            if (!Installed || lastReport == null)
                try { RemovePages(); } catch { settings = status = null; }
        }
    }

    void PopulateSettings()
    {
        Call(settings, "Category", "Opaque lighting");
        Call(settings, "Checkbox", "Enabled", (Func<bool>)(() => ClientPlugin.Config.Current.Enabled),
            (Action<bool>)(v => ClientPlugin.Config.Current.Enabled = v), "Experimental ray/path tracing. Uncovered surfaces retain native lighting.");
        Call(settings, "Slider", "Sampling scale", .25f, .5f, (Func<float>)(() => ClientPlugin.Config.Current.SamplingScale),
            (Action<float>)(v => ClientPlugin.Config.Current.SamplingScale = v), "Requested trace dimensions: quarter or half. Adaptive budget can reduce half to quarter.", .25f);
        Call(settings, "Slider", "GPU target (ms)", 1f, 16f, (Func<float>)(() => ClientPlugin.Config.Current.TargetMilliseconds),
            (Action<float>)(v => ClientPlugin.Config.Current.TargetMilliseconds = v), "Adaptive sampling target; actual asynchronous GPU timing is shown on Status.", 1f);
        Call(settings, "Slider", "Storage exposure", .0625f, 4f, (Func<float>)(() => ClientPlugin.Config.Current.StorageExposure),
            (Action<float>)(v => ClientPlugin.Config.Current.StorageExposure = v), "FP16 transport scale. Changing this clears temporal history.", .0625f);
        Call(settings, "Category", "Ray-traced shadows");
        Call(settings, "Checkbox", "Ray-traced shadows", (Func<bool>)(() => ClientPlugin.Config.Current.RayTracedShadows),
            (Action<bool>)(v => ClientPlugin.Config.Current.RayTracedShadows = v), "Opaque visibility for sun, detected lamps, emissives and emitter proxies. Off is an unshadowed local-light comparison.");
        Call(settings, "Checkbox", "Allow light block offsets", (Func<bool>)(() => ClientPlugin.Config.Current.AllowLightBlockOffsets),
            (Action<bool>)(v => ClientPlugin.Config.Current.AllowLightBlockOffsets = v), "Default off: use physical light actor positions. On: restore the game's native point offset.");
        Call(settings, "Slider", "Shadow surface bias (m)", .00025f, .01f, (Func<float>)(() => ClientPlugin.Config.Current.ShadowBias),
            (Action<float>)(v => ClientPlugin.Config.Current.ShadowBias = v), "Manual-mode receiver minimum. Automatic derives numerical bounds; independent of emitter size and light offsets.", .00025f);
        Call(settings, "Dropdown", "Shadow debug view", typeof(SERT.Core.ShadowDebugView), (Func<object>)(() => ClientPlugin.Config.Current.ShadowDebugView),
            (Action<object>)(v => ClientPlugin.Config.Current.ShadowDebugView = (SERT.Core.ShadowDebugView)v), "Source visibility, coverage, blocker quality, confidence/history, refinement, RGB sun transmission and far-sun coverage.");
        Call(settings, "Slider", "Lamp emitter radius (m)", 0f, .25f, (Func<float>)(() => ClientPlugin.Config.Current.LampRadius),
            (Action<float>)(v => ClientPlugin.Config.Current.LampRadius = v), "Centered on the physical light position; zero restores hard shadows.", .005f);
        Call(settings, "Slider", "Sun angular radius (degrees)", 0f, 1f, (Func<float>)(() => ClientPlugin.Config.Current.SolarAngularRadius),
            (Action<float>)(v => ClientPlugin.Config.Current.SolarAngularRadius = v), "Soft local sun shadows with shared distant coverage when available.", .01f);
        Call(settings, "Slider", "Refinement allowance", 0f, .1f, (Func<float>)(() => ClientPlugin.Config.Current.RefinementAllowance),
            (Action<float>)(v => ClientPlugin.Config.Current.RefinementAllowance = v), "Sparse extra receivers; GPU timing may throttle this cap.", .01f);
        Call(settings, "Slider", "Thin glass transmission", 0f, 1f, (Func<float>)(() => ClientPlugin.Config.Current.GlassTransmission),
            (Action<float>)(v => ClientPlugin.Config.Current.GlassTransmission = v), "Colored attenuation on supported glass; no refraction.", .01f);
        Call(settings, "Dropdown", "Shadow bias mode", typeof(SERT.Core.ShadowBiasMode), (Func<object>)(() => ClientPlugin.Config.Current.ShadowBiasMode),
            (Action<object>)(v => ClientPlugin.Config.Current.ShadowBiasMode = (SERT.Core.ShadowBiasMode)v), "Automatic numerical offsets or saved manual bias.");
        Call(settings, "Dropdown", "Shadow sampling mode", typeof(SERT.Core.ShadowSamplingMode), (Func<object>)(() => ClientPlugin.Config.Current.ShadowSamplingMode),
            (Action<object>)(v => ClientPlugin.Config.Current.ShadowSamplingMode = (SERT.Core.ShadowSamplingMode)v), "Blue noise or hashed comparison with equal ray counts.");
        Call(settings, "Slider", "Sampling seed", 0f, 65535f, (Func<float>)(() => ClientPlugin.Config.Current.SamplingSeed), (Action<float>)(v => ClientPlugin.Config.Current.SamplingSeed = (int)v), "Fixed seed for repeatable comparisons.", 1f);
    }

    void PopulateStatus()
    {
        Section("Renderer", d => d.RendererText);
        Section("Timing and sampling", d => d.TimingText);
        Section("Scene acceleration", d => d.SceneText);
        Section("Lighting and texture readiness", d => d.LightsText);
        Section("Reconstruction and GPU memory", d => d.ReconstructionText);
        Section("Shadow tracing", d => d.ShadowText);
        Section("Lifecycle and failures", d => d.LifecycleText);
        Section("Integration and persistence", d => d.IntegrationText);
        Call(status, "Label", "CPU snapshots refresh once per second. Ray statistics are disabled; candidate counts do not measure coverage.");
        Buttons(status);
    }

    void Section(string title, Func<RendererDiagnostics, string> get)
    {
        Call(status, "Category", title);
        Call(status, "Columns", 1);
        Call(status, "Label", title, (Func<string>)(() => get(diagnostics())));
    }

    void Buttons(object page)
    {
        Call(page, "Button", "Show Status", show, "Open a scrollable snapshot with a Refresh button.");
        Call(page, "Button", "Reset history", reset, "Clear temporal history and restart adaptive sampling on the next update.");
        Call(page, "Button", "Write report to log", log, "Write the full snapshot to SpaceEngineers.log for troubleshooting.");
    }

    static void Call(object page, string name, params object[] args)
    {
        var method = page.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance).FirstOrDefault(m =>
            m.Name == name && !m.IsGenericMethodDefinition && m.GetParameters().Length == args.Length &&
            m.GetParameters().Select((p, i) => args[i] == null ? !p.ParameterType.IsValueType : p.ParameterType.IsInstanceOfType(args[i])).All(match => match));
        if (method == null) throw new MissingMethodException(page.GetType().FullName, name);
        method.Invoke(page, args);
    }

    void RemovePages()
    {
        if (settings != null) unregister?.Invoke(null, new object[] { Folder + "/Settings" });
        if (status != null) unregister?.Invoke(null, new object[] { Folder + "/Status" });
        settings = status = null; lastReport = null;
    }

    public void Dispose() { try { RemovePages(); } catch (Exception e) { Status = e.GetBaseException().Message; } }
}
