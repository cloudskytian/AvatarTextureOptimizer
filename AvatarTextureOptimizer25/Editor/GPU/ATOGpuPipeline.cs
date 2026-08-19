// Avatar Texture Optimizer / 头像贴图优化器
// GPU-side pipeline for quality evaluation: decode (linear, premultiplied),
// iterative bilinear downsample, upsample for comparison, normal-vector field
// handling. All float render targets are created in linear read/write space so
// GPU color-space behavior is fully deterministic (final sRGB display encoding
// is done explicitly in our shader passes).
// 质量评估的 GPU 管线：解码（线性、预乘）、逐级双线性下采样、对比用上采样、
// 法线向量场处理。所有浮点 RT 都以线性空间创建，使 GPU 色彩空间行为完全
// 确定（最终 sRGB 显示编码由我们自己的 shader pass 显式完成）。

using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace FOSA.AvatarTextureOptimizer.Editor
{
    /// <summary>Texture-session for one source texture (GPU state). / 单源贴图的 GPU 会话。</summary>
    public sealed class ATOTextureSession : IDisposable
    {
        public ATOTextureEntry entry;
        /// <summary>Full-size float RT: premultiplied linear color OR unpacked normal vectors OR raw linear data. / 全尺寸浮点 RT：预乘线性色 / 已解包法线向量 / 原始线性数据。</summary>
        public RenderTexture fullLinearFloat;
        /// <summary>CPU copy of display-space original bytes (for color & masks). / 显示空间原图字节的 CPU 副本（颜色与蒙版用）。</summary>
        public Color32[] originalDisplayBytes;
        /// <summary>CPU copy of unpacked original normals (normal path). / 解包后原始法线的 CPU 副本（法线路径）。</summary>
        public Color[] originalNormals;

        public void Dispose()
        {
            if (fullLinearFloat != null)
            {
                fullLinearFloat.Release();
                UnityEngine.Object.DestroyImmediate(fullLinearFloat);
                fullLinearFloat = null;
            }
            originalDisplayBytes = null;
            originalNormals = null;
        }
    }

    /// <summary>
    /// Owns the hidden shader material and all GPU operations for evaluation.
    /// 持有内置 shader 材质并执行全部评估 GPU 操作。
    /// </summary>
    public sealed class ATOGpuPipeline : IDisposable
    {
        public const int PassLinearCopy = 0;
        public const int PassPremultiply = 1;
        public const int PassUnpremultiplyEncodeSRGB = 2;
        public const int PassEncodeSRGB = 3;
        public const int PassUnpackNormal = 4;
        public const int PassRenormalize = 5;
        public const int PassDilate = 6;
        public const int PassFillCombine = 7;
        public const int PassRotate90CW = 8;
        public const int PassPackColorCoverage = 9;
        public const int PassCombinePack = 10;
        public const int PassPackNormal = 11;
        public const int PassAlphaToRgb = 12;
        public const int PassResolveUnpremultiplySRGB = 13;
        public const int PassResolveLinearAlpha = 14;

        private readonly Material _mat;
        public readonly ATORtPool Pool;

        /// <summary>Shared hidden material (pixel-size uniforms are set by callers). / 共享隐藏材质（像素尺寸由调用方设置）。</summary>
        public Material SharedMaterial => _mat;

        public ATOGpuPipeline(ATORtPool pool)
        {
            Pool = pool;
            var shader = Shader.Find("Hidden/ATO/Quality");
            if (shader == null)
                throw new InvalidOperationException("[ATO] missing ATOQualityShaders (hidden shader not found)");
            _mat = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
        }

        public void Dispose()
        {
            if (_mat != null) UnityEngine.Object.DestroyImmediate(_mat);
        }

        private static bool NeedExplicitSrgbDecode(Texture2D tex, ATOTextureEntry entry)
        {
            // GPU samplers auto-decode sRGB textures when the project uses linear
            // lighting. In gamma projects we must decode explicitly.
            // 线性色彩工程下采样器自动解码 sRGB；伽马工程需显式解码。
            if (!entry.sRGB) return false;
            return QualitySettings.activeColorSpace != ColorSpace.Linear;
        }

        private RenderTexture CreateFloatRT(int w, int h, bool mip = false)
        {
            var rt = new RenderTexture(w, h, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear)
            {
                useMipMap = mip,
                autoGenerateMips = mip,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
            };
            rt.Create();
            return rt;
        }

        /// <summary>Open a GPU+CPU session for one texture. / 打开一张贴图的 GPU+CPU 会话。</summary>
        public ATOTextureSession OpenSession(ATOTextureEntry entry, bool normalPath)
        {
            var tex = entry.texture;
            var session = new ATOTextureSession { entry = entry };
            bool explicitDecode = NeedExplicitSrgbDecode(tex, entry);
            _mat.DisableKeyword("ATO_DECODE_SRGB");
            if (explicitDecode) _mat.EnableKeyword("ATO_DECODE_SRGB");

            session.fullLinearFloat = CreateFloatRT(tex.width, tex.height);
            if (normalPath)
            {
                // Decode to normal vectors (unpack handles DXTnm/BC5/RGB). / 解码为法线向量（解包兼容 DXTnm/BC5/RGB）。
                Graphics.Blit(tex, session.fullLinearFloat, _mat, PassUnpackNormal);
                session.originalNormals = ReadbackRegionFloat(session.fullLinearFloat,
                    new RectInt(0, 0, tex.width, tex.height));
            }
            else if (entry.sRGB)
            {
                // Premultiplied linear color. / 预乘线性色。
                Graphics.Blit(tex, session.fullLinearFloat, _mat, PassPremultiply);
                var disp = ATOTextureIO.Readback(tex, Pool);
                session.originalDisplayBytes = disp.GetPixels32();
                UnityEngine.Object.DestroyImmediate(disp);
            }
            else
            {
                // Raw linear data (masks/grayscale): no premultiply, no encode. / 原始线性数据：不预乘不编码。
                Graphics.Blit(tex, session.fullLinearFloat, _mat, PassLinearCopy);
                var disp = ATOTextureIO.Readback(tex, Pool);
                session.originalDisplayBytes = disp.GetPixels32();
                UnityEngine.Object.DestroyImmediate(disp);
            }
            return session;
        }

        /// <summary>
        /// Iterative-halving bilinear downsample of a crop rectangle into
        /// (tw,th). Uses POOL-managed RTs (caller must Return() all).
        /// 对裁剪区做逐级减半的双线性下采样到 (tw,th)。RT 来自池（调用方须归还）。
        /// </summary>
        public List<RenderTexture> DownsampleCrop(RenderTexture src, RectInt crop, int tw, int th)
        {
            var chain = new List<RenderTexture>();
            int cw = Mathf.Max(1, crop.width), ch = Mathf.Max(1, crop.height);
            tw = Mathf.Clamp(tw, 1, cw);
            th = Mathf.Clamp(th, 1, ch);

            var srcScale = new Vector2((float)cw / src.width, (float)ch / src.height);
            var srcOffset = new Vector2((float)crop.x / src.width, (float)crop.y / src.height);

            RenderTexture cur = src;
            Vector2 curScale = srcScale;
            Vector2 curOffset = srcOffset;
            int curW = cw, curH = ch;
            while (curW > tw || curH > th)
            {
                int nextW = Mathf.Max(tw, curW / 2);
                int nextH = Mathf.Max(th, curH / 2);
                var next = Pool.Rent(nextW, nextH, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);
                Vector2 scale, offset;
                if (ReferenceEquals(cur, src))
                {
                    scale = new Vector2((float)curW / src.width, (float)curH / src.height);
                    offset = curOffset;
                }
                else
                {
                    scale = Vector2.one;
                    offset = Vector2.zero;
                }
                Graphics.Blit(cur, next, scale, offset);
                chain.Add(next);
                cur = next;
                curW = nextW;
                curH = nextH;
            }
            return chain; // last element holds the target-size image / 末元素即目标尺寸图
        }

        /// <summary>
        /// Upsample a small image back to (w,h) bilinearly (for original-size
        /// comparison).
        /// 将小图双线性上采样回 (w,h)（用于原尺寸对比）。
        /// </summary>
        public RenderTexture Upsample(RenderTexture small, int w, int h)
        {
            var dst = Pool.Rent(w, h, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);
            Graphics.Blit(small, dst);
            return dst;
        }

        /// <summary>Run a shader pass into a pooled float RT. / 经 shader pass 写入池化浮点 RT。</summary>
        public RenderTexture RunPass(RenderTexture src, int pass, int w, int h)
        {
            var dst = Pool.Rent(w, h, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);
            Graphics.Blit(src, dst, _mat, pass);
            return dst;
        }

        /// <summary>Run a pass in-place via a pooled temp, replacing <paramref name="rt"/>. / 经池化临时 RT 就地执行 pass 并替换引用。</summary>
        public void RunPassSelf(ref RenderTexture rt, int pass, int w, int h)
        {
            var dst = RunPass(rt, pass, w, h);
            Pool.Return(rt);
            rt = dst;
        }

        /// <summary>
        /// Run a pass that samples <c>_MainTex</c>=<paramref name="src"/> AND
        /// <c>_SecondTex</c>=<paramref name="second"/> into a pooled RT.
        /// 执行同时采样 _MainTex=src 与 _SecondTex=second 的 pass 并写入池化 RT。
        /// </summary>
        public RenderTexture RunPassWithSecond(RenderTexture src, RenderTexture second, int pass, int w, int h,
            RenderTextureFormat fmt = RenderTextureFormat.ARGB32)
        {
            var dst = Pool.Rent(w, h, fmt, RenderTextureReadWrite.Linear);
            _mat.SetTexture(Shader.PropertyToID("_SecondTex"), second);
            Graphics.Blit(src, dst, _mat, pass);
            _mat.SetTexture(Shader.PropertyToID("_SecondTex"), null);
            return dst;
        }

        /// <summary>out.rgb = color.rgb, out.a = coverage.r / 打包颜色与覆盖。</summary>
        public void CombineColorCoverageInto(RenderTexture output, RenderTexture color, RenderTexture coverage)
        {
            _mat.SetTexture(Shader.PropertyToID("_CoverageTex"), coverage);
            Graphics.Blit(color, output, _mat, PassPackColorCoverage);
            _mat.SetTexture(Shader.PropertyToID("_CoverageTex"), null);
        }

        /// <summary>out.rgb = original.a&gt;0 ? original.rgb : fill.rgb, out.a = original.a / 覆盖合成。</summary>
        public void CombineFillInto(RenderTexture output, RenderTexture original, RenderTexture fill)
        {
            _mat.SetTexture(Shader.PropertyToID("_SecondTex"), fill);
            Graphics.Blit(original, output, _mat, PassCombinePack);
            _mat.SetTexture(Shader.PropertyToID("_SecondTex"), null);
        }

        /// <summary>Encode a normal-vector float RT to [0,1] storage bytes. / 法线向量转 [0,1] 存储。</summary>
        public void EncodeNormalToBytes(RenderTexture output, RenderTexture normalsFloat)
        {
            Graphics.Blit(normalsFloat, output, _mat, PassPackNormal);
        }

        /// <summary>Encode to display bytes (ARGB32 linear RT, shader-driven encoding). / 编码为显示字节。</summary>
        public RenderTexture EncodeToDisplay(RenderTexture srcFloat, int pass, int w, int h)
        {
            var dst = Pool.Rent(w, h, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
            Graphics.Blit(srcFloat, dst, _mat, pass);
            return dst;
        }

        /// <summary>Read back a pixel region as Color32. / 回读某像素区域为 Color32。</summary>
        public Color32[] ReadbackRegion32(RenderTexture rt, RectInt rect)
        {
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            var tex = new Texture2D(rect.width, rect.height, TextureFormat.RGBA32, false, false);
            tex.ReadPixels(new Rect(rect.x, rect.y, rect.width, rect.height), 0, 0, false);
            tex.Apply(false, false);
            RenderTexture.active = prev;
            var data = tex.GetPixels32();
            UnityEngine.Object.DestroyImmediate(tex);
            return data;
        }

        /// <summary>Read back a pixel region as float Colors. / 回读某像素区域为浮点 Color。</summary>
        public Color[] ReadbackRegionFloat(RenderTexture rt, RectInt rect)
        {
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            var tex = new Texture2D(rect.width, rect.height, TextureFormat.RGBAFloat, false, false);
            tex.ReadPixels(new Rect(rect.x, rect.y, rect.width, rect.height), 0, 0, false);
            tex.Apply(false, false);
            RenderTexture.active = prev;
            var data = tex.GetPixels(0, 0, rect.width, rect.height);
            UnityEngine.Object.DestroyImmediate(tex);
            return data;
        }
    }
}
