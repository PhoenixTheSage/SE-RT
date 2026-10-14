using System;
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
using MapFlags=SharpDX.Direct3D11.MapFlags;

static class Estimator
{
    const string Kernel="#include \"Common.hlsli\"\nRWStructuredBuffer<float> Result:register(u0);[numthreads(64,1,1)]void main(uint3 tid:SV_DispatchThreadID){uint i=tid.x;if(i>=Size.x*Size.y)return;Reservoir r=Reservoirs[i];float value=0;if(r.target>0){float3 d,s,l;float distance,pdf;evaluateLight(r.light,r.uv,float3(0,0,-5),float3(0,0,1),float3(0,0,1),.5.xxx,0,.8,d,s,l,distance,pdf);value=luma(d*.5+s)*r.sum/(r.count*r.target);}Result[i]=value;}";
    public static void Run(Device device,string root,string game)
    {
        const int side=128,count=side*side;var rc=device.ImmediateContext;uint one=BitConverter.ToUInt32(BitConverter.GetBytes(1f),0);
        var lightData=new[]{new Light{Kind=0,Position=new Vector3(-2,0,0),Radiance=new Vector3(2),Range=10,Power=1,Instance=one,Texture=one,Identity=1},new Light{Kind=0,Position=new Vector3(2,0,0),Radiance=new Vector3(.2f),Range=10,Power=1,Instance=one,Texture=one,Identity=2}};
        using(var lights=new GpuBuffer(device,2,80))using(var tree=new GpuBuffer(device,4,4))
        using(var initial=new GpuBuffer(device,count,32,true))using(var reused=new GpuBuffer(device,count,32,true))using(var result=new GpuBuffer(device,count,4,true))
        using(var pos=new GpuTexture(device,side,side,Format.R32G32B32A32_Float,16,renderTarget:true))
        using(var normal=new GpuTexture(device,side,side,Format.R32G32B32A32_Float,16,renderTarget:true))
        using(var color=new GpuTexture(device,side,side,Format.R32G32B32A32_Float,16,renderTarget:true))
        using(var identity=new GpuTexture(device,side,side,Format.R32G32_UInt,8))
        using(var constants=new Buffer(device,Utilities.SizeOf<Parameters>(),ResourceUsage.Default,BindFlags.ConstantBuffer,CpuAccessFlags.None,ResourceOptionFlags.None,0))
        using(var sampler=new SamplerState(device,new SamplerStateDescription{Filter=Filter.MinMagMipLinear,AddressU=TextureAddressMode.Wrap,AddressV=TextureAddressMode.Wrap,AddressW=TextureAddressMode.Wrap,MaximumLod=float.MaxValue}))
        using(var candidates=new ComputeShader(device,Program.Compiled["Candidates"]))using(var reuse=new ComputeShader(device,Program.Compiled["Reuse"]))
        using(var include=new ShaderInclude(root,game))using(var code=ShaderBytecode.Compile(Kernel,"main","cs_5_0",ShaderFlags.OptimizationLevel3,EffectFlags.None,null,include))using(var estimate=new ComputeShader(device,code))
        {
            lights.Upload(rc,lightData);tree.Upload(rc,new[]{0f,220,200,20});
            rc.ClearRenderTargetView(pos.Rtv,new SharpDX.Mathematics.Interop.RawColor4(0,0,-5,5));rc.ClearRenderTargetView(normal.Rtv,new SharpDX.Mathematics.Interop.RawColor4(0,0,1,.8f));rc.ClearRenderTargetView(color.Rtv,new SharpDX.Mathematics.Interop.RawColor4(.5f,.5f,.5f,0));rc.ClearUnorderedAccessView(identity.Uav,new SharpDX.Mathematics.Interop.RawInt4(1,1,0,0));
            var p=new Parameters{Width=side,Height=side,FullWidth=side,FullHeight=side,Lights=2,TreeLeaves=2,Frame=37};rc.UpdateSubresource(ref p,constants);
            void Bind(){rc.ComputeShader.SetConstantBuffer(1,constants);rc.ComputeShader.SetShaderResource(21,lights.Srv);rc.ComputeShader.SetShaderResources(24,pos.Srv,normal.Srv,color.Srv,identity.Srv);rc.ComputeShader.SetShaderResource(33,tree.Srv);rc.ComputeShader.SetSampler(0,sampler);}
            Bind();rc.ComputeShader.Set(candidates);rc.ComputeShader.SetUnorderedAccessView(0,initial.Uav);rc.Dispatch(side/8,side/8,1);rc.ClearState();
            double Mean(GpuBuffer source){Bind();rc.ComputeShader.Set(estimate);rc.ComputeShader.SetShaderResource(28,source.Srv);rc.ComputeShader.SetUnorderedAccessView(0,result.Uav);rc.Dispatch(count/64,1,1);rc.ClearState();using(var stage=new Buffer(device,count*4,ResourceUsage.Staging,BindFlags.None,CpuAccessFlags.Read,ResourceOptionFlags.None,0)){rc.CopyResource(result.Buffer,stage);var box=rc.MapSubresource(stage,0,MapMode.Read,MapFlags.None);try{var values=new float[count];Marshal.Copy(box.DataPointer,values,0,count);double sum=0;foreach(float v in values){if(float.IsNaN(v)||float.IsInfinity(v))throw new Exception("Nonfinite reservoir estimate");sum+=v;}return sum/count;}finally{rc.UnmapSubresource(stage,0);}}}
            double expected=Reference(2)+Reference(.2),fresh=Mean(initial);
            if(Math.Abs(fresh-expected)/expected>.01)throw new Exception("GPU RIS energy mismatch: "+fresh+" vs "+expected);
            Bind();rc.ComputeShader.Set(reuse);rc.ComputeShader.SetShaderResource(28,initial.Srv);rc.ComputeShader.SetUnorderedAccessView(0,reused.Uav);rc.Dispatch(side/8,side/8,1);rc.ClearState();double spatial=Mean(reused);
            if(Math.Abs(spatial-expected)/expected>.015)throw new Exception("GPU reuse energy mismatch");
            Console.WriteLine("PASS GPU fresh RIS and spatial reuse energy against analytic reference ("+count+" samples each)");
        }
    }
    static double Reference(double radiance)
    {
        double distance=Math.Sqrt(29),nl=5/distance,nh=Math.Sqrt((1+nl)/2),vh=nh,rough=.8,a=rough*rough,a2=a*a,den=nh*nh*(a2-1)+1;
        double D=a2/(Math.PI*den*den),k=(rough+1)*(rough+1)/8,G=nl/(nl*(1-k)+k),F=.04+.96*Math.Pow(1-vh,5);
        double diffuse=(1-F)*nl/Math.PI*.5/(1+(1/(.001+Math.Pow(1/1.5,3)))*.29),specular=D*G*F/4;
        return radiance*Math.Pow(1-Math.Pow(distance/10,4),3)*(diffuse+specular);
    }
}
