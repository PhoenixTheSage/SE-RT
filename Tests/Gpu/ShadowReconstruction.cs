using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using SERT.Rendering;
using SharpDX;
using SharpDX.Direct3D11;
using SharpDX.D3DCompiler;
using SharpDX.DXGI;
using Buffer = SharpDX.Direct3D11.Buffer;
using Device = SharpDX.Direct3D11.Device;

static class ShadowReconstruction
{
    public static void Run(Device device)
    {
        var rc = device.ImmediateContext; var resources = new List<IDisposable>();
        GpuTexture Tex(Format format = Format.R32G32B32A32_Float, int layers = 1)
        {
            var texture = new GpuTexture(device,8,8,format,format==Format.R32_Float?4:16,layers,renderTarget:true,array:layers>1);
            resources.Add(texture); return texture;
        }
        void Clear(GpuTexture texture,float x,float y=0,float z=0,float w=0)
        {
            if(texture.Slices!=null)foreach(var slice in texture.Slices)rc.ClearRenderTargetView(slice,new SharpDX.Mathematics.Interop.RawColor4(x,y,z,w));
            else rc.ClearRenderTargetView(texture.Rtv,new SharpDX.Mathematics.Interop.RawColor4(x,y,z,w));
        }
        float Read(GpuTexture texture,int layer=0,int x=3,int y=4,int channel=0)
        {
            var desc=texture.Texture.Description;desc.BindFlags=BindFlags.None;desc.CpuAccessFlags=CpuAccessFlags.Read;desc.Usage=ResourceUsage.Staging;desc.OptionFlags=ResourceOptionFlags.None;
            using(var staging=new Texture2D(device,desc))
            {
                rc.CopyResource(texture.Texture,staging);var box=rc.MapSubresource(staging,layer,MapMode.Read,SharpDX.Direct3D11.MapFlags.None);
                try{return BitConverter.ToSingle(BitConverter.GetBytes(Marshal.ReadInt32(IntPtr.Add(box.DataPointer,y*box.RowPitch+x*(desc.Format==Format.R32_Float?4:16)+channel*4))),0);}
                finally{rc.UnmapSubresource(staging,layer);}
            }
        }
        try
        {
            var pos=Tex();var oldPos=Tex();var normal=Tex();var oldNormal=Tex();
            var identity=Tex(Format.R32G32_UInt);var oldIdentity=Tex(Format.R32G32_UInt);
            var metadata=Tex();var oldMetadata=Tex();var current=Tex(layers:4);var previous=Tex(layers:4);var filtered=Tex(layers:4);
            var moments=Tex();var outMoments=Tex();var sun=Tex();var oldSun=Tex();var outSun=Tex();
            var sourceKey=Tex(Format.R32G32B32A32_UInt);var previousKey=Tex(Format.R32G32B32A32_UInt);
            rc.ClearUnorderedAccessView(sourceKey.Uav,new SharpDX.Mathematics.Interop.RawInt4(1,7,0,0));rc.ClearUnorderedAccessView(previousKey.Uav,new SharpDX.Mathematics.Interop.RawInt4(1,7,0,0));
            var motion=Tex(Format.R32G32_Float);var atrous=Tex(layers:4);var atrousSun=Tex();
            var fast=Tex();var oldFast=Tex();var sunFast=Tex();var oldSunFast=Tex();var guide=Tex();var oldGuide=Tex();
            Clear(oldFast,5,5,5,5);Clear(oldSunFast,1,1,1,4);Clear(guide,0);Clear(oldGuide,0);
            Clear(pos,0,0,-5,5);Clear(oldPos,0,0,-5,5);Clear(normal,0,0,1,.8f);Clear(oldNormal,0,0,1,.8f);
            rc.ClearUnorderedAccessView(identity.Uav,new SharpDX.Mathematics.Interop.RawInt4(1,7,0,0));rc.ClearUnorderedAccessView(oldIdentity.Uav,new SharpDX.Mathematics.Interop.RawInt4(1,7,0,0));
            Clear(metadata,1,1,0,1);Clear(oldMetadata,1,1,1,1);Clear(current,0,0,0,1);Clear(previous,5,5,5,8);Clear(moments,25,25,25,25);
            Clear(sun,0,0,0,1);Clear(oldSun,1,1,1,8);Clear(motion,0);
            using(var constants=new Buffer(device,Utilities.SizeOf<Parameters>(),ResourceUsage.Default,BindFlags.ConstantBuffer,CpuAccessFlags.None,ResourceOptionFlags.None,0))
            using(var temporal=new ComputeShader(device,Program.Compiled["Temporal"]))
            using(var spatial=new ComputeShader(device,Program.Compiled["Atrous"]))
            {
                var p=new Parameters{Width=8,Height=8,FullWidth=8,FullHeight=8,History=1,PreviousViewProjection=System.Numerics.Matrix4x4.Identity};
                rc.UpdateSubresource(ref p,constants);
                void Temporal()
                {
                    rc.ComputeShader.Set(temporal);rc.ComputeShader.SetConstantBuffer(1,constants);
                    rc.ComputeShader.SetShaderResources(24,pos.Srv,normal.Srv,null,identity.Srv,null,current.Srv,oldPos.Srv,metadata.Srv,moments.Srv);
                    rc.ComputeShader.SetShaderResources(34,sun.Srv,motion.Srv,oldNormal.Srv,oldIdentity.Srv,previous.Srv);
                    rc.ComputeShader.SetShaderResources(51,sourceKey.Srv,previousKey.Srv);rc.ComputeShader.SetShaderResource(45,oldSun.Srv);rc.ComputeShader.SetShaderResource(47,oldMetadata.Srv);
                    rc.ComputeShader.SetShaderResources(53,guide.Srv,oldGuide.Srv,null,oldFast.Srv,null,oldSunFast.Srv);
                    rc.ComputeShader.SetUnorderedAccessViews(0,filtered.Uav,outMoments.Uav,outSun.Uav,null,fast.Uav,sunFast.Uav);rc.Dispatch(1,1,1);rc.ClearState();
                }
                Temporal();
                if(Read(filtered)>.00001f || Read(outSun)>.00001f)throw new Exception("Changed same-source/sun visibility retained a bright shadow trail");
                Clear(metadata,1,1,1,1);Temporal();
                if(Read(filtered)<=0)throw new Exception("Unchanged visibility rejected usable temporal history");
                Clear(metadata,1,1,0,1);rc.ClearUnorderedAccessView(sourceKey.Uav,new SharpDX.Mathematics.Interop.RawInt4(2,7,0,0));Temporal();
                if(Read(filtered)<=0)throw new Exception("Different selected lights were treated as a changed same-source shadow");
                // Repeated stochastic visibility changes must accumulate with finite sources.
                p.LampRadius=.025f;p.SolarRadius=.00464f;p.History=0;
                Clear(previous,0);Clear(oldSun,0);Clear(moments,0);Clear(oldFast,0);Clear(oldSunFast,0);
                rc.ClearUnorderedAccessView(sourceKey.Uav,new SharpDX.Mathematics.Interop.RawInt4(1,7,0,0));
                void Advance()
                {
                    rc.CopyResource(filtered.Texture,previous.Texture);rc.CopyResource(outSun.Texture,oldSun.Texture);
                    rc.CopyResource(outMoments.Texture,moments.Texture);rc.CopyResource(fast.Texture,oldFast.Texture);rc.CopyResource(sunFast.Texture,oldSunFast.Texture);
                    rc.CopyResource(metadata.Texture,oldMetadata.Texture);rc.CopyResource(sourceKey.Texture,previousKey.Texture);
                    p.History=1;
                }
                for(int frame=0;frame<48;frame++)
                {
                    float visible=frame%2;Clear(current,visible,visible,visible,1);Clear(sun,visible,.2f*visible,.6f*visible,1);Clear(metadata,1,1,visible,1);
                    rc.UpdateSubresource(ref p,constants);Temporal();Advance();
                }
                if(Read(filtered,channel:3)!=32||Read(outSun,channel:3)!=32||Read(sunFast,channel:3)!=4)throw new Exception("Soft stochastic shadows did not reach 32-frame stable / 4-frame fast history");
                if(Read(outSun)<.35f||Read(outSun)>.65f||Math.Abs(Read(outSun,channel:1)-Read(outSun)*.2f)>.00001f||Math.Abs(Read(outSun,channel:2)-Read(outSun)*.6f)>.00001f)throw new Exception("RGB solar history lost transmission ratios or penumbra energy");
                // A caster leaving a stable penumbra must not leave a 32-frame trail.
                for(int frame=0;frame<8;frame++){Clear(current,1,1,1,1);Clear(sun,1,.2f,.6f,1);Clear(metadata,1,1,1,1);rc.UpdateSubresource(ref p,constants);Temporal();Advance();}
                if(Read(outSun)<.9f||Read(filtered)<.9f)throw new Exception("Fast/stable comparisons did not respond to a moving penumbra within eight frames");
                Clear(metadata,1,1,1,.1f);Clear(current,1,1,1,1);Clear(sun,1,.2f,.6f,1);rc.UpdateSubresource(ref p,constants);Temporal();
                if(Read(filtered,channel:3)>2||Read(outSun,channel:3)>2)throw new Exception("Low-confidence history exceeded two frames");
                Clear(metadata,1,1,1,1);Clear(oldPos,0,0,-8,8);Temporal();
                if(Read(filtered,channel:3)!=1||Read(outSun,channel:3)!=1)throw new Exception("Disoccluded receiver retained temporal history");
                Clear(oldPos,0,0,-5,5);rc.ClearUnorderedAccessView(oldIdentity.Uav,new SharpDX.Mathematics.Interop.RawInt4(1,8,0,0));Temporal();
                if(Read(filtered,channel:3)!=1)throw new Exception("Changed receiver revision retained history");
                rc.ClearUnorderedAccessView(oldIdentity.Uav,new SharpDX.Mathematics.Interop.RawInt4(1,7,0,0));Clear(oldNormal,1,0,0,.8f);Temporal();
                if(Read(filtered,channel:3)!=1)throw new Exception("Changed geometric normal retained history");
                Clear(oldNormal,0,0,1,.8f);p.LampRadius=0;p.SolarRadius=0;rc.UpdateSubresource(ref p,constants);
                // A deterministic directional shadow boundary must survive all three spatial steps.
                using(var code=ShaderBytecode.Compile("RWTexture2D<float4> Out:register(u0);[numthreads(8,8,1)]void main(uint3 p:SV_DispatchThreadID){Out[p.xy]=float4((p.x<4?0:1).xxx,1);}","main","cs_5_0"))
                using(var pattern=new ComputeShader(device,code))
                {
                    rc.ComputeShader.Set(pattern);rc.ComputeShader.SetUnorderedAccessView(0,sun.Uav);rc.Dispatch(1,1,1);rc.ClearState();
                }
                for(uint step=0;step<3;step++)
                {
                    p.Pass=step;rc.UpdateSubresource(ref p,constants);rc.ComputeShader.Set(spatial);rc.ComputeShader.SetConstantBuffer(1,constants);
                    rc.ComputeShader.SetShaderResources(24,pos.Srv,normal.Srv,null,identity.Srv,null,current.Srv);
                    rc.ComputeShader.SetShaderResources(31,metadata.Srv,moments.Srv,null,sun.Srv);
                    rc.ComputeShader.SetUnorderedAccessViews(0,atrous.Uav,atrousSun.Uav);rc.Dispatch(1,1,1);rc.ClearState();
                    if(Read(atrousSun)>.00001f || Read(atrousSun,x:4)<.99999f)throw new Exception("Spatial reconstruction leaked across a certified sun shadow boundary");
                }
                Console.WriteLine("PASS production shadow reconstruction: hard contacts, source keys, 48-frame stochastic RGB penumbrae, moving-penumbra response, 32/4/2 history limits, disocclusion/revision/normal rejection and three spatial steps");
            }
        }
        finally{rc.ClearState();for(int i=resources.Count-1;i>=0;i--)resources[i].Dispose();}
    }
}
