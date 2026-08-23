// SPDX-License-Identifier: MIT
// EN: Stage 4 - write the results back onto the avatar: new meshes, new materials, updated animations.
// ZH: 阶段 4 —— 将结果写回 Avatar：新网格、新材质、更新后的动画。

using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;
using Net.Fosa.AvatarTextureOptimizer.Editor.Core;
using Net.Fosa.AvatarTextureOptimizer.Editor.Interop;
using Net.Fosa.AvatarTextureOptimizer.Editor.Meshes;
using Net.Fosa.AvatarTextureOptimizer.Editor.Model;
using UnityEngine;
using UnityObject = UnityEngine.Object;

namespace Net.Fosa.AvatarTextureOptimizer.Editor.Apply
{
    /// <summary>
    /// EN: Applies the atlas stage result. Only mesh UVs and texture references are touched; no other
    ///     shader parameter is ever written, which is the strongest safety guarantee ATO offers.
    /// ZH: 应用图集阶段的结果。只改动网格 UV 与贴图引用；绝不写入任何其他着色器参数，
    ///     这是 ATO 提供的最强安全保证。
    /// </summary>
    public sealed class AtoApplier
    {
        private const string Stage = "Apply";

        private readonly BuildContext _ctx;
        private readonly Dictionary<Mesh, Mesh> _meshClones = new Dictionary<Mesh, Mesh>();
        private readonly Dictionary<Material, Material> _materialClones = new Dictionary<Material, Material>();

        /// <summary>EN: Number of meshes rewritten. ZH: 被重写的网格数量。</summary>
        public int RewrittenMeshes => _meshClones.Count;
        /// <summary>EN: Number of materials cloned. ZH: 被克隆的材质数量。</summary>
        public int RewrittenMaterials => _materialClones.Count;

        /// <summary>EN: Creates the applier. ZH: 创建应用器。</summary>
        public AtoApplier(BuildContext ctx) => _ctx = ctx;

        /// <summary>
        /// EN: Rewrites mesh UVs for every plan whose group actually got an atlas.
        /// ZH: 为每个真正获得图集的组的计划重写网格 UV。
        /// </summary>
        public void RewriteMeshes(Plugin.AtlasStageResult atlas, IReadOnlyList<Renderer> renderers, AtoProgress progress)
        {
            // EN: Group the plans by mesh so a mesh is cloned once even with many sub meshes.
            // ZH: 按网格对计划分组，使多子网格的网格也只被克隆一次。
            var byMesh = atlas.Plans.Values
                .Where(p => p.Group.IsOptimizable && atlas.AtlasSizeOf.ContainsKey(p.Group))
                .GroupBy(p => p.Slot.Mesh);

            foreach (var meshGroup in byMesh)
            {
                var original = meshGroup.Key;
                if (original == null) continue;

                var clone = GetMeshClone(original);
                var channels = meshGroup.Select(p => p.Slot.Channel).Distinct().ToList();

                foreach (var channel in channels)
                {
                    var uvs = MeshGeometry.GetUv(clone, channel);
                    if (uvs == null) continue;

                    // EN: Vertices shared between islands must be split, otherwise one vertex would need
                    //     two different atlas coordinates. Splitting is done by duplicating vertices.
                    // ZH: 被多个岛共享的顶点必须拆分，否则同一个顶点会需要两套不同的图集坐标。
                    //     拆分通过复制顶点实现。
                    var rewriter = new MeshUvRewriter(clone, channel);
                    foreach (var plan in meshGroup.Where(p => p.Slot.Channel == channel))
                    {
                        var atlasSize = atlas.AtlasSizeOf[plan.Group];
                        rewriter.AddPlan(plan, atlasSize);
                    }
                    rewriter.Commit();
                }

                // EN: Point every renderer that used the original mesh at the rewritten copy, and tell
                //     Avatar Optimizer where the original UVs went so its UV based features keep working.
                // ZH: 将所有使用原网格的渲染器指向重写后的副本，并告知 Avatar Optimizer 原始 UV 的去向，
                //     使其基于 UV 的功能继续有效。
                foreach (var r in renderers)
                {
                    if (r is SkinnedMeshRenderer smr && smr.sharedMesh == original)
                    {
                        smr.sharedMesh = clone;
                        EvacuateForAao(smr, clone, channels);
                    }
                    else if (r is MeshRenderer && r.TryGetComponent<MeshFilter>(out var mf) && mf.sharedMesh == original)
                    {
                        mf.sharedMesh = clone;
                    }
                }

                progress?.Step(0f);
            }

            AtoLog.Info(Stage, $"rewrote UVs on {_meshClones.Count} meshes");
        }

        private void EvacuateForAao(SkinnedMeshRenderer smr, Mesh mesh, List<int> channels)
        {
            if (!AaoInterop.Available) return;
            foreach (var channel in channels)
            {
                if (!AaoInterop.IsTexCoordUsed(smr, channel)) continue;
                int free = AaoInterop.FindFreeChannel(smr, mesh);
                if (free < 0)
                {
                    AtoReporting.Warn(Stage, "ATO:warn:noFreeUvChannel", smr, smr.name, channel.ToString());
                    continue;
                }
                var original = MeshGeometry.GetUv(mesh, channel);
                if (original == null) continue;
                mesh.SetUVs(free, original);
                if (!AaoInterop.RegisterEvacuation(smr, channel, free))
                    AtoReporting.Warn(Stage, "ATO:warn:aaoEvacuationFailed", smr, smr.name);
            }
        }

        private Mesh GetMeshClone(Mesh original)
        {
            if (_meshClones.TryGetValue(original, out var clone)) return clone;
            clone = UnityObject.Instantiate(original);
            clone.name = original.name + " (ATO)";
            _ctx.AssetSaver.SaveAsset(clone);
            ObjectRegistry.RegisterReplacedObject(original, clone);
            _meshClones[original] = clone;
            return clone;
        }

        /// <summary>
        /// EN: Replaces texture references on every material that used an optimized texture, and rewrites
        ///     animation object curves that referenced the old materials.
        /// ZH: 替换所有使用了被优化贴图的材质上的贴图引用，并重写引用旧材质的动画对象曲线。
        /// </summary>
        public void RewriteMaterials(AtoCollectionView collection, Plugin.AtlasStageResult atlas, AtoProgress progress)
        {
            var textureMap = new Dictionary<Texture, Texture>();
            foreach (var kv in atlas.ReplacementTexture)
                textureMap[kv.Key.Texture] = kv.Value;
            foreach (var kv in collection.WholeTextureReplacements)
                textureMap[kv.Key] = kv.Value;

            if (textureMap.Count == 0)
            {
                AtoLog.Info(Stage, "no textures were replaced; materials are left untouched.");
                return;
            }

            // EN: Which materials need a clone.
            // ZH: 哪些材质需要克隆。
            var affected = new HashSet<Material>();
            foreach (var entry in collection.AllEntries)
                foreach (var usage in entry.Usages)
                    if (usage.Material != null && textureMap.ContainsKey(entry.Texture))
                        affected.Add(usage.Material);

            foreach (var material in affected)
            {
                var clone = GetMaterialClone(material);
                foreach (var prop in Analysis.ShaderAnalysisUtil.GetTextureProperties(clone.shader))
                {
                    var current = clone.GetTexture(prop);
                    if (current != null && textureMap.TryGetValue(current, out var replacement))
                        clone.SetTexture(prop, replacement);
                }
            }

            // EN: Update renderer material slots.
            // ZH: 更新渲染器的材质槽。
            foreach (var renderer in collection.Renderers)
            {
                var mats = renderer.sharedMaterials;
                bool changed = false;
                for (int i = 0; i < mats.Length; i++)
                {
                    if (mats[i] != null && _materialClones.TryGetValue(mats[i], out var clone))
                    {
                        mats[i] = clone;
                        changed = true;
                    }
                }
                if (changed) renderer.sharedMaterials = mats;
            }

            RewriteAnimations();
            AtoLog.Info(Stage, $"replaced {textureMap.Count} textures across {_materialClones.Count} materials");
            progress?.Step(1f);
        }

        private Material GetMaterialClone(Material original)
        {
            if (_materialClones.TryGetValue(original, out var clone)) return clone;
            clone = UnityObject.Instantiate(original);
            clone.name = original.name + " (ATO)";
            _ctx.AssetSaver.SaveAsset(clone);
            ObjectRegistry.RegisterReplacedObject(original, clone);
            _materialClones[original] = clone;
            return clone;
        }

        /// <summary>
        /// EN: Rewrites both material swap curves and direct texture curves so animated states keep
        ///     pointing at the optimized assets.
        /// ZH: 同时重写材质切换曲线与直接的贴图曲线，使被动画驱动的状态仍指向优化后的资产。
        /// </summary>
        private void RewriteAnimations()
        {
            if (_materialClones.Count == 0) return;
            try
            {
                var asc = _ctx.Extension<AnimatorServicesContext>();
                asc.AnimationIndex.RewriteObjectCurves(obj =>
                {
                    if (obj is Material m && _materialClones.TryGetValue(m, out var clone)) return clone;
                    return obj;
                });
                AtoLog.Debug_(Stage, "animation object curves rewritten to the optimized materials");
            }
            catch (Exception e)
            {
                AtoLog.Warning(Stage, $"could not rewrite animation curves: {e.Message}");
            }
        }
    }

    /// <summary>
    /// EN: The subset of the collection the applier needs, kept as an interface-like view so the applier
    ///     can be reused by extensions.
    /// ZH: 应用器所需的集合子集，以类接口的视图形式保留，便于扩展复用应用器。
    /// </summary>
    public sealed class AtoCollectionView
    {
        /// <summary>EN: All texture entries. ZH: 全部贴图条目。</summary>
        public IReadOnlyCollection<TextureEntry> AllEntries;
        /// <summary>EN: Renderers to update. ZH: 需要更新的渲染器。</summary>
        public IReadOnlyList<Renderer> Renderers;
        /// <summary>EN: Replacements produced by the non atlas whole texture scaling path. ZH: 非图集的整图缩放路径产生的替换。</summary>
        public IReadOnlyDictionary<Texture, Texture> WholeTextureReplacements;
    }
}
