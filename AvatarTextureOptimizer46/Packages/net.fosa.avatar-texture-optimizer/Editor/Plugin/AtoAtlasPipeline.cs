// SPDX-License-Identifier: MIT
// EN: Stage 3 - islands, quality solving, type grouping, packing and atlas generation.
// ZH: 阶段 3 —— 岛提取、质量求解、类型分组、装箱与图集生成。
//
// EN: Two grouping levels exist and they are NOT the same thing:
//       * UvGroup    - textures that must share one island layout (same UV slots). All islands of a
//                      UvGroup are placed into one and the same atlas, because a material slot can only
//                      point at a single texture.
//       * TypeGroup  - UvGroups whose "kind signature" matches (which of colour/alpha/normal/mask exist,
//                      plus colour space and filter mode). Only these may share an atlas, otherwise an
//                      atlas of normal maps would be mostly empty when only one member has a normal map.
// ZH: 存在两级分组，二者并不相同：
//       * UvGroup   —— 必须共享同一套岛布局（相同 UV 槽）的贴图。一个 UvGroup 的所有岛都会被放进
//                      同一张图集，因为一个材质槽只能指向一张贴图。
//       * TypeGroup —— “类型签名”一致的多个 UvGroup（存在哪些 颜色/带alpha/法线/蒙版，
//                      以及色彩空间与过滤模式）。只有它们才可以共享图集，
//                      否则当只有一个成员拥有法线贴图时，法线图集会大部分为空。

using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf;
using Net.Fosa.AvatarTextureOptimizer.Editor.Atlas;
using Net.Fosa.AvatarTextureOptimizer.Editor.Core;
using Net.Fosa.AvatarTextureOptimizer.Editor.Meshes;
using Net.Fosa.AvatarTextureOptimizer.Editor.Model;
using Net.Fosa.AvatarTextureOptimizer.Editor.Packing;
using Net.Fosa.AvatarTextureOptimizer.Editor.Quality;
using Net.Fosa.AvatarTextureOptimizer.Editor.Textures;
using UnityEngine;

namespace Net.Fosa.AvatarTextureOptimizer.Editor.Plugin
{
    /// <summary>
    /// EN: Per UV slot information needed to rewrite mesh UVs after packing.
    /// ZH: 装箱后重写网格 UV 所需的逐 UV 槽信息。
    /// </summary>
    public sealed class UvRewritePlan
    {
        /// <summary>EN: The slot being rewritten. ZH: 被重写的槽。</summary>
        public UvSlot Slot;
        /// <summary>EN: The group the slot belongs to. ZH: 该槽所属的组。</summary>
        public UvGroup Group;
        /// <summary>EN: Triangles with their island assignment. ZH: 三角形及其所属岛。</summary>
        public readonly List<SourceTriangle> Triangles = new List<SourceTriangle>();
        /// <summary>EN: Integer tile shift applied to bring UVs into [0,1]. ZH: 为把 UV 归入 [0,1] 而应用的整数块偏移。</summary>
        public Vector2Int Shift;
    }

    /// <summary>
    /// EN: A set of UV groups that share atlases.
    /// ZH: 共享图集的一组 UV 组。
    /// </summary>
    public sealed class TypeGroup
    {
        /// <summary>EN: The signature all members share. ZH: 所有成员共有的签名。</summary>
        public string Signature;
        /// <summary>EN: Member UV groups. ZH: 成员 UV 组。</summary>
        public readonly List<UvGroup> Groups = new List<UvGroup>();
        /// <summary>EN: Texture kinds present in every member, in a stable order. ZH: 每个成员都具备的贴图分类，顺序稳定。</summary>
        public readonly List<string> Layers = new List<string>();
    }

    /// <summary>
    /// EN: Output of the whole atlas stage.
    /// ZH: 整个图集阶段的输出。
    /// </summary>
    public sealed class AtlasStageResult
    {
        /// <summary>EN: Mapping from an original texture entry to its generated atlas. ZH: 原始贴图条目到其生成图集的映射。</summary>
        public readonly Dictionary<TextureEntry, Texture2D> ReplacementTexture = new Dictionary<TextureEntry, Texture2D>();
        /// <summary>EN: Rewrite plans keyed by UV slot. ZH: 以 UV 槽为键的重写计划。</summary>
        public readonly Dictionary<UvSlot, UvRewritePlan> Plans = new Dictionary<UvSlot, UvRewritePlan>();
        /// <summary>EN: Final atlas size that each UV group's islands are expressed against. ZH: 每个 UV 组的岛所对应的最终图集尺寸。</summary>
        public readonly Dictionary<UvGroup, Vector2Int> AtlasSizeOf = new Dictionary<UvGroup, Vector2Int>();
        /// <summary>EN: Per atlas bookkeeping for the report. ZH: 供报告使用的逐图集记录。</summary>
        public readonly List<AtlasResult> Atlases = new List<AtlasResult>();
    }

    /// <summary>
    /// EN: Executes islands, quality and packing for every group of the avatar.
    /// ZH: 为 Avatar 的每一个组执行岛提取、质量求解与装箱。
    /// </summary>
    public sealed class AtoAtlasPipeline
    {
        private const string Stage = "Atlas";

        private readonly BuildContext _ctx;
        private readonly AtoProfile _profile;
        private readonly AtoPlatform _platform;
        private readonly Func<string, Vector3> _maxAnimatedScale;
        private readonly LinearSourceCache _cache;
        private int _atlasCounter;

        /// <summary>EN: Creates the stage. ZH: 创建该阶段。</summary>
        public AtoAtlasPipeline(BuildContext ctx, AtoProfile profile, AtoPlatform platform, Func<string, Vector3> maxAnimatedScale)
        {
            _ctx = ctx;
            _profile = profile;
            _platform = platform;
            _maxAnimatedScale = maxAnimatedScale ?? (_ => Vector3.one);
            _cache = new LinearSourceCache();
        }

        /// <summary>
        /// EN: Runs the whole stage.
        /// ZH: 执行整个阶段。
        /// </summary>
        public AtlasStageResult Run(AtoCollection collection, AtoProgress progress)
        {
            var result = new AtlasStageResult();

            // --- geometry and quality, per UV group -------------------------------------------------
            // --- 逐 UV 组的几何与质量 ---------------------------------------------------------------
            //
            // EN: Sources are decoded through a budgeted cache and are only pinned while a group is being
            //     solved or composed. Peak GPU memory is therefore bounded by the largest single group,
            //     not by the whole avatar.
            // ZH: 源贴图通过有预算的缓存解码，且只在某个组正在求解或合成时才被固定。
            //     因此 GPU 显存峰值由最大的单个组决定，而非整个 Avatar。
            var descriptorsByGroup = new Dictionary<UvGroup, List<SolverTexture>>();
            try
            {
                int done = 0;
                foreach (var group in collection.Groups.ToList())
                {
                    var triangles = BuildTriangles(group, collection.Renderers, result);
                    if (!group.IsOptimizable || triangles.Length == 0)
                    {
                        DemoteGroup(group, "no usable geometry");
                        continue;
                    }

                    group.Islands = UvIslandBuilder.Build(triangles, group.ReferenceSize);
                    foreach (var t in triangles)
                        if (result.Plans.TryGetValue(t.Slot, out var plan))
                            plan.Triangles.Add(t);

                    var descriptors = BuildDescriptors(group);
                    if (descriptors.Count == 0)
                    {
                        DemoteGroup(group, "no decodable textures");
                        continue;
                    }
                    descriptorsByGroup[group] = descriptors;

                    bool allSolid = descriptors.All(s => s.Entry.IsSolidColor);
                    foreach (var island in group.Islands) island.SolidColor = allSolid;

                    SolveGroup(group, descriptors, progress);
                    progress?.Step(++done / (float)Mathf.Max(1, collection.Groups.Count));
                }

                // --- type grouping and packing -------------------------------------------------------
                // --- 类型分组与装箱 ------------------------------------------------------------------
                var typeGroups = BuildTypeGroups(collection.Groups.Where(g => g.IsOptimizable && descriptorsByGroup.ContainsKey(g)));
                AtoLog.Info(Stage, $"{typeGroups.Count} texture type groups formed");

                foreach (var tg in typeGroups)
                {
                    PackAndCompose(tg, descriptorsByGroup, result, progress);
                    progress?.Step(0f);
                }
            }
            finally
            {
                _cache.Dispose();
            }

            return result;
        }

        private static void DemoteGroup(UvGroup group, string reason)
        {
            if (group.SkipReason == AtoSkipReason.None) group.SkipReason = AtoSkipReason.DoesNotFitAnyAtlas;
            foreach (var t in group.Textures)
                if (t.SkipReason == AtoSkipReason.None)
                {
                    t.SkipReason = group.SkipReason;
                    t.SkipDetail = reason;
                }
            AtoLog.Debug_(Stage, $"group {group.Index} demoted: {reason}");
        }

        #region Geometry

        private SourceTriangle[] BuildTriangles(UvGroup group, IReadOnlyList<Renderer> renderers, AtlasStageResult result)
        {
            var output = new List<SourceTriangle>(4096);
            var rendererByMesh = new Dictionary<Mesh, Renderer>();
            foreach (var r in renderers)
            {
                var m = r is SkinnedMeshRenderer smr ? smr.sharedMesh
                    : (r.TryGetComponent<MeshFilter>(out var mf) ? mf.sharedMesh : null);
                if (m != null && !rendererByMesh.ContainsKey(m)) rendererByMesh[m] = r;
            }

            foreach (var slot in group.Slots)
            {
                var mesh = slot.Mesh;
                if (mesh == null || slot.SubMesh >= mesh.subMeshCount) continue;

                var uvs = MeshGeometry.GetUv(mesh, slot.Channel);
                if (uvs == null)
                {
                    AtoLog.Warning(Stage, $"mesh '{mesh.name}' has no UV{slot.Channel}; group {group.Index} cannot be atlased.");
                    group.SkipReason = AtoSkipReason.NoFreeUVChannel;
                    return Array.Empty<SourceTriangle>();
                }

                var indices = mesh.GetTriangles(slot.SubMesh);

                Vector3 scale = Vector3.one;
                if (rendererByMesh.TryGetValue(mesh, out var renderer))
                {
                    scale = renderer.transform.lossyScale;
                    var path = nadena.dev.ndmf.runtime.RuntimeUtil.RelativePath(_ctx.AvatarRootObject, renderer.gameObject);
                    if (path != null)
                    {
                        var animated = _maxAnimatedScale(path);
                        scale = new Vector3(
                            Mathf.Max(Mathf.Abs(scale.x), Mathf.Abs(animated.x)),
                            Mathf.Max(Mathf.Abs(scale.y), Mathf.Abs(animated.y)),
                            Mathf.Max(Mathf.Abs(scale.z), Mathf.Abs(animated.z)));
                    }
                }

                var used = new List<int>(indices);
                var status = MeshGeometry.ClassifyRange(uvs, used, out var shift);
                if (status == UvRangeStatus.CrossesSeam)
                {
                    AtoReporting.Warn(Stage, "ATO:warn:uvCrossesSeam", mesh,
                        mesh.name, slot.SubMesh.ToString(), slot.Channel.ToString());
                    group.SkipReason = AtoSkipReason.UVOutOfRangeCrossingSeam;
                    return Array.Empty<SourceTriangle>();
                }

                var plan = new UvRewritePlan { Slot = slot, Group = group, Shift = shift };
                result.Plans[slot] = plan;

                // EN: Per triangle maximum area over the base pose and every blend shape at weight 100.
                // ZH: 逐三角形取基础姿态与每个形态键权重 100 时面积的最大值。
                var areas = MeshGeometry.TriangleMaxWorldAreas(mesh, indices, scale);

                for (int t = 0; t + 2 < indices.Length; t += 3)
                {
                    int i0 = indices[t], i1 = indices[t + 1], i2 = indices[t + 2];
                    output.Add(new SourceTriangle
                    {
                        Slot = slot,
                        TriangleIndex = t / 3,
                        UvA = uvs[i0] + shift,
                        UvB = uvs[i1] + shift,
                        UvC = uvs[i2] + shift,
                        WorldArea = areas[t / 3],
                        IslandIndex = -1,
                    });
                }
            }

            return output.ToArray();
        }

        #endregion

        #region Sources

        /// <summary>
        /// EN: Builds the per texture metadata the solver needs. No pixels are decoded here; the actual
        ///     decode happens through <see cref="LinearSourceCache"/> only while it is needed.
        /// ZH: 构建求解器所需的逐贴图元数据。此处不解码任何像素；
        ///     真正的解码只在需要时通过 <see cref="LinearSourceCache"/> 进行。
        /// </summary>
        private List<SolverTexture> BuildDescriptors(UvGroup group)
        {
            var list = new List<SolverTexture>();
            foreach (var entry in group.Textures)
            {
                if (!entry.IsOptimizable) continue;

                var strictest = AtoAlphaMode.Opaque;
                float cutoff = 1f;
                foreach (var u in entry.Usages)
                {
                    if (u.AlphaMode > strictest) strictest = u.AlphaMode;
                    if (u.AlphaMode == AtoAlphaMode.Cutout) cutoff = Mathf.Min(cutoff, u.Cutoff);
                }

                list.Add(new SolverTexture
                {
                    Entry = entry,
                    LinearSource = null,
                    AlphaMode = strictest,
                    Cutoff = strictest == AtoAlphaMode.Cutout ? cutoff : 0.5f,
                    NormalEncoding = DetectNormalEncoding(entry),
                });
            }
            return list;
        }

        /// <summary>
        /// EN: Pins every source of the group, solves all its islands, then unpins. The pin is what makes
        ///     the whole group visible to the solver at once, which is required by the weakest link rule.
        /// ZH: 固定该组的所有源贴图，求解其全部岛，然后解除固定。
        ///     固定是为了让求解器一次性看到整组——这是木桶效应规则的要求。
        /// </summary>
        private void SolveGroup(UvGroup group, List<SolverTexture> descriptors, AtoProgress progress)
        {
            var handles = new List<LinearSourceCache.Handle>(descriptors.Count);
            try
            {
                foreach (var d in descriptors)
                {
                    try
                    {
                        var handle = _cache.Acquire(d.Entry, group.ReferenceSize);
                        handles.Add(handle);
                        d.LinearSource = handle.Texture;
                    }
                    catch (Exception e)
                    {
                        AtoLog.Warning(Stage, $"could not decode '{d.Entry.Texture.name}': {e.Message}");
                        d.Entry.SkipReason = AtoSkipReason.DecodeFailed;
                    }
                }

                var usable = descriptors.Where(d => d.LinearSource != null).ToList();
                if (usable.Count == 0)
                {
                    DemoteGroup(group, "no decodable textures");
                    return;
                }

                var q = _profile.EffectiveQuality;
                foreach (var island in group.Islands)
                    IslandQualitySolver.Solve(island, usable, q, group.ReferenceSize, progress);
            }
            finally
            {
                foreach (var h in handles) h.Dispose();
                foreach (var d in descriptors) d.LinearSource = null;
            }
        }

        private static NormalEncoding DetectNormalEncoding(TextureEntry entry)
        {
            if (entry.Kind != AtoTextureKind.Normal) return NormalEncoding.Rgb;
            return entry.Texture.format switch
            {
                TextureFormat.BC5 => NormalEncoding.RedGreen,
                TextureFormat.DXT5 => NormalEncoding.AlphaGreen,
                TextureFormat.DXT5Crunched => NormalEncoding.AlphaGreen,
                _ => NormalEncoding.Rgb,
            };
        }

        #endregion

        #region Type groups

        /// <summary>
        /// EN: The layer key of a texture inside its UV group. Colour space and filter mode are part of
        ///     the key because they cannot be mixed inside one atlas.
        /// ZH: 贴图在其 UV 组内的层键。色彩空间与过滤模式也是键的一部分，
        ///     因为它们无法在同一张图集内混用。
        /// </summary>
        private static string LayerKey(TextureEntry e)
            => $"{e.Kind}|{(e.SRgb ? "srgb" : "linear")}|{e.FilterMode}";

        private static List<TypeGroup> BuildTypeGroups(IEnumerable<UvGroup> groups)
        {
            var map = new Dictionary<string, TypeGroup>(StringComparer.Ordinal);
            foreach (var g in groups)
            {
                var layers = g.Textures.Where(t => t.IsOptimizable).Select(LayerKey).Distinct().OrderBy(s => s, StringComparer.Ordinal).ToList();
                if (layers.Count == 0) continue;
                var signature = string.Join("+", layers);
                if (!map.TryGetValue(signature, out var tg))
                {
                    map[signature] = tg = new TypeGroup { Signature = signature };
                    tg.Layers.AddRange(layers);
                }
                tg.Groups.Add(g);
            }

            // EN: Deterministic ordering: heaviest signature first, so the biggest work happens early.
            // ZH: 确定性排序：最“重”的签名优先，让最大的工作量先做完。
            return map.Values
                .OrderByDescending(t => t.Groups.Sum(g => g.Islands.Sum(i => (long)i.ScaledSize.x * i.ScaledSize.y)))
                .ThenBy(t => t.Signature, StringComparer.Ordinal)
                .ToList();
        }

        #endregion

        #region Packing and composition

        private void PackAndCompose(TypeGroup typeGroup, Dictionary<UvGroup, List<SolverTexture>> sources,
            AtlasStageResult result, AtoProgress progress)
        {
            int maxEdge = TextureFormatResolver.MaxEdge(_platform);
            var candidates = AtlasCandidatePool.Build(maxEdge, _profile.allowNpot);
            int minPadding = (int)_profile.minPadding;

            // EN: Queue ordered by total rasterized island area, descending, as specified.
            // ZH: 按规格要求，队列以光栅化岛总面积降序排列。
            var queue = typeGroup.Groups
                .OrderByDescending(g => g.Islands.Sum(i => (long)i.ScaledSize.x * i.ScaledSize.y))
                .ToList();

            while (queue.Count > 0)
            {
                long needed = queue.Sum(g => g.Islands.Sum(i => (long)i.ScaledSize.x * i.ScaledSize.y));
                var viable = candidates.Where(c => c.Area >= needed).ToList();
                if (viable.Count == 0) viable = new List<AtlasCandidate> { candidates[candidates.Count - 1] };

                List<UvGroup> packedGroups = null;
                AtlasCandidate chosen = default;
                int chosenPadding = minPadding;
                float utilization = 0f;

                foreach (var candidate in viable)
                {
                    int padding = AtlasCandidatePool.PaddingFor(candidate, minPadding);
                    if (TryPackQueue(queue, candidate, padding, out var placed, out utilization))
                    {
                        packedGroups = placed;
                        chosen = candidate;
                        chosenPadding = padding;
                        break;
                    }
                }

                if (packedGroups == null || packedGroups.Count == 0)
                {
                    // EN: Not even the biggest atlas takes the largest group on its own; abandon it.
                    // ZH: 连最大的图集都装不下最大的那个组；放弃该组。
                    var failed = queue[0];
                    AtoReporting.Warn(Stage, "ATO:warn:groupTooLarge", failed.Textures.FirstOrDefault()?.Texture,
                        failed.Index.ToString(), failed.Islands.Count.ToString());
                    DemoteGroup(failed, "does not fit into the largest allowed atlas");
                    queue.RemoveAt(0);
                    continue;
                }

                int atlasIndex = _atlasCounter++;
                var size = new Vector2Int(chosen.Width, chosen.Height);
                foreach (var g in packedGroups)
                {
                    result.AtlasSizeOf[g] = size;
                    queue.Remove(g);
                }

                ComposeAtlas(typeGroup, packedGroups, sources, atlasIndex, size, chosenPadding, utilization, result);
                progress?.Step(0f);
            }
        }

        /// <summary>
        /// EN: Greedily packs as many whole UV groups as possible into one candidate atlas. A group is
        ///     atomic: either every island of it fits, or none of it is placed.
        /// ZH: 贪心地把尽可能多的完整 UV 组装进一张候选图集。组是原子的：
        ///     要么它的每一个岛都放得下，要么一个都不放。
        /// </summary>
        private bool TryPackQueue(List<UvGroup> queue, AtlasCandidate candidate, int padding,
            out List<UvGroup> placedGroups, out float utilization)
        {
            int cellsX = candidate.Width / UvIslandBuilder.CellSize;
            int cellsY = candidate.Height / UvIslandBuilder.CellSize;
            var packer = new BitmaskPacker(cellsX, cellsY);
            placedGroups = new List<UvGroup>();
            utilization = 0f;

            foreach (var group in queue)
            {
                var items = group.Islands.Select(i => BitmaskPacker.BuildItem(i, UvIslandBuilder.CellSize, padding)).ToList();
                BitmaskPacker.SortItems(items);

                // EN: Remember the previous placement so a failed attempt can be rolled back exactly.
                // ZH: 记住之前的放置，使失败的尝试可以精确回滚。
                var snapshot = group.Islands.Select(i => (i, i.AtlasOrigin, i.Rotated, i.AtlasIndex)).ToList();
                var packerSnapshot = packer.Snapshot();

                bool ok = true;
                foreach (var item in items)
                {
                    if (!packer.TryPlace(item, allowRotation: true)) { ok = false; break; }
                    item.Island.AtlasIndex = -2; // EN: tentative / ZH: 暂定
                }

                if (!ok)
                {
                    packer.Restore(packerSnapshot);
                    foreach (var (isl, origin, rotated, idx) in snapshot)
                    {
                        isl.AtlasOrigin = origin;
                        isl.Rotated = rotated;
                        isl.AtlasIndex = idx;
                    }
                    continue;
                }

                int padCells = Mathf.CeilToInt(padding / (float)UvIslandBuilder.CellSize);
                foreach (var isl in group.Islands)
                {
                    isl.AtlasOrigin = isl.AtlasOrigin * UvIslandBuilder.CellSize
                                      + Vector2Int.one * (padCells * UvIslandBuilder.CellSize);
                }
                placedGroups.Add(group);
            }

            if (placedGroups.Count == 0) return false;
            utilization = packer.OccupiedCells / (float)(cellsX * cellsY);
            return true;
        }

        private void ComposeAtlas(TypeGroup typeGroup, List<UvGroup> groups,
            Dictionary<UvGroup, List<SolverTexture>> sources, int atlasIndex, Vector2Int size,
            int padding, float utilization, AtlasStageResult result)
        {
            foreach (var layer in typeGroup.Layers)
            {
                RenderTexture accumulated = GpuTextureUtil.GetTemp(size.x, size.y);
                var prevActive = RenderTexture.active;
                RenderTexture.active = accumulated;
                GL.Clear(true, true, new Color(0, 0, 0, 0));
                RenderTexture.active = prevActive;

                TextureEntry representative = null;
                var contributing = new List<TextureEntry>();

                try
                {
                    foreach (var group in groups)
                    {
                        var solver = sources[group].FirstOrDefault(s => LayerKey(s.Entry) == layer);
                        if (solver == null) continue;
                        representative ??= solver.Entry;
                        contributing.Add(solver.Entry);

                        // EN: Decode just this one texture, draw it, and let the cache reclaim it.
                        // ZH: 只解码这一张贴图，绘制后即交还缓存回收。
                        using var handle = _cache.Acquire(solver.Entry, group.ReferenceSize);
                        AtlasComposer.Compose(handle.Texture, group.Islands, -2, size, solver.Entry.HasAlpha, accumulated);
                    }

                    if (representative == null) continue;

                    var dilated = AtlasComposer.Dilate(accumulated);
                    try
                    {
                        var tex = GpuTextureUtil.ToTexture2D(dilated, representative.SRgb, ResolveMipmaps(representative));
                        tex.name = $"ATO_{atlasIndex}_{representative.Kind}";
                        tex.wrapMode = TextureWrapMode.Clamp;
                        tex.filterMode = representative.FilterMode;
                        tex.anisoLevel = contributing.Max(c => c.AnisoLevel);
                        CompressAndSave(tex, representative, contributing);

                        foreach (var entry in contributing) result.ReplacementTexture[entry] = tex;

                        var info = new AtlasResult
                        {
                            Index = atlasIndex,
                            Size = size,
                            Group = groups[0],
                            Utilization = utilization,
                            Texture = tex,
                        };
                        info.Sources.AddRange(contributing);
                        result.Atlases.Add(info);

                        AtoLog.Info(Stage,
                            $"atlas {atlasIndex} [{layer}] {size.x}x{size.y} padding {padding}: " +
                            $"{groups.Count} UV groups, {groups.Sum(g => g.Islands.Count)} islands, " +
                            $"utilization {utilization:P1}, sources: {string.Join(", ", contributing.Select(c => c.Texture.name))}");
                    }
                    finally
                    {
                        if (!ReferenceEquals(dilated, accumulated)) GpuTextureUtil.Release(dilated);
                    }
                }
                finally
                {
                    GpuTextureUtil.Release(accumulated);
                }
            }
        }

        private bool ResolveMipmaps(TextureEntry entry)
            => _profile.textures.mipmapAndStreaming && entry.HasMipmaps;

        private void CompressAndSave(Texture2D tex, TextureEntry representative, List<TextureEntry> contributing)
        {
            try
            {
                bool hasAlpha = contributing.Any(c => c.HasAlpha);
                int channelMask = 0;
                foreach (var c in contributing) channelMask |= c.UsedChannelMask;

                TextureFormat format;
                switch (representative.Kind)
                {
                    case AtoTextureKind.Normal:
                        format = TextureFormatResolver.ResolveNormal(_platform, _profile.textures.normalFormat, _profile.allowNpot);
                        break;
                    case AtoTextureKind.Grayscale:
                        bool multi = CountBits(channelMask & 0xF) > 1;
                        format = TextureFormatResolver.ResolveGrayscale(_platform, _profile.textures.grayscaleFormat, multi, _profile.allowNpot, out _);
                        break;
                    default:
                        format = TextureFormatResolver.ResolveColor(_platform, hasAlpha,
                            _profile.textures.colorOpaqueFormat, _profile.textures.colorAlphaFormat, _profile.allowNpot);
                        break;
                }
                EditorTextureCompressor.Compress(tex, format);
            }
            catch (Exception e)
            {
                AtoLog.Warning(Stage, $"compression failed for '{tex.name}' ({e.Message}); keeping it uncompressed.");
            }

            _ctx.AssetSaver.SaveAsset(tex);
        }

        private static int CountBits(int v)
        {
            int c = 0;
            while (v != 0) { c += v & 1; v >>= 1; }
            return c;
        }

        #endregion
    }
}
