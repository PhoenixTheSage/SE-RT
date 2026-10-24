using System;
using System.Numerics;
using System.Runtime.InteropServices;
using SERT.Core;
using SERT.Rendering;

static class Program
{
    static int checks;
    static void Check(bool condition, string name) { checks++; if (!condition) throw new Exception(name); }
    static void Main()
    {
        Check(Marshal.SizeOf<Vertex>() == 32, "vertex shader ABI");
        Check(Marshal.SizeOf<Triangle>() == 16, "triangle shader ABI");
        Check(Marshal.SizeOf<BvhNode>() == 32, "node shader ABI");
        Check(Marshal.SizeOf<Instance>() == 176, "instance shader ABI");
        Check(Marshal.SizeOf<Material>() == 96, "material shader ABI");
        Check(Marshal.SizeOf<ShadowResult>() == 24, "backend-independent shadow-result ABI");
        Check(Marshal.SizeOf<Light>() == 80, "light shader ABI");
        Check(Marshal.SizeOf<SkinVertex>() == 80, "skinning shader ABI");
        Selection();
        Quality.Run(Check);
        var random = new Random(117);
        var vertices = new Vertex[300]; var triangles = new Triangle[100]; var bounds = new Bounds[100];
        for (int i = 0; i < triangles.Length; i++)
        {
            var p = new Vector3((float)random.NextDouble() * 20 - 10, (float)random.NextDouble() * 20 - 10, (float)random.NextDouble() * 20 - 10);
            vertices[i * 3].Position = p; vertices[i * 3 + 1].Position = p + new Vector3(1, 0, 0); vertices[i * 3 + 2].Position = p + new Vector3(0, 1, 0);
            triangles[i] = new Triangle { A = (uint)(i * 3), B = (uint)(i * 3 + 1), C = (uint)(i * 3 + 2) };
            bounds[i] = Bounds.Empty.Include(p).Include(p + new Vector3(1, 1, 0));
        }
        var bvh = Bvh.Build(bounds);
        Check(bvh.Depth <= Budget.MaxDepth, "bounded tree depth");
        for (int i = 0; i < 3000; i++)
        {
            var origin = new Vector3((float)random.NextDouble() * 20 - 10, (float)random.NextDouble() * 20 - 10, 20);
            var ray = new Ray(origin, -Vector3.UnitZ, 0, 100);
            var hit = bvh.Trace(vertices, triangles, ray); float nearest = ray.Max; bool found = false;
            foreach (var t in triangles)
                if (Bvh.IntersectTriangle(ray, vertices[t.A].Position, vertices[t.B].Position, vertices[t.C].Position, out float distance, out _) && distance < nearest)
                { nearest = distance; found = true; }
            Check((hit.Status == TraceStatus.Hit) == found && Math.Abs(hit.Distance - nearest) < 1e-5, "BVH equals brute-force reference");
        }
        Check(Bvh.IntersectBox(new Ray(new Vector3(0, 0, 2), -Vector3.UnitZ, 0, 10), Vector3.Zero, Vector3.One, 10), "parallel ray on slab boundary");
        Check(!Bvh.IntersectBox(new Ray(new Vector3(2, 0, 2), -Vector3.UnitZ, 0, 10), Vector3.Zero, Vector3.One, 10), "parallel ray outside slab");
        Check(Bvh.Build(Array.Empty<Bounds>()).Nodes.Length == 0, "empty scene");
        var repeated = new Bounds[5000]; for (int i = 0; i < repeated.Length; i++) repeated[i] = new Bounds(Vector3.Zero, Vector3.One);
        Check(Bvh.Build(repeated).Depth < Budget.MaxDepth, "identical centroids terminate");
        var distribution = new Distribution(new[] { 1000.0, 0.00001, 0.0 });
        Check(distribution.Pdf(1) > 0 && distribution.Pdf(2) > 0, "small emitter sampling support");
        Check(Math.Abs(distribution.Pdf(0) + distribution.Pdf(1) + distribution.Pdf(2) - 1) < 1e-12, "normalized source mixture");
        Check(new Distribution(Array.Empty<double>()).Sample(0.5) == -1, "zero lights");
        double estimate = 0; const int runs = 40000;
        var target = new[] { 2.0, 9.0, 0.0 };
        for (int r = 0; r < runs; r++)
        {
            var reservoir = new Reservoir();
            for (int c = 0; c < 8; c++) { int sample = random.Next(3); reservoir.AddProposal((uint)sample, 1, default, target[sample], 1.0 / 3, random.NextDouble()); }
            estimate += reservoir.Valid ? target[reservoir.Sample] * reservoir.EstimatorWeight : 0;
        }
        Check(Math.Abs(estimate / runs - 11) < 0.08, "RIS preserves energy with zero-target proposals");
        var source = new Reservoir(); source.AddProposal(1, 1, default, 9, 0.5, 0);
        var destination = new Reservoir(); destination.Merge(source, 3, 0);
        Check(Math.Abs(destination.EstimatorWeight - 2) < 1e-9, "reuse reevaluates target without multiplying energy");
        Check(History.FireflyLimit(100, 0) == 100, "dark history does not erase new light");
        Check(!History.Compatible(1, 1, Vector3.UnitY, Vector3.UnitY, 1, 2, 0, 0), "object change rejects history");
        Check(!History.Compatible(1, 1, Vector3.UnitY, Vector3.UnitY, 1, 1, 1, 2), "revision change rejects history");
        var adaptive = new AdaptiveBudget();
        for (int i = 0; i < 29; i++) adaptive.Observe(5);
        Check(adaptive.Scale == 0.5f, "budget hysteresis"); adaptive.Observe(5); Check(adaptive.Scale == 0.25f, "budget lowers scale");
        for (int i = 0; i < 120; i++) adaptive.Observe(1);
        Check(adaptive.Scale == 0.5f, "slow recovery");
        float[] transform = { 1,0,0,0,0,1,0,0,0,0,1,0,0,0,0,1 };
        var poseA = (float[])transform.Clone(); poseA[12] = 1;
        var poseB = (float[])transform.Clone(); poseB[12] = 2;
        float[] skinRaw = { 0,0,0,0,0,1,0,0,0,0,0,0,1,0,0,0, 1,0,0,0,0,1,1,0,0,0,0,0,1,0,0,0, 0,1,0,0,0,1,0,1,0,0,0,0,1,0,0,0 };
        object[] partA = { 0,3,0,"a",new string[4],"MESH" }, partB = { 3,3,0,"b",new string[4],"MESH" };
        object[] variantA = { partA,new[] { 1f,1,1,1 },new object[4],poseA }, variantB = { partB,new[] { 1f,1,1,1 },new object[4],poseB };
        object[] frame = { 1,1,1u,new double[3],new[] { new object[] { 1000001,skinRaw,new[] { 0,1,2,0,1,2 },new[] { partA,partB },false } },
            new[] { new object[] { 3u,0,transform,new[] { variantA,variantB },new[] { 0f,0,0,3,1,0 } } } };
        var assembled = SceneAssembler.Build(frame);
        Check(assembled.Vertices.Length == 6 && assembled.Sources[0].Pose != assembled.Sources[3].Pose, "shared vertices retain distinct bone palettes");
        Check(assembled.Poses[assembled.Sources[0].Pose].M41 == 1 && assembled.Poses[assembled.Sources[3].Pose].M41 == 2, "both part poses retained");
        poseB[12] = 3; SceneAssembler.Refresh(assembled, frame);
        Check(assembled.Poses[assembled.Sources[3].Pose].M41 == 3, "palette refresh reaches duplicated vertices");
        var emitters = LightCatalog.Build(assembled, Array.Empty<float>(), 1);
        Check(emitters[0].Kind == 2 && emitters[1].Kind == 2 && emitters[0].Triangle < emitters[1].Triangle, "emitter catalog ordered for continuation PDF lookup");
        ((float[])variantB[1])[3] = 0;
        Check(SceneAssembler.Signature(frame) != assembled.Signature, "emission disable invalidates catalog topology");
        ((float[])variantB[1])[3] = 1; uint revision = assembled.Materials[0].Revision;
        variantA[2] = new object[] { new object(),null,null,null }; SceneAssembler.Refresh(assembled, frame);
        Check(assembled.Materials[0].Revision != revision, "material texture replacement rejects prior history");
        Array.Resize(ref variantA,5);((object[])((object[][])frame[5])[0][3])[0]=variantA;
        var glassTexture=new object();object[] optical={1,new[]{.8f,.4f,.2f,.7f},glassTexture,new[]{0f,0,1,1},23u,false,"thin-glass"};variantA[4]=optical;
        var opticalScene=SceneAssembler.Build(frame);uint opticalRevision=opticalScene.Materials[0].Revision;
        optical[4]=24u;SceneAssembler.Refresh(opticalScene,frame);
        Check(opticalScene.Materials[0].Revision!=opticalRevision && opticalScene.Materials[0].Padding==24,"same-view optical content revision invalidates material history");
        opticalRevision=opticalScene.Materials[0].Revision;((float[])optical[3])[0]=.125f;SceneAssembler.Refresh(opticalScene,frame);
        Check(opticalScene.Materials[0].Revision!=opticalRevision && opticalScene.Materials[0].OpticalUv.X==.125f,"optical UV changes invalidate history without topology changes");
        opticalRevision=opticalScene.Materials[0].Revision;optical[2]=new object();SceneAssembler.Refresh(opticalScene,frame);
        Check(opticalScene.Materials[0].Revision!=opticalRevision && !ReferenceEquals(opticalScene.Textures[0][0],glassTexture),"glass texture replacement updates borrowed material view");
        Check(ClientPlugin.Shaders.OpaqueLightingOwnership.Claim("a", 7), "first lighting owner");
        Check(!ClientPlugin.Shaders.OpaqueLightingOwnership.Claim("b", 1), "conflicting lighting owner rejected");
        Check(!ClientPlugin.Shaders.OpaqueLightingOwnership.Commit("a"), "conflict prevents composition");
        ClientPlugin.Shaders.OpaqueLightingOwnership.Release("b");
        Check(ClientPlugin.Shaders.OpaqueLightingOwnership.Commit("a"), "single owner commits");
        Check(!ClientPlugin.Shaders.OpaqueLightingOwnership.ShouldRun("prism.ssgi.trace"), "SSGI conflict suppressed");
        Check(!ClientPlugin.Shaders.OpaqueLightingOwnership.ShouldRun("screenspace.shadows.contact"), "shadow conflict suppressed");
        ClientPlugin.Shaders.OpaqueLightingOwnership.BeginFrame();
        Check(ClientPlugin.Shaders.OpaqueLightingOwnership.ShouldRun("prism.ssgi.trace"), "native contributions resume on uncommitted frame");
        ClientPlugin.Shaders.OpaqueLightingOwnership.Release("a");
        var originFrame=new object[15];var nativeOrigins=new float[32];nativeOrigins[0]=10;nativeOrigins[16]=20;nativeOrigins[4]=7;
        originFrame[2]=uint.MaxValue;originFrame[6]=nativeOrigins;originFrame[14]=new[]{1f,2,3,4,5,6};
        Check(SceneAssembler.FrameIndex(originFrame)==uint.MaxValue,"unsigned frame index ABI");
        Check(ShadowSettings.HasPhysicalOrigins(originFrame),"physical-origin channel readiness");
        var physical=ShadowSettings.NativeLights(originFrame,false);
        Check(physical[0]==1 && physical[1]==2 && physical[2]==3 && physical[16]==4 && physical[18]==6 && physical[4]==7,"physical light origins replace both source positions only");
        Check(nativeOrigins[0]==10 && nativeOrigins[16]==20 && object.ReferenceEquals(ShadowSettings.NativeLights(originFrame,true),nativeOrigins),"offset toggle preserves native borrowed records");
        originFrame[14]=null;bool rejected=false;
        Check(!ShadowSettings.HasPhysicalOrigins(originFrame),"missing origin channel can fail before scene/GPU build");
        try{ShadowSettings.NativeLights(originFrame,false);}catch(InvalidOperationException){rejected=true;}
        Check(rejected,"missing physical origin channel fails closed");
        Console.WriteLine("PASS " + checks + " numerical and ABI checks");
    }
    static void Selection()
    {
        foreach (int route in new[] { HardwareChoice.Pending, HardwareChoice.DirectX11, HardwareChoice.Vulkan })
        foreach (bool option in new[] { false, true })
        foreach (bool query in new[] { false, true }) {
            var disabled = HardwareChoice.Select(option, route, query, enabled: false);
            Check(!disabled.Trace && disabled.Status == "disabled", "master switch stops tracing independently of saved hardware option, route and capability");
        }
        var offDx = HardwareChoice.Select(false, HardwareChoice.DirectX11, false);
        Check(offDx.Trace && offDx.Kind == BackendKind.SoftwareDx11, "DX11 without the option stays on the software tracer");
        var onDx = HardwareChoice.Select(true, HardwareChoice.DirectX11, true);
        Check(onDx.Trace && onDx.Kind == BackendKind.SoftwareDx11 && onDx.Status.Contains("waits for the Vulkan"), "DX11 keeps the saved option on the software tracer");
        var offVk = HardwareChoice.Select(false, HardwareChoice.Vulkan, true);
        Check(offVk.Trace && offVk.Kind == BackendKind.SoftwareVulkan, "Vulkan with the option off traces on the software Vulkan session");
        var pending = HardwareChoice.Select(false, HardwareChoice.Pending, true);
        Check(!pending.Trace, "a pending route does not trace");
        var missing = HardwareChoice.Select(true, HardwareChoice.Vulkan, false);
        Check(missing.Trace && missing.Kind == BackendKind.SoftwareVulkan && missing.Status.Contains("missing"), "Vulkan without ray query falls back to software Vulkan and names the missing API");
        var hardware = HardwareChoice.Select(true, HardwareChoice.Vulkan, true);
        Check(hardware.Trace && hardware.Kind == BackendKind.HardwareVulkan, "Vulkan with ray query selects the hardware session");
        var pendingOn = HardwareChoice.Select(true, HardwareChoice.Pending, true);
        Check(!pendingOn.Trace, "the option waits while the route is pending");
        Check(!BackendCapabilities.Available(BackendKind.HardwareVulkan, false, HardwareChoice.Vulkan, true), "hardware stays unavailable while the option is off");
        Check(BackendCapabilities.Available(BackendKind.HardwareVulkan, true, HardwareChoice.Vulkan, true), "hardware is available only for the armed Vulkan ray-query case");
        Check(BackendCapabilities.Available(BackendKind.SoftwareDx11, false, HardwareChoice.DirectX11, false), "software remains available on DX11");
        Check(BackendCapabilities.Available(BackendKind.SoftwareVulkan, false, HardwareChoice.Vulkan, false), "software is available on Vulkan without hardware tracing");
    }
}
