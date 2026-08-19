using System;
using System.Collections.Generic;
using UnityEngine;
using NetFosa.AvatarTextureOptimizer;
using NetFosa.AvatarTextureOptimizer.Editor.UV;

namespace NetFosa.AvatarTextureOptimizer.Editor.Analysis
{
    /// <summary>
    /// 单张贴图的资产级信息（导入设置 + 像素内容派生属性）。
    /// </summary>
    public sealed class TextureInfo
    {
        public Texture texture;
        public ATOColorSpace colorSpace;
        public ATOFilterMode filterMode;
        public bool hasAlpha;
        public bool isGrayscale;
        public bool isUniformColor = false; // 由质检阶段按岛判定，这里仅占位

        public bool whitelisted;
        public ATOWhitelistLevel whitelistLevel = ATOWhitelistLevel.Normal;

        /// <summary>贴图类型组（解析后填充）。</summary>
        public TextureTypeGroup typeGroup;

        /// <summary>压缩类别。</summary>
        public ATOTextureCategory category = ATOTextureCategory.MainOpaque;

        public readonly List<TextureUsage> usages = new List<TextureUsage>();

        /// <summary>动画切换并入的贴图（并入原贴图所在类型组）。</summary>
        public bool isAnimationSwap;

        /// <summary>去重后保留的实例（该贴图被合并到了 dedupOf）。</summary>
        public TextureInfo dedupTarget;

        /// <summary>动画切换并入的原始贴图（并入其所在类型组）。</summary>
        public TextureInfo swapTarget;

        /// <summary>该贴图原始路径（调试用）。</summary>
        public string debugPath;

        /// <summary>完整 whitelistLevel（Full=跳过一切 / NoAtlas=跳过图集化但仍整图缩放+导入优化 / Normal）。
        /// 枚举数值越小越严重（Full=0 &lt; NoAtlas=1 &lt; Normal=2），因此取 usages 中的最小值。</summary>
        public ATOWhitelistLevel EffectiveWhitelistLevel
        {
            get
            {
                var lvl = whitelistLevel;
                foreach (var u in usages)
                {
                    if ((int)u.whitelistLevel < (int)lvl) lvl = u.whitelistLevel; // 取最严重
                }
                return lvl;
            }
        }
    }

    /// <summary>贴图被某材质以某属性引用的一次使用记录。</summary>
    public sealed class TextureUsage
    {
        public TextureInfo info;
        public Material material;
        public string propertyName;
        public int propertyId;
        /// <summary>网格 UV 通道（0..7）；-1 表示非 UV 采样（应白名单）。</summary>
        public int uvChannel = -1;
        public ATOUsageKind kind;
        public RenderMode renderMode = RenderMode.Opaque;
        public float cutoff = 0.5f;
        public bool anyTransparent;
        public bool anyCutout;
        public float minCutoff = float.MaxValue;
        public float maxCutoff = float.MinValue;
        public bool hasSTTransform;
        public bool specialUse;
        public bool animatedProperties; // 材质属性被动画修改
        public ATOWhitelistLevel whitelistLevel = ATOWhitelistLevel.Normal;
        public string whitelistReason;
    }

    /// <summary>一个 UV 组内的单张贴图（含在组内的质量需求集合）。</summary>
    public sealed class UvGroupTexture
    {
        public TextureInfo info;
        public readonly List<MetricRequirement> requirements = new List<MetricRequirement>();
        /// <summary>该贴图是否直接参与本组 UV 采样（false=动画切换备用）。</summary>
        public bool active;
    }

    /// <summary>一个质量需求（贴图种类 × 渲染模式 × cutoff）。</summary>
    public struct MetricRequirement
    {
        public ATOUsageKind kind;
        public RenderMode mode;
        public float cutoff;
    }

    /// <summary>
    /// UV 组：同一 (renderer, 材质槽, UV 通道) 上同一 UV 区域的全部贴图（含动画切换）。
    /// 组内所有贴图在各自图集里必须使用完全相同的 rect。
    /// </summary>
    public sealed class UvGroup
    {
        public int id;
        public Renderer renderer;
        public int slotIndex;
        public int uvChannel;
        public Mesh mesh;
        public readonly List<UvGroupTexture> textures = new List<UvGroupTexture>();
        /// <summary>提取后的 UV 岛（UV 层填充）。</summary>
        public List<UvIsland> islands;
        public bool failed;
        public string failReason;
        /// <summary>是否因某种原因需要"整图缩放"而非图集化。</summary>
        public bool noAtlas;
    }

    /// <summary>
    /// 贴图类型组：决定哪些贴图合入同一份图集（含伴随法线/蒙版标志、色彩空间、filterMode）。
    /// </summary>
    public sealed class TextureTypeGroup
    {
        public int id;
        public ATOUsageKind baseKind;
        public ATOColorSpace colorSpace;
        public ATOFilterMode filterMode;
        public bool hasNormalCompanion;
        public bool hasMaskCompanion;
        public string key;
        public readonly List<TextureInfo> textures = new List<TextureInfo>();
        public bool IsNearLossless => false; // 由 QualityConfigResolver 判断

        public string DisplayKey => $"{baseKind}|{colorSpace}|{filterMode}|N={hasNormalCompanion}|M={hasMaskCompanion}";
    }

    /// <summary>
    /// 一次材质槽分析快照（扫描器输出）。
    /// </summary>
    public sealed class SlotSnapshot
    {
        public Renderer renderer;
        public int slotIndex;
        public Material material;
        public int triangleStart;
        public int triangleCount;
    }
}
