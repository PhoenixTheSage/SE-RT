using System;
using System.Collections.Generic;
using System.Numerics;
namespace SERT.Core;

public sealed class ProxyCatalog
{
    public const int Capacity = 512;
    readonly Dictionary<uint, int> slots = new Dictionary<uint, int>();
    readonly uint[] identities = new uint[Capacity];
    public readonly object[] Textures = new object[Capacity];
    public readonly Light[] Lights = new Light[Capacity];
    public void Refresh(object[][] proxies, HashSet<uint> nativeOwners)
    {
        Array.Clear(Textures, 0, Capacity);
        for (int i = 0; i < Capacity; ++i) Lights[i] = new Light { Kind = 4, Identity = identities[i], Triangle = (uint)i };
        var active = new HashSet<uint>();
        foreach (var p in proxies) if (!nativeOwners.Contains((uint)p[3])) active.Add((uint)p[0]);
        var retired = new List<uint>(); foreach (var p in slots) if (!active.Contains(p.Key)) retired.Add(p.Key);
        foreach (uint id in retired) { identities[slots[id]] = 0; slots.Remove(id); }
        foreach (var p in proxies)
        {
            uint id = (uint)p[0]; if (nativeOwners.Contains((uint)p[3])) continue;
            if (!slots.TryGetValue(id, out int slot))
            { slot = Array.IndexOf(identities, 0u); if (slot < 0) throw new InvalidOperationException("Transient emitter capacity exceeded"); slots[id] = slot; identities[slot] = id; }
            var v = (float[])p[1]; Textures[slot] = p[2];
            Lights[slot] = new Light { Kind = 4, Identity = id, Revision = (uint)p[4], Triangle = (uint)slot,
                Position = new Vector3(v[0], v[1], v[2]), Range = v[3], Radiance = new Vector3(v[4], v[5], v[6]),
                Direction = new Vector3(v[8], v[9], v[10]), ConeCos = v[11], Flags = p[2] == null ? 0u : 1u };
        }
    }
}
