// Stage5_Bake — atlas compositing, pull-push fill, PNG export, importer setup / 图集合成、外扩填充、PNG 导出、导入器配置
// Pull-push ("infinite" bleed) fills blank atlas areas from island edges; transparent planes keep
// alpha=0 (content tracked by written-mask, not alpha). Atlases: read/write off, forced Clamp, mips
// bound to streaming (VRC rule). Safe format enums per category+platform with content-based fallback.<br>
// Pull-push 无限外扩填充空白（透明平面 alpha 保持0，内容以写入掩码区分）；图集关 Read/Write、强制 Clamp、
// Mipmap 与 Streaming 绑定（VRC 规则）；按分类+平台提供安全压缩格式枚举并按内容兜底。
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using nadena.dev.ndmf;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Fosa.ATO.Editor
{
    internal static class Stage5_Bake
    {
        internal const string TempDir = "Assets/ATO_Generated";   // kept on cancel per spec / 取消时保留

        internal static void Run(BuildContext ctx, ATOPipeContext pipe, StageProgress progress)
        {
            EnsureFolder(TempDir);
            int ai = 0;
            foreach (var atlas in pipe.atlases)
            {
                ai++;
                pipe.CancelCheck(progress, ATOL10n.T("ato.stage.bake"), (float)ai / pipe.atlases.Count);
                BakeAtlas(ctx, pipe, atlas, ai);
            }
            ATOLog.Info(ATOL10n.T("ato.log.bake_done", pipe.atlases.Count));
            ATOEvents.Raise("bake", pipe, ctx.AvatarRootObject);
            ATOHookRegistry.Notify("bake", pipe);
        }

        // ---------------------------------------------------------------- one atlas
        private static void BakeAtlas(BuildContext ctx, ATOPipeContext pipe, AtlasDef atlas, int index)
        {
            // classes present = union of classes of entries' textures / 出现的类型集合
            var classes = new SortedSet<int>();
            foreach (var e in atlas.entries)
                foreach (var c in e.tex.classes) classes.Add((int)c);
            if (classes.Count == 0) return;

            // plane scales: albedo baseline 1.0; others may shrink if their needs are lower / 平面缩放
            var planeScale = new Dictionary<TexClass, float>();
            foreach (var c in classes.Select(i => (TexClass)i))
            {
                float k = 1f;
                if (c != TexClass.Albedo)
                {
                    float need = 0f;
                    foreach (var e in atlas.entries)
                    {
                        if (!e.tex.classes.Contains(c)) continue;
                        if (e.island.unifiedSize.x <= 0 || e.island.perTextureTarget == null) continue;
                        if (e.island.perTextureTarget.TryGetValue(e.tex, out var t))
                        {
                            need = Mathf.Max(need,
                                Mathf.Max(t.x / (float)Mathf.Max(1, e.island.unifiedSize.x), t.y / (float)Mathf.Max(1, e.island.unifiedSize.y)));
                        }
                    }
                    float floorS = atlas.padding > 0 ? pipe.settings.minPadding / (float)atlas.padding : 1f;
                    k = Mathf.Clamp(need <= 0 ? 1f : need, Mathf.Min(1f, floorS), 1f);
                    if (k < 0.999f) ATOLog.V($"atlas#{index} plane {c} downscaled ×{k:F2}");
                }
                planeScale[c] = k;
            }

            foreach (var cls in classes.Select(i => (TexClass)i))
            {
                var plane = BakePlane(pipe, atlas, cls, planeScale[cls], index);
                if (plane == null) continue;
                atlas.planes[cls] = plane;
                foreach (var e in atlas.entries)
                    if (e.tex.classes.Contains(cls))
                        pipe.atlasPlaneOf[(e.tex, cls)] = plane;
            }
        }

        private static AtlasDef.PlaneOut BakePlane(ATOPipeContext pipe, AtlasDef atlas, TexClass cls, float scale, int index)
        {
            int pw = Mathf.Max(64, Mathf.RoundToInt(atlas.width * scale));
            int ph = Mathf.Max(64, Mathf.RoundToInt(atlas.height * scale));
            var entries = atlas.entries.Where(e => e.tex.classes.Contains(cls)).ToList();
            if (entries.Count == 0) return null;

            var pixels = new Color32[pw * ph];
            var written = new bool[pw * ph];
            long sourceBytes = 0;
            var sources = new HashSet<TextureInfo>();

            foreach (var e in entries)
            {
                sources.Add(e.tex);
                var raw = ImageCache.GetRaw(e.tex.source, e.tex.sRGB, out int sw, out int sh);
                if (raw == null) continue;
                var bbox = Stage3_Quality.BboxPx(e.island, e.tex);
                var dst = new RectInt(
                    Mathf.RoundToInt(e.rect.x * scale), Mathf.RoundToInt(e.rect.y * scale),
                    Mathf.Max(1, Mathf.RoundToInt(e.rect.width * scale)), Mathf.Max(1, Mathf.RoundToInt(e.rect.height * scale)));
                dst.x = Mathf.Clamp(dst.x, 0, pw - 1); dst.y = Mathf.Clamp(dst.y, 0, ph - 1);
                dst.width = Mathf.Clamp(dst.width, 1, pw - dst.x);
                dst.height = Mathf.Clamp(dst.height, 1, ph - dst.y);
                CopyIsland(raw, sw, sh, bbox, pixels, written, pw, ph, dst, e.rotated);
            }

            sourceBytes = sources.Sum(x => x.ApproxBytes);
            // pull-push fill from written edges; transparent areas keep alpha 0 / pull-push 外扩；空白 alpha 保持0
            PullPushFill(pixels, written, pw, ph);

            var tex = new Texture2D(pw, ph, TextureFormat.RGBA32, mipChain: true, linear: cls != TexClass.Albedo) { name = null };
            tex.SetPixels32(pixels);
            tex.Apply(true);

            // content-driven alpha detection (safety floor) / 基于实像素内容判定 alpha（安全兜底）
            bool contentHasAlpha = entries.Any(e => e.tex.classes.Contains(TexClass.Albedo))
                ? ScanMinAlpha01(pixels) < 1f
                : cls == TexClass.Albedo && ScanMinAlpha01(pixels) < 1f;

            string fname = $"ATO_Atlas_{index}_{cls}_{pw}x{ph}.png";
            string path = $"{TempDir}/{fname}";
            File.WriteAllBytes(path, tex.EncodeToPNG());
            Object.DestroyImmediate(tex);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            var outTex = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            if (outTex == null) { ATOLog.Error("failed to reimport " + path); return null; }

            ConfigureImporter(pipe, path, cls, atlas, entries, contentHasAlpha, pw, ph);

            ATOLog.V($"atlas[{index}] {cls} {pw}x{ph} entries={entries.Count} sources={sources.Count} alpha={contentHasAlpha}");
            return new AtlasDef.PlaneOut { cls = cls, scale = Vector2.one * scale, hasAlpha = contentHasAlpha, texture = outTex, assetPath = path, sourceBytes = sourceBytes };
        }

        // ---------------------------------------------------------------- island copy (bilinear)
        private static void CopyIsland(Color32[] src, int sw, int sh, RectInt bbox,
            Color32[] dst, bool[] written, int dw, int dh, RectInt rect, bool rotated)
        {
            for (int y = 0; y < rect.height; y++)
            {
                for (int x = 0; x < rect.width; x++)
                {
                    float u = (x + 0.5f) / rect.width, v = (y + 0.5f) / rect.height;
                    if (rotated) (u, v) = (v, u); // transpose = 90° rotation / 转置即90°旋转
                    float fx = (bbox.x + u * bbox.width - 0.5f);
                    float fy = (bbox.y + v * bbox.height - 0.5f);
                    int x0 = Mathf.Clamp(Mathf.FloorToInt(fx), 0, sw - 1), y0 = Mathf.Clamp(Mathf.FloorToInt(fy), 0, sh - 1);
                    int x1 = Mathf.Min(x0 + 1, sw - 1), y1 = Mathf.Min(y0 + 1, sh - 1);
                    float tx = Mathf.Clamp01(fx - x0), ty = Mathf.Clamp01(fy - y0);
                    var c00 = src[y0 * sw + x0]; var c10 = src[y0 * sw + x1];
                    var c01 = src[y1 * sw + x0]; var c11 = src[y1 * sw + x1];
                    dst[(rect.y + y) * dw + (rect.x + x)] = new Color32(
                        Lerp8(Lerp8(c00.r, c10.r, tx), Lerp8(c01.r, c11.r, tx), ty),
                        Lerp8(Lerp8(c00.g, c10.g, tx), Lerp8(c01.g, c11.g, tx), ty),
                        Lerp8(Lerp8(c00.b, c10.b, tx), Lerp8(c01.b, c11.b, tx), ty),
                        Lerp8(Lerp8(c00.a, c10.a, tx), Lerp8(c01.a, c11.a, tx), ty));
                    written[(rect.y + y) * dw + (rect.x + x)] = true;
                }
            }
        }

        private static byte Lerp8(int a, int b, float t) => (byte)(a + (b - a) * t);

        // ---------------------------------------------------------------- pull-push
        /// <summary>CPU pull-push approximating GPU infinite bleed (mip pyramid average). / CPU 版 pull-push。</summary>
        private static void PullPushFill(Color32[] px, bool[] written, int w, int h)
        {
            // build pyramid of "color sums + counts", then fills empty cells top-down / 自上而下回填
            var levels = new List<(int w, int h, double[] sum, int[] cnt)>();
            levels.Add((w, h, ToSum(px, written, w, h), ToCnt(written)));
            while (levels[^1].w > 1 || levels[^1].h > 1)
            {
                var (lw, lh, lsum, lcnt) = levels[^1];
                int nw = Mathf.Max(1, lw / 2), nh = Mathf.Max(1, lh / 2);
                var sum = new double[nw * nh * 4]; var cnt = new int[nw * nh];
                for (int y = 0; y < nh; y++)
                for (int x = 0; x < nw; x++)
                {
                    for (int dy = 0; dy < 2; dy++)
                    for (int dx = 0; dx < 2; dx++)
                    {
                        int sx = Mathf.Min(x * 2 + dx, lw - 1), sy = Mathf.Min(y * 2 + dy, lh - 1);
                        int si = sy * lw + sx, di = y * nw + x;
                        if (lcnt[si] <= 0) continue;
                        for (int c = 0; c < 4; c++) sum[di * 4 + c] += lsum[si * 4 + c];
                        cnt[di] += lcnt[si];
                    }
                }
                levels.Add((nw, nh, sum, cnt));
            }
            // push down: fill empty from parents / 向下回填
            for (int li = levels.Count - 1; li > 0; li--)
            {
                var (pw0, ph0, sumP, cntP) = levels[li];
                var (cw0, ch0, sumC, cntC) = levels[li - 1];
                for (int y = 0; y < ch0; y++)
                for (int x = 0; x < cw0; x++)
                {
                    int ci = y * cw0 + x;
                    if (cntC[ci] > 0) continue;
                    int pi = Mathf.Min(y / 2, ph0 - 1) * pw0 + Mathf.Min(x / 2, pw0 - 1);
                    if (cntP[pi] <= 0) continue;
                    for (int c = 0; c < 4; c++) sumC[ci * 4 + c] = sumP[pi * 4 + c] / cntP[pi];
                    cntC[ci] = 1;
                }
                levels[li - 1] = (cw0, ch0, sumC, cntC);
            }
            var (fw, fh, fsum, fcnt) = levels[0];
            for (int i = 0; i < w * h && i < fcnt.Length; i++)
            {
                if (written[i]) continue;
                if (fcnt[i] <= 0) continue; // no data anywhere (never sampled) / 全空：保持透明黑
                byte a = px[i].a;
                px[i] = new Color32(
                    (byte)Mathf.Clamp((int)fsum[i * 4 + 0] / fcnt[i], 0, 255),
                    (byte)Mathf.Clamp((int)fsum[i * 4 + 1] / fcnt[i], 0, 255),
                    (byte)Mathf.Clamp((int)fsum[i * 4 + 2] / fcnt[i], 0, 255),
                    a); // alpha stays 0 (transparent planes) / alpha 保持0
            }
        }

        private static double[] ToSum(Color32[] px, bool[] written, int w, int h)
        {
            var sum = new double[w * h * 4];
            for (int i = 0; i < w * h; i++)
                if (written[i])
                {
                    sum[i * 4] = px[i].r; sum[i * 4 + 1] = px[i].g; sum[i * 4 + 2] = px[i].b; sum[i * 4 + 3] = px[i].a;
                }
            return sum;
        }
        private static int[] ToCnt(bool[] written) { var c = new int[written.Length]; for (int i = 0; i < written.Length; i++) c[i] = written[i] ? 1 : 0; return c; }

        private static float ScanMinAlpha01(Color32[] px)
        {
            int min = 255; // sampled (1/16 stride is enough for the decision) / 抽样判定
            for (int i = 0; i < px.Length; i += 16) if (px[i].a < min) min = px[i].a;
            return min / 255f;
        }

        // ---------------------------------------------------------------- importer
        private static void ConfigureImporter(ATOPipeContext pipe, string path, TexClass cls, AtlasDef atlas,
            List<AtlasDef.Entry> entries, bool contentHasAlpha, int pw, int ph)
        {
            if (!(AssetImporter.GetAtPath(path) is TextureImporter imp)) return;
            imp.textureType = cls == TexClass.Normal ? TextureImporterType.NormalMap : TextureImporterType.Default;
            imp.sRGBTexture = cls == TexClass.Albedo;
            imp.alphaIsTransparency = contentHasAlpha && cls == TexClass.Albedo;
            imp.mipmapEnabled = MipsEnabled(pipe.settings, cls);          // VRC rule: mips ⇔ streaming bound / 绑定
            imp.streamingMipmaps = imp.mipmapEnabled;
            imp.wrapMode = TextureWrapMode.Clamp;                          // atlas forced Clamp (not user-editable) / 强制Clamp不开放
            imp.filterMode = (FilterMode)Mathf.Max(1, entries.Max(e => (int)e.tex.filterMode)); // highest quality among sources / 取最高质量
            if (entries.Max(e => (int)e.tex.filterMode) >= 2) imp.filterMode = FilterMode.Trilinear;
            imp.isReadable = false;                                        // atlases default off, not user-editable / 默认关闭不开放
            imp.maxTextureSize = Mathf.Max(64, Mathf.NextPowerOfTwo(Mathf.Max(pw, ph)));
            imp.textureCompression = TextureImporterCompression.Compressed; // real format set per platform below / 格式由各平台项决定

            foreach (ATOPlatform p in Enum.GetValues(typeof(ATOPlatform)))
            {
                var ov = pipe.settings.Override(p);
                var choice = FormatChoiceFor(pipe.settings, ov, cls, contentHasAlpha);
                var fmt = ResolveFormat(choice, cls, p, contentHasAlpha, pipe);
                var name = p == ATOPlatform.PC ? "Standalone" : p == ATOPlatform.Android ? "Android" : "iPhone";
                var tps = new TextureImporterPlatformSettings
                {
                    name = name,
                    overridden = true,
                    maxTextureSize = Mathf.Min(imp.maxTextureSize, MaxPlatformSize(pipe.settings, p)),
                    format = fmt,
                    textureCompression = TextureImporterCompression.Compressed,
                    compressionQuality = 50,
                    crunchedCompression = false,
                    allowsAlphaSplitting = false,
                    androidETC2FallbackOverride = AndroidETC2FallbackOverride.UseBuildSettings,
                    overriddenForcedToCompress = false,
                };
                imp.SetPlatformTextureSettings(tps);
            }
            imp.SaveAndReimport();
        }

        private static bool MipsEnabled(ATOSettingsSnap s, TexClass cls)
        {
            if (s.mips == null) return true;
            return cls switch
            {
                TexClass.Albedo => s.mips.albedo,
                TexClass.Normal => s.mips.normal,
                _ => s.mips.mask,
            };
        }

        private static int MaxPlatformSize(ATOSettingsSnap s, ATOPlatform p)
        {
            var ov = s.Override(p);
            return ov != null && ov.enabled ? ov.maxAtlasSize : (p == ATOPlatform.PC ? AvatarTextureOptimizer.MaxAtlasSizePC : AvatarTextureOptimizer.MaxAtlasSizeMobile);
        }

        /// <summary>User format choice for category (with platform override). / 分类格式选项（含平台 override）。</summary>
        private static ATOFormatChoice FormatChoiceFor(ATOSettingsSnap s, ATOPlatformOverride ov, TexClass cls, bool hasAlpha)
        {
            if (ov == null || !ov.enabled) return ATOFormatChoice.Auto;
            var c = cls switch
            {
                TexClass.Albedo => hasAlpha ? ov.albedoAlpha : ov.albedoOpaque,
                TexClass.Normal => ov.normal,
                _ => ov.mask,
            };
            return c;
        }

        /// <summary>
        /// Resolve safe TextureImporterFormat; content-based fallbacks (alpha & multi-channel) with
        /// NDMF console warnings. NPOT: iOS PVRTC never offered (we expose none).<br/>
        /// 解析安全格式，含内容兜底；单通道选项遇到多通道内容时回退RGBA并在 NDMF 控制台告警。
        /// </summary>
        private static TextureImporterFormat ResolveFormat(ATOFormatChoice choice, TexClass cls, ATOPlatform platform, bool hasAlpha, ATOPipeContext pipe)
        {
            bool desktop = platform == ATOPlatform.PC;
            TextureImporterFormat Wanted() => choice switch
            {
                ATOFormatChoice.DXT1 => desktop ? TextureImporterFormat.DXT1 : TextureImporterFormat.ETC2_RGB4,
                ATOFormatChoice.DXT5 => desktop ? TextureImporterFormat.DXT5 : TextureImporterFormat.ETC2_RGBA8,
                ATOFormatChoice.BC7 => desktop ? TextureImporterFormat.BC7 : TextureImporterFormat.ASTC_6x6,
                ATOFormatChoice.BC5 => desktop ? TextureImporterFormat.BC5 : TextureImporterFormat.ASTC_6x6,
                ATOFormatChoice.BC4 => desktop ? TextureImporterFormat.BC4 : TextureImporterFormat.ETC2_RGB4,
                ATOFormatChoice.RGBA32 => TextureImporterFormat.RGBA32,
                ATOFormatChoice.RGB32 => hasAlpha ? TextureImporterFormat.RGBA32 : TextureImporterFormat.RGB24,
                ATOFormatChoice.R8 => TextureImporterFormat.R8,
                ATOFormatChoice.ASTC_6x6 => desktop ? TextureImporterFormat.BC7 : TextureImporterFormat.ASTC_6x6,
                ATOFormatChoice.ETC2_RGB4 => desktop ? TextureImporterFormat.DXT1 : TextureImporterFormat.ETC2_RGB4,
                ATOFormatChoice.ETC2_RGBA8 => desktop ? TextureImporterFormat.DXT5 : TextureImporterFormat.ETC2_RGBA8,
                _ => AutoDefault(),
            };
            TextureImporterFormat AutoDefault() => cls switch
            {
                TexClass.Normal => desktop ? TextureImporterFormat.DXT5 : TextureImporterFormat.ASTC_6x6,
                TexClass.Mask => desktop ? TextureImporterFormat.BC7 : TextureImporterFormat.ASTC_6x6,
                _ => hasAlpha
                    ? (desktop ? TextureImporterFormat.BC7 : TextureImporterFormat.ASTC_6x6)
                    : (desktop ? TextureImporterFormat.DXT1 : TextureImporterFormat.ASTC_6x6),
            };
            var fmt = Wanted();

            // safety fallback 1: content has alpha but format lacks alpha channel / 兜底1：有透明内容但格式无alpha
            if (hasAlpha && !FormatHasAlpha(fmt))
            {
                ErrorReport.ReportError(ATOL10n.L, ErrorSeverity.NonFatal, "ato.err.format_alpha", fmt.ToString());
                fmt = desktop ? TextureImporterFormat.BC7 : TextureImporterFormat.ASTC_6x6;
            }
            // safety fallback 2: single-channel choice but multi-channel mask content / 兜底2：单通道选项但多通道内容
            if (cls == TexClass.Mask && (fmt == TextureImporterFormat.R8 || fmt == TextureImporterFormat.BC4) && MaskIsMultiChannel(pipe))
            {
                ErrorReport.ReportError(ATOL10n.L, ErrorSeverity.NonFatal, "ato.err.format_mask_channels");
                fmt = desktop ? TextureImporterFormat.BC7 : TextureImporterFormat.ASTC_6x6;
            }
            return fmt;
        }

        private static bool MaskIsMultiChannel(ATOPipeContext pipe)
        {
            int flags = 0;
            foreach (var kv in pipe.slotRefs)
                foreach (var r in kv.Value)
                    if (r.cls == TexClass.Mask) flags |= r.maskChannelMask;
            int n = 0;
            for (int c = 0; c < 4; c++) if ((flags & (1 << c)) != 0) n++;
            return n > 1 || flags == 0;
        }

        private static bool FormatHasAlpha(TextureImporterFormat f) => f switch
        {
            TextureImporterFormat.DXT5 => true, TextureImporterFormat.BC7 => true,
            TextureImporterFormat.ASTC_6x6 => true, TextureImporterFormat.ETC2_RGBA8 => true,
            TextureImporterFormat.RGBA32 => true, TextureImporterFormat.ARGB32 => true,
            TextureImporterFormat.RGBAHalf => true, TextureImporterFormat.RGBAFloat => true,
            _ => false,
        };

        // ---- shared pixel math helpers (used by whole-texture stage) / 共享像素运算辅助 ----
        internal static Vector4 Px(Color32 c, bool premult)
        {
            var v = new Vector4(c.r / 255f, c.g / 255f, c.b / 255f, c.a / 255f);
            if (premult) { v.x *= v.w; v.y *= v.w; v.z *= v.w; }
            return v;
        }
        internal static Color32 Unpx(Vector4 v, bool premult)
        {
            if (premult && v.w > 1e-5f) { v.x /= v.w; v.y /= v.w; v.z /= v.w; }
            return new Color32(
                (byte)Mathf.Clamp(Mathf.RoundToInt(v.x * 255f), 0, 255),
                (byte)Mathf.Clamp(Mathf.RoundToInt(v.y * 255f), 0, 255),
                (byte)Mathf.Clamp(Mathf.RoundToInt(v.z * 255f), 0, 255),
                (byte)Mathf.Clamp(Mathf.RoundToInt(v.w * 255f), 0, 255));
        }
        internal static Vector4 Lerp4(Vector4 a, Vector4 b, float t) => a + (b - a) * t;
        internal static Vector4 Renormalize(Vector4 v)
        {
            var x = v.x * 2f - 1f; var y = v.y * 2f - 1f; var z = v.z * 2f - 1f;
            var l = Mathf.Sqrt(x * x + y * y + z * z);
            if (l > 1e-5f) { x /= l; y /= l; z /= l; }
            return new Vector4(x * 0.5f + 0.5f, y * 0.5f + 0.5f, z * 0.5f + 0.5f, v.w);
        }

        internal static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            var parent = Path.GetDirectoryName(path)?.Replace('\\', '/');
            if (!string.IsNullOrEmpty(parent) && !AssetDatabase.IsValidFolder(parent)) Directory.CreateDirectory(parent);
            if (!AssetDatabase.IsValidFolder(path)) AssetDatabase.CreateFolder(parent ?? "Assets", Path.GetFileName(path));
        }
    }
}
