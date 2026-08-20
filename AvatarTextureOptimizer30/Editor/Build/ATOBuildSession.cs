// ATOBuildSession.cs — 构建会话编排器 / Build session orchestrator.
// 说明：整个 ATO 管线的编排（NDMF 构建上下文内运行）：验证 → RW 启用 → 扫描 → 去重 →
// 岛提取 → 引用构建 → AAO 兼容检查 → 质量求解 → 类型组 → 装箱 → 合成 → 写入 →
// 材质更新 → 去重/槽合并 → 动画重写 → 网格 UV 写入 → 图集去重 → 移除自身 → 报告。
// 全程进度显示与取消支持；取消/异常时释放全部 CPU/GPU/内存资源，保留磁盘临时资产。
// Note: orchestrates the whole ATO pipeline inside the NDMF build context: validation → RW enable → scan → dedup →
// island extraction → ref building → AAO compat → quality solving → type groups → packing → composition → writing →
// material updates → dedup/slot merge → animation rewrite → mesh UV write → atlas dedup → self removal → report.
// Progress & cancellation throughout; on cancel/exception all CPU/GPU/memory resources are released and temp assets stay on disk.

using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;
using VRC.SDK3.Avatars.Components;

namespace Fosa.AvatarTextureOptimizer
{
    /// <summary>构建会话。/ Build session.</summary>
    internal sealed class ATOBuildSession
    {
        private readonly BuildContext _context;
        private ATOConfig _config;
        private ATOAvatarTextureOptimizer _component;
        private ATOAvatarScanResult _scan;
        private ATOQualityEvaluator _evaluator;
        private List<ATOTypeGroup> _groups = new List<ATOTypeGroup>();
        private List<ATOIsland> _allIslands = new List<ATOIsland>();
        private readonly List<ATOPackItem> _packItems = new List<ATOPackItem>();
        private readonly List<ATOAtlasPacker> _packers = new List<ATOAtlasPacker>();
        private ATOTextureWriter _writer;
        private readonly Dictionary<Texture2D, Texture2D> _textureReplacements = new Dictionary<Texture2D, Texture2D>();
        private readonly Dictionary<(Material, string), Texture2D> _usageAtlases = new Dictionary<(Material, string), Texture2D>();
        private readonly Dictionary<(string, string), Dictionary<Texture2D, Texture2D>> _pathPropAtlases = new Dictionary<(string, string), Dictionary<Texture2D, Texture2D>>();
        private readonly HashSet<Texture2D> _wholeTextureForced = new HashSet<Texture2D>();
        private readonly HashSet<Texture2D> _packFailedTextures = new HashSet<Texture2D>();
        private readonly Dictionary<Texture2D, TextureImporter> _rwToggled = new Dictionary<Texture2D, TextureImporter>();
        private readonly Dictionary<ATOIslandRef, ATOIsland> _refIslands = new Dictionary<ATOIslandRef, ATOIsland>();
        private ATOQualityParams _quality;
        private ATOReport _report;

        public ATOBuildSession(BuildContext context)
        {
            _context = context;
        }

        /// <summary>提取的岛数量（报告用）。/ Island count (for reporting).</summary>
        public int IslandCount => _allIslands?.Count ?? 0;

        /// <summary>执行管线。/ Run the pipeline.</summary>
        public void Run()
        {
            ATOProgress.Reset("ATO Avatar Texture Optimizer");
            ATOLog.ClearStages();
            try
            {
                if (!ValidateComponent()) return;
                _config = _component.config ?? new ATOConfig();
                ATOLog.Verbosity = _config.logVerbosity;
                _quality = BuildQualityParams(_config);
                _report = new ATOReport();

                // ---- 阶段 0：输入贴图 Read/Write 临时启用 / stage 0: temporarily enable Read/Write on inputs
                ATOProgress.SetStage("Enabling Read/Write");
                var tRw = new ATOLog.StageTimer("Read/Write setup");
                EnableReadWrite();
                tRw.Detail($"{_rwToggled.Count} textures toggled").Stop();
                ATOProgress.ThrowIfCancelled();

                // ---- 阶段 1：扫描 / stage 1: scan
                ATOProgress.SetStage("Scanning");
                _scan = ATOAvatarScanner.Scan(_context.AvatarRootObject, _component);
                ATOProgress.ThrowIfCancelled();

                // ---- 阶段 2：贴图去重 / stage 2: texture dedup
                ATOProgress.SetStage("Deduplicating textures");
                var dedup = ATOTextureDedup.Deduplicate(_scan, t => _scan.whitelistedTextures.Contains(t));
                ATOTextureDedup.UpdateAnimationReferences(_scan.animation.clips, dedup.replacements);
                ATOAvatarScanner.PropagateWhitelist(_scan);
                ATOProgress.ThrowIfCancelled();

                // ---- 阶段 3：岛提取 / stage 3: island extraction
                ATOProgress.SetStage("Extracting UV islands");
                _evaluator = new ATOQualityEvaluator();
                ExtractIslands();
                ATOProgress.ThrowIfCancelled();

                // ---- 阶段 4：引用构建（首遍，供重叠合并判定）/ stage 4: build refs (first pass, for overlap merge decisions)
                ATOIslandRefBuilder.BuildRefs(_allIslands, _scan.renderers, _scan.whitelistedTextures);
                ATOProgress.ThrowIfCancelled();

                // ---- 阶段 5：重叠岛合并（同贴图引用两岛才合并）+ 引用重建（权威遍）/ stage 5: merge overlaps (only when a texture references both) + rebuild refs (authoritative)
                _allIslands = ATOIslandExtractor.MergeOverlappingIslands(_allIslands, ShareTextureReal);
                BuildRefsAndCheckWraps();
                ATOProgress.ThrowIfCancelled();

                // ---- 阶段 6：AAO UV 兼容检查 / stage 6: AAO UV compatibility check
                var evacuationMap = PrepareAAOEvacuation();
                ATOProgress.ThrowIfCancelled();

                // ---- 阶段 7：质量求解 / stage 7: quality solving
                ATOProgress.SetStage("Solving quality scales");
                SolveScales();
                ATOProgress.ThrowIfCancelled();

                // ---- 阶段 8：类型组 / stage 8: type groups
                BuildGroups();
                ATOProgress.ThrowIfCancelled();

                // ---- 阶段 9：装箱 / stage 9: packing
                if (_config.generateAtlases)
                {
                    ATOProgress.SetStage("Packing atlases");
                    PackGroups();
                    ATOProgress.ThrowIfCancelled();

                    // ---- 阶段 10：图集合成 / stage 10: compose atlases
                    ATOProgress.SetStage("Composing atlases");
                    ComposeAtlases();
                }
                ATOProgress.ThrowIfCancelled();

                // ---- 阶段 11：写入贴图与图集 / stage 11: write textures & atlases
                ATOProgress.SetStage("Writing textures");
                WriteOutputs();
                ATOProgress.ThrowIfCancelled();

                // ---- 阶段 12：材质更新 / stage 12: update materials
                ATOProgress.SetStage("Updating materials");
                UpdateMaterials();
                ATOProgress.ThrowIfCancelled();

                // ---- 阶段 13：材质去重与槽合并 / stage 13: material dedup & slot merge
                var deduper = new ATODedup(_config.deduplicateMaterials, _scan.animation);
                deduper.DeduplicateMaterials(_scan.renderers, _context, _clonedMaterials);
                deduper.MergeSlots(_scan.renderers, _context, _allIslands);
                ATOProgress.ThrowIfCancelled();

                // ---- 阶段 14：动画重写 / stage 14: rewrite animations
                ATOProgress.SetStage("Rewriting animations");
                var rewrittenClips = ATOAnimationRewriter.Rewrite(
                    _scan.animation.clips, _textureReplacements, deduper.MaterialReplacements, _pathPropAtlases, deduper.SlotRebinds, ResolveAnimPath);
                ATOLog.Info($"Rewrote {rewrittenClips} animation clips. (重写 {rewrittenClips} 个动画)");
                ATOProgress.ThrowIfCancelled();

                // ---- 阶段 15：网格 UV 写入 / stage 15: mesh UV write
                ATOProgress.SetStage("Writing mesh UVs");
                ATOMeshWriter.Write(_context, _groups, _scan.renderers, evacuationMap);
                ATOProgress.ThrowIfCancelled();

                // ---- 阶段 16：图集/贴图去重 / stage 16: atlas/texture dedup
                if (_config.deduplicateTextures) DedupOutputs();

                // ---- 阶段 17：移除自身组件 / stage 17: remove self from the build
                RemoveSelfFromBuild();

                // ---- 阶段 18：报告 / stage 18: report
                _report.Build(_context, _scan, _groups, _writer, _evaluator, this);
            }
            catch (ATOProgress.BuildCancelledException)
            {
                ATOLog.Warning("ATO build cancelled; resources released, temp assets kept on disk. (构建已取消：资源已释放，磁盘临时资产保留)");
                _report?.ReportCancelled(_context);
                throw; // 交给 NDMF 标记构建中止 / let NDMF mark the build as failed
            }
            catch (Exception e)
            {
                ATOLog.Error("ATO build failed: " + e);
                Debug.LogException(e);
                _report?.ReportFailed(_context, e);
                throw;
            }
            finally
            {
                Cleanup();
            }
        }

        // ============================================================
        // 阶段 0：组件验证 / stage 0: component validation
        // ============================================================
        private bool ValidateComponent()
        {
            var components = _context.AvatarRootObject.GetComponentsInChildren<ATOAvatarTextureOptimizer>(true);
            if (components.Length == 0)
            {
                ATOLog.Verbose("No ATO component found; skipping. (未找到 ATO 组件，跳过)");
                return false;
            }
            if (components.Length > 1)
            {
                throw new InvalidOperationException(
                    "More than one ATOAvatarTextureOptimizer component found on the avatar hierarchy. Exactly one is allowed. (Avatar 上存在多个 ATO 组件，只允许一个)");
            }
            _component = components[0];
            if (_component.GetComponent<VRCAvatarDescriptor>() == null)
            {
                throw new InvalidOperationException(
                    "ATOAvatarTextureOptimizer must be placed on the GameObject with the VRCAvatarDescriptor. (ATO 组件必须挂载在存在 VRCAvatarDescriptor 的对象上)");
            }
            return true;
        }

        private static ATOQualityParams BuildQualityParams(ATOConfig config)
        {
            var values = config.GetTierValues(config.qualityTier);
            return new ATOQualityParams
            {
                msSsim = values.msSsim,
                deltaEP95 = values.deltaEP95,
                normalAngleP95 = values.normalAngleP95,
                alphaIoU = values.alphaIoU,
                alphaLinearRmse = values.alphaLinearRmse,
                grayLinearRmse = values.grayLinearRmse,
                lossless = values.IsLossless,
            };
        }

        // ============================================================
        // 阶段 0.5：RW 临时启用 / stage 0.5: temporary Read/Write enabling
        // ============================================================
        private void EnableReadWrite()
        {
            if (!_config.autoEnableReadWrite) return;
            var textures = CollectAllInputTextures();
            foreach (var tex in textures)
            {
                if (tex == null || tex.isReadable) continue;
                var path = AssetDatabase.GetAssetPath(tex);
                if (string.IsNullOrEmpty(path)) continue;
                var importer = AssetImporter.GetAtPath(path) as TextureImporter;
                if (importer == null || importer.isReadable) continue;
                importer.isReadable = true;
                importer.SaveAndReimport();
                _rwToggled[tex] = importer;
                ATOLog.Verbose($"Temporarily enabled Read/Write on '{tex.name}'");
            }
        }

        private List<Texture2D> CollectAllInputTextures()
        {
            var result = new HashSet<Texture2D>();
            foreach (var renderer in _context.AvatarRootObject.GetComponentsInChildren<Renderer>(true))
            {
                if (ATOAvatarScanner.IsEditorOnly(renderer.gameObject)) continue;
                foreach (var m in renderer.sharedMaterials)
                {
                    if (m == null || m.shader == null) continue;
                    var so = new SerializedObject(m);
                    var texEnvs = so.FindProperty("m_SavedProperties.m_TexEnvs");
                    if (texEnvs != null && texEnvs.isArray)
                    {
                        for (int i = 0; i < texEnvs.arraySize; i++)
                        {
                            var tex = texEnvs.GetArrayElementAtIndex(i)
                                .FindPropertyRelative("second.m_Texture")?.objectReferenceValue as Texture2D;
                            if (tex != null) result.Add(tex);
                        }
                    }
                    so.Dispose();
                }
            }
            return result.ToList();
        }

        // ============================================================
        // 阶段 3：岛提取 / stage 3: island extraction
        // ============================================================
        private void ExtractIslands()
        {
            var meshes = new HashSet<Mesh>();
            foreach (var r in _scan.renderers) meshes.Add(r.mesh);
            foreach (var mesh in meshes)
            {
                if (mesh == null) continue;
                var instances = _scan.renderers.Where(r => r.mesh == mesh).ToList();
                for (int channel = 0; channel < 8; channel++)
                {
                    var islands = ATOIslandExtractor.Extract(mesh, channel, instances, out _);
                    _allIslands.AddRange(islands);
                }
            }
            ATOLog.Info($"Extracted {_allIslands.Count} UV islands across {meshes.Count} meshes. (提取 {_allIslands.Count} 个 UV 岛，{meshes.Count} 个网格)");
        }

        // ============================================================
        // 阶段 4：重叠岛合并 / stage 4: overlapping island merge
        // ============================================================
        private static bool ShareTextureReal(ATOIsland a, ATOIsland b)
        {
            foreach (var ra in a.refs)
                foreach (var rb in b.refs)
                    if (ra.texture == rb.texture) return true;
            return false;
        }

        // ============================================================
        // 阶段 5：引用构建 + 越界 / stage 5: ref building + wrap handling
        // ============================================================
        private void BuildRefsAndCheckWraps()
        {
            ATOIslandRefBuilder.BuildRefs(_allIslands, _scan.renderers, _scan.whitelistedTextures);
            foreach (var island in _allIslands)
            {
                foreach (var r in island.refs)
                {
                    if (r.whitelisted && r.whitelistReason != null && r.whitelistReason.Contains("wrap seam"))
                        ATOLog.Warning($"Island on mesh '{island.mesh.name}' channel {island.channel} crosses the wrap seam; texture '{r.texture.name}' treated as whitelisted. (UV 跨 wrap 缝，贴图按白名单跳过)");
                }
            }
            // ref → island 注册表 / ref → island registry
            _refIslands.Clear();
            foreach (var island in _allIslands)
                foreach (var r in island.refs)
                    _refIslands[r] = island;

            // 贴图切换动画的角色一致性检查 / role consistency check for texture-swap animations
            CheckAnimatedSwapConsistency();
            // 动画绑定路径 → 渲染器路径解析（嵌套 Animator 相对路径的保守处理）/ animation binding path → renderer path resolution
            ResolveAnimationPaths();

            // 资产级贴图切换动画 → 整图路径 / asset-level texture swaps → whole-texture path
            foreach (var kv in _scan.animation.animatedTexturesByMaterial)
            {
                foreach (var t in kv.Value)
                {
                    _wholeTextureForced.Add(t);
                    ATOLog.Verbose($"Texture '{t.name}' referenced by asset-level animation; forced to whole-texture path. (资产级动画引用，强制整图路径)");
                }
            }
        }

        /// <summary>检查场景路径贴图切换的角色一致性（冲突 → 白名单 + 警告）。/ Scene-path texture-swap role consistency check (conflict → whitelist + warning).</summary>
        /// <summary>解析动画绑定路径到唯一渲染器路径（不唯一/无法解析的贴图切换 → 整图路径，保证动画正确）。/ Resolve animation binding paths to unique renderer paths; unresolvable swaps → whole-texture path.</summary>
        private void ResolveAnimationPaths()
        {
            _animPathResolution.Clear();
            var rendererPaths = new List<string>();
            foreach (var r in _scan.renderers) rendererPaths.Add(r.path);

            foreach (var kv in _scan.animation.animatedTexturesByPath)
            {
                var bindingPath = kv.Key.path;
                var matches = new List<string>();
                foreach (var rp in rendererPaths)
                {
                    if (rp == bindingPath) matches.Add(rp);
                    else if (rp.EndsWith("/" + bindingPath, StringComparison.Ordinal)) matches.Add(rp);
                }
                if (matches.Count == 1)
                {
                    _animPathResolution[bindingPath] = matches[0];
                }
                else
                {
                    // 无法唯一解析 → 这些贴图强制整图路径（动画曲线保持原贴图引用，通过全局替换重写）/
                    // unresolvable → force whole-texture path for these textures (curve rewritten via the global replacement map)
                    ATOLog.Warning($"Animation binding path '{bindingPath}' ({kv.Key.prop}) cannot be uniquely resolved to a renderer; swapped textures forced to whole-texture path. (动画绑定路径无法唯一解析，切换的贴图强制整图路径)");
                    foreach (var t in kv.Value)
                        _wholeTextureForced.Add(t);
                }
            }
        }

        private readonly Dictionary<string, string> _animPathResolution = new Dictionary<string, string>();

        /// <summary>场景路径动画映射使用解析后的渲染器路径。/ Resolved renderer path for a scene-path animation mapping.</summary>
        public string ResolveAnimPath(string bindingPath) =>
            _animPathResolution.TryGetValue(bindingPath, out var rp) ? rp : bindingPath;

        private void CheckAnimatedSwapConsistency()
        {
            foreach (var kv in _scan.animation.animatedTexturesByPath)
            {
                var path = kv.Key.path;
                var prop = kv.Key.prop;
                var roles = new HashSet<ATORole>();
                foreach (var island in _allIslands)
                {
                    foreach (var r in island.refs)
                    {
                        foreach (var u in r.usages)
                        {
                            var renderer = _scan.renderers.FirstOrDefault(rr => rr.path == path);
                            if (renderer == null) continue;
                            foreach (var slot in renderer.slots)
                                foreach (var m in slot)
                                    if (m == u.material && u.propertyName == prop)
                                        roles.Add(u.role);
                        }
                    }
                }
                if (roles.Count > 1)
                {
                    ATOLog.Warning($"Texture-swap animation on '{path}' property '{prop}' has conflicting roles ({string.Join(",", roles)}); affected textures whitelisted. (贴图切换动画角色冲突，相关贴图白名单)");
                    foreach (var island in _allIslands)
                    {
                        foreach (var r in island.refs)
                        {
                            foreach (var u in r.usages)
                            {
                                if (u.propertyName == prop)
                                {
                                    u.whitelisted = true;
                                    u.whitelistReason = "Animated texture swap with conflicting roles";
                                }
                            }
                        }
                    }
                }
            }
        }

        // ============================================================
        // 阶段 6：AAO UV 兼容 / stage 6: AAO UV compatibility
        // ============================================================
        private Dictionary<Renderer, Dictionary<int, int>> PrepareAAOEvacuation()
        {
            var map = new Dictionary<Renderer, Dictionary<int, int>>();
            if (!ATOAAOCompat.Available) return map;

            foreach (var renderer in _scan.renderers)
            {
                var smr = renderer.renderer as SkinnedMeshRenderer;
                if (smr == null) continue; // AAO API 仅支持 SkinnedMeshRenderer / AAO API supports SkinnedMeshRenderer only
                for (int channel = 0; channel < 8; channel++)
                {
                    // 该通道是否有将被修改的岛 / does this channel have islands to modify?
                    bool willModify = false;
                    foreach (var island in _allIslands)
                    {
                        if (island.mesh != renderer.mesh || island.channel != channel) continue;
                        foreach (var r in island.refs)
                            if (!r.whitelisted) { willModify = true; break; }
                        if (willModify) break;
                    }
                    if (!willModify) continue;
                    if (!ATOAAOCompat.IsTexCoordUsed(smr, channel)) continue;

                    // 找空闲通道保存原 UV / find a free channel to save original UVs
                    var free = -1;
                    for (int c = 0; c < 8; c++)
                    {
                        if (c == channel) continue;
                        var uvs = new List<Vector2>();
                        renderer.mesh.GetUVs(c, uvs);
                        if (uvs.Count == 0) { free = c; break; }
                    }
                    if (free < 0)
                    {
                        // 无空闲通道：该渲染器该通道白名单化 / no free channel: whitelist this renderer's channel
                        ATOLog.Warning($"AAO uses UV{channel} on '{renderer.path}' and no free UV channel exists for evacuation; whitelisting this channel. (无空闲 UV 通道可迁移，该通道白名单)");
                        foreach (var island in _allIslands)
                        {
                            if (island.mesh != renderer.mesh || island.channel != channel) continue;
                            foreach (var r in island.refs)
                            {
                                r.whitelisted = true;
                                r.whitelistReason = "AAO uses this UV channel and no evacuation channel is available";
                            }
                        }
                        continue;
                    }
                    if (!map.TryGetValue(renderer.renderer, out var inner))
                    {
                        inner = new Dictionary<int, int>();
                        map[renderer.renderer] = inner;
                    }
                    inner[channel] = free;
                }
            }
            return map;
        }

        // ============================================================
        // 阶段 7：质量求解 / stage 7: quality solving
        // ============================================================
        private void SolveScales()
        {
            var textures = _scan.textures.Values.ToList();
            int done = 0;
            foreach (var info in textures)
            {
                ATOProgress.ThrowIfCancelled();
                done++;
                ATOProgress.Update((float)done / textures.Count, $"Solving quality: {info.texture.name}");

                if (info.whitelisted) continue;
                foreach (var island in _allIslands)
                {
                    foreach (var r in island.refs)
                    {
                        if (r.texture != info.texture || r.whitelisted) continue;
                        ATOIslandScaler.SolveRef(r, island, _quality, _evaluator, _config);
                    }
                }
                // 每张贴图求解后释放其像素缓存 / release the pixel cache after each texture
                _evaluator.ReleaseTexture(info.texture);
            }

            // 岛级木桶聚合 / island-level barrel aggregation
            foreach (var island in _allIslands)
            {
                island.baseSizeU = 1f;
                island.baseSizeV = 1f;
                foreach (var r in island.refs)
                {
                    if (r.whitelisted) continue;
                    var w = r.nativeWidth * r.solvedScaleU;
                    var h = r.nativeHeight * r.solvedScaleV;
                    if (r.category == ATOScaleCategory.Normal)
                    {
                        island.normalSizeU = Mathf.Max(island.normalSizeU, w);
                        island.normalSizeV = Mathf.Max(island.normalSizeV, h);
                    }
                    else if (r.category == ATOScaleCategory.Mask)
                    {
                        island.maskSizeU = Mathf.Max(island.maskSizeU, w);
                        island.maskSizeV = Mathf.Max(island.maskSizeV, h);
                    }
                    else
                    {
                        island.baseSizeU = Mathf.Max(island.baseSizeU, w);
                        island.baseSizeV = Mathf.Max(island.baseSizeV, h);
                    }
                }
            }
            ATOLog.Info($"Quality solving done: {_evaluator.TotalEvaluations} evaluations. (质量求解完成，共 {_evaluator.TotalEvaluations} 次评估)");
        }

        // ============================================================
        // 阶段 8：类型组 / stage 8: type groups
        // ============================================================
        private void BuildGroups()
        {
            // 参与图集化的贴图：贴图级判定（任一岛与白名单共用 UV 或强制整图 → 整张贴图走整图路径，
            // 否则全部岛必须同图集，否则会破坏该贴图未重排 UV 的部分）/ texture-level decision:
            // if ANY island shares UV with a whitelist (or the texture is forced whole-texture),
            // the whole texture takes the whole-texture path — otherwise atlasing would break its unremapped UVs
            var participating = new HashSet<Texture2D>();
            foreach (var island in _allIslands)
            {
                foreach (var r in island.refs)
                {
                    if (r.whitelisted) continue;
                    if (!TextureCanAtlas(r.texture)) continue;
                    participating.Add(r.texture);
                }
            }

            // 并查集：共享岛 → 同组 / union-find: shared islands → same group
            var parent = new Dictionary<Texture2D, Texture2D>();
            Texture2D Find(Texture2D t)
            {
                if (!parent.TryGetValue(t, out var p) || p == t) return t;
                var root = Find(p);
                parent[t] = root;
                return root;
            }
            void Union(Texture2D a, Texture2D b)
            {
                var ra = Find(a);
                var rb = Find(b);
                if (ra != rb) parent[ra] = rb;
            }
            foreach (var t in participating) parent[t] = t;
            foreach (var island in _allIslands)
            {
                Texture2D prev = null;
                foreach (var r in island.refs)
                {
                    if (r.whitelisted || !participating.Contains(r.texture)) continue;
                    if (prev != null && prev != r.texture) Union(prev, r.texture);
                    prev = r.texture;
                }
            }

            var groupMap = new Dictionary<Texture2D, ATOTypeGroup>();
            int gid = 0;
            foreach (var t in participating)
            {
                var root = Find(t);
                if (!groupMap.TryGetValue(root, out var group))
                {
                    group = new ATOTypeGroup { id = gid++, key = "group" + gid };
                    groupMap[root] = group;
                    _groups.Add(group);
                }
                group.textures.Add(_scan.textures[t]);
            }
            foreach (var group in _groups)
            {
                foreach (var info in group.textures)
                {
                    foreach (var island in _allIslands)
                    {
                        foreach (var r in island.refs)
                        {
                            if (r.texture != info.texture || r.whitelisted) continue;
                            if (r.category == ATOScaleCategory.Normal) group.hasNormal = true;
                            if (r.category == ATOScaleCategory.Mask) group.hasMask = true;
                        }
                    }
                }
            }
            ATOLog.Info($"Built {_groups.Count} type groups. (构建 {_groups.Count} 个类型组)");
        }

        private readonly Dictionary<Texture2D, bool> _textureCanAtlasCache = new Dictionary<Texture2D, bool>();

        /// <summary>贴图级图集化判定（缓存）：全部非白名单岛均无"与白名单共用 UV"才可图集化。/ Texture-level atlas decision (cached): all non-whitelisted islands must be free of whitelist-shared UV.</summary>
        private bool TextureCanAtlas(Texture2D texture)
        {
            if (_textureCanAtlasCache.TryGetValue(texture, out var cached)) return cached;
            if (!_config.generateAtlases || _wholeTextureForced.Contains(texture))
            {
                _textureCanAtlasCache[texture] = false;
                return false;
            }
            bool anyRef = false;
            foreach (var island in _allIslands)
            {
                foreach (var r in island.refs)
                {
                    if (r.texture != texture || r.whitelisted) continue;
                    anyRef = true;
                    if (island.anyWhitelistedRef)
                    {
                        _textureCanAtlasCache[texture] = false;
                        return false;
                    }
                }
            }
            var result = anyRef;
            _textureCanAtlasCache[texture] = result;
            return result;
        }

        /// <summary>该引用是否可以图集化。/ Whether a ref may be atlased.</summary>
        private bool CanAtlas(ATOIslandRef r)
        {
            if (!_config.generateAtlases) return false;
            if (_wholeTextureForced.Contains(r.texture)) return false;
            if (!TextureCanAtlas(r.texture)) return false;
            return true;
        }

        // ============================================================
        // 阶段 9：装箱 / stage 9: packing
        // ============================================================
        private void PackGroups()
        {
            var platform = _config.currentPlatform;
            var platformCfg = _config.ResolvePlatformConfig(platform);
            var packer = new ATOAtlasPacker(platformCfg.atlasMaxSide, platformCfg.experimentalNPOT || _config.experimentalNPOT, (int)_config.minPadding);
            _packers.Add(packer);

            PackIslandRegistry.Build(_refIslands);
            _packFailedTextures.Clear();

            // 构建装箱项 / build pack items
            foreach (var group in _groups)
            {
                var byTexture = new Dictionary<Texture2D, ATOItem>();
                foreach (var island in _allIslands)
                {
                    foreach (var r in island.refs)
                    {
                        if (r.whitelisted || !CanAtlas(r)) continue;
                        if (!_refIslands.TryGetValue(r, out _)) continue;
                        if (!byTexture.TryGetValue(r.texture, out var item))
                        {
                            item = new ATOItem { texture = r.texture, info = _scan.textures[r.texture] };
                            byTexture[r.texture] = item;
                        }
                        item.refs.Add(r);
                    }
                }
                var items = byTexture.Values.ToList();
                var packItems = new List<ATOPackItem>();
                foreach (var item in items)
                {
                    var pi = ATOPackItemBuilder.Build(item);
                    if (pi.areaCells > 0) packItems.Add(pi);
                    else pi.Dispose();
                }
                packItems.Sort((a, b) => b.areaCells.CompareTo(a.areaCells));
                _packItems.AddRange(packItems);
                packer.Pack(group, packItems);
            }
            // 装箱失败的贴图 → 整图路径 / packing-failed textures → whole-texture path
            foreach (var pi in _packItems)
                if (pi.failed) _packFailedTextures.Add(pi.item.texture);
            packer.Dispose();
            // 注：PackIslandRegistry 在 Cleanup 中统一清理（合成阶段仍需使用）/ note: cleared in Cleanup (the compositor still needs it)
        }

        // ============================================================
        // 阶段 10：合成 / stage 10: composition
        // ============================================================
        private readonly Dictionary<ATOBin, List<ATOComposedAtlas>> _composed = new Dictionary<ATOBin, List<ATOComposedAtlas>>();

        private void ComposeAtlases()
        {
            foreach (var group in _groups)
            {
                foreach (var bin in group.bins)
                {
                    var atlases = ATOCompositor.ComposeBin(bin, _evaluator, _evaluator.Gpu);
                    _composed[bin] = atlases;
                }
            }
        }

        // ============================================================
        // 阶段 11：写入 / stage 11: writing
        // ============================================================
        private void WriteOutputs()
        {
            _writer = new ATOTextureWriter(_context, _config);
            var platform = _config.currentPlatform;
            var platformCfg = _config.ResolvePlatformConfig(platform);

            // 图集 / atlases
            int atlasCount = 0;
            foreach (var group in _groups)
            {
                foreach (var bin in group.bins)
                {
                    if (!_composed.TryGetValue(bin, out var atlases)) continue;
                    // 基础图集 sRGB / filterMode / 透明度 / base atlas sRGB & filterMode & alpha
                    var baseAtlas = atlases.FirstOrDefault(a => a.role == ATORole.Main);
                    var normalAtlas = atlases.FirstOrDefault(a => a.role == ATORole.Normal);
                    var maskAtlas = atlases.FirstOrDefault(a => a.role == ATORole.Mask);

                    var hasAlpha = baseAtlas != null && ContainsAlpha(baseAtlas);
                    var baseFilter = bin.filterMode != 0 ? bin.filterMode : MaxFilterMode(group);
                    var normalFilter = MaxRoleFilterMode(group, ATOScaleCategory.Normal);
                    var maskFilter = MaxRoleFilterMode(group, ATOScaleCategory.Mask);

                    var atlasName = $"Atlas_{group.id}_{bin.width}x{bin.height}_{atlasCount}";
                    var baseOutput = _writer.WriteAtlas(baseAtlas, atlasName, bin.isSRGB, baseFilter, hasAlpha, platform, platformCfg);
                    ATOTextureOutput normalOutput = null, maskOutput = null;
                    if (normalAtlas != null)
                        normalOutput = _writer.WriteAtlas(normalAtlas, atlasName, false, normalFilter, false, platform, platformCfg);
                    if (maskAtlas != null)
                        maskOutput = _writer.WriteAtlas(maskAtlas, atlasName, false, maskFilter, false, platform, platformCfg);

                    bin.atlases[ATORole.Main] = baseOutput?.output;
                    bin.atlases[ATORole.Normal] = normalOutput?.output;
                    bin.atlases[ATORole.Mask] = maskOutput?.output;
                    bin.hasAlpha = hasAlpha;
                    atlasCount++;

                    // 每张贴图 → 其箱的对应角色图集 / texture → its bin's per-role atlas
                    foreach (var item in bin.items)
                    {
                        foreach (var r in item.refs)
                        {
                            if (r.category == ATOScaleCategory.Normal)
                                RegisterUsageAtlases(r, bin.atlases[ATORole.Normal]);
                            else if (r.category == ATOScaleCategory.Mask)
                                RegisterUsageAtlases(r, bin.atlases[ATORole.Mask]);
                            else
                                RegisterUsageAtlases(r, bin.atlases[ATORole.Main]);
                        }
                    }
                }
            }

            // 整图路径 / whole-texture path
            WriteWholeTextures(platform, platformCfg);

            // 释放合成缓冲 / release composed buffers
            foreach (var atlases in _composed.Values)
                foreach (var a in atlases)
                    a.Dispose();
            _composed.Clear();
        }

        /// <summary>注册用途 → 图集映射（材质属性与场景路径动画共用）。/ Register usage → atlas mapping (material props & scene-path animations).</summary>
        private void RegisterUsageAtlases(ATOIslandRef r, Texture2D atlas)
        {
            if (atlas == null) return;
            foreach (var u in r.usages)
                _usageAtlases[(u.material, u.propertyName)] = atlas;

            // 场景路径贴图切换映射 / scene-path texture-swap mappings
            foreach (var kv in _scan.animation.animatedTexturesByPath)
            {
                var matched = false;
                foreach (var u in r.usages)
                    if (u.propertyName == kv.Key.prop) { matched = true; break; }
                if (!matched) continue;
                if (!_pathPropAtlases.TryGetValue(kv.Key, out var texMap))
                {
                    texMap = new Dictionary<Texture2D, Texture2D>();
                    _pathPropAtlases[kv.Key] = texMap;
                }
                texMap[r.texture] = atlas;
            }
        }

        private static bool ContainsAlpha(ATOComposedAtlas atlas)
        {
            for (int i = 0; i < atlas.pixels.Length; i++)
                if (atlas.pixels[i].w < 1f - 1e-6f) return true;
            return false;
        }

        private static FilterMode MaxFilterMode(ATOTypeGroup group)
        {
            var max = FilterMode.Point;
            foreach (var info in group.textures)
                if (info.filterMode > max) max = info.filterMode;
            return max;
        }

        private static FilterMode MaxRoleFilterMode(ATOTypeGroup group, ATOScaleCategory category)
        {
            var max = FilterMode.Point;
            foreach (var info in group.textures)
            {
                bool used = false;
                foreach (var u in info.usages)
                {
                    if (u.Category == category) { used = true; break; }
                }
                if (used && info.filterMode > max) max = info.filterMode;
            }
            return max == FilterMode.Point ? FilterMode.Bilinear : max;
        }

        private void WriteWholeTextures(ATOPlatform platform, ATOPlatformConfig platformCfg)
        {
            foreach (var info in _scan.textures.Values)
            {
                if (info.whitelisted) continue;

                // 是否需要整图路径 / whole-texture path needed?
                bool needsWhole = !_config.generateAtlases || _wholeTextureForced.Contains(info.texture) ||
                                  _packFailedTextures.Contains(info.texture);
                if (!needsWhole)
                {
                    // 已图集化？/ atlased already?
                    bool atlased = false;
                    foreach (var group in _groups)
                    {
                        foreach (var bin in group.bins)
                        {
                            foreach (var item in bin.items)
                            {
                                if (item.texture == info.texture) { atlased = true; break; }
                            }
                            if (atlased) break;
                        }
                        if (atlased) break;
                    }
                    if (atlased) continue;
                    needsWhole = true; // 未图集化且有非白名单引用 → 整图路径 / not atlased but has non-whitelisted refs → whole-texture path
                }

                // 整图缩放 = 全部岛引用的最小缩放 / whole scale = min across all island refs
                float minU = 1f, minV = 1f;
                bool hasRef = false;
                foreach (var island in _allIslands)
                {
                    foreach (var r in island.refs)
                    {
                        if (r.texture != info.texture || r.whitelisted) continue;
                        minU = Mathf.Min(minU, r.solvedScaleU);
                        minV = Mathf.Min(minV, r.solvedScaleV);
                        hasRef = true;
                    }
                }
                if (!hasRef) continue;

                var w = Mathf.Max(4, Mathf.RoundToInt(info.width * minU));
                var h = Mathf.Max(4, Mathf.RoundToInt(info.height * minV));
                var outW = NextPotClamped(w);
                var outH = NextPotClamped(h);

                // 整图读取：透明贴图预乘（线性）、法线贴图解码（DXT5nm AG → 单位法线 xyz）/ whole-texture read:
                // premultiplied for transparent (linear), decoded normals for normal maps
                var category = ResolveTextureCategory(info);
                bool premultiply = false;
                foreach (var u in info.usages)
                    if ((u.alphaUsage & ATOAlphaUsage.Blend) != 0) premultiply = true;
                var normalEnc = category == ATOTextureCategory.Normal
                    ? ATOIslandCrop.NormalEncoding.DXT5nm : ATOIslandCrop.NormalEncoding.None;
                var src = ReadFullTexture(info.texture, premultiply, normalEnc);
                var resized = ATOMetrics.Resize(src, info.width, info.height, outW, outH,
                    Unity.Collections.Allocator.Persistent);
                src.Dispose();

                // 法线重归一化（重采样后）/ renormalize normals after resampling
                if (category == ATOTextureCategory.Normal)
                {
                    for (int i = 0; i < resized.Length; i++)
                    {
                        var n = Unity.Mathematics.math.normalize(resized[i].xyz);
                        resized[i] = new Unity.Mathematics.float4(n.x, n.y, n.z, 1f);
                    }
                }
                // 预乘还原 / unpremultiply
                if (premultiply)
                {
                    for (int i = 0; i < resized.Length; i++)
                    {
                        var f = resized[i];
                        if (f.w > 1e-6f) f = new Unity.Mathematics.float4(f.x / f.w, f.y / f.w, f.z / f.w, f.w);
                        resized[i] = f;
                    }
                }

                var output = _writer.WriteTexture(resized, outW, outH, "ATO_" + info.texture.name,
                    info.isSRGB, info.filterMode, category, platform, platformCfg, info.texture);
                resized.Dispose();
                _textureReplacements[info.texture] = output.output;
                ATOLog.Verbose($"Whole-texture path: '{info.texture.name}' {info.width}x{info.height} → {outW}x{outH}");
            }
        }

        private static int NextPotClamped(int v)
        {
            var pot = Mathf.NextPowerOfTwo(v);
            return Mathf.Clamp(pot, 4, 8192);
        }

        private static ATOTextureCategory ResolveTextureCategory(ATOTextureInfo info)
        {
            bool normal = false, mask = false;
            foreach (var u in info.usages)
            {
                if (u.role == ATORole.Normal) normal = true;
                if (u.role == ATORole.Mask) mask = true;
            }
            if (normal) return ATOTextureCategory.Normal;
            if (mask) return ATOTextureCategory.Grayscale;
            foreach (var u in info.usages)
                if (u.alphaUsage != ATOAlphaUsage.Opaque) return ATOTextureCategory.Transparent;
            return ATOTextureCategory.Opaque;
        }

        private Unity.Collections.NativeArray<Unity.Mathematics.float4> ReadFullTexture(Texture2D tex,
            bool premultiply, ATOIslandCrop.NormalEncoding normalEnc)
        {
            var colors = tex.GetPixels32(0);
            var arr = new Unity.Collections.NativeArray<Unity.Mathematics.float4>(colors.Length, Unity.Collections.Allocator.Persistent);
            for (int i = 0; i < colors.Length; i++)
            {
                var c = colors[i];
                var f = new Unity.Mathematics.float4(c.r, c.g, c.b, c.a) * (1f / 255f);
                if (GetIsSrgbSafe(tex)) f.xyz = ATOMetrics.SrgbToLinear(f.xyz);
                if (normalEnc == ATOIslandCrop.NormalEncoding.DXT5nm)
                {
                    var nx = f.w * 2f - 1f;
                    var ny = f.y * 2f - 1f;
                    var nz2 = 1f - nx * nx - ny * ny;
                    f = new Unity.Mathematics.float4(nx, ny, nz2 > 0f ? Unity.Mathematics.math.sqrt(nz2) : 0f, 1f);
                }
                else if (premultiply)
                {
                    f.xyz *= f.w;
                }
                arr[i] = f;
            }
            return arr;
        }

        private static bool GetIsSrgbSafe(Texture2D t)
        {
            try { return ATOAvatarScanner.GetIsSRGB(t); }
            catch (Exception) { return true; }
        }

        // ============================================================
        // 阶段 12：材质更新 / stage 12: material updates
        // ============================================================
        private void UpdateMaterials()
        {
            var cloned = new Dictionary<Material, Material>();
            foreach (var kv in _usageAtlases)
            {
                var material = kv.Key.Item1;
                var prop = kv.Key.Item2;
                var atlas = kv.Value;
                var clone = CloneMaterial(material, cloned);
                clone.SetTexture(prop, atlas);
            }
            // 整图路径替换也写回材质 / whole-texture replacements also written into materials
            foreach (var kv in _textureReplacements)
            {
                foreach (var u in _scan.textures.TryGetValue(kv.Key, out var info) ? info.usages : new List<ATOTextureUsage>())
                {
                    var clone = CloneMaterial(u.material, cloned);
                    clone.SetTexture(u.propertyName, kv.Value);
                }
            }
            // 兜底：显式把渲染器材质槽指向克隆（NDMF 序列化重映射之外的保险）/ belt-and-braces:
            // explicitly point renderer slots at the clones (in addition to NDMF's serialization remapping)
            foreach (var renderer in _scan.renderers)
            {
                var shared = renderer.renderer.sharedMaterials;
                var changed = false;
                for (int i = 0; i < shared.Length; i++)
                {
                    if (shared[i] != null && cloned.TryGetValue(shared[i], out var rep))
                    {
                        shared[i] = rep;
                        changed = true;
                    }
                }
                if (changed) renderer.renderer.sharedMaterials = shared;
            }
            _clonedMaterials = cloned;
        }

        private Dictionary<Material, Material> _clonedMaterials;

        private Material CloneMaterial(Material material, Dictionary<Material, Material> cache)
        {
            if (cache.TryGetValue(material, out var clone)) return clone;
            clone = Object.Instantiate(material);
            clone.name = material.name;
            nadena.dev.ndmf.ObjectRegistry.RegisterReplacedObject(material, clone);
            cache[material] = clone;
            return clone;
        }

        // ============================================================
        // 阶段 16：图集/贴图去重（优化后）/ stage 16: atlas/texture dedup (post-optimization)
        // ============================================================
        private void DedupOutputs()
        {
            if (_writer == null || _writer.Outputs.Count < 2) return;
            var outputs = _writer.Outputs;
            var merged = 0;
            for (int i = 0; i < outputs.Count; i++)
            {
                if (outputs[i].output == null) continue;
                for (int j = i + 1; j < outputs.Count; j++)
                {
                    if (outputs[j].output == null) continue;
                    if (!OutputsEqual(outputs[i], outputs[j])) continue;
                    // 合并 j → i / merge j → i
                    var old = outputs[j].output;
                    var rep = outputs[i].output;
                    _textureReplacements[old] = rep;
                    foreach (var kv in _usageAtlases.ToList())
                        if (kv.Value == old) _usageAtlases[kv.Key] = rep;
                    foreach (var kv in _pathPropAtlases.ToList())
                    {
                        var texMap = kv.Value;
                        var keys = texMap.Where(p => p.Value == old).Select(p => p.Key).ToList();
                        foreach (var k in keys) texMap[k] = rep;
                    }
                    outputs[j].output = null;
                    merged++;
                    ATOLog.Verbose($"Dedup output '{old.name}' → '{rep.name}'");
                }
            }
            if (merged > 0)
                ATOLog.Info($"Output texture dedup: {merged} textures merged. (输出贴图去重：合并 {merged} 张)");
        }

        private static bool OutputsEqual(ATOTextureOutput a, ATOTextureOutput b)
        {
            if (a.width != b.width || a.height != b.height || a.category != b.category) return false;
            if (a.streaming != b.streaming) return false;
            var pa = a.output.GetPixels32(0);
            var pb = b.output.GetPixels32(0);
            if (pa.Length != pb.Length) return false;
            for (int i = 0; i < pa.Length; i++)
            {
                var x = pa[i];
                var y = pb[i];
                if (x.r != y.r || x.g != y.g || x.b != y.b || x.a != y.a) return false;
            }
            return true;
        }

        // ============================================================
        // 阶段 17：移除自身 / stage 17: self removal
        // ============================================================
        private void RemoveSelfFromBuild()
        {
            foreach (var c in _context.AvatarRootObject.GetComponentsInChildren<ATOAvatarTextureOptimizer>(true))
                Object.DestroyImmediate(c);
            // 白名单标记组件也不随成品带走 / whitelist markers don't ship with the build either
            foreach (var wl in _context.AvatarRootObject.GetComponentsInChildren<ATOWhitelist>(true))
                Object.DestroyImmediate(wl);
        }

        // ============================================================
        // 清理 / cleanup
        // ============================================================
        private void Cleanup()
        {
            // 释放装箱项掩码 / release pack item masks
            foreach (var pi in _packItems) pi.Dispose();
            _packItems.Clear();
            foreach (var packer in _packers) packer.Dispose();
            _packers.Clear();
            foreach (var group in _groups)
                foreach (var bin in group.bins)
                    bin.occupancy?.Dispose();
            foreach (var atlases in _composed.Values)
                foreach (var a in atlases)
                    a.Dispose();
            _composed.Clear();
            _evaluator?.Dispose();
            PackIslandRegistry.Clear();

            // 恢复输入贴图的 Read/Write / restore input Read/Write
            foreach (var kv in _rwToggled)
            {
                try
                {
                    var importer = kv.Value;
                    if (importer != null)
                    {
                        importer.isReadable = false;
                        importer.SaveAndReimport();
                    }
                }
                catch (Exception e)
                {
                    ATOLog.Warning($"Failed to restore Read/Write on '{kv.Key.name}': {e.Message}");
                }
            }
            _rwToggled.Clear();

            ATOProgress.Clear();
        }
    }
}
