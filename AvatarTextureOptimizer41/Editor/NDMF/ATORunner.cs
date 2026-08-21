using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf;
using UnityEditor;
using UnityEngine;

// Main pipeline orchestrator: validation -> animation analysis -> whitelist -> dedup -> collect ->
// scale -> atlas / whole-texture -> bake -> remap -> dedup -> import settings -> report -> cleanup.
// 主流程编排：校验→动画分析→白名单→去重→收集→缩放→图集/整图→烘焙→重映射→去重→导入参数→报告→清理。

namespace Net.Fosa.AvatarTextureOptimizer.Editor.Ndmf
{
    public static class ATORunner
    {
        public static void Run(BuildContext ndmfCtx)
        {
            var root = ndmfCtx.AvatarRootObject;
            var settings = root.GetComponentInChildren<ATOSettings>(true);
            if (settings == null) return; // avatar without ATO: nothing to do. 无 ATO 组件的 Avatar：跳过。

            string err = ATOSettingsValidator.Validate(root, settings);
            if (err != null)
            {
                ATOLog.Error(err);
                throw new Exception("[ATO] " + err); // abort bake. 中止烘焙。
            }

            var report = new ATOBuildReport();
            ATOLocalizer.Select(settings.Data.languageMode, settings.Data.manualLanguage);
            ATOPlatform platform = ResolvePlatform();
            ATOLog.Info($"starting optimization for avatar '{root.name}' (platform {platform}, quality tier {settings.Data.qualityTier})");

            var ctx = new ATOBuildContext { Settings = settings, Platform = platform };
            using (var rtPool = new RenderTexturePool())
            using (var decode = new TextureDecodeCache(rtPool))
            using (var cancel = new ATOCancellation())
            {
                try
                {
                    RunCore(ndmfCtx, root, settings, platform, ctx, rtPool, decode, cancel, report);
                    ATOLog.Info(report.RenderSummary());
                    ATOLog.Report(ReportDetails(ctx, report));
                }
                catch (OperationCanceledException)
                {
                    // Cancelled: release resources, keep temporary assets on disk per requirements.
                    // 已取消：释放资源，按需求保留硬盘上的临时资产。
                    ATOLog.Warn("bake cancelled by user; temporary assets kept on disk");
                }
                finally
                {
                    cancel.Clear();
                }
            }
        }

        private static void RunCore(BuildContext ndmfCtx, GameObject root, ATOSettings settings, ATOPlatform platform,
            ATOBuildContext ctx, RenderTexturePool rtPool, TextureDecodeCache decode, ATOCancellation cancel, ATOBuildReport report)
        {
            var data = settings.Data.Resolve(platform);
            var tier = data.GetTier();

            // ---- Stage 1: animation analysis. 阶段 1：动画分析。----
            report.BeginStage("Animation analysis");
            var anim = AnimationAnalyzer.Analyze(root, AnimationAnalyzer.CollectClips(root));
            report.EndStage();

            // ---- Stage 2: whitelist + dedup. 阶段 2：白名单 + 去重。----
            report.BeginStage("Dedup");
            var white = new WhiteListEvaluator(data.whitelist);
            var refs = new ReferenceUpdater();
            TextureDeduper.Dedup(root, white, decode, refs);
            report.EndStage();

            // ---- Stage 3: collect UV<->texture mapping. 阶段 3：收集 UV↔贴图映射。----
            report.BeginStage("Analysis");
            var collector = new TextureUseCollector(root, data, anim, white, decode, ctx);
            collector.Collect();
            foreach (var w in collector.Warnings) ATOLog.Warn(w);
            report.EndStage();
            ATOLog.Info($"collected {ctx.UVGroups.Count} UV groups, {ctx.UVGroups.Sum(g => g.Islands.Count)} islands");

            // ---- Stage 4: quality scaling. 阶段 4：质量缩放。----
            report.BeginStage("Quality scaling");
            IslandScaler.ScaleAll(ctx, data, tier, decode, rtPool, cancel, report);
            report.EndStage();

            // ---- Stage 5: atlasing (or whole-texture). 阶段 5：图集化（或整图）。----
            report.BeginStage("Atlas build");
            AtlasBuilder.Build(ctx, data, cancel, report);
            report.EndStage();

            // Uses that are not atlased fall back to whole-texture scaling.
            // 未图集化的引用回退到整图缩放。
            foreach (var group in ctx.UVGroups)
                foreach (var use in group.Uses)
                    if (!use.Skip && !ctx.UseAtlas.ContainsKey(use)) ctx.WholeTextureUses.Add(use);

            // ---- Stage 6: bake atlases. 阶段 6：烘焙图集。----
            report.BeginStage("Atlas baking");
            int ai = 0;
            foreach (var atlas in ctx.Atlases.ToList())
            {
                cancel.ThrowIfCancelled($"Baking atlas {ai + 1}/{ctx.Atlases.Count}", ai / (float)ctx.Atlases.Count);
                var tex = AtlasTextureBaker.Bake(atlas, ctx, rtPool);
                string path = ImportSettingsApplier.WritePngAsset(tex, "ATO_" + Sanitize(root.name) + "_" + (ai + 1) + "_" + Sanitize(atlas.Bucket.ToString()));
                UnityEngine.Object.DestroyImmediate(tex);
                var asset = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
                atlas.AtlasTexture = asset;
                ImportSettingsApplier.Apply(asset, path, atlas.Bucket.Class, data, platform);
                ATOLog.Info($"baked atlas #{ai + 1}: {atlas.Width}x{atlas.Height} {atlas.Bucket} ({atlas.IslandRects.Count} islands, utilization {atlas.Utilization * 100f:F1}%)");
                ai++;
            }
            report.EndStage();

            // ---- Stage 7: assign atlases to materials (cloning) + animation clip refs. 阶段 7：图集赋给材质（克隆）+ 动画引用。----
            report.BeginStage("Reference rewrite");
            AssignAtlasesToMaterials(root, ctx, refs);
            report.EndStage();

            // ---- Stage 8: whole-texture scaling for fallback uses. 阶段 8：回退引用的整图缩放。----
            report.BeginStage("Whole-texture scaling");
            WholeTextureScale(root, ctx, data, platform, decode, rtPool, refs, cancel);
            report.EndStage();

            // ---- Stage 9: mesh remap + AAO evacuation. 阶段 9：网格重映射 + AAO 转移。----
            report.BeginStage("Mesh remap");
            MeshReplacer.Remap(ctx, cancel);
            report.EndStage();

            // ---- Stage 10: material/slot dedup. 阶段 10：材质/槽位去重。----
            report.BeginStage("Dedup post");
            MaterialDeduper.Dedup(root, refs, anim, cancel);
            report.EndStage();

            // ---- Stage 11: cleanup — remove ourselves from the baked clone. 阶段 11：清理——从烘焙克隆体移除自身。----
            UnityEngine.Object.DestroyImmediate(root.GetComponent<ATOSettings>());
            ATOLog.Info("AvatarTextureOptimizer completed successfully");
        }

        // ------------------------------------------------ helpers ------------------------------------------------

        private static void AssignAtlasesToMaterials(GameObject root, ATOBuildContext ctx, ReferenceUpdater refs)
        {
            // For each atlased use: set the atlas texture on the (cloned) material's property.
            // 对每个图集化引用：在图集纹理设置到（克隆）材质的属性上。
            foreach (var kv in ctx.UseAtlas)
            {
                var use = kv.Key;
                var atlas = kv.Value;
                if (use.Material == null) continue;
                var mat = refs.GetWorkingMaterial(use.Material);
                mat.SetTexture(use.PropertyName, atlas.AtlasTexture);
                ctx.TouchedMaterials.Add(use.Material);
                ctx.AtlasAssignedProps.Add((use.Material, use.PropertyName));
            }
            // Replace material references in renderers with the clones. 用克隆替换渲染器中的材质引用。
            var rendered = new HashSet<Renderer>();
            foreach (var group in ctx.UVGroups)
            {
                if (group.Renderer == null || rendered.Contains(group.Renderer)) continue;
                var mats = group.Renderer.sharedMaterials;
                bool changed = false;
                for (int i = 0; i < mats.Length; i++)
                {
                    if (mats[i] != null && refs.HasWorkingMaterial(mats[i])) { mats[i] = refs.GetWorkingMaterial(mats[i]); changed = true; }
                }
                if (changed) group.Renderer.sharedMaterials = mats;
                rendered.Add(group.Renderer);
            }
            // Animation object-ref curves referencing the original materials must now target the clones.
            // 引用原材质的动画对象引用曲线需改为指向克隆。
            foreach (var clip in AnimationAnalyzer.CollectClips(root))
            {
                refs.RewriteClip(clip, obj =>
                {
                    if (obj is Material m && refs.HasWorkingMaterial(m)) return refs.GetWorkingMaterial(m);
                    return obj;
                }, root);
            }
        }

        private static void WholeTextureScale(GameObject root, ATOBuildContext ctx, ATOSettingsData data, ATOPlatform platform,
            TextureDecodeCache decode, RenderTexturePool rtPool, ReferenceUpdater refs, ATOCancellation cancel)
        {
            var byTexture = new Dictionary<Texture2D, List<TextureUse>>();
            foreach (var use in ctx.WholeTextureUses)
            {
                if (!byTexture.TryGetValue(use.Texture, out var l)) { l = new List<TextureUse>(); byTexture[use.Texture] = l; }
                l.Add(use);
            }

            var replacement = new Dictionary<Texture2D, Texture2D>();
            int idx = 0;
            foreach (var kv in byTexture)
            {
                cancel.ThrowIfCancelled($"Scaling whole texture {idx + 1}/{byTexture.Count}", idx / (float)byTexture.Count);
                var tex = kv.Key;
                var uses = kv.Value;
                // Whole scale = max needed ratio across islands (never upscale). 整图缩放 = 所有岛所需比例的最大值（永不上采样）。
                float s = 1f;
                foreach (var use in uses)
                {
                    var group = ctx.UVGroups.FirstOrDefault(g => g.Uses.Contains(use));
                    if (group == null) continue;
                    foreach (var island in group.Islands)
                    {
                        if (!use.IslandScaleFactors.TryGetValue(island, out var sf)) continue;
                        var origPx = IslandScaler.PixelSizeAtTexture(island, tex);
                        s = Mathf.Min(1f, Mathf.Max(s,
                            Mathf.Max((float)sf.x / Mathf.Max(1, origPx.x), (float)sf.y / Mathf.Max(1, origPx.y))));
                    }
                }
                bool hasAlpha = decode.Get(tex).HasAlpha;
                TextureClass cls = uses[0].Class;
                var newTex = s < 0.999f
                    ? TextureReencoder.ScaleWhole(tex, Mathf.Max(1, Mathf.RoundToInt(tex.width * s)), Mathf.Max(1, Mathf.RoundToInt(tex.height * s)), hasAlpha, rtPool)
                    : null;
                if (newTex == null)
                {
                    // No scaling: still (re)encode a managed copy so import settings can be applied safely.
                    // 无需缩放：仍生成托管副本以安全应用导入设置。
                    newTex = CopyPixels(tex, rtPool);
                }
                if (newTex == null) continue;
                string path = ImportSettingsApplier.WritePngAsset(newTex, "ATO_" + Sanitize(tex.name) + "_scaled");
                UnityEngine.Object.DestroyImmediate(newTex);
                var asset = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
                ImportSettingsApplier.Apply(asset, path, cls, data, platform);
                replacement[tex] = asset;
                idx++;
            }
            if (replacement.Count > 0)
            {
                refs.RewriteTextures(root, replacement, ctx.AtlasAssignedProps);
                ctx.TextureReplacement = replacement;
            }
        }

        private static Texture2D CopyPixels(Texture2D src, RenderTexturePool rtPool)
        {
            var rt = rtPool.Acquire(src.width, src.height, RenderTextureFormat.ARGB32, linear: true);
            Graphics.Blit(src, rt);
            var tex = new Texture2D(src.width, src.height, TextureFormat.RGBA32, false) { name = "ATO_" + src.name + "_copy" };
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            tex.ReadPixels(new Rect(0, 0, src.width, src.height), 0, 0, false);
            tex.Apply(false, true);
            RenderTexture.active = prev;
            rtPool.Release(rt);
            return tex;
        }

        private static string ReportDetails(ATOBuildContext ctx, ATOBuildReport report)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("[ATO] ---- Details (folded) ----");
            foreach (var atlas in ctx.Atlases)
            {
                sb.AppendLine($"[ATO]   atlas {atlas.Width}x{atlas.Height} {atlas.Bucket}: sources=[{string.Join(", ", atlas.SourceTextures.ConvertAll(t => t.name))}], islands={atlas.IslandRects.Count}, utilization={atlas.Utilization * 100f:F1}%");
            }
            sb.AppendLine($"[ATO]   whitelisted/fallback uses: {ctx.WholeTextureUses.Count + ctx.UVGroups.Sum(g => g.Uses.Count(u => u.Skip))}");
            return sb.ToString();
        }

        private static ATOPlatform ResolvePlatform()
        {
            switch (EditorUserBuildSettings.activeBuildTarget)
            {
                case BuildTarget.Android: return ATOPlatform.Android;
                case BuildTarget.iOS: return ATOPlatform.iOS;
                default: return ATOPlatform.PC;
            }
        }

        private static string Sanitize(string s)
        {
            var chars = s.Where(char.IsLetterOrDigit).ToArray();
            return chars.Length == 0 ? "Avatar" : new string(chars);
        }
    }
}
