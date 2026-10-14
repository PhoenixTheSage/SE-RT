using System;

namespace SERT.Core;

public enum ShadowDebugView { Lighting, SunVisibility, SelectedLightVisibility, RayCoverage, BlockerGuides, Confidence, Refinement, Transmission, FarSunCoverage }
public enum ShadowBiasMode { Automatic, Manual }
public enum ShadowSamplingMode { BlueNoise, Hashed }

public readonly struct SourceExtent
{
    public readonly float LampRadius, SolarRadius;
    public SourceExtent(float lampRadius, float solarDegrees) { LampRadius = lampRadius; SolarRadius = solarDegrees * (float)Math.PI / 180; }
    public float RadiusAt(float distance) => Math.Min(LampRadius, Math.Max(0, distance) * .25f);
}

public struct ShadowSettings
{
    public bool Enabled, AllowLightBlockOffsets;
    public float Bias;
    public ShadowDebugView DebugView;
    public ShadowBiasMode BiasMode;
    public ShadowSamplingMode Sampling;
    public float LampRadius, SolarDegrees, RefinementFraction, GlassTransmission;
    public uint SamplingSeed;
    public SourceExtent Extent => new SourceExtent(LampRadius, SolarDegrees);
    public static ShadowSettings Default => new ShadowSettings { Enabled = true, Bias = .002f, LampRadius = .025f,
        SolarDegrees = .266f, RefinementFraction = .05f, GlassTransmission = .9f };
    public static bool HasPhysicalOrigins(object[] frame) => frame.Length > 14 && frame[14] is float[] positions && positions.Length == ((float[])frame[6]).Length / 16 * 3;

    // Preserve the borrowed native records. Field 14 is the append-only physical origin channel.
    public static float[] NativeLights(object[] frame, bool allowOffsets)
    {
        var native = (float[])frame[6];
        if (allowOffsets || native.Length == 0) return native;
        if (!HasPhysicalOrigins(frame))
            throw new InvalidOperationException("Anomaly physical light origins missing; update Anomaly or enable light block offsets");
        var physical = (float[])frame[14];
        var result = (float[])native.Clone();
        for (int i = 0; i < native.Length / 16; i++)
            for (int axis = 0; axis < 3; axis++)
            {
                float value = physical[i * 3 + axis];
                if (!Budget.Finite(value)) throw new InvalidOperationException("Invalid physical light origin");
                result[i * 16 + axis] = value;
            }
        return result;
    }
}
