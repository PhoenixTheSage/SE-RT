# SE-RT continuous sampling masks

Original SE-RT generator and data; MIT licensed under the repository LICENSE.
No third-party source code or data is included. The algorithm follows the general
scalar spatiotemporal construction described by the NVIDIA research paper:
https://research.nvidia.com/publication/2021-12_scalar-spatiotemporal-blue-noise-masks

Reproduce with `python Tools/GenerateBlueNoise.py`; validate with
`python Tools/VerifySampling.py`. Both use only the Python standard library.
There are no runtime downloads or generation.

Layout: 16 x 16 x 8 RGBA unsigned bytes, pixel-major within each temporal slice.
The four independent seeds are 619, 1297, 2381 and 3571. A periodic Gaussian
void-and-cluster relaxation ranks 2048 samples per channel. Spatial sigma is
1.5 pixels; temporal sigma is 1.5 frames with weight 2. Each eight consecutive
ranks maps to one byte, giving an exactly uniform scalar histogram. This is
original small-tile data, not NVIDIA's distributed optimized masks.

SHA256: 543ac3524fdc9df83cd30436ac2d9a19ab4a034f4d0beff2b51987914650663d

At half occupancy, spatial low-frequency energy is 0.025-0.054 of a deterministic
white-noise control; temporal first-frequency energy is 0.207-0.253. These are
spectral checks, not proof of superior live-game reconstruction quality.

Continuous shader dimensions use translated channel streams, with a new
seeded spatial scramble after each eight-frame block to avoid short repeated
per-pixel directions. Light proposal selection and reservoir replacement keep
independent hashed state. Hashed and blue-noise comparison modes share ray caps.
Image comparison and hardware profiling remain live acceptance gates.
