# CLAUDE.md — Avatar Texture Optimizer (ATO)

> Project memory and progress tracker. Updated after each work session.
> 项目记忆与进度跟踪。每次工作后更新。

## Project Overview / 项目概述

**Name:** AvatarTextureOptimizer (ATO)
**Package:** `net.fosa.avatar-texture-optimizer`
**Goal:** The world's best VRChat avatar texture optimization tool.
**Type:** NDMF plugin (non-destructive, build-time optimization)

## AgentTeam Structure / AgentTeam 结构

### Coders (3)
- **Coder-A (Architecture)**: NDMF plugin structure, pipeline orchestration, data model design
- **Coder-B (Algorithms)**: Quality metrics (MS-SSIM, CIEDE2000), UV island extraction, bin packing
- **Coder-C (Integration)**: Shader analysis, animation scanning, AAO compat, GPU processing, UI/i18n

**Consensus process**: All design decisions were discussed across the three coder perspectives before implementation. Key decisions:
1. Run in `Transforming` phase (after MA, before AAO) — QualifiedName sorts correctly
2. Single comprehensive pass for the entire pipeline
3. Burst + GPU for performance-critical operations
4. UV evacuation for AAO UVUsageCompabilityAPI compatibility

### Reviewers (3)
- **Reviewer-A**: Correctness of NDMF API usage and lifecycle
- **Reviewer-B**: Algorithm correctness (quality metrics, packing)
- **Reviewer-C**: Safety (whitelist, fallback, material integrity)

### QA (3)
- **QA-A**: Full code audit — compilation correctness and API usage
- **QA-B**: Full code audit — feature completeness vs. requirements
- **QA-C**: Full code audit — edge cases, memory safety, performance

## Architecture / 架构

```
Runtime/
  ATOComponent.cs          — Main MonoBehaviour component
  API/IATOExtensionContext.cs — Public extension API

Editor/
  ATOPlugin.cs             — NDMF plugin registration (Transforming phase)
  ATOLogger.cs             — Structured logging system
  CoreDataModel.cs         — UV islands, UV groups, texture type groups
  Core/
    ATOPipeline.cs         — Main orchestration (12 phases)
    AvatarScanner.cs       — Phase 1: Scan renderers/materials/textures/animations
    TextureDeduplicator.cs — Phase 2: Content+import dedup
    ShaderTextureAnalyzer.cs — Phase 3: Shader property analysis (lilToon aware)
    UVMappingBuilder.cs    — Phase 4: Build UV→texture mappings
    QualityScaler.cs       — Phase 5: Binary-search UV island scaling
    BinPacker.cs           — Phase 7: BLF bin packing with candidate pool
    AtlasBuilder.cs        — Phase 8: Render atlas + pull-push bleeding
    MeshRebaker.cs         — Phase 9: Reassign UVs + update material refs
    MaterialDeduplicator.cs— Phase 10: Material/texture dedup + slot merge
    WholeTextureScaler.cs  — No-atlas mode: scale whole textures
    TextureImportConfigurator.cs — Phase 12: Import settings
  Quality/
    QualityEvaluator.cs    — MS-SSIM, CIEDE2000, alpha, normal metrics
  Packing/
    IslandRasterizer.cs    — 4px-granularity bitmask rasterization
  Util/
    UVIslandExtractor.cs   — Union-find UV island extraction
  GPU/
    GPUTextureProcessor.cs — RenderTexture batch operations
  AAOCompat/
    AAOCompatibility.cs    — UVUsageCompabilityAPI integration
  UI/
    ATOInspector.cs        — Custom inspector with i18n
  i18n/
    ATOI18n.cs             — JSON-based i18n system
    en.json                — English translations
    zh-CN.json             — Simplified Chinese translations
```

## Pipeline Phases / 管线阶段

| Phase | Name | Description |
|-------|------|-------------|
| 0 | Validate | Find & validate ATO component |
| 1 | Scan | Scan renderers, materials, textures, animations |
| 2 | DedupTextures | Content+import hash deduplication |
| 3 | AnalyzeShaders | Classify textures (normal/mask/color) |
| 4 | BuildMappings | Extract UV islands, build UV groups & type groups |
| 5 | QualityScale | Binary-search scale islands by quality |
| 6 | Rasterize | 4px bitmask rasterization for packing |
| 7 | Pack | BLF bin packing into candidate atlases |
| 8 | RenderAtlas | Render atlas textures + pull-push bleeding |
| 9 | RebakeMesh | Reassign UVs, update material texture refs |
| 10 | DedupMaterials | Material/texture dedup, slot merge |
| 11 | AAOCompat | UV channel evacuation for AAO |
| 12 | ImportSettings | Configure compression/mipmap/wrap |

## Quality Algorithm / 质量算法

- **Color (alpha):** MS-SSIM + ΔE(CIEDE2000) + alpha (Cutout IoU / Blend premult RMSE)
- **Color (opaque):** MS-SSIM + ΔE
- **Normal:** Angular error + p95 (decoded, renormalized)
- **Grayscale:** Per-channel linear RMSE (worst)
- **Island thresholds:** <176px → single-scale SSIM, <11px → ignore
- **Scaling:** Uniform binary search → anisotropic U/V refinement
- **Pure-color shortcut:** min(4, shortEdge)
- **Near-lossless preset:** Skip all scaling, copy as-is

## Dependencies / 依赖

- NDMF ≥1.6.0 (required)
- Unity Burst, Collections, Mathematics
- Optional: VRC SDK 3 (for VRCAvatarDescriptor validation)
- Optional: AAO (for UVUsageCompabilityAPI)
- Optional: Modular Avatar (runs before ATO in pipeline)

## Key Design Decisions / 关键设计决策

1. **Single pass in Transforming phase**: Avoids ordering complexity
2. **UV groups**: Same UV position across all atlases (prevents reference errors)
3. **Texture type groups**: Companion maps (normal/mask) share atlas for utilization
4. **Never modify material properties** other than texture references
5. **Safe fallback**: Any uncertain texture → whitelist + warning
6. **Whitelist propagation**: If dedup target is whitelisted, source is too

## Known Limitations / 已知限制 (v0.1.0)

1. **No NDMF preview** (by design — spec says "暂不支持ndmf预览")
2. **Progress bar**: Implemented with EditorUtility.DisplayCancelableProgressBar (cancellable, shows phase + progress %)
3. **Burst parallelization**: Island rasterization uses Burst-compiled IJob + IJobParallelFor (RasterizeIslandJob + PopCountJob)
4. **GPU pull-push bleeding**: Implemented proper multi-resolution pyramid algorithm (push pyramid + pull-back fill), with GPU acceleration attempt and CPU fallback
5. **Blendshape area**: Full vertex-delta comparison at weight 0 vs 100, taking max area
6. **Animation scale**: Full animation clip scanning for m_LocalScale keyframes across all controllers
7. **MS-SSIM**: Full 5-scale implementation present

## Progress / 进度

### ✅ Completed
- [x] Project structure and package.json
- [x] Runtime: ATOComponent with all settings
- [x] Runtime: Extension API interfaces
- [x] NDMF plugin registration (Transforming phase)
- [x] Logging system with timing/reporting
- [x] Core data model (UVIsland, UVGroup, TextureTypeGroup, etc.)
- [x] Phase 1: Avatar scanner (renderers, materials, textures, animations)
- [x] Phase 2: Texture deduplicator (content + import hash)
- [x] Phase 3: Shader analyzer (lilToon-aware property classification)
- [x] Phase 4: UV mapping builder (island extraction, UV groups, type groups)
- [x] Phase 4: Blendshape area (full vertex-delta weight 0 vs 100 comparison)
- [x] Phase 4: Animation scale (full animation clip m_LocalScale scanning)
- [x] Phase 5: Quality scaler (binary search, anisotropic, pure-color shortcut)
- [x] Quality metrics (MS-SSIM, CIEDE2000, alpha IoU/RMSE, normal angle)
- [x] Phase 6: Island rasterizer (4px bitmask, **Burst-compiled IJob + IJobParallelFor**)
- [x] Phase 7: Bin packer (BLF, candidate pool, 90° rotation, padding)
- [x] Phase 8: Atlas builder (render + **multi-resolution pull-push bleeding** with GPU+CPU)
- [x] Phase 9: Mesh rebaker (UV reassignment, material ref update)
- [x] Phase 10: Material deduplicator (content hash, slot merge)
- [x] No-atlas mode: WholeTextureScaler
- [x] Phase 11: AAO compatibility (UVUsageCompabilityAPI via reflection)
- [x] Phase 12: Import settings configurator
- [x] **Progress bar with cancellation** (EditorUtility.DisplayCancelableProgressBar)
- [x] i18n system with en.json and zh-CN.json
- [x] Custom inspector with i18n
- [x] GPU texture processor

### 🔲 TODO (Future Versions)
- [ ] NDMF preview support
- [ ] More shader compatibility testing (Poiyomi, Unity Standard, etc.)
- [ ] Additional i18n languages (ja, ko)

## Build/Testing Notes / 构建测试说明

This is NOT a standalone Unity project. The user syncs the package into their VRChat avatar project.
这不是独立的 Unity 工程。用户将包同步到 VRChat Avatar 工程中。

Test environment: Unity 2022.3+, VRChat SDK 3.10.4, NDMF 1.14.4, MA 1.18.2, AAO 1.9.17, lilToon 2.3.4
