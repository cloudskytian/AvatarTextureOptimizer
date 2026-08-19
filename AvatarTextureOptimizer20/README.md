# Avatar Texture Optimizer (ATO)

An open-source, non-destructive (NDMF) texture optimizer for VRChat avatars.
开源、非破坏式（NDMF）的 VRChat Avatar 贴图优化工具。

**Package**: `net.fosa.avatar-texture-optimizer` · **Unity**: 2022.3 · **License**: MIT

---

## What it does / 它做什么

ATO builds a *material-independent* mapping from mesh UVs to textures, so switching between
materials that share the same texture reuses the same mapping. It then:

1. **Deduplicates** source textures by actual pixels + import settings and re-targets all references.
2. Detects **UV islands** per mesh/UV channel (multi-channel UV supported), merges overlapping
   islands, normalizes out-of-bounds islands that can be shifted into [0,1] (cross-seam islands
   are safely skipped with a warning).
3. Scales each island with a **target-quality binary search** (uniform first, then per-axis for
   anisotropy) driven by: linear-space resampling, premultiplied-alpha downsampling,
   **MS-SSIM** (single-scale SSIM below 176 px, skipped below 11 px) + **CIEDE2000 (p95)**,
   alpha via **contour IoU** (Cutout, evaluated per referencing material's cutoff — strictest wins)
   or **linear RMSE** (Blend), normals via **angular error p95** after decode/renormalize,
   grayscale via **per-used-channel linear RMSE (worst channel)**. Solid-color islands
   short-circuit to `min(4, short side)`. Pixel-density clamps (default 2048–4096 px/m,
   steps 512–8192) prevent both blur and waste.
4. Packs islands into **atlases** using island-shape raster bitmask packing
   (4 px granularity, Burst BLF full scan, area-descending, 90° rotation for non-normal groups,
   POT candidate pool — experimental NPOT with 64 px steps). **Texture type groups**
   (has-normal / has-mask / colorspace / filter mode) prevent wasted space in companion atlases;
   companion atlases can shrink when their quality demand is uniformly lower.
   **UV groups guarantee that the same UV lands at the same place in every atlas**, so
   animation-swapped textures and normal/mask companions stay consistent (same layout,
   separate physical atlases where needed).
5. Fills empty atlas space with **GPU pull-push dilation** (alpha stays 0), rewrites mesh UVs,
   retargets materials (**only texture references are ever modified — never any other shader
   parameter**), animations (material swaps and texture curves), then deduplicates identical
   output materials/textures and merges opaque material slots when safe.
6. Compresses per category (opaque/transparent/normal/gray) with **platform-safe fallbacks**
   (PC/Android/iOS overrides, Unity-style), binds **Mipmap ↔ Mip Streaming** (VRChat rule),
   forces atlas Clamp + no Read/Write, and reports everything to the NDMF console.

Runs in the NDMF **Optimizing** phase — after Modular Avatar, before AvatarOptimizer (AAO),
with AAO `UVUsageCompabilityAPI` UV evacuation when AAO is installed (reflection-based; AAO
is optional).

## Install / 安装

Copy the package folder into `Packages/` (or add via VCC as a local package). Requires
**NDMF ≥ 1.14.4** and the VRChat Avatars SDK. Burst/Collections/Mathematics come with NDMF's
dependency chain.

## Usage / 使用

1. Add **ATO Avatar Texture Optimizer** to your avatar root (the object with the
   VRCAvatarDescriptor). Exactly one component per avatar.
   在 Avatar 根对象（挂有 VRCAvatarDescriptor）上添加组件，每个 Avatar 只允许一个。
2. Pick a **Quality Preset** (Balanced is recommended; Lossless copies pixels 1:1 without
   rescaling; Custom is never overwritten by preset switching).
   选择质量挡位（推荐"均衡"；"无损"跳过缩放原样拷贝；"自定义"不会被挡位切换覆盖）。
3. (Optional) Whitelist any object — every texture it references is left untouched.
   （可选）把任意对象加入白名单，其引用的贴图全部保持原样。
4. Build / bake. Progress is shown and cancelable; results appear in the NDMF console.
   构建即可；进度可取消，结果显示在 NDMF 控制台。

Anything ATO cannot handle safely (unknown shaders/properties, UV transforms incl. animated
`_ST`/scroll, cross-seam UVs, oversized islands) is **automatically whitelisted with an
information report** — worst case is "not optimized", never "broken".

## For developers / 面向第三方开发者

- `AtoExtensions.RegisterStage(IAtoCustomStage)` — insert custom pipeline stages
  (builtin stages sit at Order 100–900).
- `AtoExtensions.RegisterShaderSemantics(IAtoShaderSemanticsProvider)` — add shader support
  (lilToon + standard-keyword shaders ship builtin; unknown shaders fall back to whitelist).
- `AtoExtensions.OnBeforeProcess/OnAfterProcess` — inspect/modify the `AtoContext`.
- i18n: drop a `ATO_i18n_<code>.json` (flat key-value) anywhere in the project — the language
  appears automatically; UI language defaults to NDMF's setting, falls back to English.

## Logging / 日志

All logs are prefixed `[ATO]` with per-stage timings, atlas sources, island counts, sizes,
utilization and pixel reduction. Toggle verbose logging on the component.

## Known notes / 已知说明

- Islands in type groups containing normal maps are packed **without rotation** — rotating UVs
  would desynchronize stored tangents from the normal map (tangent data is never recomputed).
- NDMF preview is not supported yet (build/bake only).
- The NPOT candidate pool is experimental; formats that cannot handle NPOT are excluded
  automatically (e.g. no PVRTC anywhere; mobile uses ASTC).
