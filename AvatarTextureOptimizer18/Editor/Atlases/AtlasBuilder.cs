using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using Fosa.AvatarTextureOptimizer.Editor.Islands;
using Fosa.AvatarTextureOptimizer.Editor.Quality;

namespace Fosa.AvatarTextureOptimizer.Editor.Atlases
{
    // 图集构建器：把装箱结果写入成品图集资产。
    // - 内容采样：从线性预乘半精度池双线性采样（法线解码→重归一化→编码 xyz）；
    // - GPU pull-push（跳跃洪泛无限外扩）填满 padding 空白（透明贴图 alpha 保持 0）；
    // - 法线图集全图重归一化；
    // - 编码：去预乘 + sRGB（颜色类）→ PNG → 资产容器 + 导入设置（Read/Write 关、强制 Clamp、其余取最高质量）；
    // - 名称以 ATO_ 开头。
    // Atlas builder: writes packing results into finished atlas assets.
    internal static class AtlasBuilder
    {
        public static void Build(ATOContext ctx, ATOReport.Stage stage)
        {
            var cache = new TextureCache();
            try
            {
                // 加载全部需要的源贴图。Load all needed source textures.
                foreach (var e in ctx.islandEntities)
                {
                    if (e.noAtlasFallback || e.whitelistedFull || e.atlasId < 0) continue;
                    foreach (var u in e.uses)
                    {
                        if (u.texture != null && !cache.Has(u.texture))
                        {
                            cache.Load(u.texture, NeedsPremultiply(u.texture));
                        }
                    }
                }

                var planById = new Dictionary<int, Packing.AtlasPlan>();
                foreach (var plan in ctx.atlasPlans) planById[plan.id] = plan;

                foreach (var plan in ctx.atlasPlans)
                {
                    ctx.CheckCancelled();
                    BuildOne(ctx, plan, cache, planById, stage);
                }
            }
            finally
            {
                cache.Dispose();
            }
        }

        private static void BuildOne(ATOContext ctx, Packing.AtlasPlan plan, TextureCache cache,
            Dictionary<int, Packing.AtlasPlan> planById, ATOReport.Stage stage)
        {
            int w = plan.width, h = plan.height;
            bool sRGB = plan.kind != AtlasKind.Normal && plan.kind != AtlasKind.Grayscale;
            var buildTex = new Texture2D(w, h, TextureFormat.RGBAFloat, false, true);

            try
            {
                // 逐岛写入内容：岛属于同组全部类别图集（atlasId 仅记录锚定图集），
                // 按图集类别匹配写入对应贴图的使用（WriteIsland 内按 kind 匹配）。
                // Write island contents: islands belong to every kind's atlas of their set (atlasId records the anchor only);
                // WriteIsland matches the use by this plan's kind.
                foreach (var e in plan.islands)
                {
                    ctx.CheckCancelled();
                    WriteIsland(e, plan, cache, buildTex);
                }
                buildTex.Apply(false, false);

                // GPU pull-push 外扩（跳跃洪泛，无限外扩填满空白）。GPU pull-push dilation (jump flood).
                var dilated = AtlasGpu.DilateGpu(buildTex, w, h, plan.kind == AtlasKind.AlphaColor);

                // 法线重归一化。Renormalize normals.
                Texture2D encoded;
                if (plan.kind == AtlasKind.Normal)
                {
                    var normed = AtlasGpu.NormalizeGpu(dilated, w, h);
                    encoded = AtlasGpu.EncodeGpu(normed, w, h, false, false);
                    if (normed != dilated) Object.DestroyImmediate(normed);
                }
                else
                {
                    encoded = AtlasGpu.EncodeGpu(dilated, w, h, sRGB, true);
                }
                Object.DestroyImmediate(dilated);

                // PNG 落盘。Write PNG.
                var png = encoded.EncodeToPNG();
                Object.DestroyImmediate(encoded);

                string containerPath = AssetDatabase.GetAssetPath(ctx.ndmf.AssetContainer);
                string fileName = string.Format("{0}{1}_{2}.png", ATOConstants.AtlasNamePrefix, plan.kind, plan.id);
                string fullPath = Path.Combine(containerPath, fileName);
                File.WriteAllBytes(fullPath, png);
                AssetDatabase.ImportAsset(fullPath, ImportAssetOptions.ForceSynchronousImport);

                // 导入设置。Import settings.
                var importer = AssetImporter.GetAtPath(fullPath) as TextureImporter;
                if (importer != null) ConfigureImporter(ctx, importer, plan, sRGB);

                var atlasTex = AssetDatabase.LoadAssetAtPath<Texture2D>(fullPath);
                plan.texture = atlasTex;
                plan.assetPath = fullPath;

                // 记录岛使用级替换（同一贴图可被多个图集替换：颜色图集 + 法线图集）。
                // Record island-use-level replacements (one texture may be replaced by several atlases: color atlas + normal atlas).
                foreach (var e in plan.islands)
                {
                    foreach (var u in e.uses)
                    {
                        if (u.texture == null || u.whitelistLevel == Analysis.ATOWhitelistLevel.Full) continue;
                        if (UvGroups.UvGroupBuilder.ResolveKind(u) != plan.kind) continue;
                        u.replacementTexture = atlasTex;
                        u.replacementAtlas = plan;
                    }
                }

                long origBytes = EstimateOriginalBytes(plan);
                stage.AddLine(string.Format(ATOLocalization.Tr("log.atlasBuilt"),
                    plan.ToString(), plan.utilization * 100f, png.Length / 1024, origBytes / 1024));
                // 贴图来源明细（详细日志）。Source texture details (verbose log).
                var sources = new List<string>();
                foreach (var e in plan.islands)
                {
                    foreach (var u in e.uses)
                    {
                        if (u.texture == null || sources.Contains(u.texture.source.name)) continue;
                        if (UvGroups.UvGroupBuilder.ResolveKind(u) != plan.kind) continue;
                        sources.Add(u.texture.source.name);
                    }
                }
                stage.AddLine(string.Format(ATOLocalization.Tr("log.atlasSources"), plan.id, string.Join(", ", sources.ToArray())));
            }
            finally
            {
                Object.DestroyImmediate(buildTex);
            }
        }

        // 把岛的该类别内容写入图集（双线性采样自半精度池）。Writes an island's content for this atlas kind (bilinear from the half pool).
        private static void WriteIsland(IslandEntity e, Packing.AtlasPlan plan, TextureCache cache, Texture2D target)
        {
            // 找到该类别对应的使用。Find the use matching this atlas kind.
            IslandUse use = null;
            foreach (var u in e.uses)
            {
                if (u.texture == null) continue;
                if (UvGroups.UvGroupBuilder.ResolveKind(u) == plan.kind) { use = u; break; }
            }
            if (use == null) return;
            var entry = use.texture;
            var info = cache.Get(entry);

            int tw = entry.width, th = entry.height;
            // 局部尺寸（缩放后像素）。Local size in pixels (after scaling).
            int pw = Mathf.Max(1, Mathf.CeilToInt((e.uvMax.x - e.uvMin.x) * e.scaleX * tw));
            int ph = Mathf.Max(1, Mathf.CeilToInt((e.uvMax.y - e.uvMin.y) * e.scaleY * th));
            var localSize = new Vector2Int(pw, ph);
            var contentSize = IslandTransform.RotatedSize(localSize, e.rotation);
            var origin = new Vector2Int(e.rectPosPx.x + e.paddingPx, e.rectPosPx.y + e.paddingPx);

            int cw = Mathf.Min(contentSize.x, plan.width - origin.x);
            int ch = Mathf.Min(contentSize.y, plan.height - origin.y);
            if (cw <= 0 || ch <= 0) return;

            var colors = new Color[cw * ch];
            bool isNormal = plan.kind == AtlasKind.Normal;

            for (int y = 0; y < ch; y++)
            {
                for (int x = 0; x < cw; x++)
                {
                    var local = IslandTransform.ContentToLocal(new Vector2(x, y), localSize, e.rotation);
                    // 归一化源坐标。Normalized source coordinate.
                    float u = e.uvMin.x + (local.x / e.scaleX) / tw;
                    float v = e.uvMin.y + (local.y / e.scaleY) / th;
                    colors[y * cw + x] = SampleLinear(cache, info, u, v, tw, th, isNormal, entry);
                }
            }
            target.SetPixels(origin.x, origin.y, cw, ch, colors);
        }

        // 双线性采样（颜色：预乘线性；法线：解码后插值并重归一化；灰度：线性）。Bilinear sample.
        private static Color SampleLinear(TextureCache cache, TextureCache.EntryInfo info, float u, float v,
            int tw, int th, bool isNormal, Analysis.TextureEntry entry)
        {
            float px = u * tw - 0.5f;
            float py = v * th - 0.5f;
            int x0 = Mathf.Clamp(Mathf.FloorToInt(px), 0, tw - 1);
            int y0 = Mathf.Clamp(Mathf.FloorToInt(py), 0, th - 1);
            int x1 = Mathf.Clamp(x0 + 1, 0, tw - 1);
            int y1 = Mathf.Clamp(y0 + 1, 0, th - 1);
            float fx = Mathf.Clamp01(px - x0);
            float fy = Mathf.Clamp01(py - y0);

            if (isNormal)
            {
                var a = DecodeNormal(cache.Pool[info.offset + y0 * info.width + x0], info.dxt5nm);
                var b = DecodeNormal(cache.Pool[info.offset + y0 * info.width + x1], info.dxt5nm);
                var c = DecodeNormal(cache.Pool[info.offset + y1 * info.width + x0], info.dxt5nm);
                var d = DecodeNormal(cache.Pool[info.offset + y1 * info.width + x1], info.dxt5nm);
                var n = (a * (1 - fx) + b * fx) * (1 - fy) + (c * (1 - fx) + d * fx) * fy;
                n = n.normalized;
                return new Color(n.x * 0.5f + 0.5f, n.y * 0.5f + 0.5f, n.z * 0.5f + 0.5f, 1f);
            }

            var p00 = cache.Pool[info.offset + y0 * info.width + x0];
            var p10 = cache.Pool[info.offset + y0 * info.width + x1];
            var p01 = cache.Pool[info.offset + y1 * info.width + x0];
            var p11 = cache.Pool[info.offset + y1 * info.width + x1];
            float r = (p00.x * (1 - fx) + p10.x * fx) * (1 - fy) + (p01.x * (1 - fx) + p11.x * fx) * fy;
            float g = (p00.y * (1 - fx) + p10.y * fx) * (1 - fy) + (p01.y * (1 - fx) + p11.y * fx) * fy;
            float b2 = (p00.z * (1 - fx) + p10.z * fx) * (1 - fy) + (p01.z * (1 - fx) + p11.z * fx) * fy;
            float a2 = (p00.w * (1 - fx) + p10.w * fx) * (1 - fy) + (p01.w * (1 - fx) + p11.w * fx) * fy;
            return new Color(r, g, b2, a2);
        }

        private static Vector3 DecodeNormal(Unity.Mathematics.half4 p, bool dxt5nm)
        {
            return QualityMath.DecodeNormalByte(
                (byte)(p.x * 255f), (byte)(p.y * 255f), (byte)(p.z * 255f), (byte)(p.w * 255f), dxt5nm);
        }



        // 导入设置：Read/Write 关闭、强制 Clamp、mipmap/MipStreaming 绑定、格式按类别与平台、其余取最高质量。
        // Import settings: Read/Write off, Clamp forced, mipmap/streaming bound toggle, format per category & platform, best quality for the rest.
        private static void ConfigureImporter(ATOContext ctx, TextureImporter importer, Packing.AtlasPlan plan, bool sRGB)
        {
            var category = FormatResolver.ToCategory(plan.kind);
            var catSettings = FormatResolver.ForCategory(ctx.formats, category);

            importer.textureType = plan.kind == AtlasKind.Normal ? TextureImporterType.NormalMap : TextureImporterType.Default;
            importer.sRGBTexture = sRGB;
            importer.wrapMode = TextureWrapMode.Clamp;      // 强制 Clamp。Forced Clamp.
            importer.wrapModeU = TextureWrapMode.Clamp;
            importer.wrapModeV = TextureWrapMode.Clamp;
            importer.isReadable = false;                     // 强制关闭 Read/Write。Forced Read/Write off.
            importer.alphaIsTransparency = plan.kind == AtlasKind.AlphaColor;
            importer.npotScale = TextureImporterNPOTScale.ToLarger;
            importer.mipmapEnabled = catSettings.mipmaps;    // 绑定开关。Bound toggle.
            importer.streamingMipmaps = catSettings.mipmaps; // VRChat：开启 Mipmap 必须开启 MipStreaming。
            importer.filterMode = HighestFilter(ctx);
            importer.anisoLevel = HighestAniso(ctx);

            // 灰度多通道判定。Gray multi-channel detection.
            bool grayMulti = plan.kind == AtlasKind.Grayscale && AnyMultiChannelGray(plan);

            // 默认（全部平台）+ 三平台覆盖。Default (all platforms) + three platform overrides.
            var defaultPlatform = new TextureImporterPlatformSettings
            {
                name = "Default",
                overridden = false,
                maxTextureSize = plan.width,
                format = TextureImporterFormat.Automatic,
                textureCompression = TextureImporterCompression.Compressed
            };
            importer.SetPlatformTextureSettings(defaultPlatform);

            foreach (var platform in new[] { ATOPlatform.PC, ATOPlatform.Android, ATOPlatform.iOS })
            {
                var ov = ctx.settings.FindOverride(platform);
                var formats = ov != null && ov.enabled ? ov.formats : ctx.formats;
                var cat = FormatResolver.ForCategory(formats, category);
                string warning;
                var fmt = FormatResolver.Resolve(cat, category, platform,
                    ctx.settings.ResolveNpotAtlases(platform), plan.kind == AtlasKind.AlphaColor, grayMulti, out warning);
                if (!string.IsNullOrEmpty(warning)) ATOLog.Warn(warning);

                var ps = new TextureImporterPlatformSettings
                {
                    name = ATOPlatformUtil.ToImporterPlatformName(platform),
                    overridden = true,
                    maxTextureSize = ResolveMaxSize(platform, plan.width),
                    format = fmt,
                    textureCompression = TextureImporterCompression.Compressed,
                    crunchedCompression = false,
                    compressionQuality = 100
                };
                importer.SetPlatformTextureSettings(ps);
            }

            importer.SaveAndReimport();
        }

        private static int ResolveMaxSize(ATOPlatform platform, int atlasSize)
        {
            int max = platform != ATOPlatform.PC ? ATOConstants.MaxAtlasSizeMobile : ATOConstants.MaxAtlasSizeDesktop;
            return Mathf.Min(atlasSize, max);
        }

        private static FilterMode HighestFilter(ATOContext ctx)
        {
            var best = FilterMode.Bilinear;
            foreach (var e in ctx.islandEntities)
            {
                foreach (var u in e.uses)
                {
                    if (u.texture != null && u.texture.filterMode > best) best = u.texture.filterMode;
                }
            }
            return best;
        }

        private static int HighestAniso(ATOContext ctx)
        {
            int best = 1;
            foreach (var e in ctx.islandEntities)
            {
                foreach (var u in e.uses)
                {
                    if (u.texture != null && u.texture.anisoLevel > best) best = u.texture.anisoLevel;
                }
            }
            return best;
        }

        private static bool AnyMultiChannelGray(Packing.AtlasPlan plan)
        {
            foreach (var e in plan.islands)
            {
                foreach (var u in e.uses)
                {
                    if (u.texture == null) continue;
                    if (u.texture.usedChannels != 0 && (u.texture.usedChannels & (u.texture.usedChannels - 1)) != 0)
                    {
                        return true; // 多个通道被使用。Multiple channels used.
                    }
                    if ((u.texture.usedChannels & ~1) != 0) return true; // 非 R 通道。Non-R channel.
                }
            }
            return false;
        }

        private static bool NeedsPremultiply(Analysis.TextureEntry entry)
        {
            return entry.kind == Analysis.ATOTextureKind.Color
                && (entry.worstAlphaMode == Analysis.ATOAlphaMode.Cutout || entry.worstAlphaMode == Analysis.ATOAlphaMode.Blend);
        }

        // 估算原始体积（参与图集的贴图，按图集覆盖比例估算）。Estimated original bytes (proportional to atlas coverage).
        private static long EstimateOriginalBytes(Packing.AtlasPlan plan)
        {
            long total = 0;
            var counted = new HashSet<Analysis.TextureEntry>();
            foreach (var e in plan.islands)
            {
                foreach (var u in e.uses)
                {
                    if (u.texture == null || counted.Contains(u.texture)) continue;
                    counted.Add(u.texture);
                    total += u.texture.originalByteSize;
                }
            }
            return total;
        }
    }
}
