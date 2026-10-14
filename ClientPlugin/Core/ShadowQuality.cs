using System;
using System.Numerics;

namespace SERT.Core;

/// <summary>Float operation bounds for our barycentric reconstruction and affine transforms, not DXR constants.</summary>
public static class RayPrecision
{
    public const float Epsilon = 1.1920928955078125e-7f;
    public static float Gamma(int operations) => operations * Epsilon / (1 - operations * Epsilon);
    public static float PositionError(Vector3 a, Vector3 b, Vector3 c, Matrix4x4 world)
    {
        var magnitude = Vector3.Abs(a) + Vector3.Abs(b - a) + Vector3.Abs(c - a);
        var local = magnitude * Gamma(9);
        var propagated = new Vector3(
            Math.Abs(world.M11)*local.X + Math.Abs(world.M21)*local.Y + Math.Abs(world.M31)*local.Z,
            Math.Abs(world.M12)*local.X + Math.Abs(world.M22)*local.Y + Math.Abs(world.M32)*local.Z,
            Math.Abs(world.M13)*local.X + Math.Abs(world.M23)*local.Y + Math.Abs(world.M33)*local.Z);
        var transform = new Vector3(
            Math.Abs(world.M11)*magnitude.X + Math.Abs(world.M21)*magnitude.Y + Math.Abs(world.M31)*magnitude.Z + Math.Abs(world.M41),
            Math.Abs(world.M12)*magnitude.X + Math.Abs(world.M22)*magnitude.Y + Math.Abs(world.M32)*magnitude.Z + Math.Abs(world.M42),
            Math.Abs(world.M13)*magnitude.X + Math.Abs(world.M23)*magnitude.Y + Math.Abs(world.M33)*magnitude.Z + Math.Abs(world.M43));
        var error = propagated + transform * Gamma(5);
        return Math.Max(1e-6f, error.X + error.Y + error.Z);
    }
}

public sealed class RefinementBudget
{
    int expensive, cheap;
    public float Multiplier { get; private set; } = 1;
    public string Reason => Multiplier < 1 ? "GPU target throttling" : "requested allowance";
    public bool Observe(double milliseconds, double target)
    {
        if (double.IsNaN(milliseconds) || double.IsInfinity(milliseconds) || milliseconds < 0) return false;
        expensive = milliseconds > target ? expensive + 1 : 0;
        cheap = milliseconds < target * .8 ? cheap + 1 : 0;
        if (expensive >= 8 && Multiplier > 0) { Multiplier = Multiplier <= .125f ? 0 : Multiplier * .5f; expensive = cheap = 0; return true; }
        if (cheap >= 60 && Multiplier < 1) { Multiplier = Multiplier == 0 ? .125f : Math.Min(1, Multiplier * 2); expensive = cheap = 0; return true; }
        return false;
    }
    public int Capacity(int pixels, float fraction) => (int)Math.Floor(pixels * Math.Max(0, Math.Min(.1f, fraction)) * Multiplier);
    public void Reset() { expensive = cheap = 0; Multiplier = 1; }
}
