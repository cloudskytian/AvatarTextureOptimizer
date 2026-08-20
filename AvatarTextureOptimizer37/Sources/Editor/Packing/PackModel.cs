// ============================================================================
// ATO - packing data model
// ATO - 装箱数据模型
//
// Layout model 布局模型：
//   Each UV group has a layout scale (Kx, Ky) = atlas pixels per UV unit,
//   computed in the quality stage as the barrel-effect minimum over all
//   group members (each member must stay within its own quality-passing
//   pixel budget). On an atlas page, every island's rect is derived from
//   its (normalized) UV bbox * K, so all pages of all type groups sharing
//   a UV group map the same UV to the same normalized position.
//   每个 UV 组有布局比例 (Kx, Ky) = 每 UV 单位的图集中像素数，由质量阶段
//   按组内所有成员的木桶效应最小值计算（每个成员都须在自己的质量通过像素
//   预算内）。在图集页上，每个岛的矩形由其（归一化）UV 包围盒 * K 导出，
//   因此共享 UV 组的所有类型组图集页把同一 UV 映射到相同的归一化位置。
// ============================================================================

#region

using System.Collections.Generic;
using UnityEngine;

#endregion

namespace net.fosa.AvatarTextureOptimizer.Editor.Packing
{
    public sealed class ATOPackedPage
    {
        public int TypeGroupId;
        public int W, H;
        /// <summary>Placed items (each covers one or more UV groups).
        /// 已放置条目（每条覆盖一个或多个 UV 组）。</summary>
        public readonly List<ATOPackedItem> Items = new();
        /// <summary>Used raster area (px^2, at 4px granularity * 16).
        /// 已用光栅面积。</summary>
        public long UsedArea;
        public int IslandCount;
        /// <summary>Composed texture (stage 4). 合成贴图（阶段4）。</summary>
        public Texture2D Texture;
        /// <summary>True when any alpha < 1 exists (drives format choice).
        /// 存在 alpha<1（决定格式选择）。</summary>
        public bool HasAlpha;
        /// <summary>Mirror role pages (normal/mask/emission) for main pages.
        /// 主图页的镜像角色页（法线/蒙版/自发光）。</summary>
        public readonly Dictionary<Api.ATOTextureRole, ATOPackedPage> MirrorRoles = new();
        /// <summary>-1 for main pages; ATOTextureRole value for mirrors.
        /// 主图页为 -1；镜像页为角色值。</summary>
        public int IsMirrorRole = -1;

        public float Utilization
        {
            get
            {
                long total = (long) W * H;
                return total == 0 ? 0f : UsedArea / (float) total;
            }
        }
    }

    public sealed class ATOPackedItem
    {
        /// <summary>UV groups covered by this item. 本条目覆盖的 UV 组。</summary>
        public readonly List<ATOUVGroup> UVGroups = new();
        /// <summary>Texture ids of all islands in this item (co-location
        /// constraint). 本条目全部岛涉及的贴图（同页约束）。</summary>
        public readonly List<int> TextureIds = new();
        /// <summary>Item placement on the page. 条目的页内位置。</summary>
        public int X, Y;
        public int Rot90;
        /// <summary>Per-UV-group local offset within the item (px).
        /// 各 UV 组在条目内的本地偏移。</summary>
        public readonly List<(ATOUVGroup Group, int LX, int LY)> SubItems = new();
        /// <summary>Item raster (4px grid) for BLF. 条目光栅（4px 网格）。</summary>
        public System.Numerics.BigInteger Mask;
        public int MaskW, MaskH; // in px (multiple of 4) 像素（4 的倍数）
        public int AreaCells;    // covered cells  覆盖单元数
    }

    /// <summary>Aggregate packing output. 装箱输出汇总。</summary>
    public sealed class ATOPackResult
    {
        public readonly List<ATOPackedPage> Pages = new();
        /// <summary>(uvGroup, textureId) pairs that could not be atlased
        /// (single texture doesn't fit even the largest atlas).
        /// 无法图集化（单贴图装不进最大图集）的 (UV组, 贴图) 对。</summary>
        public readonly List<(int uvGroup, int tex)> Abandoned = new();
    }
}
