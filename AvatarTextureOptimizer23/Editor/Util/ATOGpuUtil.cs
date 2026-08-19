using System.Collections.Generic;
using UnityEngine;

namespace FOSA.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// Shared RT / material pool. Always released at the end of a bake.
    /// 共享 RT / 材质池。烘焙结束必须释放。
    /// </summary>
    internal static class ATOGpuUtil
    {
        private static readonly List<RenderTexture> Rts = new List<RenderTexture>();
        private static readonly Dictionary<string, Material> Mats = new Dictionary<string, Material>();

        public static RenderTexture GetRT(int w, int h, RenderTextureFormat fmt = RenderTextureFormat.ARGBFloat, bool linear = true)
        {
            w = Mathf.Max(1, w);
            h = Mathf.Max(1, h);
            var desc = new RenderTextureDescriptor(w, h, fmt, 0)
            {
                sRGB = !linear,
                useMipMap = false,
                autoGenerateMips = false,
                msaaSamples = 1,
                enableRandomWrite = false
            };
            var rt = new RenderTexture(desc)
            {
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.HideAndDontSave
            };
            rt.Create();
            Rts.Add(rt);
            return rt;
        }

        public static Material GetMaterial(string shaderName)
        {
            if (Mats.TryGetValue(shaderName, out var m) && m != null) return m;
            var shader = Shader.Find(shaderName);
            if (shader == null)
            {
                Debug.LogWarning($"{AvatarTextureOptimizer.LogPrefix} Shader not found: {shaderName}");
                return null;
            }
            m = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            Mats[shaderName] = m;
            return m;
        }

        public static Texture2D ReadRT(RenderTexture rt, bool linear)
        {
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGBAFloat, false, linear)
            {
                hideFlags = HideFlags.HideAndDontSave,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp
            };
            tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0, false);
            tex.Apply(false, false);
            RenderTexture.active = prev;
            return tex;
        }

        public static void Blit(Texture src, RenderTexture dst, Material mat, int pass = 0)
        {
            var prev = RenderTexture.active;
            Graphics.Blit(src, dst, mat, pass);
            RenderTexture.active = prev;
        }

        public static void ReleaseAll()
        {
            foreach (var rt in Rts)
            {
                if (rt == null) continue;
                rt.Release();
                Object.DestroyImmediate(rt);
            }
            Rts.Clear();
            foreach (var kv in Mats)
            {
                if (kv.Value != null) Object.DestroyImmediate(kv.Value);
            }
            Mats.Clear();
        }
    }
}
