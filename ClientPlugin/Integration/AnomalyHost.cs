using System;
using System.Reflection;

namespace SERT.Integration;

public sealed class AnomalyHost : IDisposable
{
    const string Id = "sert";
    MethodInfo unregister, compile, claim, commit, release, setFrameEnabled, setFarSunEnabled;
    readonly Func<string, int, byte[]> compiler;
    public AnomalyHost(Func<string, int, byte[]> compiler = null) { this.compiler = compiler; }
    public bool Bound { get; private set; }
    public string Status { get; private set; } = "waiting for Anomaly RayScene API v1";
    public bool TryBind(Action<object[]> frame, Action<object> completed = null)
    {
        if (Bound) return true;
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            var scene = assembly.GetType("ClientPlugin.Shaders.RayScene", false);
            if (scene == null) continue;
            var ownership = assembly.GetType("ClientPlugin.Shaders.OpaqueLightingOwnership", false);
            if (ownership == null) { Status = "Anomaly opaque-lighting contract missing"; return false; }
            compile = scene.GetMethod("CompileShader", BindingFlags.Public | BindingFlags.Static);
            unregister = scene.GetMethod("Unregister", BindingFlags.Public | BindingFlags.Static);
            setFarSunEnabled = scene.GetMethod("SetFarSunEnabled", BindingFlags.Public | BindingFlags.Static);
            setFrameEnabled = scene.GetMethod("SetFrameEnabled", BindingFlags.Public | BindingFlags.Static);
            claim = ownership.GetMethod("Claim"); commit = ownership.GetMethod("Commit"); release = ownership.GetMethod("Release");
            var register = scene.GetMethod("Register");
            if (compile == null || unregister == null || claim == null || commit == null || release == null || register == null)
            { Status = "incompatible Anomaly; update the framework"; return false; }
            Bound = register.Invoke(null, new object[] { Id, 1, frame }) is true;
            if (Bound && completed != null) scene.GetMethod("RegisterCompleted")?.Invoke(null, new object[] { Id, completed });
            Status = Bound ? "Anomaly RayScene API v1 connected" : "Anomaly rejected RayScene API v1";
            return Bound;
        }
        return false;
    }
    public byte[] Compile(string path, int profile) => compiler != null ? compiler(path, profile) : (byte[])compile.Invoke(null, new object[] { path, profile });
    public bool Claim() => claim != null && claim.Invoke(null, new object[] { Id, 7 }) is true;
    public bool Commit() => commit != null && commit.Invoke(null, new object[] { Id }) is true;
    public void SetFrameEnabled(bool enabled) { if (Bound) setFrameEnabled?.Invoke(null, new object[] { Id, enabled }); }
    public void SetFarSunEnabled(bool enabled) { if (Bound) setFarSunEnabled?.Invoke(null, new object[] { Id, enabled }); }
    public void Release() { release?.Invoke(null, new object[] { Id }); }
    public void Unbind()
    {
        if (!Bound) return;
        unregister?.Invoke(null, new object[] { Id });
        Bound = false;
        Status = "RayScene unregistered";
    }
    public void Dispose() { if (Bound) unregister?.Invoke(null, new object[] { Id }); Bound = false; }
}
