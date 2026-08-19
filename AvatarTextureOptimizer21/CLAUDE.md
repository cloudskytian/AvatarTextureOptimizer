# AvatarTextureOptimizer - Project Memory
# AvatarTextureOptimizer - 项目记忆

## Project Overview / 项目概述
- Package Name: net.fosa.avatar-texture-optimizer
- Version: 0.1.0
- NDMF plugin for VRChat avatar texture optimization
- Runs after Modular Avatar (Transforming phase), before AAO
- Build Phase: Optimizing

## Architecture / 架构
```
Packages/net.fosa.avatar-texture-optimizer/
├── package.json
├── Editor/
│   ├── net.fosa.avatar-texture-optimizer.asmdef
│   ├── Core/
│   │   ├── ATOPlugin.cs              - NDMF Plugin registration (7 passes)
│   │   ├── ATOBuildContext.cs         - Shared state across all passes
│   │   └── Passes/
│   │       ├── ValidationPass.cs      - Component/avatar validation
│   │       ├── AnalysisPass.cs        - Material/animation/shader analysis
│   │       ├── DeduplicationPass.cs   - Texture dedup by content+settings
│   │       ├── QualityEvaluationPass.cs - Per-island quality binary search
│   │       ├── UVProcessingPass.cs    - Atlas packing + UV reassignment
│   │       ├── ApplicationPass.cs     - Apply changes to avatar
│   │       └── PostProcessPass.cs     - Dedup, AAO compat, cleanup, report
│   ├── Analysis/
│   │   └── AnimationAnalyzer.cs       - Animation clip analysis
│   ├── Compatibility/
│   │   └── ShaderAnalyzer.cs          - lilToon/standard shader analysis
│   ├── Processing/
│   │   └── UVIslandExtractor.cs       - UV island extraction (Union-Find)
│   ├── Quality/
│   │   └── QualityEvaluator.cs        - SSIM, ΔE, alpha, normal, grayscale metrics
│   ├── Atlas/
│   │   └── AtlasBinPacker.cs          - Raster bitmask bin packing + BLF
│   ├── UI/
│   │   └── AvatarTextureOptimizerEditor.cs - Custom inspector with i18n
│   ├── Utils/
│   │   ├── TextureHelper.cs           - Texture utilities
│   │   └── ATOLog.cs                  - Logging with [ATO] prefix
│   ├── API/
│   │   └── IATOExtensions.cs          - Third-party extension interfaces
│   ├── Shaders/
│   │   └── ATOCompute.compute         - GPU compute shaders
│   └── i18n/
│       ├── ato_i18n_en.json           - English localization
│       └── ato_i18n_zh-CN.json        - Simplified Chinese localization
├── Runtime/
│   ├── net.fosa.avatar-texture-optimizer.runtime.asmdef
│   ├── Components/
│   │   └── AvatarTextureOptimizerComponent.cs - Main MonoBehaviour
│   └── API/
│       └── ATOApi.cs                  - Runtime API
└── Tests/
    └── Editor/
```

## Build Pipeline / 构建管线 (NDMF Optimizing Phase)
1. **ValidationPass** → Validate component, VRCAvatarDescriptor, single instance
2. **AnalysisPass** → Analyze renderers, shaders (lilToon/standard), animations, build UV↔Texture map
3. **DeduplicationPass** → Deduplicate textures by content hash + import settings
4. **QualityEvaluationPass** → Extract UV islands, build type/UV groups, binary search scale per island
5. **UVProcessingPass** → Pack atlases (raster bitmask BLF), generate atlas textures, assign new UVs
6. **ApplicationPass** → Replace meshes, update material refs, update animation refs, apply import settings
7. **PostProcessPass** → Material/texture dedup, AAO UVUsageCompabilityAPI, remove component, build report

## Key Design Decisions / 关键设计决策
- Use BuildPhase.Optimizing to run after MA
- RegisterTexCoordEvacuation for AAO UV compatibility
- lilToon shader property analysis via shader.GetPropertyCount()/GetPropertyType()
- GPU (RenderTexture + ComputeShader) for quality evaluation and atlas composition
- Binary search (10 iterations) for per-island UV scaling
- Raster bitmask bin packing (4px granularity) with Bottom-Left-Fill
- Texture type grouping by: normal/mask/alpha/linear/filterMode signature
- Quality presets based on academic SSIM/ΔE research thresholds
- CIEDE2000 for perceptual color difference
- MS-SSIM with 5 scales (academic weights: 0.0448, 0.2856, 0.3001, 0.2363, 0.1333)
- Normal map: decode → angle error → P95 comparison
- Union-Find for UV island extraction
- Blend shape: max of weight 0 and 100 area

## Dependencies Studied / 已研究的依赖
- NDMF 1.14.4: Plugin<T>, Pass<T>, BuildPhase.Optimizing, BuildContext, Sequence API
- AAO 1.9.17: UVUsageCompabilityAPI (RegisterTexCoordEvacuation, IsTexCoordUsed)
- lilToon 2.3.4: Property names, keyword system (_UseXxx), lilPropertyNameChecker
- MA 1.18.2: Runs in Transforming phase
- Avatar Compressor 0.9.0: Reference for texture analysis patterns
- LLC 2.13.0: Reference for NDMF plugin patterns

## Progress / 进度
- [x] Project structure created
- [x] Dependencies downloaded and studied (NDMF, AAO, lilToon, MA, etc.)
- [x] Architecture designed
- [x] Runtime component implemented
- [x] NDMF plugin registration implemented
- [x] All 7 build passes implemented
- [x] Shader analyzer (lilToon + standard) implemented
- [x] Animation analyzer implemented
- [x] UV island extractor (Union-Find) implemented
- [x] Quality evaluator (SSIM, MS-SSIM, ΔE, alpha, normal, grayscale) implemented
- [x] Atlas bin packer (raster bitmask + BLF) implemented
- [x] Custom inspector with i18n implemented
- [x] Third-party extension API implemented
- [x] Compute shaders for GPU operations created
- [x] i18n files (en + zh-CN) created
- [x] README.md created
- [x] Git committed
- [x] ZIP packaged

## Completed Features / 已完成功能
All 21 originally-missing features have been implemented:
1. ✅ Anisotropic UV scaling (uniform → per-axis binary refinement)
2. ✅ Overlapping island merging (Union-Find with UV vertex sharing)
3. ✅ MipStreaming/Mipmap binding (single toggle via TextureImporter)
4. ✅ Safety fallback system (alpha format, iOS PVRTC, normal map warnings)
5. ✅ Progress display & cancellation (CancellationTokenSource)
6. ✅ Triangle rasterization bin packing (edge function, 4px granularity)
7. ✅ Alpha premultiplication before downsampling
8. ✅ Material slot merging (with animation switch detection)
9. ✅ Platform format restrictions (iOS PVRTC→ASTC with NPOT)
10. ✅ Compression format safety (alpha→non-alpha fallback)
11. ✅ Quality evaluation upsample-back comparison
12. ✅ Texture pixel cache management
13. ✅ UV group barrel effect (max scale, capped by max original)
14. ✅ Pure color short-circuit
15. ✅ Normal map rotation safety (tangent data unchanged)
16. ✅ Grayscale per-used-channel linear RMSE
17. ✅ Whitelist same-UV: skip atlas, keep import opts
18. ✅ Animation texture→original group merging
19. ✅ Multi-channel UV extraction (all 8 channels)
20. ✅ NDMF preview explicitly not supported
21. ✅ AnimationTextureOriginalMap for type group merging

## Known Limitations / 已知限制
- GPU compute shader integration requires Unity project testing
- Pull-push uses simplified multi-pass dilate
- NPOT atlas is experimental; platform-specific validation needed with real builds
- Burst parallel evaluation markers present but full Burst job scheduling needs Unity compilation
