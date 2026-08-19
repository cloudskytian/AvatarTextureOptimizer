// ============================================================================
// AvatarTextureOptimizer (net.fosa.avatar-texture-optimizer)
// Packing/AtlasBuilder.cs — 图集构建 / Atlas texture building
//
// 需求: 将缩放后的岛内容写入图集；边缘做 GPU pull-push（无限外扩）填充空白；
//       透明贴图 alpha 保持 0；法线贴图数据原样拷贝（绝不重算切线，也不做颜色变换）。
// 共识: 岛内容经 GPU 双线性重采样（sRGB 族在线性空间过滤后回写 sRGB）；
//       近无损(尺寸不变)时直接拷贝像素，不做任何重采样。
// ============================================================================
using System.Collections.Generic;
using UnityEngine;
using UnityEditor;

namespace net.fosa.avatar_texture_optimizer.editor
{
    /// <summary>
    /// 图集构建器 / Atlas builder.
    /// </summary>
    public static class AtlasBuilder
    {
        /// <summary>
        /// 构建全部图集 / Build all atlases.
        /// </summary>
        public static void BuildAll(PackOutcome outcome, TextureDecodeCache cache, int paddingOption)
        {
            int index = 0;
            foreach (var family in outcome.families.Values)
            {
                foreach (var atlas in family.atlases)
                {
                    Cancel.Checkpoint();
                    BuildAtlas(atlas, cache, index++);
                }
            }
        }

        private static void BuildAtlas(AtlasResult atlas, TextureDecodeCache cache, int index)
        {
            bool srgb = atlas.family.sRGB;
            int size = atlas.width;

            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false, linear: !srgb);
            tex.name = $"ATO_{index}_{atlas.family.role}";
            tex.hideFlags = HideFlags.HideAndDontSave;

            // 清空为透明黑 / clear to transparent black
            var clear = new Color32[size * size];
            for (int i = 0; i < clear.Length; i++) clear[i] = new Color32(0, 0, 0, 0);
            tex.SetPixels32(clear);
            tex.Apply(false, false);

            long srcPixels = 0;

            // 内容写入 / write content
            foreach (var kv in atlas.content)
            {
                var tref = kv.Key;
                if (tref.source == null) continue;
                var islands = kv.Value;
                var raw = cache.GetRawPixels(tref.source, tref.sRGB);
                int texW = tref.source.width, texH = tref.source.height;

                foreach (var island in islands)
                {
                    int x0 = Mathf.Clamp((int)Mathf.Floor(island.uvMin.x * texW), 0, texW - 1);
                    int y0 = Mathf.Clamp((int)Mathf.Floor(island.uvMin.y * texH), 0, texH - 1);
                    int x1 = Mathf.Clamp((int)Mathf.Ceil(island.uvMax.x * texW), x0 + 1, texW);
                    int y1 = Mathf.Clamp((int)Mathf.Ceil(island.uvMax.y * texH), y0 + 1, texH);
                    int rw = x1 - x0, rh = y1 - y0;
                    srcPixels += (long)rw * rh;

                    int cw = island.finalW, ch = island.finalH;
                    Color32[] regionPixels = ExtractRegion(raw, texW, x0, y0, rw, rh);
                    Color32[] content;

                    if (cw == rw && ch == rh)
                    {
                        // 近无损: 原样拷贝 / near-lossless: copy as-is
                        content = regionPixels;
                    }
                    else
                    {
                        content = ResampleRegion(regionPixels, rw, rh, cw, ch, srgb);
                    }

                    if (island.rotated)
                    {
                        content = Rotate90CW(content, cw, ch);
                        cw = island.finalH;
                        ch = island.finalW;
                    }

                    tex.SetPixels32(island.finalRect.x, island.finalRect.y, cw, ch, content);
                }
            }
            tex.Apply(false, false);

            // GPU pull-push 外扩 / GPU pull-push fill
            Texture2D filled = PullPushFill(tex, srgb, atlas);
            if (filled != null)
            {
                Object.DestroyImmediate(tex);
                tex = filled;
            }
            else
            {
                Log.Warning($"Pull-push fill failed for atlas '{tex.name}'; leaving transparent margins (safe fallback).");
            }

            // 透明族: 岛外 alpha 置 0；不透明族: 全 alpha 255 /
            // transparent: zero alpha outside island rects; opaque: alpha = 255 everywhere
            FixAlpha(tex, atlas);

            atlas.texture = tex;
            atlas.targetPixels = (long)size * size;
            atlas.sourcePixels = srcPixels;

            Log.VerboseLog($"atlas '{tex.name}' built: {size}x{size}, utilization {atlas.utilization:P1}, " +
                           $"islands {atlas.islands.Count}, sources {atlas.sources.Count}");
        }

        private static Color32[] ExtractRegion(Color32[] raw, int texW, int x0, int y0, int rw, int rh)
        {
            var pixels = new Color32[rw * rh];
            for (int y = 0; y < rh; y++)
            {
                for (int x = 0; x < rw; x++)
                {
                    pixels[y * rw + x] = raw[(y0 + y) * texW + (x0 + x)];
                }
            }
            return pixels;
        }

        /// <summary>
        /// GPU 双线性重采样区域（sRGB 族线性空间过滤；结果与源编码一致）/
        /// GPU bilinear region resample (sRGB family filters in linear space; output matches source encoding).
        /// </summary>
        internal static Color32[] ResampleRegion(Color32[] src, int sw, int sh, int dw, int dh, bool srgb)
        {
            var tmp = new Texture2D(sw, sh, TextureFormat.RGBA32, false, linear: !srgb);
            tmp.SetPixels32(src);
            tmp.Apply(false, false);
            tmp.hideFlags = HideFlags.HideAndDontSave;

            try
            {
                var rt = new RenderTexture(dw, dh, 0, RenderTextureFormat.ARGB32,
                    srgb ? RenderTextureReadWrite.sRGB : RenderTextureReadWrite.Linear);
                rt.Create();
                var prevRT = RenderTexture.active;
                var prevSRGB = GL.sRGBWrite;
                try
                {
                    GL.sRGBWrite = srgb;
                    Graphics.Blit(tmp, rt);
                    var outTex = new Texture2D(dw, dh, TextureFormat.RGBA32, false, linear: !srgb);
                    RenderTexture.active = rt;
                    outTex.ReadPixels(new Rect(0, 0, dw, dh), 0, 0, false);
                    outTex.Apply(false, false);
                    var pixels = outTex.GetPixels32();
                    Object.DestroyImmediate(outTex);
                    return pixels;
                }
                finally
                {
                    GL.sRGBWrite = prevSRGB;
                    RenderTexture.active = prevRT;
                    rt.Release();
                    Object.DestroyImmediate(rt);
                }
            }
            finally
            {
                Object.DestroyImmediate(tmp);
            }
        }

        /// <summary>顺时针旋转 90°（内容；法线族不会走到这里） / rotate 90° CW</summary>
        private static Color32[] Rotate90CW(Color32[] src, int w, int h)
        {
            var dst = new Color32[src.Length];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    // (x,y) → 新坐标 (h-1-y, x) / new coords (h-1-y, x) with dims h×w
                    dst[x * h + (h - 1 - y)] = src[y * w + x];
                }
            }
            return dst;
        }

        /// <summary>
        /// GPU pull-push 填充（push: 步长倍增膨胀；pull: 模糊平滑）/
        /// GPU pull-push fill (push: doubling-step dilation; pull: blur).
        /// </summary>
        private static Texture2D PullPushFill(Texture2D src, bool srgb, AtlasResult atlas)
        {
            var shader = Shader.Find("Hidden/ATO/PullPush");
            if (shader == null) return null;
            var mat = new Material(shader);
            mat.hideFlags = HideFlags.HideAndDontSave;

            int size = src.width;
            var prevRT = RenderTexture.active;
            var prevSRGB = GL.sRGBWrite;

            RenderTexture rtA = null, rtB = null;
            try
            {
                rtA = new RenderTexture(size, size, 0, RenderTextureFormat.ARGB32,
                    srgb ? RenderTextureReadWrite.sRGB : RenderTextureReadWrite.Linear);
                rtA.Create();
                rtB = new RenderTexture(size, size, 0, RenderTextureFormat.ARGB32,
                    srgb ? RenderTextureReadWrite.sRGB : RenderTextureReadWrite.Linear);
                rtB.Create();

                GL.sRGBWrite = srgb;
                Graphics.Blit(src, rtA);

                // Push: 步长 1,2,4,... 覆盖全尺寸 / push with doubling steps to cover full size
                int passes = 0;
                int step = 1;
                while (step < size) { step <<= 1; passes++; }
                mat.SetFloat("_Step", 1f);
                for (int i = 0; i < passes; i++)
                {
                    mat.SetFloat("_Step", (float)(1 << i));
                    Graphics.Blit(rtA, rtB, mat, 0);
                    Graphics.Blit(rtB, rtA, mat, 0);
                    // 每步两次膨胀，传播更远 / two dilations per step for farther propagation
                }

                // Pull: 模糊 3 次 / blur a few times
                for (int i = 0; i < 3; i++)
                {
                    Graphics.Blit(rtA, rtB, mat, 1);
                    Graphics.Blit(rtB, rtA, mat, 1);
                }

                var outTex = new Texture2D(size, size, TextureFormat.RGBA32, false, linear: !srgb);
                RenderTexture.active = rtA;
                outTex.ReadPixels(new Rect(0, 0, size, size), 0, 0, false);
                outTex.Apply(false, false);
                outTex.hideFlags = HideFlags.HideAndDontSave;
                return outTex;
            }
            finally
            {
                GL.sRGBWrite = prevSRGB;
                RenderTexture.active = prevRT;
                if (rtA != null) { rtA.Release(); Object.DestroyImmediate(rtA); }
                if (rtB != null) { rtB.Release(); Object.DestroyImmediate(rtB); }
                Object.DestroyImmediate(mat);
            }
        }

        /// <summary>透明族岛外 alpha 置 0；不透明族 alpha 全 255 / alpha fixup</summary>
        private static void FixAlpha(Texture2D tex, AtlasResult atlas)
        {
            var pixels = tex.GetPixels32();
            bool opaque = atlas.family.category != TextureCategory.Transparent && !FamilyHasAlpha(atlas);

            if (opaque)
            {
                for (int i = 0; i < pixels.Length; i++) pixels[i].a = 255;
                tex.SetPixels32(pixels);
                tex.Apply(false, false);
                return;
            }

            // 透明族: 岛矩形外 alpha 置 0 / transparent: zero alpha outside island rects
            int size = tex.width;
            var rects = new List<RectInt>();
            foreach (var island in atlas.islands)
            {
                rects.Add(island.finalRect);
            }

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    bool inside = false;
                    foreach (var r in rects)
                    {
                        if (x >= r.x && x < r.x + r.width && y >= r.y && y < r.y + r.height)
                        {
                            inside = true;
                            break;
                        }
                    }
                    if (!inside) pixels[y * size + x].a = 0;
                }
            }
            tex.SetPixels32(pixels);
            tex.Apply(false, false);
        }

        private static bool FamilyHasAlpha(AtlasResult atlas)
        {
            foreach (var kv in atlas.content)
            {
                if (kv.Key.hasAlpha) return true;
            }
            return false;
        }
    }
}
