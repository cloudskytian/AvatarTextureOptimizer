// WholeTextureScaler.cs / WholeTextureScaler.cs
// Scales entire textures (non-atlas mode) according to density limits and quality targets.
// Also provides fallback scaling for whitelisted/skipped islands: keeps UVs intact but scales the whole texture.
// 根据密度限制和质量目标缩放整个贴图（非图集模式）。也为白名单/跳过的岛提供回退缩放：保持UV不变但整体缩放贴图。

using System.Collections.Generic;
using UnityEngine;
using net.fosa.avatar_texture_optimizer.Editor.Core;
using net.fosa.avatar_texture_optimizer.Editor.Util;

namespace net.fosa.avatar_texture_optimizer.Editor.Processing
{
    public static class WholeTextureScaler
    {
        /// <summary>
        /// In non-atlas mode: generate scaled versions of all unique source textures
        /// according to worst-case pixel density and quality requirements across their used islands.
        /// 在非图集模式下：根据所使用岛的最差像素密度和质量要求生成所有唯一源贴图的缩放版本。
        /// Returns old->new texture map.
        /// 返回旧->新贴图映射。
        /// </summary>
        public static Dictionary<Texture2D, Texture2D> ScaleWholeTextures(AvatarAnalysisResult analysis, bool generateAtlas)
        {
            var result = new Dictionary<Texture2D, Texture2D>();
            if (generateAtlas) return result;

            // Determine the largest required pixel size per texture across all non-whitelisted islands
            // 对每个贴图确定跨所有非白名单岛的最大所需像素尺寸
            var texMaxSize = ComputeMaxSizePerTexture(analysis, onlyWhitelisted: false);
            return ApplyScaling(texMaxSize);
        }

        /// <summary>
        /// In atlas mode: scale textures that belong to whitelisted or skipped (non-atlasized) islands.
        /// These still benefit from whole-texture quality scaling and import optimization.
        /// 在图集模式下：缩放属于白名单或跳过（未图集化）岛的贴图。这些贴图仍可从整图质量缩放和导入优化中受益。
        /// </summary>
        public static Dictionary<Texture2D, Texture2D> ScaleNonAtlasTextures(AvatarAnalysisResult analysis)
        {
            // For whitelisted/partially-whitelisted groups: scale their source textures
            // 对白名单/部分白名单组：缩放其源贴图
            var texMaxSize = new Dictionary<Texture2D, int>();

            // Collect all unique textures that are NOT already covered by an atlas
            // 收集所有未被图集覆盖的唯一贴图
            var atlasTextures = new HashSet<Texture2D>();
            foreach (var g in analysis.UvGroups)
            {
                if (g.FullyWhitelisted && !g.PartiallyWhitelisted) continue; // fully-WL: skip scaling
                if (g.FullyWhitelisted && g.PartiallyWhitelisted)
                {
                    // Partially whitelisted: scale non-WL islands' textures whole-texture
                    // 部分白名单：将非WL岛的贴图整图缩放
                    foreach (var isl in g.Islands)
                    {
                        if (isl.IsWhitelisted || isl.SourceTexture == null) continue;
                        int targetSide = Mathf.Max(isl.ScaledPixelSize.x, isl.ScaledPixelSize.y);
                        int origSide = Mathf.Max(isl.OriginalPixelSize.x, isl.OriginalPixelSize.y);
                        if (origSide <= 0) continue;
                        float scale = Mathf.Min(1f, (float)targetSide / Mathf.Max(1, origSide));
                        int wholeTarget = Mathf.RoundToInt(Mathf.Max(isl.SourceTexture.width, isl.SourceTexture.height) * scale);
                        wholeTarget = Mathf.Clamp(NextMultipleOf4(Mathf.Max(4, wholeTarget)), 4, 8192);
                        if (!texMaxSize.TryGetValue(isl.SourceTexture, out int cur) || wholeTarget > cur)
                            texMaxSize[isl.SourceTexture] = wholeTarget;
                    }
                }
            }

            // Also collect whitelisted textures' islands that still need import-optimization only (no downscale)
            // 也收集白名单贴图中仅需要导入优化（不下采样）的岛
            foreach (var island in analysis.Islands)
            {
                if (island.SourceTexture == null) continue;
                if (!island.IsWhitelisted) continue;
                // Whitelisted textures keep their original size; don't downscale.
                // 白名单贴图保持原始尺寸；不下采样。
                if (!texMaxSize.ContainsKey(island.SourceTexture))
                    texMaxSize[island.SourceTexture] = Mathf.Max(island.SourceTexture.width, island.SourceTexture.height);
            }

            return ApplyScaling(texMaxSize);
        }

        private static Dictionary<Texture2D, int> ComputeMaxSizePerTexture(AvatarAnalysisResult analysis, bool onlyWhitelisted)
        {
            var texMaxSize = new Dictionary<Texture2D, int>();
            foreach (var island in analysis.Islands)
            {
                if (island.SourceTexture == null) continue;
                if (onlyWhitelisted && !island.IsWhitelisted) continue;
                if (!onlyWhitelisted && island.IsWhitelisted)
                {
                    // Whitelisted textures keep original / 白名单贴图保持原始
                    if (!texMaxSize.ContainsKey(island.SourceTexture))
                        texMaxSize[island.SourceTexture] = Mathf.Max(island.SourceTexture.width, island.SourceTexture.height);
                    continue;
                }
                int targetSide = Mathf.Max(island.ScaledPixelSize.x, island.ScaledPixelSize.y);
                int origSide = Mathf.Max(island.OriginalPixelSize.x, island.OriginalPixelSize.y);
                if (origSide <= 0) continue;
                float scale = Mathf.Min(1f, (float)targetSide / Mathf.Max(1, origSide));
                int wholeTarget = Mathf.RoundToInt(Mathf.Max(island.SourceTexture.width, island.SourceTexture.height) * scale);
                wholeTarget = Mathf.Clamp(NextMultipleOf4(Mathf.Max(4, wholeTarget)), 4, 8192);
                if (!texMaxSize.TryGetValue(island.SourceTexture, out int cur) || wholeTarget > cur)
                    texMaxSize[island.SourceTexture] = wholeTarget;
            }
            return texMaxSize;
        }

        private static Dictionary<Texture2D, Texture2D> ApplyScaling(Dictionary<Texture2D, int> texMaxSize)
        {
            var result = new Dictionary<Texture2D, Texture2D>();
            foreach (var kv in texMaxSize)
            {
                var src = kv.Key;
                int max = kv.Value;
                if (max >= Mathf.Max(src.width, src.height)) { result[src] = src; continue; }
                var rt = GPUUtility.GetRT(max, max);
                var ok = GPUUtility.BlitScaled(src, rt, premultiplyAlpha: false);
                var scaled = new Texture2D(max, max, TextureFormat.RGBA32, src.mipmapCount > 1, src.isDataSRGB);
                if (ok)
                {
                    var prev = RenderTexture.active;
                    RenderTexture.active = rt;
                    scaled.ReadPixels(new Rect(0,0,max,max), 0, 0);
                    scaled.Apply(src.mipmapCount > 1, false);
                    RenderTexture.active = prev;
                }
                else
                {
                    // CPU fallback / CPU回退
                    var px = src.GetPixels();
                    var resized = new Color[max*max];
                    float factor = (float)src.width / max;
                    for (int y = 0; y < max; y++) for (int x = 0; x < max; x++)
                    {
                        int sx = Mathf.Clamp(Mathf.FloorToInt((x+0.5f) * factor), 0, src.width-1);
                        int sy = Mathf.Clamp(Mathf.FloorToInt((y+0.5f) * factor), 0, src.height-1);
                        resized[y*max+x] = px[sy*src.width+sx];
                    }
                    scaled.SetPixels(resized);
                    scaled.Apply(src.mipmapCount > 1, false);
                }
                scaled.name = "ATO_scaled_" + src.name;
                scaled.wrapMode = TextureWrapMode.Clamp;
                scaled.filterMode = FilterMode.Bilinear;
                scaled.anisoLevel = 1;
                GPUUtility.ReleaseRT(rt);
                result[src] = scaled;
            }
            return result;
        }

        private static int NextMultipleOf4(int n)
        {
            return (n + 3) & ~3;
        }
    }
}
