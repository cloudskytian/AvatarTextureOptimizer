# Changelog

All notable changes to Avatar Texture Optimizer are documented in this file. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/); this project uses [Semantic Versioning](https://semver.org/).

## [0.1.0] - Unreleased

> Source/static review, direct Roslyn compilation against Unity 2022.3.22f1 references, and three independent source QA passes are complete. Unity Test Runner remains 0/263; Compute/Burst, real dependency-project, target-GPU and real-avatar bake validation are still required before a verified public release.

### Added

- NDMF Optimizing-phase plugin ordered after Modular Avatar and before Avatar Optimizer, without an NDMF live-preview filter.
- Root-only, hierarchy-unique Avatar Texture Optimizer component with English, Simplified Chinese and automatic language selection.
- Renderer, material slot, shader texture property, UV0–UV7, animation, texture identity and whitelist analysis.
- Safety grouping by texture purpose, color space, filter mode, UV channel, animation closure and shared UV use.
- Burst 4-pixel bit-mask UV-island rasterization, overlapping-island handling, rotation-aware shape packing, BLF placement, multi-page POT pools and optional experimental NPOT candidates.
- GPU linear/premultiplied-alpha resampling and odd-size-safe pull-push padding.
- Quality-bounded island sizing with SSIM/MS-SSIM, CIEDE2000, cutout IoU, blended-alpha RMSE, normal angular mean/p95 and grayscale-channel RMSE.
- Preset-linked advanced quality thresholds, a near-lossless custom default, and an exact resampling bypass at target quality 1.
- Conservative animation scale, bone/constraint, BlendShape weight-domain and per-triangle frame-envelope safety analysis.
- Whole-texture optimization mode when atlas generation is disabled.
- Material, texture, generated-atlas and mesh deduplication, plus transaction-safe opaque material-slot merging and animation index rewriting.
- PC, Android and iOS setting overrides; purpose-aware compression, mipmap/Mip Streaming and format fallback.
- Optional Avatar Optimizer UV Usage Compatibility API integration guarded by exact package/assembly discovery.
- Public deterministic editor extension API for pre-analysis, texture classification and pre-commit veto/integration.
- Progress cancellation, NDMF warning/error reporting, `[ATO]` stage timing and category-based diagnostic logging.
- Transactional renderer/material/mesh/animation/AAO rollback and explicit generated-resource ownership tracking.
- EditMode regression suites for analysis, quality, packing, GPU dispatch seams, atlas fallback, mesh/material rewrite, extensions, ownership and rollback.

### Safety

- Unsupported shader composites, invalid ST, unsafe repeat/decal semantics, unresolved animation bindings, additive deformation, unknown constraints, unproven BlendShape domains and unsafe Mipmap LOD mappings preserve original content.
- Atlas pages are published only after compressed/fallback output passes final quality verification; failed pages retain original meshes and textures.
- GPU work-format usages, device texture-axis limits and conservative complete-output pixel budgets are checked before allocation; null material texture states never create unreachable blank assets.
- Incomplete rollback fails the build and retains generated objects that may still be referenced, preventing dangling Unity object references.

