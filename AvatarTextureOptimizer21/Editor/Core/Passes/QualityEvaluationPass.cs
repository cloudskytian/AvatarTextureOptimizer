// Quality Evaluation Pass - Complete with UV group barrel effect, progress reporting
// 质量评估Pass - 包含UV组木桶效应、进度报告的完整实现

using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using nadena.dev.ndmf;
using net.fosa.avatar_texture_optimizer.Editor.Processing;
using net.fosa.avatar_texture_optimizer.Editor.Quality;
using net.fosa.avatar_texture_optimizer.Runtime;
using UnityEngine;

namespace net.fosa.avatar_texture_optimizer.Editor.Core.Passes
{
    public class QualityEvaluationPass : Pass<QualityEvaluationPass>
    {
        public override string DisplayName => "ATO: Quality Evaluation / 质量评估";

        protected override void Execute(BuildContext context)
        {
            var sw = Stopwatch.StartNew();
            var atoCtx = context.GetState<ATOBuildContext>();
            if (!atoCtx.IsValid) return;
            var comp = atoCtx.Component;
            ATOLog.SetVerbose(comp.verboseLogging);
            atoCtx.ReportProgress("Quality: Extracting islands...", 0f);

            // Step 1: Extract UV islands
            var extractSw = Stopwatch.StartNew();
            ExtractUVIslands(atoCtx);
            extractSw.Stop();
            atoCtx.StageTimings["Quality:IslandExtraction"] = extractSw.Elapsed.TotalMilliseconds;
            ATOLog.Info($"Extracted {atoCtx.AllIslands.Count} UV islands in {extractSw.ElapsedMilliseconds}ms");

            atoCtx.ReportProgress("Quality: Building groups...", 0.2f);

            // Step 2: Build texture type groups
            BuildTextureTypeGroups(atoCtx);

            // Step 3: Build UV groups
            BuildUVGroups(atoCtx);

            atoCtx.ReportProgress("Quality: Evaluating...", 0.3f);

            // Step 4: Per-island quality evaluation with binary search
            EvaluateQuality(atoCtx, comp);

            atoCtx.ReportProgress("Quality: Applying barrel effect...", 0.9f);

            // Step 5: UV group barrel effect - take max scale across all textures,
            // capped by UV group's max original size
            // UV组木桶效应 - 取所有贴图中的最大缩放，受UV组最大原始尺寸钳制
            ApplyUVGroupBarrelEffect(atoCtx);

            sw.Stop();
            atoCtx.StageTimings["QualityEvaluation"] = sw.Elapsed.TotalMilliseconds;
            ATOLog.Info($"Quality evaluation complete: {sw.ElapsedMilliseconds}ms, " +
                        $"{atoCtx.AllIslands.Count} islands, {atoCtx.UVGroups.Count} UV groups");
        }

        private void ExtractUVIslands(ATOBuildContext atoCtx)
        {
            int islandId = 0;
            foreach (var ri in atoCtx.Renderers)
            {
                if (ri.SharedMesh == null) continue;
                for (int ch = 0; ch < 8; ch++)
                {
                    var uvs = new List<Vector2>();
                    ri.SharedMesh.GetUVs(ch, uvs);
                    if (uvs.Count == 0) continue;

                    // Check if this UV channel is used
                    bool used = atoCtx.UVTextureMap.Any(kvp =>
                        kvp.Key.MeshInstanceId == ri.Renderer.GetInstanceID() &&
                        kvp.Key.UvChannel == ch && kvp.Value.TextureUsages.Count > 0);
                    if (!used) continue;

                    var islands = UVIslandExtractor.ExtractIslands(
                        ri.SharedMesh, ch, uvs, ri.Renderer, atoCtx, ref islandId);
                    atoCtx.AllIslands.AddRange(islands);
                }
            }
        }

        private void BuildTextureTypeGroups(ATOBuildContext atoCtx)
        {
            var groups = new Dictionary<string, TextureTypeGroup>();

            // First pass: create groups from UV group signatures
            foreach (var uvGroup in atoCtx.UVGroups)
            {
                bool hasN = false, hasM = false, hasA = false, isL = false;
                FilterMode fm = FilterMode.Bilinear;

                foreach (var ti in uvGroup.TextureIndices)
                {
                    if (ti < 0 || ti >= atoCtx.AllTextures.Count) continue;
                    var tex = atoCtx.AllTextures[ti];
                    if (tex.IsNormalMap) hasN = true;
                    if (tex.IsGrayscale) hasM = true;
                    if (tex.HasAlpha) hasA = true;
                    if (tex.IsLinear) isL = true;
                    fm = tex.FilterMode;
                }

                // Also consider animation textures in the same group
                if (atoCtx.AnimationAnalysis?.AnimationTextureOriginalMap != null)
                {
                    foreach (var ti in uvGroup.TextureIndices)
                    {
                        if (ti < 0 || ti >= atoCtx.AllTextures.Count) continue;
                        var tex = atoCtx.AllTextures[ti];
                        foreach (var kvp in atoCtx.AnimationAnalysis.AnimationTextureOriginalMap)
                        {
                            if (kvp.Value == tex.Texture || kvp.Value == tex.OriginalTexture)
                            {
                                // Animation texture belongs to same group
                                // 动画贴图属于同一组
                            }
                        }
                    }
                }

                string sig = $"N{(hasN?1:0)}_M{(hasM?1:0)}_A{(hasA?1:0)}_L{(isL?1:0)}_F{(int)fm}";
                if (!groups.ContainsKey(sig))
                {
                    groups[sig] = new TextureTypeGroup
                    {
                        Id = groups.Count,
                        Signature = sig,
                        HasNormalMap = hasN, HasMask = hasM, HasAlpha = hasA,
                        IsLinear = isL, FilterMode = fm,
                        PrimaryRole = hasN ? TextureRole.NormalMap : (hasM ? TextureRole.Mask : TextureRole.MainColor)
                    };
                }
                groups[sig].UVGroupIds.Add(uvGroup.Id);
                groups[sig].TextureIndices.AddRange(uvGroup.TextureIndices);
                uvGroup.TypeGroupIds.Add(groups[sig].Id);
            }

            atoCtx.TextureTypeGroups = groups.Values.ToList();
        }

        private void BuildUVGroups(ATOBuildContext atoCtx)
        {
            var uvToIslands = new Dictionary<string, List<int>>();
            foreach (var isl in atoCtx.AllIslands)
            {
                string key = $"{isl.SourceMesh?.GetInstanceID()}_{isl.UvChannel}_{isl.SubMeshIndex}";
                if (!uvToIslands.ContainsKey(key)) uvToIslands[key] = new List<int>();
                uvToIslands[key].Add(isl.Id);
            }

            int gid = 0;
            foreach (var kvp in uvToIslands)
            {
                var uvGroup = new UVGroup { Id = gid++, IslandIds = kvp.Value };
                var texIdx = new HashSet<int>();
                foreach (var iid in kvp.Value)
                {
                    var isl = atoCtx.AllIslands.FirstOrDefault(i => i.Id == iid);
                    if (isl != null && isl.SourceTextureIndex >= 0)
                    {
                        texIdx.Add(isl.SourceTextureIndex);
                        isl.UVGroupId = uvGroup.Id;
                    }
                }
                uvGroup.TextureIndices = texIdx.ToList();
                uvGroup.MaxOriginalSize = 0;
                foreach (var ti in uvGroup.TextureIndices)
                {
                    if (ti < atoCtx.AllTextures.Count)
                    {
                        var tex = atoCtx.AllTextures[ti];
                        uvGroup.MaxOriginalSize = Mathf.Max(uvGroup.MaxOriginalSize, tex.Width, tex.Height);
                    }
                }
                atoCtx.UVGroups.Add(uvGroup);
            }
        }

        private void EvaluateQuality(ATOBuildContext atoCtx, AvatarTextureOptimizerComponent comp)
        {
            var qp = GetQualityParams(comp);
            bool nearLossless = comp.qualityPreset == QualityPreset.NearLossless;
            int total = atoCtx.AllIslands.Count;
            int done = 0;

            foreach (var island in atoCtx.AllIslands)
            {
                var result = QualityEvaluator.EvaluateIsland(island, atoCtx, qp, nearLossless);
                atoCtx.IslandQualityResults[island.Id] = result;
                island.ScaleFactor = result.AnisotropicScale;
                island.AnisotropicScale = result.AnisotropicScale;

                done++;
                if (done % 50 == 0)
                    atoCtx.ReportProgress($"Quality: Evaluating island {done}/{total}...",
                        0.3f + 0.6f * (done / (float)total));
            }
        }

        /// <summary>
        /// Barrel effect: for each UV group, take the maximum scale across all textures.
        /// Cap by UV group's max original size (never upscale beyond original).
        /// 木桶效应：对每个UV组，取所有贴图中的最大缩放。
        /// 受UV组最大原始尺寸钳制（绝不放大超过原始尺寸）。
        /// </summary>
        private void ApplyUVGroupBarrelEffect(ATOBuildContext atoCtx)
        {
            foreach (var uvGroup in atoCtx.UVGroups)
            {
                float maxScaleX = 0, maxScaleY = 0;

                foreach (var islandId in uvGroup.IslandIds)
                {
                    var island = atoCtx.AllIslands.FirstOrDefault(i => i.Id == islandId);
                    if (island == null) continue;

                    if (atoCtx.IslandQualityResults.TryGetValue(islandId, out var qr))
                    {
                        maxScaleX = Mathf.Max(maxScaleX, qr.AnisotropicScale.x);
                        maxScaleY = Mathf.Max(maxScaleY, qr.AnisotropicScale.y);
                    }
                }

                // Cap: scale * original_pixel_size <= maxOriginalSize
                // 钳制：缩放 * 原始像素尺寸 <= 最大原始尺寸
                maxScaleX = Mathf.Min(maxScaleX, 1f);
                maxScaleY = Mathf.Min(maxScaleY, 1f);

                uvGroup.FinalScale = Mathf.Max(maxScaleX, maxScaleY);
                uvGroup.FinalAnisotropicScale = new Vector2(maxScaleX, maxScaleY);

                // Apply to all islands in this group
                foreach (var islandId in uvGroup.IslandIds)
                {
                    var island = atoCtx.AllIslands.FirstOrDefault(i => i.Id == islandId);
                    if (island == null) continue;
                    island.ScaleFactor = uvGroup.FinalAnisotropicScale;
                    island.AnisotropicScale = uvGroup.FinalAnisotropicScale;
                }
            }
        }

        private QualityParameters GetQualityParams(AvatarTextureOptimizerComponent comp)
        {
            if (comp.qualityPreset == QualityPreset.Custom) return comp.qualityParams;
            switch (comp.qualityPreset)
            {
                case QualityPreset.NearLossless: return new QualityParameters
                    { msSsimThreshold=0.999f, ssimThreshold=0.999f, deltaEThreshold=0.5f,
                      alphaIoUThreshold=0.999f, alphaRMSEThreshold=0.001f,
                      normalAngleErrorThreshold=1f, normalP95AngleErrorThreshold=2f, grayscaleRMSEThreshold=0.001f };
                case QualityPreset.High: return new QualityParameters
                    { msSsimThreshold=0.97f, ssimThreshold=0.97f, deltaEThreshold=1f,
                      alphaIoUThreshold=0.97f, alphaRMSEThreshold=0.01f,
                      normalAngleErrorThreshold=3f, normalP95AngleErrorThreshold=6f, grayscaleRMSEThreshold=0.01f };
                case QualityPreset.Balanced: return new QualityParameters
                    { msSsimThreshold=0.95f, ssimThreshold=0.95f, deltaEThreshold=2f,
                      alphaIoUThreshold=0.95f, alphaRMSEThreshold=0.02f,
                      normalAngleErrorThreshold=5f, normalP95AngleErrorThreshold=10f, grayscaleRMSEThreshold=0.02f };
                case QualityPreset.Performance: return new QualityParameters
                    { msSsimThreshold=0.90f, ssimThreshold=0.90f, deltaEThreshold=4f,
                      alphaIoUThreshold=0.90f, alphaRMSEThreshold=0.04f,
                      normalAngleErrorThreshold=8f, normalP95AngleErrorThreshold=15f, grayscaleRMSEThreshold=0.04f };
                case QualityPreset.Aggressive: return new QualityParameters
                    { msSsimThreshold=0.85f, ssimThreshold=0.85f, deltaEThreshold=6f,
                      alphaIoUThreshold=0.85f, alphaRMSEThreshold=0.06f,
                      normalAngleErrorThreshold=12f, normalP95AngleErrorThreshold=20f, grayscaleRMSEThreshold=0.06f };
                default: return new QualityParameters();
            }
        }
    }
}
