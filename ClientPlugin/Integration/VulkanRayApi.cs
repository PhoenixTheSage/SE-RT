using System;
using System.Runtime.InteropServices;

namespace SERT.Integration;

/// <summary>
/// Resolves the renderer's already-loaded ray exports. Missing exports fail closed.
/// </summary>
internal static class VulkanRayApi
{
    const uint Abi = 1;
    static readonly object Gate = new object();
    static GetCapabilities GetCaps;
    static SetOptions SetOpts;
    static bool resolved, present;

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    public struct Capabilities
    {
        public uint Size, AbiVersion, Supported, RayQuery;
        public uint MaxPrimitives, MaxInstances, ShaderReady, Refused;
        public ulong Builds, Dispatches;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    public struct Options
    {
        public uint Size, AbiVersion, Enable, Shadows;
        public float Exposure, ShadowBias, SolarRadians;
        public uint Reserved;
    }

    public static bool ExportsPresent { get { Resolve(); return present; } }

    public static Capabilities Query()
    {
        Resolve();
        var caps = new Capabilities { Size = (uint)Marshal.SizeOf<Capabilities>(), AbiVersion = Abi };
        if (!present || GetCaps(IntPtr.Zero, ref caps) != 0)
        {
            caps.Supported = 0;
            caps.RayQuery = 0;
        }
        return caps;
    }

    public static void Set(bool enable, bool shadows, float exposure, float bias, float solarRadians)
    {
        Resolve();
        if (!present)
            return;
        var options = new Options
        {
            Size = (uint)Marshal.SizeOf<Options>(),
            AbiVersion = Abi,
            Enable = enable ? 1u : 0u,
            Shadows = shadows ? 1u : 0u,
            Exposure = exposure,
            ShadowBias = bias,
            SolarRadians = solarRadians
        };
        SetOpts(IntPtr.Zero, ref options);
    }

    public static void Disable() => Set(false, false, 0, 0, 0);

    static void Resolve()
    {
        if (resolved)
            return;
        lock (Gate)
        {
            if (resolved)
                return;
            resolved = true;
            var module = GetModuleHandle("se_vulkan_native.dll");
            if (module == IntPtr.Zero)
                return;
            var caps = GetProcAddress(module, "sevk_get_ray_capabilities");
            var options = GetProcAddress(module, "sevk_set_ray_options");
            if (caps == IntPtr.Zero || options == IntPtr.Zero)
                return;
            GetCaps = (GetCapabilities)Marshal.GetDelegateForFunctionPointer(caps, typeof(GetCapabilities));
            SetOpts = (SetOptions)Marshal.GetDelegateForFunctionPointer(options, typeof(SetOptions));
            present = true;
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate int GetCapabilities(IntPtr context, ref Capabilities caps);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate int SetOptions(IntPtr context, ref Options options);

    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr GetModuleHandle(string name);

    [DllImport("kernel32", CharSet = CharSet.Ansi, SetLastError = true)]
    static extern IntPtr GetProcAddress(IntPtr module, string name);
}
