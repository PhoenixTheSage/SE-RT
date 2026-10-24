# SE-RT DX11 implementation

SE-RT is an experimental client plugin for Space Engineers 1, built for Pulsar Legacy (`net48`) and Interim (`net10.0`). Rasterization supplies primary visibility; compute shaders traverse captured triangles for opaque lighting. Anomaly owns all game hooks and the rendering contract. SE-RT owns acceleration construction, light sampling, transport, reconstruction and GPU resources.

The six-stage shadow/reconstruction extension and current acceptance gates are documented in [ShadowQuality.md](ShadowQuality.md).

## Pipeline

1. Anomaly captures modern and legacy mesh data before native CPU buffers are freed, and publishes a camera-relative render snapshot before atmosphere. Uncaptured meshes remain explicit coverage boxes.
2. A worker builds unique-mesh, binned-SAH binary BLASes and an instance TLAS. A revision-keyed CPU BLAS cache is capped at 128 MiB. Transform/pose/material updates retain topology. Skinning runs on the GPU; shared vertices with different bone palettes receive separate assignments. Refit dispatches visit only TLAS nodes and animated BLAS nodes, bucketed by depth. Optimized shader bytecode is prepared on a worker and retained across device resets.
3. Native GBuffer reads produce low-resolution position/depth, normal/roughness and base-color/metalness guides. A restricted primary intersection confirms the instance and material revision. This confirmation is **additional traversal work**, separate from the lighting-segment budget.
4. A GPU power tree samples points, spots, emissive mesh/voxel triangles, environment and emitter proxies. The mixture reserves 5% uniform support, including small bright regions missed by the approximate power estimate. Eight fresh RIS proposals are combined with one temporal and four spatial candidates. Visibility is traced again after reuse.
5. Transport evaluates separate sun visibility, direct diffuse, direct specular, indirect diffuse and indirect specular. A diffuse/GGX mixture launches one continuation and one next-event shadow at its hit. Area/environment NEE and continuation emission use complementary MIS weights. Smooth surfaces (roughness below 0.2) retain native environment reflection.
6. Reservoir history rejects source revision changes. Radiance reconstruction rejects depth, normal, instance, material and hit-distance mismatches. Specular history uses a bounded virtual-distance reprojection heuristic. FP32 second moments guide three à-trous iterations. Full-resolution composition remodulates diffuse transport with the native base color, restores native emission and applies native fog once.
7. Only valid reconstructed opaque pixels overwrite the HDR target. Other pixels keep native lighting. The ownership contract suppresses conflicting registered lighting passes for that frame; sky, atmosphere, transparency, tonemapping and upscalers follow their normal order.

## Budgets and conventions

- Default sampling dimensions: 0.5×0.5; fallback: 0.25×0.25. At most four **lighting** segments per active sample. The primary confirmation is accounted separately.
- Primary exact-geometry radius: 128 m; residency: 256 m; diffuse continuation: 32 m; specular continuation: 64 m. Environment visibility retains a 128 m local horizon; finite sources use uncertainty-aware endpoints. Sun rays reach the resident sphere exit when Anomaly supplies far-only depth, otherwise retain the conservative native hybrid. Unknown coverage is never a certified miss. See ShadowQuality.md.
- GPU scene cap: 512 MiB, including atlases, light tree and acceleration. Reconstruction cap: 256 MiB. Memory pressure selects quarter scale. Texture arrays have the DX11 limit of 2,048 slices, including up to 512 transient proxy slots. Material tiles are deduplicated by source views; at most 32 tiles bake per frame. Native lighting carries frames while required source tiles await their first bake or a changed view.
- FP32 reservoir weights, moments, matrices and hit distances. FP16 radiance uses configurable storage exposure and a finite 60,000 ceiling. This ceiling is biased for extreme radiance; changing exposure clears history.
- Nonblocking timestamp queries are recorded on the supplied context and polled on Anomaly's immediate-context completion callback. The 4 ms default is a **target**, not a measured guarantee. Scale falls after 30 expensive observations and recovers after 120 cheap observations.
- Per-ray atomic statistics are enabled in the WARP pipeline fixture and disabled in normal rendering.
- Source IDs and revisions reject recycled emitter slots. Proxy deduplication uses an explicit native owner ID, never spatial proximity.
- Configuration uses one cached XML serializer, dirty setters, a 400 ms quiet period and worker writes. Dispose waits for pending persistence.
- Disabled SE-RT skips per-frame scene enumeration and texture lookup while keeping CPU upload capture armed for later enable.
- Topology, light-layout and resolution changes wait for a nonblocking GPU event before retiring old allocations. Native lighting carries those intervening frames. CPU upload captures survive device reset; world unload clears them.

## Coverage and current limits

Native point and spot colors already include intensity. The bridge preserves that convention, native range/falloff, diffuse/gloss factors and spotlight texture profiles. Mesh extension maps supply emission, using native diffuse albedo, compression-artifact rejection and bloom emissiveness scaling; material texture overrides cover generated screen textures. Voxel capture supplies material triples and near-distance triplanar textures. Secondary shading uses interpolated vertex normals; normal-map perturbation, paint colorization and native voxel stochastic tiling/far transitions are approximated.

The 64×64 material tiles approximate texture detail. Sub-tile emission can be lost during baking; uniform proposal support does not remove that representation error. Very small textured emitters need the live reference comparison before this can be considered a quality-complete renderer.

Billboard sources use disk proxies. File-texture billboard emission is sampled; render-target billboards currently use their color proxy. GPU effects automatically provide approximate emitter sources from CPU emitter metadata and texture atlases; individual GPU particle buffers are not decoded. Effects can also publish explicit sources with `RayScene.SetEmitter`. Generic instance-buffer draws, unsupported transparent techniques, missing textures and uncovered geometry fall back conservatively. Load Anomaly/SE-RT before loading a world: meshes uploaded before capture was armed cannot be recovered from released CPU buffers. Supported thin glass supplies tinted NEE transmission; refraction, participating-media transport, caustics and additional bounces remain deferred.

Temporal/spatial reuse and reconstruction deliberately introduce correlation and filtering bias. WARP tests validate the numerical and resource contracts; they do not establish visual quality in real worlds. Specular reprojection, proxy radiance calibration, tiny/animated emissives, mining damage, world transitions, skinning and upscaler interaction require the in-game acceptance run described below.

## Validation

`Tests/Core` exercises production BVH construction/traversal, buffer layouts, RIS energy preservation, small-emitter support, bone-palette assignments, history rejection, adaptive hysteresis and Anomaly's ownership contract on both runtimes. The ownership test uses the adjacent Anomaly checkout. `Tests/Gpu` compiles every production shader and creates it on D3D11 WARP, executes transport/reconstruction with a controlled raster fixture, and compares production GPU traversal against brute force, including transformed instances, alpha cutouts, unknown coverage and overflow. It checks GPU skin/refit with shared vertices and two palettes, deferred recording, atlas readiness, retirement, reset and quarter scale. Fresh RIS and spatial reuse are compared against an analytic lighting reference across 16,384 samples each; full transport's environment NEE/BSDF MIS is checked against diffuse quadrature over 32 frames / 524,288 samples. The fixture substitutes only its GBuffer and final display shaders; native guide/composite shaders are compiled separately.

Run:

```powershell
dotnet build SERT.sln -c Release -p:RunPostBuildEvent=Never
dotnet run --project Tests/Core/Core.csproj -c Release -f net48
dotnet run --project Tests/Core/Core.csproj -c Release -f net10.0
dotnet run --project Tests/Gpu/Gpu.csproj -c Release
foreach ($runtime in @('net48', 'net10.0')) {
    dotnet build ClientPlugin/ClientPlugin.csproj -c Release -f $runtime -p:PulsarSourceBuild=true -p:RunPostBuildEvent=Never "-p:OutputPath=bin/SourceValidation/$runtime/"
}
```

WARP tests take an optional shader root and game include directory. Their default include directory is the local SE development reference cache; pass an alternate directory for another installation.

Recorded validation on 2026-10-03: both runtime builds and both source-mode builds pass; SE-RT has no build warnings. The core suite passes 3,036 checks per runtime. All 13 production shaders compile and create on WARP, and traversal, transport, atlas readiness, fenced resize, shared-palette skin/refit, reset, RIS/reuse energy, environment MIS and mesh/voxel emission checks pass. Nine hook/compiler signatures were checked against the installed game's assembly metadata. Anomaly's 32 immediate/deferred transparent-binding assertions pass on both runtimes; its build retains ten existing unreachable-code warnings.

Before enabling by default, compare a high-sample reference with low-sample output in an enclosed room, tiny-emissive scene, moving ship, character scene, asteroid/planet, screen-texture scene and cockpit. Check camera cuts, mining/damage, world unload, device recreation, resize, half/quarter scale, competing lighting plugins and upscalers. Measure median and p95 added GPU time and both memory pools at 1080p on RTX 3060/RX 6700 XT class hardware. Current workstation results cannot certify that target.

## Terminal integration and diagnostics

SE-RT resolves Anomaly's existing `TerminalConfigRegistry` by reflection and requests `SE-RT/Settings` and `SE-RT/Status`. It retries unavailable registration once per second, removes partial registrations before retrying, and unregisters the exact page keys at plugin disposal. Anomaly owns the Rich HUD client, session mount/remount and Master handshake; this pack vendors no Rich HUD client and adds no game hooks. Missing Master leaves the Pulsar settings dialog usable and is reported explicitly.

All settings share `Config.Current`. Property notifications queue synchronization, explicit control refreshes run after a 400 ms quiet period, and Pulsar refreshes suppress setters. Anomaly's push-only controls use `Refresh()`; no `CustomValueGetter` is attached. The render callback receives a coherent config snapshot. Persistence retains its cached serializer and worker-based debounce. Changes while a previous snapshot saves are flushed by version at disposal.

The live terminal status uses CPU snapshots at most once per second and skips page refresh when the report is unchanged. The scrollable Pulsar report is refreshed on demand. It reports asynchronous GPU timestamps, actual allocated dimensions, requested/adaptive sampling, scene topology, worker duration, light candidates/proxy activity, atlas bake progress, history, memory budgets, callback/composition/fallback counts, explicit reset reasons and last render exception. Geometry describes the accepted CPU scene; resources describe current GPU allocations and can remain resident while disabled. Atlas bake counts do not certify current source revisions; fallback state carries pending-version failures. Per-ray atomics remain disabled in production. Reset and log buttons support repeatable in-game reports without synchronous GPU queries.

Integration validation on 2026-10-03: both target runtimes pass 20 reflection/persistence checks, including absent host, absent Master, partial-population rollback, shared setters, external-setting refresh, retained synchronization after refresh failure, no refresh feedback, status updates, idempotent pages, exact-key cleanup, debounce, shutdown flush, edits during an in-flight save, and invalid float normalization. Both normal/source-mode plugin builds and the WARP renderer regression suite pass. WARP also verifies resident diagnostic counts/budgets/history against the pipeline and cleared diagnostics after reset. Real-world Rich HUD layout and interaction remain to be checked after game restart.

## Ray-traced shadows and physical light origins (2026-10-04)

All detected source classes use `Shadows.hlsli` for visibility: point/spot lamps, emissive mesh/voxel triangles, producer discs, environment and sun. This remains one selected direct-light shadow per low-resolution sample, a sun ray, and at most one secondary-hit NEE shadow, plus the continuation segment. No per-light-per-pixel loop or production readback is introduced. Area sources retain sampled endpoints; lamps use configurable visibility disks and the sun an angular disk. Center-based native lamp energy and source PDFs remain unchanged.

Ordinary shadow traversal accepts any certified opaque triangle, even with a nearer unknown coverage box. Refined queries seek a nearest blocker under bounded visits/intersections. Without an opaque blocker, unsupported coverage, unready alpha masks, traversal overflow and segments outside the 256 m resident sphere remain incomplete. Finite segments account for receiver and emitter uncertainty; unresolved short segments retain native lighting. The old 128 m finite-source cutoff is removed. Local sun rays reach the sphere exit when Anomaly supplies valid far-only coverage; otherwise the conservative 128 m/native hybrid remains. Explicit hidden actors are excluded; nearby off-screen actors remain captured.

Anomaly supplies physical light actor translations in append-only `frame[14]`, aligned with native records. SE-RT replaces position XYZ in a copy by default; source color/cone/falloff/cookie conventions remain untouched. `AllowLightBlockOffsets` opts into native positions. Missing/malformed physical metadata fails closed for native lamps. Automatic bias derives software operation bounds; Manual preserves the saved 2 mm default. Bias, emitter size and block offsets are separate controls. Configuration changes invalidate history, and origin-convention changes retire/rebuild light buffers through the existing GPU fence.

Integer metadata carries complete source/revision/sample keys. Finite lamps include emitter coordinates; zero-radius punctual sources ignore unused coordinates. Hard same-source contacts reject changed visibility, while soft stochastic changes retain accumulation with confidence and fast-history comparisons. Sun RGB transmission has a separate history. Stable/fast/low-confidence limits are 32/4/2 frames. Guide-aware spatial filtering protects contacts. Supported captured glass supplies bounded tinted transmission. Sparse refinement improves a capped subset of full-resolution coordinates; reconstruction remains approximate at half/quarter dimensions.

Validation: both runtimes pass 6,060 core numerical/physical-origin/optical-revision/ABI checks and 26 shared-config integration/persistence checks. WARP retains 25 baseline shadow cases and adds 1,044 quality, eight far-depth and 63 sparse-refinement checks, alongside production transport/energy/lifecycle suites. Sequence assertions exercise stochastic RGB history, 32/4/2 limits, receiver rejection and hard boundaries. See `ShadowQuality.md` for bounded work, layouts, comparisons and acceptance gates. Both plugin builds pass on both runtimes; Anomaly retains existing unreachable-code warnings. Live-world shadow quality and combined hardware timing remain pending. Restart with both updated source plugins so appended contracts are available.

## Hardware migration

`IRayTracingBackend` is the renderer adapter boundary. The numerical scene and light structures have no game dependencies. The DX11 path stays SM5 software traversal. `HardwareChoice` keeps that path when the operational renderer is DX11, including when the saved hardware option is on. A Vulkan session traces only when that option is on and `sevk_get_ray_capabilities` reports ray query. The trace is a ray-query compute pass on the renderer's own device, built from resident world meshes, and it is not the software temporal stack or the Anomaly guest bus. No second Vulkan device is opened. DXR remains unavailable.

## Software stack on Vulkan (SoftwareVulkan)

On Anomaly's Vulkan route, `HardwareChoice` selects `SoftwareVulkan` when hardware acceleration is off, or when the device lacks ray query. That status names the missing ray query. `SoftwareVulkan` runs the DX11 stack unchanged:

- the same shaders (compiled to SPIR-V by `RayScene.CompileShader`);
- the same resources, pass order and allocation forecasts (`SoftwareForecast`, shared with DX11).

It records into SE-Vulkan's tenant compute session, which frame[7][1] carries (Anomaly `Docs/RayTracing.md`, Vulkan route). DX11's sticky shader-resource, sampler and constant state is kept per stage and copied into each dispatch or draw. As on DX11, binding a resource as an output unbinds its shader views on that stage. Renderer images and material textures are renderer ids. A failed frame records nothing.

Differences from DX11:

- GPU time comes from the session's timestamps around the tenant list. Anomaly's completion callback passes the session to `Poll`, which reads `Timing` and feeds the refinement budget and the adaptive scale as on DX11. Far-sun time adds nothing yet.
- Retirement needs no event query, because the renderer retires ids once the GPU is done with them.
- Textures that update every frame (LCD render targets) bake into the atlas once per version.
- The far-sun product is not published yet, so local softness stays native hybrid.

Validation (SE-Vulkan test projects):

- `--sert-vulkan-only` (GuestDepthGpuTests, actual Anomaly core) compiles every shader, builds every pipeline and renders the delivered frames with history.
- `--sert-scene-only` (SceneSubmissionGpuTests) composes a real GBuffer scene: the ray-coverage view traces 394 of 400 armor pixels. The 6 misses sit on shared triangle edges.

Both lanes have zero validation errors.

## Hardware ray query on Vulkan (option C)

With hardware acceleration on and ray query available, Vulkan runs `SoftwareVulkan` in ray-query mode. The status reads "hardware ray query", and SE-Vulkan's earlier native sun-visibility path is switched off.

The scene is built as acceleration structures on the tenant session:

- each mesh root's contiguous triangle range becomes a BLAS over a tight index copy;
- skinned roots rebuild their BLAS after Skin each frame, so Refit is not dispatched;
- unknown-coverage instances share a unit AABB BLAS scaled to their bounds;
- the TLAS is rebuilt each frame, with the instance index as its custom index.

`InstanceTriangleBase` (t69) maps a primitive back to its triangle. The TLAS binds at t68.

`IdentifyRayQuery`, `TraceRayQuery` and `RefineRayQuery` (profile 4, `cs_6_5`) compile the `SERT_RAY_QUERY` variants of `traceScene` and `traceVisibility`:

- Every triangle is a non-opaque candidate, so the software rules decide each one: glass on primary rays, alpha test, incomplete materials, glass transmission layers.
- AABB candidates mark unknown coverage.
- An unknown candidate counts when it is nearer than the final limit, which is the order-independent form of the software test.

Every other pass, buffer and forecast is unchanged. DX11 never compiles the wrappers.

Validation (`--sert-scene-only`):

- the ray-query view traces all 400 armor pixels (the BVH misses 6 on shared edges);
- lighting matches the software result to 0.25/255 mean;
- zero validation errors.

`--sert-vulkan-only` compiles the three variants through Anomaly's actual `CompileShader` and builds their pipelines.
