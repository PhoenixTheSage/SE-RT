using System;
using System.Numerics;

namespace SERT.Core;

public struct Reservoir
{
    public uint Sample, Revision;
    public Vector2 Parameters;
    public double WeightSum, Target, Count;
    public bool Valid => Target > 0 && Count > 0 && WeightSum > 0 && !double.IsInfinity(WeightSum);
    public void Add(uint sample, uint revision, Vector2 parameters, double target, double weight, double count, double random)
    {
        if (!(count > 0) || !(weight >= 0) || double.IsInfinity(weight) || double.IsNaN(weight)) return;
        Count += count;
        WeightSum += weight;
        if (weight > 0 && random * WeightSum < weight)
        { Sample = sample; Revision = revision; Parameters = parameters; Target = target; }
    }
    public void AddProposal(uint sample, uint revision, Vector2 parameters, double target, double pdf, double random)
    {
        if (!(pdf > 0) || double.IsInfinity(pdf) || double.IsNaN(pdf)) return;
        Add(sample, revision, parameters, target, Math.Max(0, target) / pdf, 1, random);
    }
    public void Merge(Reservoir source, double reevaluatedTarget, double random, double maxCount = 32)
    {
        if (!source.Valid) return;
        double count = Math.Min(source.Count, maxCount);
        Add(source.Sample, source.Revision, source.Parameters, reevaluatedTarget,
            source.EstimatorWeight * Math.Max(0, reevaluatedTarget) * count, count, random);
    }
    public double EstimatorWeight => Valid ? WeightSum / (Target * Count) : 0;
}

public sealed class Distribution
{
    readonly double[] cdf, pdf;
    public int Count => pdf.Length;
    public Distribution(double[] power, double uniformFraction = 0.05)
    {
        if (power == null) throw new ArgumentNullException(nameof(power));
        if (uniformFraction <= 0 || uniformFraction > 1) throw new ArgumentOutOfRangeException(nameof(uniformFraction));
        cdf = new double[power.Length]; pdf = new double[power.Length];
        double sum = 0;
        foreach (double p in power) if (p > 0 && !double.IsInfinity(p) && !double.IsNaN(p)) sum += p;
        double accumulated = 0;
        for (int i = 0; i < power.Length; ++i)
        {
            double p = power[i] > 0 && !double.IsInfinity(power[i]) && !double.IsNaN(power[i]) ? power[i] : 0;
            pdf[i] = sum > 0 ? (1 - uniformFraction) * p / sum + uniformFraction / power.Length : 1.0 / power.Length;
            cdf[i] = accumulated += pdf[i];
        }
        if (cdf.Length != 0) cdf[cdf.Length - 1] = 1;
    }
    public double Pdf(int index) => pdf[index];
    public int Sample(double random)
    {
        if (Count == 0) return -1;
        int lo = 0, hi = Count - 1;
        while (lo < hi) { int mid = (lo + hi) / 2; if (random < cdf[mid]) hi = mid; else lo = mid + 1; }
        return lo;
    }
}

public static class History
{
    public static bool Compatible(float depth, float previousDepth, Vector3 normal, Vector3 previousNormal,
        uint identity, uint previousIdentity, uint revision, uint previousRevision)
    {
        if (!Budget.Finite(depth) || !Budget.Finite(previousDepth) || depth <= 0 || previousDepth <= 0) return false;
        return identity == previousIdentity && revision == previousRevision &&
            Math.Abs(depth - previousDepth) <= Math.Max(0.02f, depth * 0.01f) &&
            Vector3.Dot(normal, previousNormal) >= 0.92f;
    }
    public static float FireflyLimit(float luminance, float currentNeighborMaximum)
    {
        if (!(currentNeighborMaximum > 0) || !Budget.Finite(currentNeighborMaximum)) return luminance;
        return Math.Min(luminance, currentNeighborMaximum * 12f);
    }
}

public sealed class AdaptiveBudget
{
    int expensive, cheap;
    public float Scale { get; private set; } = 0.5f;
    public bool Observe(double milliseconds, double target = 4)
    {
        if (double.IsNaN(milliseconds) || double.IsInfinity(milliseconds) || milliseconds < 0) return false;
        expensive = milliseconds > target * 1.15 ? expensive + 1 : 0;
        cheap = milliseconds < target * 0.55 ? cheap + 1 : 0;
        if (Scale == 0.5f && expensive >= 30) { Scale = 0.25f; expensive = cheap = 0; return true; }
        if (Scale == 0.25f && cheap >= 120) { Scale = 0.5f; expensive = cheap = 0; return true; }
        return false;
    }
    public void Reset() { Scale = 0.5f; expensive = cheap = 0; }
}
