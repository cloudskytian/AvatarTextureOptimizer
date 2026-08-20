# Avatar Texture Optimizer (ATO)

`net.fosa.avatar-texture-optimizer` — an open-source [NDMF](https://github.com/bdunderscore/ndmf)
tool for VRChat avatars. It maps mesh UVs to **textures** (not materials), scales each UV island
to a target perceptual quality, culls unused texture area, and repacks the islands into
**texture-type-grouped atlases** — all while completely ignoring unrelated material parameters.

> Status: **early development**. The pipeline is wired end-to-end and bakes through NDMF. The GPU
> quality/bleed shaders and a few advanced edge cases (true submesh merging, GPU-batch MS-SSIM) are
> implemented but should be verified on real avatars before production use.

## What it does

1. Scans every enabled/animated `SkinnedMeshRenderer`/`MeshRenderer` (skips `EditorOnly`).
2. Builds a **mesh-UV → texture** map. Even if the user swaps materials that share a texture, the
   map is unchanged, so the result is reusable.
3. Analyzes animations for material/texture swaps, object activation, object scale, animated ST
   (tiling/offset), render-mode/cutoff changes, and takes the **strictest** requirement.
4. Deduplicates source textures by **pixels + import settings** (different import settings count as
   different).
5. Extracts UV islands (connected triangles), normalizes out-of-[0,1] UVs when safe, and merges
   overlapping islands within the same texture.
6. Scales each **UV group** (all maps — color/normal/mask/animation — that share one UV identity)
   to the smallest size that still passes every quality threshold, using a binary search:
   - **Linear-space** resampling; **premultiplied-alpha** downscaling for transparent maps.
   - **MS-SSIM** (single-scale SSIM when the box short edge < 176px; metric ignored below 11px)
     + **CIEDE2000 ΔE** + **alpha** (Cutout: post-clip contour IoU; Blend: linear RMSE).
   - **Normal maps**: decode → linear resample → renormalize → encode, then angular error + p95.
   - **Grayscale/data maps**: linear RMSE on the used channels only, worst channel wins.
   - All metrics compare the *downscaled-then-bilinearly-upscaled* region against the original
     (final GPU compression loss is **excluded**).
   - **Pixel density** clamp (min/max px per world meter) and a source-pixel-size clamp.
   - **Solid-color** islands collapse to `min(4, short edge)` (unless quality is near-lossless).
   - Quality == 1 skips resampling entirely (raw copy) for that texture class.
7. Packs islands into atlases using a **4px-granularity bitmask + full-scan BLF + 90° rotation
   (bitmask transpose)** raster packer, with a candidate **atlas pool** (power-of-two or
   experimental NPOT). UV groups are placed atomically; same-UV maps always land at identical
   positions across their type group's atlases. Normal maps are never rotated and never
   recomputed.
8. **Bleeds** island edges into padding using a GPU **pull-push** dilation (transparent islands
   keep alpha 0).
9. Rewrites **mesh UVs** only, and **texture references** only — never any other material
    property. Evacuates UV channels with AAO's `UVUsageCompabilityAPI` when AAO is installed.
10. Optionally deduplicates materials and generated textures/atlases, and reports timing, island
    counts, atlas sizes, utilization, and memory savings to the **NDMF console**.

### Texture type groups

Maps are grouped by `(which special maps exist, sRGB, filterMode)`. For example, two color maps
that both have normals share one group; three maps that have normals *and* masks share another.
This avoids the "10 maps in one atlas, only 1 has a normal → 9/10 of the normal atlas wasted"
problem. If a sub-map type (e.g. masks) in a group needs lower quality overall, its atlas can be
scaled down past minimum padding to save space.

### Safety first

Anything that cannot be proven safe is treated as whitelist + warning:

- unknown shaders / unclassifiable texture properties,
- ST tiling/offset or animated ST, decals/parallax or transform usage,
- UVs that cross a wrap seam (rely on Repeat),
- a single island that doesn't fit the largest atlas (becomes a scaled standalone texture),
- user-selected formats that don't fit content (alpha content without alpha format, single-channel
  request for multi-channel grayscale) — corrected at build time with a warning.

## Install

This is a UPM package. Drop the `net.fosa.avatar-texture-optimizer` folder into your project's
`Packages/` directory (or add it as a local package). Requirements:

- Unity 2022.3
- [VRChat Avatars SDK 3.10.x](https://vrchat.com/home/download)
- [NDMF 1.14.4+](https://github.com/bdunderscore/ndmf)
- (optional) Modular Avatar, Avatar Optimizer, lilToon, avatar-compressor, LightLimitChanger — ATO
  detects and soft-integrates with them but works without them.

## Use

1. Add the component via **Tools → Avatar Texture Optimizer → Add to selected avatar** (it must go
   on a GameObject with a `VRCAvatarDescriptor`; one component per avatar).
2. Pick a **Quality Preset** (Low / Medium / High / Very High / Near Lossless / Custom).
3. Add anything you don't want touched to the **Whitelist**.
4. Build & Test the avatar normally — ATO runs in NDMF's **Optimizing** phase, after Modular Avatar
   and before Avatar Optimizer.

### Platform overrides

PC / Android / iOS each have a foldout. Enable one to override max atlas size, NPOT, and
per-class format/mip settings; NPOT automatically strips unsupported formats (e.g. iOS strips
PVRTC-only combinations).

## Extending ATO

Third parties can register extensions (assembly reference to `net.fosa.ato.editor`) through
`Fosa.Ato.Editor.Extensibility.AtoExtensions`:

```csharp
public interface IShaderAnalyzer  { ... } // custom shader texture classification
public interface IQualityMetric   { ... } // extra pass/fail quality metric
public interface IAtlasPacker     { ... } // custom placement strategy
```

### i18n

Translations live in `Resources/i18n/ato-*.json`. Drop a new `ato-<lang>.json` next to the others
(or in `Assets/AvatarTextureOptimizer/i18n/`) and it appears in the language dropdown automatically.
The default is **Auto** (reads NDMF's language) with English fallback.

## Layout

```
Runtime/
  Components/AvatarTextureOptimizer.cs   component + serialized settings
  Models/AtoSettings.cs                  quality presets, per-class params, platform overrides
Editor/
  Plugin/                                NDMF plugin + pass ordering
  Pipeline/                              orchestrator + data model
  Pipeline/Stages/                       01 collect .. 12 finalize
  Analysis/                              shader property analyzer, UV rasterizer
  Quality/                               MS-SSIM / ΔE / normal / data metrics + binary search
  Packing/                               bitmask BLF packer + atlas pool
  Shaders/                               pull-push bleed shader (GPU dilation)
  Util/                                  logging, progress, color math, texture IO, AAO bridge
  i18n/                                  localizer
  UI/                                    custom inspector
  Extensibility/                         public extension interfaces
```

## Logging

Every stage logs `[ATO] ⏱ <stage>: <ms>`. Enable **Verbose Logging** in the inspector (advanced) for
per-atlas details: source textures, island counts, utilization, and before/after bytes.

## Known limitations / roadmap

- NDMF preview is not supported (by design, for now).
- True submesh merging for identical opaque materials is conservative (material references are
  deduplicated; index-buffer merging is a planned follow-up).
- The CPU metric path is the reference; the GPU compute batch path is wired but should be
  benchmarked and verified on real avatars.
- Settings fields may change freely during development; no version-compatibility promise yet.
