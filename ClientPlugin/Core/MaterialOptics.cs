using System;
using System.Numerics;

namespace SERT.Core;

public static class MaterialOptics
{
    public const uint ThinGlass = 32;
    public const uint OneSidedGlass = 64;
    public static object[] Apply(object[] variant, ref Material material)
    {
        var textures = (object[])variant[2];
        material.Flags &= ~(ThinGlass | OneSidedGlass); material.Optical = Vector4.Zero; material.OpticalUv = new Vector4(0, 0, 1, 1);
        if (variant.Length < 5 || !(variant[4] is object[] optics) || optics.Length != 7 || !Equals(optics[6], "thin-glass") || !(optics[0] is int version) || version != 1 ||
            !(optics[1] is float[] values) || values.Length != 4 || optics[2] == null ||
            !(optics[3] is float[] uv) || uv.Length != 4 || !(optics[4] is uint) || !(optics[5] is bool)) return textures;
        foreach (float x in values) if (!Budget.Finite(x)) return textures;
        foreach (float x in uv) if (!Budget.Finite(x)) return textures;
        material.Flags |= ThinGlass;
        if ((bool)optics[5]) material.Flags |= OneSidedGlass;
        material.Padding = (uint)optics[4];
        material.Optical = new Vector4(Math.Max(0, values[0]), Math.Max(0, values[1]), Math.Max(0, values[2]), Math.Max(0, Math.Min(1, values[3])));
        material.OpticalUv = new Vector4(uv[0], uv[1], uv[2], uv[3]);
        return new[] { optics[2], null, null, null };
    }
    public static Vector3 Transmission(Vector3 tint, float opacity, float strength)
    {
        float maximum = Math.Max(tint.X, Math.Max(tint.Y, tint.Z));
        tint = Vector3.Clamp(tint / Math.Max(maximum, 1e-6f), Vector3.Zero, Vector3.One);
        return Vector3.Lerp(Vector3.One, tint * strength, Math.Max(0, Math.Min(1, opacity)));
    }
}
