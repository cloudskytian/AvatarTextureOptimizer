using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Unity.Collections;
using UnityEditor;
using UnityEngine;
using Fosa.AvatarTextureOptimizer;

namespace Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// Generates per-renderer UV-compatible atlas families and applies only texture/UV edits. / 生成每个渲染器 UV 兼容的图集族，仅应用纹理与 UV 修改。
    /// </summary>
    internal static class AtlasPipeline
    {
        public static void Generate(BuildSnapshot snapshot, ATOBuildSession.BuildContextAdapter context,
            AvatarTextureOptimizer component, ATOPlatformOptions options, ATOLogger logger, ATOProgress progress,
            ATOBuildReport report)
        {
            int atlasSerial = 0;
            for (int rendererIndex = 0; rendererIndex < snapshot.Renderers.Count; rendererIndex++)
            {
                RendererRecord renderer = snapshot.Renderers[rendererIndex];
                if (renderer.SkipAll) continue;
                List<int> channels = snapshot.Islands.Where(i => i.Material.Owner == renderer)
                    .Select(i => i.UVChannel).Distinct().OrderBy(i => i).ToList();
                for (int channelIndex = 0; channelIndex < channels.Count; channelIndex++)
                {
                    int channel = channels[channelIndex];
                    List<IslandRecord> islands = snapshot.Islands
                        .Where(i => i.Material.Owner == renderer && i.UVChannel == channel && !i.SkipAtlas)
                        .ToList();
                    bool blocked = renderer.UnsafeUVChannels.Contains(channel) ||
                                   snapshot.Islands.Any(i => i.Material.Owner == renderer && i.UVChannel == channel && i.SkipAtlas) ||
                                   HasSharedVertexAcrossIslands(islands);
                    if (blocked || islands.Count == 0)
                    {
                        if (blocked)
                        {
                            report.FallbackCount++;
                            logger.Warning("UV channel " + channel + " on renderer '" + renderer.Renderer.name +
                                           "' cannot be safely remapped; all related textures use fallback. / UV 通道无法安全重映射，相关纹理回退。");
                        }
                        continue;
                    }

                    List<List<IslandRecord>> typeGroups = islands.GroupBy(i => i.TypeGroupKey ?? string.Empty)
                        .Select(g => g.ToList()).ToList();
                    for (int groupIndex = 0; groupIndex < typeGroups.Count; groupIndex++)
                    {
                        List<IslandRecord> pending = typeGroups[groupIndex]
                            .OrderByDescending(i => i.OutputWidth * (long)i.OutputHeight).ToList();
                        while (pending.Count > 0)
                        {
                            List<IslandRecord> queue = new List<IslandRecord> { pending[0] };
                            AtlasPackingResult queuePacking = AtlasPacker.TryPack(queue, EffectiveMaxAtlasSize(options),
                                options.atlasMinimumSize, options.experimentalNpotAtlases, component.minimumPadding, 4, logger, progress);
                            if (queuePacking == null)
                            {
                                report.FallbackCount++;
                                pending.RemoveAt(0);
                                logger.Warning("Island on renderer '" + renderer.Renderer.name + "' cannot fit the maximum atlas; kept as optimized standalone texture. / UV 岛无法装入最大图集，保留为独立优化纹理。");
                                continue;
                            }

                            int candidateIndex = 1;
                            while (candidateIndex < pending.Count)
                            {
                                List<IslandRecord> test = new List<IslandRecord>(queue) { pending[candidateIndex] };
                                AtlasPackingResult testPacking = AtlasPacker.TryPack(test, EffectiveMaxAtlasSize(options),
                                    options.atlasMinimumSize, options.experimentalNpotAtlases, component.minimumPadding, 4, logger, progress);
                                if (testPacking == null) break;
                                queue = test;
                                queuePacking = testPacking;
                                candidateIndex++;
                            }

                            if (ApplyPackedQueue(queue, queuePacking, renderer, channel, atlasSerial++, context, component,
                                options, snapshot, logger, report))
                            {
                                pending.RemoveRange(0, queue.Count);
                            }
                            else
                            {
                                report.FallbackCount++;
                                pending.RemoveAt(0);
                                logger.Warning("Atlas queue was rejected by UV conflict or output failure; the first island falls back. / 图集队列因 UV 冲突或输出失败被拒绝，首个岛回退。");
                            }
                        }
                    }
                }
                progress.Step(0.02f + 0.75f * ((rendererIndex + 1) / (float)Math.Max(1, snapshot.Renderers.Count)),
                    "Generate atlases " + (rendererIndex + 1) + "/" + snapshot.Renderers.Count + " / 生成图集");
            }

            TexturePipeline.OptimizeFallbackReferences(snapshot, context, component, options, logger, report);
        }

        private static bool HasSharedVertexAcrossIslands(IList<IslandRecord> islands)
        {
            Dictionary<int, IslandRecord> owners = new Dictionary<int, IslandRecord>();
            for (int i = 0; i < islands.Count; i++)
            {
                for (int j = 0; j < islands[i].Triangles.Count; j++)
                {
                    IslandTriangle triangle = islands[i].Triangles[j];
                    int[] vertices = { triangle.A, triangle.B, triangle.C };
                    for (int k = 0; k < vertices.Length; k++)
                    {
                        IslandRecord existing;
                        if (owners.TryGetValue(vertices[k], out existing) && existing != islands[i]) return true;
                        owners[vertices[k]] = islands[i];
                    }
                }
            }
            return false;
        }

        private static bool ApplyPackedQueue(IList<IslandRecord> islands, AtlasPackingResult packing,
            RendererRecord renderer, int channel, int serial, ATOBuildSession.BuildContextAdapter context,
            AvatarTextureOptimizer component, ATOPlatformOptions options, BuildSnapshot snapshot, ATOLogger logger,
            ATOBuildReport report)
        {
            Dictionary<int, Vector2> assignments = new Dictionary<int, Vector2>();
            for (int i = 0; i < packing.Placements.Count; i++)
            {
                AtlasPlacement placement = packing.Placements[i];
                for (int triangleIndex = 0; triangleIndex < placement.Island.Triangles.Count; triangleIndex++)
                {
                    IslandTriangle triangle = placement.Island.Triangles[triangleIndex];
                    if (!AddAssignment(assignments, triangle.A, ToAtlasUV(placement, triangle.UVA, placement.Island),
                        logger) ||
                        !AddAssignment(assignments, triangle.B, ToAtlasUV(placement, triangle.UVB, placement.Island),
                            logger) ||
                        !AddAssignment(assignments, triangle.C, ToAtlasUV(placement, triangle.UVC, placement.Island),
                            logger)) return false;
                }
            }

            if (renderer.IsSkinned && !AAOUVCompatibility.Prepare((SkinnedMeshRenderer)renderer.Renderer, channel, renderer, logger))
                return false;

            List<TextureAssetInfo> channels = new List<TextureAssetInfo>();
            for (int islandIndex = 0; islandIndex < islands.Count; islandIndex++)
            {
                foreach (TextureReference reference in islands[islandIndex].Material.References)
                {
                    if (reference.Texture == null || channels.Contains(reference.Texture)) continue;
                    channels.Add(reference.Texture);
                }
            }
            if (channels.Count == 0) return false;

            Dictionary<TextureAssetInfo, Texture2D> generated = new Dictionary<TextureAssetInfo, Texture2D>();
            for (int textureIndex = 0; textureIndex < channels.Count; textureIndex++)
            {
                TextureAssetInfo texture = channels[textureIndex];
                ATOTextureCategory category = CategoryFor(texture, islands);
                string name = "ATO_" + Sanitize(texture.DisplayName) + "_R" + renderer.Renderer.GetInstanceID() +
                              "_UV" + channel + "_A" + serial;
                Texture2D atlas = GeneratedTextureWriter.CreateAndSave(context, packing.Width, packing.Height, name,
                    (raw, covered) => FillAtlas(raw, covered, packing, texture, snapshot, logger), category,
                    ATOPlatformResolver.Current(), options, texture, logger);
                if (atlas == null)
                {
                    for (int generatedIndex = 0; generatedIndex < generated.Count; generatedIndex++)
                    {
                        // Persistent generated PNGs are harmless and will be replaced on the next NDMF build.
                        // 已生成的持久化 PNG 无害，下次 NDMF 构建会替换它们。
                    }
                    return false;
                }
                generated.Add(texture, atlas);
            }

            ApplyMeshAssignments(renderer, channel, assignments, context, logger);
            for (int islandIndex = 0; islandIndex < islands.Count; islandIndex++)
            {
                IslandRecord island = islands[islandIndex];
                island.AtlasIndex = serial;
                for (int referenceIndex = 0; referenceIndex < island.Material.References.Count; referenceIndex++)
                {
                    TextureReference reference = island.Material.References[referenceIndex];
                    Texture2D atlas;
                    if (!generated.TryGetValue(reference.Texture, out atlas)) continue;
                    reference.OptimizedTexture = atlas;
                    reference.AtlasAssigned = true;
                    Material material = GetWorkingMaterial(island.Material, context, component, logger);
                    material.SetTexture(reference.PropertyName, atlas);
                }
            }

            ApplyWorkingMaterials(renderer);
            long sourcePixels = 0;
            for (int i = 0; i < islands.Count; i++) sourcePixels += (long)islands[i].OutputWidth * islands[i].OutputHeight;
            report.AddAtlas("ATO_" + Sanitize(renderer.Renderer.name) + "_UV" + channel + "_A" + serial,
                packing.Width, packing.Height, islands.Count, sourcePixels, (long)packing.Width * packing.Height,
                packing.Utilization);
            logger.Info("Atlas sources for " + renderer.Renderer.name + ": " +
                        string.Join(", ", channels.Select(c => c.DisplayName).ToArray()) + "; size=" + packing.Width + "x" +
                        packing.Height + "; utilization=" + packing.Utilization.ToString("P1") + ". / 图集来源与统计已记录。");
            return true;
        }

        private static void ApplyMeshAssignments(RendererRecord renderer, int channel, Dictionary<int, Vector2> assignments,
            ATOBuildSession.BuildContextAdapter context, ATOLogger logger)
        {
            Mesh mesh = EnsureWorkingMesh(renderer, context);
            List<Vector4> uv = new List<Vector4>();
            mesh.GetUVs(channel, uv);
            if (uv.Count != mesh.vertexCount)
            {
                if (channel == 0 && mesh.uv != null && mesh.uv.Length == mesh.vertexCount)
                {
                    uv.Clear();
                    Vector2[] source = mesh.uv;
                    for (int i = 0; i < source.Length; i++) uv.Add(new Vector4(source[i].x, source[i].y, 0f, 0f));
                }
                else
                {
                    logger.Warning("Working mesh has no writable UV channel; atlas output is not assigned. / 工作网格没有可写 UV 通道，图集不赋值。");
                    return;
                }
            }
            foreach (KeyValuePair<int, Vector2> assignment in assignments)
            {
                Vector4 value = uv[assignment.Key];
                value.x = assignment.Value.x;
                value.y = assignment.Value.y;
                uv[assignment.Key] = value;
            }
            mesh.SetUVs(channel, uv);
        }

        private static Mesh EnsureWorkingMesh(RendererRecord renderer, ATOBuildSession.BuildContextAdapter context)
        {
            if (renderer.WorkingMesh != null) return renderer.WorkingMesh;
            renderer.WorkingMesh = UnityEngine.Object.Instantiate(renderer.SourceMesh);
            renderer.WorkingMesh.name = "ATO_" + renderer.SourceMesh.name + "_Mesh";
            if (renderer.IsSkinned)
            {
                ((SkinnedMeshRenderer)renderer.Renderer).sharedMesh = renderer.WorkingMesh;
            }
            else
            {
                MeshFilter filter = renderer.Renderer.GetComponent<MeshFilter>();
                if (filter != null) filter.sharedMesh = renderer.WorkingMesh;
            }
            context.RegisterReplacement(renderer.SourceMesh, renderer.WorkingMesh);
            return renderer.WorkingMesh;
        }

        private static Material GetWorkingMaterial(MaterialUse use, ATOBuildSession.BuildContextAdapter context,
            AvatarTextureOptimizer component, ATOLogger logger)
        {
            if (use.WorkingMaterial != null) return use.WorkingMaterial;
            use.WorkingMaterial = UnityEngine.Object.Instantiate(use.SourceMaterial);
            use.WorkingMaterial.name = "ATO_" + use.SourceMaterial.name + "_Material";
            context.RegisterReplacement(use.SourceMaterial, use.WorkingMaterial);
            return use.WorkingMaterial;
        }

        private static void ApplyWorkingMaterials(RendererRecord renderer)
        {
            Material[] materials = renderer.Renderer.sharedMaterials;
            for (int i = 0; i < renderer.Materials.Count; i++)
            {
                MaterialUse use = renderer.Materials[i];
                if (use.WorkingMaterial != null && use.Slot >= 0 && use.Slot < materials.Length) materials[use.Slot] = use.WorkingMaterial;
            }
            renderer.Renderer.sharedMaterials = materials;
        }

        private static bool AddAssignment(Dictionary<int, Vector2> assignments, int vertex, Vector2 value, ATOLogger logger)
        {
            Vector2 existing;
            if (assignments.TryGetValue(vertex, out existing))
            {
                if ((existing - value).sqrMagnitude > 1e-8f)
                {
                    logger.Warning("One mesh vertex requires two atlas positions; this UV channel falls back to preserve correctness. / 同一网格顶点需要两个图集位置，UV 通道回退以保证正确性。");
                    return false;
                }
                return true;
            }
            assignments.Add(vertex, value);
            return true;
        }

        private static Vector2 ToAtlasUV(AtlasPlacement placement, Vector2 uv, IslandRecord island)
        {
            Vector2 normalized = uv + island.UVTranslation;
            float u = island.UVBounds.width <= 1e-8f ? 0.5f : Mathf.InverseLerp(island.UVBounds.xMin, island.UVBounds.xMax, normalized.x);
            float v = island.UVBounds.height <= 1e-8f ? 0.5f : Mathf.InverseLerp(island.UVBounds.yMin, island.UVBounds.yMax, normalized.y);
            float px;
            float py;
            if (placement.Rotated)
            {
                px = placement.X + (1f - v) * placement.ContentWidth;
                py = placement.Y + u * placement.ContentHeight;
            }
            else
            {
                px = placement.X + u * placement.ContentWidth;
                py = placement.Y + v * placement.ContentHeight;
            }
            return new Vector2(px / placement.AtlasWidth, py / placement.AtlasHeight);
        }

        private static void FillAtlas(NativeArray<Color32> raw, BitArray covered, AtlasPackingResult packing,
            TextureAssetInfo texture, BuildSnapshot snapshot, ATOLogger logger)
        {
            for (int placementIndex = 0; placementIndex < packing.Placements.Count; placementIndex++)
            {
                AtlasPlacement placement = packing.Placements[placementIndex];
                TextureReference reference = placement.Island.Material.References.FirstOrDefault(r => r.Texture == texture);
                if (reference == null || reference.Texture == null) continue;
                TexturePixelData source = snapshot.PixelCache.Get(reference.Texture.Source, logger);
                if (source == null) continue;
                int originalWidth = placement.Rotated ? placement.ContentHeight : placement.ContentWidth;
                int originalHeight = placement.Rotated ? placement.ContentWidth : placement.ContentHeight;
                for (int y = 0; y < placement.ContentHeight; y++)
                {
                    for (int x = 0; x < placement.ContentWidth; x++)
                    {
                        float localU;
                        float localV;
                        if (placement.Rotated)
                        {
                            localU = (y + 0.5f) / Mathf.Max(1, originalWidth);
                            localV = 1f - (x + 0.5f) / Mathf.Max(1, originalHeight);
                        }
                        else
                        {
                            localU = (x + 0.5f) / Mathf.Max(1, originalWidth);
                            localV = (y + 0.5f) / Mathf.Max(1, originalHeight);
                        }
                        Rect bounds = placement.Island.UVBounds;
                        float sourceU = bounds.xMin + bounds.width * localU;
                        float sourceV = bounds.yMin + bounds.height * localV;
                        if (!ContainsIslandPoint(placement.Island, new Vector2(sourceU, sourceV))) continue;
                        Color32 value = AtlasPixelSampler.Sample(source, sourceU, sourceV, reference.Category);
                        int px = placement.X + x;
                        int py = placement.Y + y;
                        if (px < 0 || py < 0 || px >= packing.Width || py >= packing.Height) continue;
                        int index = py * packing.Width + px;
                        raw[index] = value;
                        covered[index] = true;
                    }
                }
            }
        }

        private static bool ContainsIslandPoint(IslandRecord island, Vector2 point)
        {
            for (int i = 0; i < island.Triangles.Count; i++)
            {
                IslandTriangle triangle = island.Triangles[i];
                Vector2 a = triangle.UVA + island.UVTranslation;
                Vector2 b = triangle.UVB + island.UVTranslation;
                Vector2 c = triangle.UVC + island.UVTranslation;
                float d1 = Sign(point, a, b);
                float d2 = Sign(point, b, c);
                float d3 = Sign(point, c, a);
                bool negative = d1 < 0f || d2 < 0f || d3 < 0f;
                bool positive = d1 > 0f || d2 > 0f || d3 > 0f;
                if (!(negative && positive)) return true;
            }
            return false;
        }

        private static float Sign(Vector2 p1, Vector2 p2, Vector2 p3)
        {
            return (p1.x - p3.x) * (p2.y - p3.y) - (p2.x - p3.x) * (p1.y - p3.y);
        }

        private static ATOTextureCategory CategoryFor(TextureAssetInfo texture, IList<IslandRecord> islands)
        {
            ATOTextureCategory result = ATOTextureCategory.Opaque;
            for (int i = 0; i < islands.Count; i++)
                for (int j = 0; j < islands[i].Material.References.Count; j++)
                {
                    TextureReference reference = islands[i].Material.References[j];
                    if (reference.Texture != texture) continue;
                    if (reference.Category == ATOTextureCategory.Normal) return ATOTextureCategory.Normal;
                    if (reference.Category == ATOTextureCategory.Transparent) result = ATOTextureCategory.Transparent;
                    else if (reference.Category == ATOTextureCategory.Grayscale && result == ATOTextureCategory.Opaque)
                        result = ATOTextureCategory.Grayscale;
                }
            return result;
        }

        private static int EffectiveMaxAtlasSize(ATOPlatformOptions options)
        {
            int max = options == null ? 8192 : options.maxAtlasSize;
            if (ATOPlatformResolver.Current() == ATOPlatform.Android) max = Mathf.Min(max, 4096);
            return Mathf.Clamp(max, 64, 8192);
        }

        private static string Sanitize(string name)
        {
            if (string.IsNullOrEmpty(name)) return "Texture";
            return new string(name.Select(c => char.IsLetterOrDigit(c) || c == '_' || c == '-' ? c : '_').ToArray());
        }
    }

    internal static class AtlasPixelSampler
    {
        public static Color32 Sample(TexturePixelData data, float u, float v, ATOTextureCategory category)
        {
            if (category == ATOTextureCategory.Normal)
            {
                Vector3 normal = Vector3.zero;
                AddNormal(ref normal, GetBilinear(data, u - 0.25f / data.Width, v - 0.25f / data.Height));
                AddNormal(ref normal, GetBilinear(data, u + 0.25f / data.Width, v - 0.25f / data.Height));
                AddNormal(ref normal, GetBilinear(data, u - 0.25f / data.Width, v + 0.25f / data.Height));
                AddNormal(ref normal, GetBilinear(data, u + 0.25f / data.Width, v + 0.25f / data.Height));
                normal = normal.sqrMagnitude < 1e-8f ? Vector3.forward : normal.normalized;
                return new Color32((byte)Mathf.RoundToInt((normal.x * 0.5f + 0.5f) * 255f),
                    (byte)Mathf.RoundToInt((normal.y * 0.5f + 0.5f) * 255f),
                    (byte)Mathf.RoundToInt((normal.z * 0.5f + 0.5f) * 255f), 255);
            }

            Color[] samples =
            {
                ToLinear(GetBilinear(data, u - 0.25f / data.Width, v - 0.25f / data.Height)),
                ToLinear(GetBilinear(data, u + 0.25f / data.Width, v - 0.25f / data.Height)),
                ToLinear(GetBilinear(data, u - 0.25f / data.Width, v + 0.25f / data.Height)),
                ToLinear(GetBilinear(data, u + 0.25f / data.Width, v + 0.25f / data.Height))
            };
            Color result = Color.clear;
            for (int i = 0; i < samples.Length; i++)
            {
                Color sample = samples[i];
                result.r += sample.r * sample.a;
                result.g += sample.g * sample.a;
                result.b += sample.b * sample.a;
                result.a += sample.a;
            }
            result *= 0.25f;
            if (result.a > 1e-5f)
            {
                result.r /= result.a;
                result.g /= result.a;
                result.b /= result.a;
            }
            return new Color32((byte)Mathf.Clamp(Mathf.RoundToInt(Mathf.LinearToGammaSpace(Mathf.Clamp01(result.r)) * 255f), 0, 255),
                (byte)Mathf.Clamp(Mathf.RoundToInt(Mathf.LinearToGammaSpace(Mathf.Clamp01(result.g)) * 255f), 0, 255),
                (byte)Mathf.Clamp(Mathf.RoundToInt(Mathf.LinearToGammaSpace(Mathf.Clamp01(result.b)) * 255f), 0, 255),
                (byte)Mathf.Clamp(Mathf.RoundToInt(Mathf.Clamp01(result.a) * 255f), 0, 255));
        }

        private static void AddNormal(ref Vector3 target, Color32 color)
        {
            target += new Vector3(color.r / 255f * 2f - 1f, color.g / 255f * 2f - 1f, color.b / 255f * 2f - 1f);
        }

        private static Color GetBilinear(TexturePixelData data, float u, float v)
        {
            float x = Mathf.Clamp01(u) * (data.Width - 1);
            float y = Mathf.Clamp01(v) * (data.Height - 1);
            int x0 = Mathf.FloorToInt(x);
            int y0 = Mathf.FloorToInt(y);
            int x1 = Mathf.Min(data.Width - 1, x0 + 1);
            int y1 = Mathf.Min(data.Height - 1, y0 + 1);
            float tx = x - x0;
            float ty = y - y0;
            Color a = data.Get(x0, y0);
            Color b = data.Get(x1, y0);
            Color c = data.Get(x0, y1);
            Color d = data.Get(x1, y1);
            return Color.Lerp(Color.Lerp(a, b, tx), Color.Lerp(c, d, tx), ty);
        }

        private static Color ToLinear(Color color)
        {
            return new Color(Mathf.GammaToLinearSpace(color.r), Mathf.GammaToLinearSpace(color.g),
                Mathf.GammaToLinearSpace(color.b), color.a);
        }
    }
}
