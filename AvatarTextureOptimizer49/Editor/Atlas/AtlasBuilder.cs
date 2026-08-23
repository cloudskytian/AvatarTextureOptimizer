using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;

namespace Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>Which textures an atlas hosts. / 图集承载的贴图种类。</summary>
    internal enum AtlasKind
    {
        Primary = 0,   // color / linear-color primary / 主色
        NormalAux = 1, // normal mirror / 法线镜像
        LinearAux = 2, // mask &amp; grayscale mirror / 蒙版与灰度镜像
    }

    /// <summary>A built atlas texture plus metadata for reporting and UV rewriting. / 生成的图集及其元数据。</summary>
    internal class BuiltAtlas
    {
        internal string Name;
        internal int Width, Height;
        internal TextureCategory FormatCategory;
        internal AtlasKind Kind;
        internal int MirrorDownscaleShift; // whole-mirror 2^-k / 镜像整体缩小
        internal Texture2D Texture;
        internal TypeGroup TypeGroup;
        internal AtlasLayout Layout;
        /// <summary>source textures composited into this atlas / 图集包含的源贴图</summary>
        internal readonly List<Texture2D> Sources = new List<Texture2D>();
        internal float Utilization;
        internal bool HasAlpha;
    }

    /// <summary>
    /// Composes atlas textures from packed layouts: island bytes are copied (with 90° rotation)
    /// into an RGBA buffer; aux mirrors may be uniformly downscaled by 2^k when every instance has
    /// quality headroom (identical normalized rects, so mesh UVs stay valid); empty space is
    /// filled with GPU pull-push bleed (infinite edge extension; transparent atlases keep alpha 0
    /// outside islands); then the texture is compressed with the platform-safe format and the
    /// mip+streaming switch. / 从装箱布局合成图集：岛字节拷贝（含90°旋转）→ 镜像按质量余量整体2^k缩小
    /// （归一化矩形不变，网格UV仍有效）→ GPU pull-push 无限外扩渗色（透明图集外部alpha保持0）→
    /// 平台安全格式压缩 + Mip/Streaming 开关。
    /// </summary>
    internal class AtlasBuilder
    {
        private readonly AtoSettings _settings;
        private readonly AtoPlatform _platform;
        private int _atlasCounter;

        internal readonly List<BuiltAtlas> Atlases = new List<BuiltAtlas>();
        internal readonly List<string> Warnings = new List<string>();

        internal AtlasBuilder(AtoSettings settings, AtoPlatform platform)
        {
            _settings = settings;
            _platform = platform;
        }

        /// <summary>Build the base atlas and the mirrors of one layout. / 构建一个布局的主图集与镜像图集。</summary>
        internal void Build(AtlasLayout layout,
            Dictionary<(UvGroup group, UvIsland island, Texture2D tex), IslandInstance> instances)
        {
            var primary = BuildOne(layout, AtlasKind.Primary, instances, 0);
            if (primary == null) return;
            Atlases.Add(primary);

            if (layout.TypeGroup.HasNormalAux)
            {
                int shift = ComputeMirrorDownscale(layout, AtlasKind.NormalAux, instances);
                var mirror = BuildOne(layout, AtlasKind.NormalAux, instances, shift);
                if (mirror != null) Atlases.Add(mirror);
            }

            if (layout.TypeGroup.HasLinearAux)
            {
                int shift = ComputeMirrorDownscale(layout, AtlasKind.LinearAux, instances);
                var mirror = BuildOne(layout, AtlasKind.LinearAux, instances, shift);
                if (mirror != null) Atlases.Add(mirror);
            }
        }

        /// <summary>
        /// Mirror downscale: largest 2^k such that every instance of the mirror still passes its
        /// own minimum scale, padding stays ≥ minimum, and the atlas stays ≥64px.
        /// / 镜像缩小：质量余量、最小padding与64px下限约束下的最大2^k。
        /// </summary>
        private int ComputeMirrorDownscale(AtlasLayout layout, AtlasKind kind,
            Dictionary<(UvGroup, UvIsland, Texture2D), IslandInstance> instances)
        {
            float minHeadroom = 1f;
            foreach (var p in layout.Placed)
            {
                foreach (var kv in p.Group.textures)
                {
                    if (!MatchesKind(kv.Value, kind)) continue;
                    if (!instances.TryGetValue((p.Group, p.Island, kv.Key), out var inst)) continue;
                    float lx = inst.finalW / (float)Mathf.Max(1, inst.region.width);
                    float ly = inst.finalH / (float)Mathf.Max(1, inst.region.height);
                    float hx = lx > 0f ? Mathf.Clamp01(inst.ownMinScaleX / lx) : 1f;
                    float hy = ly > 0f ? Mathf.Clamp01(inst.ownMinScaleY / ly) : 1f;
                    minHeadroom = Mathf.Min(minHeadroom, Mathf.Min(hx, hy));
                }
            }

            if (minHeadroom >= 0.4999f) return 0;

            int k = 0;
            while (k < 8 && Mathf.Pow(0.5f, k + 1) + 1e-4f <= minHeadroom) k++;

            int padding = Mathf.Max(_settings.minPadding, Mathf.CeilToInt(Mathf.Max(layout.Width, layout.Height) / 128f));
            while (k > 0 && ((layout.Width >> k) < 64 || (layout.Height >> k) < 64 || (padding >> k) < _settings.minPadding))
                k--;

            return Mathf.Max(0, k);
        }

        private static bool MatchesKind(TexCategory cat, AtlasKind kind)
        {
            switch (kind)
            {
                case AtlasKind.Primary:
                    return cat == TexCategory.Color || cat == TexCategory.LinearColor;
                case AtlasKind.NormalAux:
                    return cat == TexCategory.Normal;
                default:
                    return cat == TexCategory.Mask || cat == TexCategory.Grayscale;
            }
        }

        // ------------------------------------------------------------------ compose
        private BuiltAtlas BuildOne(AtlasLayout layout, AtlasKind kind,
            Dictionary<(UvGroup, UvIsland, Texture2D), IslandInstance> instances, int downscaleShift)
        {
            int w = layout.Width >> downscaleShift;
            int h = layout.Height >> downscaleShift;
            if (w < 4 || h < 4) return null;

            var buffer = new Color32[w * h];
            var coverage = new byte[w * h];
            bool anyContent = false, hasAlpha = false;
            var sources = new HashSet<Texture2D>();
            int islandCount = 0;

            foreach (var p in layout.Placed)
            {
                bool islandUsed = false;
                foreach (var kv in p.Group.textures.Where(t => MatchesKind(t.Value, kind)))
                {
                    if (!instances.TryGetValue((p.Group, p.Island, kv.Key), out var inst)) continue;
                    if (inst.atlasBytes == null) continue;
                    sources.Add(kv.Key);
                    islandUsed = true;
                    CopyIsland(buffer, coverage, w, h, p, inst, downscaleShift, ref hasAlpha);
                    anyContent = true;
                }
                if (islandUsed) islandCount++;
            }

            if (!anyContent) return null;

            // format category: primary color atlas decides opaque/transparent by content
            // 主色图集按内容判定不透明/透明；镜像分别使用法线/灰度格式
            var formatCat = kind switch
            {
                AtlasKind.NormalAux => TextureCategory.Normal,
                AtlasKind.LinearAux => TextureCategory.Grayscale,
                _ => hasAlpha ? TextureCategory.Transparent : TextureCategory.Opaque,
            };

            // ---- pull-push bleed (GPU) / GPU渗色无限外扩 ----
            var shader = Shader.Find("Hidden/ATO/Gfx");
            if (shader != null)
            {
                var alphaBackup = (Color32[])buffer.Clone();
                PullPushBleed(ref buffer, coverage, w, h, shader, formatCat == TextureCategory.Transparent || hasAlpha);
                if (formatCat == TextureCategory.Transparent || hasAlpha)
                {
                    // transparent atlases keep alpha 0 outside islands / 透明图集外部alpha保持0
                    for (int i = 0; i < buffer.Length; i++) buffer[i].a = alphaBackup[i].a;
                }
            }

            bool srgb = formatCat == TextureCategory.Opaque || formatCat == TextureCategory.Transparent;
            var userFormat = UserFormat(formatCat);
            bool singleChannel = formatCat == TextureCategory.Grayscale && IsSingleChannel(buffer);
            var format = TextureFormats.Resolve(userFormat, formatCat, _platform, hasAlpha,
                _settings.experimentalNpot, singleChannel, out var warning);
            if (warning != null) Warnings.Add($"{layout.TypeGroup.Key}/{kind}: {warning}");

            bool mip = MipEnabled(formatCat);
            var name = $"ATO_{layout.TypeGroup.KeyHash()}_{kind}" + (downscaleShift > 0 ? $"_m{downscaleShift}" : "") +
                       $"_{_atlasCounter++}";
            var tex = TextureFormats.BuildTexture(name, w, h, buffer, format, srgb, mip, _platform);

            var atlas = new BuiltAtlas
            {
                Name = name, Width = w, Height = h, FormatCategory = formatCat, Kind = kind,
                MirrorDownscaleShift = downscaleShift, Texture = tex, TypeGroup = layout.TypeGroup,
                Layout = layout, Sources = sources.ToList(), Utilization = layout.Utilization,
                HasAlpha = hasAlpha,
            };
            ATOLog.Info($"atlas '{name}': {w}x{h} {tex.format}, {islandCount} islands, " +
                        $"sources=[{string.Join(",", atlas.Sources.Select(s => s.name).Distinct().Take(8))}], " +
                        $"util={atlas.Utilization:P1}" + (downscaleShift > 0 ? $" mirror 2^-{downscaleShift}" : ""));
            return atlas;
        }

        private AtoFormat UserFormat(TextureCategory category) => category switch
        {
            TextureCategory.Opaque => _settings.opaqueFormat,
            TextureCategory.Transparent => _settings.transparentFormat,
            TextureCategory.Normal => _settings.normalFormat,
            TextureCategory.Grayscale => _settings.grayscaleFormat,
            _ => AtoFormat.Auto,
        };

        private bool MipEnabled(TextureCategory category) => category switch
        {
            TextureCategory.Opaque => _settings.opaqueMip,
            TextureCategory.Transparent => _settings.transparentMip,
            TextureCategory.Normal => _settings.normalMip,
            TextureCategory.Grayscale => _settings.grayscaleMip,
            _ => true,
        };

        /// <summary>Copy one island instance into the atlas buffer (with 90° rotation &amp; mirror downscale). / 拷贝岛实例（含旋转与镜像整体缩小）。</summary>
        private static void CopyIsland(Color32[] buffer, byte[] coverage, int atlasW, int atlasH,
            PlacedIsland p, IslandInstance inst, int downshift, ref bool hasAlpha)
        {
            int iw = Mathf.Max(1, inst.finalW >> downshift);
            int ih = Mathf.Max(1, inst.finalH >> downshift);
            int x0 = p.X >> downshift, y0 = p.Y >> downshift;

            // resample instance bytes when the mirror is downscaled / 镜像缩小时重采样实例
            Color32[] bytes = inst.atlasBytes;
            if (downshift > 0 && (iw != inst.finalW || ih != inst.finalH))
            {
                bytes = inst.storageCategory == TexCategory.Normal
                    ? NormalResampler.Downsample(inst.atlasBytes, inst.finalW, inst.finalH, iw, ih)
                    : QuickBilinear(inst.atlasBytes, inst.finalW, inst.finalH, iw, ih);
            }

            if (!p.Rotated90)
            {
                for (int y = 0; y < ih && y0 + y < atlasH; y++)
                {
                    int dstRow = (y0 + y) * atlasW + x0;
                    int srcW = bytes.Length / ih; // instance width / 实例宽
                    for (int x = 0; x < iw && x0 + x < atlasW; x++)
                    {
                        var c = bytes[y * srcW + x];
                        buffer[dstRow + x] = c;
                        coverage[dstRow + x] = 1;
                        if (c.a < 255) hasAlpha = true;
                    }
                }
            }
            else
            {
                // clockwise rotation: (ix,iy) → (X+iy, Y+(w-1-ix)) / 顺时针旋转拷贝
                for (int iy = 0; iy < ih; iy++)
                {
                    for (int ix = 0; ix < iw; ix++)
                    {
                        int dx = x0 + iy;
                        int dy = y0 + (iw - 1 - ix);
                        if (dx < 0 || dy < 0 || dx >= atlasW || dy >= atlasH) continue;
                        var c = bytes[iy * iw + ix];
                        buffer[dy * atlasW + dx] = c;
                        coverage[dy * atlasW + dx] = 1;
                        if (c.a < 255) hasAlpha = true;
                    }
                }
            }
        }

        private static Color32[] QuickBilinear(Color32[] src, int sw, int sh, int dw, int dh)
        {
            var dst = new Color32[dw * dh];
            float sx = sw / (float)dw, sy = sh / (float)dh;
            for (int y = 0; y < dh; y++)
            {
                float fy = (y + 0.5f) * sy - 0.5f;
                int y0 = Mathf.Clamp((int)fy, 0, sh - 1);
                int y1 = Mathf.Min(y0 + 1, sh - 1);
                float ty = Mathf.Clamp01(fy - y0);
                for (int x = 0; x < dw; x++)
                {
                    float fx = (x + 0.5f) * sx - 0.5f;
                    int x0 = Mathf.Clamp((int)fx, 0, sw - 1);
                    int x1 = Mathf.Min(x0 + 1, sw - 1);
                    float tx = Mathf.Clamp01(fx - x0);
                    dst[y * dw + x] = Lerp2(
                        Lerp2(src[y0 * sw + x0], src[y0 * sw + x1], tx),
                        Lerp2(src[y1 * sw + x0], src[y1 * sw + x1], tx), ty);
                }
            }
            return dst;

            static Color32 Lerp2(Color32 a, Color32 b, float t) => new Color32(
                (byte)(a.r + (b.r - a.r) * t),
                (byte)(a.g + (b.g - a.g) * t),
                (byte)(a.b + (b.b - a.b) * t),
                (byte)(a.a + (b.a - a.a) * t));
        }

        private static bool IsSingleChannel(Color32[] buffer)
        {
            for (int i = 0; i < buffer.Length; i += 3)
            {
                var c = buffer[i];
                if (c.r != c.g || c.g != c.b) return false;
            }
            return true;
        }

        // ------------------------------------------------------------------ pull-push
        /// <summary>
        /// GPU pull-push pyramid: colors pulled with coverage weights down to 1×1, then pushed
        /// back to fill every uncovered pixel (infinite edge extension). / GPU pull-push 金字塔：
        /// 按覆盖度下拉到1×1再上推填满所有空白（无限外扩）。
        /// </summary>
        private static void PullPushBleed(ref Color32[] buffer, byte[] coverage, int w, int h,
            Shader shader, bool hasAlphaContent)
        {
            var prev = RenderTexture.active;
            var mat = new Material(shader);
            var chain = new List<RenderTexture>();
            try
            {
                var rt0 = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGBHalf,
                    RenderTextureReadWrite.Linear);
                chain.Add(rt0);
                var payload = new Color[w * h];
                for (int i = 0; i < buffer.Length; i++)
                    payload[i] = new Color(buffer[i].r / 255f, buffer[i].g / 255f, buffer[i].b / 255f,
                        coverage[i] != 0 ? 1f : 0f);
                Upload(rt0, payload, w, h);

                // pull down: weighted average by coverage / 下拉
                int cw = w, ch = h;
                while (cw > 1 || ch > 1)
                {
                    int nw = Mathf.Max(1, cw / 2), nh = Mathf.Max(1, ch / 2);
                    var next = RenderTexture.GetTemporary(nw, nh, 0, RenderTextureFormat.ARGBHalf,
                        RenderTextureReadWrite.Linear);
                    Graphics.Blit(chain[chain.Count - 1], next, mat, 2);
                    chain.Add(next);
                    cw = nw; ch = nh;
                }

                // push up: fill uncovered pixels from the coarser level / 上推填补
                var quadMat = mat;
                for (int i = chain.Count - 2; i >= 0; i--)
                {
                    quadMat.SetTexture("_MainTex", chain[i]);
                    quadMat.SetTexture("_PrevTex", chain[i + 1]);
                    Graphics.SetRenderTarget(chain[i]);
                    GL.PushMatrix();
                    GL.LoadOrtho();
                    quadMat.SetPass(3);
                    GL.Begin(GL.QUADS);
                    GL.TexCoord2(0, 0); GL.Vertex3(0, 0, 0);
                    GL.TexCoord2(1, 0); GL.Vertex3(1, 0, 0);
                    GL.TexCoord2(1, 1); GL.Vertex3(1, 1, 0);
                    GL.TexCoord2(0, 1); GL.Vertex3(0, 1, 0);
                    GL.End();
                    GL.PopMatrix();
                }

                // read back / 读回
                RenderTexture.active = chain[0];
                var tmp = new Texture2D(w, h, TextureFormat.RGBAFloat, false, true);
                tmp.ReadPixels(new Rect(0, 0, w, h), 0, 0, false);
                tmp.Apply(false, false);
                var floats = tmp.GetPixels();
                UnityEngine.Object.DestroyImmediate(tmp);
                for (int i = 0; i < buffer.Length; i++)
                    buffer[i] = new Color32(
                        (byte)Mathf.Round(Mathf.Clamp01(floats[i].r) * 255f),
                        (byte)Mathf.Round(Mathf.Clamp01(floats[i].g) * 255f),
                        (byte)Mathf.Round(Mathf.Clamp01(floats[i].b) * 255f),
                        buffer[i].a); // alpha untouched / alpha不动
            }
            catch (Exception e)
            {
                ATOLog.Warning("pull-push bleed failed, empty areas stay flat: " + e.Message);
            }
            finally
            {
                RenderTexture.active = prev;
                foreach (var rt in chain) RenderTexture.ReleaseTemporary(rt);
                UnityEngine.Object.DestroyImmediate(mat);
            }
        }

        private static void Upload(RenderTexture rt, Color[] payload, int w, int h)
        {
            var tmp = new Texture2D(w, h, TextureFormat.RGBAFloat, false, true);
            tmp.SetPixels(payload);
            tmp.Apply(false, false);
            Graphics.Blit(tmp, rt);
            UnityEngine.Object.DestroyImmediate(tmp);
        }
    }

    internal static class TypeGroupExt
    {
        /// <summary>Short stable hash for atlas naming (ATO_ prefix added by builder). / 命名用短哈希。</summary>
        internal static string KeyHash(this TypeGroup tg)
        {
            uint hash = 2166136261;
            foreach (var c in tg.Key) hash = (hash ^ c) * 16777619;
            return (hash % 0xFFFF).ToString("X4");
        }
    }
}
