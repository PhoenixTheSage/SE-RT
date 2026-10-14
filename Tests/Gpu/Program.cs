using System;
using System.IO;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using SharpDX.D3DCompiler;
using Device=SharpDX.Direct3D11.Device;

static class Program
{
    internal static readonly System.Collections.Generic.Dictionary<string,byte[]> Compiled = new System.Collections.Generic.Dictionary<string,byte[]>();
    static int Main(string[] args)
    {
        try
        {
            string root=Path.GetFullPath(args.Length>0?args[0]:"Assets/Shaders");
            string game=args.Length>1?args[1]:@"C:\Users\Zeridian\.agents\skills\se-dev-game-code\Data\Content\Shaders";
            using(var device=new Device(DriverType.Warp,DeviceCreationFlags.None))
            {
            if(Array.IndexOf(args,"--far")>=0){FarSunQuality.Run(device,root,game);return 0;}
            if(Array.IndexOf(args,"--quality")>=0){ShadowQuality.Run(device,root,game);return 0;}
            if(Array.IndexOf(args,"--reconstruction")>=0){foreach(string name in new[]{"Temporal","Atrous"})Compiled[name]=ShaderCache.Compile(Path.Combine(root,name+".hlsl"),"__compute_shader","cs_5_0",root,game);ShadowReconstruction.Run(device);return 0;}
            foreach(string path in Directory.GetFiles(root,"*.hlsl"))
            {
                string name=Path.GetFileNameWithoutExtension(path), profile=name=="Fullscreen"?"vs_5_0":name=="Guides"||name=="Atlas"||name=="Composite"?"ps_5_0":"cs_5_0";
                string entry=profile[0]=='v'?"__vertex_shader":profile[0]=='p'?"__pixel_shader":"__compute_shader";
                var code=ShaderCache.Compile(path,entry,profile,root,game);
                {
                    if(profile[0]=='v')using(var shader=new VertexShader(device,code)){}
                    else if(profile[0]=='p')using(var shader=new PixelShader(device,code)){}
                    else using(var shader=new ComputeShader(device,code)){}
                    Console.WriteLine("PASS "+name+" "+code.Length+" bytes");
                    Compiled[name] = code;
                }
            }
            Pipeline.Run(device,root,game);
            if(Array.IndexOf(args,"--pipeline")>=0)return 0;
            Shadows.Run(device,root,game);
            ShadowQuality.Run(device,root,game);
            FarSunQuality.Run(device,root,game);
            RefinementQuality.Run(device,root,game);
            ShadowReconstruction.Run(device);
            Traversal.Run(device,root,game);
            Estimator.Run(device,root,game);
            Emission.Run(device,root,game);
            }
            Console.WriteLine("PASS production shader compilation and WARP creation");return 0;
        }
        catch(Exception e){Console.Error.WriteLine(e);return 1;}
    }
}
