using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// Optional GPU batch compare. Falls back silently if the compute shader is missing.
    /// 可选 GPU 批量比较。找不到 compute shader 时静默回退。
    /// </summary>
    public static class GpuQuality
    {
        const string Path = "Packages/net.fosa.avatar-texture-optimizer/Editor/Shaders/ATOQuality.compute";
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
            _cs = AssetDatabase.LoadAssetAtPath<ComputeShader>(Path);
        }

        /// <summary>
        /// Upload two linear images and return a coarse SSIM / RMSE pair. Not a replacement for the CPU MS-SSIM + CIEDE2000 path.
        /// 上传两张线性图，返回粗 SSIM / RMSE。不能替代 CPU 的 MS-SSIM + CIEDE2000。
        /// </summary>
        public static bool TryCoarse(Color[] a, Color[] b, int w, int h, out float ssim, out float rmse)
        {
            ssim = 1f;
            rmse = 0f;
            if (!Available || a == null || b == null || w <= 0 || h <= 0) return false;
            RenderTexture rta = null, rtb = null;
            ComputeBuffer buf = null;
            try
            {
                rta = Upload(a, w, h);
                rtb = Upload(b, w, h);
                buf = new ComputeBuffer(8, sizeof(uint));
                buf.SetData(new uint[8]);
                var k = _cs.FindKernel("CompareStats");
                _cs.SetTexture(k, "SrcA", rta);
                _cs.SetTexture(k, "SrcB", rtb);
                _cs.SetBuffer(k, "Acc", buf);
                _cs.SetInt("Width", w);
                _cs.SetInt("Height", h);
                _cs.Dispatch(k, (w + 7) / 8, (h + 7) / 8, 1);
                var data = new uint[8];
                buf.GetData(data);
                if (data[0] == 0) return false;
                var n = (float)data[0];
                var ma = data[1] / 10000f / n;
                var mb = data[2] / 10000f / n;
                var va = data[3] / 10000f / n - ma * ma;
                var vb = data[4] / 10000f / n - mb * mb;
                var cv = data[5] / 10000f / n - ma * mb;
                const float c1 = 0.0001f, c2 = 0.0009f;
                ssim = (float)((2 * ma * mb + c1) * (2 * cv + c2) /
                               ((ma * ma + mb * mb + c1) * (va + vb + c2) + 1e-12));
                rmse = Mathf.Sqrt(data[6] / 10000f / n);
                return true;
            }
            catch
            {
                return false;
            }
            finally
            {
                if (rta != null) rta.Release();
                if (rtb != null) rtb.Release();
                buf?.Release();
            }
        }

        static RenderTexture Upload(Color[] px, int w, int h)
        {
            var tex = new Texture2D(w, h, TextureFormat.RGBAFloat, false, true);
            tex.SetPixels(px);
            tex.Apply(false, false);
            var rt = new RenderTexture(w, h, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear)
            {
                enableRandomWrite = true
            };
            rt.Create();
            Graphics.Blit(tex, rt);
            Object.DestroyImmediate(tex);
            return rt;
        }
    }
}
