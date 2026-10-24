using System;
using System.Numerics;
using System.Runtime.InteropServices;
using SERT.Core;

namespace SERT.Rendering;

/// <summary>b1 constants shared by every software-stack shader (Common.hlsli Parameters), on DX11 and Vulkan.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct Parameters
{
    public uint FullWidth, FullHeight, Width, Height;
    public uint Vertices, Triangles, Instances, Nodes;
    public uint Lights, Materials, Tlas, Frame;
    public Vector4 SunDirection, SunColor, Sky;
    public Matrix4x4 PreviousViewProjection;
    public Vector4 CameraDelta;
    public uint History, Pass, TreeLeaves, Poses;
    public float Exposure, Primary, Diffuse, Specular;
    public uint CollectStatistics, ShadowFlags;
    public float ShadowBias;
    public uint ShadowDebugView;
    public float LampRadius, SolarRadius, GlassTransmission, RefinementFraction;
    public uint BiasMode, SamplingMode, SamplingSeed, FarSunValid;
    public Matrix4x4 FarSunMatrix;
    public uint RefinementCapacity, RefinementPixels, QualityPadding0, QualityPadding1;
}

/// <summary>Allocation forecasts the software stack checks before and after allocating, on either device.</summary>
internal static class SoftwareForecast
{
    static long BufferBytes(int count,int stride) => (long)Math.Max(1,count)*stride;
    public static long Scene(SceneGeometry scene,int tiles,int lightCount,int refitEntries,int skinnedCount,bool farSun)
    {
        int leaves=1;while(leaves<lightCount)leaves=checked(leaves*2);
        return checked(BufferBytes(scene.Vertices.Length,32)+BufferBytes(scene.Triangles.Length,16)+BufferBytes(scene.Nodes.Length,32)+
            BufferBytes(scene.Instances.Length,176)+BufferBytes(scene.Materials.Length,96)+BufferBytes(scene.Sources.Length,80)+BufferBytes(scene.Poses.Length,64)+
            BufferBytes(scene.Vertices.Length,4)+Math.Max(1,tiles)*65536L+BufferBytes(lightCount,80)+leaves*8L+32+
            BufferBytes(skinnedCount,4)+refitEntries*4L+8192+Marshal.SizeOf<Parameters>()+(farSun?2048L*2048*4+80:0));
    }
    public static long Screen(int pixels, int capacity) => checked((long)pixels * 500 + Math.Max(1, capacity) * (2L * Marshal.SizeOf<RefinementSample>() + 8) + 4);
    public static int PoolCapacity(int pixels, float fraction)
    {
        long available = Budget.ReconstructionBytes - (long)pixels * 500 - 4;
        return Math.Max(0, (int)Math.Min(Math.Floor(pixels * (double)fraction), Math.Max(0, available / (2L * Marshal.SizeOf<RefinementSample>() + 8))));
    }
}
