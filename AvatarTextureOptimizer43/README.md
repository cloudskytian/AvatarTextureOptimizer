# Avatar Texture Optimizer (ATO)

An open-source **NDMF** tool that atlases and shrinks VRChat avatar textures by analysing **mesh UVs**, not material parameters.

开源 NDMF 工具：按网格 UV（而不是材质参数）分析、缩小并图集化 VRChat Avatar 贴图。

Package: `net.fosa.avatar-texture-optimizer`  
Requires: Unity 2022.3, [NDMF](https://github.com/bdunderscore/ndmf) ≥ 1.8, VRChat Avatars SDK ≥ 3.7  
Optional: Modular Avatar, Avatar Optimizer (AAO), lilToon

---

## For avatar authors 给小白用户

### Install

1. Add this folder as a VPM/UPM package, **or** drop it into `Packages/net.fosa.avatar-texture-optimizer`.
2. Your project already needs NDMF and the VRChat SDK (the same setup Modular Avatar uses).

### Use

1. Select the **avatar root** (the object that has `VRCAvatarDescriptor`).
2. Add component **FOSA → Avatar Texture Optimizer**.  
   There must be **exactly one** on the whole avatar.
3. Leave the defaults. Default quality is **High** (looks the same at normal VRChat distance, saves a lot of VRAM).
4. Bake with NDMF / upload to VRChat as usual.
5. After bake the component removes itself from the baked copy. Check the **NDMF console** for a summary. Lines starting with `[ATO]` are detailed logs.

### What the defaults do

| Option | Default | Meaning |
| --- | --- | --- |
| Generate atlas | On | Pack UV islands into `ATO_*` atlases, drop unused texels, rewrite UVs |
| Quality | High | MS-SSIM 0.97, ΔE00 mean 2 / p95 4, … |
| Min / max density | 2048–4096 px/m | Stops blur and texel waste |
| Min padding | 4 px | Actual padding = max(4, ceil(atlasLongSide/128)) |
| Dedup materials / textures | On | Merge identical assets after optimize |
| Mip + MipStreaming | On | Bound together (VRChat rule) |

Turn **Generate atlas** off if you only want whole-texture downscale with original UVs.

### Whitelist

Drag **any** object (mesh, material, texture, animation clip, GameObject…) into the list. Every texture those objects reference skips **all** optimization. Sister textures that share the same UV still skip atlasing.

### Language

- **Auto** follows NDMF’s language.
- **Manual** picks a `Localization/*.json` file. Drop extra json files in that folder to add languages. Missing keys fall back to English.

### Cancel

The progress bar can be cancelled. Bake stops, temp files on disk stay, CPU/GPU/memory are released.

---

## For advanced users 给高级用户

Advanced foldout (inspector):

- Numeric quality thresholds (named presets overwrite them; **Custom** does not)
- Experimental NPOT atlases (64 px steps, OK with MipStreaming + Crunch)
- Per-class formats (opaque / transparent / normal / gray)
- Per-platform override (PC / Android / iOS), Unity-style: hidden until enabled
- Verbose `[ATO]` log

Safety fallbacks always win at bake time (e.g. a transparent atlas will not be saved as DXT1; a multi-channel gray map will not be saved as BC4 — you get a console warning).

NDMF **preview is not supported** yet.

---

## How it works 工作原理

Runs in NDMF `Optimizing` phase, **after Modular Avatar**, **before AAO**.

1. Skip EditorOnly / disabled (unless animation enables them).
2. Dedup textures by **pixels + importer settings** (different importer → different texture). Whitelist is sticky.
3. Analyse every texture slot (lilToon + standard keywords + `ShaderUtil`). Non-mesh UVs, decals, MatCap, `_ST` / scroll / rotate (including animation) → treat as whitelist + warning.
4. Build UV groups: one mesh UV channel ↔ all textures that sample it (including animation swaps). Same UV ⇒ same atlas layout.
5. Type groups: companion maps (normal/mask) + color space + filterMode. A texture used both with and without a normal is promoted to the “has normal” group.
6. Extract UV islands, translate overflow into `[0,1]` when it does not cross a wrap seam, merge overlaps.
7. World area = max(blendshape 0, each blendshape at 100) × max animated hierarchy scale.
8. Per-island quality search (uniform then anisotropic). Compare bilinear-upsampled coverage against the original (MS-SSIM, CIEDE2000, alpha IoU/RMSE, normal angle, gray RMSE). No final-compressor loss in the metric. Solid islands short-circuit when quality ≠ lossless.
9. Pack with 4px Burst-style bitmasks, full-scan BLF, **shape not rectangles**, 90° only when the UV group has **no** normals (tangents are never rebuilt).
10. Pull-push fill empty atlas texels (transparent keeps alpha 0).
11. Rewrite mesh UVs and **only** material texture references. Clone materials/meshes first.
12. If AAO is present, evacuate original UVs it still needs (`UVUsageCompabilityAPI`).
13. Dedup materials/atlases; merge opaque slots when animation does not solo-switch them.
14. Destroy this component on the baked avatar. Report to the NDMF console.

---

## For third-party developers 第三方扩展

```csharp
using Fosa.ATO.Editor;
using UnityEngine;

public class MyLilForkAnalyzer : IAtoShaderAnalyzer
{
    public AtoShaderInfo Analyze(Material material)
    {
        if (material == null || material.shader == null) return null;
        if (!material.shader.name.Contains("MyFork")) return null; // decline
        var info = new AtoShaderInfo { AlphaMode = AtoAlphaMode.Cutout, Cutoff = 0.5f };
        info.Slots.Add(new AtoShaderSlot {
            PropertyName = "_MainTex", UvChannel = 0, Class = AtoTextureClass.Opaque
        });
        return info; // first non-null extra analyzer wins after built-in lilToon/standard
    }
}

[UnityEditor.InitializeOnLoad]
static class Register {
    static Register() => AtoApi.RegisterShaderAnalyzer(new MyLilForkAnalyzer());
}
```

Events (same bake):

- `AtoApi.BeforeAnalyze` / `AfterAnalyze` / `BeforeApply` / `AfterApply`
- Context: `AtoBakeContext` (avatar root, component, resolved settings, report, texture refs, UV groups)

i18n: ship extra `Localization/<bcp47>.json` flat objects `{"key":"value"}`. Keys used by NDMF errors also need `:description` and `:hint` suffixes.

Do **not** modify shader parameters other than texture assignments from an extension if you want ATO’s safety guarantees.

---

## Quality presets

| Preset | MS-SSIM | ΔE00 mean / p95 | Normal ° mean / p95 | Cutout IoU | Blend α RMSE |
| --- | --- | --- | --- | --- | --- |
| Lossless | skip scaling |  |  |  |  |
| Ultra | 0.99 | 1.0 / 2.3 | 5 / 10 | 0.995 | 0.01 |
| **High (default)** | 0.97 | 2.0 / 4.0 | 8 / 15 | 0.99 | 0.02 |
| Medium | 0.94 | 3.5 / 6.0 | 12 / 20 | 0.97 | 0.04 |
| Low | 0.90 | 5.0 / 10 | 18 / 30 | 0.94 | 0.08 |
| Custom | all 1 (near-lossless) until you edit |  |  |  |  |

Sources: Wang 2003 MS-SSIM; Sharma 2005 CIEDE2000; typical JND ΔE00 ≈ 2.3.

---

## Known limitations

- NDMF preview: not implemented
- Blendshapes: only weight 0 and 100 per shape, no combinations, no negative / >100
- PhysBone-driven scale is not analysed
- Islands that wrap the UV seam or cannot be translated into `[0,1]` are skipped (warning)
- 90° packing rotation is disabled for UV groups that include normal maps
- GPU blit resample is used for opaque/large islands; premul-alpha and CIEDE2000/MS-SSIM still run on CPU (compute shader shipped for later)
- NPOT candidate pool keeps aspect ≤ 2 (full 64×64 double loop is too slow to BLF)
- If NDMF exposes a generated-asset folder, atlases are saved as PNG with TextureImporter (`mipmapEnabled` and `streamingMipmaps` bound). Otherwise they fall back to NDMF sub-assets + `CompressTexture`.

---

## License

MIT. See `LICENSE`.
