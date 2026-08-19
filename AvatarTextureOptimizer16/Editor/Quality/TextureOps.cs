using UnityEngine;

namespace AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// GPU texture operations: crop, scale, bilinear upsample. / GPU 贴图操作：裁剪、缩放、双线性上采样。
    /// </summary>
    public static class TextureOps
    {
        /// <summary>Crop a region to a readable Texture2D. / 裁剪区域为可读 Texture2D。</summary>
        public static Texture2D Crop(Texture2D src, int x, int y, int w, int h)
        {
            var rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            Graphics.Blit(src, rt, new Vector2((float)w / src.width, (float)h / src.height),
                new Vector2((float)x / src.width, (float)y / src.height));
            var outTex = new Texture2D(w, h, TextureFormat.RGBA32, false);
            outTex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            outTex.Apply();
            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(rt);
            return outTex;
        }

        /// <summary>Scale a texture to a new size with bilinear filtering (readable result). / 双线性缩放到新尺寸（可读结果）。</summary>
        public static Texture2D Scale(Texture2D src, int w, int h)
        {
            if (w <= 0) w = 1;
            if (h <= 0) h = 1;
            var rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            rt.filterMode = FilterMode.Bilinear;
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            Graphics.Blit(src, rt);
            var outTex = new Texture2D(w, h, TextureFormat.RGBA32, false);
            outTex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            outTex.Apply();
            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(rt);
            return outTex;
        }

        /// <summary>
        /// Produce a candidate: crop region, downscale by <paramref name="scale"/>, upsample back to region size.
        /// 生成候选：裁剪区域 → 按 scale 缩小 → 上采样回区域尺寸。
        /// </summary>
        public static Texture2D MakeScaledCandidate(Texture2D src, Rect region, float scaleX, float scaleY)
        {
            int rw = Mathf.Max(1, Mathf.RoundToInt(region.width));
            int rh = Mathf.Max(1, Mathf.RoundToInt(region.height));
            var crop = Crop(src, (int)region.x, (int)region.y, rw, rh);
            int sw = Mathf.Max(1, Mathf.RoundToInt(rw * scaleX));
            int sh = Mathf.Max(1, Mathf.RoundToInt(rh * scaleY));
            var small = Scale(crop, sw, sh);
            var up = Scale(small, rw, rh);
            if (small != crop) Object.DestroyImmediate(small);
            Object.DestroyImmediate(crop);
            return up;
        }

        /// <summary>Is the region a single uniform color? / 区域是否为单一纯色？</summary>
        public static bool IsSolidColor(Texture2D tex, Rect region)
        {
            int x0 = Mathf.Clamp((int)region.x, 0, tex.width - 1);
            int y0 = Mathf.Clamp((int)region.y, 0, tex.height - 1);
            int x1 = Mathf.Clamp((int)(region.x + region.width) - 1, 0, tex.width - 1);
            int y1 = Mathf.Clamp((int)(region.y + region.height) - 1, 0, tex.height - 1);
            var first = tex.GetPixel(x0, y0);
            const int step = 3;
            for (int y = y0; y <= y1; y += step)
                for (int x = x0; x <= x1; x += step)
                    if (tex.GetPixel(x, y) != first) return false;
            return true;
        }
    }
}
