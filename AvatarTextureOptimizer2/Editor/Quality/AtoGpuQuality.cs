using System;
using UnityEditor;
using UnityEngine;
using Net.Fosa.AvatarTextureOptimizer;

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// GPU batch path via compute + RenderTexture. Falls back to CPU.
    /// GPU 批量路径；失败回退 CPU。
    /// </summary>
    public static class AtoGpuQuality
    {
        static ComputeShader _cs;
        static bool _tried;

        public static bool Available
        {
            get
            {
                Ensure();
                return _cs != null && SystemInfo.supportsComputeShaders;
            }
        }

        static void Ensure()
        {
            if (_tried) return;
            _tried = true;
            var guids = AssetDatabase.FindAssets("AtoQuality t:ComputeShader");
            foreach (var g in guids)
            {
                var p = AssetDatabase.GUIDToAssetPath(g);
                if (p.EndsWith("AtoQuality.compute"))
                {
                    _cs = AssetDatabase.LoadAssetAtPath<ComputeShader>(p);
                    break;
                }
            }
            if (_cs == null)
                AtoLog.VerboseInfo("AtoQuality.compute not found; CPU quality path");
        }

        /// <summary>
        /// Downsample via Blit (GPU bilinear) then readback a small crop for CPU metrics.
        /// GPU 双线性缩小后回读，CPU 做精确 CIEDE2000 / MS-SSIM。
        /// </summary>
        public static Color32[] GpuDownsample(Texture src, int dw, int dh, bool linear)
        {
            var rt = RenderTexture.GetTemporary(dw, dh, 0, RenderTextureFormat.ARGB32,
                linear ? RenderTextureReadWrite.Linear : RenderTextureReadWrite.sRGB);
            var prev = RenderTexture.active;
            try
            {
                Graphics.Blit(src, rt);
                RenderTexture.active = rt;
                var tex = new Texture2D(dw, dh, TextureFormat.RGBA32, false, linear);
                tex.ReadPixels(new Rect(0, 0, dw, dh), 0, 0);
                tex.Apply(false, false);
                var px = tex.GetPixels32();
                UnityEngine.Object.DestroyImmediate(tex);
                return px;
            }
            finally
            {
                RenderTexture.active = prev;
                RenderTexture.ReleaseTemporary(rt);
            }
        }

        public static bool TryMeanBuffer(Texture2D a, Texture2D b, string kernel, Vector4 param, out float mean)
        {
            mean = 0;
            Ensure();
            if (_cs == null) return false;
            try
            {
                int k = _cs.FindKernel(kernel);
                int w = a.width, h = a.height;
                var buf = new ComputeBuffer(w * h * 2, sizeof(float));
                _cs.SetTexture(k, "_Src", a);
                _cs.SetTexture(k, "_Dst", b);
                _cs.SetBuffer(k, "_Out", buf);
                _cs.SetVector("_Params", param);
                _cs.Dispatch(k, (w + 7) / 8, (h + 7) / 8, 1);
                var data = new float[w * h];
                buf.GetData(data, 0, 0, w * h);
                buf.Release();
                double acc = 0;
                for (int i = 0; i < data.Length; i++) acc += data[i];
                mean = (float)(acc / data.Length);
                return true;
            }
            catch (Exception ex)
            {
                AtoLog.VerboseInfo("GPU quality fail: " + ex.Message);
                return false;
            }
        }
    }
}
