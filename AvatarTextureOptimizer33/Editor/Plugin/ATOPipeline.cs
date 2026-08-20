// SPDX-License-Identifier: MIT
// EN: The orchestrator. Runs every stage of the optimisation in order and owns all native resources.
// ZH: 总控。按顺序执行优化的每个阶段，并持有全部原生资源。

using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf;
using Net.Fosa.AvatarTextureOptimizer.Editor.API;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// EN: A queue of packing units sharing the same set of texture classes.
    /// ZH: 一组共享相同贴图类别集合的装箱队列。
    /// </summary>
    internal sealed class ATOQueue
    {
        public string Signature;
        public readonly HashSet<ATOTextureClass> Classes = new HashSet<ATOTextureClass>();
        public readonly List<ATOPackUnit> Units = new List<ATOPackUnit>();
    }

    /// <summary>
    /// EN: Runs the whole optimisation for one avatar.
    /// ZH: 对一个 Avatar 执行完整优化流程。
    /// </summary>
    public sealed class ATOPipeline : IDisposable
    {
        private readonly BuildContext _ctx;
        private readonly ATOSettings _settings;
        private readonly ATOLog _log;
        private readonly ATOReporter _reporter;
        private readonly ATOProgress _progress;
        private readonly ATOTextureCache _cache;
        private readonly ATOShaderAnalyzer _shaderAnalyzer;
        private readonly ATOAAOCompat _aao;
        private readonly ATOTextureWriter _writer;
        private readonly ATOAtlasComposer _composer;
        private readonly ATOMeshRewriter _meshRewriter;
        private readonly ATOMaterialRewriter _materialRewriter;
        private readonly ATOPlatform _platform;
        private readonly ATOPlatformProfile _profile;
        private readonly ATOQualityParameters _quality;

        private readonly Dictionary<ATOUVKey, ATOIslandSet> _islandSets = new Dictionary<ATOUVKey, ATOIslandSet>();
        private readonly List<ATOUVGroup> _groups = new List<ATOUVGroup>();
        private readonly List<ATOAtlas> _atlases = new List<ATOAtlas>();
        private readonly Dictionary<Texture2D, Texture2D> _textureResult = new Dictionary<Texture2D, Texture2D>();

        public readonly ATOStatistics Statistics = new ATOStatistics();

        private ATOAnimationInfo _anim;
        private ATOScanResult _scan;

        public ATOPipeline(BuildContext ctx, ATOSettings settings, ATOLog log, ATOProgress progress)
        {
            _ctx = ctx;
            _settings = settings;
            _log = log;
            _progress = progress;
            _reporter = new ATOReporter(log);
            _cache = new ATOTextureCache(log);
            _shaderAnalyzer = new ATOShaderAnalyzer(log);
            _aao = new ATOAAOCompat(log);
            _writer = new ATOTextureWriter(log, _reporter);
            _composer = new ATOAtlasComposer(log, _cache);
            _meshRewriter = new ATOMeshRewriter(log);
            _materialRewriter = new ATOMaterialRewriter(log);
            _platform = ATOTextureWriter.CurrentPlatform();
            _profile = settings.EffectiveProfile(_platform);
            _quality = settings.EffectiveQuality();
        }

        // ------------------------------------------------------------------ entry point

        /// <summary>
        /// EN: Runs the pipeline. Throws <see cref="ATOCancelledException"/> when the user cancels.
        /// ZH: 执行整个管线。用户取消时抛出 <see cref="ATOCancelledException"/>。
        /// </summary>
        public void Run(IEnumerable<nadena.dev.ndmf.animator.VirtualClip> clips)
        {
            var root = _ctx.AvatarRootObject;

            using (_log.Step("1. animation analysis"))
            {
                _progress.BeginPhase("ato:progress:animation", 0.00f, 0.05f);
                _anim = ATOAnimationAnalyzer.Analyze(clips, _log);
            }

            using (_log.Step("2. avatar scan"))
            {
                _progress.BeginPhase("ato:progress:scan", 0.05f, 0.15f);
                var scanner = new ATOAvatarScanner(_log, _reporter, _shaderAnalyzer, _settings, _anim);
                _scan = scanner.Scan(root, _cache);
                ATOExtensions.InvokeScanned(root, _scan);
            }

            Statistics.TexturesConsidered = _scan.Textures.Count;
            foreach (var t in _scan.Textures.Values)
            {
                if (t.Whitelisted)
                {
                    Statistics.TexturesWhitelisted++;
                    continue;
                }

                Statistics.OriginalBytes += t.OriginalByteSize;
            }

            if (!_settings.generateAtlas)
            {
                using (_log.Step("3. whole texture rescale"))
                {
                    _progress.BeginPhase("ato:progress:quality", 0.15f, 0.70f);
                    RescaleWholeTextures();
                }
            }
            else
            {
                using (_log.Step("3. UV islands"))
                {
                    _progress.BeginPhase("ato:progress:islands", 0.15f, 0.30f);
                    BuildIslands();
                }

                using (_log.Step("4. UV groups"))
                {
                    BuildUVGroups();
                }

                using (_log.Step("5. target quality"))
                {
                    _progress.BeginPhase("ato:progress:quality", 0.30f, 0.60f);
                    EvaluateQuality();
                }

                using (_log.Step("6. packing"))
                {
                    _progress.BeginPhase("ato:progress:pack", 0.60f, 0.72f);
                    PackAtlases();
                    ATOExtensions.InvokePacked(root, _atlases);
                }

                using (_log.Step("7. atlas composition"))
                {
                    _progress.BeginPhase("ato:progress:compose", 0.72f, 0.88f);
                    ComposeAtlases();
                }

                using (_log.Step("8. leftover rescale"))
                {
                    RescaleWholeTextures();
                }
            }

            using (_log.Step("9. mesh & material rewrite"))
            {
                _progress.BeginPhase("ato:progress:rewrite", 0.88f, 0.96f);
                RewriteAvatar();
            }

            using (_log.Step("10. final deduplication"))
            {
                _progress.BeginPhase("ato:progress:finalDedup", 0.96f, 1.00f);
                FinalDeduplication();
            }

            foreach (var t in _textureResult.Values)
            {
                if (t == null) continue;
                Statistics.ResultBytes += EstimateBytes(t);
            }

            ATOExtensions.InvokeCompleted(root, Statistics);
        }

        private static long EstimateBytes(Texture2D t) => (long)t.width * t.height * 4;

        // ------------------------------------------------------------------ islands

        private void BuildIslands()
        {
            var builder = new ATOUVIslandBuilder(_log, _settings.mergeOverlappingIslands);

            // EN: The largest scale any renderer of a mesh can reach drives the world area.
            // ZH: 使用某网格的所有渲染器中最大的缩放决定世界面积。
            var meshScale = new Dictionary<Mesh, Vector3>();
            foreach (var r in _scan.Renderers)
            {
                if (r.Mesh == null) continue;
                if (meshScale.TryGetValue(r.Mesh, out var existing))
                {
                    meshScale[r.Mesh] = Vector3.Max(existing, r.MaxLossyScale);
                }
                else
                {
                    meshScale[r.Mesh] = r.MaxLossyScale;
                }
            }

            var keys = new HashSet<ATOUVKey>();
            foreach (var tex in _scan.Textures.Values)
            {
                if (tex.Whitelisted) continue;
                foreach (var key in tex.UVKeys) keys.Add(key);
            }

            var done = 0;
            foreach (var key in keys)
            {
                _progress.Report(done++ / (float)Math.Max(1, keys.Count), key.ToString());

                var scale = meshScale.TryGetValue(key.Mesh, out var s) ? s : Vector3.one;
                var set = builder.Build(key, scale);
                _islandSets[key] = set;

                if (!set.CrossesWrapSeam) continue;

                _reporter.Warn("ato:warn:uvOutOfRange", key.Mesh, key.ToString());
                foreach (var tex in _scan.Textures.Values)
                    if (tex.UVKeys.Contains(key))
                        tex.AtlasBlocked = true;
            }

            _log.Info("island", $"built islands for {keys.Count} UV streams");
        }

        // ------------------------------------------------------------------ UV groups

        /// <summary>
        /// EN: A UV stream that any whitelisted or blocked texture samples must not be repacked, therefore
        ///     every other texture sampling the same stream is blocked from atlasing as well (fixpoint).
        /// ZH: 只要某路 UV 被白名单或被阻止的贴图采样，就不能重排；因此采样同一路 UV 的其他贴图
        ///     也必须一并禁止图集化（求不动点）。
        /// </summary>
        private void PropagateAtlasBlocking()
        {
            var changed = true;
            var rounds = 0;

            while (changed && rounds++ < 64)
            {
                changed = false;
                var blockedKeys = new HashSet<ATOUVKey>();

                foreach (var tex in _scan.Textures.Values)
                {
                    if (!tex.Whitelisted && !tex.AtlasBlocked) continue;
                    foreach (var key in tex.UVKeys) blockedKeys.Add(key);
                }

                foreach (var tex in _scan.Textures.Values)
                {
                    if (tex.Whitelisted || tex.AtlasBlocked) continue;
                    foreach (var key in tex.UVKeys)
                    {
                        if (!blockedKeys.Contains(key)) continue;
                        tex.AtlasBlocked = true;
                        tex.BlockReason ??= $"shares {key} with an excluded texture";
                        changed = true;
                        break;
                    }
                }
            }

            var blocked = _scan.Textures.Values.Count(t => t.AtlasBlocked && !t.Whitelisted);
            if (blocked > 0)
                _log.Info("group", $"{blocked} textures skip atlasing because they share UVs with excluded textures");
        }

        private void BuildUVGroups()
        {
            PropagateAtlasBlocking();

            // EN: Connected components of the (UV key <-> texture) bipartite graph.
            // ZH: (UV 键 <-> 贴图) 二部图的连通分量。
            var textureToGroup = new Dictionary<ATOTextureInfo, ATOUVGroup>();
            var keyToGroup = new Dictionary<ATOUVKey, ATOUVGroup>();

            foreach (var tex in _scan.Textures.Values)
            {
                if (tex.Whitelisted || tex.AtlasBlocked || tex.UVKeys.Count == 0) continue;

                ATOUVGroup group = null;
                foreach (var key in tex.UVKeys)
                    if (keyToGroup.TryGetValue(key, out var existing))
                    {
                        group = existing;
                        break;
                    }

                if (group == null)
                {
                    group = new ATOUVGroup { Id = _groups.Count };
                    _groups.Add(group);
                }

                MergeInto(group, tex, textureToGroup, keyToGroup);
            }

            _groups.RemoveAll(g => g.Textures.Count == 0);
            for (var i = 0; i < _groups.Count; i++)
            {
                var g = _groups[i];
                g.Id = i;
                foreach (var key in g.Keys)
                    if (_islandSets.TryGetValue(key, out var set))
                        g.Islands.AddRange(set.Islands);
                foreach (var t in g.Textures) g.Classes.Add(t.Class);
            }

            _log.Info("group", $"{_groups.Count} UV groups " +
                               $"(islands={_groups.Sum(g => g.Islands.Count)}, textures={_groups.Sum(g => g.Textures.Count)})");
        }

        private void MergeInto(ATOUVGroup group, ATOTextureInfo tex,
            Dictionary<ATOTextureInfo, ATOUVGroup> textureToGroup, Dictionary<ATOUVKey, ATOUVGroup> keyToGroup)
        {
            if (textureToGroup.TryGetValue(tex, out var existing) && existing == group) return;

            if (existing != null && existing != group)
            {
                // EN: Two groups collided; merge the smaller into the larger. ZH: 两个组冲突，把小的并入大的。
                var from = existing;
                foreach (var t in from.Textures.ToList())
                {
                    if (!group.Textures.Contains(t)) group.Textures.Add(t);
                    textureToGroup[t] = group;
                }

                foreach (var k in from.Keys)
                {
                    if (!group.Keys.Contains(k)) group.Keys.Add(k);
                    keyToGroup[k] = group;
                }

                from.Textures.Clear();
                from.Keys.Clear();
                return;
            }

            group.Textures.Add(tex);
            textureToGroup[tex] = group;

            foreach (var key in tex.UVKeys)
            {
                if (keyToGroup.TryGetValue(key, out var owner) && owner != group)
                {
                    foreach (var t in owner.Textures.ToList())
                    {
                        if (!group.Textures.Contains(t)) group.Textures.Add(t);
                        textureToGroup[t] = group;
                    }

                    foreach (var k in owner.Keys)
                    {
                        if (!group.Keys.Contains(k)) group.Keys.Add(k);
                        keyToGroup[k] = group;
                    }

                    owner.Textures.Clear();
                    owner.Keys.Clear();
                }

                if (!group.Keys.Contains(key)) group.Keys.Add(key);
                keyToGroup[key] = group;
            }
        }

        // ------------------------------------------------------------------ quality

        /// <summary>EN: Per group, per class required island size. ZH: 每个组、每个类别所需的岛尺寸。</summary>
        private readonly Dictionary<(ATOUVGroup, ATOTextureClass), float> _classScale =
            new Dictionary<(ATOUVGroup, ATOTextureClass), float>();

        private void EvaluateQuality()
        {
            using var evaluator = new ATOQualityEvaluator(_log, _cache, _quality, _settings.IsLossless());

            var totalIslands = _groups.Sum(g => g.Islands.Count);
            var done = 0;

            foreach (var group in _groups)
            {
                foreach (var island in group.Islands)
                {
                    _progress.Report(done++ / (float)Math.Max(1, totalIslands), null);

                    if (!_islandSets.TryGetValue(island.Key, out var set) || set.NormalisedUV == null) continue;
                    var triangles = island.Key.Mesh.GetTriangles(island.Key.SubMesh);

                    var layout = Vector2Int.zero;
                    var maxSource = Vector2Int.zero;
                    var perClass = new Dictionary<ATOTextureClass, Vector2Int>();

                    foreach (var tex in group.Textures)
                    {
                        if (!tex.UVKeys.Contains(island.Key)) continue;

                        var decoded = _cache.Get(tex.Source, tex.Role == ATOTextureRole.Normal);
                        var rect = ATORaster.IslandPixelRect(island.Bounds, decoded.Width, decoded.Height);
                        var scale = evaluator.FindIslandScale(tex, island, set.NormalisedUV, triangles, out _);

                        var size = new Vector2Int(
                            Mathf.Max(1, Mathf.RoundToInt(rect.width * scale.x)),
                            Mathf.Max(1, Mathf.RoundToInt(rect.height * scale.y)));

                        maxSource = Vector2Int.Max(maxSource, new Vector2Int(rect.width, rect.height));
                        layout = Vector2Int.Max(layout, size);

                        var cls = tex.Class;
                        perClass[cls] = perClass.TryGetValue(cls, out var v) ? Vector2Int.Max(v, size) : size;
                    }

                    if (layout == Vector2Int.zero) continue;

                    // EN: Never exceed the largest original size inside the group (bucket effect).
                    // ZH: 绝不超过组内最大的原始尺寸（木桶效应）。
                    layout = Vector2Int.Min(layout, maxSource);
                    island.SourcePixelSize = maxSource;
                    island.TargetPixelSize = layout;
                    island.Scale = new Vector2(
                        layout.x / (float)Mathf.Max(1, island.SourcePixelSize.x),
                        layout.y / (float)Mathf.Max(1, island.SourcePixelSize.y));

                    foreach (var kv in perClass)
                    {
                        var ratio = Mathf.Max(kv.Value.x / (float)layout.x, kv.Value.y / (float)layout.y);
                        var key = (group, kv.Key);
                        _classScale[key] = _classScale.TryGetValue(key, out var prev)
                            ? Mathf.Max(prev, Mathf.Clamp(ratio, 1f / 16f, 1f))
                            : Mathf.Clamp(ratio, 1f / 16f, 1f);
                    }
                }
            }

            _log.Info("quality", $"evaluated {done} islands");
        }

        // ------------------------------------------------------------------ packing

        private void PackAtlases()
        {
            var maxSize = Mathf.Min(_profile.maxAtlasSize, _platform == ATOPlatform.PC ? 8192 : 4096);
            var packer = new ATOAtlasPacker(_log, _settings.allowIslandRotation, _settings.allowNPOT, maxSize);
            var pool = packer.BuildCandidatePool();

            // EN: Queues are formed by the set of texture classes of a group (the "type group" rule).
            // ZH: 队列按组的贴图类别集合形成（即“贴图类型组”规则）。
            var queues = new Dictionary<string, ATOQueue>();

            foreach (var group in _groups)
            {
                if (group.Islands.Count == 0 || group.Textures.Count == 0) continue;

                var unit = BuildPackUnit(group);
                if (unit == null || unit.Items.Count == 0) continue;

                var signature = string.Join("+", group.Classes.Select(c => c.ToString()).OrderBy(x => x));
                if (!queues.TryGetValue(signature, out var queue))
                {
                    queue = new ATOQueue { Signature = signature };
                    foreach (var c in group.Classes) queue.Classes.Add(c);
                    queues[signature] = queue;
                }

                queue.Units.Add(unit);
                group.RasterArea = unit.RasterArea;
            }

            _log.Info("pack", $"{queues.Count} texture type queues");

            foreach (var queue in queues.Values)
            {
                var results = packer.PackQueue(queue.Units, pool,
                    candidate => ComputePadding(_settings.minPadding, Mathf.Max(candidate.Width, candidate.Height)),
                    unit =>
                {
                    unit.Group.AtlasBlocked = true;
                    foreach (var t in unit.Group.Textures) t.AtlasBlocked = true;
                    _reporter.Warn("ato:warn:tooLarge", unit.Group.Textures.FirstOrDefault()?.Source,
                        unit.Group.Textures.FirstOrDefault()?.Source?.name ?? "?");
                });

                foreach (var result in results) CreateAtlasesForResult(queue, result);
                _progress.Report(1f, null);
            }
        }

        private static int ComputePadding(int minPadding, int maxCandidateEdge)
        {
            // EN: padding = ceil(maxEdge / 128), clamped up to the configured minimum.
            // ZH: padding = ceil(最大边长 / 128)，并向上钳制到配置的最小值。
            var padding = Mathf.CeilToInt(maxCandidateEdge / 128f);
            return Mathf.Max(Mathf.Max(4, minPadding), padding);
        }

        private ATOPackUnit BuildPackUnit(ATOUVGroup group)
        {
            var unit = new ATOPackUnit { Group = group };

            foreach (var island in group.Islands)
            {
                if (island.TargetPixelSize.x <= 0 || island.TargetPixelSize.y <= 0) continue;
                if (!_islandSets.TryGetValue(island.Key, out var set) || set.NormalisedUV == null) continue;

                var triangles = island.Key.Mesh.GetTriangles(island.Key.SubMesh);
                var mask = ATORaster.RasterizeMask(set.NormalisedUV, triangles, island.Triangles, island.Bounds,
                    island.TargetPixelSize.x, island.TargetPixelSize.y);

                unit.Items.Add(new ATOPackItem
                {
                    Island = island,
                    Mask = mask,
                    MaskRotated = _settings.allowIslandRotation ? mask.Rotate90() : null,
                });

                unit.RasterArea += mask.PixelArea;
            }

            return unit;
        }

        private void CreateAtlasesForResult(ATOQueue queue, ATOPackResult result)
        {
            var layoutIndex = _atlases.Count;

            foreach (var cls in queue.Classes)
            {
                var scale = 1f;
                foreach (var unit in result.Units)
                {
                    if (_classScale.TryGetValue((unit.Group, cls), out var s)) scale = Mathf.Max(scale == 1f ? 0f : scale, s);
                }

                if (scale <= 0f) scale = 1f;
                scale = SnapClassScale(scale, result.Size);

                var atlas = new ATOAtlas
                {
                    Index = _atlases.Count,
                    Class = cls,
                    Width = Mathf.Max(64, Mathf.RoundToInt(result.Size.Width * scale)),
                    Height = Mathf.Max(64, Mathf.RoundToInt(result.Size.Height * scale)),
                    ClassScale = scale,
                    Name = $"ATO_{cls.Role}_{layoutIndex}_{_atlases.Count}",
                };

                foreach (var unit in result.Units)
                {
                    atlas.Islands.AddRange(unit.Items.Select(i => i.Island));
                    foreach (var tex in unit.Group.Textures)
                        if (tex.Class.Equals(cls))
                            atlas.Sources.Add(tex);
                }

                if (atlas.Sources.Count == 0) continue;

                atlas.HasAlpha = atlas.Sources.Any(RequiresAlpha);
                atlas.Utilisation = result.Utilisation;
                atlas.LayoutWidth = result.Size.Width;
                atlas.LayoutHeight = result.Size.Height;
                _atlases.Add(atlas);

                _log.Info("atlas",
                    $"{atlas.Name}: {atlas.Width}x{atlas.Height} (layout {result.Size}, scale {scale:F2}) " +
                    $"sources={atlas.Sources.Count} islands={atlas.Islands.Count} utilisation={atlas.Utilisation:P1}");
            }
        }

        private float SnapClassScale(float scale, ATOCandidate layout)
        {
            if (scale >= 0.99f) return 1f;

            if (_settings.allowNPOT)
            {
                var w = Mathf.Max(64, Mathf.CeilToInt(layout.Width * scale / 64f) * 64);
                return w / (float)layout.Width;
            }

            var snapped = 1f;
            while (snapped * 0.5f >= scale && layout.Width * snapped * 0.5f >= 64 &&
                   layout.Height * snapped * 0.5f >= 64)
                snapped *= 0.5f;
            return snapped;
        }

        private static bool RequiresAlpha(ATOTextureInfo tex)
        {
            if (tex.Role == ATOTextureRole.ColorTransparent) return true;
            if (tex.Role == ATOTextureRole.Grayscale) return tex.UsedChannels[3];
            return false;
        }

        // ------------------------------------------------------------------ composition

        private void ComposeAtlases()
        {
            var done = 0;
            foreach (var atlas in _atlases)
            {
                _progress.Report(done++ / (float)Math.Max(1, _atlases.Count), atlas.Name);

                using var buffer = ATOAtlasBuffer.Create(atlas.Width, atlas.Height);

                foreach (var tex in atlas.Sources)
                {
                    foreach (var island in atlas.Islands)
                    {
                        if (!tex.UVKeys.Contains(island.Key)) continue;
                        _composer.BlitIsland(buffer, tex, island, atlas.ClassScale);
                        Statistics.IslandsPacked++;
                    }
                }

                _composer.PullPushFill(buffer);

                // EN: An atlas whose sources are all fully opaque does not need an alpha channel at all.
                // ZH: 所有来源都完全不透明时，图集根本不需要 alpha 通道。
                atlas.HasAlpha = atlas.Sources.Any(s => RequiresAlpha(s) && !_cache.Get(s.Source, false).AlphaIsOpaque);

                var usedChannels = new[] { false, false, false, false };
                foreach (var tex in atlas.Sources)
                    for (var c = 0; c < 4; c++)
                        usedChannels[c] |= tex.UsedChannels[c];

                var request = new ATOWriteRequest
                {
                    Name = atlas.Name,
                    Role = atlas.Class.Role,
                    SRGB = atlas.Class.SRGB,
                    Filter = atlas.Class.Filter,
                    AnisoLevel = atlas.Sources.Max(s => s.AnisoLevel),
                    HasAlpha = atlas.HasAlpha,
                    UsedChannels = usedChannels,
                    Profile = _profile,
                    Platform = _platform,
                };

                atlas.Result = _writer.Write(buffer.Pixels, atlas.Width, atlas.Height, request);
                _ctx.AssetSaver.SaveAsset(atlas.Result);

                foreach (var tex in atlas.Sources)
                {
                    tex.Result = atlas.Result;
                    _textureResult[tex.Source] = atlas.Result;
                    Statistics.TexturesOptimised++;
                }

                Statistics.AtlasCount++;
            }
        }

        // ------------------------------------------------------------------ non atlased textures

        private void RescaleWholeTextures()
        {
            using var evaluator = new ATOQualityEvaluator(_log, _cache, _quality, _settings.IsLossless());

            var pending = _scan.Textures.Values
                .Where(t => !t.Whitelisted && t.Result == null)
                .ToList();

            var done = 0;
            foreach (var tex in pending)
            {
                _progress.Report(done++ / (float)Math.Max(1, pending.Count), tex.ToString());

                var decoded = _cache.Get(tex.Source, tex.Role == ATOTextureRole.Normal);
                var scale = Vector2.one;

                if (!_settings.IsLossless())
                {
                    // EN: Treat the whole texture as a single island covering everything.
                    // ZH: 把整张贴图当作一个覆盖全部区域的岛来处理。
                    var pseudo = new ATOIsland
                    {
                        Key = new ATOUVKey(null, 0, 0),
                        Bounds = new Rect(0, 0, 1, 1),
                        Triangles = Array.Empty<int>(),
                        Vertices = Array.Empty<int>(),
                        WorldArea = 0f,
                    };

                    scale = FindWholeTextureScale(evaluator, tex, decoded, pseudo);
                }

                var w = Mathf.Max(1, Mathf.RoundToInt(decoded.Width * scale.x));
                var h = Mathf.Max(1, Mathf.RoundToInt(decoded.Height * scale.y));

                Texture2D result = null;
                if (w == decoded.Width && h == decoded.Height && _settings.IsLossless())
                {
                    // EN: Lossless tier: bit exact copy, only the import parameters change.
                    // ZH: 近无损挡位：逐位拷贝，只改导入参数。
                    result = _writer.CloneVerbatim(tex.Source, "ATO_" + tex.Source.name, MipmapForRole(tex.Role));
                }

                if (result == null) result = WriteRescaled(tex, decoded, w, h);

                tex.Result = result;
                _textureResult[tex.Source] = result;
                _ctx.AssetSaver.SaveAsset(result);
                Statistics.TexturesOptimised++;
            }
        }

        private bool MipmapForRole(ATOTextureRole role)
        {
            switch (role)
            {
                case ATOTextureRole.Normal: return _profile.mipmapNormal;
                case ATOTextureRole.Grayscale: return _profile.mipmapGrayscale;
                default: return _profile.mipmapColor;
            }
        }

        private Vector2 FindWholeTextureScale(ATOQualityEvaluator evaluator, ATOTextureInfo tex,
            ATODecodedTexture decoded, ATOIsland pseudo)
        {
            // EN: Reuse the island search with a full-coverage synthetic island (two triangles).
            // ZH: 用一个覆盖全图的合成岛（两个三角形）复用岛的搜索逻辑。
            var uv = new[]
            {
                new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1),
            };
            var triangles = new[] { 0, 1, 2, 0, 2, 3 };
            pseudo.Triangles = new[] { 0, 1 };
            pseudo.Vertices = new[] { 0, 1, 2, 3 };

            return evaluator.FindIslandScale(tex, pseudo, uv, triangles, out _);
        }

        private Texture2D WriteRescaled(ATOTextureInfo tex, ATODecodedTexture decoded, int w, int h)
        {
            var src = new NativeArray<float4>(decoded.Width * decoded.Height, Allocator.TempJob,
                NativeArrayOptions.UninitializedMemory);
            var dst = new NativeArray<float4>(w * h, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            var half = new NativeArray<half4>(w * h, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);

            try
            {
                var premultiply = tex.Role == ATOTextureRole.ColorTransparent;

                new ATOExtractRegionJob
                {
                    Source = decoded.Pixels,
                    Destination = src,
                    SourceWidth = decoded.Width,
                    SourceHeight = decoded.Height,
                    X0 = 0,
                    Y0 = 0,
                    Width = decoded.Width,
                    Height = decoded.Height,
                    PremultiplyAlpha = premultiply,
                }.Schedule(decoded.Height, 1).Complete();

                new ATODownsampleJob
                {
                    Source = src,
                    Destination = dst,
                    SrcWidth = decoded.Width,
                    SrcHeight = decoded.Height,
                    DstWidth = w,
                    DstHeight = h,
                }.Schedule(h, 1).Complete();

                for (var i = 0; i < dst.Length; i++)
                {
                    var c = dst[i];
                    if (premultiply) c = new float4(c.w > 1e-5f ? c.xyz / c.w : float3.zero, c.w);
                    if (tex.Role == ATOTextureRole.Normal)
                        c = new float4(math.normalizesafe(c.xyz, new float3(0, 0, 1)), 1f);
                    half[i] = new half4((half)c.x, (half)c.y, (half)c.z, (half)c.w);
                }

                return WriteFromDecoded(tex, half, w, h);
            }
            finally
            {
                src.Dispose();
                dst.Dispose();
                half.Dispose();
            }
        }

        private Texture2D WriteFromDecoded(ATOTextureInfo tex, NativeArray<half4> pixels, int w, int h)
        {
            var request = new ATOWriteRequest
            {
                Name = "ATO_" + tex.Source.name,
                Role = tex.Role,
                SRGB = tex.SRGB,
                Filter = tex.Filter,
                AnisoLevel = tex.AnisoLevel,
                HasAlpha = RequiresAlpha(tex) || HasAlphaContent(tex),
                UsedChannels = tex.UsedChannels,
                Profile = _profile,
                Platform = _platform,
            };

            return _writer.Write(pixels, w, h, request);
        }

        private bool HasAlphaContent(ATOTextureInfo tex)
        {
            var decoded = _cache.Get(tex.Source, false);
            return decoded.HasAlphaContent;
        }

        // ------------------------------------------------------------------ rewriting

        private void RewriteAvatar()
        {
            // 1) meshes
            var perMesh = new Dictionary<Mesh, List<ATOUVRewrite>>();

            foreach (var atlas in _atlases)
            {
                foreach (var island in atlas.Islands)
                {
                    if (!island.Placement.Valid) continue;
                    if (!_islandSets.TryGetValue(island.Key, out var set) || set.NormalisedUV == null) continue;

                    if (!perMesh.TryGetValue(island.Key.Mesh, out var list))
                    {
                        list = new List<ATOUVRewrite>();
                        perMesh[island.Key.Mesh] = list;
                    }

                    var rewrite = list.FirstOrDefault(r => r.Key.Equals(island.Key));
                    if (rewrite == null)
                    {
                        rewrite = new ATOUVRewrite
                        {
                            Key = island.Key,
                            NormalisedUV = set.NormalisedUV,
                            LayoutWidth = atlas.LayoutWidth,
                            LayoutHeight = atlas.LayoutHeight,
                        };
                        list.Add(rewrite);
                    }

                    if (!rewrite.Islands.Contains(island)) rewrite.Islands.Add(island);
                }
            }

            var meshMap = new Dictionary<Mesh, Mesh>();
            foreach (var kv in perMesh)
            {
                var newMesh = _meshRewriter.Rewrite(kv.Key, kv.Value);
                if (newMesh == null || ReferenceEquals(newMesh, kv.Key)) continue;
                meshMap[kv.Key] = newMesh;
                _ctx.AssetSaver.SaveAsset(newMesh);
                Statistics.MeshesRewritten++;
            }

            // 2) AAO UV evacuation must happen before the renderer receives the new mesh
            foreach (var r in _scan.Renderers)
            {
                if (!(r.Renderer is SkinnedMeshRenderer smr)) continue;
                if (!meshMap.TryGetValue(r.Mesh, out var newMesh)) continue;
                if (!_aao.Available) continue;

                for (var channel = 0; channel < 8; channel++)
                {
                    if (!_aao.IsTexCoordUsed(smr, channel)) continue;

                    var free = _aao.FindFreeChannel(smr, newMesh);
                    if (free < 0)
                    {
                        _log.Warning("aao", $"'{smr.name}': no free UV channel to evacuate UV{channel}");
                        continue;
                    }

                    var original = new List<Vector2>();
                    r.Mesh.GetUVs(channel, original);
                    if (original.Count == 0) continue;

                    // EN: The rewritten mesh may have more vertices; pad with the last value.
                    // ZH: 重写后的网格顶点可能更多；用最后一个值补齐。
                    while (original.Count < newMesh.vertexCount) original.Add(original[original.Count - 1]);
                    newMesh.SetUVs(free, original.GetRange(0, newMesh.vertexCount));
                    _aao.RegisterEvacuation(smr, channel, free);
                }
            }

            // 3) materials
            Texture2D Map(Texture2D t)
            {
                if (t == null) return null;
                if (_textureResult.TryGetValue(t, out var direct)) return direct;
                if (_scan.Deduplication.TryGetValue(t, out var canonical) &&
                    _textureResult.TryGetValue(canonical, out var viaDedup))
                    return viaDedup;
                return null;
            }

            var materialMap = new Dictionary<Material, Material>();
            foreach (var material in _scan.Materials)
            {
                var rewritten = _materialRewriter.Rewrite(material, Map);
                if (!ReferenceEquals(rewritten, material))
                {
                    materialMap[material] = rewritten;
                    _ctx.AssetSaver.SaveAsset(rewritten);
                }
            }

            // 4) renderers
            foreach (var r in _scan.Renderers)
            {
                if (meshMap.TryGetValue(r.Mesh, out var newMesh))
                {
                    if (r.Renderer is SkinnedMeshRenderer smr) smr.sharedMesh = newMesh;
                    else
                    {
                        var filter = r.Renderer.GetComponent<MeshFilter>();
                        if (filter != null) filter.sharedMesh = newMesh;
                    }
                }

                var materials = r.Renderer.sharedMaterials;
                var changed = false;
                for (var i = 0; i < materials.Length; i++)
                {
                    if (materials[i] == null) continue;
                    if (!materialMap.TryGetValue(materials[i], out var replacement)) continue;
                    materials[i] = replacement;
                    changed = true;
                }

                if (changed) r.Renderer.sharedMaterials = materials;
            }

            // 5) animations
            RewriteAnimations(materialMap);
        }

        private void RewriteAnimations(Dictionary<Material, Material> materialMap)
        {
            if (materialMap.Count == 0) return;

            try
            {
                var asc = _ctx.Extension<nadena.dev.ndmf.animator.AnimatorServicesContext>();
                asc.AnimationIndex.RewriteObjectCurves(obj =>
                {
                    if (obj is Material m && materialMap.TryGetValue(m, out var replacement)) return replacement;
                    return obj;
                });
                _log.Info("anim", $"rewrote animation material references ({materialMap.Count} materials)");
            }
            catch (Exception e)
            {
                _log.Warning("anim", $"could not rewrite animation references: {e.Message}");
            }
        }

        // ------------------------------------------------------------------ final dedup

        private void FinalDeduplication()
        {
            if (_settings.deduplicateTextures) DeduplicateResultTextures();

            if (_settings.deduplicateMaterials)
            {
                var materials = new HashSet<Material>();
                foreach (var r in _scan.Renderers)
                foreach (var m in r.Renderer.sharedMaterials)
                    if (m != null)
                        materials.Add(m);

                var map = _materialRewriter.DeduplicateMaterials(materials);
                if (map.Count > 0)
                {
                    foreach (var r in _scan.Renderers)
                    {
                        var mats = r.Renderer.sharedMaterials;
                        var changed = false;
                        for (var i = 0; i < mats.Length; i++)
                        {
                            if (mats[i] != null && map.TryGetValue(mats[i], out var canonical))
                            {
                                mats[i] = canonical;
                                changed = true;
                            }
                        }

                        if (changed) r.Renderer.sharedMaterials = mats;
                    }

                    RewriteAnimations(map);
                    Statistics.MaterialsDeduplicated = map.Count;
                }

                MergeMaterialSlots();
            }
        }

        /// <summary>
        /// EN: Merges generated textures whose content and parameters are identical and updates the
        ///     material references accordingly.
        /// ZH: 合并内容与参数完全相同的生成贴图，并同步更新材质引用。
        /// </summary>
        private void DeduplicateResultTextures()
        {
            var byHash = new Dictionary<string, Texture2D>();
            var mapping = new Dictionary<Texture2D, Texture2D>();

            foreach (var texture in _textureResult.Values.Distinct())
            {
                if (texture == null) continue;
                if (!_writer.TryGetHash(texture, out var hash)) continue;

                if (byHash.TryGetValue(hash, out var canonical))
                {
                    if (!ReferenceEquals(canonical, texture)) mapping[texture] = canonical;
                }
                else
                {
                    byHash[hash] = texture;
                }
            }

            if (mapping.Count == 0) return;

            foreach (var material in _materialRewriter.Clones.Values.Distinct())
            {
                if (material == null || material.shader == null) continue;
                var shader = material.shader;
                var count = shader.GetPropertyCount();
                for (var i = 0; i < count; i++)
                {
                    if (shader.GetPropertyType(i) != UnityEngine.Rendering.ShaderPropertyType.Texture) continue;
                    var name = shader.GetPropertyName(i);
                    if (!(material.GetTexture(name) is Texture2D tex)) continue;
                    if (!mapping.TryGetValue(tex, out var canonical)) continue;

                    var scale = material.GetTextureScale(name);
                    var offset = material.GetTextureOffset(name);
                    material.SetTexture(name, canonical);
                    material.SetTextureScale(name, scale);
                    material.SetTextureOffset(name, offset);
                }
            }

            foreach (var key in _textureResult.Keys.ToList())
                if (_textureResult[key] != null && mapping.TryGetValue(_textureResult[key], out var canonical))
                    _textureResult[key] = canonical;

            Statistics.TexturesDeduplicated = mapping.Count;
            _log.Info("dedup", $"merged {mapping.Count} generated textures");
        }

        private void MergeMaterialSlots()
        {
            foreach (var r in _scan.Renderers)
            {
                if (r.AnimatedSlots.Count > 0) continue;

                Mesh mesh = null;
                if (r.Renderer is SkinnedMeshRenderer smr) mesh = smr.sharedMesh;
                else
                {
                    var filter = r.Renderer.GetComponent<MeshFilter>();
                    if (filter != null) mesh = filter.sharedMesh;
                }

                if (mesh == null) continue;
                if (!_ctx.IsTemporaryAsset(mesh)) continue; // EN: only touch meshes we created. ZH: 只处理我们生成的网格。

                if (!_materialRewriter.TryMergeSlots(r.Renderer, mesh, r.AnimatedSlots, out var merged,
                        out var materials, out _)) continue;

                if (r.Renderer is SkinnedMeshRenderer smr2) smr2.sharedMesh = merged;
                else
                {
                    var filter = r.Renderer.GetComponent<MeshFilter>();
                    if (filter != null) filter.sharedMesh = merged;
                }

                r.Renderer.sharedMaterials = materials;
                _ctx.AssetSaver.SaveAsset(merged);
            }
        }

        public void Dispose()
        {
            _cache.Dispose();
            _composer.Dispose();
        }
    }
}
