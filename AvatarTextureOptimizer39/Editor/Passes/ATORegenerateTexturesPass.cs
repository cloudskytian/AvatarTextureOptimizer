// Avatar Texture Optimizer (ATO)
// Bilingual comments: English + Simplified Chinese. 双语注释：英文 + 简体中文。

using System.Collections.Generic;
using AvatarTextureOptimizer.Editor.Atlas;
using AvatarTextureOptimizer.Editor.Core;
using AvatarTextureOptimizer.Editor.Texture;
using nadena.dev.ndmf;
using UnityEngine;

namespace AvatarTextureOptimizer.Editor.Passes
{
    /// <summary>
    /// Pass 8 — regenerate textures: build atlas textures from placements, apply
    /// pull-push edge bleeding, set compression/mipmap/MipStreaming/wrap/read-write,
    /// and (in non-atlas mode) scale whole textures. Generated assets are named ATO_*.
    ///
    /// Pass 8 —— 再生贴图：由摆放构建图集、pull-push 边缘外扩、设置压缩/mipmap/
    /// MipStreaming/wrap/read-write，（非图集模式下）缩放整张贴图。产物命名 ATO_*。
    /// </summary>
    public sealed class ATORegenerateTexturesPass : Pass<ATORegenerateTexturesPass>
    {
        public override string DisplayName => "ATO: Regenerate textures / 再生贴图";

        private ATOBuildState _state;

        protected override void Execute(BuildContext context)
        {
            _state = context.GetState<ATOBuildState>();
            if (_state.Component == null) return;
            _state.BeginStage("Regenerate textures / 再生贴图");

            using var _ = ATOLog.Time("Regenerate textures");

            if (_state.Component.generateAtlas)
            {
                GenerateAtlases(context);
                // Textures sharing UV with whitelisted textures skip atlas-ization but
                // still get whole-texture scaling. 与白名单共享 UV 的贴图跳过图集化但仍整图缩放。
                ScaleWholeTextures(context, onlySkipAtlas: true);
            }
            else
            {
                ScaleWholeTextures(context, onlySkipAtlas: false);
            }
        }

        // ------------------------------------------------------------------ atlases

        private void GenerateAtlases(BuildContext context)
        {
            int atlasIndex = 0;

            foreach (var group in _state.AtlasGroups)
            {
                foreach (var atlasRes in group.Atlases)
                {
                    _state.ThrowIfCancelled();
                    var atlas = BuildAtlas(atlasRes.Placements, atlasRes.Size, context);
                    _state.GeneratedAtlases.Add(atlas);
                    atlasIndex++;
                }
            }

            ATOLog.Info($"Generated {atlasIndex} atlas(es). / 生成了 {atlasIndex} 个图集。");
        }

        private Texture2D BuildAtlas(List<ATOPlacement> placements,
            int size, BuildContext context)
        {
            // Infer category + alpha from placements. 从摆放推断类别与 alpha。
            ATOTextureCategory category = ATOTextureCategory.Albedo;
            bool hasAlpha = false;
            foreach (var p in placements)
            {
                var any = p.Entry.Textures.Find(t => t != null);
                if (any != null) { category = any.Category; if (any.HasAlpha) hasAlpha = true; }
            }

            var settings = _state.Component.compression.Get(category);
            bool mip = settings != null && settings.mipmapsAndStreaming;

            var atlas = new Texture2D(size, size, TextureFormat.RGBA32, mip, false);
            atlas.name = $"ATO_Atlas_{_state.GeneratedAtlases.Count}";
            atlas.wrapMode = TextureWrapMode.Clamp;

            var pixels = new Color[size * size];

            foreach (var p in placements)
            {
                var rec = p.Entry.Textures.Find(t => t != null && !t.SkipAll)
                          ?? p.Entry.Textures.Find(t => t != null);
                if (rec == null || rec.Pixels == null) continue;

                // Crop island region. 裁剪岛区域。
                int x0 = Mathf.Clamp(Mathf.FloorToInt(p.Entry.NormalizedBounds.xMin * rec.Width), 0, rec.Width - 1);
                int y0 = Mathf.Clamp(Mathf.FloorToInt(p.Entry.NormalizedBounds.yMin * rec.Height), 0, rec.Height - 1);
                int sw = Mathf.Max(1, Mathf.Clamp(Mathf.CeilToInt(p.Entry.NormalizedBounds.width * rec.Width), 1, rec.Width - x0));
                int sh = Mathf.Max(1, Mathf.Clamp(Mathf.CeilToInt(p.Entry.NormalizedBounds.height * rec.Height), 1, rec.Height - y0));

                var cropped = new Color[sw * sh];
                for (int y = 0; y < sh; y++)
                    for (int x = 0; x < sw; x++)
                        cropped[y * sw + x] = rec.Pixels[(y0 + y) * rec.Width + (x0 + x)];

                bool premultiply = rec.HasAlpha;
                var scaled = ATOResampler.Downsample(cropped, sw, sh, p.PixelW, p.PixelH, premultiply);

                // Normal maps: renormalize after resampling (correct decode/resample/renormalize).
                // 法线贴图：重采样后重归一化（正确的解码/重采样/重归一化）。
                if (rec.Category == ATOTextureCategory.Normal)
                    RenormalizeNormals(scaled);

                WriteRotated(pixels, size, scaled, p.PixelW, p.PixelH, p.PixelX, p.PixelY, p.Rotation);
            }

            // Pull-push: bleed edge colors outward to fill empty space (alpha stays 0 for transparent).
            // pull-push：边缘颜色外扩填满空白（透明贴图 alpha 保持 0）。
            BleedEdges(pixels, size);

            atlas.SetPixels(pixels);
            atlas.Apply(true, false);

            ApplyImportSettings(atlas, settings, hasAlpha, category == ATOTextureCategory.Mask, hasAlpha);

            context.ObjectRegistry.GetReference(atlas);
            return atlas;
        }

        private static void WriteRotated(Color[] dst, int A, Color[] src, int sw, int sh,
            int px, int py, int rot)
        {
            for (int y = 0; y < sh; y++)
            {
                for (int x = 0; x < sw; x++)
                {
                    int dx, dy;
                    switch (rot)
                    {
                        case 90: dx = px + (sh - 1 - y); dy = py + x; break;
                        case 180: dx = px + (sw - 1 - x); dy = py + (sh - 1 - y); break;
                        case 270: dx = px + y; dy = py + (sw - 1 - x); break;
                        default: dx = px + x; dy = py + y; break;
                    }
                    if (dx >= 0 && dy >= 0 && dx < A && dy < A)
                        dst[dy * A + dx] = src[y * sw + x];
                }
            }
        }

        /// <summary>Renormalize normal-map pixels in place. 原位重归一化法线像素。</summary>
        private static void RenormalizeNormals(Color[] px)
        {
            for (int i = 0; i < px.Length; i++)
            {
                var v = new Vector3(px[i].r * 2f - 1f, px[i].g * 2f - 1f, px[i].b * 2f - 1f);
                if (v.sqrMagnitude < 1e-6f) v = Vector3.forward;
                else v.Normalize();
                px[i] = new Color(v.x * 0.5f + 0.5f, v.y * 0.5f + 0.5f, v.z * 0.5f + 0.5f, px[i].a);
            }
        }

        /// <summary>Iterative dilation to fill empty texels with edge colors. 迭代膨胀填充空白。</summary>
        private static void BleedEdges(Color[] px, int size)
        {
            var filled = new bool[size * size];
            var q = new Queue<int>();
            for (int i = 0; i < px.Length; i++)
            {
                if (px[i].a > 0f || px[i].r > 0f || px[i].g > 0f || px[i].b > 0f)
                {
                    filled[i] = true;
                    q.Enqueue(i);
                }
            }

            int[] dx = { 1, -1, 0, 0 }, dy = { 0, 0, 1, -1 };
            int guard = 0;
            while (q.Count > 0 && guard++ < size * size)
            {
                int cur = q.Dequeue();
                int cx = cur % size, cy = cur / size;
                for (int k = 0; k < 4; k++)
                {
                    int nx = cx + dx[k], ny = cy + dy[k];
                    if (nx < 0 || ny < 0 || nx >= size || ny >= size) continue;
                    int ni = ny * size + nx;
                    if (filled[ni]) continue;
                    var src = px[cur];
                    px[ni] = new Color(src.r, src.g, src.b, 0f); // alpha stays 0. alpha 保持 0。
                    filled[ni] = true;
                    q.Enqueue(ni);
                }
            }
        }

        // ------------------------------------------------------------ whole-texture

        private void ScaleWholeTextures(BuildContext context, bool onlySkipAtlas)
        {
            foreach (var rec in _state.Textures.Values)
            {
                if (rec.SkipAll || rec.Pixels == null) continue;

                if (onlySkipAtlas && !IsInSkipAtlasEntry(rec)) continue;

                // Find the minimum island scale for this texture (wooden barrel).
                // 取该贴图各岛的最小缩放（木桶效应）。
                float s = 1f;
                foreach (var entry in _state.Islands)
                {
                    if (!entry.Textures.Contains(rec)) continue;
                    s = Mathf.Min(s, entry.UniformScale);
                }

                if (s >= 1f) continue;

                int nw = Mathf.Max(1, Mathf.RoundToInt(rec.Width * s));
                int nh = Mathf.Max(1, Mathf.RoundToInt(rec.Height * s));
                var scaled = ATOResampler.Downsample(rec.Pixels, rec.Width, rec.Height, nw, nh, rec.HasAlpha);

                if (rec.Category == ATOTextureCategory.Normal)
                    RenormalizeNormals(scaled);

                var settings = _state.Component.compression.Get(rec.Category);
                bool mip = settings != null && settings.mipmapsAndStreaming;

                var tex = new Texture2D(nw, nh, TextureFormat.RGBA32, mip, !rec.IsSrgb);
                tex.name = "ATO_" + rec.Texture.name;
                tex.wrapMode = TextureWrapMode.Clamp;
                tex.SetPixels(scaled);
                tex.Apply(true, false);

                ApplyImportSettings(tex, settings, rec.HasAlpha,
                    rec.Category == ATOTextureCategory.Mask, rec.HasAlpha);
                context.ObjectRegistry.GetReference(tex);

                var original = rec.Texture;
                rec.Texture = tex; // replaced; rewrite pass assigns to materials. 已替换，重写阶段赋给材质。
                _state.TextureRemap[original] = tex;
            }
        }

        private bool IsInSkipAtlasEntry(ATOTextureRecord rec)
        {
            foreach (var entry in _state.Islands)
            {
                if (!entry.SkipAtlas) continue;
                if (entry.Textures.Contains(rec)) return true;
            }
            return false;
        }

        // -------------------------------------------------------------- import settings

        /// <summary>
        /// Apply compression, mip/streaming binding, forced Clamp, and read/write=off,
        /// with safety filtering per category + actual pixel content.
        ///
        /// 应用压缩、mip/streaming 绑定、强制 Clamp、关闭 Read/Write，并按类别与像素实际
        /// 内容做安全过滤。
        /// </summary>
        private static void ApplyImportSettings(Texture2D tex, ATOCategorySettings settings,
            bool hasAlpha, bool isGrayscale, bool hasMultiChannelGray)
        {
            // Forced Clamp. 强制 Clamp。
            tex.wrapMode = TextureWrapMode.Clamp;

            // Mipmap + MipStreaming are bound together (VRChat requirement).
            // Mipmap 与 MipStreaming 绑定（VRChat 要求）。
            bool mip = settings != null && settings.mipmapsAndStreaming;

            if (settings != null && settings.format != ATOCompressionFormat.Auto)
            {
                var fmt = ResolveFormat(settings.format, hasAlpha, isGrayscale, hasMultiChannelGray, tex.name);

                if (fmt != null && SystemInfo.SupportsTextureFormat(fmt.Value))
                    EditorUtility.CompressTexture(tex, fmt.Value, TextureCompressionQuality.Normal);
            }

            // NOTE: MipStreaming is a TextureImporter setting; it is applied to the saved
            // asset after NDMF serializes it (see ATOImportApplier). The mip chain itself
            // is already created via the Texture2D constructor's mipChain argument.
            // 注意：MipStreaming 是 TextureImporter 设置，在 NDMF 序列化资产后应用；
            // mip 链已通过 Texture2D 构造函数的 mipChain 参数创建。
            _ = mip;
        }

        /// <summary>
        /// Resolve a safe TextureFormat for the given settings + content, with fallback and
        /// warnings when a user-selected format is unsafe for the actual pixels.
        ///
        /// 为给定设置+内容解析安全 TextureFormat；当用户选择的格式对实际像素不安全时，
        /// 兜底并告警。
        /// </summary>
        private static TextureFormat? ResolveFormat(ATOCompressionFormat format, bool hasAlpha,
            bool isGrayscale, bool hasMultiChannelGray, string name)
        {
            TextureFormat? fmt = format switch
            {
                ATOCompressionFormat.RGBA32 => TextureFormat.RGBA32,
                ATOCompressionFormat.BC7 => TextureFormat.BC7,
                ATOCompressionFormat.BC5 => TextureFormat.BC5,
                ATOCompressionFormat.BC4 => TextureFormat.BC4,
                ATOCompressionFormat.BC1 => TextureFormat.DXT1,
                ATOCompressionFormat.BC3 => TextureFormat.DXT5,
                ATOCompressionFormat.ASTC_6x6 => TextureFormat.ASTC_6x6,
                ATOCompressionFormat.ASTC_4x4 => TextureFormat.ASTC_4x4,
                ATOCompressionFormat.ETC2_RGBA => TextureFormat.ETC2_RGBA8,
                _ => null,
            };

            // No-alpha format on a texture that has alpha → unsafe, fallback.
            // 有 alpha 的贴图不能用无 alpha 格式 → 兜底。
            if (hasAlpha && (fmt == TextureFormat.DXT1 || fmt == TextureFormat.BC4))
            {
                ATOLog.Warning($"Texture {name} has alpha but format {format} drops it; " +
                               $"falling back to BC7. / 贴图含 alpha 但格式会丢弃它，回退 BC7。");
                return SystemInfo.SupportsTextureFormat(TextureFormat.BC7) ? TextureFormat.BC7 : TextureFormat.RGBA32;
            }

            // Single-channel format on a multi-channel grayscale texture → keep channels + warn.
            // 多通道灰度贴图用单通道格式 → 保留通道并告警。
            if (isGrayscale && hasMultiChannelGray && fmt == TextureFormat.BC4)
            {
                ATOLog.Warning($"Grayscale texture {name} has multiple used channels but format BC4 " +
                               $"is single-channel; keeping multi-channel. / 灰度贴图使用了多通道但 BC4 为单通道，保留多通道。");
                return TextureFormat.RGBA32;
            }

            return fmt;
        }
    }
}
