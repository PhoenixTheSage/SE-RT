using System;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using SharpDX;
using SharpDX.Direct3D11;
using SharpDX.D3DCompiler;
using SharpDX.DXGI;
using Buffer=SharpDX.Direct3D11.Buffer;
using Device=SharpDX.Direct3D11.Device;

static class FarSunQuality
{
    [StructLayout(LayoutKind.Sequential)]struct Clip { public Matrix4x4 Inverse; public Vector4 Parameters; }
    sealed class Includes:SharpDX.CallbackBase,Include
    {
        readonly string root,game;
        internal Includes(string root,string game){this.root=root;this.game=game;}
        public Stream Open(IncludeType type,string name,Stream parent){return File.OpenRead(name.StartsWith("Keen/")?Path.Combine(game,name.Substring(5)):Path.Combine(root,name));}
        public void Close(Stream stream)=>stream.Dispose();
    }
    internal static void Run(Device device,string root,string game)
    {
        string anomaly=Path.GetFullPath(Path.Combine(root,"../../../Anomaly/Assets/Shaders"));
        string stub=@"cbuffer Test : register(b12){float4 Alpha;};struct MaterialVertexPayload{float2 uv:TEXCOORD0;};
struct PixelInterface{float3 screen_position;MaterialVertexPayload custom;uint LOD;};struct MaterialOutputInterface{uint LOD;};
void init_ps_interface(inout PixelInterface p){p.LOD=0;}
MaterialOutputInterface make_mat_interface(){return (MaterialOutputInterface)0;}
void pixel_program(PixelInterface p,inout MaterialOutputInterface m){clip(Alpha.x-.5);}
#define ANOMALY_FAR_SUN 1
";
        var rc=device.ImmediateContext;
        using(var target=new Texture2D(device,new Texture2DDescription{Width=1,Height=1,ArraySize=1,MipLevels=1,Format=Format.R32_Typeless,SampleDescription=new SampleDescription(1,0),BindFlags=BindFlags.DepthStencil}))
        using(var dsv=new DepthStencilView(device,target,new DepthStencilViewDescription{Format=Format.D32_Float,Dimension=DepthStencilViewDimension.Texture2D}))
        using(var stage=new Texture2D(device,new Texture2DDescription{Width=1,Height=1,ArraySize=1,MipLevels=1,Format=Format.R32_Typeless,SampleDescription=new SampleDescription(1,0),Usage=ResourceUsage.Staging,CpuAccessFlags=CpuAccessFlags.Read}))
        using(var cb=new Buffer(device,80,ResourceUsage.Default,BindFlags.ConstantBuffer,CpuAccessFlags.None,ResourceOptionFlags.None,0))
        using(var alphaCb=new Buffer(device,16,ResourceUsage.Default,BindFlags.ConstantBuffer,CpuAccessFlags.None,ResourceOptionFlags.None,0))
        using(var raster=new RasterizerState(device,new RasterizerStateDescription{FillMode=FillMode.Solid,CullMode=CullMode.None,IsDepthClipEnabled=true}))
        using(var depth=new DepthStencilState(device,new DepthStencilStateDescription{IsDepthEnabled=true,DepthWriteMask=DepthWriteMask.All,DepthComparison=Comparison.LessEqual}))
        using(var vsCode=ShaderBytecode.Compile("float4 main(uint id:SV_VertexID):SV_Position{float2 uv=float2((id<<1)&2,id&2);return float4(uv*float2(2,-2)+float2(-1,1),.5,1);}","main","vs_5_0"))
        using(var vertex=new VertexShader(device,vsCode))
        {
            int checks=0;
            foreach(bool cutout in new[]{false,true})
            using(var include=new Includes(anomaly,game))
            using(var code=ShaderBytecode.Compile(cutout?stub+File.ReadAllText(Path.Combine(anomaly,"Geometry/Passes/Depth/PixelStage.hlsli")):File.ReadAllText(Path.Combine(anomaly,"FarSunDepth.hlsl")),"__pixel_shader","ps_5_0",ShaderFlags.OptimizationLevel3,EffectFlags.None,null,include))
            using(var shader=new PixelShader(device,code))
            using(var reflection=new ShaderReflection(code))
            {
                if(reflection.GetConstantBuffer("AnomalyFarSunClip").Description.Size!=80||reflection.GetResourceBindingDescription("AnomalyFarSunClip").BindPoint!=13)throw new Exception("Far-sun clip reflection contract");checks++;
                foreach(var sample in new[]{new[]{200f,1,1},new[]{400f,1,0},new[]{400f,0,cutout?1:0}})
                {
                    var clip=new Clip{Inverse=Matrix4x4.CreateTranslation(0,0,sample[0]),Parameters=new Vector4(1,256,0,0)};var alpha=new Vector4(sample[1],0,0,0);
                    rc.ClearState();rc.UpdateSubresource(ref clip,cb);rc.UpdateSubresource(ref alpha,alphaCb);rc.ClearDepthStencilView(dsv,DepthStencilClearFlags.Depth,1,0);
                    rc.OutputMerger.SetTargets(dsv);rc.OutputMerger.SetDepthStencilState(depth);rc.Rasterizer.State=raster;rc.Rasterizer.SetViewport(0,0,1,1);
                    rc.InputAssembler.PrimitiveTopology=SharpDX.Direct3D.PrimitiveTopology.TriangleList;rc.VertexShader.Set(vertex);rc.PixelShader.Set(shader);rc.PixelShader.SetConstantBuffer(13,cb);rc.PixelShader.SetConstantBuffer(12,alphaCb);rc.Draw(3,0);rc.ClearState();
                    rc.CopyResource(target,stage);var map=rc.MapSubresource(stage,0,MapMode.Read,SharpDX.Direct3D11.MapFlags.None);float observed;
                    try{observed=BitConverter.ToSingle(BitConverter.GetBytes(Marshal.ReadInt32(map.DataPointer)),0);}finally{rc.UnmapSubresource(stage,0);}
                    if((observed>=.99f)!=(sample[2]>0))throw new Exception("Far-sun native clipping/cutout regression "+cutout+" "+sample[0]+" "+sample[1]+" -> "+observed);checks++;
                }
            }
            Console.WriteLine("PASS "+checks+" Anomaly far-depth WARP checks: resident sphere exclusion, outside casters, native depth-program cutouts and b13 reflection");
        }
    }
}
