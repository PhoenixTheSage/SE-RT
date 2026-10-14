using System;
using System.Globalization;
using System.Text;
using SERT.Core;

namespace SERT.Rendering;

/// <summary>CPU snapshot only. Reading diagnostics never maps GPU memory or flushes a context.</summary>
public sealed class RendererDiagnostics
{
    public string State = "starting", Backend = "not created", Host = "waiting", Terminal = "waiting", Persistence = "unknown";
    public string LastError = "none", BuildState = "not started", LastReset = "none";
    public bool Enabled, History;
    public float RequestedScale = .5f, AdaptiveScale = .5f, Exposure = 1, Target = 4;
    public double GpuMilliseconds = double.NaN, BuildMilliseconds = double.NaN, FrameAgeSeconds = double.NaN;
    public long Measurements, Frames, Composed, Fallbacks, Resets, Builds, SceneBytes, ReconstructionBytes;
    public double FarSunMilliseconds = double.NaN;
    public string FarSunCoverage = "native hybrid", RefinementReason = "pending";
    public float RefinementRequested, RefinementEffective, LampRadius = .025f, SolarDegrees = .266f, GlassTransmission = .9f;
    public ShadowBiasMode BiasMode;
    public ShadowSamplingMode SamplingMode;
    public uint Frame;
    public int Epoch, Width, Height, FullWidth, FullHeight;
    public int Vertices, Triangles, Nodes, Instances, Materials, Poses;
    public int NativeLights, EmissiveLights, ActiveProxies, LightCapacity, AtlasTiles, ReadyTiles;
    public bool Shadows = true, AllowLightBlockOffsets, HardwareAcceleration, RayQuery;
    public string Route = "pending", HardwareStatus = "hardware acceleration off";
    public uint RaySupported, RayShaderReady;
    public float ShadowBias = .002f;
    public ShadowDebugView ShadowDebugView;

    static string Number(double value, string format = "0.00") => double.IsNaN(value) || double.IsInfinity(value) ? "pending" : value.ToString(format, CultureInfo.InvariantCulture);
    static string MiB(long bytes) => (bytes / 1048576d).ToString("0.0", CultureInfo.InvariantCulture);
    public string RendererText => "State: " + State + "\nBackend: " + Backend + "; enabled: " + Enabled +
        "\nLast callback: " + Number(FrameAgeSeconds, "0.0") + " s ago; frame " + Frame + "; epoch " + Epoch;
    public string TimingText => "GPU: " + Number(GpuMilliseconds) + " ms; target " + Number(Target) + " ms; completed measurements " + Measurements +
        "\nShared far-sun pass: " + Number(FarSunMilliseconds) + " ms; combined " + Number(GpuMilliseconds + (double.IsNaN(FarSunMilliseconds) ? 0 : FarSunMilliseconds)) + " ms" +
        "\nSampling dimensions: requested " + Number(RequestedScale) + "; adaptive cap " + Number(AdaptiveScale) +
        "\nTrace " + Width + " x " + Height + "; internal render " + FullWidth + " x " + FullHeight;
    public string SceneText => "Instances " + Instances + "; vertices " + Vertices + "; triangles " + Triangles +
        "\nBVH nodes " + Nodes + "; materials " + Materials + "; bone poses " + Poses +
        "\nAcceleration worker: " + BuildState + "; builds " + Builds + "; last work " + Number(BuildMilliseconds) + " ms";
    public string LightsText => "Native point/spot " + NativeLights + "; emissive triangle candidates " + EmissiveLights +
        "\nActive emitter proxies " + ActiveProxies + "; total allocated candidates " + LightCapacity +
        "\nTexture atlas: " + ReadyTiles + " / " + AtlasTiles + " tiles baked (source versions may still be pending)";
    public string ReconstructionText => "Temporal history: " + (History ? "available for next frame" : "invalid / warming up") + "; storage exposure " + Number(Exposure) +
        "\nGPU scene " + MiB(SceneBytes) + " / " + MiB(Budget.SceneBytes) + " MiB; reconstruction " + MiB(ReconstructionBytes) + " / " + MiB(Budget.ReconstructionBytes) + " MiB";
    public string LifecycleText => "Callbacks " + Frames + "; composed " + Composed + "; native fallback callbacks " + Fallbacks +
        "\nExplicit history/lifecycle resets " + Resets + "; last reset: " + LastReset + "\nLast exception: " + LastError;
    public string IntegrationText => "Anomaly: " + Host + "\nRich HUD: " + Terminal + "\nSettings file: " + Persistence +
        "\nHardware acceleration: " + (HardwareAcceleration ? "saved on" : "saved off") + "; route " + Route +
        "; ray query " + (RayQuery ? "present" : "absent") + "; " + HardwareStatus;
    public string ShadowText => "Opaque shadow rays: " + (Shadows ? "enabled" : "disabled (comparison)") + "; source origins: " + (AllowLightBlockOffsets ? "native offsets allowed" : "physical positions") +
        "\nBias mode " + BiasMode + "; sampling " + SamplingMode + "; lamp radius " + Number(LampRadius, "0.000") + " m; sun radius " + Number(SolarDegrees, "0.000") + " degrees" +
        "\nSaved manual minimum bias " + Number(ShadowBias, "0.00000") + " m" + (BiasMode == ShadowBiasMode.Automatic ? " (inactive in Automatic)" : "") + "; debug view " + ShadowDebugView +
        "\nSun coverage: " + FarSunCoverage + "; thin glass transmission " + Number(GlassTransmission) +
        "\nRefinement allowance: requested " + Number(RefinementRequested * 100) + "% / effective cap " + Number(RefinementEffective * 100) + "%; " + RefinementReason +
        "\nBlocker guides: any-hit estimate in base samples; bounded nearest query in refinement. Unknown coverage keeps native lighting.";

    public string ToReport()
    {
        var text = new StringBuilder("SE-RT renderer diagnostics\nCaptured UTC: ");
        text.Append(DateTime.UtcNow.ToString("u", CultureInfo.InvariantCulture));
        foreach (var section in new[] { RendererText, TimingText, SceneText, LightsText, ReconstructionText, ShadowText, LifecycleText, IntegrationText })
            text.Append("\n\n").Append(section);
        text.Append("\n\nGPU timestamps arrive asynchronously and may describe an earlier frame. Buffers may remain allocated while disabled.");
        text.Append("\nSampling: ").Append(Budget.Proposals).Append(" direct proposals, ").Append(Budget.SpatialNeighbors).Append(" reuse neighbors; one diffuse/specular bounce, three spatial filter passes.");
        text.Append("\nRay statistics are disabled in production; candidate counts are not traced-ray or coverage measurements. Uncovered surfaces keep native lighting.");
        return text.ToString();
    }
}
