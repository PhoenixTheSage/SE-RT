using System;
using System.IO;
using System.Numerics;
using System.Reflection;
using SERT.Core;
using SERT.Integration;
using SERT.Rendering;
using SharpDX;
using SharpDX.Direct3D11;
using SharpDX.D3DCompiler;
using SharpDX.DXGI;
using Buffer=SharpDX.Direct3D11.Buffer;
using Device=SharpDX.Direct3D11.Device;
using MapFlags=SharpDX.Direct3D11.MapFlags;

static class Pipeline
{
    public static void Run(Device d,string root,string game)
    {
        byte[] Compile(string path,int profile)
        {
            string name=Path.GetFileNameWithoutExtension(path);string source=File.ReadAllText(path);
            if(name!="Guides" && name!="Composite")return Program.Compiled[name];
            // Native GBuffer shaders are compiled separately; this fixture gives transport a known primary surface.
            if(name=="Guides")source="struct O{float4 p:SV_Target0;float4 n:SV_Target1;float4 c:SV_Target2;};O __pixel_shader(float4 p:SV_Position){O o;o.p=float4(0,0,-5,5);o.n=float4(0,0,1,.8);o.c=float4(.5,.5,.5,0);return o;}";
            if(name=="Composite")source="Texture2DArray<float4> r:register(t29);float4 __pixel_shader(float4 p:SV_Position):SV_Target0{return float4(r.Load(int4(0,0,0,0)).rgb,1);}";
            using(var include=new ShaderInclude(root,game))
            using(var code=ShaderBytecode.Compile(source,profile==0?"__vertex_shader":profile==1?"__pixel_shader":"__compute_shader",profile==0?"vs_5_0":profile==1?"ps_5_0":"cs_5_0",ShaderFlags.OptimizationLevel3,EffectFlags.None,null,include,path))return code.Bytecode.Data;
        }
        var rc=d.ImmediateContext;
        using(var baseTexture=new GpuTexture(d,4,4,Format.R16G16B16A16_Float,8,renderTarget:true))
        using(var shadow=new GpuTexture(d,4,4,Format.R32_Float,4,renderTarget:true))
        using(var frameBuffer=new Buffer(d,4096,ResourceUsage.Default,BindFlags.ConstantBuffer,CpuAccessFlags.None,ResourceOptionFlags.None,0))
        using(var cube=new Texture2D(d,new Texture2DDescription{Width=4,Height=4,ArraySize=6,MipLevels=1,Format=Format.R16G16B16A16_Float,SampleDescription=new SampleDescription(1,0),BindFlags=BindFlags.ShaderResource|BindFlags.RenderTarget,OptionFlags=ResourceOptionFlags.TextureCube}))
        using(var cubeView=new ShaderResourceView(d,cube))
        using(var backend=new SoftwareDx11(new AnomalyHost(Compile),()=>Path.GetDirectoryName(root),collectStatistics:true))
        {
            rc.ClearRenderTargetView(baseTexture.Rtv,new SharpDX.Mathematics.Interop.RawColor4(.5f,.5f,.5f,.5f));
            rc.ClearRenderTargetView(shadow.Rtv,new SharpDX.Mathematics.Interop.RawColor4(1,1,1,1));
            for(int i=0;i<6;i++)using(var face=new RenderTargetView(d,cube,new RenderTargetViewDescription{Format=Format.R16G16B16A16_Float,Dimension=RenderTargetViewDimension.Texture2DArray,Texture2DArray=new RenderTargetViewDescription.Texture2DArrayResource{ArraySize=1,FirstArraySlice=i}}))rc.ClearRenderTargetView(face,new SharpDX.Mathematics.Interop.RawColor4(.1f,.1f,.1f,1));
            float[] matrix={1,0,0,0,0,1,0,0,0,0,1,0,0,0,0,1};
            float[] vertices={-20,-20,-5,0,0,1,0,0,0,0,0,0,0,0,0,0,20,-20,-5,0,0,1,1,0,0,0,0,0,0,0,0,0,0,20,-5,0,0,1,.5f,1,0,0,0,0,0,0,0,0};
            object[] part={0,3,0,"fixture",new string[4],"MESH"};
            object[] variant={part,new[]{1f,1,1,0},new object[]{baseTexture.Srv,baseTexture.Srv,baseTexture.Srv,null},Array.Empty<float>()};
            object[][] meshes={new object[]{1,vertices,new[]{0,1,2},new[]{part},false}};
            object[][] descriptions={new object[]{1u,0,matrix,new object[]{variant},new[]{-20f,-20,-5,20,20,-5}}};
            float id=BitConverter.ToSingle(BitConverter.GetBytes(7u),0);
            object[] frame={1,1,1u,new double[3],meshes,descriptions,new[]{0f,0,0,10,3,3,3,1,0,0,-1,-1,0,id,1,1},
                new object[]{d,rc,baseTexture.Rtv,baseTexture.Srv,baseTexture.Srv,baseTexture.Srv,baseTexture.Srv,null,frameBuffer,shadow.Srv,cubeView,cubeView,baseTexture.Srv},new[]{4,4},new[]{0f,0,1,1,1,1,1,0,.1f,.1f,.1f,0},matrix,matrix};
            var scene=SceneAssembler.Build(frame);
            Array.Resize(ref frame,15);frame[12]=Array.Empty<object[]>();frame[13]=new[]{new object[]{null,new[]{0f,1,0}}};frame[14]=new[]{0f,0,0};
            ((float[])frame[6])[0]=2; // Native point offset differs from the physical origin.
            bool ready=backend.Render(frame,scene,.5f,1);
            for(int retry=0;!ready&&retry<1000;retry++){System.Threading.Thread.Sleep(5);backend.Poll(rc);ready=backend.Render(frame,scene,.5f,1);}
            if(!ready)throw new Exception(backend.Status);
            var metadata=(GpuTexture)typeof(SoftwareDx11).GetField("metadata",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(backend);
            var numericalGuide=(GpuTexture)typeof(SoftwareDx11).GetField("geometryGuide",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(backend);
            if(numericalGuide.Texture.Description.Format!=Format.R32G32B32A32_Float)throw new Exception("Ray-origin geometric normals and error bounds lost FP32 precision");
            var pixel=ReadFloat(rc,d,metadata.Texture);
            if(pixel[0]!=1)throw new Exception("Production trace unexpectedly invalid");
            var stats=(GpuBuffer)typeof(SoftwareDx11).GetField("stats",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(backend);
            uint[] counters=ReadBuffer(rc,d,stats.Buffer,8);
            if(counters[0]>4*4 || counters[0]==0 || counters[1]!=0)throw new Exception("Lighting segment cap/status violated");
            frame[2]=2u;frame[9]=new[]{0f,0,1,1,1,1,1,1,.1f,.1f,.1f,0};
            var residentLights=(Light[])typeof(SoftwareDx11).GetField("lightData",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(backend);
            if(residentLights[0].Position.X!=0)throw new Exception("Default renderer used the offset source instead of its physical origin");
            var offsetSettings=ShadowSettings.Default;offsetSettings.AllowLightBlockOffsets=true;backend.SetShadowSettings(offsetSettings);
            bool offsetReady=backend.Render(frame,scene,.5f,1);
            for(int retry=0;!offsetReady&&retry<1000;retry++){rc.Flush();backend.Poll(rc);offsetReady=backend.Render(frame,scene,.5f,1);if(!offsetReady)System.Threading.Thread.Sleep(1);}
            residentLights=(Light[])typeof(SoftwareDx11).GetField("lightData",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(backend);
            if(!offsetReady || residentLights[0].Position.X!=2)throw new Exception("Offset opt-in did not rebuild and refresh source positions");
            backend.SetShadowSettings(ShadowSettings.Default);
            bool physicalReady=backend.Render(frame,scene,.5f,1);
            for(int retry=0;!physicalReady&&retry<1000;retry++){rc.Flush();backend.Poll(rc);physicalReady=backend.Render(frame,scene,.5f,1);if(!physicalReady)System.Threading.Thread.Sleep(1);}
            if(!physicalReady || !backend.Render(frame,scene,.5f,1))throw new Exception("Physical-source/history restore failed");
            var diagnostics=new RendererDiagnostics();backend.FillDiagnostics(diagnostics);
            var catalog=LightCatalog.Build(scene,(float[])frame[6],(int)frame[1]);
            if(diagnostics.LightCapacity!=catalog.Length || diagnostics.NativeLights!=((float[])frame[6]).Length/16 ||
                diagnostics.EmissiveLights!=catalog.Length-diagnostics.NativeLights-ProxyCatalog.Capacity-1 ||
                !diagnostics.History || diagnostics.Width<=0 || diagnostics.SceneBytes<=0 || diagnostics.ReconstructionBytes<=0 ||
                diagnostics.ReadyTiles>diagnostics.AtlasTiles)throw new Exception("Renderer diagnostic snapshot disagrees with resident pipeline");
            using(var deferred=new DeviceContext(d))
            {
                ((object[])frame[7])[1]=deferred;
                if(!backend.Render(frame,scene,.5f,1))throw new Exception("Deferred-context render failed");
                using(var commands=deferred.FinishCommandList(false))rc.ExecuteCommandList(commands,false);
                ((object[])frame[7])[1]=rc;
            }
            long beforeMeasurements=backend.Measurements;int notifications=0;
            for(int retry=0;retry<1000&&notifications==0;retry++){rc.Flush();backend.Poll(rc,ms=>{if(double.IsNaN(ms)||ms<0)throw new Exception("Invalid asynchronous combined GPU timing");notifications++;});if(notifications==0)System.Threading.Thread.Sleep(1);}
            if(notifications==0||backend.Measurements-beforeMeasurements!=notifications)throw new Exception("GPU budget notification did not visit every completed measurement");
            Array.Resize(ref frame,15);
            var proxyViews=new System.Collections.Generic.List<ShaderResourceView>();
            try
            {
                var records=new object[33][];
                for(int i=0;i<records.Length;i++)
                {
                    var view=new ShaderResourceView(d,baseTexture.Texture);proxyViews.Add(view);
                    records[i]=new object[]{(uint)(20+i),new[]{2f,0,-2,.5f,.1f,.1f,.1f,0,0,0,1,(float)Math.PI*.25f,0,0,0,0},view,uint.MaxValue,1u};
                }
                frame[12]=records;
                if(backend.Render(frame,scene,.5f,1)||!backend.Status.Contains("awaiting bake"))throw new Exception("Incomplete source atlas overwrote native lighting");
                if(!backend.Render(frame,scene,.5f,1))throw new Exception("Source atlas did not become ready after its remaining tiles baked");
                frame[12]=Array.Empty<object[]>();
            }
            finally {foreach(var view in proxyViews)view.Dispose();}
            // A constant environment must retain its integrated diffuse energy after NEE/BSDF MIS.
            frame[8]=new[]{256,256};frame[9]=new[]{0f,0,1,1,0,0,0,0,.1f,.1f,.1f,0};
            var native=(float[])frame[6];native[4]=native[5]=native[6]=0;
            bool referenceReady=backend.Render(frame,scene,.5f,1);
            for(int retry=0;!referenceReady&&retry<1000;retry++){rc.Flush();backend.Poll(rc);referenceReady=backend.Render(frame,scene,.5f,1);if(!referenceReady)System.Threading.Thread.Sleep(1);}
            if(!referenceReady)throw new Exception("Environment reference fixture did not become ready");
            var radiance=(GpuTexture)typeof(SoftwareDx11).GetField("noisy",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(backend);
            double expected=0;
            for(int i=0;i<10000;i++){double nl=(i+.5)/10000,cosHalf=Math.Sqrt((1+nl)*.5),f=.04+.96*Math.Pow(1-cosHalf,5);expected+=2*nl*(1-f)*.1/10000;}
            foreach(ShadowSamplingMode mode in new[]{ShadowSamplingMode.BlueNoise,ShadowSamplingMode.Hashed})
            {
                var comparison=ShadowSettings.Default;comparison.Sampling=mode;comparison.SamplingSeed=173;backend.SetShadowSettings(comparison);
                double observed=0;
                for(uint sampleFrame=0;sampleFrame<32;sampleFrame++){frame[2]=sampleFrame+16;if(!backend.Render(frame,scene,.5f,1))throw new Exception("Energy sequence fell back");observed+=MeanHalfRed(rc,d,radiance.Texture)/32;}
                if(Math.Abs(observed/expected-1)>.015)throw new Exception(mode+" environment MIS lost diffuse energy: "+observed+" vs "+expected);
                Console.WriteLine("PASS production "+mode+" environment NEE/BSDF MIS diffuse energy against quadrature (32 frames / 524288 samples, equal caps)");
            }
            backend.SetShadowSettings(ShadowSettings.Default);
            frame[9]=new[]{0f,0,1,1,1,1,1,0,.1f,.1f,.1f,0};native[4]=native[5]=native[6]=3;
            frame[8]=new[]{8,8};
            if(backend.Render(frame,scene,.5f,1)||!backend.Status.Contains("retirement"))throw new Exception("Resize retired resources without the GPU fence");
            bool resized=false;
            for(int retry=0;!resized&&retry<1000;retry++){rc.Flush();backend.Poll(rc);resized=backend.Render(frame,scene,.5f,1);if(!resized)System.Threading.Thread.Sleep(1);}
            if(!resized)throw new Exception("Resize retirement did not complete");
            var pose=(float[])matrix.Clone();pose[12]=1;
            variant[3]=pose;for(int i=0;i<3;i++)vertices[i*16+12]=1;
            var poseB=(float[])matrix.Clone();poseB[12]=2;
            object[] partB={3,3,0,"fixture-b",new string[4],"MESH"};
            object[] variantB={partB,new[]{1f,1,1,0},variant[2],poseB};
            meshes[0][2]=new[]{0,1,2,0,1,2}; meshes[0][3]=new[]{part,partB};
            descriptions[0][3]=new[]{variant,variantB};
            descriptions[0][4]=new[]{-19f,-20,-5,21,20,-5};scene=SceneAssembler.Build(frame);
            bool drawn=backend.Render(frame,scene,.5f,1);
            for(int retry=0;!drawn && retry<1000;retry++){rc.Flush();backend.Poll(rc);drawn=backend.Render(frame,scene,.5f,1);if(!drawn)System.Threading.Thread.Sleep(1);}
            if(!drawn)throw new Exception("Skinned render/retirement failed");
            var vertexBuffer=(GpuBuffer)typeof(SoftwareDx11).GetField("vertices",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(backend);
            var transformed=Traversal.Read<Vertex>(rc,d,vertexBuffer.Buffer,6);
            if(Math.Abs(transformed[0].Position.X+19)>1e-5 || Math.Abs(transformed[3].Position.X+18)>1e-5)throw new Exception("Production GPU skinning lost a shared vertex bone palette");
            backend.Reset();
            var cleared=new RendererDiagnostics();backend.FillDiagnostics(cleared);
            if(cleared.Width!=0 || cleared.FullWidth!=0 || cleared.LightCapacity!=0 || cleared.AtlasTiles!=0 || cleared.History || cleared.SceneBytes!=0 || cleared.ReconstructionBytes!=0)
                throw new Exception("Reset left stale resident diagnostics");
            if(!backend.Render(frame,scene,.25f,1))throw new Exception("Device/resource rebuild failed");
            if(backend.SceneBytes>Budget.SceneBytes||backend.ReconstructionBytes>Budget.ReconstructionBytes)throw new Exception("Memory budget violated");
            Console.WriteLine("PASS production transport/reconstruction pipeline, segment cap, history, deferred recording, source atlas readiness, fenced resize, shared-palette GPU skin/refit, reset and quarter scale");
        }
    }
    static float[] ReadFloat(DeviceContext rc,Device d,Texture2D texture)
    {
        var desc=texture.Description;desc.Usage=ResourceUsage.Staging;desc.BindFlags=0;desc.CpuAccessFlags=CpuAccessFlags.Read;desc.OptionFlags=0;
        using(var staging=new Texture2D(d,desc)){rc.CopyResource(texture,staging);var box=rc.MapSubresource(staging,0,MapMode.Read,MapFlags.None);
            try{var result=new float[4];System.Runtime.InteropServices.Marshal.Copy(box.DataPointer,result,0,4);return result;}finally{rc.UnmapSubresource(staging,0);}}
    }
    static double MeanHalfRed(DeviceContext rc,Device d,Texture2D texture)
    {
        var desc=texture.Description;desc.Usage=ResourceUsage.Staging;desc.BindFlags=0;desc.CpuAccessFlags=CpuAccessFlags.Read;desc.OptionFlags=0;
        using(var staging=new Texture2D(d,desc))
        {
            rc.CopyResource(texture,staging);var box=rc.MapSubresource(staging,0,MapMode.Read,MapFlags.None);
            try
            {
                double sum=0;for(int y=0;y<desc.Height;y++)for(int x=0;x<desc.Width;x++)
                {ushort bits=unchecked((ushort)System.Runtime.InteropServices.Marshal.ReadInt16(box.DataPointer,y*box.RowPitch+x*8));int e=(bits>>10)&31,m=bits&1023;if(e==31)throw new Exception("Nonfinite transport radiance");
                    double value=e==0?m*Math.Pow(2,-24):(1+m/1024.0)*Math.Pow(2,e-15);sum+=(bits&32768)!=0?-value:value;}
                return sum/(desc.Width*desc.Height);
            }
            finally {rc.UnmapSubresource(staging,0);}
        }
    }
    static uint[] ReadBuffer(DeviceContext rc,Device d,Buffer buffer,int count)
    {
        using(var staging=new Buffer(d,count*4,ResourceUsage.Staging,BindFlags.None,CpuAccessFlags.Read,ResourceOptionFlags.None,0)){rc.CopyResource(buffer,staging);var box=rc.MapSubresource(staging,0,MapMode.Read,MapFlags.None);
            try{var result=new uint[count];for(int i=0;i<count;i++)result[i]=(uint)System.Runtime.InteropServices.Marshal.ReadInt32(box.DataPointer,i*4);return result;}finally{rc.UnmapSubresource(staging,0);}}
    }
}
