using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using Net.Fosa.AvatarTextureOptimizer;

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// Scales islands (or whole textures) with binary search until the worst metric of the UV group passes.
    /// 用二分搜索缩放岛（或整图），直到 UV 组最差指标全部达标。
    /// Uniform first, then anisotropic. GPU resample + Burst metrics.
    /// 先均匀缩放，再各向异性。GPU 重采样 + Burst 指标。
    /// </summary>
    public static class AtoQualityEval
    {
        public static void ScaleIslands(AtoContext ctx, AtoUvGroup group)
        {
            var q = ctx.Settings.quality;
            var near = ctx.Settings.qualityPreset == AtoQualityPreset.NearLossless || q.IsNearLossless;
            foreach (var isl in group.Islands)
            {
                var pixW = Mathf.Max(1, Mathf.CeilToInt(isl.UvRect.width * isl.OrigW));
                var pixH = Mathf.Max(1, Mathf.CeilToInt(isl.UvRect.height * isl.OrigH));
                isl.TargetW = pixW;
                isl.TargetH = pixH;
                if (near)
                {
                    AtoLog.VerboseInfo($"Skip scale (near lossless) island {isl.Id} {pixW}x{pixH}");
                    continue;
                }

                var density = PixelDensity(isl);
                var minD = (int)ctx.Settings.minDensity;
                var maxD = (int)ctx.Settings.maxDensity;
                var maxByDensity = density <= 1e-6f ? 1f : maxD / density;
                var minByDensity = density <= 1e-6f ? 1f : minD / density;
                // Clamp by original island size. / 受原岛物理像素钳制。
                var maxS = Mathf.Min(1f, maxByDensity);
                var minS = Mathf.Clamp(minByDensity, 0.02f, maxS);

                if (isl.SolidColor)
                {
                    var side = Mathf.Min(4, Mathf.Min(pixW, pixH));
                    isl.TargetW = Mathf.Max(1, side);
                    isl.TargetH = Mathf.Max(1, side);
                    continue;
                }

                // Uniform binary search. / 均匀二分。
                var lo = minS; var hi = maxS; var best = maxS;
                for (var i = 0; i < 8; i++)
                {
                    var mid = (lo + hi) * 0.5f;
                    if (PassAll(ctx, group, isl, mid, mid))
                    {
                        best = mid; hi = mid;
                    }
                    else lo = mid;
                }
                var sx = best; var sy = best;
                // Anisotropic refine. / 各向异性细化。
                lo = minS; hi = sx;
                for (var i = 0; i < 6; i++)
                {
                    var mid = (lo + hi) * 0.5f;
                    if (PassAll(ctx, group, isl, mid, sy)) { sx = mid; hi = mid; }
                    else lo = mid;
                }
                lo = minS; hi = sy;
                for (var i = 0; i < 6; i++)
                {
                    var mid = (lo + hi) * 0.5f;
                    if (PassAll(ctx, group, isl, sx, mid)) { sy = mid; hi = mid; }
                    else lo = mid;
                }

                isl.TargetW = Mathf.Clamp(Mathf.RoundToInt(pixW * sx), 1, pixW);
                isl.TargetH = Mathf.Clamp(Mathf.RoundToInt(pixH * sy), 1, pixH);
            }

            // Barrel: UV group takes max size, not larger than original max.
            // 木桶：UV 组取最大尺寸，且不超过原最大。
            foreach (var isl in group.Islands)
            {
                var maxW = 1; var maxH = 1;
                foreach (var u in group.Textures)
                {
                    if (u.Texture == null) continue;
                    maxW = Mathf.Max(maxW, Mathf.CeilToInt(isl.UvRect.width * u.Texture.width));
                    maxH = Mathf.Max(maxH, Mathf.CeilToInt(isl.UvRect.height * u.Texture.height));
                }
                isl.TargetW = Mathf.Min(isl.TargetW, maxW);
                isl.TargetH = Mathf.Min(isl.TargetH, maxH);
            }
        }

        public static void ScaleWholeTexture(AtoContext ctx, Texture2D tex, List<AtoTextureUse> uses, out int w, out int h)
        {
            w = tex.width; h = tex.height;
            var q = ctx.Settings.quality;
            if (ctx.Settings.qualityPreset == AtoQualityPreset.NearLossless || q.IsNearLossless)
                return;

            if (IsSolid(ctx, tex, out _))
            {
                w = Mathf.Min(4, tex.width);
                h = Mathf.Min(4, tex.height);
                return;
            }

            var lo = 0.05f; var hi = 1f; var best = 1f;
            for (var i = 0; i < 8; i++)
            {
                var mid = (lo + hi) * 0.5f;
                var tw = Mathf.Max(1, Mathf.RoundToInt(tex.width * mid));
                var th = Mathf.Max(1, Mathf.RoundToInt(tex.height * mid));
                if (PassTexture(ctx, tex, uses, tw, th))
                {
                    best = mid; hi = mid;
                }
                else lo = mid;
            }
            w = Mathf.Max(1, Mathf.RoundToInt(tex.width * best));
            h = Mathf.Max(1, Mathf.RoundToInt(tex.height * best));
        }

        private static float PixelDensity(AtoIsland isl)
        {
            var pix = Mathf.Max(1f, isl.UvRect.width * isl.OrigW) * Mathf.Max(1f, isl.UvRect.height * isl.OrigH);
            var world = Mathf.Max(1e-8f, isl.WorldArea);
            return Mathf.Sqrt(pix / world);
        }

        private static bool PassAll(AtoContext ctx, AtoUvGroup group, AtoIsland isl, float sx, float sy)
        {
            foreach (var use in group.Textures)
            {
                if (use.Texture == null || use.Whitelisted) continue;
                if (!PassIsland(ctx, use, isl, sx, sy)) return false;
            }
            return true;
        }

        private static bool PassIsland(AtoContext ctx, AtoTextureUse use, AtoIsland isl, float sx, float sy)
        {
            var tex = use.Texture;
            var srcW = Mathf.Max(1, Mathf.RoundToInt(isl.UvRect.width * tex.width * sx));
            var srcH = Mathf.Max(1, Mathf.RoundToInt(isl.UvRect.height * tex.height * sy));
            var origW = Mathf.Max(1, Mathf.RoundToInt(isl.UvRect.width * tex.width));
            var origH = Mathf.Max(1, Mathf.RoundToInt(isl.UvRect.height * tex.height));
            return PassCrop(ctx, use, isl.UvRect, srcW, srcH, origW, origH);
        }

        private static bool PassTexture(AtoContext ctx, Texture2D tex, List<AtoTextureUse> uses, int w, int h)
        {
            foreach (var use in uses)
            {
                if (use.Texture != tex) continue;
                var dummy = new Rect(0, 0, 1, 1);
                if (!PassCrop(ctx, use, dummy, w, h, tex.width, tex.height)) return false;
            }
            return true;
        }

        private static bool PassCrop(AtoContext ctx, AtoTextureUse use, Rect uv, int smallW, int smallH, int origW, int origH)
        {
            var q = ctx.Settings.quality;
            var shortSide = Mathf.Min(origW, origH);
            var kind = use.Kind == AtoTextureKind.Normal ? 1
                : (use.Kind == AtoTextureKind.Gray || use.Kind == AtoTextureKind.Mask) ? 2
                : use.AlphaMode == AtoAlphaMode.Blend ? 3
                : use.AlphaMode == AtoAlphaMode.Cutout ? 4 : 0;

            var orig = CropLinear(ctx, use.Texture, uv, origW, origH, use.IsSrgb, use.Kind == AtoTextureKind.Normal);
            var small = GpuResample(ctx, orig, smallW, smallH, use.IsSrgb, use.AlphaMode != AtoAlphaMode.Opaque);
            var up = GpuResample(ctx, small, origW, origH, use.IsSrgb, use.AlphaMode != AtoAlphaMode.Opaque);

            var n = origW * origH;
            var na = new NativeArray<float4>(n, Allocator.TempJob);
            var nb = new NativeArray<float4>(n, Allocator.TempJob);
            var pixelsO = orig.GetPixels();
            var pixelsU = up.GetPixels();
            for (var i = 0; i < n; i++)
            {
                var a = pixelsO[i]; var b = pixelsU[i];
                na[i] = new float4(a.r, a.g, a.b, a.a);
                nb[i] = new float4(b.r, b.g, b.b, b.a);
            }
            var o = new NativeArray<float>(5, Allocator.TempJob);
            var job = new AtoSsimDeJob
            {
                Orig = na, Cmp = nb, W = origW, H = origH, Kind = kind,
                Cutoff = use.Cutoff, GrayMask = use.UsedGrayChannels == 0 ? 1 : use.UsedGrayChannels, Out = o
            };
            job.Schedule().Complete();
            var ssim = o[0]; var dE = o[1]; var alpha = o[2]; var ang = o[3]; var p95 = o[4];
            o.Dispose(); na.Dispose(); nb.Dispose();
            UnityEngine.Object.DestroyImmediate(orig);
            UnityEngine.Object.DestroyImmediate(small);
            UnityEngine.Object.DestroyImmediate(up);

            if (kind == 1)
                return ang <= q.normalAngleDeg + 1e-4f && p95 <= q.normalP95Deg + 1e-4f;
            if (kind == 2)
                return alpha <= q.grayRmse + 1e-4f;

            var ssimOk = shortSide < 11 || ssim >= q.msSsim - 1e-4f;
            var deOk = dE <= q.deltaE + 1e-4f;
            var aOk = true;
            if (kind == 3) aOk = alpha <= q.alphaRmse + 1e-4f;
            if (kind == 4) aOk = alpha >= q.cutoutIou - 1e-4f;
            return ssimOk && deOk && aOk;
        }

        private static Texture2D CropLinear(AtoContext ctx, Texture2D tex, Rect uv, int w, int h, bool srgb, bool normal)
        {
            var src = ctx.GetReadable(tex);
            var rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32,
                srgb ? RenderTextureReadWrite.sRGB : RenderTextureReadWrite.Linear);
            var prev = RenderTexture.active;
            // Blit with scale/offset. / 用 scale/offset blit 裁切。
            var mat = AtoBlit.Material();
            mat.SetTexture("_MainTex", src);
            mat.SetVector("_ST", new Vector4(uv.width, uv.height, uv.x, uv.y));
            Graphics.Blit(src, rt, mat, 0);
            RenderTexture.active = rt;
            var dst = new Texture2D(w, h, TextureFormat.RGBA32, false, !srgb);
            dst.ReadPixels(new Rect(0, 0, w, h), 0, 0, false);
            dst.Apply(false, false);
            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(rt);
            return dst;
        }

        private static Texture2D GpuResample(AtoContext ctx, Texture2D src, int w, int h, bool srgb, bool premultiply)
        {
            var rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32,
                srgb ? RenderTextureReadWrite.sRGB : RenderTextureReadWrite.Linear);
            var mat = AtoBlit.Material();
            var pass = premultiply ? 2 : 1;
            Graphics.Blit(src, rt, mat, pass);
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            var dst = new Texture2D(w, h, TextureFormat.RGBA32, false, !srgb);
            dst.ReadPixels(new Rect(0, 0, w, h), 0, 0, false);
            dst.Apply(false, false);
            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(rt);
            return dst;
        }

        public static bool IsSolid(AtoContext ctx, Texture2D tex, out Color32 c)
        {
            c = default;
            var px = ctx.GetPixels(tex);
            if (px == null || px.Length == 0) return true;
            c = px[0];
            for (var i = 1; i < px.Length; i += Math.Max(1, px.Length / 4096))
            {
                var p = px[i];
                if (p.r != c.r || p.g != c.g || p.b != c.b || p.a != c.a) return false;
            }
            return true;
        }
    }

    internal static class AtoBlit
    {
        private static Material _mat;
        public static Material Material()
        {
            if (_mat != null) return _mat;
            var sh = Shader.Find("Hidden/ATO/Processing");
            if (sh == null)
            {
                AtoLog.Warn("Hidden/ATO/Processing shader missing; using Graphics.Blit fallback.");
                sh = Shader.Find("Hidden/BlitCopy") ?? Shader.Find("Unlit/Texture");
            }
            _mat = new Material(sh) { hideFlags = HideFlags.HideAndDontSave };
            return _mat;
        }
    }
}
