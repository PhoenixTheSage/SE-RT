# SE-RT changelog

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
