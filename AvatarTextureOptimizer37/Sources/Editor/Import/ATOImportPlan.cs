// ============================================================================
// ATO - import plan (computed stage 5, applied stage 7)
// ATO - 导入计划（阶段5计算，阶段7应用）
// ============================================================================

#region

using UnityEngine;

#endregion

namespace net.fosa.AvatarTextureOptimizer.Editor.Import
{
    public sealed class ATOImportPlan
    {
        public Texture2D Texture;
        public ATOTextureCategory Category;
        public bool HasAlpha;
        /// <summary>Resolved compression format (safe fallback applied).
        /// 解析后的压缩格式（已安全回退）。</summary>
        public TextureImporterFormat Format;
        public bool Mipmaps;
        public bool NpotAllowed;
        /// <summary>True when the user's choice was unsafe and fell back.
        /// 用户选择不安全已回退。</summary>
        public bool FallbackUsed;
        public string FallbackReason;
    }
}
