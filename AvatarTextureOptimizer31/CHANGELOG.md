# Changelog

All notable changes to Avatar Texture Optimizer will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [0.1.0] - 2024-08-20

### Added
- Initial release
- NDMF plugin registration (Transforming phase, after MA before AAO)
- Avatar component (ATOComponent) with quality presets, platform overrides, texture formats
- Full optimization pipeline:
  - Avatar scanning (renderers, materials, textures, animations)
  - Texture deduplication (content + import settings hash)
  - Shader property analysis (lilToon-aware)
  - UV-to-texture mapping (island extraction, UV groups, texture type groups)
  - Quality-based UV island scaling (binary search, anisotropic refinement)
  - Island rasterization (4px bitmask)
  - BLF bin packing (candidate pool, 90° rotation, padding)
  - Atlas rendering with pull-push edge bleeding
  - Mesh UV rebaking and material reference updates
  - Material/texture deduplication with slot merging
  - AAO UVUsageCompabilityAPI compatibility
  - Texture import settings (compression, MipStreaming, Clamp)
- Quality metrics:
  - MS-SSIM (multi-scale structural similarity)
  - CIEDE2000 (perceptual color difference)
  - Alpha IoU (cutout) / premultiplied RMSE (blend)
  - Normal map angular error + p95
  - Grayscale per-channel RMSE
- Whitelist system (mesh, material, texture, animation)
- i18n system with English and Simplified Chinese
- Custom inspector with i18n support
- Extension API for third-party developers
- Detailed logging with timing and utilization reports
