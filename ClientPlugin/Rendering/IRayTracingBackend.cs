using System;
using System.Runtime.CompilerServices;
using SERT.Core;

[assembly: InternalsVisibleTo("SERT.Dx11")]

namespace SERT.Rendering;

// Renderer adapters own acceleration and dispatch; transport and scene ABI stay independent of SE.
public interface IRayTracingBackend : IDisposable
{
    string Name { get; }
    string Status { get; }
    long SceneBytes { get; }
    long ReconstructionBytes { get; }
    bool Render(object[] frame, SceneGeometry geometry, float scale, float exposure);
    void SetShadowSettings(ShadowSettings settings);
    void InvalidateHistory();
    void Reset();
}

/// <summary>DX11 software session. The satellite assembly implements this; the main plugin does not reference SharpDX.</summary>
public interface ISoftwareDx11Session
{
    bool RefinementExhausted { get; }
    void ResetBudget();
    void ObserveBudget(double target);
    void Poll(object context, Action<double> measured);
    void FillDiagnostics(RendererDiagnostics data);
}

public enum BackendKind { SoftwareDx11, HardwareDxr, HardwareVulkan }

public readonly struct HardwareDecision
{
    public HardwareDecision(bool trace, BackendKind kind, string status)
    {
        Trace = trace; Kind = kind; Status = status;
    }
    public bool Trace { get; }
    public BackendKind Kind { get; }
    public string Status { get; }
}

/// <summary>Saved option, Anomaly route, and ray-query support. No second device is opened.</summary>
public static class HardwareChoice
{
    public const int Pending = 0;
    public const int DirectX11 = 1;
    public const int Vulkan = 2;

    public static HardwareDecision Select(bool option, int route, bool rayQuery) => Select(option, route, rayQuery, true);

    public static HardwareDecision Select(bool option, int route, bool rayQuery, bool enabled)
    {
        if (!enabled)
            return new HardwareDecision(false, route == DirectX11 ? BackendKind.SoftwareDx11 : BackendKind.HardwareVulkan, "disabled");
        if (route == DirectX11)
            return new HardwareDecision(true, BackendKind.SoftwareDx11, option
                ? "software DX11; hardware acceleration waits for the Vulkan renderer"
                : "software DX11");
        if (route != Vulkan || !option)
            return new HardwareDecision(false, BackendKind.HardwareVulkan, route == Pending
                ? "waiting for Anomaly renderer route"
                : "hardware acceleration off");
        if (!rayQuery)
            return new HardwareDecision(false, BackendKind.HardwareVulkan, "hardware acceleration missing ray query");
        return new HardwareDecision(true, BackendKind.HardwareVulkan, "hardware ray query");
    }
}

public static class BackendCapabilities
{
    public static bool Available(BackendKind backend, bool option, int route, bool rayQuery)
    {
        var decision = HardwareChoice.Select(option, route, rayQuery);
        return decision.Trace && decision.Kind == backend;
    }
}
