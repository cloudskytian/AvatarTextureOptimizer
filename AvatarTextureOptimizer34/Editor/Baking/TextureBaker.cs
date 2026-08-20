// AvatarTextureOptimizer - TextureBaker
// EN: Bakes packed atlases into RenderTextures (blit island regions with rotation & resampling), runs pull-push
// edge extension, then reads back to Texture2D assets.
// CN: 把装箱结果烘焙为 RenderTexture（按旋转与重采样 blit 岛区域），执行 pull-push 边缘外扩，再读回 Texture2D 资产。
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace net.fosa.avatar_texture_optimizer
{
    public static class TextureBaker
    {
        /// <summary>
        /// EN: Loads the blit material (ATOBlit). Returns null if missing (caller falls back to plain blits).
        /// CN: 加载 blit 材质（ATOBlit）。缺失时返回 null（调用方退化为普通 blit）。
        /// </summary>
        public static Material FindBlitMaterial()
        {
            var shader = Shader.Find("Hidden/ATO/Blit");
            if (shader == null) return null;
            return new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
        }

        /// <summary>EN: Bakes one atlas into a Texture2D (linear/sRGB per usage). / CN: 把一个图集烘焙为 Texture2D。</summary>
        public static Texture2D BakeAtlas(AtoBuildState state, PackedAtlas atlas, RenderTexturePool pool,
            Material blitMat, bool useGpuPullPush, System.Action<string> progress)
        {
            bool srgb = atlas.usage == TextureUsage.Albedo;
            var fmt = srgb ? RenderTextureFormat.ARGB32 : RenderTextureFormat.ARGB32;
            var rt = pool.Get(atlas.width, atlas.height, fmt, 0, srgb);
            RenderTexture prev = RenderTexture.active;
            Graphics.SetRenderTarget(rt);
            GL.Clear(true, true, new Color(0, 0, 0, 0));
            RenderTexture.active = prev;

            // EN: Copy each island content into the atlas.
            // CN: 拷贝每个岛的内容进图集。
            var decodedCache = new Dictionary<Texture2D, Texture2D>();
            foreach (var pi in atlas.islands)
            {
                if (state.Cancelled) break;
                var src = pi.tex.texture;
                if (src == null) continue;
                var decoded = decodedCache.TryGetValue(src, out var d) ? d : (decodedCache[src] = state.Decoder.Decode(src));
                if (decoded == null) continue;

                // EN: Source pixel rect (island frac rect in the original texture).
                // CN: 源像素矩形（岛 frac 矩形 × 原贴图尺寸）。
                var srcRect = new Rect(
                    pi.island.fracRect.x * src.width,
                    pi.island.fracRect.y * src.height,
                    Mathf.Max(1f, pi.island.fracRect.width * src.width),
                    Mathf.Max(1f, pi.island.fracRect.height * src.height));

                int destW = Mathf.Max(1, Mathf.RoundToInt(pi.rect.width));
                int destH = Mathf.Max(1, Mathf.RoundToInt(pi.rect.height));
                CopyRegion(state, decoded, srcRect, rt, pi.rect, destW, destH, pi.rotation,
                    pi.tex.usage, pi.tex.HasAlphaRequirement, blitMat, pool);
            }

            // EN: Pull-push / dilation to fill empty atlas space (alpha stays 0 for transparent atlases).
            // CN: Pull-push / 扩张填充图集空白区域（透明图集 alpha 保持 0）。
            if (!state.Cancelled)
            {
                bool transparent = atlas.usage == TextureUsage.Albedo &&
                                   HasAnyAlpha(atlas);
                PullPush.Execute(state, rt, atlas, pool, blitMat, useGpuPullPush, transparent);
            }

            // EN: Readback to CPU texture.
            // CN: 读回 CPU 贴图。
            RenderTexture.active = rt;
            var tex = new Texture2D(atlas.width, atlas.height, TextureFormat.RGBA32, false, srgb);
            tex.ReadPixels(new Rect(0, 0, atlas.width, atlas.height), 0, 0);
            tex.Apply();
            RenderTexture.active = prev;
            pool.Release(rt);
            foreach (var kv in decodedCache) { } // decoded textures come from the shared decoder cache; do not destroy
            return tex;
        }

        /// <summary>EN: True when any island texture of the atlas has alpha requirements. / CN: 图集中任一岛贴图有 alpha 需求时为真。</summary>
        public static bool HasAnyAlpha(PackedAtlas atlas)
        {
            foreach (var pi in atlas.islands)
                if (pi.tex.HasAlphaRequirement) return true;
            return false;
        }

        /// <summary>
        /// EN: Blits a source region into a destination rect of the atlas RT (rotation via shader UV mapping).
        /// CN: 把源区域 blit 到图集 RT 的目标矩形（旋转经着色器 UV 映射实现）。
        /// </summary>
        private static void CopyRegion(AtoBuildState state, Texture2D src, Rect srcRect, RenderTexture dst,
            Rect dstRect, int destW, int destH, int rotation, TextureUsage usage, bool hasAlpha,
            Material blitMat, RenderTexturePool pool)
        {
            RenderTexture prev = RenderTexture.active;
            RenderTexture.active = dst;
            GL.Viewport(new Rect(dstRect.x, dstRect.y, dstRect.width, dstRect.height));

            if (blitMat != null)
            {
                blitMat.SetTexture("_MainTex", src);
                blitMat.SetVector("_SrcRect",
                    new Vector4(srcRect.x / src.width, srcRect.y / src.height,
                        srcRect.width / src.width, srcRect.height / src.height));
                blitMat.SetVector("_DestSize", new Vector2(destW, destH));
                blitMat.SetFloat("_Rotate", rotation);
                int pass = usage == TextureUsage.Normal ? 2 : (hasAlpha ? 1 : 0);
                Graphics.Blit(src, blitMat, pass);
            }
            else
            {
                // EN: Fallback: direct blit without resampling control (still rotated via texGen below is not
                // possible without a shader; fallback ignores rotation — flagged in VERIFY.md).
                // CN: 回退：无重采样控制的直接 blit（无着色器时无法旋转——见 VERIFY.md）。
                Graphics.Blit(src, dst);
            }
            RenderTexture.active = prev;
        }
    }
}
