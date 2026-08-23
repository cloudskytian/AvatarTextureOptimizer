using System.Collections.Generic;
using UnityEngine;

namespace Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// Whole-texture scaling for textures that must NOT be atlased (whitelist same-UV groups,
    /// oversized atoms, or the "generate atlas off" mode). The entire image is treated as one
    /// region and binary-searched with the same quality metrics. / 整图缩放：用于不参与图集化的贴图
    /// （白名单同UV组、超限单体、或关闭图集模式）。整图作为单一区域按相同质量算法二分缩放。
    /// </summary>
    internal class WholeTextureOptimizer
    {
        private readonly QualityEvaluator _evaluator;
        private readonly AtoSettings _settings;
        private readonly AtoPlatform _platform;

        /// <summary>texture → optimized replacement. / 贴图 → 优化后替换。</summary>
        internal readonly Dictionary<Texture2D, Texture2D> Replacements = new Dictionary<Texture2D, Texture2D>();

        internal WholeTextureOptimizer(QualityEvaluator evaluator, AtoSettings settings, AtoPlatform platform)
        {
            _evaluator = evaluator;
            _settings = settings;
            _platform = platform;
        }

        /// <summary>
        /// Optimize one texture. `category` is the strictest role across usages; alphaCandidates
        /// the union across referencing materials. / 优化单张贴图（类别取最严格用途，透明组合取并集）。
        /// </summary>
        internal void Optimize(Texture2D tex, TexCategory category, bool srgb,
            IReadOnlyCollection<(AlphaMode, float)> alphaCandidates, TextureStore store)
        {
            if (Replacements.ContainsKey(tex)) return;
            var q = _settings.quality;
            if (q.IsNearLossless)
            {
                // near-lossless: keep pixels; only compression/mip settings applied at export
                // 近无损：不动像素，仅压缩/导流参数在导出时应用
                Replacements[tex] = tex;
                return;
            }

            var pixels = store.GetPixels(tex);
            bool hasAlpha = false;
            for (int i = 0; i < pixels.Length; i += 13)
                if (pixels[i].a < 250) { hasAlpha = true; break; }

            int w = tex.width, h = tex.height;
            float lo = 0.25f, hi = 1f;
            bool hasAlphaUsage = hasAlpha && QualityEvaluator.UsesAlpha(alphaCandidates);

            bool Pass(float s)
            {
                int dw = Mathf.Max(1, Mathf.RoundToInt(w * s));
                int dh = Mathf.Max(1, Mathf.RoundToInt(h * s));
                if (dw == w && dh == h) return true;
                var scaled = _evaluator.Downsample(pixels, w, h, dw, dh, category, srgb);
                var test = _evaluator.Upsample(scaled, dw, dh, w, h, category, hasAlphaUsage);
                return _evaluator.Evaluate(category, srgb, pixels, test, w, h, alphaCandidates, q,
                    hasAlpha, out _);
            }

            float scale = hi;
            if (Pass(lo))
            {
                for (int it = 0; it < 6; it++)
                {
                    float mid = 0.5f * (lo + hi);
                    if (mid <= lo || mid >= hi) break;
                    if (Pass(mid)) { scale = mid; lo = mid; }
                    else hi = mid;
                }
                // note: `lo` tracks the passing side / lo 始终为达标侧
                scale = lo;
            }
            else
            {
                ATOLog.Verbose($"whole-texture '{tex.name}': quality floor unreachable, kept ×{lo}");
                scale = lo;
            }

            int fw = Mathf.Max(1, Mathf.RoundToInt(w * scale));
            int fh = Mathf.Max(1, Mathf.RoundToInt(h * scale));

            if (fw == w && fh == h)
            {
                Replacements[tex] = tex;
                return;
            }

            var bytes = _evaluator.MakeAtlasBytes(pixels, w, h, fw, fh, category, srgb);
            var formatCat = category switch
            {
                TexCategory.Normal => TextureCategory.Normal,
                TexCategory.Color => hasAlpha ? TextureCategory.Transparent : TextureCategory.Opaque,
                _ => TextureCategory.Grayscale,
            };
            var format = TextureFormats.Resolve(UserFormat(formatCat), formatCat, _platform, hasAlpha,
                false, false, out _);
            var name = "ATO_T_" + tex.name;
            var newTex = TextureFormats.BuildTexture(name, fw, fh, bytes, format,
                formatCat == TextureCategory.Opaque || formatCat == TextureCategory.Transparent,
                MipOn(formatCat), _platform);
            Replacements[tex] = newTex;
            ATOLog.Info($"whole-texture '{tex.name}': {w}x{h} → {fw}x{fh} {newTex.format}");
        }

        private AtoFormat UserFormat(TextureCategory c) => c switch
        {
            TextureCategory.Opaque => _settings.opaqueFormat,
            TextureCategory.Transparent => _settings.transparentFormat,
            TextureCategory.Normal => _settings.normalFormat,
            _ => _settings.grayscaleFormat,
        };

        private bool MipOn(TextureCategory c) => c switch
        {
            TextureCategory.Opaque => _settings.opaqueMip,
            TextureCategory.Transparent => _settings.transparentMip,
            TextureCategory.Normal => _settings.normalMip,
            _ => _settings.grayscaleMip,
        };
    }
}
