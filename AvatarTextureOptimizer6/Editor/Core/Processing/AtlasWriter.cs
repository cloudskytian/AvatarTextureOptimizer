using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using NetFosa.AvatarTextureOptimizer.Editor.Analysis;
using NetFosa.AvatarTextureOptimizer.Editor.Atlas;
using NetFosa.AvatarTextureOptimizer.Editor.Logging;
using NetFosa.AvatarTextureOptimizer.Editor.Utils;
using UnityEngine;
using NetFosa.AvatarTextureOptimizer;
using NetFosa.AvatarTextureOptimizer.Editor.Quality;

namespace NetFosa.AvatarTextureOptimizer.Editor.Processing
{
    /// <summary>
    /// 图集写出器：把装箱结果用 GPU（RenderTexture）批量写入图集，
    /// 岛内容线性空间重采样（透明预乘 alpha），空白区 GPU pull-push 无限外扩填充，
    /// 最后读回并创建 Texture2D（线性值；sRGB 图集在写回时编码）。
    /// </summary>
    public sealed class AtlasWriter : IDisposable
    {
        private readonly RenderTexturePool _pool;
        private readonly bool _useGpu;
        private readonly ATOLogger _logger;
        private readonly Texture2D _whiteTex;

        private Material _resampleMat;
        private Material _pushDownMat;
        private Material _pullUpMat;

        public AtlasWriter(RenderTexturePool pool, bool useGpu, ATOLogger logger)
        {
            _pool = pool;
            _useGpu = useGpu;
            _logger = logger;
            _whiteTex = new Texture2D(1, 1, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
            _whiteTex.SetPixel(0, 0, Color.white);
            _whiteTex.Apply();
        }

        private Material ResampleMat => _resampleMat != null ? _resampleMat : (_resampleMat = new Material(Shader.Find("ATO/Resample")));
        private Material PushDownMat => _pushDownMat != null ? _pushDownMat : (_pushDownMat = new Material(Shader.Find("ATO/PullPush")));
        private Material PullUpMat => _pullUpMat != null ? _pullUpMat : (_pullUpMat = new Material(Shader.Find("ATO/PullPush")));

        /// <summary>
        /// 生成一张图集 Texture2D（尚未保存为资产）。
        /// </summary>
        public Texture2D WriteAtlas(AtlasResult atlas, Dictionary<UvIsland, TextureInfo> islandTextures, TextureCache cache)
        {
            int W = atlas.width, H = atlas.height;
            bool srgb = atlas.colorSpace == ATOColorSpace.SRGB;

            // 透明判定：类别为透明 或 任一来源贴图含 alpha
            bool hasAlpha = atlas.category == ATOTextureCategory.MainTransparent;
            if (!hasAlpha)
            {
                foreach (var kv in islandTextures)
                {
                    if (kv.Value.hasAlpha) { hasAlpha = true; break; }
                }
            }

            var atlasRt = _pool.Get(W, H, RenderTextureFormat.ARGB32, true); // 线性
            var coverageRt = _pool.Get(W, H, RenderTextureFormat.ARGB32, true);

            RenderTexture prev = RenderTexture.active;

            var resample = ResampleMat;
            if (resample == null || resample.shader == null)
            {
                _logger.Error("ATO/Resample shader not found; falling back to CPU atlas assembly.");
                RenderTexture.active = prev;
                return WriteAtlasCpu(atlas, islandTextures, cache, srgb, hasAlpha);
            }

            Graphics.SetRenderTarget(atlasRt);
            GL.Clear(true, true, new Color(0, 0, 0, 0));
            Graphics.SetRenderTarget(coverageRt);
            GL.Clear(true, true, Color.black);

            // ---- 1) 绘制各岛内容 ----
            foreach (var p in atlas.placements)
            {
                if (!islandTextures.TryGetValue(p.island, out var info)) continue;
                var tex = info.texture as Texture2D;
                if (tex == null) continue;
                int texW = tex.width, texH = tex.height;

                var bounds = p.island.uvBounds;
                int sx = Mathf.Clamp(Mathf.RoundToInt(bounds.x * texW), 0, texW - 1);
                int sy = Mathf.Clamp(Mathf.RoundToInt(bounds.y * texH), 0, texH - 1);
                int sw = Mathf.Clamp(Mathf.RoundToInt(bounds.width * texW), 1, texW - sx);
                int sh = Mathf.Clamp(Mathf.RoundToInt(bounds.height * texH), 1, texH - sy);

                // 内容矩形（像素）。旋转 90° 时 UV 空间旋转：X 向跨度 = rectV×W，Y 向跨度 = rectU×H
                // （与 MeshUvRewriter 的旋转映射一致；仅正方形图集允许旋转）
                int cw = Mathf.Max(1, Mathf.RoundToInt(p.island.atlasRect.width * W));
                int ch = Mathf.Max(1, Mathf.RoundToInt(p.island.atlasRect.height * H));
                int px = p.cellX * 4;
                int py = p.cellY * 4;
                int dw = p.rotated ? Mathf.Max(1, Mathf.RoundToInt(p.island.atlasRect.height * W)) : cw;
                int dh = p.rotated ? Mathf.Max(1, Mathf.RoundToInt(p.island.atlasRect.width * H)) : ch;

                // 源区域（贴图 UV 空间）
                float srcU0 = (float)sx / texW;
                float srcV0 = (float)sy / texH;
                float srcWU = (float)sw / texW;
                float srcVH = (float)sh / texH;

                resample.SetTexture("_MainTex", tex);
                resample.SetVector("_SrcScale", new Vector4(srcWU, srcVH, 0, 0));
                resample.SetVector("_SrcBias", new Vector4(srcU0, srcV0, 0, 0));
                resample.SetVector("_DestScale", new Vector4((float)dw / W, (float)dh / H, 0, 0));
                resample.SetVector("_DestOffset", new Vector4((float)px / W * 2f - 1f + (float)dw / W,
                    (float)py / H * 2f - 1f + (float)dh / H, 0, 0));
                resample.SetFloat("_Premultiply", hasAlpha ? 1f : 0f);
                resample.SetFloat("_OutputGamma", 0f);
                resample.SetFloat("_SrcRotate", p.rotated ? 1f : 0f);
                resample.SetFloat("_SourceSRGB", SourceSRGB(info.colorSpace == ATOColorSpace.SRGB));

                Graphics.Blit(tex, atlasRt, resample);

                // ---- 2) 覆盖掩码（内容矩形，供 pull-push） ----
                resample.SetTexture("_MainTex", _whiteTex);
                resample.SetVector("_SrcScale", Vector4.one);
                resample.SetVector("_SrcBias", Vector4.zero);
                resample.SetVector("_DestScale", new Vector4((float)dw / W, (float)dh / H, 0, 0));
                resample.SetVector("_DestOffset", new Vector4((float)px / W * 2f - 1f + (float)dw / W,
                    (float)py / H * 2f - 1f + (float)dh / H, 0, 0));
                resample.SetFloat("_Premultiply", 0f);
                resample.SetFloat("_OutputGamma", 0f);
                resample.SetFloat("_SrcRotate", p.rotated ? 1f : 0f);
                resample.SetFloat("_SourceSRGB", 0f);
                Graphics.Blit(_whiteTex, coverageRt, resample);
            }

            // ---- 3) pull-push ----
            RenderTexture result = PullPush(atlasRt, coverageRt, W, H, hasAlpha);

            // ---- 4) 读回 ----
            var tex2d = new Texture2D(W, H, TextureFormat.RGBA32, false, true);
            RenderTexture.active = result;
            tex2d.ReadPixels(new Rect(0, 0, W, H), 0, 0);
            tex2d.Apply(false, false);
            RenderTexture.active = prev;

            // sRGB 图集：线性 → sRGB 编码
            if (srgb)
            {
                var px = tex2d.GetPixels32();
                Parallel.For(0, px.Length, i =>
                {
                    px[i].r = (byte)Mathf.RoundToInt(Mathf.Clamp01(Utils.ColorSpace.LinearToSrgb(px[i].r / 255f)) * 255f);
                    px[i].g = (byte)Mathf.RoundToInt(Mathf.Clamp01(Utils.ColorSpace.LinearToSrgb(px[i].g / 255f)) * 255f);
                    px[i].b = (byte)Mathf.RoundToInt(Mathf.Clamp01(Utils.ColorSpace.LinearToSrgb(px[i].b / 255f)) * 255f);
                });
                var converted = new Texture2D(W, H, TextureFormat.RGBA32, false);
                converted.SetPixels32(px);
                converted.Apply(false, false);
                UnityEngine.Object.DestroyImmediate(tex2d);
                tex2d = converted;
            }

            _pool.Release(atlasRt);
            _pool.Release(coverageRt);
            if (result != atlasRt) _pool.Release(result);

            return tex2d;
        }

        private RenderTexture PullPush(RenderTexture content, RenderTexture coverage, int W, int H, bool hasAlpha)
        {
            var push = PushDownMat;
            var pull = PullUpMat;
            if (push == null || push.shader == null || pull == null || pull.shader == null)
            {
                _logger.Warn("ATO/PullPush shader missing; skipping fill (blank atlas areas stay empty).");
                return content;
            }

            int levels = 0;
            int w = W, h = H;
            while (w > 1 && h > 1 && levels < 14)
            {
                w = Math.Max(1, w / 2);
                h = Math.Max(1, h / 2);
                levels++;
            }

            var levelRT = new RenderTexture[levels + 1];
            var covRT = new RenderTexture[levels + 1];
            levelRT[0] = content;
            covRT[0] = coverage;

            // push
            for (int k = 0; k < levels; k++)
            {
                int nw = Math.Max(1, levelRT[k].width / 2);
                int nh = Math.Max(1, levelRT[k].height / 2);
                var l = _pool.Get(nw, nh, RenderTextureFormat.ARGB32, true);
                var c = _pool.Get(nw, nh, RenderTextureFormat.ARGB32, true);

                push.SetTexture("_MainTex", levelRT[k]);
                push.SetTexture("_CoverageTex", covRT[k]);
                Graphics.Blit(levelRT[k], l, push, 0);

                // 覆盖降采样（bilinear 平均）
                var resample = ResampleMat;
                if (resample == null || resample.shader == null)
                {
                    _pool.Release(l);
                    _pool.Release(c);
                    _logger.Warn("ATO/Resample shader missing; skipping pull-push pyramid.");
                    return content;
                }
                resample.SetTexture("_MainTex", covRT[k]);
                resample.SetVector("_SrcScale", Vector4.one);
                resample.SetVector("_SrcBias", Vector4.zero);
                resample.SetVector("_DestScale", Vector4.one);
                resample.SetVector("_DestOffset", Vector4.zero);
                resample.SetFloat("_Premultiply", 0f);
                resample.SetFloat("_OutputGamma", 0f);
                resample.SetFloat("_SrcRotate", 0f);
                resample.SetFloat("_SourceSRGB", 0f);
                Graphics.Blit(covRT[k], c, resample);

                levelRT[k + 1] = l;
                covRT[k + 1] = c;
            }

            // pull（从粗到细）
            var result = levelRT[levels];
            for (int k = levels - 1; k >= 0; k--)
            {
                var outRt = _pool.Get(levelRT[k].width, levelRT[k].height, RenderTextureFormat.ARGB32, true);
                pull.SetTexture("_MainTex", levelRT[k]);
                pull.SetTexture("_CoverageTex", covRT[k]);
                pull.SetTexture("_CoarseTex", result);
                pull.SetFloat("_Transparent", hasAlpha ? 1f : 0f);
                Graphics.Blit(result, outRt, pull, 1);
                if (k < levels - 1) _pool.Release(result);
                if (k != 0 && levelRT[k] != content) _pool.Release(levelRT[k]);
                _pool.Release(covRT[k]);
                result = outRt;
            }

            return result;
        }

        // ---------------- CPU 兜底 ----------------
        private Texture2D WriteAtlasCpu(AtlasResult atlas, Dictionary<UvIsland, TextureInfo> islandTextures,
            TextureCache cache, bool srgb, bool hasAlpha)
        {
            int W = atlas.width, H = atlas.height;
            var data = new Color32[W * H];
            foreach (var p in atlas.placements)
            {
                if (!islandTextures.TryGetValue(p.island, out var info)) continue;
                var tex = info.texture as Texture2D;
                if (tex == null) continue;
                var bounds = p.island.uvBounds;
                int sx = Mathf.Clamp(Mathf.RoundToInt(bounds.x * tex.width), 0, tex.width - 1);
                int sy = Mathf.Clamp(Mathf.RoundToInt(bounds.y * tex.height), 0, tex.height - 1);
                int sw = Mathf.Clamp(Mathf.RoundToInt(bounds.width * tex.width), 1, tex.width - sx);
                int sh = Mathf.Clamp(Mathf.RoundToInt(bounds.height * tex.height), 1, tex.height - sy);
                int cw = Mathf.Max(1, Mathf.RoundToInt(p.island.atlasRect.width * W));
                int ch = Mathf.Max(1, Mathf.RoundToInt(p.island.atlasRect.height * H));
                int px = p.cellX * 4, py = p.cellY * 4;
                int dw = p.rotated ? ch : cw;
                int dh = p.rotated ? cw : ch;

                var srcPx = cache.GetPixels(tex, out _, out _);
                var src = ImageOps.ExtractRegionLinear(srcPx, tex.width, tex.height, sx, sy, sw, sh,
                    info.colorSpace == ATOColorSpace.SRGB);
                var crop = ImageOps.DownscaleWithAlpha(src, sw, sh, dw, dh, hasAlpha);

                for (int y = 0; y < dh; y++)
                {
                    for (int x = 0; x < dw; x++)
                    {
                        int srcIdx = (y * dw + x) * 4;
                        float r = crop[srcIdx], g = crop[srcIdx + 1], b = crop[srcIdx + 2], a = crop[srcIdx + 3];
                        if (srgb)
                        {
                            r = Utils.ColorSpace.LinearToSrgb(r);
                            g = Utils.ColorSpace.LinearToSrgb(g);
                            b = Utils.ColorSpace.LinearToSrgb(b);
                        }
                        int dx = p.rotated ? (px + (dh - 1 - y)) : (px + x);
                        int dy = p.rotated ? (py + x) : (py + y);
                        if (dx >= 0 && dx < W && dy >= 0 && dy < H)
                        {
                            data[dy * W + dx] = new Color32(
                                (byte)Mathf.Clamp(Mathf.RoundToInt(r * 255f), 0, 255),
                                (byte)Mathf.Clamp(Mathf.RoundToInt(g * 255f), 0, 255),
                                (byte)Mathf.Clamp(Mathf.RoundToInt(b * 255f), 0, 255),
                                (byte)Mathf.Clamp(Mathf.RoundToInt(a * 255f), 0, 255));
                        }
                    }
                }
            }
            var result = new Texture2D(W, H, TextureFormat.RGBA32, false, true);
            result.SetPixels32(data);
            result.Apply(false, false);
            return result;
        }

        /// <summary>源 sRGB 且在 Gamma 工程下 → 需手动转线性（保证线性空间处理的确定性）。</summary>
        private static float SourceSRGB(bool sourceSrgb)
        {
            bool gammaProject;
            try
            {
                gammaProject = UnityEditor.PlayerSettings.colorSpace == ColorSpace.Gamma;
            }
            catch (Exception)
            {
                gammaProject = false;
            }
            return (gammaProject && sourceSrgb) ? 1f : 0f;
        }

        /// <summary>
        /// GPU 整图缩放：源贴图 → 目标尺寸 RT（线性重采样，透明预乘），读回 Texture2D。
        /// 返回 null 表示 GPU 路径不可用（调用方回退 CPU）。
        /// </summary>
        public Texture2D ScaleWholeTextureGpu(Texture2D src, int dstW, int dstH, bool srgb, bool hasAlpha)
        {
            var resample = ResampleMat;
            if (resample == null || resample.shader == null) return null;

            var rt = _pool.Get(dstW, dstH, RenderTextureFormat.ARGB32, true);
            var prev = RenderTexture.active;

            resample.SetTexture("_MainTex", src);
            resample.SetVector("_SrcScale", Vector4.one);
            resample.SetVector("_SrcBias", Vector4.zero);
            resample.SetVector("_DestScale", Vector4.one);
            resample.SetVector("_DestOffset", Vector4.zero);
            resample.SetFloat("_Premultiply", hasAlpha ? 1f : 0f);
            resample.SetFloat("_OutputGamma", 0f);
            resample.SetFloat("_SrcRotate", 0f);
            resample.SetFloat("_SourceSRGB", SourceSRGB(srgb));
            Graphics.Blit(src, rt, resample);

            var tex2d = new Texture2D(dstW, dstH, TextureFormat.RGBA32, false, true);
            RenderTexture.active = rt;
            tex2d.ReadPixels(new Rect(0, 0, dstW, dstH), 0, 0);
            tex2d.Apply(false, false);
            RenderTexture.active = prev;
            _pool.Release(rt);

            if (srgb)
            {
                var px = tex2d.GetPixels32();
                System.Threading.Tasks.Parallel.For(0, px.Length, i =>
                {
                    px[i].r = (byte)Mathf.RoundToInt(Mathf.Clamp01(Utils.ColorSpace.LinearToSrgb(px[i].r / 255f)) * 255f);
                    px[i].g = (byte)Mathf.RoundToInt(Mathf.Clamp01(Utils.ColorSpace.LinearToSrgb(px[i].g / 255f)) * 255f);
                    px[i].b = (byte)Mathf.RoundToInt(Mathf.Clamp01(Utils.ColorSpace.LinearToSrgb(px[i].b / 255f)) * 255f);
                });
                var converted = new Texture2D(dstW, dstH, TextureFormat.RGBA32, false);
                converted.SetPixels32(px);
                converted.Apply(false, false);
                UnityEngine.Object.DestroyImmediate(tex2d);
                tex2d = converted;
            }
            return tex2d;
        }

        public void Dispose()
        {
            if (_resampleMat != null) UnityEngine.Object.DestroyImmediate(_resampleMat);
            if (_pushDownMat != null) UnityEngine.Object.DestroyImmediate(_pushDownMat);
            if (_pullUpMat != null) UnityEngine.Object.DestroyImmediate(_pullUpMat);
            if (_whiteTex != null) UnityEngine.Object.DestroyImmediate(_whiteTex);
        }
    }
}
