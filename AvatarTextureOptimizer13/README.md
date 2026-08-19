# AvatarTextureOptimizer (ATO)

> The best VRChat avatar texture optimizer. 全世界最好的 VRChat 贴图优化工具。

**ATO** is an open-source **NDMF** plugin for VRChat avatars. It analyzes the mapping between a
mesh's UV coordinates and its textures, then — without touching any shader parameter other than
texture references — shrinks UV islands to a configurable **target quality**, strips the unused
parts of each texture, and re-packs the islands into one or more atlases to maximize texture
utilization while preserving visual quality.

- **Package name**: `net.fosa.avatar-texture-optimizer`
- **Runs**: after **Modular Avatar**, before **Avatar Optimizer (AAO)** in the NDMF `Optimizing` phase.
- **Non-destructive**: only meshes and texture references are modified; source assets are never mutated.

---

## Features 功能

- **UV ↔ texture mapping analysis** — builds the relationship between mesh UVs and the textures
  sampled on each material slot, including textures introduced by **animation material/texture swaps**.
- **Target-quality island scaling** — binary-search (uniform → per-axis anisotropic) driven by
  `MS-SSIM / SSIM + ΔE(CIEDE2000) + alpha (IoU for Cutout, RMSE for Blend)`, `normal angle error`,
  and per-channel grayscale RMSE. Every metric must pass; the strictest referencing material wins.
- **Texture type groups** — textures sharing the same "special map signature" (normal / mask,
  color space, filter mode) are packed together so the atlases for their normals / masks do not
  waste space.
- **UV groups** — all textures sampled with the same UV (including animation swaps) share an
  identical island layout across their atlases.
- **Atlas packing** — bitmask rasterization (4 px cells) + full-scan **BLF** with 90° rotation
  steps (rotation disabled for normal maps — tangent data is never recomputed), candidate atlas
  pool (POT or experimental NPOT), edge **pull-push** padding fill.
- **Whitelist** — whitelist any mesh / material / texture / animation / GameObject; every texture
  it references is skipped from all optimization (whitelist contamination propagates through dedup).
- **Deduplication** — textures by content + import settings; fully-equivalent materials; identical
  generated atlases; opaque material slot merging (with animation-safe checks).
- **Multi-UV-channel** support, out-of-bounds UV normalization, overlapping-island merging,
  blend-shape (0/100) and animated-scale area handling.
- **lilToon & standard-keyword shader analysis** — property tables derived from lilToon's public
  shader structure; unknown shaders fall back to whitelist + warning.
- **Platform overrides** (PC / Android / iOS), per-kind safe compression choices, Mipmap +
  MipStreaming binding, forced Clamp + Read/Write off on generated atlases.
- **AAO compatibility** — reflects `UVUsageCompabilityAPI` (AAO's original spelling) and evacuates
  original UVs to a spare channel when AAO would use a channel ATO rewrites. Works without AAO too.
- **Cancellable progress**, memory-conscious caching, `[ATO]`-prefixed verbose logging, per-stage
  timings, and a final report in the NDMF console (summary shown, details collapsed).
- **Extensible** — `IATOPostProcessor` hook + `ATOExtensionOrderAttribute`; user-extensible JSON i18n.

---

## Requirements 依赖

- Unity **2022.3**
- `com.vrchat.base` ≥ 3.10.4, `com.vrchat.avatars` ≥ 3.10.4
- `nadena.dev.ndmf` ≥ 1.14.4
- `com.unity.burst`, `com.unity.collections`, `com.unity.mathematics` (declared; Burst backend is
  a planned optimization on top of the included CPU reference implementation)

---

## Installation 安装

1. Add the package to your VCC / VPM project (or copy this folder into `Packages/`).
2. Put an **`Avatar Texture Optimizer`** component (menu `ATO/Avatar Texture Optimizer`) on the
   GameObject that holds your **VRCAvatarDescriptor**.
   - Only **one** component per avatar subtree is allowed.
3. Configure the options (defaults are a good starting point) and upload / run the NDMF build.

### Options 选项（摘要）

| Option | Default | Notes |
|---|---|---|
| Target Quality | Balanced | Lossless / High / Balanced / Low / Custom (custom defaults to all-1 = near-lossless) |
| Generate Atlas | on | off → no atlas / no unused-UV stripping / no UV re-arrangement; only whole-texture scaling |
| Min / Max Pixel Density | 2048 / 4096 px/m | presets 512 / 1024 / 2048 / 4096 / 8192; clamped by the island's real footprint |
| Island Padding | 4 px | 4 / 8 / 16 / 32 / 64; effective = max(user, ceil(edge/128)) ≥ 4 |
| NPOT atlas sizes | off | experimental; 64 px steps; incompatible formats (e.g. iOS PVRTC) excluded |
| Dedup materials / textures | on | dedup + opaque slot merging (animation-safe) |
| Mipmaps (MipStreaming bound) | on | single switch (VRChat requires Streaming when Mipmaps are on) |
| Compression | Auto per kind | opaque color / transparent color / normal / grayscale |

---

## How it works 工作原理

```
MA (Modular Avatar)  →  ATO  →  AAO (Avatar Optimizer)
```

1. **Validate** — exactly one component; must sit on a `VRCAvatarDescriptor`; else abort.
2. **Analyze** — renderers (enabled or animated-on), material slots, shader property tables
   (lilToon / standard / generic), animation material & texture swaps, whitelist, texture dedup,
   UV groups + texture type groups, UV island extraction (per slot / channel), strictest alpha mode.
3. **Optimize** — per-island binary-search scaling (or whole-texture scaling when atlas is off),
   with solid-color / lossless shortcuts and density clamping.
4. **Atlas** — BLF packing per type group, pull-push fill, saved with configured import settings.
5. **Reassign** — clone meshes, rewrite UVs (with AAO evacuation), update material + animation
   texture references.
6. **Dedup** — materials (renderers + animation clips), opaque slot merge, identical atlases.
7. **Report & cleanup** — post processors, remove the component, print the NDMF-console report.

---

## Extending ATO 扩展

Implement `IATOPostProcessor` in your own assembly; ATO discovers it via `TypeCache` and runs it
after the core pipeline, before the final report:

```csharp
[ATOExtensionOrder(100)]
public class MyPostProcessor : IATOPostProcessor
{
    public string DisplayName => "My Post Processor";
    public void PostProcess(ATOPostProcessContext context)
    {
        foreach (var tex in context.generatedTextures)
            Debug.Log($"Atlas {tex.name}: util={tex.utilization:P0}");
    }
}
```

## i18n 本地化

Drop `*.json` files (flat `key: value`) into the package `Localization/` folder — each file is one
language (`en.json`, `zh-Hans.json`, …). "Auto" follows NDMF's language with a language-family
fallback and finally English.

---

## Development status 开发状态

ATO is in active development. The pipeline is fully implemented end-to-end:

- **Burst-accelerated metrics** (SSIM / MS-SSIM, alpha RMSE & IoU, normal angle error,
  area-average & bilinear resampling) with automatic CPU fallback, plus a
  **GPU (RenderTexture) bilinear upsample** path — every backend degrades gracefully.
- Import-parameter application (Mipmap ⇔ MipStreaming single switch) for kept,
  non-whitelisted textures via clone-on-diff (source assets are never mutated).
- NPOT atlas format exclusion (PVRTC on iOS → ASTC), grayscale multi-channel and
  alpha-channel safety fallbacks with console warnings.

### Remaining validation 剩余验证

- This repository is developed **without a Unity checkout**; the code is written strictly
  against the documented APIs of NDMF 1.14.4 / AAO 1.9.17 / MA 1.18.2 / lilToon 2.3.4 /
  VRC 3.10.4, but **must be compiled and bake-tested inside Unity** before release.
- Burst / GPU backends are opt-in accelerators with CPU reference fallbacks; their
  scheduling is verified by construction but should be profiled on real avatars.

See `CLAUDE.md` for the full design notes and roadmap.

---

## License 许可证

MIT (to be confirmed with the maintainer). Open-source; contributions welcome.
