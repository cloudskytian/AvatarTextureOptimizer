// ATO — Avatar Texture Optimizer
// GPU (RenderTexture) bilinear upsample used by the quality evaluation. The spec requires
// "将缩小后的岛实际覆盖区双线性上采样回原尺寸后与原图比较" — bilinear filtering is exactly
// what Graphics.Blit does with the default material, so this is a correct GPU path for the
// upsample step. Falls back to CPU whenever the GPU is unavailable or the region is small
// (upload + readback would not pay off).
// 质量评估用的 GPU（RenderTexture）双线性上采样。规范要求"将缩小后的岛实际覆盖区双线性上采样
// 回原尺寸后与原图比较"——Graphics.Blit 默认就是双线性滤波，因此这是上采样步骤的正确 GPU 路径。
// GPU 不可用或区域过小（上传+读回不划算）时回退到 CPU。

using System;
using UnityEngine;

namespace net.fosa.ato.editor
{
    /// <summary>
    /// GPU resampling helper. GPU 重采样辅助。
    /// </summary>
    public static class GpuResampler
    {
        /// <summary>Minimum region edge (px) to bother using the GPU. 值得用 GPU 的最小区域边长（px）。</summary>
        public const int MinGpuEdge = 64;

        /// <summary>True when a GPU context should be available. GPU 上下文是否可用。</summary>
        public static bool IsAvailable => SystemInfo.supportsComputeShaders || SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Null;

        /// <summary>
        /// Bilinear-upsample a region on the GPU. Returns null when it cannot / should not run.
        /// 在 GPU 上双线性上采样区域；无法/不宜运行时返回 null。
        /// </summary>
        public static Color[] TryUpsample(Color[] src, int w, int h, int dw, int dh)
        {
            if (src == null || w <= 0 || h <= 0) return null;
            if (!IsAvailable) return null;
            if (Mathf.Min(dw, dh) < MinGpuEdge || Mathf.Min(w, h) < 4) return null;

            try
            {
                var srcTex = new Texture2D(w, h, TextureFormat.RGBAFloat, false, true);
                srcTex.SetPixels(src);
                srcTex.Apply(false, false);

                var rt = RenderTexture.GetTemporary(dw, dh, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);
                var prev = RenderTexture.active;
                Graphics.Blit(srcTex, rt); // bilinear by default 默认双线性
                RenderTexture.active = rt;
                var outTex = new Texture2D(dw, dh, TextureFormat.RGBAFloat, false, true);
                outTex.ReadPixels(new Rect(0, 0, dw, dh), 0, 0);
                outTex.Apply(false, false);
                RenderTexture.active = prev;
                RenderTexture.ReleaseTemporary(rt);
                UnityEngine.Object.DestroyImmediate(srcTex);

                var result = outTex.GetPixels();
                UnityEngine.Object.DestroyImmediate(outTex);
                return result;
            }
            catch (Exception e)
            {
                ATOLog.Verbose($"[GPU] upsample fell back to CPU: {e.Message}");
                return null;
            }
        }
    }
}
