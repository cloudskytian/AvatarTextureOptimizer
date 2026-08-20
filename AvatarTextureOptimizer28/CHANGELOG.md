# Changelog

## 0.1.0

Initial development release.

- NDMF plugin running in `BuildPhase.Optimizing`, ordered after Modular Avatar and before Avatar Optimizer.
- Mesh-UV to texture mapping with UV groups and texture type groups.
- Perceptual target quality algorithm: MS-SSIM / SSIM, CIEDE2000, cutout silhouette IoU, blend alpha RMSE,
  normal angular error, per-channel data RMSE. Linear space, premultiplied-alpha downsampling.
- Uniform then per-axis binary search for the smallest passing island scale, with texel-density clamping.
- Shape-aware atlas packing on 4 px coverage bitmasks with full-scan BLF, 90 degree rotation by transpose,
  and a POT / experimental NPOT candidate pool.
- Pull-push atlas hole filling with a padding-bounded mip chain.
- Platform overrides for PC / Android / iOS with safety-enforced compression formats.
- Input and output deduplication of textures and materials, including safe material slot merging.
- Reflection-based Avatar Optimizer `UVUsageCompabilityAPI` support.
- User-extensible JSON localisation, English and Simplified Chinese included.
- Extension API for third-party shader support providers and build observers.
