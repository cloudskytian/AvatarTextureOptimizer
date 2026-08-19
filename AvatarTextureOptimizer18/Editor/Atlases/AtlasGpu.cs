using UnityEngine;

namespace Fosa.AvatarTextureOptimizer.Editor.Atlases
{
    // GPU 图集/贴图操作工具（RenderTexture 批量执行）：跳跃洪泛外扩、法线重归一化、线性预乘 → 字节编码、RT 回读。
    // GPU atlas/texture utilities (RenderTexture batch ops): jump-flood dilation, normal renormalization,
    // linear-premultiplied → byte encoding, RT readback.
    internal static class AtlasGpu
    {
        // GPU 跳跃洪泛外扩（无限外扩填满空白）。GPU jump-flood dilation (infinite spread over empty areas).
        public static Texture2D DilateGpu(Texture2D source, int w, int h, bool keepAlphaZero)
        {
            var mat = new Material(Shader.Find("Hidden/ATO/Dilate"));
            try
            {
                mat.SetFloat("_KeepAlphaZero", keepAlphaZero ? 1f : 0f);
                var rtA = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);
                var rtB = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);
                try
                {
                    Graphics.Blit(source, rtA);
                    int maxSide = Mathf.Max(w, h);
                    for (int step = 1; step < maxSide; step *= 2)
                    {
                        mat.SetFloat("_Step", step);
                        Graphics.Blit(rtA, rtB, mat);
                        var tmp = rtA; rtA = rtB; rtB = tmp;
                    }
                    // 最后以 1 步长扫尾（保证密实填充）。Final sweep at step 1 for a solid fill.
                    mat.SetFloat("_Step", 1f);
                    Graphics.Blit(rtA, rtB, mat);
                    return ReadbackFloat(rtB, w, h);
                }
                finally
                {
                    RenderTexture.ReleaseTemporary(rtA);
                    RenderTexture.ReleaseTemporary(rtB);
                }
            }
            finally
            {
                Object.DestroyImmediate(mat);
            }
        }

        public static Texture2D NormalizeGpu(Texture2D source, int w, int h)
        {
            var mat = new Material(Shader.Find("Hidden/ATO/NormalizeNormal"));
            try
            {
                var rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);
                try
                {
                    Graphics.Blit(source, rt, mat);
                    return ReadbackFloat(rt, w, h);
                }
                finally
                {
                    RenderTexture.ReleaseTemporary(rt);
                }
            }
            finally
            {
                Object.DestroyImmediate(mat);
            }
        }

        // 编码：线性预乘 → （可选）去预乘 + sRGB 字节（输出 RGBA32 非 sRGB 贴图）。Encode to bytes.
        public static Texture2D EncodeGpu(Texture2D source, int w, int h, bool sRGB, bool unpremultiply)
        {
            var mat = new Material(Shader.Find("Hidden/ATO/Encode"));
            try
            {
                mat.SetFloat("_SRGB", sRGB ? 1f : 0f);
                mat.SetFloat("_Unpremultiply", unpremultiply ? 1f : 0f);
                var rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
                try
                {
                    Graphics.Blit(source, rt, mat);
                    var tex = new Texture2D(w, h, TextureFormat.RGBA32, false, false);
                    var prev = RenderTexture.active;
                    RenderTexture.active = rt;
                    tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                    tex.Apply(false, false);
                    RenderTexture.active = prev;
                    return tex;
                }
                finally
                {
                    RenderTexture.ReleaseTemporary(rt);
                }
            }
            finally
            {
                Object.DestroyImmediate(mat);
            }
        }

        public static Texture2D ReadbackFloat(RenderTexture rt, int w, int h)
        {
            var tex = new Texture2D(w, h, TextureFormat.RGBAFloat, false, true);
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            tex.Apply(false, false);
            RenderTexture.active = prev;
            return tex;
        }
    }
}
