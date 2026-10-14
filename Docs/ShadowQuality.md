# Shadow quality and reconstruction

Implemented for software DX11, with backend-independent source extents, shadow
results, settings and the existing adapter boundary for future renderer APIs.
Hardware adapters remain unavailable until SE1 exposes suitable devices, queues,
scene resources and synchronization.

## Delivery stages and comparison paths

1. Baseline: existing numerical, transport energy, resource-lifecycle, settings
   and shadow fixtures were retained. Native lighting remains the per-pixel
   fallback for incomplete transport. Ray-traced shadows can be disabled for a
   local-light comparison while retaining native sun coverage.
2. Origins and sampling: Automatic is the default; Manual retains the saved
   ShadowBias minimum, including old configs. Primary confirmation reconstructs
   the surface and geometric normal. Float-operation bounds cover barycentric
   reconstruction, instance transforms and GPU bone transforms/weighted sums.
   Geometry normals offset rays; native shading normals evaluate BRDFs. Finite
   segments account for uncertainty at both endpoints. Numerically unresolved
   very short segments keep native lighting. No emitter actor/mesh is skipped.
   The original MIT offline RGBA mask is documented in Assets/Sampling/README.md.
   Blue-noise and Hashed use the same estimator and ray caps; SamplingSeed is
   fixed by config. The low-resolution receiver grid stays spatially stable.
3. Shadow metadata and denoising: RGB transmittance, coverage, blocker distance
   and guide quality are separate. Any-hit distance is approximate; refinement
   finds the nearest captured opaque blocker under the same traversal limits.
   Unknown coverage can lower guide quality without invalidating certified
   opaque zero transmission. Integer source/revision/sample keys preserve all
   32 bits. Different selected lights may accumulate radiance; source-specific
   visibility comparisons require matching keys. Sun is reconstructed separately
   from four radiance signals. Stable history caps at 32, fast history at 4 and
   low-confidence/reactive history at 2. Geometry, revision, motion, virtual
   specular hit distance and disocclusion rejection remain. Soft stochastic
   visibility changes reduce confidence or accumulation rather than resetting
   every frame. Penumbra evidence controls edge filtering; clear rays borrow
   neighboring blocker guides without interpreting a selected lamp as an
   all-light mask. Hard contacts retain restrictive filtering.
4. Finite sources and distant sun: lamps default to a 0.025 m visibility disk
   (0-0.25 m); sun angular radius defaults to 0.266 degrees (0-1). Zero gives hard
   shadows. Disk radius is clamped to a quarter of receiver/source distance.
   Center brightness, range, spotlight cone/cookie and intensity are unchanged.
   Emissive triangle/disc PDFs remain unchanged. Anomaly owns a 2048-square
   far-only directional depth map for a 256 m receiver region and 32 km caster
   reach. Depth fragments inside the resident sphere are clipped, preserving
   native alpha/dither programs. Local solar rays extend to the sphere exit and
   multiply far opaque coverage. No native sun mask is multiplied when this
   product is valid. Missing, stale or unclipped variants retain the conservative
   native hybrid and explicitly report constrained local softness.
5. Sparse refinement: two GPU classification phases prioritize hard geometry
   disagreement, then disocclusion, low confidence and shadow gradients. One
   full-resolution coordinate is selected per footprint. Atomic compaction has
   a strict pool capacity: default 5% of low-resolution pixels, adjustable 0-10%.
   Each refined receiver confirms primary geometry once and runs fresh eight-
   proposal RIS plus the same MIS/continuation transport, capped at four lighting
   segments. The sparse 192-byte result has an integer low-resolution lookup.
   Only matching full-resolution coordinates composite. Invalid results retain
   the base/native path. Sparse history caps at four frames (two for low
   confidence). Eight consecutive combined over-target GPU measurements halve
   the allowance; 60 below 80% restore it. Refinement exhausts before resolution
   adaptation responds. Scheduling has no synchronous GPU readback.
   Every completed timestamp measurement drives the hysteresis, including
   multiple measurements delivered by one completion callback.
6. Transmission: Anomaly appends a versioned known-GLASS optical descriptor from
   the transparent-material registry, excluding its error material. Native
   premultiplied color is unpremultiplied, hue-normalized, texture-modulated and
   converted to T = lerp(white, GlassTransmission * tint, opacity); default 0.9.
   A bounded visibility traversal multiplies up to eight distinct surfaces.
   Coincident coplanar triangles count once; distinct panes count separately.
   Opaque blockers certify black even after unknown/excess transmission layers.
   Unresolved textures, unsupported categories, overflow and excess layers
   without an opaque blocker remain incomplete. RGB transmission applies to
   primary and secondary NEE and sun. Camera-visible glass stays native;
   primary confirmation passes supported glass to reach the opaque GBuffer
   surface; unsupported transparency still remains a coverage gap.
   continuation through refractive glass, thickness absorption, caustics and
   media transport are outside this implementation.

## Bounds, allocations and observability

Traversal uses 64-entry stacks, at most 8192 node visits and 4096 triangle tests
per segment; transmission stores at most eight surfaces. A missing nearest guide
never turns certified black into invalid visibility. Parameters is 336 bytes,
Material is 96 bytes and RefinedSample is 192 bytes. CPU layouts, structured
uploads and reflected constant-buffer offsets/bindings are checked together.

Reconstruction forecasts match allocated payload bytes: 500 bytes per trace
pixel, plus (2 * 192 + 8) bytes per sparse pool slot and a four-byte counter.
Geometric normals and numerical uncertainty retain FP32 precision; filtering
guides and radiance histories use FP16 where appropriate.
Zero-capacity D3D buffers reserve one physical slot. A pool is reduced to fit
256 MiB; half scale drops to quarter before exceeding the base history budget.
If quarter cannot fit, native fallback remains. Scene forecasts count padded
buffers, exact power-tree capacity, both material atlases, skin error storage,
refit/skin lists, the 8192-byte sampling asset and the parameter CB. An additional
16 MiB plus its 80-byte clip CB is reserved for Anomaly's requested far depth product. Total stays within
512 MiB. Allocation forecasts are checked against actual payload sums.

Config, Pulsar and Anomaly Rich HUD share lamp radius, solar angle, bias mode,
manual bias, sampling mode/seed, refinement allowance and glass transmission.
Persistence remains debounced. Status reports combined timings, far fallback
reason, configured extent, requested/effective refinement and throttle/memory
reason. Debug views include RGB sun/transmission, selected source, ray coverage,
blocker quality (blue missing, orange any-hit, green nearest), confidence/history,
sparse selection (yellow exact refined coordinate), and effective far coverage
(green clear, blue blocked, orange unavailable, magenta outside the product).

## Regression and live acceptance

Run both Core/Integration target frameworks and the complete Tests/Gpu executable.
GPU tests compile and create all production shaders under WARP, retain old shadow
and energy fixtures, and exercise tinted composition, layer/duplicate limits,
nearest-guide classification, far-sphere clipping/native cutout behavior, integer
keys, sparse compaction/overflow/segment caps, odd-size coordinate composition,
invalid fallback, deferred recording, fenced replacement, skin/refit and reset.
Core error bounds are compared against double-precision affine reconstruction
and weighted four-bone skinning. Reconstruction sequences exercise 48-frame
stochastic RGB signals, 32/4/2 histories and depth/revision/normal rejection.
Fast/stable comparisons also remove a departing caster's penumbra trail within
eight fixture frames without rejecting the preceding stable stochastic sequence.
Hashed and blue-noise transport each preserve diffuse quadrature energy over
32 frames / 524,288 samples with matching seeds and work caps.
The offline sampling check verifies exact scalar histograms and suppression of
spatial/temporal low frequencies against a fixed-seed white-noise control.
Source fixtures and shader caches are local test outputs, never runtime assets.

WARP establishes contracts and numerical correctness. It does not establish the
4 ms hardware target or live visual acceptance. Record fixed-seed Hashed/BlueNoise
runs with equal allowances and compare against high-sample references for static
penumbrae, moving lamps/casters/grids, camera motion/disocclusion, colored overlapping
lights and supported glass. Include ship interiors, cockpits, emissive panels,
railings, off-screen casters and distant terrain at half and quarter dimensions.
Verify contact stability, no double sun shadow, texture/material revisions,
world unload/device reset, and combined SE-RT plus far-sun timing. Those measured
quality/performance gates remain pending an actual game/hardware run.

Guidance: [NRD signal separation](https://github.com/NVIDIA-RTX/NRD),
[scalar spatiotemporal blue noise](https://research.nvidia.com/publication/2021-12_scalar-spatiotemporal-blue-noise-masks),
and [error-bound ray origins](https://developer.nvidia.com/blog/solving-self-intersection-artifacts-in-directx-raytracing/).
The software operation bounds are derived for this implementation; DXR constants
are not copied. NRD is not a dependency.
