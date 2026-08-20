# AvatarTextureOptimizer (ATO) — Project Memory

> Everything about this project is recorded here. Package: `net.fosa.avatar-texture-optimizer`.
> Goal: best-in-class open-source NDMF VRChat avatar texture optimizer (UV-island quality
> scaling + texture-type-grouped atlas packing).

## 1. Current Status / 整体进度

- Phase: **Initial scaffolding + core architecture**. Third-party sources downloaded and
  read for integration points. Code being written. NOT yet compiled in Unity by us
  (user verifies in their own Unity project).
- The user requires every change to result in a complete bakeable build and a git commit.
  We are in a sandbox without Unity, so "bakeable" is guaranteed by careful API use against
  the *actual* read sources; the user runs the real bake.

## 2. Verified Integration Facts (read from actual source, do not guess)

### NDMF 1.14.4 (`nadena.dev.ndmf`, Unity 2022.3)
- Plugin: derive `Plugin<T>`, `[assembly: ExportsPlugin(typeof(MyPlugin))]`,
  `Configure()` uses `InPhase(BuildPhase.X).Run(...).Then...`.
- Phases: FirstChance, PlatformInit, Resolving, Generating, Transforming, Optimizing,
  PlatformFinish. MA mostly runs Resolving/Transforming; AAO runs Optimizing.
- Ordering constraints (fluent `Sequence`/`DeclaringPass`):
  `AfterPlugin("nadena.dev.modular-avatar")` and `BeforePlugin("com.anatawa12.avatar-optimizer")`.
  We run our single heavy pass in `Optimizing`, after MA, before AAO.
- `BuildContext.AvatarRootObject` (GameObject), `.AssetSaver` (IAssetSaver, persists temp
  assets into the avatar asset container), `.GetState<T>()`, `.ErrorReport`.
- Errors: subclass `SimpleError` (TitleKey/DetailsKey + Localizer) or implement `IError`.
  Report via `ErrorReport` / `ctx.ErrorReport.AddError(...)`. We'll use a lightweight
  `Debug.Log`/report hybrid prefixed `[ATO]`.
- NDMF removes EditorOnly in Resolving (`RemoveEditorOnlyPass`); we must still skip
  EditorOnly-tagged renderers defensively.

### AAO 1.9.17 (`com.anatawa12.avatar-optimizer`)
- Public API assembly `Anatawa12.AvatarOptimizer.API` (file `API-Editor/UVUsageCompabilityAPI.cs`).
  NOTE the spelling is **`UVUsageCompabilityAPI`** (misspelled "Compability" — matches AAO).
- `bool IsTexCoordUsed(SkinnedMeshRenderer renderer, int channel 0..7)`
- `void RegisterTexCoordEvacuation(SkinnedMeshRenderer renderer, int originalChannel, int savedChannel)`
  - If AAO will use channel N (e.g. RemoveMeshByMask uses UV0, RemoveMeshByUVTile uses a chosen
    channel), we must evacuate original UVs to a free channel and register it. We do this per
    renderer/channel when we remap a channel AAO uses. AAO then uses the evacuated copy and
    cleans it up. savedChannel must be unused (else InvalidOperationException).
- Must guard with reflection / type presence so ATO works when AAO is not installed. AAO must
  be an *optional* dependency (soft reference in asmdef `versionDefines`/reflection).
- AAO QualifiedName = `com.anatawa12.avatar-optimizer`; it sorts itself last.

### Modular Avatar 1.18.2 (`nadena.dev.modular-avatar`)
- Runs Resolving + Transforming + a little Optimizing. We place AfterPlugin MA.
- QualifiedName `nadena.dev.modular-avatar`.

### lilToon 2.3.4 (`jp.lilxyzw.liltoon`)
- Sampled 2D properties (subset, from `lil_common_input*.hlsl`):
  Main/color: `_MainTex`, `_Main2ndTex`, `_Main3rdTex`, `_OutlineTex`;
  Normal: `_BumpMap`, `_Bump2ndMap`, `_MatCapBumpMap`, `_MatCap2ndBumpMap`;
  Mask/grayscale: `_TriMask`, `_Main2ndBlendMask`, `_Main3rdBlendMask`, `_EmissionBlendMask`,
  `_Emission2ndBlendMask`, `_MatCapBlendMask`, `_MatCap2ndBlendMask`, `_AlphaMask`,
  `_ShadowStrengthMask`, `_ShadowBorderMask`, `_ShadowBlurMask`, `_SmoothnessTex`,
  `_MetallicGlossMap`, `_OutlineWidthMask`, `_RimShadeMask`, `_DissolveMask`, `_DissolveNoiseMask`,
  `_FurMask`, `_FurLengthMask`, `_FurNoiseMask`, `_AnisotropyScaleMask`, `_GlitterColorTex`, ...
  Emission: `_EmissionMap`, `_Emission2ndMap`, `_EmissionGradTex`, `_Emission2ndGradTex`;
  MatCap: `_MatCapTex`, `_MatCap2ndTex`; Reflection: `_ReflectionColorTex`, `_BacklightColorTex`;
  Shadow: `_ShadowColorTex`, `_Shadow2ndColorTex`, `_Shadow3rdColorTex`; etc.
- Our `IShaderPropertyAnalyzer` must be data-driven: read the shader's Properties block +
  `[NoScaleOffset]`/`[Normal]`/`[MainColor]`/`[MainTexture]` attributes and standard keywords,
  with a curated lilToon/Standard/URP Lit table as the primary source. Unknown shader that
  we cannot confidently classify => treat its textures as whitelist (skip) + warning, per spec.

### avatar-compressor 0.9.0 (`dev.limitex.avatar-compressor`)
- Good reference architecture (Service/Analysis/Backend split, GPU compute backend via
  ComputeShader, MaterialCollector, AnimationUsageMap, TextureFormatSelector). We do NOT copy
  it; we reuse its *patterns*. It does not do UV-island atlas packing.

### LLC 2.13.0 — light-limit-changer; only needed for animation/material compatibility awareness.

## 3. Feasibility Assessment (delivered to user)

The design is **feasible**. Main risks/decisions:
1. MS-SSIM + CIEDE2000 + normal angular error at build time is expensive but feasible on GPU
   (RenderTexture/compute) with Burst fallback. We implement: GPU path primary (compute
   shader batch), CPU/Burst path for headless/fallback. Metrics operate on the *resampled
   island upscaled back to original box* vs original (excludes final GPU compression loss).
2. "UV group" (same across all maps sharing a UV incl. normal/mask/animation) is the
   non-negotiable correctness anchor — all maps in a UV group get identical atlas placement.
3. Atlas padding bleed via GPU "pull-push" infinite dilation; transparent keeps alpha 0.
4. We ONLY modify mesh UVs + texture references. Never other material properties. Material/
   texture dedup is optional (default on) and merges slots only for opaque materials when
   animation doesn't separately switch them.
5. Correctness-first safety: anything we cannot prove safe (ST/tiling/offset, decal, wrap
   seam, unknown shader classification, cross-seam out-of-[0,1]) => whitelist + warning.
6. Scale: this is a large project. We build a compiling, coherent product with all pipeline
   stages wired; the GPU quality shader and a few advanced paths need on-device bake
   verification (which the user does). Every stage logs `[ATO]` with timings.

## 4. Architecture

```
Runtime/
  Components/AvatarTextureOptimizer.cs   (MonoBehaviour, lives on VRCAvatarDescriptor root)
  Models/AtoSettings*.cs                 (serialized settings: quality, platform overrides, ...)
Editor/
  Plugin/AtoPlugin.cs                    (NDMF Plugin; Optimizing; after MA before AAO)
  Plugin/AtoPass.cs                      (single pass; progress + cancel + report)
  Pipeline/AtoPipeline.cs                (orchestrates stages; timing; cancel)
  Pipeline/Stages/
    01 ValidateAndCollect.cs             (single component rule, renderer scan)
    02 MaterialTextureMapping.cs         (UV<->texture map; multi-UV as independent; ST checks)
    03 AnimationAnalysis.cs              (animator/animation clips: mat/tex switches, GO active,
                                          object scale, material renderMode/cutoff)
    04 EligibilityAndWhitelist.cs        (apply whitelist + safety rules => groups)
    05 TextureDedup.cs                   (pixel+importsettings dedup, update refs)
    06 IslandExtraction.cs               (rasterize UV islands, overlap merge, normalization)
    07 QualityScaling.cs                 (binary search per UV group, bucket/type group max)
    08 AtlasPacking.cs                   (BLF + 4px bitmask raster packing + rotation + pool)
    09 AtlasComposition.cs               (compose atlas GPU, bleed dilation, import settings)
    10 UvRemapAndMeshWrite.cs            (rewrite mesh UVs; AAO evacuation)
    11 ReferenceRewrite.cs               (materials + animation clips; slot merge for opaque)
    12 FinalDedupAndImport.cs            (material/texture dedup; mipstream; platform formats)
  Shaders/AtoQuality.compute             (downsample/upsample, SSIM/MS-SSIM, dE00, normal ang)
  Shaders/AtoAtlas.compose.shader/...    (blit + pull-push dilation)
  Quality/                               metric structs, binary search, presets
  Packing/                               bitmask, BLF, packer, atlas pool, rotation/transpose
  Analysis/                              ShaderPropertyAnalyzer, lilToon table, channel usage
  Util/                                  Burst rasterizer, math (CIEDE2000, SSIM), logging,
                                        progress, cancellation, texture IO/cache, GPU readback
  i18n/Localizer.cs + Resources/i18n/*.json
  UI/ (custom Editor for the component; advanced foldouts; platform overrides)
```

### Key data model concepts
- `MaterialSlotRef` : (renderer, slotIndex) — handles multi-material + animated slot swaps.
- `UvChannel` : (mesh, channel 0..7) — each UV channel independent.
- `TextureUsage` : (Texture2D, importSettingsHash, TexType, sRGB flag, filterMode,
  transparencyMode, cutoff, channelsUsed) — type group key includes: which special maps
  exist (normal/mask/emission...), colorSpace, filterMode.
- `Island` : triangles, UV bbox, world-area (max over blendshapes 0/100 & max anim scale),
  source physical pixels, target pixels after density clamp, anisotropy.
- `UvGroup` : set of islands sharing a UV identity across maps — identical placement in every
  atlas of the group. The resize bucket = max required size across maps (wooden bucket).
- `TypeGroup` : textures grouped by (special-map presence signature, colorSpace, filterMode).
  Each type group produces one or more atlases; a type group with a lower-quality subset map
  (e.g. mask) can scale that subset atlas down past min padding to save space.
- `Atlas` : candidate-pool-chosen dimensions, padding, list of placed islands, utilization.

### Quality algorithm (exact from spec)
- Linear-space resampling; premultiplied-alpha downscale for transparent.
- Metric: MS-SSIM (fallback single-scale SSIM when box short edge <176px; ignore metric when
  <11px) + ΔE CIEDE2000 + alpha (Cutout: post-clip contour IoU / Blend: linear RMSE). For a
  texture referenced by multiple materials, evaluate each material's transparency mode &
  cutoff and take the strictest. Opaque: MS-SSIM + ΔE. Normal: decode/resample/renormalize/
  encode then angular error + p95. Grayscale: linear RMSE on used channels only, worst channel.
- Compare downscaled-then-upscaled (bilinear) coverage region against original.
- Binary search UV scale; all thresholds must pass; UV group takes max size (≤ group max original).
- Density: default min 2048 px/m, max 4096 px/m; selectable 512/1024/2048/4096/8192; clamped by
  the island's true physical pixel size on the source file.
- Quality != 1: solid-color islands short-circuit to min(4, short edge). Quality == 1: skip
  resampling for that texture type entirely (incl. solid), copy raw.
- Burst parallel + GPU (RenderTexture) batch evaluation. Excludes final compression loss.

### Packing (exact from spec)
- Bitmask rasterizer at 4px granularity (Burst), full-scan BLF, area-desc then edge-length-desc,
  90° rotation via bitmask transpose; normal maps keep tangent data, never recompute normals.
- Candidate atlas pool: power-of-two by default (64 min, 8192 max / 4096 mobile); NPOT
  experimental steps by 64; NPOT strips unsupported formats (e.g. iOS strips PVRTC).
- Queue per type group, sorted by total scaled+culled rasterized island area desc. For each
  queue: compute total raster area needed, discard candidate atlases smaller than that, sort
  by area asc then long/short ratio asc (most square first). Pack one whole texture (+ its UV
  group) atomically; first candidate that fits all islands => final atlas. If current texture
  doesn't fit remaining space of largest atlas, open/ reuse another same-type queue and try a
  smaller texture; if a single texture can't fit the largest atlas, abandon atlas for that UV
  group and keep scaled standalone + warning. Pack using island raster shapes, not rectangles.
- Padding = ceil(maxEdge/128) clamped to min 4; user selectable min padding 4/8/16/32/64
  (default 4). GPU pull-push dilation fills atlas empty space (transparent alpha stays 0).
- Atlas names start `ATO_`.

## 5. Design Decisions / Conventions
- Namespace: `Fosa.Ato` (Editor) and `Fosa.Ato.Runtime`.
- All user-facing strings via i18n keys; ship `en.json` + `zh-hans.json`. Auto reads NDMF
  language, fallback English. Comments bilingual (EN/中文).
- Logging: `AtoLog` with `[ATO]` prefix, levels, per-stage timing, detail foldout. A verbose
  toggle for advanced users. Never add logging after the fact — instrument everywhere.
- Memory: stream textures, release RenderTextures with `using`/`Release`, `AsyncGPUReadback`,
  Burst jobs with temp allocations freed, cache rasterization/decoded islands keyed by
  (texture, import hash). Avoid holding full-res CPU copies of many textures at once.
- Progress/cancel: `AtoProgress` + `CancellationToken`; cancel stops work, keeps temp assets
  on disk, frees CPU/GPU/memory.
- Single component enforcement: component has `[DisallowMultipleComponent]` +
  `[RequireComponent(typeof(VRCAvatarDescriptor))]`; pass also scans for duplicates and aborts.
- Platforms: PC / Android / iOS; override foldouts; defaults from current build target.
- Compression: safe enum per (transparent/opaque/normal/grayscale), derived from atlas alpha;
  user choices validated at build (e.g. no alpha-less format for alpha content; single-channel
  request for a multi-channel grayscale atlas => save multi-channel + warn).
- Mipmap + MipStreaming bound together (VRChat rule): one toggle per texture class; on => both.
- Read/Write off + Clamp forced on generated atlases (not user-editable); other import settings
  take the strictest/highest quality across source textures.

## 6. Work Log / Git
- Each meaningful change: edit code => (user verifies bake) => `git commit`. In sandbox we
  commit after each coherent milestone.
- Commit message format: `ATO: <summary>`.

## 7. TODO / Remaining Work (initial)
- [x] Download + read integration sources
- [x] package.json + asmdefs + meta files
- [x] Runtime component + settings model
- [x] i18n (en, zh-hans) + Localizer
- [x] NDMF plugin/pass wiring + ordering
- [x] Pipeline stages 01..12 (scaffold + real impl)
- [x] Shader property analyzer (lilToon/Standard/URP table + generic)
- [x] Quality metrics (CPU reference MS-SSIM/ΔE/normal/alpha) + presets; GPU batch compute TODO
- [x] Bitmask BLF packer + atlas pool + rotation (normals never rotated)
- [x] Atlas compose + GPU pull-push dilation shader
- [x] Mesh UV rewrite + AAO evacuation (soft ref via reflection)
- [x] Reference rewrite (materials + animation clips) + opaque slot merge (conservative)
- [x] Final dedup + import settings/platform formats
- [x] UI (foldouts, platform overrides, quality preset, language dropdown)
- [x] Extension interfaces for third parties
- [x] README.md
- [ ] GPU compute batch quality path (AtoQuality.compute) — current metric is CPU reference
- [ ] True index-buffer merge for identical opaque material slots
- [ ] Smoke bake in Unity (USER) — verify on real avatars, feed back issues

### Milestone M1 (initial delivery)
All 12 stages compile against the *actual* read NDMF/AAO/MA/lilToon sources, run in
Optimizing phase after MA before AAO, report to NDMF console with [ATO] timings, and produce
ATO_* atlases or scaled standalone textures with rewritten mesh UVs + texture references only.

## 8. Things to flag to the user (open questions)
- We target Unity 2022.3 (matches NDMF/SDK). Confirm if they need 2019/2021 compat.
- We do not implement NDMF preview (spec says not yet supported).
- Optional deps (AAO, lilToon, MA, LLC, avatar-compressor) are soft references; ATO works
  without them. VRC SDK + NDMF are hard requirements.

## M1.1 Patch (correctness self-review)
- Fixed atlas blit shader: removed SV_VertexID (incompatible with GL immediate-mode); uses
  appdata vertex input under LoadPixelMatrix and supports 90° CW UV rotation.
- Populated TextureUsage.Alpha/Cutoff via new MaterialTransparency detector (lilToon/Standard/URP/HDRP);
  strictest mode + highest cutoff across referencing/animated materials. This makes alpha metrics
  (Cutout IoU / Blend RMSE) actually take effect.
- Cleaned UvRasterizer.MaxWorldArea (removed leaked temporary Mesh).
- Verified NDMF APIs from source: IAssetSaver.SaveAsset(obj) single-arg; ErrorReport.ReportError(IError)
  public static (AddError is internal). AtoPipeline uses both correctly.

## M1.2 Patch (atomic texture-bundle packing)
- Critical fix: Stage08 previously packed UV groups independently, which could split one source
  texture's islands across multiple atlases -> invalid material reference (a slot can only point to
  one texture). Refactored so the atomic pack unit is ONE SOURCE TEXTURE + all its UV groups.
- Bundles sorted raster-area desc then edge desc; candidate atlases filtered by total queue area and
  sorted area asc / ratio asc (most square first). If a bundle doesn't fit even the largest atlas,
  it falls back to scaled standalone + warning. Otherwise the queue splits (largest bundle alone,
  rest recurses) reusing same-type queues.
- Removed per-bundle native masks (not used for BLF; per-group masks are used and disposed inline).
