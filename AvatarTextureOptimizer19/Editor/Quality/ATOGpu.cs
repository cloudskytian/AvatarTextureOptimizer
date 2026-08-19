// English: GPU (RenderTexture / Compute) helpers. Falls back silently to CPU if shaders are missing.
// 中文：GPU（RenderTexture / Compute）辅助。着色器缺失时静默回退 CPU。
using UnityEditor;
using UnityEngine;

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    internal static class ATOGpu
    {
        private static ComputeShader _quality;

        public static ComputeShader QualityShader
        {
            get
            {
                if (_quality != null) return _quality;
                var guids = AssetDatabase.FindAssets("ATO_Quality t:ComputeShader");
                if (guids != null && guids.Length > 0)
                {
                    _quality = AssetDatabase.LoadAssetAtPath<ComputeShader>(AssetDatabase.GUIDToAssetPath(guids[0]));
                }

                return _quality;
            }
        }

        /// <summary>
        /// Linear resample via Blit. Used for decode and as a GPU batch front-end.
        /// 通过 Blit 做线性重采样，用于解码以及 GPU 批处理前端。
        /// </summary>
        public static Texture2D BlitResample(Texture src, int w, int h, bool linear)
        {
            if (src == null || w <= 0 || h <= 0) return null;
            var desc = new RenderTextureDescriptor(w, h, RenderTextureFormat.ARGB32, 0)
            {
                sRGB = !linear,
                msaaSamples = 1
            };
            var rt = RenderTexture.GetTemporary(desc);
            var prev = RenderTexture.active;
            try
            {
                Graphics.Blit(src, rt);
                RenderTexture.active = rt;
                var tex = new Texture2D(w, h, TextureFormat.RGBA32, false, linear);
                tex.ReadPixels(new Rect(0, 0, w, h), 0, 0, false);
                tex.Apply(false, false);
                return tex;
            }
            finally
            {
                RenderTexture.active = prev;
                RenderTexture.ReleaseTemporary(rt);
            }
        }
    }
}
