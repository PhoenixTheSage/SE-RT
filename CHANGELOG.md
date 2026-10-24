# SE-RT changelog

## 1.1.0 — 2026-10-24

- Add `SoftwareVulkan`: the software ray-tracing stack on Anomaly's Vulkan route, recorded into SE-Vulkan's tenant compute session. Vulkan selects it when hardware acceleration is off or the device lacks ray query.
- Share the `Parameters` constants and allocation forecasts between the DX11 and Vulkan backends.
- Link `VulkanRayApi.cs` into the GPU test project so it builds again.
- Measure SoftwareVulkan's GPU time with the tenant session's timestamps, so the adaptive budget and timing diagnostics work on Vulkan.
- Hardware acceleration on Vulkan now runs the full stack through ray query: tenant BLAS/TLAS and the `SERT_RAY_QUERY` variants of Identify, Trace and Refine. It replaces the native sun-visibility path.

## 1.0.2 — 2026-10-17

- Gate backend selection and native Vulkan ray settings with the main Enabled switch while preserving the saved Hardware Acceleration preference.
- Retain the existing three-argument HardwareChoice API and add explicit enabled-state selection; cover disabled Vulkan and DX11 decisions in core tests.
- Document the conservative Vulkan sun-visibility scope and native fallback for incomplete geometry/budget refusals.

## 1.0.1 — 2026-10-14

- Resolve and rebind SERT.Dx11 to the live Pulsar core, preserving the runtime backend interface identity; reset assembly resolution on disposal.
- Build and deploy the matching DX11 companion with isolated intermediate state and valid game access grants.
- Keep diagnostic actions on the Status page, standardize Show Status, and update integration regression assertions.

## 1.0.0 — previous release

Release SE-RT 1.0.0 ray-traced lighting

Version: 1.0.0 (first published release). Requires Anomaly RayScene API v1.

Changelog:
- Trace captured triangles with DX11 compute for opaque shadows and supported thin-glass transmission. Rasterization keeps primary visibility.
- Light from native sun, point, and spot sources, emissive meshes and voxels, environment lighting, bounded emitter proxies, and one indirect continuation.
- Reconstruct with RIS, temporal and spatial reuse, and separate diffuse, specular, and sun histories. Unknown coverage keeps the conservative native fallback.
- Default to half resolution, and drop to quarter resolution under memory or sustained GPU-time pressure. Sparse full-resolution refinement defaults to 5 percent of trace pixels.
- Combine local sun rays with Anomaly's far-only depth product. Finite-source rays end at the sampled physical source.
- Keep hardware tracing on a Vulkan ray-query pass. It stays off until that renderer is active and the ray-query API is present.
- Ship Rich HUD settings and status under Anomaly Shaders. The pack starts disabled, and config saves on a quiet worker.

Co-authored-by: Cursor <cursoragent@cursor.com>
