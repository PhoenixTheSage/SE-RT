using System;
using System.IO;
using System.Collections.Generic;
using SharpDX.D3DCompiler;

sealed class ShaderInclude : SharpDX.CallbackBase, Include
{
    readonly string local, game;
    readonly Dictionary<Stream,string> parents = new Dictionary<Stream,string>();
    internal readonly Dictionary<string,long> Files = new Dictionary<string,long>();
    public ShaderInclude(string local, string game) { this.local=local;this.game=game; }
    public Stream Open(IncludeType type,string name,Stream parent)
    {
        name=name.Replace('/',Path.DirectorySeparatorChar);
        string path=null;
        if(type==IncludeType.System && File.Exists(Path.Combine(game,name)))path=Path.Combine(game,name);
        if(path==null && parent!=null && parents.TryGetValue(parent,out var folder)) {var candidate=Path.Combine(folder,name);if(File.Exists(candidate))path=candidate;}
        if(path==null && File.Exists(Path.Combine(local,name)))path=Path.Combine(local,name);
        if(path==null && File.Exists(Path.Combine(game,name)))path=Path.Combine(game,name);
        if(path==null)throw new FileNotFoundException("Shader include",name);
        var stream=File.OpenRead(path); Files[Path.GetFullPath(path)]=File.GetLastWriteTimeUtc(path).Ticks;parents[stream]=Path.GetDirectoryName(path);return stream;
    }
    public void Close(Stream stream){parents.Remove(stream);stream.Dispose();}
}
