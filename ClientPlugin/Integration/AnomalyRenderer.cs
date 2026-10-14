using System;
using System.Reflection;

namespace SERT.Integration;

/// <summary>
/// Early read of Anomaly's renderer route. No compile-time reference.
/// Pending closes both device paths. A missing route fails closed.
/// </summary>
internal static class AnomalyRenderer
{
    public const string RouteTypeName = "ClientPlugin.Backend.RendererRoute";
    public const int Pending = 0;
    public const int DirectX11 = 1;
    public const int Vulkan = 2;
    public const string Shared = "shared";
    public const string Dx11Device = "dx11-device";
    public const string Guest = "guest";

    static string seen = "";

    public static bool Found => Route() != null;
    public static int Active => ReadInt("Active", Pending);
    public static string ActiveName => ReadString("ActiveName") ?? "pending";
    public static bool Decided => ReadBool("Decided");
    public static bool UseSharedBus => ReadBool("UseSharedBus");
    public static bool UseDx11Device => ReadBool("UseDx11Device");
    public static bool UseGuestImages => ReadBool("UseGuestImages");
    public static string LastRefusal => ReadString("LastRefusal") ?? "";

    public static bool RunsOnWindows => Environment.OSVersion.Platform == PlatformID.Win32NT;

    /// <summary>DX11 device, Harmony, and SharpDX work. Closed on Linux.</summary>
    public static bool AllowDx11Device => RunsOnWindows && UseDx11Device;

    /// <summary>Catalog images and fullscreen packets.</summary>
    public static bool AllowGuestWork => Found && UseGuestImages;

    /// <summary>Linux runs only when Anomaly has opened the Vulkan guest bus.</summary>
    public static bool LinuxRequiresGuest => !RunsOnWindows && !AllowGuestWork;

    public static bool IsPathOpen(string path)
    {
        var type = Route();
        if (type == null)
            return false;
        var method = type.GetMethod("IsPathOpen", BindingFlags.Public | BindingFlags.Static);
        return method?.Invoke(null, new object[] { path }) is true;
    }

    public static void Observe()
    {
        string line = !Found
            ? "Anomaly renderer route missing"
            : "Anomaly renderer " + ActiveName
              + " shared=" + UseSharedBus
              + " dx11=" + UseDx11Device
              + " guest=" + UseGuestImages;
        if (line == seen)
            return;
        seen = line;
        try { VRage.Utils.MyLog.Default.WriteLine(line); }
        catch { }
    }

    static Type Route()
    {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type type;
            try { type = assembly.GetType(RouteTypeName, false); }
            catch { continue; }
            if (type != null)
                return type;
        }
        return null;
    }

    static bool ReadBool(string name) => Property(name) is true;

    static int ReadInt(string name, int fallback) => Property(name) is int n ? n : fallback;

    static string ReadString(string name) => Property(name) as string;

    static object Property(string name)
    {
        var type = Route();
        if (type == null)
            return null;
        try { return type.GetProperty(name, BindingFlags.Public | BindingFlags.Static)?.GetValue(null); }
        catch { return null; }
    }
}
