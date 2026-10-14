using System;
using SharpDX.Direct3D11;
namespace SERT.Rendering;

internal sealed class GpuTimer : IDisposable
{
    readonly Device device;
    readonly Sample[] samples = new Sample[4];
    Sample active;
    int write;
    public double Milliseconds { get; private set; } = double.NaN;
    public long Measurements { get; private set; }
    public GpuTimer(Device device) { this.device = device; }
    public void Poll(DeviceContext rc, Action<double> measured = null)
    {
        if (rc.TypeInfo != DeviceContextType.Immediate) return;
        for (int offset=0;offset<samples.Length;offset++)
        {
            var s=samples[(write+offset)%samples.Length];
            if (s == null || !s.Pending) continue;
            if (!rc.GetData(s.Disjoint, AsynchronousFlags.DoNotFlush, out QueryDataTimestampDisjoint period) ||
                !rc.GetData(s.End, AsynchronousFlags.DoNotFlush, out long end) || !rc.GetData(s.Start, AsynchronousFlags.DoNotFlush, out long start)) continue;
            s.Pending = false;
            if (period.Disjoint || period.Frequency <= 0 || end < start) continue;
            Milliseconds = (end - start) * 1000.0 / period.Frequency; Measurements++;
            measured?.Invoke(Milliseconds);
        }
    }
    public void Begin(DeviceContext rc)
    {
        if (active != null) throw new InvalidOperationException("GPU measurement already active");
        var s = samples[write] ?? (samples[write] = new Sample(device));
        if (s.Pending) return;
        write = (write + 1) % samples.Length; active = s; rc.Begin(s.Disjoint); rc.End(s.Start);
    }
    public void End(DeviceContext rc)
    {
        if (active == null) return;
        rc.End(active.End); rc.End(active.Disjoint); active.Pending = true; active = null;
    }
    public void Dispose() { foreach (var s in samples) s?.Dispose(); }
    sealed class Sample : IDisposable
    {
        public Query Start, End, Disjoint;
        public bool Pending;
        public Sample(Device d)
        {
            try { Start = new Query(d, new QueryDescription { Type = QueryType.Timestamp }); End = new Query(d, new QueryDescription { Type = QueryType.Timestamp }); Disjoint = new Query(d, new QueryDescription { Type = QueryType.TimestampDisjoint }); }
            catch { Dispose(); throw; }
        }
        public void Dispose() { Start?.Dispose(); End?.Dispose(); Disjoint?.Dispose(); }
    }
}
