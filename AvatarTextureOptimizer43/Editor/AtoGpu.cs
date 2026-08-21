using System;
using UnityEditor;
using UnityEngine;

namespace Fosa.ATO.Editor
{
    /// <summary>
    /// GPU resample + pull-push. Falls back to CPU on any failure (no compute, mobile editor, etc.).
    /// GPU 重采样与 pull-push。失败则 CPU 回退。
    /// </summary>
    public static class AtoGpu
    {
        static ComputeShader _cs;
        static Material _pp;
        static bool _csTried, _ppTried;
        static bool _csOk, _ppOk;

        public static bool ComputeReady
        {
            get
            {
                EnsureCs();
                return _csOk;
            }
        }

        public static bool PullPushReady
        {
            get
            {
                EnsurePp();
                return _ppOk;
            }
        }

        static void EnsureCs()
        {
            if (_csTried) return;
            _csTried = true;
            _cs = Load<ComputeShader>("AtoQuality.compute",
                "Packages/net.fosa.avatar-texture-optimizer/Editor/Shaders/AtoQuality.compute");
            _csOk = _cs != null && SystemInfo.supportsComputeShaders;
            AtoLog.Info("GPU compute " + (_csOk ? "ready" : "unavailable — CPU fallback"));
        }

        static void EnsurePp()
        {
            if (_ppTried) return;
            _ppTried = true;
            var sh = Shader.Find("Hidden/ATO/PullPush");
            if (sh == null)
                sh = Load<Shader>("AtoPullPush.shader",
                    "Packages/net.fosa.avatar-texture-optimizer/Editor/Shaders/AtoPullPush.shader");
            if (sh != null)
            {
                _pp = new Material(sh);
                _ppOk = true;
            }
            AtoLog.Info("GPU pull-push " + (_ppOk ? "ready" : "unavailable — CPU fallback"));
        }

        static T Load<T>(string fileName, string pkgPath) where T : UnityEngine.Object
        {
            var t = AssetDatabase.LoadAssetAtPath<T>(pkgPath);
            if (t != null) return t;
            var guids = AssetDatabase.FindAssets(System.IO.Path.GetFileNameWithoutExtension(fileName));
            foreach (var g in guids)
            {
                var p = AssetDatabase.GUIDToAssetPath(g);
                if (p != null && p.EndsWith(fileName, StringComparison.OrdinalIgnoreCase))
                    return AssetDatabase.LoadAssetAtPath<T>(p);
            }
            return null;
        }

        /// <summary>
        /// Bilinear GPU blit resample. Premul uses compute when available.
        /// GPU 双线性重采样。预乘走 compute。
        /// </summary>
        public static bool TryResample(Color[] src, int sw, int sh, int dw, int dh, bool premul, bool linear, out Color[] dst)
        {
            dst = null;
            if (src == null || sw < 1 || sh < 1 || dw < 1 || dh < 1) return false;
            if (sw * sh > 4096 * 4096 || dw * dh > 4096 * 4096) return false;
            try
            {
                var srcTex = new Texture2D(sw, sh, TextureFormat.RGBAFloat, false, linear);
                srcTex.SetPixels(src);
                srcTex.Apply(false, false);
                var rt = RenderTexture.GetTemporary(dw, dh, 0, RenderTextureFormat.ARGBFloat,
                    linear ? RenderTextureReadWrite.Linear : RenderTextureReadWrite.sRGB);
                rt.filterMode = FilterMode.Bilinear;
                var prev = RenderTexture.active;
                Graphics.Blit(srcTex, rt);
                RenderTexture.active = rt;
                var tmp = new Texture2D(dw, dh, TextureFormat.RGBAFloat, false, linear);
                tmp.ReadPixels(new Rect(0, 0, dw, dh), 0, 0, false);
                tmp.Apply(false, false);
                dst = tmp.GetPixels();
                UnityEngine.Object.DestroyImmediate(tmp);
                UnityEngine.Object.DestroyImmediate(srcTex);
                RenderTexture.active = prev;
                RenderTexture.ReleaseTemporary(rt);
                return dst != null && dst.Length == dw * dh;
            }
            catch (Exception e)
            {
                AtoLog.Detail("GPU resample failed: " + e.Message);
                dst = null;
                return false;
            }
        }

        /// <summary>
        /// GPU pull-push into an atlas Texture2D that still has CPU pixels. Returns filled pixels.
        /// 对图集做 GPU pull-push，失败返回 false。
        /// </summary>
        public static bool TryPullPush(Color[] px, bool[] filled, int w, int h, bool keepAlphaZero)
        {
            EnsurePp();
            if (!_ppOk || _pp == null || px == null) return false;
            var temps = new System.Collections.Generic.List<RenderTexture>();
            Texture2D src = null;
            try
            {
                src = new Texture2D(w, h, TextureFormat.RGBAFloat, false, true);
                src.wrapMode = TextureWrapMode.Clamp;
                src.filterMode = FilterMode.Bilinear;
                src.SetPixels(px);
                src.Apply(false, false);

                Texture current = src;
                int cw = w, ch = h;
                while (cw > 2 && ch > 2)
                {
                    int nw = Math.Max(1, cw / 2), nh = Math.Max(1, ch / 2);
                    var rt = RenderTexture.GetTemporary(nw, nh, 0, RenderTextureFormat.ARGBFloat,
                        RenderTextureReadWrite.Linear);
                    temps.Add(rt);
                    Graphics.Blit(current, rt, _pp, 0);
                    current = rt;
                    cw = nw; ch = nh;
                }

                _pp.SetFloat("_KeepAlphaZero", keepAlphaZero ? 1f : 0f);
                // Push from coarsest back to full res. 从最粗层 push 回全分辨率。
                for (int i = temps.Count - 1; i >= 0; i--)
                {
                    int hw = i == 0 ? w : temps[i - 1].width;
                    int hh = i == 0 ? h : temps[i - 1].height;
                    var dest = RenderTexture.GetTemporary(hw, hh, 0, RenderTextureFormat.ARGBFloat,
                        RenderTextureReadWrite.Linear);
                    _pp.SetTexture("_Low", temps[i]);
                    Texture baseTex = i == 0 ? src : (Texture)temps[i - 1];
                    Graphics.Blit(baseTex, dest, _pp, 1);
                    temps.Add(dest);
                    current = dest;
                }

                var prev = RenderTexture.active;
                RenderTexture.active = current as RenderTexture;
                if (RenderTexture.active != null)
                {
                    var tmp = new Texture2D(w, h, TextureFormat.RGBAFloat, false, true);
                    tmp.ReadPixels(new Rect(0, 0, w, h), 0, 0, false);
                    tmp.Apply(false, false);
                    var got = tmp.GetPixels();
                    UnityEngine.Object.DestroyImmediate(tmp);
                    if (got != null && got.Length == px.Length)
                    {
                        for (int i = 0; i < px.Length; i++)
                            if (!filled[i]) px[i] = got[i];
                    }
                }
                RenderTexture.active = prev;
                return true;
            }
            catch (Exception e)
            {
                AtoLog.Detail("GPU pull-push failed: " + e.Message);
                return false;
            }
            finally
            {
                foreach (var rt in temps) RenderTexture.ReleaseTemporary(rt);
                if (src != null) UnityEngine.Object.DestroyImmediate(src);
            }
        }

        public static Color[] ResampleOrCpu(Color[] src, int sw, int sh, int dw, int dh, bool premul, bool linear)
        {
            if (dw == sw && dh == sh) return src;
            // GPU blit is not premul-correct; use CPU when premul is required.
            // GPU blit 不做预乘，透明下采样走 CPU。
            if (!premul && (sw >= 64 || sh >= 64) && TryResample(src, sw, sh, dw, dh, false, linear, out var gpu) && gpu != null)
                return gpu;
            return AtoTextureUtil.Resample(src, sw, sh, dw, dh, premul, linear);
        }
    }
}
