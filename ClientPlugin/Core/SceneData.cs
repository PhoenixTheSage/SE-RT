using System;
using System.Numerics;
using System.Runtime.InteropServices;

namespace SERT.Core;

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct Vertex
{
    public Vector3 Position;
    public float U;
    public Vector3 Normal;
    public float V;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct Triangle
{
    public uint A, B, C, Material;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct BvhNode
{
    public Vector3 Min;
    public uint Left;
    public Vector3 Max;
    public uint Count;
    public bool IsLeaf => Count != 0;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct Instance
{
    public Matrix4x4 World, Inverse;
    public Vector3 Min;
    public uint Root;
    public Vector3 Max;
    public uint Identity;
    public uint MaterialBase, Revision, Flags, Padding;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct Material
{
    public Vector3 Color;
    public float Roughness;
    public Vector3 Emission;
    public float Metalness;
    public uint Texture, Flags, Revision, Padding;
    public Vector4 UvTransform;
    public Vector4 Optical, OpticalUv;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct Light
{
    public Vector3 Position;
    public float Range;
    public Vector3 Radiance;
    public float Power;
    public Vector3 Direction;
    public float ConeCos;
    public uint Kind, Identity, Revision, Triangle;
    public uint Instance, Texture, Flags, Padding;
}

public enum LightKind : uint { Point, Spot, Triangle, Environment }
public enum TraceStatus : uint { Miss, Hit, Incomplete, Overflow }

public struct Ray
{
    public Vector3 Origin, Direction;
    public float Min, Max;
    public Ray(Vector3 origin, Vector3 direction, float min = 0.001f, float max = 128f)
    { Origin = origin; Direction = direction; Min = min; Max = max; }
}

public struct Hit
{
    public TraceStatus Status;
    public float Distance;
    public Vector2 Barycentric;
    public uint Triangle, Instance;
}

public readonly struct Bounds
{
    public readonly Vector3 Min, Max;
    public Bounds(Vector3 min, Vector3 max) { Min = min; Max = max; }
    public static Bounds Empty => new Bounds(new Vector3(float.PositiveInfinity), new Vector3(float.NegativeInfinity));
    public bool IsEmpty => Min.X > Max.X || Min.Y > Max.Y || Min.Z > Max.Z;
    public Vector3 Center => (Min + Max) * 0.5f;
    public float Area { get { var d = Vector3.Max(Max - Min, Vector3.Zero); return 2 * (d.X * d.Y + d.Y * d.Z + d.Z * d.X); } }
    public Bounds Include(Vector3 p) => new Bounds(Vector3.Min(Min, p), Vector3.Max(Max, p));
    public Bounds Include(Bounds b) => new Bounds(Vector3.Min(Min, b.Min), Vector3.Max(Max, b.Max));
    public Bounds Transform(Matrix4x4 matrix)
    {
        var b = Empty;
        for (var i = 0; i < 8; ++i)
            b = b.Include(Vector3.Transform(new Vector3((i & 1) == 0 ? Min.X : Max.X,
                (i & 2) == 0 ? Min.Y : Max.Y, (i & 4) == 0 ? Min.Z : Max.Z), matrix));
        return b;
    }
}

public static class Budget
{
    public const long SceneBytes = 512L * 1024 * 1024;
    public const long ReconstructionBytes = 256L * 1024 * 1024;
    public const float PrimaryDistance = 128, ResidencyDistance = 256, DiffuseDistance = 32, SpecularDistance = 64;
    public const int MaxDepth = 30, StackSize = 64, Proposals = 8, SpatialNeighbors = 4, MaxSegments = 4;
    public static bool Finite(float x) => !float.IsNaN(x) && !float.IsInfinity(x);
    public static bool Finite(Vector3 v) => Finite(v.X) && Finite(v.Y) && Finite(v.Z);
}
