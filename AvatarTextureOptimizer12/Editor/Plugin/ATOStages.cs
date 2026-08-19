// SPDX-License-Identifier: MIT
// AvatarTextureOptimizer (ATO) - Pipeline stages used by the main pass.
// AvatarTextureOptimizer (ATO) - 主 Pass 使用的各流水线阶段。

using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf;
using Net.Fosa.AvatarTextureOptimizer.Editor.Analysis;
using Net.Fosa.AvatarTextureOptimizer.Editor.Atlas;
using Net.Fosa.AvatarTextureOptimizer.Editor.Core;
using Net.Fosa.AvatarTextureOptimizer.Editor.MeshOps;
using Net.Fosa.AvatarTextureOptimizer.Editor.Quality;
using Unity.Mathematics;
using UnityEngine;

namespace Net.Fosa.AvatarTextureOptimizer.Editor.Plugin
{
    /// <summary>EN: Builds UV islands for every UV stream. ZH: 为每条 UV 流构建 UV 岛。</summary>
    public static class IslandStage
    {
        public static void Build(UsageGraph graph, List<RendererEntry> renderers,
            Dictionary<UVSlotKey, UVIslandSet> islandSets,
            Dictionary<TextureUsage, List<IslandPlan>> plansByTexture,
            Dictionary<UVIsland, float> worldAreaByIsland,
            ATOProgress progress)
        {
            int done = 0;
            foreach (var kv in graph.UvToTextures)
            {
                progress.Report(done++, graph.UvToTextures.Count);

                var key = kv.Key;
                var textures = kv.Value
                    .Select(t => graph.Textures.TryGetValue(t, out var u) ? u : null)
                    .Where(u => u != null && !u.Excluded)
                    .ToList();
                if (textures.Count == 0) continue;

                var entry = renderers.FirstOrDefault(r => ReferenceEquals(r.Renderer, key.Renderer));
                if (entry == null) continue;

                var mesh = entry.Mesh;
                if (key.SubMesh >= mesh.subMeshCount) continue;

                var uvList = new List<Vector2>();
                mesh.GetUVs(key.UvChannel, uvList);
                if (uvList.Count == 0) continue;

                var triangles = mesh.GetTriangles(key.SubMesh);
                if (triangles.Length == 0) continue;

                // EN: Islands are defined in mesh space, so one set is shared by every texture on this UV.
                // ZH: 岛定义在网格空间，因此该 UV 上的所有贴图共享同一份岛集合。
                var reference = textures[0].Texture;
                var set = UVIslandBuilder.Build(triangles, uvList.ToArray(), reference.width, reference.height);
                UVIslandBuilder.MergeOverlapping(set, reference.width, reference.height);

                if (set.HasCrossSeamIsland)
                {
                    // EN: Cross-seam islands depend on repeat sampling; the whole UV stream is untouchable.
                    // ZH: 跨缝的岛依赖 repeat 采样；整条 UV 流都不能动。
                    ATOReportUtil.Warn("ATO:warn:cross_seam_uv", key.Renderer, key.UvChannel);
                    foreach (var t in textures) t.Excluded = true;
                    set.Dispose();
                    continue;
                }

                islandSets[key] = set;

                var areas = MeshMetrics.ComputeTriangleWorldAreas(entry.Renderer, mesh, triangles,
                    entry.MaxAnimatedScale);
                foreach (var island in set.Islands)
                {
                    float area = 0f;
                    foreach (var t in island.TriangleIds) area += areas[t];
                    worldAreaByIsland[island] = area;
                }

                foreach (var usage in textures)
                {
                    if (!plansByTexture.TryGetValue(usage, out var list))
                        plansByTexture[usage] = list = new List<IslandPlan>();

                    foreach (var island in set.Islands)
                    {
                        // EN: Each texture gets its own plan over the *same* island geometry, so the UV group
                        //     stays in lock-step while source rects follow each texture's own resolution.
                        // ZH: 每张贴图在“同一份”岛几何上各有一份计划，
                        //     从而 UV 组保持同步，而源矩形按各自贴图的分辨率取值。
                        var perTextureIsland = island;
                        var rect = SourceRect(perTextureIsland, usage.Texture.width, usage.Texture.height);
                        list.Add(new IslandPlan
                        {
                            Texture = usage,
                            Set = set,
                            Island = perTextureIsland,
                            SourceRect = rect,
                        });
                    }
                }
            }
        }

        private static RectInt SourceRect(UVIsland island, int texW, int texH)
        {
            int x0 = Mathf.Clamp(Mathf.FloorToInt(island.Min.x * texW), 0, Mathf.Max(0, texW - 1));
            int y0 = Mathf.Clamp(Mathf.FloorToInt(island.Min.y * texH), 0, Mathf.Max(0, texH - 1));
            int x1 = Mathf.Clamp(Mathf.CeilToInt(island.Max.x * texW), x0 + 1, texW);
            int y1 = Mathf.Clamp(Mathf.CeilToInt(island.Max.y * texH), y0 + 1, texH);
            return new RectInt(x0, y0, x1 - x0, y1 - y0);
        }
    }

    /// <summary>EN: Runs the target-quality search over every island. ZH: 对每个岛执行目标质量搜索。</summary>
    public static class QualityStage
    {
        public static void SolveAll(UsageGraph graph,
            Dictionary<TextureUsage, List<IslandPlan>> plansByTexture,
            Dictionary<UVIsland, float> worldAreaByIsland,
            ATOQualityParams quality, ATOProgress progress)
        {
            int total = plansByTexture.Values.Sum(v => v.Count);
            int done = 0;

            foreach (var kv in plansByTexture)
            {
                var usage = kv.Key;
                var pixels = TextureIntrospection.ReadStoredPixels(usage.Texture);
                if (!pixels.IsCreated) continue;

                foreach (var plan in kv.Value)
                {
                    progress.Report(done++, total, usage.Texture.name);

                    // EN: Island pixel dimensions must be recomputed per texture resolution.
                    // ZH: 岛的像素尺寸需按各贴图分辨率重新计算。
                    plan.Island.PixelWidth = Mathf.Max(1, plan.SourceRect.width);
                    plan.Island.PixelHeight = Mathf.Max(1, plan.SourceRect.height);

                    var original = IslandScaler.ExtractIsland(usage, plan.SourceRect, pixels,
                        usage.Texture.width, usage.Texture.height);
                    worldAreaByIsland.TryGetValue(plan.Island, out var worldArea);

                    IslandScaler.SolveScale(plan, quality, original, worldArea,
                        usage.Texture.width, usage.Texture.height);
                }
            }

            // EN: Bucket effect: resolve one shared footprint per island across the whole UV group.
            // ZH: 木桶效应：在整个 UV 组范围内为每个岛解析出一份共享的占位。
            IslandScaler.ResolveGroupFootprints(plansByTexture.Values.SelectMany(v => v));
        }
    }

    /// <summary>EN: Rasterises islands and packs them into atlases. ZH: 光栅化岛并装入图集。</summary>
    public static class PackStage
    {
        public static List<AtlasPlan> Pack(UsageGraph graph,
            Dictionary<TextureUsage, List<IslandPlan>> plansByTexture,
            ATOPlatformSettings options, ATOProgress progress)
        {
            // EN: Islands are shared by every texture of a UV stream, so rasterise each one exactly once
            //     and cache the mask on the island itself - the packer and the baker both reuse it.
            // ZH: 岛由一条 UV 流上的所有贴图共享，因此每个岛只光栅化一次并把掩码缓存在岛上——
            //     装箱器与烘焙器都会复用它。
            var rasterised = new HashSet<UVIsland>();
            foreach (var kv in plansByTexture)
            foreach (var plan in kv.Value)
            {
                progress.ThrowIfCancelled();
                if (!rasterised.Add(plan.Island)) continue;
                if (plan.Island.Mask.IsCreated) plan.Island.Mask.Dispose();
                plan.Island.Mask = UVIslandBuilder.Rasterise(plan.Island, plan.Set.Triangles, plan.Set.Uv,
                    kv.Key.Texture.width, kv.Key.Texture.height, AtlasPacker.CellSize);
            }
            ATOLog.Debug_($"rasterised {rasterised.Count} distinct island(s)");

            int maxSize = options.platform == ATOPlatform.PC
                ? Mathf.Min(options.maxAtlasSize, 8192)
                : Mathf.Min(options.maxAtlasSize, 4096);

            var pool = AtlasPacker.BuildCandidatePool(maxSize, options.experimentalNpot);
            var groups = AtlasPacker.BuildPackGroups(graph.UvGroups, plansByTexture);
            int counter = 0;

            var strategy = API.ATOExtensionRegistry.PackingStrategyOverride;
            if (strategy != null)
            {
                // EN: A third-party packer replaces ours wholesale.
                // ZH: 第三方装箱器整体替换我们的实现。
                try
                {
                    return strategy.Pack(groups, pool, (int)options.minPadding, ref counter);
                }
                catch (System.Exception e)
                {
                    ATOLog.Warn($"custom packing strategy threw ({e.Message}); using the built-in packer");
                    counter = 0;
                }
            }

            return AtlasPacker.PackAll(groups, pool, (int)options.minPadding, progress, ref counter);
        }
    }

    /// <summary>EN: Bakes atlases and handles textures that were not atlased. ZH: 烘焙图集并处理未进入图集的贴图。</summary>
    public static class BakeStage
    {
        public static void BakeAll(BuildContext ctx, List<AtlasPlan> plans, ATOPlatformSettings options,
            Dictionary<Texture2D, Texture2D> replacement, ATOBuildReport report, ATOProgress progress)
        {
            for (int i = 0; i < plans.Count; i++)
            {
                var atlas = plans[i];
                progress.Report(i, plans.Count, $"#{atlas.Index}");

                var baked = AtlasBaker.Bake(atlas, progress);
                baked.name = $"ATO_Atlas_{atlas.Index}";

                var representative = atlas.Sources.First();
                bool needsAlpha = atlas.Sources.Any(s => s.AlphaMode != ATOAlphaMode.Opaque && s.Content.HasAlpha);
                var cls = representative.IsNormalMap ? ATOTextureClass.NormalMap
                    : representative.Class == ATOTextureClass.Grayscale ? ATOTextureClass.Grayscale
                    : needsAlpha ? ATOTextureClass.TransparentColor : ATOTextureClass.OpaqueColor;

                var classSettings = options.ForClass(cls);
                int channelMask = atlas.Sources.Aggregate(0, (m, s) => m | s.Content.VaryingChannels);

                var format = TextureOutput.Resolve(classSettings.format, options.platform, cls,
                    needsAlpha, channelMask, baked.name);
                format = TextureOutput.DropCrunchIfNpot(format, baked.width, baked.height, baked.name);
                TextureOutput.Finalise(baked, format, classSettings.mipmapAndStreaming,
                    classSettings.compressionQuality, baked.name);

                ctx.AssetSaver.SaveAsset(baked);

                foreach (var source in atlas.Sources) replacement[source.Texture] = baked;

                long bytes = TextureOutput.EstimateBytes(baked.width, baked.height, format,
                    classSettings.mipmapAndStreaming);
                report.OptimisedBytes += bytes;
                report.AtlasLines.Add(
                    $"#{atlas.Index} {baked.width}x{baked.height} {format} util={atlas.Utilisation:P1} " +
                    $"islands={atlas.Islands.Count} sources=[{string.Join(", ", atlas.Sources.Select(s => s.Texture.name))}] " +
                    $"{bytes / 1024 / 1024.0:F2} MB");
            }
        }

        /// <summary>
        /// EN: Textures that were never placed in an atlas (whitelisted textures excepted) still get
        ///     whole-texture rescaling plus the configured import/compression settings. This is also the
        ///     path used when the user turns atlas generation off entirely.
        /// ZH: 未被放入图集的贴图（白名单贴图除外）仍会接受整图缩放以及配置的导入/压缩设置。
        ///     用户完全关闭图集生成时也走这条路径。
        /// </summary>
        public static void OptimiseUnatlased(BuildContext ctx, UsageGraph graph,
            ATOPlatformSettings options, ATOQualityParams quality,
            Dictionary<Texture2D, Texture2D> replacement, ATOBuildReport report, ATOProgress progress)
        {
            var pending = graph.Textures.Values
                .Where(u => !u.Excluded && !replacement.ContainsKey(u.Texture))
                .ToList();

            for (int i = 0; i < pending.Count; i++)
            {
                var usage = pending[i];
                progress.Report(i, pending.Count, usage.Texture.name);

                var src = TextureIntrospection.ReadStoredPixels(usage.Texture);
                if (!src.IsCreated) continue;

                int w = usage.Texture.width, h = usage.Texture.height;
                var full = new RectInt(0, 0, w, h);
                var original = IslandScaler.ExtractIsland(usage, full, src, w, h);

                int targetW = w, targetH = h;
                if (!quality.lossless)
                {
                    var probe = new IslandPlan
                    {
                        Texture = usage,
                        Island = new UVIsland
                        {
                            Min = new float2(0f, 0f),
                            Max = new float2(1f, 1f),
                            PixelWidth = w,
                            PixelHeight = h,
                            UvArea = 1f,
                        },
                        SourceRect = full,
                    };
                    IslandScaler.SolveScale(probe, quality, original, 0f, w, h);
                    targetW = Mathf.Max(1, probe.DesiredWidth);
                    targetH = Mathf.Max(1, probe.DesiredHeight);
                }

                var cls = usage.Class;
                var classSettings = options.ForClass(cls);
                bool needsAlpha = usage.AlphaMode != ATOAlphaMode.Opaque && usage.Content.HasAlpha;

                var format = TextureOutput.Resolve(classSettings.format, options.platform, cls,
                    needsAlpha, usage.Content.VaryingChannels, usage.Texture.name);
                format = TextureOutput.DropCrunchIfNpot(format, targetW, targetH, usage.Texture.name);

                Texture2D output;
                if (targetW == w && targetH == h && quality.lossless)
                {
                    // EN: Near-lossless: copy the original bytes verbatim, no resampling at all.
                    // ZH: 近无损：原样拷贝原始字节，完全不重采样。
                    output = new Texture2D(w, h, TextureFormat.RGBA32, classSettings.mipmapAndStreaming,
                        !usage.SRGB) { name = usage.Texture.name + "_ATO" };
                    output.SetPixelData(src, 0);
                    output.Apply(classSettings.mipmapAndStreaming, false);
                }
                else
                {
                    var scaled = original.Downsample(targetW, targetH);
                    output = LinearToTexture(scaled, usage, classSettings.mipmapAndStreaming);
                    output.name = usage.Texture.name + "_ATO";
                }

                output.wrapMode = usage.Texture.wrapMode;
                output.filterMode = usage.Texture.filterMode;
                output.anisoLevel = usage.Texture.anisoLevel;

                TextureOutput.Finalise(output, format, classSettings.mipmapAndStreaming,
                    classSettings.compressionQuality, output.name);
                ctx.AssetSaver.SaveAsset(output);

                replacement[usage.Texture] = output;
                long bytes = TextureOutput.EstimateBytes(targetW, targetH, format,
                    classSettings.mipmapAndStreaming);
                report.OptimisedBytes += bytes;

                if (targetW != w || targetH != h)
                {
                    report.Notes.Add($"{usage.Texture.name}: {w}x{h} -> {targetW}x{targetH} {format}");
                }
            }
        }

        private static Texture2D LinearToTexture(LinearImage img, TextureUsage usage, bool mipmaps)
        {
            bool normal = usage.IsNormalMap;
            bool dxt5nm = normal && NormalCodec.IsDxt5nm(usage.Content);
            bool srgb = usage.SRGB && !normal;
            bool premultiplied = img.Premultiplied;

            var pixels = new Color32[img.Width * img.Height];
            for (int i = 0; i < pixels.Length; i++)
            {
                var v = img.Pixels[i];
                if (normal)
                {
                    pixels[i] = NormalCodec.Encode(v.xyz, dxt5nm);
                    continue;
                }
                if (premultiplied)
                {
                    float a = Mathf.Max(v.w, 1e-4f);
                    v = new float4(v.x / a, v.y / a, v.z / a, v.w);
                }
                if (srgb)
                {
                    pixels[i] = new Color32(
                        Byte(TextureIntrospection.LinearToSrgb(v.x)),
                        Byte(TextureIntrospection.LinearToSrgb(v.y)),
                        Byte(TextureIntrospection.LinearToSrgb(v.z)),
                        Byte(v.w));
                }
                else
                {
                    pixels[i] = new Color32(Byte(v.x), Byte(v.y), Byte(v.z), Byte(v.w));
                }
            }

            var tex = new Texture2D(img.Width, img.Height, TextureFormat.RGBA32, mipmaps, !srgb);
            tex.SetPixels32(pixels);
            tex.Apply(mipmaps, false);
            return tex;
        }

        private static byte Byte(float v) => (byte)Mathf.Clamp(Mathf.RoundToInt(v * 255f), 0, 255);
    }
}
