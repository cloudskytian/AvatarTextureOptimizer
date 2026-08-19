// Post-Process Pass - Material/texture dedup, AAO compat, cleanup, report
// 后处理Pass - 材质/贴图去重、AAO兼容、清理、报告

using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using nadena.dev.ndmf;
using net.fosa.avatar_texture_optimizer.Runtime;
using UnityEngine;

namespace net.fosa.avatar_texture_optimizer.Editor.Core.Passes
{
    /// <summary>
    /// Final pass: deduplicates materials/textures, integrates with AAO UV compatibility API,
    /// removes the ATO component, and generates the build report.
    /// 最终Pass：材质/贴图去重、与AAO UV兼容性API集成、移除ATO组件、生成构建报告。
    /// </summary>
    public class PostProcessPass : Pass<PostProcessPass>
    {
        public override string DisplayName => "ATO: Post-Process / 后处理";

        protected override void Execute(BuildContext context)
        {
            var sw = Stopwatch.StartNew();
            var atoCtx = context.GetState<ATOBuildContext>();
            if (!atoCtx.IsValid) return;

            var component = atoCtx.Component;

            // Step 1: Material deduplication
            if (component.deduplicateMaterials)
            {
                var dedupSw = Stopwatch.StartNew();
                DeduplicateMaterials(atoCtx, context);
                dedupSw.Stop();
                atoCtx.StageTimings["PostProcess:MaterialDedup"] = dedupSw.Elapsed.TotalMilliseconds;
            }

            // Step 2: Texture/Atlas deduplication
            if (component.deduplicateTextures)
            {
                var texDedupSw = Stopwatch.StartNew();
                DeduplicateTextures(atoCtx);
                texDedupSw.Stop();
                atoCtx.StageTimings["PostProcess:TextureDedup"] = texDedupSw.Elapsed.TotalMilliseconds;
            }

            // Step 3: AAO UVUsageCompabilityAPI integration
            var aaoSw = Stopwatch.StartNew();
            IntegrateAAOCompatibility(atoCtx);
            aaoSw.Stop();
            atoCtx.StageTimings["PostProcess:AAO"] = aaoSw.Elapsed.TotalMilliseconds;

            // Step 4: Remove ATO component from build result
            RemoveATOComponent(context);

            // Step 5: Generate build report
            GenerateReport(atoCtx);

            sw.Stop();
            atoCtx.StageTimings["PostProcess"] = sw.Elapsed.TotalMilliseconds;
            ATOLog.Info($"Post-processing complete: {sw.ElapsedMilliseconds}ms");
        }

        private void DeduplicateMaterials(ATOBuildContext atoCtx, BuildContext context)
        {
            var root = context.AvatarRootObject;
            var allMaterials = new Dictionary<string, List<Material>>();

            // Collect all materials from renderers
            foreach (var rendererInfo in atoCtx.Renderers)
            {
                if (rendererInfo.SharedMaterials == null) continue;
                foreach (var mat in rendererInfo.SharedMaterials)
                {
                    if (mat == null) continue;

                    string hash = GetMaterialHash(mat);
                    if (!allMaterials.ContainsKey(hash))
                        allMaterials[hash] = new List<Material>();
                    allMaterials[hash].Add(mat);
                }
            }

            int dedupCount = 0;
            foreach (var group in allMaterials.Values)
            {
                if (group.Count <= 1) continue;

                // Check if these materials can be safely merged
                // (no animation switching individual materials in the group)
                bool canMerge = true;
                if (atoCtx.AnimationAnalysis != null)
                {
                    foreach (var swap in atoCtx.AnimationAnalysis.MaterialSwaps)
                    {
                        // If animation switches between materials in this group, don't merge
                        bool hasSwapInGroup = swap.SwappedMaterials.Any(m => group.Contains(m));
                        bool hasOriginalInGroup = group.Contains(swap.OriginalMaterial);
                        if (hasSwapInGroup && hasOriginalInGroup)
                        {
                            canMerge = false;
                            break;
                        }
                    }
                }

                if (!canMerge) continue;

                var canonical = group[0];

                for (int i = 1; i < group.Count; i++)
                {
                    ReplaceMaterialReferences(atoCtx, group[i], canonical, context);
                    dedupCount++;
                }
            }

            if (dedupCount > 0)
            {
                ATOLog.Info($"Deduplicated {dedupCount} materials.");
                atoCtx.ReportEntries.Add(new ReportEntry
                {
                    Severity = ReportSeverity.Info,
                    Category = "Material Dedup / 材质去重",
                    Message = $"Merged {dedupCount} identical materials",
                    MessageZh = $"合并了{dedupCount}个相同的材质"
                });
            }
        }

        private void DeduplicateTextures(ATOBuildContext atoCtx)
        {
            // Deduplicate generated textures/atlases by content
            var texGroups = new Dictionary<string, List<Texture2D>>();

            foreach (var tex in atoCtx.GeneratedTextures)
            {
                if (tex == null) continue;
                string hash = TextureHelper.GetTextureContentHash(tex);
                if (!texGroups.ContainsKey(hash))
                    texGroups[hash] = new List<Texture2D>();
                texGroups[hash].Add(tex);
            }

            int dedupCount = 0;
            foreach (var group in texGroups.Values)
            {
                if (group.Count <= 1) continue;
                var canonical = group[0];

                for (int i = 1; i < group.Count; i++)
                {
                    ReplaceTextureInMaterials(atoCtx, group[i], canonical);
                    dedupCount++;
                }
            }

            if (dedupCount > 0)
            {
                ATOLog.Info($"Deduplicated {dedupCount} generated textures.");
            }
        }

        private void IntegrateAAOCompatibility(ATOBuildContext atoCtx)
        {
#if ATO_HAS_AAO
            try
            {
                // Register UV evacuation with AAO's UVUsageCompabilityAPI
                // For each modified mesh, register the UV channel evacuation
                foreach (var kvp in atoCtx.ModifiedMeshes)
                {
                    var originalMesh = kvp.Key;
                    var newMesh = kvp.Value;

                    // Find the renderer using this mesh
                    foreach (var rendererInfo in atoCtx.Renderers)
                    {
                        if (rendererInfo.SharedMesh == originalMesh &&
                            rendererInfo.Renderer is SkinnedMeshRenderer smr)
                        {
                            // Register UV evacuation for modified channels
                            var modifiedChannels = new HashSet<int>();
                            foreach (var island in atoCtx.AllIslands)
                            {
                                if (island.SourceMesh == originalMesh && island.NewUVs != null)
                                {
                                    modifiedChannels.Add(island.UvChannel);
                                }
                            }

                            foreach (int channel in modifiedChannels)
                            {
                                // Find an available evacuation channel
                                int evacChannel = FindEvacuationChannel(newMesh, channel);
                                if (evacChannel >= 0 && evacChannel != channel)
                                {
                                    // Copy original UVs to evacuation channel before modification
                                    CopyUVChannel(newMesh, channel, evacChannel);

                                    Anatawa12.AvatarOptimizer.API.UVUsageCompabilityAPI
                                        .RegisterTexCoordEvacuation(smr, channel, evacChannel);

                                    ATOLog.Verbose($"Registered UV{channel} → UV{evacChannel} evacuation for AAO.");
                                }
                            }
                        }
                    }
                }

                ATOLog.Info("AAO UVUsageCompabilityAPI integration complete.");
            }
            catch (System.Exception e)
            {
                ATOLog.Warning($"AAO integration failed (non-fatal): {e.Message}");
            }
#else
            ATOLog.Info("AAO not detected. Skipping UVUsageCompabilityAPI integration.");
#endif
        }

        private int FindEvacuationChannel(Mesh mesh, int usedChannel)
        {
            // Find an unused UV channel for evacuation
            for (int ch = 0; ch < 8; ch++)
            {
                if (ch == usedChannel) continue;

                var uvs = new List<Vector2>();
                mesh.GetUVs(ch, uvs);
                if (uvs.Count == 0) return ch; // Empty channel available
            }
            return -1; // No available channel
        }

        private void CopyUVChannel(Mesh mesh, int src, int dst)
        {
            var uvs = new List<Vector2>();
            mesh.GetUVs(src, uvs);
            if (uvs.Count > 0)
            {
                mesh.SetUVs(dst, uvs);
            }
        }

        private void RemoveATOComponent(BuildContext context)
        {
            var component = context.AvatarRootObject.GetComponent<AvatarTextureOptimizerComponent>();
            if (component != null)
            {
                Object.DestroyImmediate(component);
                ATOLog.Info("ATO component removed from build result.");
            }
        }

        private void GenerateReport(ATOBuildContext atoCtx)
        {
            // Add summary report
            int totalOriginalTexels = 0;
            int totalOptimizedTexels = 0;

            foreach (var texInfo in atoCtx.AllTextures)
            {
                if (texInfo.IsWhitelisted) continue;
                totalOriginalTexels += texInfo.Width * texInfo.Height;
            }

            foreach (var atlas in atoCtx.Atlases)
            {
                totalOptimizedTexels += atlas.Width * atlas.Height;
            }

            // Also count fallback scaled textures
            foreach (var tex in atoCtx.GeneratedTextures)
            {
                if (tex != null && !atoCtx.Atlases.Any(a => a.AtlasTexture == tex))
                {
                    totalOptimizedTexels += tex.width * tex.height;
                }
            }

            float savings = totalOriginalTexels > 0
                ? (1f - (float)totalOptimizedTexels / totalOriginalTexels) * 100f
                : 0;

            atoCtx.ReportEntries.Add(new ReportEntry
            {
                Severity = ReportSeverity.Info,
                Category = "Summary / 总结",
                Message = $"Texture texels: {totalOriginalTexels:N0} → {totalOptimizedTexels:N0} ({savings:F1}% reduction)",
                MessageZh = $"贴图纹素：{totalOriginalTexels:N0} → {totalOptimizedTexels:N0}（减少{savings:F1}%）",
                Details = BuildDetailedReport(atoCtx),
                DetailsZh = BuildDetailedReportZh(atoCtx)
            });

            // Add timing report
            string timingStr = "";
            foreach (var kvp in atoCtx.StageTimings.OrderBy(k => k.Key))
            {
                timingStr += $"  {kvp.Key}: {kvp.Value:F1}ms\n";
            }

            atoCtx.ReportEntries.Add(new ReportEntry
            {
                Severity = ReportSeverity.Info,
                Category = "Timing / 耗时",
                Message = $"Stage timings:\n{timingStr}",
                MessageZh = $"阶段耗时：\n{timingStr}"
            });

            // Add warnings
            foreach (var warning in atoCtx.Warnings)
            {
                atoCtx.ReportEntries.Add(new ReportEntry
                {
                    Severity = ReportSeverity.Warning,
                    Category = "Warning / 警告",
                    Message = warning
                });
            }

            // Output report to NDMF console
            ATOLog.Info("=== ATO Build Report ===");
            foreach (var entry in atoCtx.ReportEntries)
            {
                switch (entry.Severity)
                {
                    case ReportSeverity.Info:
                        ATOLog.Info($"[{entry.Category}] {entry.Message}");
                        break;
                    case ReportSeverity.Warning:
                        ATOLog.Warning($"[{entry.Category}] {entry.Message}");
                        break;
                    case ReportSeverity.Error:
                        ATOLog.Error($"[{entry.Category}] {entry.Message}");
                        break;
                }
            }
        }

        private string BuildDetailedReport(ATOBuildContext atoCtx)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("Atlas Details:");
            foreach (var atlas in atoCtx.Atlases)
            {
                sb.AppendLine($"  {atlas.Name}: {atlas.Width}x{atlas.Height}, " +
                             $"{atlas.IslandCount} islands, {atlas.Utilization:P1} utilization");
            }
            sb.AppendLine($"Whitelisted textures: {atoCtx.WhitelistedTextureIds.Count}");
            sb.AppendLine($"Total islands: {atoCtx.AllIslands.Count}");
            sb.AppendLine($"UV groups: {atoCtx.UVGroups.Count}");
            sb.AppendLine($"Type groups: {atoCtx.TextureTypeGroups.Count}");
            return sb.ToString();
        }

        private string BuildDetailedReportZh(ATOBuildContext atoCtx)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("图集详情：");
            foreach (var atlas in atoCtx.Atlases)
            {
                sb.AppendLine($"  {atlas.Name}: {atlas.Width}x{atlas.Height}, " +
                             $"{atlas.IslandCount}个岛, {atlas.Utilization:P1}利用率");
            }
            sb.AppendLine($"白名单贴图：{atoCtx.WhitelistedTextureIds.Count}");
            sb.AppendLine($"总岛数：{atoCtx.AllIslands.Count}");
            sb.AppendLine($"UV组：{atoCtx.UVGroups.Count}");
            sb.AppendLine($"类型组：{atoCtx.TextureTypeGroups.Count}");
            return sb.ToString();
        }

        private void ReplaceMaterialReferences(ATOBuildContext atoCtx, Material oldMat, Material newMat, BuildContext context)
        {
            foreach (var rendererInfo in atoCtx.Renderers)
            {
                if (rendererInfo.SharedMaterials == null) continue;
                var mats = rendererInfo.SharedMaterials;
                bool changed = false;

                for (int i = 0; i < mats.Length; i++)
                {
                    if (mats[i] == oldMat)
                    {
                        mats[i] = newMat;
                        changed = true;
                    }
                }

                if (changed)
                {
                    if (rendererInfo.Renderer is SkinnedMeshRenderer smr)
                        smr.sharedMaterials = mats;
                    else if (rendererInfo.Renderer is MeshRenderer mr)
                        mr.sharedMaterials = mats;
                }
            }
        }

        private void ReplaceTextureInMaterials(ATOBuildContext atoCtx, Texture2D oldTex, Texture2D newTex)
        {
            var checkedMaterials = new HashSet<Material>();
            foreach (var rendererInfo in atoCtx.Renderers)
            {
                if (rendererInfo.SharedMaterials == null) continue;
                foreach (var mat in rendererInfo.SharedMaterials)
                {
                    if (mat == null || checkedMaterials.Contains(mat)) continue;
                    checkedMaterials.Add(mat);

                    var shader = mat.shader;
                    if (shader == null) continue;

                    int propCount = shader.GetPropertyCount();
                    for (int i = 0; i < propCount; i++)
                    {
                        if (shader.GetPropertyType(i) == UnityEngine.Rendering.ShaderPropertyType.Texture)
                        {
                            var propName = shader.GetPropertyName(i);
                            if (mat.GetTexture(propName) == oldTex)
                            {
                                mat.SetTexture(propName, newTex);
                            }
                        }
                    }
                }
            }
        }

        private string GetMaterialHash(Material mat)
        {
            if (mat == null) return "";
            var sb = new System.Text.StringBuilder();
            sb.Append(mat.shader?.name ?? "");
            sb.Append("_");

            var shader = mat.shader;
            if (shader != null)
            {
                int propCount = shader.GetPropertyCount();
                for (int i = 0; i < propCount; i++)
                {
                    var propType = shader.GetPropertyType(i);
                    var propName = shader.GetPropertyName(i);

                    switch (propType)
                    {
                        case UnityEngine.Rendering.ShaderPropertyType.Texture:
                            var tex = mat.GetTexture(propName);
                            sb.Append($"{propName}:{tex?.GetInstanceID() ?? 0},");
                            break;
                        case UnityEngine.Rendering.ShaderPropertyType.Float:
                        case UnityEngine.Rendering.ShaderPropertyType.Range:
                            sb.Append($"{propName}:{mat.GetFloat(propName):F4},");
                            break;
                        case UnityEngine.Rendering.ShaderPropertyType.Color:
                            sb.Append($"{propName}:{mat.GetColor(propName)},");
                            break;
                        case UnityEngine.Rendering.ShaderPropertyType.Vector:
                            sb.Append($"{propName}:{mat.GetVector(propName)},");
                            break;
                    }
                }
            }

            return sb.ToString();
        }
    }
}
