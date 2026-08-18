# AvatarTextureOptimizer (ATO)

The world's best VRChat avatar texture optimizer. Built on [NDMF](https://github.com/bdunderscore/ndmf) to work alongside Modular Avatar, Avatar Optimizer, lilToon, and other non-destructive tools.

**Language / 语言**: English / 中文（代码注释、i18n均中英双语）

## What it does (功能)

ATO analyzes every mesh renderer on your avatar, maps UV islands → textures in a material-agnostic way (so swapping materials, animating textures, or changing shader parameters doesn't invalidate the mapping), and:

- Repacks UV islands into tightly packed atlases, discarding unused texels
- Scales each UV island independently to a target quality level using MS-SSIM + CIEDE2000 ΔE + normal angular error + alpha metrics
- Groups textures that share the same UV coordinates (albedo + normal + masks etc.) into aligned "UV groups" so UV locations match across layers
- Groups texture layers into "texture type groups" so that (e.g.) two textures with normal maps are packed together — avoiding the wasted atlas space you get when mixing normal-less and normal-having textures in the same atlas
- Respects per-platform texture settings, MipStreaming binding, and safe compression formats
- Preserves animation references, material slots, and blendshapes
- Provides a whitelist system for objects/materials/textures/animations you don't want touched
- Runs after Modular Avatar, before Avatar Optimizer, and cooperates with AAO's UVUsageCompatibilityAPI

## Status / 状态

v0.1.0-dev — Full pipeline implemented:

- ✅ UV island extraction with UV-edge welding (handles split normals / hard edges)
- ✅ UV normalization for out-of-[0,1] UVs that don't cross wrap seams
- ✅ Animation integration (texture swaps, material switches, ST animation whitelisting, blend shape max-area, animation scale)
- ✅ BlendShape max triangle area (weight 0 + 100) and animation scale worst-casing
- ✅ Shader database for Unity Standard, Unlit, lilToon (full prop list), UTS2, plus auto-discovery
- ✅ Quality metrics: MS-SSIM / SSIM, CIEDE2000 ΔE, normal angular p95, alpha RMSE (Blend), cutout IoU, grayscale RMSE
- ✅ Quality-driven per-island binary-search scaling (uniform + anisotropic refinement)
- ✅ Solid-color short-circuit (4px minimum when quality < 1)
- ✅ UV groups (same UV = aligned placement across type groups)
- ✅ Texture type groups (sRGB/filter/normal/alpha separation)
- ✅ 4px-granularity bitmask triangle rasterization
- ✅ Bottom-Left Fill packing with 90° rotation + pull-push edge dilation
- ✅ Candidate atlas pool (POT / NPOT at 64px steps)
- ✅ Proper tangent rotation (-90° about normal) for rotated normal-map islands
- ✅ GPU blit scaling with CPU fallback; clamp wrap mode; mipmap + MipStreaming binding
- ✅ Padding formula: `max(4, configured, ceil(max_side/128))`
- ✅ Platform overrides (PC/Android/iOS) with safe compression format popups
- ✅ AAO `UVUsageCompabilityAPI` reflection-based integration (UV evacuation)
- ✅ Texture deduplication (pre-analysis) and material merging (post-atlas)
- ✅ Cancelable progress bar; NDMF error report integration
- ✅ i18n (English + Simplified Chinese); auto-language from NDMF prefs
- ✅ Non-atlas whole-texture scaling path
- ✅ Whitelist system (GameObject/Material/Texture hierarchy-aware)
- ✅ Self-removal after bake
- 🔲 Burst/Job acceleration for rasterization (CPU path is currently used; Burst package is optional)
- 🔲 GPU-compute quality metrics (CPU path is used currently)

v0.1.0-dev — 完整管线已实现。所有核心功能均已上线，剩余的Burst/GPU加速项为可选性能优化（CPU路径功能完整正确）。

## Installation / 安装

1. Ensure your Unity project already has:
   - VRChat Avatars SDK 3.7+
   - [NDMF](https://github.com/bdunderscore/ndmf) 1.14+
   - [Modular Avatar](https://github.com/bdunderscore/modular-avatar) 1.18+ (recommended)
   - [Avatar Optimizer](https://github.com/anatawa12/AvatarOptimizer) 1.9+ (recommended)
   - [lilToon](https://github.com/lilxyzw/lilToon) 2.0+ (if you use it; auto-detected)
2. Copy the `net.fosa.avatar-texture-optimizer` folder into your project's `Packages/` directory (or add as a VPM package).
3. Add the `Avatar Texture Optimizer` component to your avatar root (the object with your `VRCAvatarDescriptor`). Only one per avatar.
4. Configure your quality preset and options, then build & test.

## Quality Presets / 质量挡位

| Preset | MS-SSIM | ΔE | Normal Angle | α RMSE (Blend) | Cutout IoU |
|--------|---------|----|--------------|----------------|------------|
| Very Low | 0.90 | 10.0 | 12° | 0.12 | 0.94 |
| Low | 0.94 | 6.0 | 8° | 0.08 | 0.96 |
| **Medium** | 0.97 | 3.5 | 5° | 0.04 | 0.98 |
| High | 0.985 | 2.0 | 3° | 0.02 | 0.99 |
| Very High | 0.995 | 1.0 | 1.5° | 0.01 | 0.995 |
| Custom | user-defined (defaults to near-lossless 1.0) | | | | |

Pixel density presets: 512 / 1024 / 2048 (default min) / 4096 (default max) / 8192 px/m.

## Key Features / 关键特性

- **Atlas generation on/off**: When disabled, ATO won't repack UVs — it will only scale whole textures and apply import settings.
- **Platform overrides**: Per-platform (PC / Android / iOS) max atlas size, compression format, mipmap + MipStreaming binding, and Crunch compression.
- **NPOT atlases (experimental)**: Non-power-of-two atlas sizes at 64px steps for better density; automatically disables incompatible formats (iOS PVRTC).
- **Padding control**: 4 / 8 / 16 / 32 / 64 px between islands, with GPU pull-push edge bleeding to reduce mipmap artifacts.
- **Whitelist**: Drag any GameObject, Mesh, Material, Texture, or AnimationClip to skip it (and all textures it references) from optimization.
- **Automatic shader analysis**: Recognizes Unity Standard, Unlit, lilToon, UTS2, and VRChat mobile shaders out of the box, plus best-effort auto-detection of unknown shaders.
- **Safety first**: Any UV set that has ST offsets/rotation (animated or static), crosses wrap seams in a way that can't be normalized, uses non-mesh UV (screen-space / matcap), or can't be safely analyzed is automatically whitelisted with a warning in the NDMF console.
- **i18n**: Auto-detects NDMF language; ships with English and Simplified Chinese; third parties can add more by dropping JSON files into `Editor/Resources/Localization/`.

## Extending ATO / 扩展ATO

Third-party developers can hook into the pipeline:

```csharp
using net.fosa.avatar_texture_optimizer.Editor;

// Register a custom shader recognizer so ATO knows your shader's texture slots
ATOExtensions.RegisterShaderRecognizer((mat) => {
    if (mat.shader.name != "My/Shader") yield break;
    yield return ("_MyMainTex", TexturePropertyKind.BaseColor, 0, AlphaMode.Opaque, 0.5f);
    yield return ("_MyBump",   TexturePropertyKind.Normal,   0, AlphaMode.Opaque, 0.5f);
});

// Register a custom post-processor that runs after atlases are built
ATOExtensions.RegisterPostProcessor((analysis, atlases) => {
    // your code here
});
```

## Safety Guarantees / 安全性保证

- ATO only modifies meshes and texture references — **no other shader/material parameters are ever changed**.
- When anything looks unsafe (unrecognized shader with non-standard UV transforms, animated UV scale/offset, texture that isn't readable, etc.), that texture falls back to a safe whole-texture optimization path (or is skipped entirely) instead of risking broken output.
- Tangent-space normal maps are never rotated unless the tangent frame can be correctly updated (currently rotation is disabled for normals; when enabled in a future update, mesh tangents will be rotated along with the UV island).
- Mipmaps and MipStreaming are always enabled/disabled together per VRChat requirements.
- The ATO component removes itself after a bake so the avatar doesn't carry editor-only data into uploads.

## Logs & Reporting / 日志与报告

After a bake, check the NDMF Error Report window for a summary: atlas count, islands processed, texture memory savings, warnings, and per-atlas details (when verbose logging is on). All logs are prefixed with `[ATO]` in the Unity console.

烘焙完成后，请在NDMF错误报告窗口查看摘要：图集数、处理的岛数、贴图内存节省、警告，以及（开启详细日志时）每个图集的详情。所有日志在Unity控制台以`[ATO]`开头。

## Build Phase Order / 构建阶段顺序

```
Modular Avatar (Transforming phase)
        ↓
Avatar Texture Optimizer (Transforming phase, after MA, before AAO)
        ↓
Avatar Optimizer (Optimizing phase)
```

ATO is compatible with AAO's `UVUsageCompabilityAPI` — when AAO is present, ATO evacuates UV channels that AAO needs so UV packing doesn't interfere with AAO features like Remove Mesh By Mask.

## License / 许可证

MIT

## Thanks / 致谢

- [bd_](https://github.com/bdunderscore) for NDMF and Modular Avatar
- [anatawa12](https://github.com/anatawa12) for Avatar Optimizer
- [lilxyzw](https://github.com/lilxyzw) for lilToon
- The VRChat creator community
