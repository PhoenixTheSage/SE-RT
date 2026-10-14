using System;
using SERT.Core;
using SERT.Rendering;
using SharpDX;
using SharpDX.Direct3D11;
using SharpDX.D3DCompiler;
using SharpDX.DXGI;
using Device = SharpDX.Direct3D11.Device;
using Buffer = SharpDX.Direct3D11.Buffer;
using V3 = System.Numerics.Vector3;
using V4 = System.Numerics.Vector4;
using M4 = System.Numerics.Matrix4x4;

static class Emission
{
    const string Kernel = "#include \"Common.hlsli\"\nRWStructuredBuffer<float4> Result:register(u0);[numthreads(1,1,1)]void main(){Hit h=(Hit)0;h.status=1;h.uv=float2(.25,.25);float3 p,n,c,e;float metal,rough;uint2 id;hitSurface(h,p,n,c,metal,rough,e,id);Result[0]=float4(e,1);float3 d,s,l,bd,bs;float distance,pdf;evaluateLight(0,float2(.25,.5),float3(0,0,-2),float3(0,0,1),float3(0,0,1),.5.xxx,0,.8,d,s,l,distance,pdf);brdf(float3(0,0,1),float3(0,0,1),l,.5.xxx,0,.8,bd,bs);Result[1]=float4((d+s)*pdf/max(bd+bs,1e-8),1);}";
    public static void Run(Device device, string root, string game)
    {
        var rc = device.ImmediateContext;
        using (var vertices = new GpuBuffer(device, 3, 32))
        using (var triangles = new GpuBuffer(device, 1, 16))
        using (var instances = new GpuBuffer(device, 1, 176))
        using (var materials = new GpuBuffer(device, 1, 96))
        using (var lights = new GpuBuffer(device, 1, 80))
        using (var result = new GpuBuffer(device, 2, 16, true))
        using (var tree = new GpuBuffer(device, 2, 4, true))
        using (var cm = new GpuTexture(device, 1, 1, Format.R32G32B32A32_Float, 16, 1, true, false, true))
        using (var ex = new GpuTexture(device, 1, 1, Format.R32G32B32A32_Float, 16, 1, true, false, true))
        using (var constants = new Buffer(device, Utilities.SizeOf<Parameters>(), ResourceUsage.Default, BindFlags.ConstantBuffer, CpuAccessFlags.None, ResourceOptionFlags.None, 0))
        using (var sampler = new SamplerState(device, new SamplerStateDescription { Filter = Filter.MinMagMipLinear, AddressU = TextureAddressMode.Wrap, AddressV = TextureAddressMode.Wrap, AddressW = TextureAddressMode.Wrap, MaximumLod = float.MaxValue }))
        using (var include = new ShaderInclude(root, game))
        using (var code = ShaderBytecode.Compile(Kernel, "main", "cs_5_0", ShaderFlags.OptimizationLevel3, EffectFlags.None, null, include))
        using (var evaluate = new ComputeShader(device, code))
        using (var weights = new ComputeShader(device, Program.Compiled["LightWeights"]))
        {
            vertices.Upload(rc, new[] { new Vertex { Position = new V3(-1, -1, 0), Normal = V3.UnitZ }, new Vertex { Position = new V3(1, -1, 0), Normal = V3.UnitZ }, new Vertex { Position = new V3(0, 1, 0), Normal = V3.UnitZ } });
            triangles.Upload(rc, new[] { new Triangle { A = 0, B = 1, C = 2 } });
            instances.Upload(rc, new[] { new Instance { World = M4.Identity, Inverse = M4.Identity, Identity = 1 } });
            lights.Upload(rc, new[] { new Light { Kind = 2 } });
            rc.ClearRenderTargetView(cm.Slices[0], new SharpDX.Mathematics.Interop.RawColor4(.4f, .5f, .6f, .25f));
            void Case(uint flags, float extension, float bloom)
            {
                materials.Upload(rc, new[] { new Material { Color = V3.One, Emission = new V3(2), Flags = flags } });
                rc.ClearRenderTargetView(ex.Slices[0], new SharpDX.Mathematics.Interop.RawColor4(extension, .5f, 1, 1));
                var p = new Parameters { Vertices = 3, Triangles = 1, Instances = 1, Materials = 1, Lights = 1, TreeLeaves = 1, SunColor = new V4(0, 0, 0, bloom) };
                rc.UpdateSubresource(ref p, constants);
                void Bind() { rc.ComputeShader.SetConstantBuffer(1, constants); rc.ComputeShader.SetShaderResources(16, vertices.Srv, triangles.Srv, null, instances.Srv, materials.Srv, lights.Srv, cm.Srv, ex.Srv); rc.ComputeShader.SetSampler(0, sampler); }
                Bind(); rc.ComputeShader.Set(evaluate); rc.ComputeShader.SetUnorderedAccessView(0, result.Uav); rc.Dispatch(1, 1, 1); rc.ClearState();
                var values = Traversal.Read<V4>(rc, device, result.Buffer, 2);
                float factor = ((flags & 4) == 0 ? Math.Max(0, extension - 1f / 255) : extension) * .75f * 2 * bloom;
                var expected = new V3(.4f, .5f, .6f) * factor;
                for (int i = 0; i < 2; ++i)
                {
                    var actual = new V3(values[i].X, values[i].Y, values[i].Z);
                    if (!Budget.Finite(actual) || V3.Distance(actual, expected) > 1e-4) throw new Exception("Emission mismatch between continuation and area-light sampling: " + actual + " vs " + expected);
                }
                Bind(); rc.ComputeShader.Set(weights); rc.ComputeShader.SetUnorderedAccessView(0, tree.Uav); rc.Dispatch(1, 1, 1); rc.ClearState();
                float power = Traversal.Read<float>(rc, device, tree.Buffer, 2)[1];
                float expectedPower = V3.Dot(expected, new V3(.2126f, .7152f, .0722f)) * 2;
                if (!Budget.Finite(power) || Math.Abs(power - expectedPower) > 1e-4) throw new Exception("Emission power tree differs from sampled radiance");
            }
            Case(0, 1f / 255, 3); Case(0, .75f, 3); Case(4, 1f / 255, 3); Case(0, .75f, 0);
            Console.WriteLine("PASS mesh/voxel emission agreement, native metalness/bloom scaling, compression artifact rejection and power-tree energy");
        }
    }
}
