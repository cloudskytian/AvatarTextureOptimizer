using System.Collections.Generic;
using UnityEngine;

namespace Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>Texture semantic category. / 贴图语义类别。</summary>
    internal enum TexCategory
    {
        /// <summary>sRGB color (main / emission ...). / sRGB 颜色（主色、自发光等）。</summary>
        Color = 0,
        /// <summary>Tangent-space normal map. / 切线空间法线。</summary>
        Normal = 1,
        /// <summary>Packed linear RGBA mask. / 打包线性 RGBA 蒙版。</summary>
        Mask = 2,
        /// <summary>Grayscale with a known used-channel set. / 已知使用通道的灰度图。</summary>
        Grayscale = 3,
        /// <summary>Linear color data (HDR-ish / data). / 线性颜色数据。</summary>
        LinearColor = 4,
    }

    /// <summary>Blend mode of a material as seen by the alpha metric. / 透明模式下 alpha 度量视角。</summary>
    internal enum AlphaMode
    {
        Opaque = 0,
        Cutout = 1,
        Blend = 2,
    }

    /// <summary>One analyzed texture slot of a material. / 材质上一个贴图槽位的分析结果。</summary>
    internal class TexSlot
    {
        internal string property;
        internal Texture2D texture;
        internal TexCategory category = TexCategory.Color;
        /// <summary>Mesh UV channel 0..3; -1 = non-mesh UV (matcap etc.) → whitelist. / 网格UV通道；-1=非网格UV（白名单）。</summary>
        internal int uvChannel;
        /// <summary>Safe for atlasing (no ST/scroll/rotation/decal...). / 可安全图集化（无变换/贴花等）。</summary>
        internal bool safe = true;
        internal string unsafeReason;
    }

    /// <summary>Per-material analysis result. / 单个材质的分析结果。</summary>
    internal class MaterialAnalysis
    {
        internal Material material;
        internal bool isLilToon;
        /// <summary>Could not be fully understood; all its textures get whitelisted with a warning. / 无法完全理解：其全部贴图白名单+警告。</summary>
        internal bool unknown;
        internal string unknownReason;
        internal readonly List<TexSlot> slots = new List<TexSlot>();

        // ---- alpha facts / 透明度事实（可能被动画改严，扫描器会合并） ----
        internal AlphaMode alphaMode = AlphaMode.Opaque;
        internal float cutoff = 0.5f;
        /// <summary>All (mode, cutoff) combos seen for this material incl. animation keyframes. / 含动画帧在内的全部组合。</summary>
        internal readonly HashSet<(AlphaMode, float)> alphaCandidates = new HashSet<(AlphaMode, float)>();
    }
}
