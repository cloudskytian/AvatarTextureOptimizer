// Avatar Texture Optimizer (ATO)
// Whole-texture scaling for the no-atlas mode and for UV-mates of whitelisted textures.
// Resizes textures in place (references stay valid) using the same quality gate.
// 无图集模式与白名单贴图同 UV 贴图的整图缩放。原地缩放（引用保持有效），沿用同一质量门控。

using UnityEngine;

namespace NetFosa.ATO
{
    /// <summary>
    /// Resizes whole textures in place. / 原地整图缩放。
    /// </summary>
    public static class ATODirectResizer
    {
        public static void Resize(ATOBuildContext build, ATOProgress progress)
        {
            var thr = ATOQualityModel.Resolve(build);
            if (ATOQualityModel.IsLossless(thr)) return; // nothing to do / 无损则无需处理

            var targets = new System.Collections.Generic.List<ATOTextureRef>();
            foreach (var t in build.textures)
                if (t.wholeTextureScale && !t.skipAllOptimization && t.texture != null)
                    targets.Add(t);

            progress.Begin(targets.Count);
            foreach (var tr in targets)
            {
                ResizeOne(build, tr, thr);
                progress.Advance(1, tr.texture.name);
                progress.ThrowIfCancelled();
            }
        }

        private static void ResizeOne(ATOBuildContext build, ATOTextureRef tr, ATOQualityThresholds thr)
        {
            var source = tr.texture;
            var readable = ATOUtil.EnsureReadable(source); // needed for GetPixels / GetPixels 需要可读
            int w = source.width, h = source.height;
            var original = readable.GetPixels();

            // Clone before resizing so the user's asset is never mutated. / 缩放前先克隆，绝不修改用户资产。
            if (!source.name.EndsWith("_ato"))
            {
                var clone = ATOUtil.CloneTexture(source);
                clone.name = source.name + "_ato";
                tr.texture = clone;
                foreach (var u in tr.usages)
                    if (u.material != null && u.material.HasProperty(u.propertyName))
                        u.material.SetTexture(u.propertyName, clone);
                build.animRemap.textureRemap[source] = clone;
            }
            var tex = tr.texture;

            float best = 1f;
            float lo = 0.25f, hi = 1f;
            for (int i = 0; i < 10; i++)
            {
                float mid = (lo + hi) * 0.5f;
                int nw = Mathf.Max(4, Mathf.RoundToInt(w * mid));
                int nh = Mathf.Max(4, Mathf.RoundToInt(h * mid));
                if (Passes(tr, original, w, h, nw, nh, thr)) { best = mid; hi = mid; }
                else lo = mid;
            }

            if (best >= 0.999f) return; // no reduction / 无需缩小

            int tw = Mathf.Max(4, Mathf.RoundToInt(w * best));
            int th = Mathf.Max(4, Mathf.RoundToInt(h * best));
            var small = new Color[tw * th];
            bool usedGpu = false;
            // GPU fast path for exact halving (RenderTexture + ComputeShader). / 精确减半时走 GPU 快速路径。
            if (tr.hasAlpha && tw * 2 == w && th * 2 == h)
                usedGpu = ATOGpu.PremultipliedDownsample2x(tex, out small);
            if (!usedGpu)
            {
                if (tr.hasAlpha) ATOTextureSampler.PremultipliedDownsample(original, w, h, small, tw, th);
                else { for (int y = 0; y < th; y++) for (int x = 0; x < tw; x++) small[y * tw + x] = original[Mathf.Clamp(y * h / th, 0, h - 1) * w + Mathf.Clamp(x * w / tw, 0, w - 1)]; }
            }

            tex.Reinitialize(tw, th, tex.format, tex.mipmapCount > 1);
            tex.SetPixels(small);
            tex.Apply(tex.mipmapCount > 1);
            tr.wholeScale = best;
            ATOLogger.Info($"Whole-texture scaled '{tex.name}': {w}x{h} -> {tw}x{th} ({(1f - best) * 100f:F1}% smaller).");
        }

        private static bool Passes(ATOTextureRef tr, Color[] original, int w, int h, int nw, int nh, ATOQualityThresholds thr)
        {
            var small = new Color[nw * nh];
            if (tr.hasAlpha) ATOTextureSampler.PremultipliedDownsample(original, w, h, small, nw, nh);
            else { for (int y = 0; y < nh; y++) for (int x = 0; x < nw; x++) small[y * nw + x] = original[Mathf.Clamp(y * h / nh, 0, h - 1) * w + Mathf.Clamp(x * w / nw, 0, w - 1)]; }
            var up = new Color[w * h];
            ATOTextureSampler.BilinearUpsample(small, nw, nh, up, w, h);
            var r = ATOQualityEvaluator.Evaluate(tr, original, up, null, w, h, Mathf.Min(w, h), thr);
            return r != null && r.pass;
        }
    }
}
