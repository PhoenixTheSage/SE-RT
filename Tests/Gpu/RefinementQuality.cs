using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using SERT.Core;
using SERT.Rendering;
using SharpDX;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using Buffer=SharpDX.Direct3D11.Buffer;
using Device=SharpDX.Direct3D11.Device;

static class RefinementQuality
{
    const string Surface=@"struct SurfaceInterface{float native_depth,depth,metalness,gloss,emissive;float3 position,N,V,base_color,albedo,f0;};
SurfaceInterface read_gbuffer(uint2 pixel){SurfaceInterface s=(SurfaceInterface)0;s.native_depth=1;s.depth=5;s.position=float3(0,0,-5);s.N=float3(0,0,1);s.V=s.N;s.base_color=.5;s.albedo=.5;s.f0=.04;s.gloss=.2;return s;}
float3 get_camera_position(){return 0;}
";
    const string Lighting=@"struct FakeLight{float ambientSpecularFactor;};struct FakePost{float BloomEmissiveness;};struct FakeFrame{FakeLight Light;FakePost Post;};
static const FakeFrame frame_={{1},{1}};
float3 MainDirectionalLight(SurfaceInterface s){return 1;}
float3 AmbientSpecular(float3 a,float b,float3 c,float3 d,float e){return 0;}
float GetAmbientFarFactor(float d){return 1;}
float3 Fog(float3 color,float depth){return color;}
";
    internal static void Run(Device device,string root,string game)
    {
        var resources=new List<IDisposable>();T Keep<T>(T resource)where T:IDisposable{resources.Add(resource);return resource;}
        int checks=0;void Check(bool valid,string message){checks++;if(!valid)throw new Exception("Refinement: "+message);}
        var rc=device.ImmediateContext;
        GpuBuffer Buf(int count,int stride,bool writable=false)=>Keep(new GpuBuffer(device,count,stride,writable));
        GpuTexture Tex(Format format=Format.R32G32B32A32_Float,int layers=1,bool render=true,int w=8,int h=8)=>Keep(new GpuTexture(device,w,h,format,format==Format.R32_UInt?4:format==Format.R32G32_UInt?8:16,layers,render,array:layers>1));
        void Clear(GpuTexture texture,float x,float y=0,float z=0,float a=0){if(texture.Slices!=null)foreach(var slice in texture.Slices)rc.ClearRenderTargetView(slice,new SharpDX.Mathematics.Interop.RawColor4(x,y,z,a));else rc.ClearRenderTargetView(texture.Rtv,new SharpDX.Mathematics.Interop.RawColor4(x,y,z,a));}
        byte[] Fixture(string name)
        {
            string source=File.ReadAllText(Path.Combine(root,name+".hlsl"));
            source=source.Replace("#include <GBuffer/GBuffer.hlsli>",Surface).Replace("#include <Lighting/Light.hlsli>",Surface+Lighting).Replace("#include <Postprocess/PostprocessBase.hlsli>","");
            string path=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,name+"Fixture.hlsl");
            if(!File.Exists(path)||File.ReadAllText(path)!=source)File.WriteAllText(path,source);
            return ShaderCache.Compile(path,name=="Composite"?"__pixel_shader":"__compute_shader",name=="Composite"?"ps_5_0":"cs_5_0",root,game);
        }
        try
        {
            Check(Marshal.SizeOf<RefinementSample>()==192,"CPU sparse-pool layout");
            var classify=Keep(new ComputeShader(device,Fixture("Classify")));var refine=Keep(new ComputeShader(device,Fixture("Refine")));
            var composite=Keep(new PixelShader(device,Fixture("Composite")));var fullscreen=Keep(new VertexShader(device,Program.Compiled["Fullscreen"]));
            var cb=Keep(new Buffer(device,Marshal.SizeOf<Parameters>(),ResourceUsage.Default,BindFlags.ConstantBuffer,CpuAccessFlags.None,ResourceOptionFlags.None,0));
            var pos=Tex();var normal=Tex();var color=Tex();var meta=Tex();var identity=Tex(Format.R32G32_UInt,render:false);var keys=Tex(Format.R32G32B32A32_UInt,render:false);
            var sun=Tex();var radiance=Tex(layers:4);var lookup=Tex(Format.R32_UInt,render:false);var target=Tex(w:31,h:29);
            var cm=Tex(layers:1);var ext=Tex(layers:1);
            // Explicit array views for the shader material bindings.
            using(var colorView=new ShaderResourceView(device,cm.Texture,new ShaderResourceViewDescription{Format=Format.R32G32B32A32_Float,Dimension=SharpDX.Direct3D.ShaderResourceViewDimension.Texture2DArray,Texture2DArray=new ShaderResourceViewDescription.Texture2DArrayResource{ArraySize=1,MipLevels=1}}))
            using(var extView=new ShaderResourceView(device,ext.Texture,new ShaderResourceViewDescription{Format=Format.R32G32B32A32_Float,Dimension=SharpDX.Direct3D.ShaderResourceViewDimension.Texture2DArray,Texture2DArray=new ShaderResourceViewDescription.Texture2DArrayResource{ArraySize=1,MipLevels=1}}))
            {
                var vertices=Buf(3,32);var triangles=Buf(1,16);var nodes=Buf(2,32);var instances=Buf(1,176);var materials=Buf(1,96);var lights=Buf(1,80);var tree=Buf(2,4);
                var count=Buf(1,4,true);var work=Buf(4,8,true);var pool=Buf(4,192,true);var stats=Buf(4,4,true);
                uint one=BitConverter.ToUInt32(BitConverter.GetBytes(1f),0);
                vertices.Upload(rc,new[]{new Vertex{Position=new Vector3(-20,-20,-5),Normal=Vector3.UnitZ},new Vertex{Position=new Vector3(20,-20,-5),Normal=Vector3.UnitZ},new Vertex{Position=new Vector3(0,20,-5),Normal=Vector3.UnitZ}});
                triangles.Upload(rc,new[]{new Triangle{A=0,B=1,C=2}});var lo=new Vector3(-20,-20,-5);var hi=new Vector3(20,20,-5);
                nodes.Upload(rc,new[]{new BvhNode{Min=lo,Max=hi,Count=1},new BvhNode{Min=lo,Max=hi,Count=1}});instances.Upload(rc,new[]{new Instance{World=Matrix4x4.Identity,Inverse=Matrix4x4.Identity,Root=0,Identity=1,Min=lo,Max=hi}});
                materials.Upload(rc,new[]{new Material{Color=Vector3.One}});lights.Upload(rc,new[]{new Light{Position=Vector3.Zero,Range=10,Radiance=new Vector3(3),Power=1,Identity=1,Instance=one,Texture=one}});tree.Upload(rc,new[]{0f,1});
                Clear(pos,0,0,-5,5);Clear(normal,0,0,1,.8f);Clear(color,.5f,.5f,.5f,0);Clear(meta,1,0,1,.1f);Clear(sun,1,1,1,1);Clear(radiance,0,0,0,1);Clear(cm,1,1,1,0);Clear(ext,0,.8f,1,1);
                rc.ClearUnorderedAccessView(identity.Uav,new SharpDX.Mathematics.Interop.RawInt4(1,1,0,0));rc.ClearUnorderedAccessView(keys.Uav,new SharpDX.Mathematics.Interop.RawInt4());rc.ClearUnorderedAccessView(lookup.Uav,new SharpDX.Mathematics.Interop.RawInt4());rc.ClearUnorderedAccessView(count.Uav,new SharpDX.Mathematics.Interop.RawInt4());rc.ClearUnorderedAccessView(stats.Uav,new SharpDX.Mathematics.Interop.RawInt4());
                rc.ClearUnorderedAccessView(work.Uav,new SharpDX.Mathematics.Interop.RawInt4(0x1357,0x1357,0x1357,0x1357));rc.ClearUnorderedAccessView(pool.Uav,new SharpDX.Mathematics.Interop.RawInt4(0x5a5a,0x5a5a,0x5a5a,0x5a5a));
                var sampler=Keep(new SamplerState(device,new SamplerStateDescription{Filter=Filter.MinMagMipLinear,AddressU=TextureAddressMode.Wrap,AddressV=TextureAddressMode.Wrap,AddressW=TextureAddressMode.Wrap,MaximumLod=float.MaxValue}));
                var p=new Parameters{FullWidth=31,FullHeight=29,Width=8,Height=8,Vertices=3,Triangles=1,Nodes=2,Instances=1,Materials=1,Lights=1,Tlas=1,TreeLeaves=1,Primary=128,Diffuse=32,Specular=64,SunDirection=new Vector4(0,0,1,1),Exposure=1,SamplingMode=1,RefinementFraction=.05f,RefinementCapacity=3,RefinementPixels=3,CollectStatistics=1};
                void Bind(){rc.UpdateSubresource(ref p,cb);rc.ComputeShader.SetConstantBuffer(1,cb);rc.ComputeShader.SetShaderResources(16,vertices.Srv,triangles.Srv,nodes.Srv,instances.Srv,materials.Srv,lights.Srv,colorView,extView);rc.ComputeShader.SetShaderResources(24,pos.Srv,normal.Srv,color.Srv,identity.Srv);rc.ComputeShader.SetShaderResource(31,meta.Srv);rc.ComputeShader.SetShaderResource(33,tree.Srv);rc.ComputeShader.SetShaderResource(34,sun.Srv);rc.ComputeShader.SetShaderResource(51,keys.Srv);rc.ComputeShader.SetSamplers(0,sampler,sampler);}
                for(uint phase=0;phase<2;phase++){p.QualityPadding0=phase;Bind();rc.ComputeShader.Set(classify);rc.ComputeShader.SetUnorderedAccessViews(0,count.Uav,work.Uav,lookup.Uav);rc.Dispatch(1,1,1);rc.ClearState();}
                Check(Traversal.Read<uint>(rc,device,count.Buffer,1)[0]==64,"all difficult footprints eligible, capacity counter bounded by footprint count");
                var tasks=Traversal.Read<uint>(rc,device,work.Buffer,8);Check(tasks[6]==0x1357&&tasks[7]==0x1357,"work-list overflow sentinel");
                Bind();rc.ComputeShader.Set(refine);rc.ComputeShader.SetShaderResources(60,count.Srv,work.Srv);rc.ComputeShader.SetUnorderedAccessViews(0,pool.Uav,null,null,stats.Uav);rc.Dispatch(1,1,1);rc.ClearState();
                uint segments=Traversal.Read<uint>(rc,device,stats.Buffer,4)[0];Check(segments>3&&segments<=15,"one primary plus at most four lighting segments per receiver");
                var values=Traversal.Read<RefinementSample>(rc,device,pool.Buffer,4);var raw=Traversal.Read<uint>(rc,device,pool.Buffer,192);
                for(int i=144;i<192;i++)Check(raw[i]==0x5a5a,"sparse-pool overflow sentinel");
                for(int i=0;i<3;i++)Check(values[i].Metadata.X==1&&values[i].Identity==1&&values[i].DirectDiffuse.X>0&&values[i].PixelX<31&&values[i].PixelY<29,"fresh full transport result and full-resolution coordinate");
                var raster=Keep(new RasterizerState(device,new RasterizerStateDescription{FillMode=FillMode.Solid,CullMode=CullMode.None,IsDepthClipEnabled=true}));
                void Draw(){rc.ClearState();rc.UpdateSubresource(ref p,cb);rc.PixelShader.SetConstantBuffer(1,cb);rc.PixelShader.SetShaderResources(24,pos.Srv,normal.Srv,null,identity.Srv);rc.PixelShader.SetShaderResource(29,radiance.Srv);rc.PixelShader.SetShaderResource(31,meta.Srv);rc.PixelShader.SetShaderResource(34,sun.Srv);rc.PixelShader.SetShaderResources(62,pool.Srv,lookup.Srv);rc.Rasterizer.State=raster;rc.Rasterizer.SetViewport(0,0,31,29);rc.InputAssembler.PrimitiveTopology=SharpDX.Direct3D.PrimitiveTopology.TriangleList;rc.VertexShader.Set(fullscreen);rc.PixelShader.Set(composite);rc.OutputMerger.SetRenderTargets(target.Rtv);rc.Draw(3,0);rc.ClearState();}
                int Matching(float red,float green,float blue)
                {
                    var desc=target.Texture.Description;desc.Usage=ResourceUsage.Staging;desc.BindFlags=0;desc.CpuAccessFlags=CpuAccessFlags.Read;desc.OptionFlags=0;
                    using(var stage=new Texture2D(device,desc))
                    {
                        rc.CopyResource(target.Texture,stage);var map=rc.MapSubresource(stage,0,MapMode.Read,SharpDX.Direct3D11.MapFlags.None);int selected=0;
                        try{for(int y=0;y<29;y++)for(int x=0;x<31;x++){
                            float Channel(int c)=>BitConverter.ToSingle(BitConverter.GetBytes(Marshal.ReadInt32(IntPtr.Add(map.DataPointer,y*map.RowPitch+x*16+c*4))),0);
                            if(Math.Abs(Channel(0)-red)<.001f&&Math.Abs(Channel(1)-green)<.001f&&Math.Abs(Channel(2)-blue)<.001f)selected++;
                        }}finally{rc.UnmapSubresource(stage,0);}return selected;
                    }
                }
                int Selected()=>Matching(1,1,0);
                p.ShadowDebugView=6;Draw();Check(Selected()==3,"only three exact full-resolution coordinates composite; no footprint spread");
                for(int i=0;i<3;i++){values[i].GuideQuality=10;values[i].Metadata.Z=.25f;values[i].Metadata.W=.8f;values[i].DirectDiffuse.W=4;values[i].Sun=new Vector4(.9f,.2f,.4f,4);}pool.Upload(rc,values);
                p.ShadowDebugView=4;Draw();Check(Matching(0,1,0)==3,"guide debug uses refined nearest guides at matching coordinates");
                p.ShadowDebugView=5;Draw();Check(Matching(.8f,.125f,0)==3,"confidence and history debug use refined metadata");
                p.ShadowDebugView=2;Draw();Check(Matching(.25f,.25f,.25f)==3,"source debug uses refined selected-source visibility");
                p.ShadowDebugView=7;Draw();Check(Matching(.9f,.2f,.4f)==3,"RGB transmission debug preserves tint");
                p.ShadowDebugView=1;Draw();Check(Matching(.36326f,.36326f,.36326f)==3,"sun visibility debug separates scalar visibility from RGB transmission");
                p.ShadowDebugView=6;
                for(int i=0;i<3;i++)values[i].Metadata=Vector4.Zero;pool.Upload(rc,values);Draw();Check(Selected()==0,"invalid refined receivers retain base path");
                p.RefinementCapacity=0;Draw();Check(Selected()==0,"zero effective allowance disables stale pool lookup");
            }
            Console.WriteLine("PASS "+checks+" sparse-refinement WARP checks: capacity, one candidate per footprint, segment cap, overflow, full transport, odd-size coordinate composite and invalid fallback");
        }
        finally{rc.ClearState();for(int i=resources.Count-1;i>=0;i--)resources[i].Dispose();}
    }
}
