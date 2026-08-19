// AvatarAnalyzer.cs
// Collects renderers/slots/materials, expands the whitelist, dedupes textures and builds
// the island↔texture usage graph. / 收集渲染器/槽位/材质,展开白名单,贴图去重,构建岛↔贴图使用图。
// Copyright (c) 2026 fosa. Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf;
using UnityEditor;
using UnityEngine;

namespace net.fosa.ato
{
    internal sealed partial class ATOProcessor
    {
        // ================================================================== //
        // Component validation / 组件验证
        // ================================================================== //
        private void ValidateComponent()
        {
            var root = _d.Ctx.AvatarRootObject;
            var components = root.GetComponentsInChildren<AvatarTextureOptimizer>(true);
            var descriptor = root.GetComponent<VRC.SDK3.Avatars.Components.VRCAvatarDescriptor>();

            if (components.Length == 0)
            {
                ATOLog.V("no ATO component on avatar; skipping");
                return; // no component → nothing to do / 无组件→不处理
            }

            if (descriptor == null)
            {
                // Avatar root has no descriptor → ATO cannot work / 根上无描述符→无法工作
                ATOErrors.Report(_d.Ctx, ErrorSeverity.Error, "ato.error.no_descriptor", root);
                _d.Component = null;
                return;
            }

            // Multiple components → keep the one on the descriptor object, remove others.
            // 多组件→保留描述符物体上的那个,移除其余。
            var valid = new List<AvatarTextureOptimizer>();
            foreach (var c in components)
                if (c.gameObject == root) valid.Add(c);

            if (valid.Count == 0)
            {
                ATOErrors.Report(_d.Ctx, ErrorSeverity.Error, "ato.error.not_on_root", components[0]);
                _d.Component = null;
                return;
            }

            _d.Component = valid[0];
            if (components.Length > 1)
            {
                for (int i = 0; i < components.Length; i++)
                    if (!ReferenceEquals(components[i], _d.Component))
                    {
                        ATOErrors.Report(_d.Ctx, ErrorSeverity.NonFatal, "ato.warn.extra_component_removed", components[i]);
                        UnityEngine.Object.DestroyImmediate(components[i]);
                    }
            }
            ATOLog.V($"component ok on '{root.name}'");
        }

        // ================================================================== //
        // Platform resolution / 平台解析
        // ================================================================== //
        private void ResolvePlatform()
        {
            var target = EditorUserBuildSettings.activeBuildTarget;
            _d.Platform = target == BuildTarget.Android ? ATOPlatform.Android
                : target == BuildTarget.iOS ? ATOPlatform.iOS
                : ATOPlatform.Windows;
            _d.EffectiveProfile = _d.Component.Resolve(_d.Platform);
            ATOLog.Info($"target platform: {_d.Platform} (build target {target}), " +
                        $"atlas={_d.EffectiveProfile.generateAtlas}, npot={_d.EffectiveProfile.experimentalNpotAtlas}, " +
                        $"padding={(int)_d.EffectiveProfile.padding}, preset={_d.Component.qualityPreset}");
        }

        // ================================================================== //
        // Animation collection / 动画收集
        // ================================================================== //
        private void CollectAnimations()
        {
            _d.Animations = new AnimationDatabase();
            AnimationAnalyzer.Collect(_d.Ctx, _d.Animations);
        }

        // ================================================================== //
        // Renderer collection / 渲染器收集
        // ================================================================== //
        private void CollectRenderers()
        {
            var root = _d.Ctx.AvatarRootTransform;
            int processed = 0, skippedInactive = 0, skippedEditorOnly = 0;

            foreach (var go in root.GetComponentsInChildren<Transform>(true))
            {
                // EditorOnly tag → VRChat removes at build / EditorOnly 标签→构建时移除
                if (IsEditorOnly(go))
                {
                    skippedEditorOnly++;
                    continue;
                }

                var path = RelativePath(go);
                bool activeSelfChain = IsActiveInHierarchy(go);
                bool animatedActive = _d.Animations.AnimatedActivePaths.Contains(path) ||
                                      _d.Animations.AnimatedActivePaths.Contains(path + "/");

                var renderers = go.GetComponents<Renderer>();
                if (renderers.Length == 0) continue;

                foreach (var r in renderers)
                {
                    if (!(r is SkinnedMeshRenderer) && !(r is MeshRenderer)) continue;

                    bool rendererEnabled = r.enabled;
                    // m_Enabled animation not tracked per renderer; treat any material animation as "used".
                    // 渲染器 enable 动画未逐个跟踪;有材质动画即视为使用。
                    bool potentiallyVisible = (activeSelfChain && rendererEnabled) || animatedActive;
                    if (!potentiallyVisible)
                    {
                        skippedInactive++;
                        continue;
                    }

                    Mesh mesh = r is SkinnedMeshRenderer smr ? smr.sharedMesh : r.GetComponent<MeshFilter>()?.sharedMesh;
                    if (mesh == null) { skippedInactive++; continue; }

                    var rec = new RendererRecord
                    {
                        Renderer = r,
                        Path = path,
                        InitiallyActive = activeSelfChain && rendererEnabled,
                        AnimatedActive = animatedActive,
                        Mesh = mesh,
                    };
                    CollectSlotMaterials(rec);
                    _d.Renderers.Add(rec);
                    processed++;
                }
            }

            ATOLog.V($"renderers: {processed} processed, {skippedInactive} inactive/ignored, {skippedEditorOnly} EditorOnly");

            // Max animated scale per renderer (volume-based, combined over hierarchy). / 每渲染器最大动画缩放(按层级合并)
            foreach (var rec in _d.Renderers)
            {
                float factor = 1f;
                var t = rec.Renderer.transform;
                while (t != null && t != _d.Ctx.AvatarRootTransform)
                {
                    var p = RelativePath(t);
                    if (_d.Animations.MaxScaleByPath.TryGetValue(p, out var s)) factor *= Mathf.Max(1f, s);
                    t = t.parent;
                }
                rec.MaxScaleFactor = factor;
            }
        }

        private void CollectSlotMaterials(RendererRecord rec)
        {
            var mats = rec.Renderer.sharedMaterials;
            for (int i = 0; i < mats.Length; i++)
            {
                var list = new List<Material>();
                if (mats[i] != null) list.Add(mats[i]);
                if (_d.Animations.MaterialSwaps.TryGetValue(rec.Path, out var bySlot) &&
                    bySlot.TryGetValue(i, out var swaps))
                    foreach (var m in swaps)
                        if (m != null && !list.Contains(m)) list.Add(m);
                if (list.Count > 0) rec.SlotMaterials[i] = list;
            }
        }

        private static bool IsEditorOnly(Component c)
        {
            try { return c.CompareTag("EditorOnly"); }
            catch (UnityException) { return false; } // untagged prefab root etc. / 未标签等情况
        }

        private static bool IsActiveInHierarchy(GameObject go)
        {
            var t = go.transform;
            while (t != null)
            {
                if (!t.gameObject.activeSelf) return false;
                t = t.parent;
            }
            return true;
        }

        private string RelativePath(Component c)
        {
            if (c == null) return "";
            var sb = new System.Text.StringBuilder();
            var t = c.transform;
            var root = _d.Ctx.AvatarRootTransform;
            if (t == root) return "";
            while (t != null && t != root)
            {
                if (sb.Length > 0) sb.Insert(0, "/");
                sb.Insert(0, t.name);
                t = t.parent;
            }
            return sb.ToString();
        }

        // ================================================================== //
        // Material analysis / 材质分析
        // ================================================================== //
        private void AnalyzeMaterials()
        {
            int analyzed = 0;
            foreach (var rec in _d.Renderers)
            foreach (var kv in rec.SlotMaterials)
            foreach (var mat in kv.Value)
            {
                if (_materialAnalyses.ContainsKey(mat)) continue;
                analyzed++;

                var analysis = ShaderAnalyzer.Analyze(mat, _d.Animations, rec.Path, kv.Key);
                _materialAnalyses[mat] = analysis;
                if (analysis.ShaderUnsupported)
                {
                    foreach (var u in analysis.Usages) MarkWhitelisted(u.Texture, $"shader '{analysis.ShaderName}' unsupported");
                    continue;
                }

                foreach (var u in analysis.Usages)
                {
                    if (u.NonMeshUv) continue; // matcap etc.: not atlasable but not our business / matcap 等不可装箱
                    if (u.HasTransform)
                    {
                        MarkWhitelisted(u.Texture, $"UV transform on {u.PropertyName} of '{mat.name}'");
                        continue;
                    }
                    GetOrCreateNode(u.Texture).Usages.Add(u);
                }

                foreach (var wc in analysis.WhitelistCandidates)
                    MarkWhitelisted(wc.tex, wc.reason);
            }

            // Animation texture swaps merged into nodes / 动画贴图切换并入节点
            foreach (var kv in _d.Animations.TextureSwaps)
                foreach (var e in kv.Value)
                {
                    var node = GetOrCreateNode(e.Tex);
                    // Find usages of the same property on the same renderer path to inherit context. / 继承同属性用途上下文
                    TextureUsage inherited = null;
                    foreach (var u in node.Usages)
                        if (u.PropertyName == e.Prop) { inherited = u; break; }
                    if (inherited == null)
                    {
                        // No original usage found → texture only used by animation; needs a mesh context. / 无原用途→仅动画使用;需要网格上下文
                        MarkWhitelisted(e.Tex, $"animated texture {e.Prop} has no non-animated usage context");
                        continue;
                    }
                    node.Usages.Add(new TextureUsage
                    {
                        Texture = e.Tex, Role = inherited.Role, UvChannel = inherited.UvChannel,
                        PropertyName = inherited.PropertyName, Material = inherited.Material,
                        HasTransform = false, NonMeshUv = false,
                        UsedChannels = inherited.UsedChannels, Alpha = inherited.Alpha,
                        Cutoff = inherited.Cutoff, MultiCutoffs = inherited.MultiCutoffs,
                        BlendAlsoRequired = inherited.BlendAlsoRequired,
                        Srgb = inherited.Srgb, Filter = inherited.Filter,
                    });
                }

            ATOLog.V($"material analysis: {analyzed} materials, {_d.TextureNodes.Count} texture nodes, {_d.WhitelistedTextures.Count} whitelisted");
        }

        private readonly Dictionary<Material, ShaderAnalyzer.MaterialAnalysis> _materialAnalyses =
            new Dictionary<Material, ShaderAnalyzer.MaterialAnalysis>();

        private TextureNode GetOrCreateNode(Texture2D tex)
        {
            if (!_d.TextureNodes.TryGetValue(tex, out var n))
            {
                _d.TextureNodes[tex] = n = new TextureNode
                {
                    Tex = tex, InstanceId = tex.GetInstanceID(),
                    Srgb = ShaderAnalyzer.ImportSrgb(tex),
                    Filter = tex.filterMode,
                };
            }
            return n;
        }

        internal void MarkWhitelisted(Texture2D tex, string reason)
        {
            if (tex == null) return;
            if (_d.WhitelistedTextures.Add(tex))
            {
                ATOLog.V($"whitelist: '{tex.name}' — {reason}");
                _d.ReportDetails.Add($"whitelist: {tex.name} — {reason}");
            }
            _d.TextureNodes.Remove(tex);
        }

        // ================================================================== //
        // Whitelist expansion / 白名单展开
        // ================================================================== //
        private void BuildWhitelist()
        {
            foreach (var obj in _d.Component.whitelist)
            {
                if (obj == null) continue;
                foreach (var tex in WhitelistExpander.Expand(obj, _d.Ctx, _d.Renderers))
                    MarkWhitelisted(tex, $"user whitelist object '{obj.name}' ({obj.GetType().Name})");
            }
            ATOLog.V($"whitelist expansion complete: {_d.WhitelistedTextures.Count} textures");
        }

        // ================================================================== //
        // Texture dedup / 贴图去重
        // ================================================================== //
        private void DedupeTextures()
        {
            if (!_d.Component.dedupeTextures) return;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var map = TextureDeduplicator.BuildMap(_d);
            if (map.Count == 0) return;
            int replaced = 0;

            // Re-point usages / 重定向用途
            foreach (var node in _d.TextureNodes.Values.ToList())
            {
                if (map.TryGetValue(node.Tex, out var canonical))
                {
                    var canonNode = GetOrCreateNode(canonical);
                    foreach (var u in node.Usages) canonNode.Usages.Add(u);
                    _d.TextureNodes.Remove(node.Tex);
                    _d.TextureDedupMap[node.Tex] = canonical;
                    replaced++;
                }
            }

            // Whitelist propagates over dedup results / 去重结果继承白名单
            foreach (var kv in map)
                if (_d.WhitelistedTextures.Contains(kv.Value))
                    MarkWhitelisted(kv.Key, "dedup target is whitelisted");

            sw.Stop();
            ATOLog.V($"texture dedup: {map.Count} duplicate groups, {replaced} replaced ({sw.ElapsedMilliseconds} ms)");
        }

        // ================================================================== //
        // Usage graph / 使用图
        // ================================================================== //
        private void BuildUsageGraph()
        {
            // 1) Link islands↔textures is done in ExtractIslands (needs meshes); here we build
            //    the grouping skeleton. / 岛↔贴图连接在 ExtractIslands 中完成;此处构建分组骨架。
            foreach (var node in _d.TextureNodes.Values)
            {
                node.PrimaryRole = node.Usages.Count == 0 ? TexRole.Color
                    : node.Usages.Any(u => u.Role == TexRole.Color) ? TexRole.Color
                    : node.Usages.Any(u => u.Role == TexRole.Normal) ? TexRole.Normal
                    : TexRole.Mask;

                foreach (var u in node.Usages)
                {
                    if (u.Role == TexRole.Color)
                    {
                        // Companion detection: same material & uv channel / 配对检测:同材质同通道
                        foreach (var other in node.Usages)
                            if (other != u && other.Material == u.Material && other.UvChannel == u.UvChannel)
                            {
                                if (other.Role == TexRole.Normal) node.HasNormalCompanion = true;
                                if (other.Role == TexRole.Mask) node.HasMaskCompanion = true;
                            }
                    }
                }
            }
            ATOLog.V($"usage graph: {_d.TextureNodes.Count} nodes ready for island linking");
        }

        // ================================================================== //
        // Island extraction / 岛提取
        // ================================================================== //
        private void ExtractIslands()
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            int usableSets = 0, unusableSets = 0, totalIslands = 0;

            foreach (var rec in _d.Renderers)
            {
                var mesh = rec.Mesh;
                if (mesh == null) continue;

                // Which (submesh, channel) pairs are actually used by processed textures. / 实际被处理的贴图使用的(子网格,通道)对。
                if (_matToNodes == null) BuildMaterialIndex();
                var wanted = new HashSet<int>();
                foreach (var kv in rec.SlotMaterials)
                foreach (var mat in kv.Value)
                {
                    if (!_matToNodes.TryGetValue(mat, out var nodes)) continue;
                    foreach (var node in nodes)
                    foreach (var u in node.Usages)
                        if (u.Material == mat && u.UvChannel >= 0 && u.UvChannel < 4)
                            wanted.Add(kv.Key * 4 + u.UvChannel);
                }

                foreach (var code in wanted)
                {
                    int submesh = code / 4, channel = code % 4;
                    if (submesh >= mesh.subMeshCount) continue;

                    long key = IslandSetKey(mesh, submesh, channel);
                    if (_islandSetByKey.TryGetValue(key, out int existingSetId))
                    {
                        // reuse / 复用
                        LinkIslandsToTextures(existingSetId, rec, submesh, channel);
                        if (IsSetBlocked(rec, submesh, channel))
                            _d.IslandSets[existingSetId].BlockedByWhitelist = true;
                        continue;
                    }

                    var set = new IslandSetData { Mesh = mesh, SubMesh = submesh, Channel = channel };
                    int setId = _d.IslandSets.Count;
                    _d.IslandSets.Add(set);
                    _islandSetByKey[key] = setId;

                    var result = IslandExtractor.Extract(mesh, submesh, channel, rec, _d.Animations);
                    set.Islands = result.Islands;
                    set.Unusable = result.Unusable;
                    set.UnusableReason = result.UnusableReason;
                    if (result.Normalized) set.NormalizeOffset = result.NormalizeOffset;
                    set.NormalizedUvs = result.NormalizedUvs;

                    if (set.Unusable)
                    {
                        unusableSets++;
                        foreach (var node in _d.TextureNodes.Values)
                        foreach (var u in node.Usages)
                            if (u.Material != null && UsesSlotRec(u, rec, submesh, channel))
                                MarkWhitelisted(u.Texture, $"UV set unusable: {result.UnusableReason}");
                        continue;
                    }

                    usableSets++;
                    totalIslands += set.Islands.Count;
                    LinkIslandsToTextures(setId, rec, submesh, channel);
                    if (IsSetBlocked(rec, submesh, channel))
                        set.BlockedByWhitelist = true;
                }
            }

            PropagateWhitelistBlocking();
            ATOLog.V($"whitelist blocking: {_d.UvGroups.Count(g => g.Textures.Any(t => t.NoAtlas))} components fall back to whole-texture scaling");

            sw.Stop();
            ATOLog.V($"island extraction: {usableSets} usable sets ({totalIslands} islands), {unusableSets} unusable, {sw.ElapsedMilliseconds} ms");
        }

        private readonly Dictionary<long, int> _islandSetByKey = new Dictionary<long, int>();
        private Dictionary<Material, List<TextureNode>> _matToNodes;

        private static long IslandSetKey(Mesh mesh, int submesh, int channel)
        {
            return ((long)mesh.GetInstanceID() << 20) | ((uint)(submesh * 4 + channel) & 0xFFFFF);
        }

        /// <summary>
        /// Any texture sampled on this (slot, channel) that is whitelisted or excluded from
        /// processing → the whole island set must keep its original UVs.
        /// / 该(槽位,通道)上采样到白名单/被排除贴图→整组岛必须保留原UV。
        /// </summary>
        private bool IsSetBlocked(RendererRecord rec, int submesh, int channel)
        {
            List<Material> mats;
            if (!rec.SlotMaterials.TryGetValue(submesh, out mats)) return false;
            foreach (var mat in mats)
            {
                ShaderAnalyzer.MaterialAnalysis analysis;
                if (!_materialAnalyses.TryGetValue(mat, out analysis)) continue;
                foreach (var u in analysis.Usages)
                {
                    if (u.UvChannel != channel || u.NonMeshUv) continue;
                    if (_d.WhitelistedTextures.Contains(u.Texture)) return true;
                    if (!_d.TextureNodes.ContainsKey(u.Texture)) return true;
                }
                foreach (var wc in analysis.WhitelistCandidates)
                    foreach (var u in analysis.Usages)
                        if (u.UvChannel == channel && u.Texture == wc.tex) return true;
            }
            return false;
        }

        /// <summary>
        /// Fixpoint: blocked islands → their textures NoAtlas → other islands of those
        /// textures blocked → ... (texture atomicity + UV consistency). / 不动点传播:
        /// 岛被封禁→其贴图不可图集化→这些贴图的其他岛也封禁(贴图原子性+UV一致性)。
        /// </summary>
        private void PropagateWhitelistBlocking()
        {
            var blocked = new HashSet<long>();
            for (int setId = 0; setId < _d.IslandSets.Count; setId++)
            {
                var set = _d.IslandSets[setId];
                if (!set.BlockedByWhitelist) continue;
                foreach (var isl in set.Islands) blocked.Add(ATOBuildData.Key(setId, isl.Id));
            }

            bool changed = true;
            while (changed)
            {
                changed = false;
                // blocked island → all its textures NoAtlas / 封禁岛→其全部贴图不可图集化
                foreach (var key in blocked)
                {
                    List<TextureNode> list;
                    if (!_d.IslandTextures.TryGetValue(key, out list)) continue;
                    foreach (var n in list)
                        if (!n.NoAtlas) { n.NoAtlas = true; changed = true; }
                }
                // NoAtlas texture → all its islands blocked / 不可图集化贴图→其全部岛封禁
                if (changed)
                {
                    foreach (var node in _d.TextureNodes.Values)
                    {
                        if (!node.NoAtlas) continue;
                        foreach (var iref in node.IslandRefs)
                            if (blocked.Add(iref.Key)) changed = true;
                    }
                }
            }

        }

        private bool UsesSlotRec(TextureUsage u, RendererRecord rec, int submesh, int channel)
        {
            // Whether this usage really targets (renderer, submesh, channel). / 该用途是否真的指向此渲染器/子网格/通道。
            return u.UvChannel == channel &&
                   rec.SlotMaterials.TryGetValue(submesh, out var mats) && mats.Contains(u.Material);
        }

        private void BuildMaterialIndex()
        {
            _matToNodes = new Dictionary<Material, List<TextureNode>>();
            foreach (var node in _d.TextureNodes.Values)
                foreach (var u in node.Usages)
                {
                    if (u.Material == null) continue;
                    if (!_matToNodes.TryGetValue(u.Material, out var list))
                        _matToNodes[u.Material] = list = new List<TextureNode>();
                    if (!list.Contains(node)) list.Add(node);
                }
        }

        private void LinkIslandsToTextures(int setId, RendererRecord rec, int submesh, int channel)
        {
            var set = _d.IslandSets[setId];
            foreach (var node in _d.TextureNodes.Values)
            {
                bool linked = false;
                foreach (var u in node.Usages)
                    if (u.UvChannel == channel && rec.SlotMaterials.TryGetValue(submesh, out var mats) && mats.Contains(u.Material))
                    { linked = true; break; }
                if (!linked) continue;

                foreach (var isl in set.Islands)
                {
                    var iref = new IslandRef(setId, isl.Id);
                    node.IslandRefs.Add(iref);
                    if (!_d.IslandTextures.TryGetValue(iref.Key, out var list))
                        _d.IslandTextures[iref.Key] = list = new List<TextureNode>();
                    if (!list.Contains(node)) list.Add(node);
                }
            }
        }
    }
}
