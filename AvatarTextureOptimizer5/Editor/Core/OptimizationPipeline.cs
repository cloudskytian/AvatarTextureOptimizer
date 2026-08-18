// Copyright (c) fosa. Licensed under the MIT License.
// Orchestrates every optimization stage. All state lives here so the NDMF pass stays thin and
// so cancellation can unwind cleanly from a single place.
// 编排所有优化阶段。全部状态集中于此，使 NDMF pass 保持轻薄，
// 并使取消操作能够从单一位置干净地回退。

using System;
using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// Aggregate outcome of a pipeline run, used for reporting.
    /// 管线运行的汇总结果，用于生成报告。
    /// </summary>
    public sealed class OptimizationResult
    {
        /// <summary>Atlases produced. / 生成的图集。</summary>
        public readonly List<AtlasResult> Atlases = new List<AtlasResult>();

        /// <summary>Generated textures needing asset persistence. / 需要持久化为资产的生成贴图。</summary>
        public readonly List<Texture2D> GeneratedTextures = new List<Texture2D>();

        /// <summary>Generated meshes needing asset persistence. / 需要持久化为资产的生成网格。</summary>
        public readonly List<Mesh> GeneratedMeshes = new List<Mesh>();

        /// <summary>Generated materials needing asset persistence. / 需要持久化为资产的生成材质。</summary>
        public readonly List<Material> GeneratedMaterials = new List<Material>();

        /// <summary>Source bytes before optimization. / 优化前的源字节数。</summary>
        public long OriginalBytes;

        /// <summary>Resulting bytes after optimization. / 优化后的字节数。</summary>
        public long OptimizedBytes;

        /// <summary>Number of textures actually optimized. / 实际被优化的贴图数量。</summary>
        public int OptimizedTextureCount;

        /// <summary>True when the user cancelled. / 用户取消时为 true。</summary>
        public bool Cancelled;

        /// <summary>
        /// Maps merged-away materials onto their surviving representative, so animation
        /// references can be repointed after deduplication.
        /// 将被合并掉的材质映射到存活的代表，
        /// 以便在去重之后重定向动画引用。
        /// </summary>
        public Dictionary<Material, Material> MaterialDeduplication;

        /// <summary>Bytes saved, never negative in reporting. / 节省的字节数，报告中不为负。</summary>
        public long SavedBytes => Math.Max(0, OriginalBytes - OptimizedBytes);
    }

    /// <summary>
    /// Runs the full optimization for one avatar.
    /// 为单个 Avatar 执行完整优化流程。
    /// </summary>
    public sealed class OptimizationPipeline : IDisposable
    {
        private readonly ATOLogger _log;
        private readonly TextureCache _cache;
        private readonly ShaderAnalyzer _shaderAnalyzer;
        private readonly Func<string, float, bool> _progress;

        private bool _cancelled;

        /// <summary>Creates a pipeline. / 创建管线。</summary>
        /// <param name="log">Logger. / 日志器。</param>
        /// <param name="progress">
        /// Progress callback returning false to cancel. / 进度回调，返回 false 表示取消。
        /// </param>
        public OptimizationPipeline(ATOLogger log, Func<string, float, bool> progress = null)
        {
            _log = log;
            _progress = progress;
            _cache = new TextureCache(log);
            _shaderAnalyzer = new ShaderAnalyzer(log);
        }

        private bool Cancelled => _cancelled;

        private bool Report(string stageKey, float fraction)
        {
            if (_cancelled) return false;
            if (_progress == null) return true;

            if (!_progress(stageKey, fraction))
            {
                _cancelled = true;
                _log?.Warning("Optimization cancelled by user");
                return false;
            }

            return true;
        }

        /// <summary>
        /// Executes the pipeline against an avatar root.
        /// 针对 Avatar 根节点执行管线。
        /// </summary>
        public OptimizationResult Run(GameObject avatarRoot, PlatformSettings settings)
        {
            var result = new OptimizationResult();
            if (avatarRoot == null || settings == null) return result;

            var quality = settings.ResolveQuality();

            // ---- Stage 1: whitelist + collection ----
            // ---- 阶段 1：白名单 + 收集 ----
            if (!Report("ato.progress.collecting", 0.05f)) return Cancel(result);

            var whitelistResolver = new WhitelistResolver(_log);
            var whitelisted = whitelistResolver.Resolve(CollectWhitelistEntries(avatarRoot));

            var animationAnalyzer = new AnimationAnalyzer(_log);
            var clips = CollectClips(avatarRoot);
            var animation = animationAnalyzer.Analyze(clips);

            var collector = new TextureCollector(_log, _shaderAnalyzer, _cache);
            var collection = collector.Collect(
                avatarRoot, whitelisted, CollectWhitelistEntries(avatarRoot), animation);

            if (collection.Groups.Count == 0)
            {
                _log?.Info("No optimizable textures found");
                return result;
            }

            // ---- Stage 2: mesh analysis, island extraction ----
            // ---- 阶段 2：网格分析与岛提取 ----
            if (!Report("ato.progress.analyzing", 0.15f)) return Cancel(result);

            var uvAnimatedPaths = animationAnalyzer.FindUVAnimatedPaths(clips);
            BuildIslands(avatarRoot, collection, animation, uvAnimatedPaths);

            // ---- Stage 3: quality-driven island sizing ----
            // ---- 阶段 3：质量驱动的岛尺寸决策 ----
            if (!Report("ato.progress.quality", 0.30f)) return Cancel(result);

            ResolveIslandSizes(collection, quality, settings);
            if (Cancelled) return Cancel(result);

            // ---- Stage 4: packing ----
            // ---- 阶段 4：装箱 ----
            if (!Report("ato.progress.packing", 0.60f)) return Cancel(result);

            if (settings.generateAtlas)
            {
                PackAll(collection, settings, result);
            }

            if (Cancelled) return Cancel(result);

            // ---- Stage 5: composition ----
            // ---- 阶段 5：合成 ----
            if (!Report("ato.progress.compositing", 0.80f)) return Cancel(result);

            var textureMap = CompositeAtlases(collection, result, settings);
            if (Cancelled) return Cancel(result);

            // ---- Stage 6: apply to the avatar ----
            // ---- 阶段 6：应用到 Avatar ----
            if (!Report("ato.progress.finalizing", 0.95f)) return Cancel(result);

            ApplyToAvatar(avatarRoot, collection, result, textureMap, settings);

            AccumulateStatistics(collection, result);

            _log?.Info(
                $"Pipeline finished: {result.OptimizedTextureCount} textures, " +
                $"{result.Atlases.Count} atlases");

            return result;
        }

        private OptimizationResult Cancel(OptimizationResult result)
        {
            result.Cancelled = true;
            return result;
        }

        /// <summary>
        /// Extracts UV islands for every group, applying normalization and overlap merging.
        /// 为每个组提取 UV 岛，并应用归一化与重叠合并。
        /// </summary>
        private void BuildIslands(
            GameObject avatarRoot,
            CollectionResult collection,
            AnimationFindings animation,
            HashSet<string> uvAnimatedPaths)
        {
            foreach (var group in collection.Groups)
            {
                if (group.Whitelisted) continue;
                if (Cancelled) return;
                if (group.Streams.Count == 0) continue;

                var stream = group.Streams[0];
                var renderer = stream.Renderer;
                if (renderer == null)
                {
                    group.Whitelisted = true;
                    group.SkipReason = "renderer was destroyed";
                    continue;
                }

                var path = GetRelativePath(avatarRoot.transform, renderer.transform);
                if (uvAnimatedPaths.Contains(path))
                {
                    group.Whitelisted = true;
                    group.SkipReason = "UV properties are animated";
                    _log?.Warning($"{renderer.name}: excluded, UV properties are animated");
                    continue;
                }

                var mesh = GetMesh(renderer);
                if (mesh == null || !mesh.isReadable)
                {
                    group.Whitelisted = true;
                    group.SkipReason = mesh == null ? "no mesh" : "mesh is not readable";
                    continue;
                }

                var uvs = new List<Vector2>();
                mesh.GetUVs(stream.Channel, uvs);
                if (uvs.Count == 0)
                {
                    group.Whitelisted = true;
                    group.SkipReason = $"mesh has no UV{stream.Channel}";
                    continue;
                }

                var triangles = mesh.triangles;
                var uvArray = uvs.ToArray();

                var islands = UVIslandExtractor.Extract(triangles, uvArray, _log);
                islands = UVIslandExtractor.MergeOverlapping(islands, _log);

                // Normalise out-of-range islands; anything crossing a tile seam is unsafe.
                // 归一化越界的岛；任何跨越平铺接缝的内容都不安全。
                var unsafeSeam = false;
                foreach (var island in islands)
                {
                    var state = UVIslandExtractor.TryNormalize(island, out var offset);
                    if (state == UVNormalizationResult.CrossesSeam)
                    {
                        unsafeSeam = true;
                        break;
                    }

                    island.NormalizationOffset = offset;
                }

                if (unsafeSeam)
                {
                    group.Whitelisted = true;
                    group.SkipReason = "UV islands cross a tile boundary";
                    _log?.Warning(
                        $"{renderer.name}: excluded, UV islands cross a tile boundary");
                    continue;
                }

                var scale = ResolveMaxScale(renderer, animation, path);
                MeshAreaAnalyzer.ComputeIslandAreas(mesh, triangles, uvArray, islands, scale);

                group.Islands.Clear();
                group.Islands.AddRange(islands);

                // Cache geometry so the packing stage can rasterize true island shapes.
                // 缓存几何数据，使装箱阶段能够光栅化岛的真实形状。
                group.SourceTriangles = triangles;
                group.SourceUVs = uvArray;
            }
        }

        /// <summary>
        /// Runs the quality search per island and stores the chosen packed size.
        /// 对每个岛执行质量搜索并存储选定的打包尺寸。
        /// </summary>
        private void ResolveIslandSizes(
            CollectionResult collection, QualityParameters quality, PlatformSettings settings)
        {
            foreach (var group in collection.Groups)
            {
                if (group.Whitelisted || group.Islands.Count == 0) continue;
                if (Cancelled) return;

                foreach (var island in group.Islands)
                {
                    if (Cancelled) return;

                    // Every texture in the group shares one layout, so each texture proposes a
                    // size and the group takes the largest: the most demanding texture wins.
                    // 组内所有贴图共享同一布局，因此每张贴图各自提出尺寸并取最大值：
                    // 要求最高的贴图胜出。
                    var best = Vector2Int.one;

                    foreach (var texInfo in group.Textures)
                    {
                        if (texInfo.Whitelisted) continue;

                        var decoded = _cache.Get(texInfo.Texture);
                        if (decoded == null) continue;

                        var rect = ComputeSourceRect(island, decoded.Width, decoded.Height);
                        if (rect.width <= 0 || rect.height <= 0) continue;

                        // Recorded for reporting only; the compositor recomputes it per texture
                        // because group members can differ in resolution.
                        // 仅用于报告；合成器会按每张贴图重新计算，
                        // 因为组内成员的分辨率可能不同。
                        island.SourceRect = rect;

                        var cropped = Resampler.Crop(decoded, rect);

                        var ctx = new EvaluationContext
                        {
                            Parameters = quality,
                            Category = texInfo.Category,
                            AlphaMode = texInfo.StrictestAlphaMode,
                            UsedChannels = texInfo.UsedChannels,
                        };
                        ctx.Cutoffs.AddRange(texInfo.Cutoffs);
                        if (ctx.Cutoffs.Count == 0) ctx.Cutoffs.Add(0.5f);

                        var maxSize = new Vector2Int(rect.width, rect.height);
                        var minSize = Vector2Int.one;

                        var chosen = QualityEvaluator.FindOptimalSize(
                            cropped, ctx, minSize, maxSize, () => Cancelled);

                        best = new Vector2Int(
                            Mathf.Max(best.x, chosen.x), Mathf.Max(best.y, chosen.y));
                    }

                    island.PackedSize = new Vector2Int(
                        Mathf.Max(1, best.x), Mathf.Max(1, best.y));
                }
            }
        }

        /// <summary>
        /// Converts an island's UV bounds into a pixel rect in the source texture.
        /// 将岛的 UV 包围盒转换为源贴图中的像素矩形。
        /// </summary>
        public static RectInt ComputeSourceRect(UVIsland island, int width, int height)
        {
            var b = island.UVBounds;

            var x0 = Mathf.Clamp(Mathf.FloorToInt(b.xMin * width), 0, Mathf.Max(0, width - 1));
            var y0 = Mathf.Clamp(Mathf.FloorToInt(b.yMin * height), 0, Mathf.Max(0, height - 1));
            var x1 = Mathf.Clamp(Mathf.CeilToInt(b.xMax * width), x0 + 1, width);
            var y1 = Mathf.Clamp(Mathf.CeilToInt(b.yMax * height), y0 + 1, height);

            return new RectInt(x0, y0, x1 - x0, y1 - y0);
        }

        /// <summary>
        /// Rasterizes islands and packs each type-signature queue into atlases.
        /// 光栅化岛并将每个类型签名队列装入图集。
        /// </summary>
        private void PackAll(
            CollectionResult collection, PlatformSettings settings, OptimizationResult result)
        {
            var packer = new AtlasPacker(_log);
            var queues = new Dictionary<string, List<UVGroup>>(StringComparer.Ordinal);

            foreach (var group in collection.Groups)
            {
                if (group.Whitelisted || group.Islands.Count == 0) continue;

                if (!queues.TryGetValue(group.TypeSignature, out var list))
                {
                    list = new List<UVGroup>();
                    queues[group.TypeSignature] = list;
                }

                list.Add(group);
            }

            var pool = packer.BuildCandidatePool(settings.maxAtlasSize, settings.allowNpot);

            foreach (var kv in queues)
            {
                if (Cancelled) return;

                // Padding scales with atlas size so large atlases keep their islands apart at
                // every mip level, clamped to the user's configured minimum.
                // padding 随图集尺寸缩放，使大图集在所有 mip 层级都能隔开各岛，
                // 并以用户配置的最小值作为下限。
                var padding = Mathf.Max(
                    (int)settings.minPadding,
                    Mathf.CeilToInt(settings.maxAtlasSize / 128f));

                foreach (var group in kv.Value)
                {
                    // Without the source geometry the rasterizer cannot know the island's true
                    // outline, so such a group is skipped rather than packed as a bounding box,
                    // which would waste atlas area and break the shape-aware guarantee.
                    // 缺少源几何数据时光栅化器无法得知岛的真实轮廓，
                    // 因此跳过该组，而不是按包围盒装箱——那会浪费图集面积并破坏形状感知保证。
                    if (group.SourceTriangles == null || group.SourceUVs == null)
                    {
                        group.Whitelisted = true;
                        group.SkipReason = "source geometry unavailable for rasterization";
                        _log?.Warning(
                            $"UV group {group.Id}: skipped, source geometry unavailable");
                        continue;
                    }

                    foreach (var island in group.Islands)
                    {
                        if (Cancelled) return;
                        IslandRasterizer.Rasterize(
                            island, group.SourceTriangles, group.SourceUVs, padding);
                    }
                }

                var packable = new List<UVGroup>();
                foreach (var group in kv.Value)
                {
                    if (!group.Whitelisted && group.Islands.Count > 0) packable.Add(group);
                }

                if (packable.Count == 0) continue;

                var atlases = packer.PackQueue(packable, pool, padding, () => Cancelled);
                foreach (var atlas in atlases)
                {
                    atlas.TypeSignature = kv.Key;
                    atlas.Padding = padding;
                    result.Atlases.Add(atlas);
                }
            }
        }

        private void AccumulateStatistics(
            CollectionResult collection, OptimizationResult result)
        {
            foreach (var kv in collection.Textures)
            {
                var info = kv.Value;
                if (info.Whitelisted) continue;

                result.OriginalBytes += EstimateBytes(info.Width, info.Height);
                result.OptimizedTextureCount++;
            }

            foreach (var atlas in result.Atlases)
            {
                result.OptimizedBytes += EstimateBytes(atlas.Width, atlas.Height);
            }
        }

        /// <summary>
        /// Estimates VRAM for a texture including its mip chain (4/3 of the base level).
        /// 估算含 mip 链的贴图显存占用（基础层的 4/3）。
        /// </summary>
        public static long EstimateBytes(int width, int height)
        {
            return (long)(width * (long)height * 4 * 4.0 / 3.0);
        }

        private static Vector3 ResolveMaxScale(
            Renderer renderer, AnimationFindings animation, string path)
        {
            var scale = renderer.transform.lossyScale;

            if (animation != null &&
                animation.MaxAnimatedScale.TryGetValue(path, out var animated))
            {
                // Animated scale can grow the object, which raises the texel density it needs.
                // 动画缩放可能放大物体，从而提高其所需的像素密度。
                scale = new Vector3(
                    Mathf.Max(Mathf.Abs(scale.x), Mathf.Abs(animated.x)),
                    Mathf.Max(Mathf.Abs(scale.y), Mathf.Abs(animated.y)),
                    Mathf.Max(Mathf.Abs(scale.z), Mathf.Abs(animated.z)));
            }

            return scale;
        }

        private static Mesh GetMesh(Renderer renderer)
        {
            if (renderer is SkinnedMeshRenderer smr) return smr.sharedMesh;
            if (renderer.TryGetComponent<MeshFilter>(out var mf)) return mf.sharedMesh;
            return null;
        }

        private static string GetRelativePath(Transform root, Transform target)
        {
            if (target == root) return string.Empty;

            var parts = new List<string>();
            var current = target;

            while (current != null && current != root)
            {
                parts.Add(current.name);
                current = current.parent;
            }

            parts.Reverse();
            return string.Join("/", parts);
        }

        private static List<Object> CollectWhitelistEntries(GameObject avatarRoot)
        {
            var entries = new List<Object>();
            var component = avatarRoot.GetComponentInChildren<AvatarTextureOptimizer>(true);
            if (component != null && component.Settings?.whitelist != null)
            {
                entries.AddRange(component.Settings.whitelist);
            }

            return entries;
        }

        private static List<AnimationClip> CollectClips(GameObject avatarRoot)
        {
            var clips = new List<AnimationClip>();
            var seen = new HashSet<AnimationClip>();

            foreach (var animator in avatarRoot.GetComponentsInChildren<Animator>(true))
            {
                var controller = animator.runtimeAnimatorController;
                if (controller == null) continue;

                foreach (var clip in controller.animationClips)
                {
                    if (clip != null && seen.Add(clip)) clips.Add(clip);
                }
            }

            return clips;
        }

        /// <summary>
        /// Renders every atlas and returns the source-texture to atlas-texture mapping.
        /// 渲染所有图集，并返回源贴图到图集贴图的映射。
        /// </summary>
        private Dictionary<Texture2D, Texture2D> CompositeAtlases(
            CollectionResult collection, OptimizationResult result, PlatformSettings settings)
        {
            var map = new Dictionary<Texture2D, Texture2D>();

            using (var compositor = new AtlasCompositor(_log, _cache))
            {
                foreach (var atlas in result.Atlases)
                {
                    if (Cancelled) return map;

                    // Every distinct texture in the atlas's groups needs its own atlas image,
                    // all sharing one layout so a single UV rewrite serves all of them.
                    // 图集内各组的每张不同贴图都需要生成各自的图集图像，
                    // 它们共享同一布局，因此一次 UV 重写即可服务全部贴图。
                    var textures = new Dictionary<Texture2D, List<UVIsland>>();

                    foreach (var group in atlas.Groups)
                    {
                        foreach (var texInfo in group.Textures)
                        {
                            if (texInfo.Whitelisted || texInfo.Texture == null) continue;

                            if (!textures.TryGetValue(texInfo.Texture, out var islands))
                            {
                                islands = new List<UVIsland>();
                                textures[texInfo.Texture] = islands;
                            }

                            islands.AddRange(group.Islands);
                        }
                    }

                    foreach (var kv in textures)
                    {
                        if (Cancelled) return map;

                        var info = collection.Textures.TryGetValue(kv.Key, out var ti) ? ti : null;
                        var category = info?.Category ?? TextureCategory.OpaqueColor;
                        var isSRGB = info?.IsSRGB ?? true;

                        var composited = compositor.Composite(
                            kv.Key,
                            kv.Value,
                            atlas.Width,
                            atlas.Height,
                            isSRGB,
                            category == TextureCategory.NormalMap,
                            atlas.Padding);

                        if (composited == null) continue;

                        composited.name =
                            $"{TextureOutput.NamePrefix}{kv.Key.name}_{atlas.Index}";

                        var categorySettings = settings.GetCategory(category);
                        TextureOutput.Finalise(
                            composited,
                            categorySettings,
                            category,
                            DetectPlatform(),
                            info?.HasAlphaContent ?? false,
                            _log);

                        map[kv.Key] = composited;
                        result.GeneratedTextures.Add(composited);
                    }
                }
            }

            _log?.Info($"Composited {map.Count} atlas textures");
            return map;
        }

        /// <summary>
        /// Rewrites meshes to the atlas layout and repoints materials at the new textures.
        /// 将网格重写到图集布局，并把材质指向新贴图。
        /// </summary>
        private void ApplyToAvatar(
            GameObject avatarRoot,
            CollectionResult collection,
            OptimizationResult result,
            Dictionary<Texture2D, Texture2D> textureMap,
            PlatformSettings settings)
        {
            if (textureMap.Count == 0)
            {
                _log?.Info("No atlas textures were produced; avatar left unchanged");
                return;
            }

            var rewriter = new MeshUVRewriter(_log);
            var remapper = new MaterialRemapper(_log);
            var aao = new AAOCompat(_log);

            // Rewrite each renderer's mesh once per UV channel that was repacked.
            // 对每个渲染器，按被重排的 UV 通道各重写一次网格。
            foreach (var group in collection.Groups)
            {
                if (Cancelled) return;
                if (group.Whitelisted || group.Islands.Count == 0) continue;
                if (group.Streams.Count == 0) continue;

                var atlas = FindAtlasFor(result, group);
                if (atlas == null) continue;

                foreach (var stream in group.Streams)
                {
                    var renderer = stream.Renderer;
                    if (renderer == null) continue;

                    var mesh = GetMesh(renderer);
                    if (mesh == null) continue;

                    // Tell Avatar Optimizer where the original UVs went before we overwrite
                    // them, otherwise its mesh removal would read repacked coordinates.
                    // 在覆盖原始 UV 之前告知 Avatar Optimizer 其去向，
                    // 否则其网格移除会读取到重排后的坐标。
                    if (aao.IsAvailable && renderer is SkinnedMeshRenderer smr &&
                        aao.IsTexCoordUsed(smr, stream.Channel))
                    {
                        var free = AAOCompat.FindFreeUVChannel(mesh, stream.Channel);
                        if (free < 0 || !aao.RegisterEvacuation(smr, stream.Channel, free))
                        {
                            _log?.Warning(
                                $"{renderer.name}: cannot evacuate UV{stream.Channel} for " +
                                "Avatar Optimizer; skipping this renderer");
                            continue;
                        }

                        var original = new List<Vector2>();
                        mesh.GetUVs(stream.Channel, original);
                        mesh.SetUVs(free, original);
                    }

                    var newMesh = rewriter.Rewrite(
                        mesh, stream.Channel, group.Islands, atlas.Width, atlas.Height);

                    if (newMesh == null) continue;

                    AssignMesh(renderer, newMesh);
                    result.GeneratedMeshes.Add(newMesh);
                }
            }

            // Repoint materials at the atlas textures.
            // 将材质指向图集贴图。
            foreach (var renderer in collection.Renderers)
            {
                if (Cancelled) return;
                if (renderer == null) continue;

                var materials = renderer.sharedMaterials;
                var changed = false;

                for (var i = 0; i < materials.Length; i++)
                {
                    var remapped = remapper.Remap(materials[i], textureMap);
                    if (remapped != materials[i])
                    {
                        materials[i] = remapped;
                        changed = true;
                    }
                }

                if (changed) renderer.sharedMaterials = materials;
            }

            // Merge materials that ended up identical. Safe because the key covers the shader,
            // every property value and every texture reference, so merged materials render the
            // same; this only removes redundant draw-call state.
            // 合并最终完全相同的材质。之所以安全，是因为键覆盖了着色器、
            // 所有属性值与所有贴图引用，合并后的材质渲染结果一致；
            // 这只会消除冗余的 draw call 状态。
            if (settings.deduplicateMaterials)
            {
                DeduplicateMaterials(collection, result, remapper);
            }

            foreach (var mat in remapper.CreatedMaterials)
            {
                if (mat != null && !result.GeneratedMaterials.Contains(mat))
                {
                    result.GeneratedMaterials.Add(mat);
                }
            }

            _log?.Info(
                $"Applied {result.GeneratedMeshes.Count} meshes and " +
                $"{result.GeneratedMaterials.Count} materials");
        }

        /// <summary>
        /// Collapses duplicate materials across all renderers and records the mapping so
        /// animation references can be repointed by the caller.
        /// 在所有渲染器间合并重复材质，并记录映射，
        /// 以便调用方据此重定向动画引用。
        /// </summary>
        private void DeduplicateMaterials(
            CollectionResult collection, OptimizationResult result, MaterialRemapper remapper)
        {
            var all = new List<Material>();
            var seen = new HashSet<Material>();

            foreach (var renderer in collection.Renderers)
            {
                if (renderer == null) continue;
                foreach (var m in renderer.sharedMaterials)
                {
                    if (m != null && seen.Add(m)) all.Add(m);
                }
            }

            if (all.Count == 0) return;

            var mapping = remapper.BuildDeduplication(all);
            var merged = 0;

            foreach (var renderer in collection.Renderers)
            {
                if (renderer == null) continue;

                var materials = renderer.sharedMaterials;
                var changed = false;

                for (var i = 0; i < materials.Length; i++)
                {
                    var m = materials[i];
                    if (m == null) continue;
                    if (!mapping.TryGetValue(m, out var rep) || rep == m) continue;

                    materials[i] = rep;
                    changed = true;
                    merged++;
                }

                if (changed) renderer.sharedMaterials = materials;
            }

            // Record the surviving materials only, so merged-away duplicates are not persisted
            // as assets and left dangling in the build output.
            // 只记录存活的材质，使被合并掉的重复项不会被持久化为资产、
            // 从而在构建产物中留下悬挂引用。
            result.MaterialDeduplication = mapping;

            if (merged > 0) _log?.Info($"Material dedup: {merged} slots merged");
        }

        private static AtlasResult FindAtlasFor(OptimizationResult result, UVGroup group)
        {
            foreach (var atlas in result.Atlases)
            {
                if (atlas.Groups.Contains(group)) return atlas;
            }

            return null;
        }

        private static void AssignMesh(Renderer renderer, Mesh mesh)
        {
            if (renderer is SkinnedMeshRenderer smr)
            {
                smr.sharedMesh = mesh;
            }
            else if (renderer.TryGetComponent<MeshFilter>(out var mf))
            {
                mf.sharedMesh = mesh;
            }
        }

        private static ATOPlatform DetectPlatform()
        {
            switch (UnityEditor.EditorUserBuildSettings.activeBuildTarget)
            {
                case UnityEditor.BuildTarget.Android: return ATOPlatform.Android;
                case UnityEditor.BuildTarget.iOS: return ATOPlatform.iOS;
                default: return ATOPlatform.PC;
            }
        }

        /// <inheritdoc />
        public void Dispose()
        {
            _cache?.Dispose();
        }
    }
}
