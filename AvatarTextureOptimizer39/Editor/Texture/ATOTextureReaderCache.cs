// Avatar Texture Optimizer (ATO)
// Bilingual comments: English + Simplified Chinese. 双语注释：英文 + 简体中文。

using AvatarTextureOptimizer.Editor.Core;
using UnityEngine;

namespace AvatarTextureOptimizer.Editor.Texture
{
    /// <summary>
    /// Cache helper ensuring a texture record exists in the build state, reusing pixel
    /// reads so each texture is decoded at most once.
    ///
    /// 缓存辅助：确保构建状态中存在贴图记录，复用像素读取，每张贴图最多解码一次。
    /// </summary>
    public static class ATOTextureReaderCache
    {
        public static ATOTextureRecord Ensure(ATOBuildState state, Texture2D tex)
        {
            if (tex == null) return null;
            if (state.Textures.TryGetValue(tex, out var existing)) return existing;

            var rec = ATOTextureReader.Read(tex);
            if (rec == null)
            {
                state.SkippedTextures.Add(tex);
                return null;
            }

            state.Textures[tex] = rec;
            return rec;
        }
    }
}
