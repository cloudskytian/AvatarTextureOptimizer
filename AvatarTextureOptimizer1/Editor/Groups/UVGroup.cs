// UVGroup.cs / UVGroup.cs
// A UV group is a set of UV islands across materials/textures that SHARE THE SAME UV COORDINATES
// on a mesh (e.g. main color + normal + mask all using UV0). All islands in the same UV group must
// occupy the SAME rectangle position in every atlas of their texture-type group, so that the different
// textures sample the same UV location across layers.
// UV组是指在一个网格上共享相同UV坐标的一组UV岛（例如主色+法线+蒙版都使用UV0）。
// 同一UV组内所有岛在其贴图类型组的每一个图集里必须占据相同的矩形位置，
// 这样不同贴图在跨图层采样时UV位置一致，不会出错。

using System.Collections.Generic;
using net.fosa.avatar_texture_optimizer.Editor.Core;
using UnityEngine;

namespace net.fosa.avatar_texture_optimizer.Editor.Groups
{
    /// <summary>
    /// Represents one UV coordinate (mesh+channel+triangle-set) and all the texture islands that map onto it.
    /// 表示一个UV坐标（网格+通道+三角面集合）以及所有映射到其上的贴图岛。
    /// </summary>
    public class UVGroup
    {
        /// <summary>Stable identifier / 稳定标识符</summary>
        public int Id;
        /// <summary>The UV islands (one per referenced texture layer on this UV set) / UV岛（此UV集上每个被引用的贴图层一个）</summary>
        public List<UVIsland> Islands = new();
        /// <summary>Combined source-UV bounding box (union across all islands) / 合并的源UV包围盒（所有岛的并集）</summary>
        public Rect SourceBounds;
        /// <summary>Combined whitelist status: if any island is whitelisted, same-UV other islands skip atlasization / 合并白名单状态：任一岛白名单则同UV其他岛跳过图集化</summary>
        public bool PartiallyWhitelisted;
        /// <summary>Fully whitelisted (no atlas, no per-island scale — whole texture scaling only) / 完全白名单（不图集、不逐岛缩放——仅整图缩放）</summary>
        public bool FullyWhitelisted;
        /// <summary>Minimum pixel density required by any island in the group / 组内任一岛要求的最小像素密度</summary>
        public float MinRequiredDensity;
        /// <summary>Maximum pixel density allowed by original source / 原始源允许的最大像素密度</summary>
        public float MaxAllowedDensity;
        /// <summary>Union of all usage flags (baseColor/normal/etc.) for this UV group / 本UV组的所有用途标记并集（主色/法线等）</summary>
        public TextureUsageFlags UsageFlags;
        /// <summary>Target pixel rectangle in atlas (same rect across all atlases in the type group) / 图集中的目标像素矩形（类型组中所有图集相同）</summary>
        public RectInt TargetPixelRect;
        /// <summary>Whether rotated 90° / 是否旋转90度</summary>
        public bool Rotated;
        /// <summary>Final target scale (uniform, then anisotropic) / 最终目标缩放（先均匀后各向异性）</summary>
        public Vector2 FinalScale = Vector2.one;
        /// <summary>True if all islands in the group are solid-color short-circuit / 是否组内所有岛都是纯色短路</summary>
        public bool IsSolidColor;

        /// <summary>Texture type groups this UV group participates in (one per texture "layer") / 本UV组参与的贴图类型组（每个贴图层一个）</summary>
        public List<TextureTypeGroup> TypeGroups = new();

        /// <summary>Worst-case quality target across all islands in the group / 组内所有岛最差情况的质量目标</summary>
        public QualityTarget EffectiveQuality = new();
    }

    /// <summary>
    /// Aggregated quality target for a UV group (worst case across all layers and material usages).
    /// UV组的聚合质量目标（所有层和材质用途的最差情况）。
    /// </summary>
    public class QualityTarget
    {
        public float MsSSIM = 0.98f;
        public float DeltaE = 2f;
        public float NormalAngleDeg = 3f;
        public float AlphaRMSE = 0.02f;
        public float CutoutIoU = 0.99f;
        public float GrayscaleRMSE = 0.04f;
        public bool IsNearLossless;
    }
}
