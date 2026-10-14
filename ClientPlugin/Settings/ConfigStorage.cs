using System;
using System.IO;
using System.Threading.Tasks;
using System.Xml.Serialization;
using VRage.FileSystem;
using VRage.Utils;
namespace ClientPlugin.Settings;
public static class ConfigStorage
{
    static readonly XmlSerializer Serializer = new XmlSerializer(typeof(Config));
    static readonly object Gate = new object();
    static long dirtyAt;
    static int version, saved;
    static Task pending;
    static bool lastWriteFailed;
    static bool hasFile;
    static string FilePath => Path.Combine(MyFileSystem.UserDataPath, "Storage", Plugin.Name + ".cfg");
    public static string Status
    {
        get { lock (Gate) return (lastWriteFailed ? "save failed; retry pending" : pending != null && !pending.IsCompleted ? "saving on worker" : version != saved ? "dirty; waiting for quiet period" : hasFile ? "saved" : "defaults; no file yet") + "; " + FilePath; }
    }
    public static void MarkDirty() { lock (Gate) { version++; dirtyAt = DateTime.UtcNow.Ticks; } }
    public static void Save(Config config) => MarkDirty();
    public static Config Load()
    {
        try { lock (Gate) if (File.Exists(FilePath)) using (var file = File.OpenRead(FilePath)) { var config = (Config)Serializer.Deserialize(file); hasFile = true; return config; } }
        catch (Exception e) { MyLog.Default.Warning("SE-RT config load: " + e.Message); }
        return new Config();
    }
    public static void FlushPending(bool force = false)
    {
        Task work;
        lock (Gate)
        {
            if (pending != null && !pending.IsCompleted) work = pending;
            else if (version == saved || (!force && DateTime.UtcNow.Ticks - dirtyAt < TimeSpan.FromMilliseconds(400).Ticks)) return;
            else
            {
                int snapshotVersion = version; var snapshot = Config.Current.Snapshot();
                lastWriteFailed = false;
                string path = FilePath;
                work = pending = Task.Run(() =>
                {
                    try
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(path)); string temp = path + ".tmp";
                        lock (Serializer) using (var stream = File.Create(temp)) Serializer.Serialize(stream, snapshot);
                        if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path);
                        lock (Gate) { saved = snapshotVersion; hasFile = true; }
                    }
                    catch (Exception e) { lock (Gate) lastWriteFailed = true; MyLog.Default.Warning("SE-RT config save: " + e.Message); }
                });
            }
        }
        if (force) { work.GetAwaiter().GetResult(); lock (Gate) if (version == saved || lastWriteFailed) return; FlushPending(true); }
    }
}
