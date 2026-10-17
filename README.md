# SE-RT

Experimental ray/path-traced lighting for Space Engineers 1, with opaque shadows and supported thin-glass transmission. DX11 compute shaders trace captured triangles; rasterization supplies primary visibility. Specialized reconstruction carries the low sample budget.

Requires the local Anomaly build containing `RayScene` API v1. SE-RT does not patch game rendering methods. Load both plugins before entering a world, then enable SE-RT in its configuration dialog. It starts disabled. With Rich HUD Master in the world, the same settings appear under **Anomaly Shaders → SE-RT → Settings**, beside a live **Status** page. The Pulsar configuration dialog remains available without Master.

The main Enabled switch gates both software and Vulkan hardware tracing. A saved Hardware Acceleration preference remains available while SE-RT is disabled, without enabling native ray builds or dispatches.

The Vulkan hardware path supplements native sun visibility before stock material lighting. Local lights retain native shading. It does not implement hardware reflections, indirect illumination, or the software temporal reconstruction stack. Its incomplete TLAS retains native shadow coverage for geometry it cannot certify, and budget refusals retain native output.

Features include native sun/point/spot sources, emissive mesh and voxel textures, generated material texture changes, environment lighting, bounded emitter proxies, one indirect continuation, RIS with temporal/spatial reuse, separate diffuse/specular/sun histories, and conservative native fallback. Half dimensions are the default; quarter dimensions are selected under memory or sustained GPU-time pressure.

See [implementation and acceptance notes](Docs/Implementation.md) for the pipeline, exact budgets, current approximations and tests. Hardware tracing is a Vulkan ray-query pass on the renderer device. It stays off until that renderer is active and the ray-query API is present. DX11 stays on the software backend.

Ray-traced shadows are enabled with the renderer. Lamps default to a 0.025 m visibility disk and sunlight to a 0.266 degree angular radius; zero restores hard shadows. Point, spot, emissive triangle, emitter-disc and environment samples use bounded geometry visibility queries. Sun shadows combine local rays with Anomaly's far-only depth product, retaining the conservative native hybrid while that product is unavailable. Finite-source rays end at the sampled physical source. Unknown unblocked coverage retains native lighting; a captured opaque blocker can certify shadow. All sources participate in the low-sample estimator.

**Allow Light Block Offsets** defaults to off: lamp lighting and shadow endpoints use the physical light actor position. Turn it on to restore native point-light offsets. This requires Anomaly's `frame[14]` physical-origin channel; older builds fail closed until updated or offsets are allowed. **Automatic Bias** derives offsets from software operation bounds; **Manual** retains the saved Shadow Bias. Emitter radius, solar angle, sampling mode/seed, refinement allowance and glass transmission appear in both menus. Debug views show source visibility, coverage, blocker-guide quality, history confidence/age, exact refinement coordinates, RGB transmission and far-sun coverage.

Known captured glass supports thin tinted transmission through up to eight distinct surfaces; camera-visible glass stays native. Sparse full-resolution refinement defaults to 5% of trace pixels, with one primary confirmation and at most four lighting segments per refined receiver. Custom reconstruction uses separate sun and radiance signals with 32-frame stable, 4-frame fast and 2-frame low-confidence history. See [shadow quality and acceptance](Docs/ShadowQuality.md) for contracts, exact bounds, comparison paths and live validation requirements.

## Build and validation

Set game/Pulsar paths in ignored `Directory.Build.props.user`, or use the existing `setup.py` discovery. Build both runtimes without deploying:

```powershell
dotnet build SERT.sln -c Release -p:RunPostBuildEvent=Never
dotnet run --project Tests/Core/Core.csproj -c Release -f net48
dotnet run --project Tests/Core/Core.csproj -c Release -f net10.0
dotnet run --project Tests/Gpu/Gpu.csproj -c Release
dotnet run --project Tests/Integration/Integration.csproj -c Release -f net48
dotnet run --project Tests/Integration/Integration.csproj -c Release -f net10.0
```

Normal builds retain the template's local Pulsar deployment: Legacy for net48 and Interim for net10.0, including Assets and plugin.xml. The game supplies SharpDX; do not ship another copy. The manifest is local and unpublished on PluginHub. Origin is the SE-RT repository. The template remote remains the client-plugin-template history; do not push SE-RT changes there.

Configuration lives under the active game profile's `Storage/SERT.cfg`. On this workstation the active profile is `T:/SteamLibrary/steamapps/common/SpaceEngineers/Profile`.

Both menus update one shared config. Controls synchronize after 400 ms without an edit; rendering uses a consistent snapshot on the next callback. Saves run on a worker after the same quiet period and flush at shutdown. SE-RT never writes Anomaly's config.

**Renderer Status** opens a scrollable report with an explicit Refresh button. The Rich HUD Status page refreshes changed diagnostics at most once per second. Reports include asynchronous GPU time, requested/adaptive scale, internal and trace dimensions, acceleration-worker state, geometry/light counts, baked atlas tiles, temporal history, GPU memory budgets, fallback counters, the last exception, Master availability and persistence state. **Reset History** clears reconstruction and restarts the adaptive budget; **Log Status** writes the full snapshot to `SpaceEngineers.log`. Candidate counts include reserved proxy slots and are not ray counts or proof of coverage. No blocking GPU readback is added.

The 4 ms / 1080p midrange performance target and real-world visual acceptance are unverified. WARP is a correctness check, not a hardware benchmark. Secondary normal maps/paint, native voxel distance transitions, individual GPU particles and render-target billboard textures remain approximations/gaps described in the implementation notes. GPU effects already supply approximate sources from emitter metadata.
