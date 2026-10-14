using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using ClientPlugin.Settings;
using ClientPlugin.Settings.Elements;
namespace ClientPlugin;
public class Config : INotifyPropertyChanged
{
    public readonly string Title = "SE-RT";
    bool enabled;
    readonly object gate = new object();
    float scale = .5f, target = 4, exposure = 1;
    bool rayTracedShadows = true, allowLightBlockOffsets, hardwareAcceleration;
    float shadowBias = .002f, lampRadius = .025f, solarDegrees = .266f, refinementFraction = .05f, glassTransmission = .9f;
    SERT.Core.ShadowBiasMode biasMode;
    SERT.Core.ShadowSamplingMode sampling;
    int samplingSeed;
    SERT.Core.ShadowDebugView shadowDebugView;
    public static readonly Config Default = new Config();
    public static readonly Config Current = ConfigStorage.Load();
    [Checkbox(description: "Experimental opaque lighting. Requires Anomaly RayScene v1; uncovered surfaces retain native lighting.")]
    public bool Enabled { get { lock (gate) return enabled; } set => Set(ref enabled, value); }
    [Checkbox(description: "Arms only when the operational renderer is Vulkan and the ray-query API is present. Saved while another renderer is active; no second device is opened.")]
    public bool HardwareAcceleration { get { lock (gate) return hardwareAcceleration; } set => Set(ref hardwareAcceleration, value); }
    [Slider(.25f, .5f, .25f, SliderAttribute.SliderType.Float, description: "Ray sampling dimensions relative to the internal render resolution.")]
    public float SamplingScale { get { lock (gate) return scale; } set => Set(ref scale, SERT.Core.Budget.Finite(value) && value < .375f ? .25f : .5f); }
    [Slider(1, 16, 1, SliderAttribute.SliderType.Float, description: "GPU time target in milliseconds. A target, not a measured performance guarantee.")]
    public float TargetMilliseconds { get { lock (gate) return target; } set => Set(ref target, SERT.Core.Budget.Finite(value) ? Math.Max(1, Math.Min(16, value)) : 4); }
    [Slider(.0625f, 4, .0625f, SliderAttribute.SliderType.Float, description: "Exposure scale for FP16 transport. Changing this clears history.")]
    public float StorageExposure { get { lock (gate) return exposure; } set => Set(ref exposure, SERT.Core.Budget.Finite(value) ? Math.Max(.0625f, Math.Min(4, value)) : 1); }
    [Checkbox(description: "Trace opaque shadow visibility for detected point, spot, emissive, proxy, environment and sun sources. Off is an unshadowed local-light comparison; native distant sun coverage remains.")]
    public bool RayTracedShadows { get { lock (gate) return rayTracedShadows; } set => Set(ref rayTracedShadows, value); }
    [Checkbox(description: "Allow the game's point-light block offset. Default off traces and shades from the physical light actor position.")]
    public bool AllowLightBlockOffsets { get { lock (gate) return allowLightBlockOffsets; } set => Set(ref allowLightBlockOffsets, value); }
    [Slider(.00025f, .01f, .00025f, SliderAttribute.SliderType.Float, description: "Receiver minimum bias in metres for Manual mode. Automatic derives numerical bounds. Independent of emitter size and light block offsets.")]
    public float ShadowBias { get { lock (gate) return shadowBias; } set => Set(ref shadowBias, SERT.Core.Budget.Finite(value) ? Math.Max(.00025f, Math.Min(.01f, value)) : .002f); }
    [Dropdown(description: "Lighting, sun/selected source visibility, coverage, blocker-guide quality, confidence/history, refinement, RGB transmission or far-sun coverage. Selected visibility describes one sampled source.")]
    public SERT.Core.ShadowDebugView ShadowDebugView { get { lock (gate) return shadowDebugView; } set => Set(ref shadowDebugView, Enum.IsDefined(typeof(SERT.Core.ShadowDebugView), value) ? value : SERT.Core.ShadowDebugView.Lighting); }
    [Slider(0, .25f, .005f, SliderAttribute.SliderType.Float, description: "Lamp emitter radius in metres; zero gives hard shadows. Brightness and physical center stay fixed.")]
    public float LampRadius { get { lock (gate) return lampRadius; } set => Set(ref lampRadius, Clamp(value, 0, .25f, .025f)); }
    [Slider(0, 1, .01f, SliderAttribute.SliderType.Float, description: "Sun angular radius in degrees; distant coverage availability is shown in Status.")]
    public float SolarAngularRadius { get { lock (gate) return solarDegrees; } set => Set(ref solarDegrees, Clamp(value, 0, 1, .266f)); }
    [Dropdown(description: "Automatic numerical surface offsets or the saved manual minimum bias.")]
    public SERT.Core.ShadowBiasMode ShadowBiasMode { get { lock (gate) return biasMode; } set => Set(ref biasMode, Enum.IsDefined(typeof(SERT.Core.ShadowBiasMode), value) ? value : SERT.Core.ShadowBiasMode.Automatic); }
    [Dropdown(description: "Blue-noise continuous samples or hashed samples for a comparison at equal ray counts.")]
    public SERT.Core.ShadowSamplingMode ShadowSamplingMode { get { lock (gate) return sampling; } set => Set(ref sampling, Enum.IsDefined(typeof(SERT.Core.ShadowSamplingMode), value) ? value : SERT.Core.ShadowSamplingMode.BlueNoise); }
    [Slider(0, 65535, 1, SliderAttribute.SliderType.Integer, description: "Deterministic sampling seed for repeatable comparisons.")]
    public int SamplingSeed { get { lock (gate) return samplingSeed; } set => Set(ref samplingSeed, Math.Max(0, Math.Min(65535, value))); }
    [Slider(0, .1f, .01f, SliderAttribute.SliderType.Float, description: "Maximum additional full-resolution receivers as a fraction of trace pixels. GPU timing can throttle this allowance.")]
    public float RefinementAllowance { get { lock (gate) return refinementFraction; } set => Set(ref refinementFraction, Clamp(value, 0, .1f, .05f)); }
    [Slider(0, 1, .01f, SliderAttribute.SliderType.Float, description: "Thin tinted glass transmission approximation; does not refract light or change visible glass rendering.")]
    public float GlassTransmission { get { lock (gate) return glassTransmission; } set => Set(ref glassTransmission, Clamp(value, 0, 1, .9f)); }
    static float Clamp(float value, float minimum, float maximum, float fallback) => SERT.Core.Budget.Finite(value) ? Math.Max(minimum, Math.Min(maximum, value)) : fallback;
    [Button(description: "Show renderer status and current resource use.")]
    public void RendererStatus() => Plugin.Instance?.ShowStatus();
    [Button(description: "Clear temporal reconstruction and restart the adaptive sampling budget on the next update.")]
    public void ResetHistory() => Plugin.Instance?.RequestHistoryReset();
    [Button(description: "Write a complete renderer snapshot to SpaceEngineers.log.")]
    public void LogStatus() => Plugin.Instance?.LogStatus();
    public event PropertyChangedEventHandler PropertyChanged;
    internal Config Snapshot() { lock (gate) return new Config { enabled = enabled, hardwareAcceleration = hardwareAcceleration, scale = scale, target = target, exposure = exposure,
        rayTracedShadows = rayTracedShadows, allowLightBlockOffsets = allowLightBlockOffsets, shadowBias = shadowBias, shadowDebugView = shadowDebugView, lampRadius = lampRadius, solarDegrees = solarDegrees, refinementFraction = refinementFraction, glassTransmission = glassTransmission, biasMode = biasMode, sampling = sampling, samplingSeed = samplingSeed }; }
    internal SERT.Core.ShadowSettings Shadows => new SERT.Core.ShadowSettings { Enabled = rayTracedShadows, AllowLightBlockOffsets = allowLightBlockOffsets, Bias = shadowBias, DebugView = shadowDebugView, LampRadius = lampRadius, SolarDegrees = solarDegrees, RefinementFraction = refinementFraction, GlassTransmission = glassTransmission, BiasMode = biasMode, Sampling = sampling, SamplingSeed = (uint)samplingSeed };
    void Set<T>(ref T field, T value, [CallerMemberName] string name = null)
    {
        lock (gate)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return;
            field = value;
        }
        ConfigStorage.MarkDirty(); PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
