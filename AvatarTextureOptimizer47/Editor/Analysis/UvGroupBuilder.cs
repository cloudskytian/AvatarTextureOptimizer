using System;
using System.Collections.Generic;
using System.Linq;
using Fosa.AvatarTextureOptimizer.Editor.Core;
using Fosa.AvatarTextureOptimizer.Editor.Reporting;
using UnityEngine;

namespace Fosa.AvatarTextureOptimizer.Editor.Analysis
{
    /// <summary>EN: Builds UV groups, islands, physical-density bounds, and connected texture type groups. ZH: 构建 UV 组、岛、物理密度边界与连通贴图类型组。</summary>
    internal static class UvGroupBuilder
    {
        public static void Build(BuildPlan plan, BuildProgress progress, AtoBuildReport report)
        {
            var nextGroupId = 0;
            var nextIslandId = 0;
            for (var rendererIndex = 0; rendererIndex < plan.Renderers.Count; rendererIndex++)
            {
                progress.Report("Building UV groups / 构建 UV 组", rendererIndex, Math.Max(1, plan.Renderers.Count));
                var renderer = plan.Renderers[rendererIndex];
                foreach (var slot in renderer.PossibleMaterials.Keys.OrderBy(x => x))
                {
                    var usages = renderer.PossibleMaterials[slot]
                        .Where(plan.Materials.ContainsKey)
                        .SelectMany(x => plan.Materials[x].Usages)
                        .Where(x => x.Renderers.Contains(renderer.Renderer))
                        .ToList();
                    foreach (var channelGroup in usages.GroupBy(x => x.UvChannel))
                    {
                        var group = new UvGroup
                        {
                            Id = nextGroupId++,
                            Renderer = renderer,
                            SubMesh = slot,
                            UvChannel = channelGroup.Key,
                        };
                        group.Usages.AddRange(channelGroup);
                        group.Materials.UnionWith(renderer.PossibleMaterials[slot].Where(x => x != null));
                        group.Whitelisted = !plan.Profile.generateAtlases || plan.Profile.quality.IsExact ||
                                            plan.AaoBlockedRenderers.Contains(renderer.Renderer) || group.Usages.Any(x => x.Protected) ||
                                            renderer.PossibleMaterials[slot].Any(x => x != null && plan.Materials.TryGetValue(x, out var record) && record.Whitelisted);
                        if (plan.Profile.generateAtlases && plan.Profile.quality.IsExact)
                            group.FallbackReason = "Exact quality requires original, non-resampled texture copies";

                        var optimizable = group.Usages.Where(x => !x.Protected).ToList();
                        if (optimizable.Count > 0)
                        {
                            if (renderer.SourceMesh.GetTopology(slot) != MeshTopology.Triangles)
                            {
                                group.Whitelisted = true;
                                group.FallbackReason = "Submesh topology is not triangles";
                                ProtectGroup(plan, group);
                                report.Warn($"{renderer.Renderer.name} slot {slot} is not triangle topology; textures remain unchanged.", renderer.Renderer);
                            }
                            else
                            {
                                var islands = UvIslandExtractor.Extract(renderer.SourceMesh, slot, group.UvChannel, group.Id,
                                    out var translation, out var failure);
                                group.IntegerTranslation = translation;
                                if (!string.IsNullOrEmpty(failure))
                                {
                                    group.Whitelisted = true;
                                    group.FallbackReason = failure;
                                    ProtectGroup(plan, group);
                                    report.Warn($"{renderer.Renderer.name} slot {slot} UV{group.UvChannel}: {failure}; textures were protected.", renderer.Renderer);
                                }
                                else
                                {
                                    if (translation != Vector2.zero && optimizable.Any(x => x.Texture.wrapModeU != TextureWrapMode.Repeat || x.Texture.wrapModeV != TextureWrapMode.Repeat))
                                    {
                                        group.Whitelisted = true;
                                        group.FallbackReason = "Out-of-range UV translation requires Repeat sampling";
                                        ProtectGroup(plan, group);
                                        report.Warn($"{renderer.Renderer.name} slot {slot} uses out-of-range UVs without Repeat sampling; textures were protected.", renderer.Renderer);
                                    }
                                    var areaByTriangle = MorphAreaAnalyzer.Build(renderer.SourceMesh,
                                        renderer.SourceMesh.GetTriangles(slot, true));
                                    foreach (var island in islands)
                                    {
                                        island.Id = nextIslandId++;
                                        InitializeIslandMetrics(group, island, plan.Profile, areaByTriangle);
                                        group.Islands.Add(island);
                                    }
                                    if (plan.Profile.quality.IsExact && group.Islands.Any(island => group.Usages.Where(x => !x.Protected)
                                        .Select(x => (Mathf.CeilToInt(island.UvBounds.width * x.Texture.width),
                                            Mathf.CeilToInt(island.UvBounds.height * x.Texture.height))).Distinct().Count() > 1))
                                    {
                                        group.Whitelisted = true;
                                        group.FallbackReason = "Exact quality textures have incompatible physical island sizes";
                                        report.Warn($"{renderer.Renderer.name} slot {slot} uses exact quality with mismatched texture sizes; atlas resampling was disabled.", renderer.Renderer);
                                    }
                                }
                            }
                        }
                        plan.UvGroups.Add(group);
                    }
                }
            }
            report.IslandCount = plan.UvGroups.Sum(x => x.Islands.Count);
            PropagateFallbacks(plan.UvGroups);
            BuildTypeGroups(plan);
        }

        private static void InitializeIslandMetrics(UvGroup group, UvIsland island, PlatformProfile profile,
            IReadOnlyDictionary<(int a, int b, int c), float> areaByTriangle)
        {
            var area = 0f;
            foreach (var triangle in island.Triangles)
                if (areaByTriangle.TryGetValue((triangle.A, triangle.B, triangle.C), out var value)) area += value;
            island.ModelArea = area * group.Renderer.MaximumAreaScale;

            var maxWidth = 1;
            var maxHeight = 1;
            foreach (var usage in group.Usages.Where(x => !x.Protected))
            {
                maxWidth = Math.Max(maxWidth, Mathf.CeilToInt(island.UvBounds.width * usage.Texture.width));
                maxHeight = Math.Max(maxHeight, Mathf.CeilToInt(island.UvBounds.height * usage.Texture.height));
            }
            island.SourcePixelSize = new Vector2Int(maxWidth, maxHeight);
            island.MinimumDensityPixelSize = DensitySize(island.ModelArea, maxWidth, maxHeight, profile.minimumPixelDensity, island.SourcePixelSize);
            island.MaximumDensityPixelSize = DensitySize(island.ModelArea, maxWidth, maxHeight, profile.maximumPixelDensity, island.SourcePixelSize);
            island.TargetPixelSize = island.MaximumDensityPixelSize;
            if (profile.quality.IsExact)
            {
                island.MinimumDensityPixelSize = island.SourcePixelSize;
                island.MaximumDensityPixelSize = island.SourcePixelSize;
                island.TargetPixelSize = island.SourcePixelSize;
            }
        }

        private static Vector2Int DensitySize(float modelArea, int sourceWidth, int sourceHeight, int density, Vector2Int sourceCap)
        {
            if (modelArea <= 1e-12f) return new Vector2Int(Math.Min(4, sourceCap.x), Math.Min(4, sourceCap.y));
            var aspect = Mathf.Max(1e-6f, (float)sourceWidth / Math.Max(1, sourceHeight));
            var pixelArea = modelArea * density * density;
            var width = Mathf.CeilToInt(Mathf.Sqrt(pixelArea * aspect));
            var height = Mathf.CeilToInt(Mathf.Sqrt(pixelArea / aspect));
            return new Vector2Int(Mathf.Clamp(width, 1, sourceCap.x), Mathf.Clamp(height, 1, sourceCap.y));
        }

        private static void ProtectGroup(BuildPlan plan, UvGroup group)
        {
            foreach (var usage in group.Usages) plan.ProtectedTextures.Add(usage.Texture);
            foreach (var usage in plan.Materials.Values.SelectMany(x => x.Usages))
                if (plan.ProtectedTextures.Contains(usage.Texture)) usage.Protected = true;
        }

        private static void PropagateFallbacks(IReadOnlyList<UvGroup> groups)
        {
            var union = new UnionFind(groups.Count);
            var textures = new Dictionary<Texture2D, int>();
            var materials = new Dictionary<Material, int>();
            for (var i = 0; i < groups.Count; i++)
            {
                foreach (var texture in groups[i].Usages.Select(x => x.Texture).Distinct())
                { if (textures.TryGetValue(texture, out var prior)) union.Union(i, prior); else textures[texture] = i; }
                foreach (var material in groups[i].Materials)
                { if (materials.TryGetValue(material, out var prior)) union.Union(i, prior); else materials[material] = i; }
            }
            foreach (var component in union.Groups().Values)
            {
                if (!component.Any(x => groups[x].Whitelisted)) continue;
                foreach (var index in component)
                {
                    groups[index].Whitelisted = true;
                    if (string.IsNullOrEmpty(groups[index].FallbackReason))
                        groups[index].FallbackReason = "Connected UV/material/texture group requires fallback";
                }
            }
        }

        private static void BuildTypeGroups(BuildPlan plan)
        {
            var candidates = plan.UvGroups.Where(x => !x.Whitelisted && x.Islands.Count > 0).ToList();
            var union = new UnionFind(candidates.Count);
            var byTexture = new Dictionary<Texture2D, int>();
            var byMaterial = new Dictionary<Material, int>();
            for (var i = 0; i < candidates.Count; i++)
            {
                foreach (var texture in candidates[i].Usages.Select(x => x.Texture).Distinct())
                {
                    if (byTexture.TryGetValue(texture, out var previous)) union.Union(i, previous); else byTexture[texture] = i;
                }
                foreach (var material in candidates[i].Materials)
                {
                    if (byMaterial.TryGetValue(material, out var previous)) union.Union(i, previous); else byMaterial[material] = i;
                }
            }

            var bySignature = new Dictionary<string, TextureTypeGroup>(StringComparer.Ordinal);
            foreach (var component in union.Groups().Values)
            {
                var uvGroups = component.Select(x => candidates[x]).ToList();
                var signature = string.Join("|", uvGroups.SelectMany(x => x.Usages)
                    .Select(x => $"{ShaderAnalyzer.CanonicalRole(x.PropertyName)}:{x.Semantic}:{x.IsSrgb}:{x.FilterMode}:{x.UsedChannelMask}")
                    .Distinct().OrderBy(x => x, StringComparer.Ordinal));
                if (!bySignature.TryGetValue(signature, out var typeGroup))
                {
                    typeGroup = new TextureTypeGroup { Key = signature };
                    bySignature.Add(signature, typeGroup);
                    plan.TypeGroups.Add(typeGroup);
                }
                typeGroup.UvGroups.AddRange(uvGroups);
                typeGroup.PackingAtoms.Add(uvGroups);
            }
        }
    }
}
