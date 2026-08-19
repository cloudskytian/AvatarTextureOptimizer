using System;
using System.Collections.Generic;
using System.Linq;
using NetFosa.AvatarTextureOptimizer.Editor.Analysis;
using NetFosa.AvatarTextureOptimizer.Editor.Atlas;
using NetFosa.AvatarTextureOptimizer.Editor.i18n;
using NetFosa.AvatarTextureOptimizer.Editor.Logging;
using NetFosa.AvatarTextureOptimizer.Editor.Processing;
using NetFosa.AvatarTextureOptimizer.Editor.Quality;
using NetFosa.AvatarTextureOptimizer.Editor.UV;
using NetFosa.AvatarTextureOptimizer.Editor.Utils;
using UnityEditor;
using UnityEngine;
using NetFosa.AvatarTextureOptimizer;

namespace NetFosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// 主流程编排（MA 后、AAO 前执行）：
    /// 校验 → 扫描/动画/映射 → 岛提取 → 冲突检测 → 质量缩放 → 图集/整图缩放 →
    /// 生成贴图与压缩 → UV 重写(AAO 撤离) → 材质/动画修补 → 后置去重与槽合并 → 移除自身 → 报告。
    /// </summary>
    public static class BuildPipeline
    {
        public static void Execute(GameObject avatarRoot, ATOLogger logger)
        {
            // ---------- 0. 组件校验 ----------
            var components = avatarRoot.GetComponentsInChildren<AvatarTextureOptimizer>(true);
            if (components == null || components.Length == 0)
            {
                logger.Info("No AvatarTextureOptimizer component on this avatar; skipping.");
                return;
            }
            if (components.Length > 1)
            {
                throw new InvalidOperationException(
                    "[ATO] More than one AvatarTextureOptimizer component found on the avatar (including children). Only one is allowed. Aborting bake.");
            }
            var component = components[0];

            var descriptor = avatarRoot.GetComponent("VRC.SDKBase.VRC_AvatarDescriptor");
            if (descriptor == null)
            {
                throw new InvalidOperationException(
                    "[ATO] The object with AvatarTextureOptimizer must also carry a VRC_AvatarDescriptor. Aborting bake.");
            }

            var settingsError = component.ValidateSettings();
            if (!string.IsNullOrEmpty(settingsError))
            {
                throw new InvalidOperationException($"[ATO] Invalid settings: {settingsError}");
            }

            var settings = EffectiveSettings.Resolve(component, PlatformResolver.CurrentPlatform);
            logger.Verbose = settings.verboseLogging;

            var report = new BuildReport();
            using var progress = new ProgressScope("Avatar Texture Optimizer", logger);
            using var ctx = new ATOContext(avatarRoot, component, settings, logger, report);

            var sw = new System.Diagnostics.Stopwatch();
            sw.Start();
            logger.Info($"=== AvatarTextureOptimizer build started (platform {settings.platform}) ===");

            try
            {
                // ---------- 1. 动画分析 ----------
                progress.Report(0.02f, Localized("ato.progress.animations"));
                ctx.Animation = new AnimationAnalyzer(avatarRoot).Analyze();

                // ---------- 2. 扫描 ----------
                progress.Report(0.05f, Localized("ato.progress.analyze"));
                ctx.Scanner = new AvatarScanner(avatarRoot, ctx.Animation);
                ctx.Scanner.Scan();

                // ---------- 3. 映射（UV ↔ 贴图） ----------
                progress.Report(0.08f, Localized("ato.progress.mapping"));
                ctx.Mapping = new TextureMappingBuilder(avatarRoot, ctx.Scanner, ctx.Animation, ctx.Cache,
                    logger, component.whitelist);
                ctx.Mapping.Build();

                // ---------- 4. UV 岛提取 ----------
                var groups = ctx.Mapping.UvGroups;
                foreach (var g in groups)
                {
                    if (g.failed) continue;
                    if (g.noAtlas) continue;
                    g.islands = UvIslandExtractor.Extract(g, logger);
                }
                report.IslandsProcessed = groups.Sum(g => g.islands?.Count ?? 0);

                // ---------- 5. 顶点冲突检测（装箱前） ----------
                var rewriter = new MeshUvRewriter(groups, logger);
                rewriter.DetectVertexConflicts();

                // ---------- 6. 质量缩放 ----------
                progress.Report(0.2f, Localized("ato.progress.scaling"));
                var evaluator = new QualityEvaluator(ctx.Cache, settings.useGPU, logger);
                var scaler = new UvScaler(evaluator, settings, ctx.Animation, logger, report);
                scaler.ScaleAll(groups);

                // ---------- 7. 图集构建 ----------
                progress.Report(0.45f, Localized("ato.progress.atlas"));
                var pool = new CandidatePool(settings.npotEnabled, settings.platform != ATOPlatform.PC,
                    minSide: 64);
                var atlasBuilder = new AtlasBuilder(settings, evaluator, ctx.Cache, logger, report, pool, settings.useBurst);
                var atlasResult = atlasBuilder.Build(groups, ctx.Mapping.TypeGroups);

                // ---------- 8. 生成贴图（图集 + 整图缩放） ----------
                progress.Report(0.6f, Localized("ato.progress.write"));
                var replaceMap = new Dictionary<Texture, Texture>();
                using (var writer = new AtlasWriter(ctx.RtPool, settings.useGPU, logger))
                {
                    // 图集
                    foreach (var a in atlasResult.Atlases)
                    {
                        progress.ThrowIfCancelled();
                        var tex2d = writer.WriteAtlas(a, a.islandTextures, ctx.Cache);
                        if (tex2d == null) continue;

                        string path = AssetSaver.NextAssetPath(".png");
                        var asset = AssetSaver.SaveTexture(tex2d, path, logger);
                        UnityEngine.Object.DestroyImmediate(tex2d);
                        if (asset == null) continue;

                        bool hasAlpha = a.category == ATOTextureCategory.MainTransparent ||
                                        a.islandTextures.Values.Any(t => t.hasAlpha);
                        CompressionApplier.Apply(path, asset.width, asset.height, a.category, a.colorSpace,
                            a.typeGroup != null ? a.typeGroup.filterMode : ATOFilterMode.Bilinear,
                            hasAlpha, settings.npotEnabled, settings, report);

                        foreach (var kv in a.islandTextures)
                        {
                            var oldTex = kv.Value.texture;
                            if (oldTex != null && !replaceMap.ContainsKey(oldTex))
                            {
                                replaceMap[oldTex] = asset;
                            }
                        }

                        // 报告
                        report.Atlases.Add(new BuildReport.AtlasEntry
                        {
                            name = asset.name,
                            width = asset.width,
                            height = asset.height,
                            islandCount = a.placements.Count,
                            utilization = a.totalCells > 0 ? (float)a.usedCells / a.totalCells : 0f,
                            sources = a.sources,
                            category = a.category,
                        });
                        report.TexelsOut += (long)asset.width * asset.height;
                        logger.Info($"Atlas '{asset.name}' {asset.width}x{asset.height} generated ({a.placements.Count} islands).");
                    }

                    // 整图缩放
                    foreach (var kv in atlasResult.WholeTextureScales)
                    {
                        progress.ThrowIfCancelled();
                        var info = kv.Key;
                        if (info == null || info.texture == null) continue;
                        if (info.EffectiveWhitelistLevel == ATOWhitelistLevel.Full) continue;
                        if (kv.Value >= 1f - 1e-4f)
                        {
                            // 无需缩放：仍保持原贴图（但导入参数优化？白名单外的贴图默认全平台参数优化——
                            // 本版本聚焦几何优化；导入参数优化对原资产不执行，避免改用户资产）
                            continue;
                        }
                        var newTex = ScaleWholeTexture(info, kv.Value, settings, writer, ctx, report);
                        if (newTex != null && !replaceMap.ContainsKey(info.texture))
                        {
                            replaceMap[info.texture] = newTex;
                        }
                    }
                }

                report.TexturesOut = replaceMap.Count + atlasResult.UntouchedWhitelist.Count;
                report.TexturesIn = ctx.Mapping.AllTextures.Count(t => t.dedupTarget == null);
                report.WhitelistedTextures = atlasResult.UntouchedWhitelist.Count;
                foreach (var t in ctx.Mapping.AllTextures)
                {
                    if (t.dedupTarget == null && t.texture != null)
                        report.TexelsIn += (long)t.texture.width * t.texture.height;
                }

                // ---------- 9. UV 重写（含 AAO 撤离） ----------
                progress.Report(0.8f, Localized("ato.progress.write"));
                rewriter.Rewrite(evacuateAAO: true);

                // ---------- 10. 材质与动画修补 ----------
                progress.Report(0.9f, Localized("ato.progress.patch"));
                var patcher = new AnimationPatcher(logger);
                BuildPathCache(avatarRoot, patcher);
                AssignMaterials(avatarRoot, replaceMap, patcher);
                patcher.PatchAll(ctx.Animation.Clips);

                // ---------- 11. 后置去重与槽合并 ----------
                var postDedup = new PostDeduplicator(logger, patcher);
                postDedup.DeduplicateMaterials(avatarRoot.GetComponentsInChildren<Renderer>(true), settings.deduplicateMaterials);
                patcher.PatchAll(ctx.Animation.Clips); // 材质替换可能引入新引用，再打一遍
                if (settings.mergeIdenticalMaterialSlots)
                {
                    var merger = new SlotMerger(logger, ctx.Animation, patcher);
                    merger.MergeAll(avatarRoot.GetComponentsInChildren<Renderer>(true));
                    patcher.PatchAll(ctx.Animation.Clips);
                }

                // ---------- 12. 移除自身组件 ----------
                UnityEngine.Object.DestroyImmediate(component);
                evaluator.Dispose();

                // ---------- 13. 报告 ----------
                report.StepTimings.Add(("total", sw.Elapsed));
                LogReport(report, logger);
            }
            catch (ATOBuildCancelledException)
            {
                logger.Warn(Localized("ato.cancelled"));
                // 保留磁盘临时资产（不清理），释放资源
            }
            catch (Exception e)
            {
                logger.Error($"Build failed: {e}");
                throw;
            }
        }

        // ---------------- 整图缩放 ----------------

        private static Texture ScaleWholeTexture(TextureInfo info, float scale, EffectiveSettings settings,
            AtlasWriter writer, ATOContext ctx, BuildReport report)
        {
            var tex = info.texture as Texture2D;
            if (tex == null) return null;
            int nw = Mathf.Max(1, Mathf.RoundToInt(tex.width * scale));
            int nh = Mathf.Max(1, Mathf.RoundToInt(tex.height * scale));
            if (nw >= tex.width && nh >= tex.height) return null;

            Texture2D result = null;
            if (settings.useGPU)
            {
                result = writer.ScaleWholeTextureGpu(tex, nw, nh, info.colorSpace == ATOColorSpace.SRGB,
                    info.hasAlpha);
            }
            if (result == null)
            {
                result = ScaleWholeTextureCpu(info, nw, nh);
            }
            if (result == null) return null;

            string path = AssetSaver.NextAssetPath(".png");
            var asset = AssetSaver.SaveTexture(result, path, ctx.Logger);
            UnityEngine.Object.DestroyImmediate(result);
            if (asset == null) return null;

            CompressionApplier.Apply(path, asset.width, asset.height, info.category, info.colorSpace,
                info.filterMode, info.hasAlpha, settings.npotEnabled, settings, report);

            report.ScaledTextures.Add(new BuildReport.ScaledTextureEntry
            {
                name = tex.name,
                fromW = tex.width,
                fromH = tex.height,
                toW = asset.width,
                toH = asset.height,
                atlasFailed = info.whitelistLevel == ATOWhitelistLevel.NoAtlas,
            });
            report.TexelsOut += (long)asset.width * asset.height;
            ctx.Logger.Info($"Whole-texture scaled '{tex.name}' {tex.width}x{tex.height} -> {asset.width}x{asset.height}.");
            return asset;
        }

        private static Texture2D ScaleWholeTextureCpu(TextureInfo info, int nw, int nh)
        {
            var tex = info.texture as Texture2D;
            if (tex == null) return null;
            try
            {
                var px = GetPixelsSafe(info);
                if (px == null) return null;
                var src = ImageOps.ExtractRegionLinear(px, tex.width, tex.height, 0, 0, tex.width, tex.height,
                    info.colorSpace == ATOColorSpace.SRGB);
                var crop = ImageOps.DownscaleWithAlpha(src, tex.width, tex.height, nw, nh, info.hasAlpha);
                var outPx = new Color32[nw * nh];
                bool srgb = info.colorSpace == ATOColorSpace.SRGB;
                for (int i = 0; i < outPx.Length; i++)
                {
                    float r = crop[i * 4], g = crop[i * 4 + 1], b = crop[i * 4 + 2], a = crop[i * 4 + 3];
                    if (srgb)
                    {
                        r = Utils.ColorSpace.LinearToSrgb(r);
                        g = Utils.ColorSpace.LinearToSrgb(g);
                        b = Utils.ColorSpace.LinearToSrgb(b);
                    }
                    outPx[i] = new Color32(
                        (byte)Mathf.Clamp(Mathf.RoundToInt(r * 255f), 0, 255),
                        (byte)Mathf.Clamp(Mathf.RoundToInt(g * 255f), 0, 255),
                        (byte)Mathf.Clamp(Mathf.RoundToInt(b * 255f), 0, 255),
                        (byte)Mathf.Clamp(Mathf.RoundToInt(a * 255f), 0, 255));
                }
                var result = new Texture2D(nw, nh, TextureFormat.RGBA32, false, true);
                result.SetPixels32(outPx);
                result.Apply(false, false);
                return result;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[ATO] CPU whole-texture scale failed for '{info.texture?.name}': {e.Message}");
                return null;
            }
        }

        private static Color32[] GetPixelsSafe(TextureInfo info)
        {
            var tex = info.texture as Texture2D;
            if (tex == null) return null;
            if (tex.isReadable) return tex.GetPixels32();
            // 不可读：尝试用渲染拷贝（与 TextureCache 相同的路径）
            try
            {
                var rt = RenderTexture.GetTemporary(tex.width, tex.height, 0, RenderTextureFormat.ARGB32,
                    tex.colorSpace == ColorSpace.Linear ? RenderTextureReadWrite.Linear : RenderTextureReadWrite.sRGB);
                Graphics.Blit(tex, rt);
                var prev = RenderTexture.active;
                RenderTexture.active = rt;
                var copy = new Texture2D(tex.width, tex.height, TextureFormat.RGBA32, false,
                    tex.colorSpace == ColorSpace.Linear);
                copy.ReadPixels(new Rect(0, 0, tex.width, tex.height), 0, 0);
                copy.Apply(false, false);
                RenderTexture.active = prev;
                RenderTexture.ReleaseTemporary(rt);
                var px = copy.GetPixels32();
                UnityEngine.Object.DestroyImmediate(copy);
                return px;
            }
            catch (Exception)
            {
                return null;
            }
        }

        // ---------------- 材质赋值 ----------------

        private static void AssignMaterials(GameObject root, Dictionary<Texture, Texture> replaceMap,
            AnimationPatcher patcher)
        {
            if (replaceMap.Count == 0) return;
            int changed = 0;
            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
            {
                var mats = r.sharedMaterials;
                for (int i = 0; i < mats.Length; i++)
                {
                    var mat = mats[i];
                    if (mat == null || mat.shader == null) continue;
                    bool matChanged = false;
                    foreach (var propName in mat.GetTexturePropertyNames())
                    {
                        var tex = mat.GetTexture(propName);
                        if (tex == null) continue;
                        if (replaceMap.TryGetValue(tex, out var newTex))
                        {
                            mat.SetTexture(propName, newTex);
                            matChanged = true;
                            patcher.AddTextureReplacement(r, i, propName, tex, newTex);
                        }
                    }
                    if (matChanged) changed++;
                }
            }
        }

        private static void BuildPathCache(GameObject root, AnimationPatcher patcher)
        {
            patcher.Root = root;
            patcher.PathCache.Clear();
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                patcher.PathCache[t.GetPath(root.transform)] = t;
            }
        }

        private static string GetPath(this Transform t, Transform root)
        {
            if (t == root) return "";
            var names = new List<string>();
            var cur = t;
            while (cur != null && cur != root)
            {
                names.Insert(0, cur.name);
                cur = cur.parent;
            }
            return string.Join("/", names);
        }

        private static string Localized(string key) => Localization.L(key);

        private static void LogReport(BuildReport report, ATOLogger logger)
        {
            logger.Info(report.FormatSummary(Localization.L("ato.report.title")));
            // 细节（折叠式：默认只输出总体，细节标记 [ATO][Detail]）
            if (report.Atlases.Count > 0 || report.ScaledTextures.Count > 0)
            {
                logger.Info(report.FormatDetails());
            }
        }
    }
}
