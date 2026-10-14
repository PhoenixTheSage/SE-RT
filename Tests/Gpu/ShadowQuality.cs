using System;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using SERT.Core;
using SERT.Rendering;
using SharpDX;
using SharpDX.Direct3D11;
using SharpDX.D3DCompiler;
using SharpDX.DXGI;
using Buffer=SharpDX.Direct3D11.Buffer;
using Device=SharpDX.Direct3D11.Device;

static class ShadowQuality
{
    const int Samples=4096;
    const string Kernel=@"#include ""Shadows.hlsli""
RWStructuredBuffer<float4> Results:register(u0);RWStructuredBuffer<uint4> Keys:register(u1);
[numthreads(64,1,1)]void main(uint3 id:SV_DispatchThreadID){uint2 pixel=uint2(id.x&63,id.x>>6);float2 uv=sampleContinuous(pixel,0);ShadowResult r;
if(Iteration>=4){Hit h;if(Iteration==4)h=tracePrimary(CameraDelta.xyz,float3(0,0,1),PrimaryRange);else h=trace(CameraDelta.xyz,float3(0,0,1),PrimaryRange);r=(ShadowResult)0;r.status=h.status;r.blocker=h.distance;}
else if(Iteration<2)r=traceVisibility(CameraDelta.xyz,float3(0,0,1),PrimaryRange,Iteration==1);
else if(Iteration==2)r=sunShadow(CameraDelta.xyz,float3(0,0,1),1e-6,uv,true);
else r=lampShadow(Lights[0],uv,CameraDelta.xyz,float3(0,0,1),1e-6,float3(0,0,1),5,false);
Results[id.x*2]=float4(r.transmission,r.status);Results[id.x*2+1]=float4(r.blocker,r.quality,uv);Keys[id.x]=shadowKey(Lights[0],uv);}";
    internal static void Run(Device device,string root,string game)
    {
        int checks=0;void Check(bool ok,string message){checks++;if(!ok)throw new Exception("Shadow quality: "+message);}
        var rc=device.ImmediateContext;
        using(var vb=new GpuBuffer(device,90,32))using(var tb=new GpuBuffer(device,30,16))using(var nb=new GpuBuffer(device,2,32))
        using(var ib=new GpuBuffer(device,1,176))using(var mb=new GpuBuffer(device,3,96))using(var lb=new GpuBuffer(device,1,80))
        using(var result=new GpuBuffer(device,Samples*2,16,true))using(var keys=new GpuBuffer(device,Samples,16,true))
        using(var color=new GpuTexture(device,1,1,Format.R32G32B32A32_Float,16,renderTarget:true,writable:false,array:true))
        using(var far=new GpuTexture(device,2048,2048,Format.R32_Float,4,renderTarget:true,writable:false))
        using(var noise=new GpuTexture(device,16,16,Format.R8G8B8A8_UInt,4,8,writable:false,array:true))
        using(var constants=new Buffer(device,Utilities.SizeOf<Parameters>(),ResourceUsage.Default,BindFlags.ConstantBuffer,CpuAccessFlags.None,ResourceOptionFlags.None,0))
        using(var sampler=new SamplerState(device,new SamplerStateDescription{Filter=Filter.MinMagMipLinear,AddressU=TextureAddressMode.Wrap,AddressV=TextureAddressMode.Wrap,AddressW=TextureAddressMode.Wrap,MaximumLod=float.MaxValue}))
        using(var include=new ShaderInclude(root,game))using(var code=ShaderBytecode.Compile(Kernel,"main","cs_5_0",ShaderFlags.OptimizationLevel1,EffectFlags.None,null,include))
        using(var shader=new ComputeShader(device,code))
        {
            var p=new Parameters{FullWidth=64,FullHeight=64,Width=64,Height=64,Vertices=90,Triangles=30,Instances=1,Nodes=2,Materials=3,Lights=1,Tlas=1,Primary=10,GlassTransmission=.9f,SamplingMode=0};
            var material=new Material{Flags=32,Color=Vector3.One,Optical=new Vector4(1,.5f,.25f,.5f),OpticalUv=new Vector4(0,0,1,1)};
            var lamp=new Light{Position=new Vector3(0,0,5),Identity=0xffeeddcc,Revision=0xfedcba98};
            byte[] asset=File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(root),"Sampling/blue-noise-16x16x8.rgba"));
            var pin=GCHandle.Alloc(asset,GCHandleType.Pinned);
            try{for(int slice=0;slice<8;slice++)rc.UpdateSubresource(new DataBox(IntPtr.Add(pin.AddrOfPinnedObject(),slice*1024),64,1024),noise.Texture,slice);}finally{pin.Free();}
            Check(asset.Length==8192,"offline mask dimensions");
            for(int channel=0;channel<4;channel++){int[] counts=new int[256];for(int i=channel;i<asset.Length;i+=4)counts[asset[i]]++;foreach(int count in counts)Check(count==8,"uniform scalar rank distribution");}
            rc.ClearRenderTargetView(color.Slices[0],new SharpDX.Mathematics.Interop.RawColor4(1,1,1,1));
            rc.ClearRenderTargetView(far.Rtv,new SharpDX.Mathematics.Interop.RawColor4(1,1,1,1));
            void Scene(float[] depths,uint[] materials,bool half=false)
            {
                var vertices=new Vertex[90];var triangles=new Triangle[30];
                for(int i=0;i<depths.Length;i++){float z=depths[i];vertices[i*3].Position=new Vector3(half?0:-10,-10,z);vertices[i*3+1].Position=new Vector3(10,-10,z);vertices[i*3+2].Position=new Vector3(0,10,z);triangles[i]=new Triangle{A=(uint)(i*3),B=(uint)(i*3+1),C=(uint)(i*3+2),Material=materials[i]};}
                vb.Upload(rc,vertices);tb.Upload(rc,triangles);
                nb.Upload(rc,new[]{new BvhNode{Min=new Vector3(-10,-10,0),Max=new Vector3(10,10,256),Count=(uint)depths.Length},new BvhNode{Min=new Vector3(-10,-10,0),Max=new Vector3(10,10,256),Count=1}});
                ib.Upload(rc,new[]{new Instance{World=Matrix4x4.Identity,Inverse=Matrix4x4.Identity,Root=0,Min=new Vector3(-10,-10,0),Max=new Vector3(10,10,256)}});
            }
            Vector4[] Dispatch(int count=1)
            {
                mb.Upload(rc,new[]{material,new Material(),new Material{Flags=2}});lb.Upload(rc,new[]{lamp});rc.UpdateSubresource(ref p,constants);
                rc.ComputeShader.Set(shader);rc.ComputeShader.SetConstantBuffer(1,constants);rc.ComputeShader.SetShaderResources(16,vb.Srv,tb.Srv,nb.Srv,ib.Srv,mb.Srv,lb.Srv,color.Srv,color.Srv);
                rc.ComputeShader.SetShaderResource(50,noise.Srv);rc.ComputeShader.SetShaderResource(59,far.Srv);rc.ComputeShader.SetSamplers(0,sampler,sampler);
                rc.ComputeShader.SetUnorderedAccessViews(0,result.Uav,keys.Uav);rc.Dispatch((count+63)/64,1,1);rc.ClearState();return Traversal.Read<Vector4>(rc,device,result.Buffer,Samples*2);
            }
            void Near(Vector4 actual,Vector3 expected,string label){Check(Vector3.Distance(new Vector3(actual.X,actual.Y,actual.Z),expected)<2e-5&&actual.W==0,label+" "+actual);}
            Scene(new[]{1f},new[]{0u});Near(Dispatch()[0],new Vector3(.95f,.725f,.6125f),"single tinted layer");
            Scene(new[]{1f,1f},new[]{0u,0u});Near(Dispatch()[0],new Vector3(.95f,.725f,.6125f),"coincident coplanar triangles deduplicate");
            var depths=new float[8];var mats=new uint[8];for(int i=0;i<8;i++)depths[i]=i+1;
            Scene(depths,mats);Near(Dispatch()[0],new Vector3((float)Math.Pow(.95,8),(float)Math.Pow(.725,8),(float)Math.Pow(.6125,8)),"eight distinct layers compose");
            Array.Resize(ref depths,9);Array.Resize(ref mats,9);depths[8]=9;Scene(depths,mats);Check(Dispatch()[0].W==2,"ninth layer remains incomplete");
            Array.Resize(ref depths,10);Array.Resize(ref mats,10);depths[9]=9.5f;mats[9]=1;Scene(depths,mats);Check(Dispatch()[0]==new Vector4(0,0,0,1),"opaque blocker certifies black after excess layers");
            Scene(new[]{1f,2f},new[]{2u,1u});Check(Dispatch()[0].W==1,"opaque blocker certifies black behind unsupported material");
            Scene(new[]{3f,1f},new[]{1u,1u});var any=Dispatch();Check(any[1].X==3&&any[1].Y==1,"any-hit guide classified approximate");
            p.Pass=1;var nearest=Dispatch();Check(nearest[1].X==1&&nearest[1].Y==2,"bounded nearest guide differs from arbitrary blocker");p.Pass=0;
            material.Flags=32|64;Scene(new[]{1f},new[]{0u});Near(Dispatch()[0],Vector3.One,"one-sided glass rejects back faces");material.Flags=32;
            material.Flags=32|2;Scene(new[]{1f,2f},new[]{0u,1u});p.Pass=4;var primary=Dispatch();Check(primary[0].W==1&&primary[1].X==2,"opaque primary confirmation passes supported native glass");
            p.Pass=5;Check(Dispatch()[0].W==2,"continuation through glass remains unsupported");p.Pass=4;Scene(new[]{1f,2f},new[]{2u,1u});Check(Dispatch()[0].W==2,"unsupported primary transparent coverage still fails closed");material.Flags=32;
            p.Pass=2;p.FarSunValid=1;p.SunDirection=new Vector4(0,0,1,1);p.FarSunMatrix=Matrix4x4.CreateScale(.001f);p.SolarRadius=.266f*(float)Math.PI/180;
            Scene(new[]{200f},new[]{1u});Check(Dispatch()[0].W==1,"far product extends local solar ray to resident sphere exit");
            Scene(new[]{200f},new[]{0u});Near(Dispatch()[0],new Vector3(.95f,.725f,.6125f),"RGB glass applies to sun");
            rc.ClearRenderTargetView(far.Rtv,new SharpDX.Mathematics.Interop.RawColor4(0,0,0,0));p.CameraDelta=new Vector4(0,0,5,0);Scene(new[]{200f},new[]{2u});Check(Dispatch()[0].W==1,"far opaque coverage certifies black despite local gaps");
            p.CameraDelta=Vector4.Zero;p.Pass=3;p.FarSunValid=0;p.LampRadius=0;Scene(new[]{3f},new[]{1u},true);
            Check(Dispatch()[0].W==1,"radius-zero lamp retains hard center shadow");p.LampRadius=.025f;
            var soft=Dispatch(Samples);double lit=0;for(int i=0;i<Samples;i++)lit+=soft[i*2].X/Samples;
            Check(lit>.4&&lit<.6,"finite lamp disk produces fractional penumbra at fixed ray count "+lit);
            var rawKeys=Traversal.Read<uint>(rc,device,keys.Buffer,Samples*4);Check(rawKeys[0]==lamp.Identity,"source keys retain all 32 identity bits");
            Check(rawKeys[2]!=rawKeys[6]||rawKeys[3]!=rawKeys[7],"finite-source sample coordinates distinguish history keys");
            Console.WriteLine("PASS "+checks+" shadow quality checks: RGB glass, eight-layer and duplicate limits, any/nearest guides, far-sun residency, finite lamps, integer keys and offline ranks");
        }
    }
}
