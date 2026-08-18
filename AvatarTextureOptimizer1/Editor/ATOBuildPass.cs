// ATOBuildPass.cs / ATOBuildPass.cs
// Main NDMF pass that runs the ATO optimization pipeline.
// 运行ATO优化管线的主NDMF Pass。

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using nadena.dev.ndmf;
using net.fosa.avatar_texture_optimizer;
using net.fosa.avatar_texture_optimizer.Editor.Atlas;
using net.fosa.avatar_texture_optimizer.Editor.Core;
using net.fosa.avatar_texture_optimizer.Editor.Groups;
using net.fosa.avatar_texture_optimizer.Editor.Processing;
using net.fosa.avatar_texture_optimizer.Editor.Quality;
using net.fosa.avatar_texture_optimizer.Editor.Util;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace net.fosa.avatar_texture_optimizer.Editor
{
    public class ATOBuildPass : Pass<ATOBuildPass>
    {
        public static ATOBuildPass Instance = new ATOBuildPass();
        public override string DisplayName => "Avatar Texture Optimizer";

        private volatile bool _cancelRequested;

        protected override void Execute(BuildContext context)
        {
            var settings = context.AvatarRootObject.GetComponent<AvatarTextureOptimizer>();
            if (settings == null) return;

            _cancelRequested = false;
            var swTotal = Stopwatch.StartNew();
            var log = new ATOLogger(settings.verboseLogging);
            log.StartTotal();

            int expectedPhases = 9;
            log.SetProgressCallback((phase, progress) =>
            {
                if (_cancelRequested) throw new OperationCanceledException("ATO cancelled by user / ATO已被用户取消");
                if (EditorUtility.DisplayCancelableProgressBar("Avatar Texture Optimizer", phase, progress))
                {
                    _cancelRequested = true;
                    throw new OperationCanceledException("ATO cancelled by user / ATO已被用户取消");
                }
            }, expectedPhases);

            try
            {
                // Validation / 验证
                var all = context.AvatarRootObject.GetComponentsInChildren<AvatarTextureOptimizer>(true);
                if (all.Length > 1) { log.LogError(ATOLocalization.T("error.multipleComponents")); log.EmitFinalReport(context); return; }
                if (!settings.IsValidAvatarRoot()) { log.LogError(ATOLocalization.T("error.noAvatarDescriptor")); log.EmitFinalReport(context); return; }

                // Platform / 平台
                var platform = settings.targetPlatform == TargetPlatform.Auto ? DetectPlatform() : settings.targetPlatform;
                var po = settings.GetEffectivePlatformSettings(platform);
                int maxAtlasSize = Mathf.Clamp(po.maxAtlasSize, 64, platform == TargetPlatform.Android || platform == TargetPlatform.iOS ? 4096 : 8192);
                int padding = Mathf.Max(4, (int)settings.atlasPadding);

                // 1. Analyze (includes scan, dedup, animation, blendshapes, island extraction, grouping)
                // 1. 分析（包含扫描、去重、动画、blendshape、岛提取、分组）
                AvatarAnalysisResult analysis;
                using (log.Phase("phase.analyze"))
                {
                    ThrowIfCancelled();
                    analysis = AvatarAnalyzer.Analyze(context, settings, log);
                    if (analysis == null || !analysis.IsValid) { log.EmitFinalReport(context); return; }
                }

                // 2. Quality scaling / 质量缩放
                using (log.Phase("phase.qualityScale"))
                {
                    ThrowIfCancelled();
                    // Run scaler always (sets ScaledPixelSize used by both atlas and whole-texture paths)
                    // 始终运行scaler（设置两种路径都需要的ScaledPixelSize）
                    UVScaler.ComputeTargetScales(analysis, log);
                }

                List<AtlasTexture> atlases = new List<AtlasTexture>();
                Dictionary<Texture2D, Texture2D> wholeTextureMap = null;

                if (settings.generateAtlas)
                {
                    // 3a. Build atlases (pack, blit, dilate) / 构建图集（装箱、blit、外扩）
                    using (log.Phase("phase.pack"))
                    {
                        ThrowIfCancelled();
                        atlases = AtlasBuilder.BuildAll(analysis, log, padding, settings.allowNPOT, maxAtlasSize);
                        log.AtlasCount = atlases.Count;
                    }

                    // 3b. Also run whole-texture scaling for whitelisted/partially-whitelisted/skipped islands
                    // 同时对白名单/部分白名单/跳过的岛运行整图缩放
                    using (log.Phase("phase.rasterize"))
                    {
                        ThrowIfCancelled();
                        wholeTextureMap = WholeTextureScaler.ScaleNonAtlasTextures(analysis);
                    }
                }
                else
                {
                    // Whole-texture scaling (non-atlas mode) / 整图缩放（非图集模式）
                    using (log.Phase("phase.pack"))
                    {
                        ThrowIfCancelled();
                        wholeTextureMap = WholeTextureScaler.ScaleWholeTextures(analysis, false);
                    }
                }

                // 4. Remesh / 重写网格
                using (log.Phase("phase.remesh"))
                {
                    ThrowIfCancelled();
                    MeshProcessor.Remesh(analysis, settings.generateAtlas);
                    MeshProcessor.ApplyToRenderers(analysis, context);
                }

                // 5. Assign atlas materials / whole-texture references + texture settings
                // 5. 分配图集材质/整图引用 + 贴图设置
                using (log.Phase("phase.generateAtlases"))
                {
                    ThrowIfCancelled();
                    if (settings.generateAtlas)
                        AssignAtlasMaterials(analysis, atlases, context);
                    AssignScaledWholeTextures(analysis, wholeTextureMap);
                    TextureProcessor.ApplyTextureSettings(analysis, atlases, wholeTextureMap, context, platform);
                }

                // 6. Dedup / 去重
                using (log.Phase("phase.dedup"))
                {
                    ThrowIfCancelled();
                    if (settings.deduplicate) MaterialMerger.Deduplicate(analysis, true, log);
                }

                // 7. Update animations / 更新动画
                using (log.Phase("phase.updateAnimations"))
                {
                    ThrowIfCancelled();
                    // Build combined texture map (atlases + whole-scaled) for animation updates
                    var combinedMap = new Dictionary<Texture2D, Texture2D>();
                    if (wholeTextureMap != null)
                        foreach (var kv in wholeTextureMap) combinedMap[kv.Key] = kv.Value;
                    // Atlas mappings override (islands that were atlasized get their atlas texture)
                    foreach (var atl in atlases)
                        foreach (var pl in atl.Placements)
                            foreach (var isl in pl.group.Islands)
                                if (isl.SourceTexture != null && isl.AssignedAtlas != null && !isl.IsWhitelisted)
                                    combinedMap[isl.SourceTexture] = atl.Texture;

                    if (settings.generateAtlas)
                        AnimationUpdater.UpdateAnimations(analysis, atlases, combinedMap);
                    else
                        AnimationUpdater.UpdateTexturesOnly(combinedMap, context.AvatarRootObject);
                }

                // 8. AAO compat: register UV channel evacuation if AAO is present
                // 8. AAO兼容：若AAO存在则注册UV通道疏散
                using (log.Phase("phase.compat"))
                {
                    ThrowIfCancelled();
                    try { AAOCompat.RegisterEvacuation(analysis); }
                    catch (Exception e) { log.LogInfo($"[ATO] AAO compat skipped: {e.Message} / AAO兼容跳过：{e.Message}"); }
                }

                // 9. Run extension post-processors / 运行扩展后处理器
                foreach (var pp in ATOExtensions.GetPostProcessors())
                {
                    try { pp(analysis, atlases); }
                    catch (Exception e) { Debug.LogWarning($"[ATO] Extension post-processor failed: {e.Message}"); }
                }

                // 10. Finalize / 收尾
                using (log.Phase("phase.finalize"))
                {
                    ThrowIfCancelled();
                    UnityEngine.Object.DestroyImmediate(settings);
                }

                swTotal.Stop();
                log.LogInfo($"[ATO] Total time: {swTotal.ElapsedMilliseconds} ms / 总耗时：{swTotal.ElapsedMilliseconds}毫秒");
                log.EmitFinalReport(context);
            }
            catch (OperationCanceledException)
            {
                log.LogInfo("[ATO] Cancelled by user / 用户取消");
                log.EmitFinalReport(context);
            }
            catch (Exception e)
            {
                log.LogError($"[ATO] Fatal error: {e.Message}\n{e.StackTrace}");
                Debug.LogException(e);
                try { log.EmitFinalReport(context); } catch { }
                throw;
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }

        private void ThrowIfCancelled()
        {
            if (_cancelRequested) throw new OperationCanceledException();
        }

        private static TargetPlatform DetectPlatform()
        {
            switch (EditorUserBuildSettings.activeBuildTarget)
            {
                case BuildTarget.Android: return TargetPlatform.Android;
                case BuildTarget.iOS: return TargetPlatform.iOS;
                default: return TargetPlatform.PC;
            }
        }

        /// <summary>
        /// Build a (propName -> atlas) map per material slot by looking up the island with matching source texture.
        /// 为每个材质槽建立(propName -> atlas)映射：查找匹配源贴图的岛。
        /// </summary>
        private static void AssignAtlasMaterials(AvatarAnalysisResult analysis, List<AtlasTexture> atlases, BuildContext context)
        {
            var propToAtlas = new Dictionary<(Renderer renderer, int slot, string propName), AtlasTexture>();
            foreach (var atl in atlases)
            {
                foreach (var pl in atl.Placements)
                {
                    foreach (var isl in pl.group.Islands)
                    {
                        if (isl.SourceTexture == null || isl.IsWhitelisted) continue;
                        var re = isl.RendererEntry;
                        if (re == null) continue;
                        if (isl.MaterialSlot < 0 || isl.MaterialSlot >= re.Materials.Length) continue;
                        var me = re.Materials[isl.MaterialSlot];
                        if (me == null) continue;
                        foreach (var b in me.TextureBindings)
                        {
                            if (b.tex == isl.SourceTexture)
                            {
                                var key = (isl.Renderer, isl.MaterialSlot, b.prop.PropertyName);
                                if (!propToAtlas.ContainsKey(key))
                                    propToAtlas[key] = atl;
                            }
                        }
                    }
                }
            }

            foreach (var re in analysis.Renderers)
            {
                var newMats = new Material[re.Materials.Length];
                for (int i = 0; i < re.Materials.Length; i++)
                {
                    var original = re.Materials[i]?.Material;
                    if (original == null) { newMats[i] = original; continue; }
                    var nm = new Material(original);
                    nm.name = "ATO_" + original.name;

                    foreach (var b in re.Materials[i].TextureBindings)
                    {
                        if (!nm.HasProperty(b.prop.PropertyName)) continue;
                        if (b.tex == null) continue;
                        if (propToAtlas.TryGetValue((re.Renderer, i, b.prop.PropertyName), out var targetAtlas)
                            && targetAtlas != null && targetAtlas.Texture != null)
                        {
                            nm.SetTexture(b.prop.PropertyName, targetAtlas.Texture);
                        }
                    }

                    context.AssetSaver.SaveAsset(nm);
                    newMats[i] = nm;
                }
                if (re.Renderer != null) re.Renderer.sharedMaterials = newMats;
                for (int i = 0; i < re.Materials.Length; i++)
                    if (re.Materials[i] != null) re.Materials[i].Material = newMats[i];
            }
        }

        private static void AssignScaledWholeTextures(AvatarAnalysisResult analysis, Dictionary<Texture2D, Texture2D> map)
        {
            if (map == null) return;
            foreach (var re in analysis.Renderers)
            {
                foreach (var me in re.Materials)
                {
                    if (me?.Material == null) continue;
                    foreach (var b in me.TextureBindings)
                    {
                        if (b.tex == null) continue;
                        // Only assign if this property wasn't already assigned to an atlas
                        // (atlas-assigned textures already have their material slot set to atlas texture)
                        // 只分配尚未被atlas替换的属性（atlas分配的材质槽已设置为atlas贴图）
                        if (map.TryGetValue(b.tex, out var rep) && rep != b.tex && me.Material.HasProperty(b.prop.PropertyName))
                        {
                            var cur = me.Material.GetTexture(b.prop.PropertyName) as Texture2D;
                            // Don't overwrite if already set to an atlas (ATO_) texture
                            // 若已设置为ATO_atlas贴图则不覆盖
                            if (cur == null || !cur.name.StartsWith("ATO_Atlas"))
                                me.Material.SetTexture(b.prop.PropertyName, rep);
                        }
                    }
                }
            }
        }
    }
}
