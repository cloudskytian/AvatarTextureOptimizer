// AvatarTextureOptimizer
// File: Editor/Quality/GPUImageOps.cs
//
// C# wrapper over ATO_ImageOps.compute + RenderTexture helpers.
// All processing is done in LINEAR space with premultiplied alpha where
// required; sRGB encoding is only re-applied when writing final pixels.
//
// ATO_ImageOps.compute 的 C# 封装 + RenderTexture 辅助。
// 所有处理都在【线性空间】完成，必要时使用预乘 alpha；仅在写最终像素时
// 重新应用 sRGB 编码。

using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace net.fosa.avatar_texture_optimizer.editor.quality
{
    public static class GPUImageOps
    {
        private static ComputeShader _shader;
        private static bool _loadFailed;

        private static ComputeShader Shader
        {
            get
            {
                if (_shader != null || _loadFailed) return _shader;
                _shader = AssetDatabase.LoadAssetAtPath<ComputeShader>(
                    "Packages/net.fosa.avatar-texture-optimizer/Editor/Quality/Shaders/ATO_ImageOps.compute");
                if (_shader == null)
                {
                    _loadFailed = true;
                    logging.ATOLog.Warn("Failed to load ATO_ImageOps.compute — GPU quality evaluation disabled. / 无法加载 ATO_ImageOps.compute——GPU 质量评估已禁用。");
                }
                return _shader;
            }
        }

        private static int Kernel(string name) => Shader.FindKernel(name);

        /// <summary>
        /// Create a linear RGBA32F render texture with random-write enabled.
        /// 创建线性 RGBA32F 且可随机写入的渲染纹理。
        /// </summary>
        public static RenderTexture CreateRT(int width, int height)
        {
            var rt = new RenderTexture(width, height, 0, GraphicsFormat.R32G32B32A32_SFloat)
            {
                enableRandomWrite = true,
                useMipMap = false,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                name = "ATO_RT",
            };
            rt.Create();
            return rt;
        }

        /// <summary>Read a RenderTexture back to a Color[] (main thread). / 将 RenderTexture 读回 Color[]（主线程）。</summary>
        public static Color[] Readback(RenderTexture rt)
        {
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGBAFloat, false, true); // linear / 线性
            tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
            tex.Apply();
            var pixels = tex.GetPixels();
            UnityEngine.Object.DestroyImmediate(tex);
            RenderTexture.active = prev;
            return pixels;
        }

        /// <summary>Linearize an sRGB Texture2D region into a linear RT. / 将 sRGB Texture2D 区域线性化到线性 RT。</summary>
        public static RenderTexture LinearizeRegion(Texture2D src, RectInt region, int targetW, int targetH)
        {
            // Clamp the region to the texture bounds (out-of-bounds islands are
            // normalizable but may extend past the edge). / 将区域钳制到纹理
            // 范围内（越界但可归一的岛可能超出边缘）。
            region.x = Mathf.Clamp(region.x, 0, src.width - 1);
            region.y = Mathf.Clamp(region.y, 0, src.height - 1);
            region.width = Mathf.Clamp(region.width, 1, src.width - region.x);
            region.height = Mathf.Clamp(region.height, 1, src.height - region.y);

            var rt = CreateRT(targetW, targetH);
            // Stage 1: copy the region into a raw RT via Graphics.CopyTexture
            // (fast path) or Blit fallback.
            // 阶段 1：通过 Graphics.CopyTexture（快路径）或 Blit 兜底将区域
            // 拷入原始 RT。
            var raw = CreateRT(region.width, region.height);
            try
            {
                Graphics.CopyTexture(src, 0, 0, region.x, region.y, region.width, region.height, raw, 0, 0, 0, 0);
            }
            catch
            {
                // CopyTexture is not supported for all texture layouts; use
                // CPU fallback: read pixels and upload.
                // CopyTexture 并非支持所有纹理布局；使用 CPU 兜底：读像素上传。
                var prev = RenderTexture.active;
                RenderTexture.active = raw;
                GL.PushMatrix();
                GL.LoadPixelMatrix(0, region.width, 0, region.height);
                Graphics.DrawTexture(new Rect(0, 0, region.width, region.height), src,
                    new Rect(region.x / (float)src.width, region.y / (float)src.height,
                        region.width / (float)src.width, region.height / (float)src.height),
                    0, 0, 0, 0);
                GL.PopMatrix();
                RenderTexture.active = prev;
            }

            if (targetW == region.width && targetH == region.height)
            {
                Linearize(raw, rt);
            }
            else
            {
                var mid = CreateRT(targetW, targetH);
                DownsampleBilinear(raw, targetW, targetH, mid);
                Linearize(mid, rt);
                mid.Release();
            }
            raw.Release();
            return rt;
        }

        public static void Linearize(RenderTexture src, RenderTexture dst)
        {
            if (Shader == null) return;
            Shader.SetTexture(Kernel("Linearize"), "InTex", src);
            Shader.SetTexture(Kernel("Linearize"), "OutTex", dst);
            Shader.SetVector("TexSize", new Vector4(src.width, src.height, 0, 0));
            Dispatch(Kernel("Linearize"), dst.width, dst.height);
        }

        public static void EncodeSRGB(RenderTexture src, RenderTexture dst)
        {
            if (Shader == null) return;
            Shader.SetTexture(Kernel("EncodeSRGB"), "InTex", src);
            Shader.SetTexture(Kernel("EncodeSRGB"), "OutTex", dst);
            Shader.SetVector("OutSize", new Vector4(dst.width, dst.height, 0, 0));
            Dispatch(Kernel("EncodeSRGB"), dst.width, dst.height);
        }

        public static void Premultiply(RenderTexture src, RenderTexture dst)
        {
            if (Shader == null) return;
            Shader.SetTexture(Kernel("Premultiply"), "InTex", src);
            Shader.SetTexture(Kernel("Premultiply"), "OutTex", dst);
            Shader.SetVector("TexSize", new Vector4(src.width, src.height, 0, 0));
            Dispatch(Kernel("Premultiply"), dst.width, dst.height);
        }

        public static void DownsampleBilinear(RenderTexture src, int dstW, int dstH, RenderTexture dst)
        {
            if (Shader == null) return;
            Shader.SetTexture(Kernel("Downsample"), "InTex", src);
            Shader.SetTexture(Kernel("Downsample"), "OutTex", dst);
            Shader.SetVector("TexSize", new Vector4(src.width, src.height, 1f / src.width, 1f / src.height));
            Shader.SetVector("OutSize", new Vector4(dst.width, dst.height, 1f / dst.width, 1f / dst.height));
            Dispatch(Kernel("Downsample"), dstW, dstH);
        }

        public static void UpsampleBilinear(RenderTexture src, int dstW, int dstH, RenderTexture dst)
        {
            if (Shader == null) return;
            Shader.SetTexture(Kernel("Upsample"), "InTex", src);
            Shader.SetTexture(Kernel("Upsample"), "OutTex", dst);
            Shader.SetVector("TexSize", new Vector4(src.width, src.height, 1f / src.width, 1f / src.height));
            Shader.SetVector("OutSize", new Vector4(dst.width, dst.height, 1f / dst.width, 1f / dst.height));
            Dispatch(Kernel("Upsample"), dstW, dstH);
        }

        /// <summary>Detect whether a region is solid-colored (difference vs center). / 检测区域是否为纯色（与中心像素的差异）。</summary>
        public static bool IsSolid(RenderTexture src)
        {
            if (Shader == null) return false;
            var flag = CreateRT(src.width, src.height);
            Shader.SetTexture(Kernel("DetectSolid"), "InTex", src);
            Shader.SetTexture(Kernel("DetectSolid"), "OutTex", flag);
            Shader.SetVector("TexSize", new Vector4(src.width, src.height, 0, 0));
            Dispatch(Kernel("DetectSolid"), src.width, src.height);
            var pixels = Readback(flag);
            flag.Release();
            foreach (var p in pixels)
                if (p.r > 0.5f) return false;
            return true;
        }

        public static void DecodeNormal(RenderTexture src, RenderTexture dst)
        {
            if (Shader == null) return;
            Shader.SetTexture(Kernel("DecodeNormal"), "InTex", src);
            Shader.SetTexture(Kernel("DecodeNormal"), "OutTex", dst);
            Shader.SetVector("TexSize", new Vector4(src.width, src.height, 0, 0));
            Dispatch(Kernel("DecodeNormal"), dst.width, dst.height);
        }

        public static void ReencodeNormal(RenderTexture src, RenderTexture dst)
        {
            if (Shader == null) return;
            Shader.SetTexture(Kernel("ReencodeNormal"), "InTex", src);
            Shader.SetTexture(Kernel("ReencodeNormal"), "OutTex", dst);
            Shader.SetVector("OutSize", new Vector4(dst.width, dst.height, 0, 0));
            Dispatch(Kernel("ReencodeNormal"), dst.width, dst.height);
        }

        public static void NormalAngle(RenderTexture a, RenderTexture b, RenderTexture dst)
        {
            if (Shader == null) return;
            Shader.SetTexture(Kernel("NormalAngle"), "InTex", a);
            Shader.SetTexture(Kernel("NormalAngle"), "RefTex", b);
            Shader.SetTexture(Kernel("NormalAngle"), "OutTex", dst);
            Shader.SetVector("TexSize", new Vector4(a.width, a.height, 0, 0));
            Dispatch(Kernel("NormalAngle"), dst.width, dst.height);
        }

        private static void Dispatch(int kernel, int width, int height)
        {
            Shader.Dispatch(kernel, Mathf.Max(1, Mathf.CeilToInt(width / 8f)), Mathf.Max(1, Mathf.CeilToInt(height / 8f)), 1);
        }

        /// <summary>Release a list of RenderTextures safely. / 安全释放一组 RenderTexture。</summary>
        public static void ReleaseAll(params RenderTexture[] rts)
        {
            foreach (var rt in rts)
                if (rt != null) rt.Release();
        }
    }
}
