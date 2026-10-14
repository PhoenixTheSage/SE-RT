using System;
using System.IO;
using System.Reflection;
using Mono.Cecil;

namespace ClientPlugin;

/// <summary>Keep the companion's Config and handshake types in the live Pulsar assembly.</summary>
internal static class SatelliteAssembly
{
    static ResolveEventHandler coreResolver;

    internal static string Find(string fileName, Assembly core)
    {
        string beside = Path.Combine(Path.GetDirectoryName(core.Location) ?? "", fileName);
        if (File.Exists(beside))
            return beside;
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type lookup;
            try { lookup = assembly.GetType("ClientPlugin.Backend.SatelliteLookup", false); }
            catch { continue; }
            var found = lookup?.GetMethod("Find")?.Invoke(null, new object[] { fileName, core }) as string;
            if (!string.IsNullOrEmpty(found) && File.Exists(found))
                return found;
        }
        return beside;
    }

    internal static void Reset()
    {
        if (coreResolver != null)
            AppDomain.CurrentDomain.AssemblyResolve -= coreResolver;
        coreResolver = null;
    }

    internal static Assembly Load(string path, Assembly core)
    {
        using var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(Path.GetDirectoryName(path));
        resolver.AddSearchDirectory(Path.GetDirectoryName(core.Location));
        resolver.AddSearchDirectory(Path.GetDirectoryName(typeof(VRage.Plugins.IPlugin).Assembly.Location));
        using var module = ModuleDefinition.ReadModule(path, new ReaderParameters { AssemblyResolver = resolver });
        var live = core.GetName();
        bool changed = false;
        foreach (var reference in module.AssemblyReferences)
        {
            if (reference.Name != "SERT")
                continue;
            if (reference.FullName == live.FullName)
                break;
            // Pulsar -Sources assigns a generated name. Loading the disk core would
            // create a second Config.Current and incompatible enum identities.
            reference.Name = live.Name;
            reference.Version = live.Version;
            reference.Culture = live.CultureName;
            reference.PublicKeyToken = live.GetPublicKeyToken();
            changed = true;
            break;
        }
        if (!changed)
            return Assembly.LoadFrom(path);
        using var bytes = new MemoryStream();
        module.Write(bytes);
        // Legacy cannot resolve an in-memory companion's reference to a core in
        // the LoadFrom context. Resolve only this exact live core identity.
        Reset();
        coreResolver = (_, args) => args.Name == live.FullName ? core : null;
        AppDomain.CurrentDomain.AssemblyResolve += coreResolver;
        try { return Assembly.Load(bytes.ToArray()); }
        catch { Reset(); throw; }
    }
}
