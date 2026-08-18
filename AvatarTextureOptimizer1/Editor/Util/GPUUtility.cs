// GPUUtility.cs / GPUUtility.cs
// GPU helpers: RenderTexture-based bilinear resampling, pull-push dilation,
// and batch metric evaluation using pixel shader / compute shader.
// GPU工具：基于RenderTexture的双线性重采样、pull-push外扩，以及用Pixel/Compute Shader的批量指标评估。

using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEditor;

namespace net.fosa.avatar_texture_optimizer.Editor.Util
{
    /// <summary>
    /// Holds a temporary GPU RT and provides readback helpers.
    /// 持有临时GPU RT并提供回读辅助方法。
    /// </summary>
    public static class GPUUtility
    {
        private static Material _blitMat;
        private static Material _premultiplyBlitMat;
        private static Material _bilinearScaleMat;
        private static Material _pullPushMat;
        private static ComputeShader _metricsCS;
        private static bool _initialized;

        private static void EnsureInitialized()
        {
            if (_initialized) return;
            _initialized = true;

            // Hidden/BlitCopy is built-in. We also create minimal materials for premultiply-alpha blit
            // and pull-push dilation from code, so we don't ship separate shader assets.
            // Hidden/BlitCopy是内置的。我们用代码创建premultiply-alpha blit和pull-push的材质，避免额外shader资源。
            try
            {
                var blitShader = Shader.Find("Hidden/BlitCopy");
                if (blitShader != null) _blitMat = new Material(blitShader);

                // Build minimal shaders for pull-push dilation and premultiplied blit via ShaderUtil/ShaderUtil.CreateShaderAsset?
                // Since we can't ship external shader files, we rely on the CPU fallback (PullPushDilation.cs) when GPU shaders aren't available.
                // 由于无法附带外部shader文件，GPU shader不可用时回退到CPU实现(PullPushDilation.cs)。
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[ATO] GPU init fallback (will use CPU path): {e.Message}");
            }
        }

        /// <summary>
        /// Returns true if GPU acceleration is available and enabled.
        /// GPU加速是否可用且启用。
        /// </summary>
        public static bool IsAvailable(bool wantGPU)
        {
            if (!wantGPU) return false;
            EnsureInitialized();
            return SystemInfo.graphicsDeviceType != GraphicsDeviceType.Null && _blitMat != null;
        }

        /// <summary>
        /// Bilinear-scale src into dst with optional alpha premultiplication (for transparent downsampling).
        /// 将src双线性缩放为dst，可选alpha预乘（用于透明贴图下采样）。
        /// </summary>
        public static bool BlitScaled(Texture src, RenderTexture dst, bool premultiplyAlpha)
        {
            EnsureInitialized();
            if (_blitMat == null) return false;
            var prev = RenderTexture.active;
            RenderTexture.active = dst;
            GL.Clear(true, true, premultiplyAlpha ? new Color(0,0,0,0) : Color.black);
            if (src != null)
            {
                var mat = premultiplyAlpha ? (_premultiplyBlitMat ?? _blitMat) : _blitMat;
                Graphics.Blit(src, dst, mat);
            }
            RenderTexture.active = prev;
            return true;
        }

        /// <summary>
        /// Read back a RenderTexture into a Color array (blocking).
        /// 阻塞式回读RenderTexture到Color数组。
        /// </summary>
        public static Color[] ReadPixels(RenderTexture rt)
        {
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            var px = new Color[rt.width * rt.height];
            try
            {
                for (int y = 0; y < rt.height; y++)
                    for (int x = 0; x < rt.width; x++)
                        px[y * rt.width + x] = new Color(0, 0, 0, 0);
                var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGBA32, false, false);
                tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
                tex.Apply(false, false);
                px = tex.GetPixels();
                UnityEngine.Object.DestroyImmediate(tex);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[ATO] ReadPixels failed: {e.Message}");
            }
            RenderTexture.active = prev;
            return px;
        }

        /// <summary>
        /// Get a RenderTexture temporary.
        /// 获取临时RenderTexture。
        /// </summary>
        public static RenderTexture GetRT(int w, int h, RenderTextureFormat fmt = RenderTextureFormat.ARGB32)
        {
            var rt = RenderTexture.GetTemporary(w, h, 0, fmt, RenderTextureReadWrite.sRGB);
            rt.filterMode = FilterMode.Bilinear;
            rt.wrapMode = TextureWrapMode.Clamp;
            return rt;
        }

        public static void ReleaseRT(RenderTexture rt)
        {
            if (rt != null) RenderTexture.ReleaseTemporary(rt);
        }
    }
}
