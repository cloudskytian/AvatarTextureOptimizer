# Avatar Texture Optimizer (ATO)

**The world's best VRChat avatar texture optimization tool.**

ATO is an open-source NDMF plugin that analyzes VRChat avatars' meshes, builds UV-to-texture mappings, shrinks UV islands by quality metrics, and packs them into efficient atlases — maximizing texture utilization while preserving visual quality.

**Avatar Texture Optimizer（ATO）是一个开源 NDMF 插件，用于分析 VRChat Avatar 的网格，建立 UV 到贴图的映射关系，按质量缩放 UV 岛，并将其打包成高效图集——在保持视觉质量的同时最大化贴图利用率。**

---

## ✨ Features / 功能

### Quality-Driven Optimization / 质量驱动优化
- **MS-SSIM** (Wang et al. 2003) for structural similarity
- **CIEDE2000** (Sharma et al. 2005) for perceptual color difference
- **Alpha metrics**: IoU (cutout) / premultiplied RMSE (blend)
- **Normal map**: Angular error + p95 after proper decode/renormalize
- **Grayscale**: Per-channel linear RMSE (worst channel)
- Binary search UV island scaling with anisotropic refinement
- Pure-color island shortcut
- Near-lossless preset (copy as-is)

### Smart Atlas Generation / 智能图集生成
- **UV groups**: Same UV position guaranteed across all atlas maps
- **Texture type groups**: Companion maps (normal/mask) share atlases for maximum utilization
- **BLF bin packing** with 4px-granularity bitmask rasterization
- **Candidate atlas pool**: POT or experimental NPOT
- 90° rotation support (color only — normal tangents never rotated)
- GPU pull-push edge bleeding for seamless padding

### Safety First / 安全第一
- **Whitelist system**: Any object type (mesh, material, texture, animation)
- **Strict eligibility**: Only textures without ST transforms, decals, or special UV usage
- **Never modifies material properties** other than texture references
- **Safe fallback**: Uncertain textures → whitelist + warning
- **AAO compatibility**: UV channel evacuation via UVUsageCompabilityAPI

### Other / 其他
- Material & texture deduplication (with animation-aware slot merging)
- Multi-channel UV support
- Blendshape area consideration (max of 0/100)
- Animation scale consideration
- lilToon-aware shader analysis
- Per-platform overrides (PC/Android/iOS)
- MipStreaming/Mipmap binding (VRChat compliance)
- User-extensible i18n (English + 简体中文 included)
- Detailed logging with timing and utilization reports

---

## 📦 Installation / 安装

1. Ensure your project has [NDMF](https://github.com/bdunderscore/ndmf) ≥1.6.0 installed
2. Add this package to your project (via VPM or manual import)
3. Add the **ATO** component to your avatar root

---

## 🚀 Usage / 使用

1. Select your avatar root
2. Add Component → **Avatar Texture Optimizer → ATO**
3. Configure settings:
   - **Quality Preset**: Near-Lossless / High / Medium / Low / Custom
   - **Generate Atlas**: Enable/disable atlas packing
   - **Padding**: Island spacing (4/8/16/32/64px)
   - **Whitelist**: Objects to skip
   - **Advanced**: Custom quality thresholds, GPU/Burst toggles
   - **Platform Overrides**: Per-platform atlas size limits
   - **Texture Formats**: Per-category compression settings
4. Build/play your avatar — ATO processes automatically during the NDMF build

---

## ⚙️ Quality Presets / 质量挡位

| Preset | MS-SSIM | ΔE | Alpha RMSE | Normal Angle | Description |
|--------|---------|------|------------|-------------|-------------|
| **Near-Lossless** | 1.0 | 0.0 | 0.0 | 0.0° | Skip all scaling, copy as-is |
| **High** | 0.995 | 1.0 | 0.01 | 5.0° | Very minor loss (default) |
| **Medium** | 0.97 | 3.0 | 0.03 | 10.0° | Good balance |
| **Low** | 0.93 | 6.0 | 0.08 | 20.0° | Aggressive optimization |
| **Custom** | User | User | User | User | Fully configurable |

---

## 🔧 Pipeline / 管线

ATO runs in NDMF's **Transforming** phase (after Modular Avatar, before Avatar Optimizer):

```
Scan → Dedup Textures → Analyze Shaders → Build UV Mappings →
Quality Scale → Rasterize → Pack → Render Atlas →
Rebake Mesh → Dedup Materials → AAO Compat → Import Settings
```

---

## 🔌 Extension API / 扩展 API

Third-party developers can extend ATO via `Fosa.AvatarTextureOptimizer.API.IATOExtensionPoint`:

```csharp
public class MyExtension : IATOExtensionPoint
{
    public bool OnPreScan(IATOPipelineContext context) { ... }
    public void OnMappingsBuilt(IATOPipelineContext context) { ... }
    public List<IslandRef> OnPrePack(IATOPipelineContext context, List<IslandRef> islands) { ... }
    public void OnPostAtlas(IATOPipelineContext context) { ... }
}
```

### i18n Extension / 国际化扩展

Add a JSON file to the i18n folder to add a new language:

```json
{
    "entries": [
        {"key": "ato.title", "value": "Your translation here"}
    ]
}
```

---

## 📋 Requirements / 要求

- Unity 2022.3+
- [NDMF](https://github.com/bdunderscore/ndmf) ≥1.6.0
- VRChat SDK 3 (recommended)
- Unity Burst, Collections, Mathematics packages

---

## 📝 License / 许可

MIT License — see [COPYING](COPYING.md)

---

## 🙏 Credits / 致谢

- Quality metrics based on: Wang et al. (2003), Sharma et al. (2005)
- NDMF framework by [bdunderscore](https://github.com/bdunderscore)
- lilToon shader compatibility by [lilxyzw](https://github.com/lilxyzw)
- AAO UV compatibility API by [anatawa12](https://github.com/anatawa12)
