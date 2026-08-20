// ============================================================================
// ATO public API - atlas packing
// ATO 公开 API - 图集装箱
//
// The default packer rasterizes each island at 4px granularity and places it
// with a full-scan bottom-left-first strategy (area desc, side desc, 90°
// rotation steps). Third parties may replace it; placements MUST respect the
// UV-group alignment constraints carried in the island data.
// 默认装箱器以 4px 粒度光栅化每个岛，并采用全扫描 BLF 策略（面积降序、边长降
// 序、90° 旋转步进）。第三方可替换装箱器；摆放必须遵守岛数据携带的 UV 组对齐
// 约束。
// ============================================================================

#region

using System.Collections.Generic;

#endregion

namespace net.fosa.AvatarTextureOptimizer.Api
{
    /// <summary>Simple 2D int vector (public API friendly). 简单 2D 整数向量。</summary>
    [System.Serializable]
    public struct ATOPixel2
    {
        public int X;
        public int Y;
        public ATOPixel2(int x, int y) { X = x; Y = y; }
    }

    /// <summary>One island to place. Raster mask at 4px granularity:
    /// <see cref="MaskBits"/> has ceil(width/4)*ceil(height/4) bits (row-major,
    /// LSB = leftmost), 1 = covered.
    /// 单个待放置岛。4px 粒度光栅掩码：行优先，LSB=最左，1=覆盖。</summary>
    public sealed class ATOPackIsland
    {
        /// <summary>Stable id (loggable). 稳定 id。</summary>
        public int Id;
        /// <summary>Raster width in pixels (already multiple of 4).
        /// 光栅宽度（已为 4 的倍数）。</summary>
        public int Width;
        /// <summary>Raster height in pixels (already multiple of 4).
        /// 光栅高度（已为 4 的倍数）。</summary>
        public int Height;
        /// <summary>Coverage mask. 覆盖掩码。</summary>
        public System.Numerics.BigInteger MaskBits;
        /// <summary>Covered cell count (raster cells of 4x4).
        /// 覆盖单元数（4x4 单元）。</summary>
        public int CellCount;
        /// <summary>True when a 90° rotation is allowed for this island.
        /// 是否允许 90° 旋转。</summary>
        public bool Rotatable;
        /// <summary>UV group id: islands with the same UV group placed in the
        /// SAME atlas must share an identical (scale,offset) mapping; the
        /// packer must place the group's anchor island and copy the transform
        /// for the rest. 0 = no constraint.
        /// UV 组 id：同一图集内同 UV 组岛必须共享同一 (scale,offset) 映射；0=无
        /// 约束。</summary>
        public int UVGroup;
        /// <summary>Source texture group id: all islands of the same texture
        /// MUST end up in the same atlas. 同源贴图组 id：同一贴图的所有岛必须
        /// 在同一图集。</summary>
        public int TextureGroup;
        /// <summary>For UV-grouped islands: required relative offset from the
        /// group anchor island (pixels). 相对组锚岛的偏移（像素）。</summary>
        public ATOPixel2 GroupOffset;
    }

    /// <summary>One placement result. 单个摆放结果。</summary>
    public struct ATOPackPlacement
    {
        public int IslandId;
        /// <summary>Top-left pixel in the atlas. 图集内左上角像素。</summary>
        public ATOPixel2 Pos;
        /// <summary>Applied rotation: 0 or 1 (90°). 旋转：0 或 1（90°）。</summary>
        public int Rot90;
    }

    /// <summary>One candidate atlas size. 单个候选图集尺寸。</summary>
    public struct ATOPackAtlasCandidate
    {
        public int Width;
        public int Height;
    }

    /// <summary>Replacement atlas packer. Implementations must be
    /// deterministic.
    /// 替换装箱器。实现必须确定。</summary>
    public interface IATOAtlasPacker
    {
        string Tag { get; }

        /// <summary>Packs islands into the smallest suitable candidate atlas.
        /// Returns null when no candidate fits (the caller falls back to
        /// no-atlas processing for that texture group).
        /// 将岛装入最合适的候选图集；无解返回 null（调用方对该贴图组回退到无
        /// 图集处理）。</summary>
        bool TryPack(
            IEnumerable<ATOPackIsland> islands,
            int paddingPx,
            IEnumerable<ATOPackAtlasCandidate> candidates,
            List<ATOPackPlacement> outPlacements,
            out ATOPackAtlasCandidate chosen);
    }
}
