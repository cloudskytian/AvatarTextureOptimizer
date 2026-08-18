// Avatar Texture Optimizer (ATO)
// GPU (RenderTexture + ComputeShader) temporary-resource management and dispatch with a
// managed fallback. Release-on-cancel is supported.
// GPU（RenderTexture + ComputeShader）临时资源管理与调度，带托管兜底，支持取消时释放。

using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace NetFosa.ATO
{
    /// <summary>
    /// Tracks pooled GPU temporaries and dispatches the downsample compute shader.
    /// 追踪池化的 GPU 临时资源并调度下采样计算着色器。
    /// </summary>
    public static class ATOGpu
    {
        private static readonly List<RenderTexture> _pooled = new List<RenderTexture>();
        private static ComputeShader _downsampleShader;
        private static bool _shaderLookedUp;
        private static bool _supported = SystemInfo.supportsComputeShaders;

        public static bool Supported => _supported;

        public static RenderTexture GetTemporary(int w, int h, RenderTextureFormat fmt = RenderTextureFormat.ARGB32)
        {
            var rt = RenderTexture.GetTemporary(w, h, 0, fmt, RenderTextureReadWrite.sRGB);
            _pooled.Add(rt);
            return rt;
        }

        public static void Release(RenderTexture rt)
        {
            if (rt == null) return;
            _pooled.Remove(rt);
            RenderTexture.ReleaseTemporary(rt);
        }

        public static void ReleaseAll()
        {
            foreach (var rt in _pooled)
                if (rt != null) RenderTexture.ReleaseTemporary(rt);
            _pooled.Clear();
        }

        private static ComputeShader DownsampleShader
        {
            get
            {
                if (!_shaderLookedUp)
                {
                    _shaderLookedUp = true;
                    foreach (var p in new[]
                    {
                        "Packages/net.fosa.avatar-texture-optimizer/Editor/GPU/ATODownsample.compute",
                        "Assets/AvatarTextureOptimizer/Editor/GPU/ATODownsample.compute",
                        "Assets/ATO/Editor/GPU/ATODownsample.compute",
                    })
                    {
                        var s = AssetDatabase.LoadAssetAtPath<ComputeShader>(p);
                        if (s != null) { _downsampleShader = s; break; }
                    }
                }
                return _downsampleShader;
            }
        }

        /// <summary>
        /// Premultiplied-alpha 2x downsample on the GPU. Returns false (and does nothing)
        /// when unsupported, leaving the caller to use the CPU path.
        /// GPU 预乘 alpha 2x 下采样。不支持时返回 false（不做事），由调用方走 CPU 路径。
        /// </summary>
        public static bool PremultipliedDownsample2x(Texture2D src, out Color[] result)
        {
            result = null;
            if (!_supported || src == null || !src.isReadable) return false;
            var shader = DownsampleShader;
            if (shader == null) return false;

            int w = src.width, h = src.height;
            int dw = Mathf.Max(1, w / 2), dh = Mathf.Max(1, h / 2);

            RenderTexture srcRT = null, dstRT = null;
            try
            {
                srcRT = GetTemporary(w, h, RenderTextureFormat.ARGB32);
                dstRT = GetTemporary(dw, dh, RenderTextureFormat.ARGB32);
                dstRT.enableRandomWrite = true;

                Graphics.Blit(src, srcRT);

                int kernel = shader.FindKernel("Downsample2x");
                shader.SetTexture(kernel, "_SrcTex", srcRT);
                shader.SetTexture(kernel, "_DstTex", dstRT);
                shader.Dispatch(kernel, Mathf.Max(1, dw / 8), Mathf.Max(1, dh / 8), 1);

                var tmp = new Texture2D(dw, dh, TextureFormat.RGBA32, false, false);
                var prev = RenderTexture.active;
                RenderTexture.active = dstRT;
                tmp.ReadPixels(new Rect(0, 0, dw, dh), 0, 0);
                tmp.Apply();
                RenderTexture.active = prev;

                result = tmp.GetPixels();
                Object.DestroyImmediate(tmp);
                return true;
            }
            catch (System.Exception e)
            {
                ATOLogger.Debug($"GPU downsample unavailable ({e.Message}); falling back to CPU. / GPU 下采样不可用（{e.Message}），回退 CPU。");
                return false;
            }
            finally
            {
                if (srcRT != null) Release(srcRT);
                if (dstRT != null) Release(dstRT);
            }
        }
    }
}
