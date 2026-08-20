// Avatar Texture Optimizer (ATO)
// Bilingual comments: English + Simplified Chinese. 双语注释：英文 + 简体中文。

using UnityEngine;

namespace AvatarTextureOptimizer.Editor.Core
{
    /// <summary>
    /// Memory-management helpers. Texture pixel caches are large (a 4096² RGBA32 texture is
    /// 64 MB raw); releasing them at the right pipeline boundaries keeps peak memory
    /// bounded without hurting correctness (each cache has a defined lifetime).
    ///
    /// 内存管理辅助。贴图像素缓存很大（4096² RGBA32 约 64 MB 原始数据）；在正确的流水线
    /// 边界释放可限制内存峰值，同时不影响正确性（每个缓存有明确生命周期）。
    /// </summary>
    public static class ATOMemory
    {
        /// <summary>
        /// Release the raw (Color32) pixel cache — only needed for dedup hashing and alpha
        /// detection, both of which finish before island scaling.
        ///
        /// 释放原始（Color32）像素缓存 —— 仅去重哈希与 alpha 检测需要，二者均在岛缩放前完成。
        /// </summary>
        public static void ReleaseRawPixels(ATOBuildState state)
        {
            if (state == null) return;
            foreach (var rec in state.Textures.Values)
                rec.Pixels32 = null;
        }

        /// <summary>
        /// Release the linear (Color) pixel cache — needed until atlas generation finishes.
        ///
        /// 释放线性（Color）像素缓存 —— 图集生成完成后不再需要。
        /// </summary>
        public static void ReleaseLinearPixels(ATOBuildState state)
        {
            if (state == null) return;
            foreach (var rec in state.Textures.Values)
                rec.Pixels = null;
        }

        /// <summary>Force a GC pass (safe in edit mode). 强制执行一次 GC（编辑器模式安全）。</summary>
        public static void Collect()
        {
            System.GC.Collect();
            System.GC.WaitForPendingFinalizers();
            Resources.UnloadUnusedAssets();
        }
    }
}
