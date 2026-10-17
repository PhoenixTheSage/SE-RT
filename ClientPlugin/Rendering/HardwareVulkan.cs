using SERT.Core;
using SERT.Integration;

namespace SERT.Rendering;

/// <summary>
/// Submits ray options to the renderer's own device. Geometry stays in that device.
/// </summary>
public sealed class HardwareVulkan : IRayTracingBackend
{
    string status = "hardware ray query; not the software temporal stack";

    public string Name => "HardwareVulkan";
    public string Status => status;
    public long SceneBytes => 0;
    public long ReconstructionBytes => 0;

    public void Apply(bool enabled, bool shadows, float exposure, float bias, float solarRadians)
    {
        VulkanRayApi.Set(enabled, enabled && shadows, exposure, bias, solarRadians);
        if (!enabled) { status = "disabled"; return; }
        var caps = VulkanRayApi.Query();
        status = caps.RayQuery == 0
            ? "hardware acceleration missing ray query"
            : caps.ShaderReady == 0
                ? "hardware ray query; shader not ready; not the software temporal stack"
                : "hardware ray query; not the software temporal stack";
    }

    public bool Render(object[] frame, SceneGeometry geometry, float scale, float exposure) => VulkanRayApi.ExportsPresent;

    public void SetShadowSettings(ShadowSettings settings) { }
    public void InvalidateHistory() { }
    public void Reset() { }
    public void Dispose() => VulkanRayApi.Disable();
}
