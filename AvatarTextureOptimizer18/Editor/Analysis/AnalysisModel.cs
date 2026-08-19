using System.Collections.Generic;
using UnityEngine;

namespace Fosa.AvatarTextureOptimizer.Editor.Analysis
{
    // 贴图类型（决定度量方式与类别）。Texture kind (drives metrics and category).
    public enum ATOTextureKind
    {
        Color = 0,
        NormalMap = 1,
        Grayscale = 2,
        Mask = 3
    }

    // 透明模式。Alpha mode.
    public enum ATOAlphaMode
    {
        Opaque = 0,
        Cutout = 1,
        Blend = 2,
        Unknown = 3
    }

    // 白名单级别。Whitelist level.
    public enum ATOWhitelistLevel
    {
        // 正常处理。Processed normally.
        None = 0,
        // 跳过图集化；仍参与整图缩放与导入参数优化。No atlasing; still participates in whole-texture scaling and import-parameter optimization.
        NoAtlas = 1,
        // 跳过一切优化（含导入参数）。Skips everything including import parameters.
        Full = 2
    }

    // 材质槽条目：渲染器 + 槽位索引 + 材质 + 该槽的贴图使用。
    // A material slot entry: renderer + slot index + material + the texture uses of this slot.
    public sealed class SlotEntry
    {
        public Renderer renderer;
        public int slotIndex;
        public Material material;
        // 渲染网格（SkinnedMeshRenderer.sharedMesh 或 MeshFilter.sharedMesh）。The rendered mesh.
        public Mesh mesh;
        public bool isSkinned;
        public readonly List<TextureUse> uses = new List<TextureUse>();

        // 动画是否启停该渲染器。Whether animation toggles this renderer.
        public bool rendererToggledByAnimation;
        // 动画是否切换该材质槽（m_Materials.Array.data[i] 对象引用动画）。Whether animation swaps this slot's material.
        public bool slotSwappedByAnimation;
        // 禁用且动画永不启用 → 不处理。Disabled and never enabled by animation → skipped.
        public bool alwaysDisabled;
        // 该槽可能出现的全部材质（基础材质 + 动画切换材质）。All materials that may appear on this slot (base + animated swaps).
        public readonly List<Material> sourceMaterials = new List<Material>();

        public override string ToString()
        {
            return renderer != null
                ? string.Format("{0}[{1}] ({2})", renderer.name, slotIndex, material != null ? material.name : "null")
                : "SlotEntry(null)";
        }
    }

    // 一次贴图使用：某个材质槽通过某属性引用某贴图。
    // One texture usage: a material slot references a texture through a property.
    public sealed class TextureUse
    {
        public SlotEntry slot;
        // 引用该贴图的材质（基础材质或动画切换材质）。The material referencing this texture (base or animated swap).
        public Material sourceMaterial;
        public string propertyName;
        public Texture2D texture;
        public ATOTextureKind kind = ATOTextureKind.Color;
        // 解析后的 UV 通道（含 UVMode 属性静态值）。Resolved UV channel (incl. static UVMode property value).
        public int uvChannel;
        // 着色器声明 [NoScaleOffset] → ST 无关。Shader declares NoScaleOffset → ST is irrelevant.
        public bool noScaleOffset;
        // 静态 ST/ScrollRotate 变换（非默认值）。Static ST/ScrollRotate transform (non-default values).
        public bool stTransform;
        // 动画修改 ST/ScrollRotate。ST/ScrollRotate animated.
        public bool stAnimated;
        // 动画修改 UVMode 属性。UVMode property animated.
        public bool uvModeAnimated;
        // 特殊用途 UV（MatCap/Rim/渐变/屏幕空间等）→ 白名单。Special-purpose UV → whitelist.
        public bool specialPurposeUV;
        // 来自动画切换材质。Comes from an animated material swap.
        public bool fromAnimatedSwap;
        // 来自动画直接切换的贴图。Comes from an animated texture-property swap.
        public bool animatedTextureProperty;
        public ATOAlphaMode alphaMode = ATOAlphaMode.Unknown;
        public float cutoff = 0.5f;
        public ATOWhitelistLevel whitelistLevel = ATOWhitelistLevel.None;
        public string whitelistReason;

        public bool Processed => whitelistLevel == ATOWhitelistLevel.None;

        public override string ToString()
        {
            return string.Format("{0}.{1} ({2})", slot != null ? slot.ToString() : "?", propertyName, texture != null ? texture.name : "null");
        }
    }

    // 贴图条目（去重后的物理贴图）。A texture entry (a physical texture after dedup).
    public sealed class TextureEntry
    {
        public Texture2D source;
        public string assetPath = "";
        public string assetGuid = "";
        // 导入设置签名（不同导入设置视为不同贴图）。Import-settings signature (different settings → different texture).
        public string importKey = "";
        // 像素内容签名。Pixel-content signature.
        public string pixelKey = "";
        public int width, height;
        public bool sRGB = true;
        public bool readable;
        public bool hasAlpha;
        public TextureWrapMode wrapU = TextureWrapMode.Repeat;
        public TextureWrapMode wrapV = TextureWrapMode.Repeat;
        public FilterMode filterMode = FilterMode.Bilinear;
        public int anisoLevel = 1;
        public bool mipmapEnabled;
        public bool streamingMipmaps;
        public bool isNormalMapImporter;
        // 分类：使用中最严苛者（法线 > 颜色 > 蒙版 > 灰度）。Kind: the most demanding among its uses.
        public ATOTextureKind kind = ATOTextureKind.Color;
        // 是否存在不同用途的不同分类。Whether uses have mixed kinds.
        public bool mixedKinds;
        // 最严苛透明模式（Blend > Cutout > Opaque）。Worst alpha mode among uses.
        public ATOAlphaMode worstAlphaMode = ATOAlphaMode.Unknown;
        public ATOWhitelistLevel whitelistLevel = ATOWhitelistLevel.None;
        public string whitelistReason;
        // 若非空：本条目将被替换为该条目（去重结果）。If set, this entry will be replaced by the target (dedup).
        public TextureEntry dedupTarget;
        // 被动画直接引用（贴图切换目标）。Referenced directly by animation (texture-swap target).
        public bool animatedSwapReference;
        public readonly List<TextureUse> uses = new List<TextureUse>();
        // 原始体积估算（报告用）。Original size estimate (for reports).
        public long originalByteSize;
        // 整图缩放结果（非图集化贴图：无图集模式、NoAtlas、fallback）。Whole-texture scale (for non-atlased textures).
        public float wholeTextureScale = 1f;
        // 整图缩放已应用。Whole-texture scaling applied.
        public bool wholeTextureScaled;
        // 灰度贴图被使用的通道位掩码（r=1,g=2,b=4）。Grayscale used-channel bitmask.
        public int usedChannels = 0b111;
        // 法线解码模式（DXT5nm swizzle）。Normal decode mode (DXT5nm swizzle).
        public bool dxt5nm;
        // 替换贴图（图集/整图缩放/导入副本；应用阶段写入）。Replacement texture (atlas/whole-scaled/import copy; set at apply).
        public Texture2D replacementTexture;
        // 替换贴图对应的图集计划。The atlas plan of the replacement (if atlased).
        public Packing.AtlasPlan replacementAtlas;

        public bool IsDedupSurvivor => dedupTarget == null;
        public bool Processed => whitelistLevel == ATOWhitelistLevel.None && dedupTarget == null;

        // 是否已被动画直接切换（贴图属性动画的目标）。Whether this texture is animated into a property directly.
        public bool IsAnimatedSwap => animatedSwapReference;

        public override string ToString()
        {
            return string.Format("{0} ({1}x{2})", source != null ? source.name : "null", width, height);
        }
    }

    // 单个动画剪辑引用的集合（用于白名单动画传播）。References of a single animation clip (for whitelisted-clip propagation).
    public sealed class ClipRefs
    {
        public readonly HashSet<Material> materials = new HashSet<Material>();
        public readonly HashSet<Texture2D> textures = new HashSet<Texture2D>();
    }

    // 动画分析结果。Animation analysis results.
    public sealed class AnimationAnalysis
    {
        // 动画切换到的材质。Materials animated into slots.
        public readonly HashSet<Material> materialSwapTargets = new HashSet<Material>();
        // 任何动画引用过的材质（材质去重需保守处理）。Materials referenced by any animation (conservative for material dedup).
        public readonly HashSet<Material> materialsReferenced = new HashSet<Material>();
        // 动画切换的贴图目标。Textures animated into material properties.
        public readonly HashSet<Texture2D> animatedTextureTargets = new HashSet<Texture2D>();
        // 动画启停过的 Renderer。Renderers toggled by animation.
        public readonly HashSet<Renderer> rendererToggled = new HashSet<Renderer>();
        // 动画启停过的 GameObject。GameObjects toggled by animation.
        public readonly HashSet<GameObject> objectToggled = new HashSet<GameObject>();
        // 动画最大局部缩放（逐分量绝对值）。Max animated local scale per axis (absolute).
        public readonly Dictionary<Transform, Vector3> maxLocalScale = new Dictionary<Transform, Vector3>();
        // 形态键：名称 → max(0,100) 状态的最大权重（仅取 0 与 100 两个状态的最大值，不考虑负数/超 100/排列组合）。
        // Blend shapes: name → max weight between the 0 and 100 states (only these two states; no negatives, >100 or combinations).
        public readonly Dictionary<SkinnedMeshRenderer, Dictionary<string, float>> blendShapeWeights = new Dictionary<SkinnedMeshRenderer, Dictionary<string, float>>();
        // 槽位级 float 属性动画范围（属性名已去除 .x/.y/.z/.w 分量后缀）。Slot-level animated float-property ranges.
        public readonly Dictionary<SlotEntry, Dictionary<string, Vector2>> slotFloatRanges = new Dictionary<SlotEntry, Dictionary<string, Vector2>>();
        // 槽位级贴图属性动画（贴图切换/引用动画）。Slot-level animated texture properties.
        public readonly Dictionary<SlotEntry, HashSet<string>> slotTexturePropsAnimated = new Dictionary<SlotEntry, HashSet<string>>();
        // 槽位级切换材质集合（动画切换该槽位的所有目标材质）。Slot-level swap material set (all materials animated into the slot).
        public readonly Dictionary<SlotEntry, HashSet<Material>> slotSwapMaterials = new Dictionary<SlotEntry, HashSet<Material>>();
        // 槽位级切换贴图集合（动画直接切换的贴图目标）。Slot-level swap texture set (textures animated into the slot's properties).
        public readonly Dictionary<SlotEntry, HashSet<Texture2D>> slotSwapTextures = new Dictionary<SlotEntry, HashSet<Texture2D>>();
        // 每个动画剪辑的引用。References per animation clip.
        public readonly Dictionary<AnimationClip, ClipRefs> clipRefs = new Dictionary<AnimationClip, ClipRefs>();
        // 每个剪辑的动画器根（用于重写绑定时解析路径）。Animator root per clip (for resolving paths when rewriting bindings).
        public readonly Dictionary<AnimationClip, Transform> clipBase = new Dictionary<AnimationClip, Transform>();

        // 某槽位的某 float 属性是否被动画修改。Whether a float property of a slot is animated.
        public bool IsSlotPropAnimated(SlotEntry slot, string propertyName)
        {
            return slotFloatRanges.TryGetValue(slot, out var d) && d.ContainsKey(propertyName);
        }

        // 某槽位的某贴图属性是否被动画修改（贴图切换）。Whether a texture property of a slot is animated.
        public bool IsSlotTexturePropAnimated(SlotEntry slot, string propertyName)
        {
            return slotTexturePropsAnimated.TryGetValue(slot, out var s) && s.Contains(propertyName);
        }
    }
}
