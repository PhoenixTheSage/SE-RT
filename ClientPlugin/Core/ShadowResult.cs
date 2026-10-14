using System.Numerics;
using System.Runtime.InteropServices;

namespace SERT.Core;

public enum BlockerGuideQuality : uint { Missing, ApproximateAnyHit, NearestCaptured }

// Shared transport result for software and future hardware adapters. Coverage is
// independent of guide quality: an approximate opaque hit still certifies zero.
[StructLayout(LayoutKind.Sequential,Pack=4)]
public struct ShadowResult
{
    public Vector3 Transmittance;
    public TraceStatus Coverage;
    public float BlockerDistance;
    public BlockerGuideQuality GuideQuality;
    public bool Certified => Coverage==TraceStatus.Miss||Coverage==TraceStatus.Hit;
}
