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
using MapFlags=SharpDX.Direct3D11.MapFlags;

static class Traversal
{
    [StructLayout(LayoutKind.Sequential,Pack=4)]struct RayData { public Vector3 Origin;public float Max;public Vector3 Direction;public float Pad; }
    [StructLayout(LayoutKind.Sequential,Pack=4)]struct Result { public uint Status,Triangle,Instance;public float Distance;public Vector2 Uv; }
    const string Kernel="#include \"Common.hlsli\"\nstruct RayData{float3 o;float maxT;float3 d;float pad;};StructuredBuffer<RayData> Rays:register(t46);RWStructuredBuffer<Hit> Results:register(u0);[numthreads(64,1,1)]void main(uint3 id:SV_DispatchThreadID){if(id.x<Iteration){RayData r=Rays[id.x];Results[id.x]=trace(r.o,r.d,r.maxT);}}";
    public static void Run(Device device,string root,string game)
    {
        var rc=device.ImmediateContext;const int count=512;var random=new Random(91);
        var verts=new[]{new Vertex{Position=new Vector3(-1,-1,0),Normal=Vector3.UnitZ},new Vertex{Position=new Vector3(1,-1,0),Normal=Vector3.UnitZ,U=1},new Vertex{Position=new Vector3(0,1,0),Normal=Vector3.UnitZ,V=1}};
        var triangle=new Triangle{A=0,B=1,C=2};var blas=Bvh.Build(new[]{new Bounds(new Vector3(-1,-1,0),new Vector3(1,1,0))});
        var transforms=new[]{Matrix4x4.CreateScale(2,1,.5f)*Matrix4x4.CreateRotationY(.3f)*Matrix4x4.CreateTranslation(-2,0,-5),Matrix4x4.CreateScale(.5f,3,2)*Matrix4x4.CreateRotationZ(.6f)*Matrix4x4.CreateTranslation(2,1,-8)};
        var inst=new Instance[3];var bounds=new Bounds[3];
        for(int i=0;i<2;i++){var world=transforms[i];Matrix4x4.Invert(world,out var inverse);var b=new Bounds(new Vector3(-1,-1,0),new Vector3(1,1,0)).Transform(world);inst[i]=new Instance{World=world,Inverse=inverse,Root=0,Identity=(uint)(i+1),Min=b.Min,Max=b.Max};bounds[i]=b;}
        bounds[2]=new Bounds(new Vector3(7,-1,-7),new Vector3(8,1,-6));inst[2]=new Instance{Root=uint.MaxValue,Identity=3,Min=bounds[2].Min,Max=bounds[2].Max};
        var tlas=Bvh.Build(bounds,1);var instances=new Instance[3];for(int i=0;i<3;i++)instances[i]=inst[tlas.Order[i]];
        var nodes=new BvhNode[blas.Nodes.Length+tlas.Nodes.Length];Array.Copy(blas.Nodes,nodes,blas.Nodes.Length);
        for(int i=0;i<tlas.Nodes.Length;i++){var n=tlas.Nodes[i];if(!n.IsLeaf)n.Left+=(uint)blas.Nodes.Length;nodes[blas.Nodes.Length+i]=n;}
        var rays=new RayData[count];for(int i=0;i<count;i++)rays[i]=new RayData{Origin=new Vector3((float)random.NextDouble()*12-4,(float)random.NextDouble()*8-4,1),Direction=-Vector3.UnitZ,Max=20};
        rays[0]=new RayData{Origin=new Vector3(7.5f,0,1),Direction=-Vector3.UnitZ,Max=20};
        using(var vb=new GpuBuffer(device,3,32))using(var tb=new GpuBuffer(device,1,16))using(var nb=new GpuBuffer(device,nodes.Length,32))using(var ib=new GpuBuffer(device,3,176))
        using(var mb=new GpuBuffer(device,1,96))using(var rb=new GpuBuffer(device,count,32))using(var result=new GpuBuffer(device,count,24,true))
        using(var atlas=new GpuTexture(device,1,1,Format.R32G32B32A32_Float,16,renderTarget:true,writable:false,array:true))
        using(var constants=new Buffer(device,Utilities.SizeOf<Parameters>(),ResourceUsage.Default,BindFlags.ConstantBuffer,CpuAccessFlags.None,ResourceOptionFlags.None,0))
        using(var sampler=new SamplerState(device,new SamplerStateDescription{Filter=Filter.MinMagMipLinear,AddressU=TextureAddressMode.Wrap,AddressV=TextureAddressMode.Wrap,AddressW=TextureAddressMode.Wrap,MaximumLod=float.MaxValue}))
        using(var include=new ShaderInclude(root,game))using(var code=ShaderBytecode.Compile(Kernel,"main","cs_5_0",ShaderFlags.OptimizationLevel3,EffectFlags.None,null,include))using(var shader=new ComputeShader(device,code))
        {
            vb.Upload(rc,verts);tb.Upload(rc,new[]{triangle});nb.Upload(rc,nodes);ib.Upload(rc,instances);rb.Upload(rc,rays);
            var material=new Material{Color=Vector3.One,Emission=Vector3.Zero};mb.Upload(rc,new[]{material});
            rc.ClearRenderTargetView(atlas.Slices[0],new SharpDX.Mathematics.Interop.RawColor4(0,.5f,1,1));
            var p=new Parameters{Instances=3,Nodes=(uint)nodes.Length,Vertices=3,Triangles=1,Materials=1,Tlas=(uint)blas.Nodes.Length,Pass=count};rc.UpdateSubresource(ref p,constants);
            void Dispatch(){rc.ComputeShader.Set(shader);rc.ComputeShader.SetConstantBuffer(1,constants);rc.ComputeShader.SetShaderResources(16,vb.Srv,tb.Srv,nb.Srv,ib.Srv,mb.Srv);rc.ComputeShader.SetShaderResources(22,atlas.Srv,atlas.Srv);rc.ComputeShader.SetShaderResource(46,rb.Srv);rc.ComputeShader.SetSampler(0,sampler);rc.ComputeShader.SetUnorderedAccessView(0,result.Uav);rc.Dispatch(count/64,1,1);rc.ClearState();}
            Dispatch();var hits=Read<Result>(rc,device,result.Buffer,count);
            int checks=0;
            for(int i=0;i<count;i++)
            {
                float nearest=20;bool found=false,missing=false;var ray=rays[i];
                foreach(var instance in instances){if(!Bvh.IntersectBox(new Ray(ray.Origin,ray.Direction,0,ray.Max),instance.Min,instance.Max,nearest))continue;if(instance.Root==uint.MaxValue){missing=true;continue;}
                    var local=new Ray(Vector3.Transform(ray.Origin,instance.Inverse),Vector3.TransformNormal(ray.Direction,instance.Inverse),.0001f,nearest);
                    if(Bvh.IntersectTriangle(local,verts[0].Position,verts[1].Position,verts[2].Position,out float distance,out _)&&distance<nearest){nearest=distance;found=true;}}
                uint expected=missing?2u:found?1u:0u;if(hits[i].Status!=expected||(!missing&&Math.Abs(hits[i].Distance-nearest)>1e-4))throw new Exception("GPU/brute traversal disagreement at "+i);checks++;
            }
            if(hits[0].Status!=2)throw new Exception("Missing coverage was certified as a miss");
            material.Flags=1;mb.Upload(rc,new[]{material});rc.ClearRenderTargetView(atlas.Slices[0],new SharpDX.Mathematics.Interop.RawColor4(0,.5f,0,1));Dispatch();hits=Read<Result>(rc,device,result.Buffer,count);
            foreach(var hit in hits)if(hit.Status==1)throw new Exception("Alpha cutout blocked a ray");
            // A deliberately cyclic invalid tree proves the work cap returns overflow rather than hanging.
            nodes[0]=new BvhNode{Min=new Vector3(-100),Max=new Vector3(100),Left=0,Count=0};nb.Upload(rc,nodes);material.Flags=0;mb.Upload(rc,new[]{material});Dispatch();hits=Read<Result>(rc,device,result.Buffer,count);
            bool overflow=false;foreach(var hit in hits)overflow|=hit.Status==3;if(!overflow)throw new Exception("Traversal work cap missing");
            Console.WriteLine("PASS "+checks+" GPU/brute intersections, transformed instances, unknown coverage, alpha cutouts and overflow");
        }
    }
    internal static T[] Read<T>(DeviceContext rc,Device d,Buffer source,int count) where T:struct
    {
        int stride=Marshal.SizeOf<T>();using(var staging=new Buffer(d,count*stride,ResourceUsage.Staging,BindFlags.None,CpuAccessFlags.Read,ResourceOptionFlags.None,0)){rc.CopyResource(source,staging);var box=rc.MapSubresource(staging,0,MapMode.Read,MapFlags.None);try{var result=new T[count];for(int i=0;i<count;i++)result[i]=Marshal.PtrToStructure<T>(IntPtr.Add(box.DataPointer,i*stride));return result;}finally{rc.UnmapSubresource(staging,0);}}
    }
}
