using System;
using System.IO;
using SharpDX.D3DCompiler;

static class ShaderCache
{
    internal static byte[] Compile(string path,string entry,string profile,string root,string game)
    {
        string folder=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"ShaderCache");Directory.CreateDirectory(folder);
        string key=Path.Combine(folder,Path.GetFileNameWithoutExtension(path)+".dxbc"),manifest=key+".deps";
        if(File.Exists(key)&&File.Exists(manifest))
        {
            bool valid=true;
            foreach(string line in File.ReadAllLines(manifest)){int split=line.LastIndexOf('|');if(split<0||!long.TryParse(line.Substring(split+1),out long ticks)||File.GetLastWriteTimeUtc(line.Substring(0,split)).Ticks!=ticks){valid=false;break;}}
            if(valid)return File.ReadAllBytes(key);
        }
        long modified=File.GetLastWriteTimeUtc(path).Ticks;
        using(var include=new ShaderInclude(root,game))
        using(var code=ShaderBytecode.Compile(File.ReadAllText(path),entry,profile,ShaderFlags.OptimizationLevel3,EffectFlags.None,null,include,path))
        {
            File.WriteAllBytes(key,code.Bytecode.Data);
            using(var writer=new StreamWriter(manifest)){writer.WriteLine(Path.GetFullPath(path)+"|"+modified);foreach(var item in include.Files)writer.WriteLine(item.Key+"|"+item.Value);}
            return code.Bytecode.Data;
        }
    }
}
