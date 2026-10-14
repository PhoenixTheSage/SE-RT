using System;
using System.Collections.Generic;
using System.Numerics;

namespace SERT.Core;

public static class LightCatalog
{
    public static bool Refresh(Light[] lights, float[] native, SceneGeometry scene, int epoch)
    {
        int count = native.Length / 16, existing = 0;
        while (existing < lights.Length && lights[existing].Kind < 2) existing++;
        if (existing != count) return false;
        for (int i = 0; i < count; ++i)
        {
            int k = i * 16; uint identity = BitConverter.ToUInt32(BitConverter.GetBytes(native[k + 13]), 0);
            if (lights[i].Identity != identity || lights[i].Kind != (uint)native[k + 12]) return false;
            var l = lights[i]; var radiance = new Vector3(native[k + 4], native[k + 5], native[k + 6]);
            if (l.Radiance != radiance || l.Range != native[k + 3] || l.ConeCos != native[k + 11]) l.Revision++;
            l.Position = new Vector3(native[k], native[k + 1], native[k + 2]); l.Radiance = radiance; l.Range = native[k + 3];
            l.Power = native[k + 7]; l.Direction = new Vector3(native[k + 8], native[k + 9], native[k + 10]); l.ConeCos = native[k + 11];
            l.Instance = Bits(native[k + 14]); l.Texture = Bits(native[k + 15]); lights[i] = l;
        }
        for (int i = count; i < lights.Length; ++i) if (lights[i].Kind == 2)
        { var l = lights[i]; l.Revision = scene.Materials[l.Texture].Revision; lights[i] = l; }
        return true;
    }
    // Triangle candidates retain support even when an emissive texture has a tiny bright region.
    public static Light[] Build(SceneGeometry scene, float[] native, int epoch)
    {
        var lights = new List<Light>();
        for (int k = 0; k + 15 < native.Length; k += 16)
            lights.Add(new Light { Position = new Vector3(native[k], native[k + 1], native[k + 2]), Range = native[k + 3],
                Radiance = new Vector3(native[k + 4], native[k + 5], native[k + 6]), Power = native[k + 7],
                Direction = new Vector3(native[k + 8], native[k + 9], native[k + 10]), ConeCos = native[k + 11],
                Kind = (uint)native[k + 12], Identity = Bits(native[k + 13]), Revision = (uint)epoch, Instance = Bits(native[k + 14]), Texture = Bits(native[k + 15]) });
        for (int i = 0; i < scene.Instances.Length; ++i)
        {
            var instance = scene.Instances[i];
            if (instance.Root == uint.MaxValue) continue;
            var stack = new Stack<uint>(); stack.Push(instance.Root);
            while (stack.Count != 0)
            {
                var n = scene.Nodes[stack.Pop()];
                if (!n.IsLeaf) { stack.Push(n.Left); stack.Push(n.Left + 1); continue; }
                for (uint t = n.Left; t < n.Left + n.Count; t++)
                {
                    var triangle = scene.Triangles[t]; uint m = instance.MaterialBase + triangle.Material;
                    var material = scene.Materials[m];
                    if ((material.Flags & 2) != 0 || material.Emission.LengthSquared() <= 0) continue;
                    lights.Add(new Light { Kind = (uint)LightKind.Triangle, Triangle = t, Instance = (uint)i,
                        Texture = m, Identity = unchecked(instance.Identity * 16777619u ^ t), Revision = material.Revision });
                }
            }
        }
        // Keep emitters ordered for logarithmic lookup of the continuation-hit MIS PDF.
        int nativeCount = native.Length / 16;
        lights.Sort(nativeCount, lights.Count - nativeCount, Comparer<Light>.Create((a, b) =>
            a.Instance != b.Instance ? a.Instance.CompareTo(b.Instance) : a.Triangle.CompareTo(b.Triangle)));
        for (int i = 0; i < ProxyCatalog.Capacity; ++i) lights.Add(new Light { Kind = 4, Triangle = (uint)i });
        lights.Add(new Light { Kind = (uint)LightKind.Environment, Identity = uint.MaxValue, Revision = (uint)epoch });
        if ((long)lights.Count * 96 + scene.ByteSize > Budget.SceneBytes) throw new InvalidOperationException("Light catalog exceeds scene budget");
        return lights.ToArray();
    }
    static uint Bits(float value) => BitConverter.ToUInt32(BitConverter.GetBytes(value), 0);
}
