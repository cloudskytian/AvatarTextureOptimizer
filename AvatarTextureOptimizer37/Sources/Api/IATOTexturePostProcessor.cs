// ============================================================================
// ATO public API - texture post-processing hook
// ATO 公开 API - 贴图后处理钩子
//
// Invoked AFTER atlas pages / scaled textures are composed but BEFORE the
// Texture2D is saved to the asset database. Implementations MAY modify
// pixels or metadata (e.g. extra dithering, palette quantization). Mutations
// must never change size or color space.
// 在图集页/缩放贴图合成之后、保存到资产数据库之前调用。实现可修改像素或元数
// 据（如额外抖动、调色板量化），但绝不允许改变尺寸或颜色空间。
// ============================================================================

#region

using UnityEngine;

#endregion

namespace net.fosa.AvatarTextureOptimizer.Api
{
    public interface IATOTexturePostProcessor
    {
        string Tag { get; }

        /// <summary>Process one composed texture in place.
        /// 就地处理一张已合成的贴图。</summary>
        /// <param name="texture">Composed texture. 已合成贴图。</param>
        /// <param name="category">Texture category. 贴图类别。</param>
        /// <param name="isAtlasPage">True for generated atlas pages.
        /// 是否为生成的图集页。</param>
        void Process(Texture2D texture, ATOTextureCategory category, bool isAtlasPage);
    }
}
