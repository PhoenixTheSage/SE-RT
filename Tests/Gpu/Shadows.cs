using System;
using System.IO;
using System.Numerics;
using SERT.Core;
using SERT.Rendering;
using SharpDX;
using SharpDX.Direct3D11;
using SharpDX.D3DCompiler;
using SharpDX.DXGI;
using Buffer = SharpDX.Direct3D11.Buffer;
using Device = SharpDX.Direct3D11.Device;

static class Shadows
{
    const string Kernel = "#include \"Shadows.hlsli\"\nRWStructuredBuffer<float4> Results:register(u0);" +
        "[numthreads(1,1,1)]void main(){float3 ld,ls,d;float distance,pdf;" +
        "evaluateLight(0,float2(4.0/9,.5),CameraDelta.xyz,float3(0,0,1),float3(0,0,1),1,0,.8,ld,ls,d,distance,pdf);" +
        "if(Iteration==1){d=float3(0,0,1);distance=128;}bool valid;bool vis=visible(CameraDelta.xyz,float3(0,0,1),d,distance,Iteration==1||Lights[0].kind==3,valid);Results[0]=float4(vis,valid,luma(ld+ls),float(shadowSampleToken(Lights[0],float2(.25,Exposure))));}";
    static uint Bits(float value) => BitConverter.ToUInt32(BitConverter.GetBytes(value), 0);
    public static void Run(Device device, string root, string game)
    {
        var rc = device.ImmediateContext; int checks = 0;
        using (var vertices = new GpuBuffer(device, 6, 32))
        using (var triangles = new GpuBuffer(device, 2, 16))
        using (var nodes = new GpuBuffer(device, 2, 32))
        using (var instances = new GpuBuffer(device, 2, 176))
        using (var materials = new GpuBuffer(device, 1, 96))
        using (var lights = new GpuBuffer(device, 1, 80))
        using (var result = new GpuBuffer(device, 1, 16, true))
        using (var statistics = new GpuBuffer(device, 4, 4, true))
        using (var color = new GpuTexture(device, 1, 1, Format.R32G32B32A32_Float, 16, renderTarget: true, writable: false, array: true))
        using (var extension = new GpuTexture(device, 1, 1, Format.R32G32B32A32_Float, 16, renderTarget: true, writable: false, array: true))
        using (var cube = new Texture2D(device, new Texture2DDescription { Width = 1, Height = 1, ArraySize = 6, MipLevels = 1, Format = Format.R32G32B32A32_Float,
            SampleDescription = new SampleDescription(1, 0), BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget, OptionFlags = ResourceOptionFlags.TextureCube }))
        using (var cubeView = new ShaderResourceView(device, cube))
        using (var constants = new Buffer(device, Utilities.SizeOf<Parameters>(), ResourceUsage.Default, BindFlags.ConstantBuffer, CpuAccessFlags.None, ResourceOptionFlags.None, 0))
        using (var sampler = new SamplerState(device, new SamplerStateDescription { Filter = Filter.MinMagMipLinear, AddressU = TextureAddressMode.Wrap, AddressV = TextureAddressMode.Wrap, AddressW = TextureAddressMode.Wrap, MaximumLod = float.MaxValue }))
        using (var include = new ShaderInclude(root, game))
        using (var code = ShaderBytecode.Compile(Kernel, "main", "cs_5_0", ShaderFlags.OptimizationLevel3, EffectFlags.None, null, include))
        using (var shader = new ComputeShader(device, code))
        {
            for (int i = 0; i < 6; i++) using (var face = new RenderTargetView(device, cube, new RenderTargetViewDescription { Format = Format.R32G32B32A32_Float,
                Dimension = RenderTargetViewDimension.Texture2DArray, Texture2DArray = new RenderTargetViewDescription.Texture2DArrayResource { ArraySize = 1, FirstArraySlice = i } }))
                rc.ClearRenderTargetView(face, new SharpDX.Mathematics.Interop.RawColor4(1, 1, 1, 1));
            var p = new Parameters { Vertices = 6, Triangles = 2, Instances = 1, Nodes = 2, Materials = 1, Lights = 1, Tlas = 1, ShadowBias = .002f, CollectStatistics = 1, SunColor = Vector4.One };
            var lamp = new Light { Position = new System.Numerics.Vector3(0, 0, 5), Range = 100, Radiance = System.Numerics.Vector3.One,
                Power = 1, Direction = -System.Numerics.Vector3.UnitZ, ConeCos = .8f, Instance = Bits(1), Texture = Bits(1) };
            var material = new Material { Color = System.Numerics.Vector3.One, Emission = System.Numerics.Vector3.One };
            var known = new Instance { World = Matrix4x4.Identity, Inverse = Matrix4x4.Identity, Root = 0, Identity = 1, Min = new System.Numerics.Vector3(-8, -4, 0), Max = new System.Numerics.Vector3(8, 8, 256) };
            var unknown = new Instance { Root = uint.MaxValue, Min = new System.Numerics.Vector3(-2, -2, 1), Max = new System.Numerics.Vector3(2, 2, 2) };
            void Scene(float first, float second = 400)
            {
                vertices.Upload(rc, new[] {
                    new Vertex { Position = new System.Numerics.Vector3(-8,-4,first), Normal = -System.Numerics.Vector3.UnitZ }, new Vertex { Position = new System.Numerics.Vector3(8,-4,first), Normal = -System.Numerics.Vector3.UnitZ }, new Vertex { Position = new System.Numerics.Vector3(0,8,first), Normal = -System.Numerics.Vector3.UnitZ },
                    new Vertex { Position = new System.Numerics.Vector3(-8,-4,second), Normal = -System.Numerics.Vector3.UnitZ }, new Vertex { Position = new System.Numerics.Vector3(8,-4,second), Normal = -System.Numerics.Vector3.UnitZ }, new Vertex { Position = new System.Numerics.Vector3(0,8,second), Normal = -System.Numerics.Vector3.UnitZ } });
                triangles.Upload(rc, new[] { new Triangle { A = 0, B = 1, C = 2 }, new Triangle { A = 3, B = 4, C = 5 } });
                nodes.Upload(rc, new[] { new BvhNode { Min = new System.Numerics.Vector3(-8,-4,0), Max = new System.Numerics.Vector3(8,8,500), Count = 2 },
                    new BvhNode { Min = new System.Numerics.Vector3(-8,-4,0), Max = new System.Numerics.Vector3(8,8,500), Count = p.Instances } });
                instances.Upload(rc, new[] { known, unknown });
            }
            System.Numerics.Vector4 Dispatch()
            {
                lights.Upload(rc, new[] { lamp }); materials.Upload(rc, new[] { material }); rc.UpdateSubresource(ref p, constants);
                rc.ClearRenderTargetView(color.Slices[0], new SharpDX.Mathematics.Interop.RawColor4(1,1,1,0));
                rc.ComputeShader.Set(shader); rc.ComputeShader.SetConstantBuffer(1, constants);
                rc.ComputeShader.SetShaderResources(16, vertices.Srv, triangles.Srv, nodes.Srv, instances.Srv, materials.Srv, lights.Srv, color.Srv, extension.Srv);
                rc.ComputeShader.SetShaderResource(43, cubeView); rc.ComputeShader.SetSamplers(0, sampler, sampler);
                rc.ComputeShader.SetUnorderedAccessViews(0, result.Uav, null, null, statistics.Uav); rc.Dispatch(1,1,1); rc.ClearState();
                return Traversal.Read<System.Numerics.Vector4>(rc, device, result.Buffer, 1)[0];
            }
            void Check(bool lit, bool valid, string label, bool energy = false)
            {
                var value = Dispatch(); checks++;
                if ((value.X > .5f) != lit || (value.Y > .5f) != valid || (energy && value.Z <= 0))
                    throw new Exception("Shadow " + label + ": " + value);
            }
            rc.ClearRenderTargetView(extension.Slices[0], new SharpDX.Mathematics.Interop.RawColor4(1,.8f,1,1));
            Scene(3); Check(false,true,"point blocker",true);
            float nativeToken=Dispatch().W;p.Exposure=1;checks++;
            if(Dispatch().W!=nativeToken)throw new Exception("Punctual shadow history token depends on unused sample UV");p.Exposure=0;
            lamp.Kind=1; Check(false,true,"spot blocker",true);
            lamp.Kind=2; lamp.Triangle=1; lamp.Instance=0; lamp.Texture=0; Scene(3,5); Check(false,true,"emissive blocker",true);
            float areaToken=Dispatch().W;p.Exposure=1;checks++;
            if(Dispatch().W==areaToken)throw new Exception("Area shadow history token does not distinguish stochastic samples");p.Exposure=0;
            Scene(6,5); Check(true,true,"emissive endpoint excludes source surface",true);
            lamp.Kind=4; lamp.Position=new System.Numerics.Vector3(0,0,5); lamp.Range=.5f; lamp.ConeCos=(float)Math.PI*.25f; Scene(3); Check(false,true,"emitter disc blocker",true);
            lamp.Kind=3; Check(false,true,"environment blocker",true);
            p.Pass=1; Check(false,true,"directional sun blocker");
            Scene(200); Check(true,true,"sun local horizon",false); p.Pass=0;
            lamp.Kind=0; lamp.Instance=Bits(1); lamp.Texture=Bits(1); lamp.Range=100;
            Scene(6); Check(true,true,"blocker behind point endpoint",true);
            Scene(0); Check(true,true,"receiver does not self shadow",true);
            Scene(.001f); p.ShadowBias=.00025f; Check(false,true,"thin contact with small bias",true); p.ShadowBias=.002f;
            p.Instances=2; Scene(3); Check(false,true,"unknown nearer than certified blocker",true);
            Scene(6); Check(false,false,"unknown alone cannot certify visibility");
            p.ShadowFlags=1; Check(true,true,"shadow comparison toggle"); p.ShadowFlags=0;
            p.Instances=1; material.Flags=1; rc.ClearRenderTargetView(extension.Slices[0], new SharpDX.Mathematics.Interop.RawColor4(1,.8f,0,1)); Scene(3);
            Check(true,true,"alpha cutout",true);
            material.Flags=1|8; Check(false,false,"unready alpha texture");
            material.Flags=2; Check(false,false,"transparent coverage remains unknown");
            material.Flags=0; rc.ClearRenderTargetView(extension.Slices[0],new SharpDX.Mathematics.Interop.RawColor4(1,.8f,1,1));
            lamp.Position=new System.Numerics.Vector3(0,0,200); lamp.Range=1000; Scene(160); Check(false,true,"finite light beyond old 128 m cutoff",true);
            Scene(220); Check(true,true,"finite 200 m source endpoint",true);
            lamp.Position=new System.Numerics.Vector3(0,0,500); Scene(3); Check(false,true,"known blocker before residency horizon",true);
            Scene(400); Check(false,false,"uncaptured far segment",true);
            p.CameraDelta=new System.Numerics.Vector4(0,0,260,0); Check(false,false,"receiver outside residency"); p.CameraDelta=System.Numerics.Vector4.Zero;
            nodes.Upload(rc,new[]{new BvhNode(),new BvhNode{Min=new System.Numerics.Vector3(-8,-4,0),Max=new System.Numerics.Vector3(8,8,500),Left=1}});
            Check(false,false,"bounded traversal overflow");
            Console.WriteLine("PASS " + checks + " production shadow checks: point, spot, sun, emissive/disc/environment, endpoints, bias, cutouts, unknown coverage, residency and overflow");
        }
    }
}
