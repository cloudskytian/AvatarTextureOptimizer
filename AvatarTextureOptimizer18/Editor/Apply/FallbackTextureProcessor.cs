using System.IO;
using UnityEditor;
using UnityEngine;
using Fosa.AvatarTextureOptimizer.Editor.Atlases;
using Fosa.AvatarTextureOptimizer.Editor.Quality;

namespace Fosa.AvatarTextureOptimizer.Editor.Apply
{
    // Fallback 贴图处理器：非图集化贴图（无图集模式 / NoAtlas / 装箱失败）的整图缩放与导入参数优化副本。
    // Fallback texture processor: whole-texture scaling and import-optimized copies for non-atlased textures.
    //
    // 策略：
    // - wholeTextureScale < 1 → GPU 双线性整图缩放（线性预乘域）→ 新资产 + 优化导入设置；
    // - wholeTextureScale == 1 且导入参数有变化 → 从源贴图重编码 PNG 副本 + 优化导入设置（绝不修改用户源资产）；
    // - 无变化 → 保持原贴图。
    // Strategy: scaled → GPU bilinear whole resize + optimized import settings; unchanged-but-import-differs →
    // re-encoded PNG copy + optimized settings (user assets are never modified); no diff → keep original.
    internal static class FallbackTextureProcessor
    {
        public static void Process(ATOContext ctx, ATOReport.Stage stage)
        {
            int scaled = 0, copied = 0;
            var cache = new TextureCache();
            try
            {
                foreach (var entry in ctx.textures)
                {
                    ctx.CheckCancelled();
                    if (entry.dedupTarget != null) continue;             // 去重后引用规范条目。Dedup → canonical only.
                    if (entry.whitelistLevel == Analysis.ATOWhitelistLevel.Full) continue;
                    if (entry.replacementTexture != null) continue;      // 已图集化。Already atlased.
                    if (!NeedsProcessing(ctx, entry)) continue;

                    cache.Load(entry, NeedsPremultiply(entry));

                    float scale = entry.wholeTextureScale;
                    bool wantMips = FormatResolver.ForCategory(ctx.formats, ResolveCategory(entry)).mipmaps;

                    if (scale < 1f - 1e-4f)
                    {
                        // 整图缩放。Whole-texture scaling.
                        var tex = BuildScaled(ctx, entry, cache, scale);
                        if (tex != null)
                        {
                            entry.replacementTexture = tex;
                            entry.wholeTextureScaled = true;
                            scaled++;
                        }
                    }
                    else if (ImportDiffers(ctx, entry, wantMips))
                    {
                        // 导入参数副本。Import-optimized copy.
                        var tex = BuildCopy(ctx, entry, cache);
                        if (tex != null)
                        {
                            entry.replacementTexture = tex;
                            copied++;
                        }
                    }
                }
                stage.AddLine(string.Format(ATOLocalization.Tr("log.fallbackSummary"), scaled, copied));
            }
            finally
            {
                cache.Dispose();
            }
        }

        // 是否需要处理：无图集模式全部；或 NoAtlas；或有装箱失败岛。Whether processing is needed.
        private static bool NeedsProcessing(ATOContext ctx, Analysis.TextureEntry entry)
        {
            if (!ctx.settings.generateAtlas) return true;
            if (entry.whitelistLevel == Analysis.ATOWhitelistLevel.NoAtlas) return true;
            foreach (var use in entry.uses)
            {
                if (use.slot == null) continue;
                // 使用所在岛是否装箱失败。Whether any using island gave up atlasing.
                foreach (var e in ctx.islandEntities)
                {
                    if (e.mesh != use.slot.mesh || e.uvChannel != use.uvChannel || e.submesh != use.slot.slotIndex) continue;
                    if (e.noAtlasFallback) return true;
                }
            }
            return false;
        }

        private static ATOTextureCategory ResolveCategory(Analysis.TextureEntry entry)
        {
            switch (entry.kind)
            {
                case Analysis.ATOTextureKind.NormalMap: return ATOTextureCategory.NormalMap;
                case Analysis.ATOTextureKind.Grayscale:
                case Analysis.ATOTextureKind.Mask: return ATOTextureCategory.Grayscale;
                default:
                    return (entry.worstAlphaMode == Analysis.ATOAlphaMode.Cutout || entry.worstAlphaMode == Analysis.ATOAlphaMode.Blend)
                        ? ATOTextureCategory.AlphaColor
                        : ATOTextureCategory.OpaqueColor;
            }
        }

        // GPU 整图缩放（线性预乘域双线性）。GPU whole-texture scaling (bilinear in the linear premultiplied domain).
        private static Texture2D BuildScaled(ATOContext ctx, Analysis.TextureEntry entry, TextureCache cache, float scale)
        {
            var info = cache.Get(entry);
            int w = entry.width, h = entry.height;
            int nw = Mathf.Max(4, Mathf.RoundToInt(w * scale));
            int nh = Mathf.Max(4, Mathf.RoundToInt(h * scale));
            if (nw >= w && nh >= h) return null;

            var src = new Texture2D(w, h, TextureFormat.RGBAFloat, false, true);
            try
            {
                var colors = new Color[w * h];
                bool isNormal = entry.kind == Analysis.ATOTextureKind.NormalMap;
                for (int i = 0; i < w * h; i++)
                {
                    var p = cache.Pool[info.offset + i];
                    if (isNormal && entry.dxt5nm)
                    {
                        // DXT5nm → xyz 解码后再缩放。Decode DXT5nm → xyz before scaling.
                        var n = QualityMath.DecodeNormalByte((byte)(p.x * 255f), (byte)(p.y * 255f), (byte)(p.z * 255f), (byte)(p.w * 255f), true);
                        colors[i] = new Color(n.x * 0.5f + 0.5f, n.y * 0.5f + 0.5f, n.z * 0.5f + 0.5f, 1f);
                    }
                    else
                    {
                        colors[i] = new Color(p.x, p.y, p.z, p.w);
                    }
                }
                src.SetPixels(colors);
                src.Apply(false, false);

                var rt = RenderTexture.GetTemporary(nw, nh, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);
                try
                {
                    Graphics.Blit(src, rt);
                    var small = new Texture2D(nw, nh, TextureFormat.RGBAFloat, false, true);
                    var prev = RenderTexture.active;
                    RenderTexture.active = rt;
                    small.ReadPixels(new Rect(0, 0, nw, nh), 0, 0);
                    small.Apply(false, false);
                    RenderTexture.active = prev;

                    bool sRGB = entry.sRGB && entry.kind != Analysis.ATOTextureKind.NormalMap && entry.kind != Analysis.ATOTextureKind.Grayscale;
                    bool alpha = entry.worstAlphaMode == Analysis.ATOAlphaMode.Cutout || entry.worstAlphaMode == Analysis.ATOAlphaMode.Blend;
                    var encoded = Atlases.AtlasGpu.EncodeGpu(small, nw, nh, sRGB, alpha || sRGB);
                    Object.DestroyImmediate(small);
                    return SaveAndConfigure(ctx, entry, encoded, nw, nh);
                }
                finally
                {
                    RenderTexture.ReleaseTemporary(rt);
                }
            }
            finally
            {
                Object.DestroyImmediate(src);
            }
        }

        // 导入参数副本（不缩放）。Import-optimized copy (no scaling).
        private static Texture2D BuildCopy(ATOContext ctx, Analysis.TextureEntry entry, TextureCache cache)
        {
            var info = cache.Get(entry);
            int w = entry.width, h = entry.height;
            bool sRGB = entry.sRGB && entry.kind != Analysis.ATOTextureKind.NormalMap && entry.kind != Analysis.ATOTextureKind.Grayscale;
            bool alpha = entry.worstAlphaMode == Analysis.ATOAlphaMode.Cutout || entry.worstAlphaMode == Analysis.ATOAlphaMode.Blend;

            var encoded = new Texture2D(w, h, TextureFormat.RGBA32, false, false);
            try
            {
                var bytes = new Color32[w * h];
                bool isNormal = entry.kind == Analysis.ATOTextureKind.NormalMap;
                for (int i = 0; i < w * h; i++)
                {
                    var p = cache.Pool[info.offset + i];
                    if (isNormal && entry.dxt5nm)
                    {
                        // DXT5nm → xyz 解码后再编码。Decode DXT5nm → xyz before encoding.
                        var n = QualityMath.DecodeNormalByte((byte)(p.x * 255f), (byte)(p.y * 255f), (byte)(p.z * 255f), (byte)(p.w * 255f), true);
                        bytes[i] = new Color32(
                            QualityMath.LinearToSrgbByte(n.x * 0.5f + 0.5f),
                            QualityMath.LinearToSrgbByte(n.y * 0.5f + 0.5f),
                            QualityMath.LinearToSrgbByte(n.z * 0.5f + 0.5f), 255);
                        continue;
                    }
                    float r = p.x, g = p.y, b = p.z, a = p.w;
                    if (sRGB)
                    {
                        if (a > 1e-4f) { r /= a; g /= a; b /= a; }
                        r = QualityMath.LinearToSrgbByte(r) / 255f;
                        g = QualityMath.LinearToSrgbByte(g) / 255f;
                        b = QualityMath.LinearToSrgbByte(b) / 255f;
                    }
                    bytes[i] = new Color32((byte)(r * 255f), (byte)(g * 255f), (byte)(b * 255f), (byte)(a * 255f));
                }
                encoded.SetPixels32(bytes);
                encoded.Apply(false, false);
                return SaveAndConfigure(ctx, entry, encoded, w, h);
            }
            finally
            {
                Object.DestroyImmediate(encoded);
            }
        }

        // 保存 PNG + 导入设置 + 注册替换。Save PNG + import settings + register replacement.
        private static Texture2D SaveAndConfigure(ATOContext ctx, Analysis.TextureEntry entry, Texture2D encoded, int w, int h)
        {
            var png = encoded.EncodeToPNG();
            string containerPath = AssetDatabase.GetAssetPath(ctx.ndmf.AssetContainer);
            string fileName = string.Format("{0}fallback_{1}_{2}.png", ATOConstants.AtlasNamePrefix,
                entry.source != null ? entry.source.name : "tex", entry.assetGuid.Substring(0, Mathf.Min(8, entry.assetGuid.Length)));
            string fullPath = Path.Combine(containerPath, fileName);
            File.WriteAllBytes(fullPath, png);
            AssetDatabase.ImportAsset(fullPath, ImportAssetOptions.ForceSynchronousImport);

            var importer = AssetImporter.GetAtPath(fullPath) as TextureImporter;
            if (importer != null)
            {
                var category = ResolveCategory(entry);
                var catSettings = FormatResolver.ForCategory(ctx.formats, category);
                bool sRGB = entry.sRGB && entry.kind != Analysis.ATOTextureKind.NormalMap && entry.kind != Analysis.ATOTextureKind.Grayscale;
                importer.textureType = entry.kind == Analysis.ATOTextureKind.NormalMap ? TextureImporterType.NormalMap : TextureImporterType.Default;
                importer.sRGBTexture = sRGB;
                importer.wrapMode = TextureWrapMode.Clamp;
                importer.wrapModeU = TextureWrapMode.Clamp;
                importer.wrapModeV = TextureWrapMode.Clamp;
                importer.isReadable = false;
                importer.alphaIsTransparency = category == ATOTextureCategory.AlphaColor;
                importer.npotScale = TextureImporterNPOTScale.ToLarger;
                importer.mipmapEnabled = catSettings.mipmaps;
                importer.streamingMipmaps = catSettings.mipmaps;
                importer.filterMode = entry.filterMode;
                importer.anisoLevel = entry.anisoLevel;

                bool grayMulti = (entry.kind == Analysis.ATOTextureKind.Grayscale || entry.kind == Analysis.ATOTextureKind.Mask)
                    && (entry.usedChannels & ~1) != 0;
                bool hasAlpha = category == ATOTextureCategory.AlphaColor;

                var def = new TextureImporterPlatformSettings
                {
                    name = "Default",
                    overridden = false,
                    maxTextureSize = Mathf.Max(w, h),
                    format = TextureImporterFormat.Automatic,
                    textureCompression = TextureImporterCompression.Compressed
                };
                importer.SetPlatformTextureSettings(def);

                foreach (var platform in new[] { ATOPlatform.PC, ATOPlatform.Android, ATOPlatform.iOS })
                {
                    var ov = ctx.settings.FindOverride(platform);
                    var formats = ov != null && ov.enabled ? ov.formats : ctx.formats;
                    var cat = FormatResolver.ForCategory(formats, category);
                    string warning;
                    var fmt = FormatResolver.Resolve(cat, category, platform,
                        ctx.settings.ResolveNpotAtlases(platform), hasAlpha, grayMulti, out warning);
                    if (!string.IsNullOrEmpty(warning)) ATOLog.Warn(warning);
                    var ps = new TextureImporterPlatformSettings
                    {
                        name = ATOPlatformUtil.ToImporterPlatformName(platform),
                        overridden = true,
                        maxTextureSize = Mathf.Max(w, h),
                        format = fmt,
                        textureCompression = TextureImporterCompression.Compressed,
                        crunchedCompression = false,
                        compressionQuality = 100
                    };
                    importer.SetPlatformTextureSettings(ps);
                }
                importer.SaveAndReimport();
            }

            var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(fullPath);
            ctx.ndmf.ObjectRegistry.RegisterReplacedObject(entry.source, tex);
            return tex;
        }

        // 导入参数是否有差异（需要副本）：mipmap/streaming 绑定、强制 Clamp/Read/Write 关、格式（Auto 或平台覆盖）。
        // Whether import settings differ (a copy is needed): mipmap/streaming toggle, forced Clamp/Read-Write-off, formats.
        private static bool ImportDiffers(ATOContext ctx, Analysis.TextureEntry entry, bool wantMips)
        {
            if (entry.mipmapEnabled != wantMips) return true;
            if (entry.streamingMipmaps != wantMips) return true;
            if (entry.wrapU != TextureWrapMode.Clamp || entry.wrapV != TextureWrapMode.Clamp) return true;
            if (entry.readable) return true;

            var importer = AssetImporter.GetAtPath(entry.assetPath) as TextureImporter;
            if (importer == null) return true; // 非资产贴图（运行时生成）→ 必须副本化。Procedural textures must be copied.

            var category = ResolveCategory(entry);
            foreach (var platform in new[] { ATOPlatform.PC, ATOPlatform.Android, ATOPlatform.iOS })
            {
                var ov = ctx.settings.FindOverride(platform);
                var formats = ov != null && ov.enabled ? ov.formats : ctx.formats;
                var cat = FormatResolver.ForCategory(formats, category);
                if (cat.format == ATOCompressionFormat.Auto && (ov == null || !ov.enabled)) continue;
                string warning;
                var fmt = FormatResolver.Resolve(cat, category, platform, false,
                    category == ATOTextureCategory.AlphaColor, false, out warning);
                var ps = importer.GetPlatformTextureSettings(ATOPlatformUtil.ToImporterPlatformName(platform));
                if (!ps.overridden || ps.format != fmt) return true;
            }
            return false;
        }

        private static bool NeedsPremultiply(Analysis.TextureEntry entry)
        {
            return entry.kind == Analysis.ATOTextureKind.Color
                && (entry.worstAlphaMode == Analysis.ATOAlphaMode.Cutout || entry.worstAlphaMode == Analysis.ATOAlphaMode.Blend);
        }
    }
}
