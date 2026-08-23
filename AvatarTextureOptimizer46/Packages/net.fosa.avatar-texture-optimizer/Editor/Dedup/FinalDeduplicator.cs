// SPDX-License-Identifier: MIT
// EN: Stage 5 - post optimization deduplication of textures and materials, plus material slot merging.
// ZH: 阶段 5 —— 优化后的贴图与材质去重，以及材质槽合并。

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;
using Net.Fosa.AvatarTextureOptimizer.Editor.Analysis;
using Net.Fosa.AvatarTextureOptimizer.Editor.Core;
using Net.Fosa.AvatarTextureOptimizer.Editor.Textures;
using UnityEngine;
using UnityObject = UnityEngine.Object;

namespace Net.Fosa.AvatarTextureOptimizer.Editor.Dedup
{
    /// <summary>
    /// EN: Runs after everything else has been applied. Optimization frequently makes assets that were
    ///     different before become byte identical - for example two materials that only differed by the
    ///     texture they pointed at, when both textures ended up in the same atlas. This stage collapses
    ///     them and repairs every reference, including animation curves and material slot indices.
    /// ZH: 在其他一切都应用完毕后运行。优化经常会让原本不同的资产变得逐字节相同——
    ///     例如两个仅仅贴图不同的材质，在两张贴图进入同一图集后就完全一致了。
    ///     本阶段将它们合并，并修复所有引用，包括动画曲线与材质槽索引。
    /// </summary>
    public sealed class FinalDeduplicator
    {
        private const string Stage = "FinalDedupe";

        private static readonly Regex MaterialSlotBinding =
            new Regex(@"^m_Materials\.Array\.data\[(?<idx>\d+)\]$", RegexOptions.Compiled);

        private readonly BuildContext _ctx;
        private readonly bool _dedupeTextures;
        private readonly bool _dedupeMaterials;

        /// <summary>EN: How many textures were eliminated. ZH: 被消除的贴图数量。</summary>
        public int TexturesRemoved { get; private set; }
        /// <summary>EN: How many materials were eliminated. ZH: 被消除的材质数量。</summary>
        public int MaterialsRemoved { get; private set; }
        /// <summary>EN: How many material slots were merged away. ZH: 被合并掉的材质槽数量。</summary>
        public int SlotsMerged { get; private set; }

        /// <summary>EN: Creates the stage. ZH: 创建该阶段。</summary>
        public FinalDeduplicator(BuildContext ctx, bool dedupeTextures, bool dedupeMaterials)
        {
            _ctx = ctx;
            _dedupeTextures = dedupeTextures;
            _dedupeMaterials = dedupeMaterials;
        }

        /// <summary>
        /// EN: Runs the whole stage.
        /// ZH: 执行整个阶段。
        /// </summary>
        /// <param name="renderers">EN: Renderers to repair. ZH: 需要修复的渲染器。</param>
        /// <param name="animatedSlots">EN: Renderer path to the set of material slot indices an animation can swap. ZH: 渲染器路径 -&gt; 动画可能切换的材质槽索引集合。</param>
        /// <param name="progress">EN: Progress reporter. ZH: 进度报告器。</param>
        public void Run(IReadOnlyList<Renderer> renderers, IReadOnlyDictionary<string, HashSet<int>> animatedSlots, AtoProgress progress)
        {
            var textureMap = _dedupeTextures ? DeduplicateTextures(renderers) : new Dictionary<Texture, Texture>();
            progress?.Step(0.33f);

            var materialMap = _dedupeMaterials
                ? DeduplicateMaterials(renderers, textureMap)
                : new Dictionary<Material, Material>();
            progress?.Step(0.66f);

            if (materialMap.Count > 0) RewriteAnimationMaterials(materialMap);
            if (_dedupeMaterials) MergeMaterialSlots(renderers, animatedSlots);

            AtoLog.Info(Stage,
                $"removed {TexturesRemoved} duplicate textures, {MaterialsRemoved} duplicate materials, merged {SlotsMerged} material slots");
            progress?.Step(1f);
        }

        #region Textures

        private Dictionary<Texture, Texture> DeduplicateTextures(IReadOnlyList<Renderer> renderers)
        {
            var map = new Dictionary<Texture, Texture>();
            var buckets = new Dictionary<string, List<Texture2D>>(StringComparer.Ordinal);
            var seen = new HashSet<Texture2D>();

            foreach (var material in EnumerateMaterials(renderers))
            {
                if (material == null || material.shader == null) continue;
                foreach (var prop in ShaderAnalysisUtil.GetTextureProperties(material.shader))
                {
                    if (!(material.GetTexture(prop) is Texture2D tex)) continue;
                    // EN: Only assets ATO itself produced may be collapsed; user assets are untouchable.
                    // ZH: 只有 ATO 自己产出的资产才可以被合并；用户的资产不可动。
                    if (!_ctx.AssetSaver.IsTemporaryAsset(tex)) continue;
                    if (!seen.Add(tex)) continue;

                    string key;
                    try
                    {
                        key = GeneratedTextureKey(tex);
                    }
                    catch (Exception e)
                    {
                        AtoLog.Warning(Stage, $"hashing generated texture '{tex.name}' failed ({e.Message}); it will not be deduplicated.");
                        continue;
                    }

                    if (!buckets.TryGetValue(key, out var list)) buckets[key] = list = new List<Texture2D>();
                    list.Add(tex);
                }
            }

            foreach (var kv in buckets)
            {
                var list = kv.Value;
                if (list.Count < 2) continue;
                list.Sort((a, b) => string.CompareOrdinal(a.name, b.name) != 0
                    ? string.CompareOrdinal(a.name, b.name)
                    : a.GetInstanceID().CompareTo(b.GetInstanceID()));
                var canonical = list[0];
                for (int i = 1; i < list.Count; i++)
                {
                    map[list[i]] = canonical;
                    ObjectRegistry.RegisterReplacedObject(list[i], canonical);
                    TexturesRemoved++;
                }
                AtoLog.Debug_(Stage, $"collapsed {list.Count} identical generated textures into '{canonical.name}'");
            }

            if (map.Count == 0) return map;

            foreach (var material in EnumerateMaterials(renderers))
            {
                if (material == null || material.shader == null) continue;
                if (!_ctx.AssetSaver.IsTemporaryAsset(material)) continue;
                foreach (var prop in ShaderAnalysisUtil.GetTextureProperties(material.shader))
                {
                    var current = material.GetTexture(prop);
                    if (current != null && map.TryGetValue(current, out var canonical))
                        material.SetTexture(prop, canonical);
                }
            }

            return map;
        }

        /// <summary>
        /// EN: Key for a generated texture: dimensions, format, sampler state and the decoded pixels.
        /// ZH: 生成贴图的键：尺寸、格式、采样状态与解码后的像素。
        /// </summary>
        private static string GeneratedTextureKey(Texture2D tex)
        {
            var settings = $"{tex.width}x{tex.height}|{tex.format}|{tex.filterMode}|{tex.wrapMode}|{tex.anisoLevel}|{tex.mipmapCount}";
            var rt = GpuTextureUtil.ToLinearRT(tex);
            LinearImage img = null;
            try
            {
                img = GpuTextureUtil.Readback(rt, new RectInt(0, 0, rt.width, rt.height));
                unchecked
                {
                    // EN: FNV-1a over the raw float pixels. A 64 bit hash plus the settings string is
                    //     enough here because a false positive would need a deliberate collision.
                    // ZH: 对原始浮点像素做 FNV-1a。64 位哈希加上设置串已经足够，
                    //     因为要出现误判需要刻意构造碰撞。
                    ulong hash = 14695981039346656037UL;
                    for (int i = 0; i < img.Pixels.Length; i++)
                    {
                        var c = img.Pixels[i];
                        hash = Mix(hash, c.r);
                        hash = Mix(hash, c.g);
                        hash = Mix(hash, c.b);
                        hash = Mix(hash, c.a);
                    }
                    return settings + "|" + hash.ToString("X16");
                }
            }
            finally
            {
                img?.Dispose();
                GpuTextureUtil.Release(rt);
            }
        }

        private static ulong Mix(ulong hash, float value)
        {
            unchecked
            {
                uint bits = (uint)BitConverter.SingleToInt32Bits(value);
                for (int b = 0; b < 4; b++)
                {
                    hash ^= (byte)(bits >> (b * 8));
                    hash *= 1099511628211UL;
                }
                return hash;
            }
        }

        #endregion

        #region Materials

        private Dictionary<Material, Material> DeduplicateMaterials(IReadOnlyList<Renderer> renderers,
            Dictionary<Texture, Texture> textureMap)
        {
            Texture Canonical(Texture t) => t != null && textureMap.TryGetValue(t, out var c) ? c : t;

            var buckets = new Dictionary<string, List<Material>>(StringComparer.Ordinal);
            var seen = new HashSet<Material>();
            foreach (var material in EnumerateMaterials(renderers))
            {
                if (material == null || !seen.Add(material)) continue;
                if (!_ctx.AssetSaver.IsTemporaryAsset(material)) continue;
                var key = MaterialSignature.Compute(material, Canonical);
                if (!buckets.TryGetValue(key, out var list)) buckets[key] = list = new List<Material>();
                list.Add(material);
            }

            var map = new Dictionary<Material, Material>();
            foreach (var kv in buckets)
            {
                var list = kv.Value;
                if (list.Count < 2) continue;
                list.Sort((a, b) => string.CompareOrdinal(a.name, b.name) != 0
                    ? string.CompareOrdinal(a.name, b.name)
                    : a.GetInstanceID().CompareTo(b.GetInstanceID()));
                var canonical = list[0];
                for (int i = 1; i < list.Count; i++)
                {
                    map[list[i]] = canonical;
                    ObjectRegistry.RegisterReplacedObject(list[i], canonical);
                    MaterialsRemoved++;
                }
                AtoLog.Debug_(Stage, $"collapsed {list.Count} identical materials into '{canonical.name}'");
            }

            if (map.Count == 0) return map;

            foreach (var renderer in renderers)
            {
                if (renderer == null) continue;
                var mats = renderer.sharedMaterials;
                bool changed = false;
                for (int i = 0; i < mats.Length; i++)
                {
                    if (mats[i] != null && map.TryGetValue(mats[i], out var canonical))
                    {
                        mats[i] = canonical;
                        changed = true;
                    }
                }
                if (changed) renderer.sharedMaterials = mats;
            }

            return map;
        }

        private void RewriteAnimationMaterials(Dictionary<Material, Material> map)
        {
            try
            {
                var asc = _ctx.Extension<AnimatorServicesContext>();
                asc.AnimationIndex.RewriteObjectCurves(obj =>
                    obj is Material m && map.TryGetValue(m, out var canonical) ? canonical : obj);
                AtoLog.Debug_(Stage, "animation object curves repointed at the deduplicated materials");
            }
            catch (Exception e)
            {
                AtoLog.Warning(Stage, $"could not rewrite animation curves: {e.Message}");
            }
        }

        #endregion

        #region Material slot merging

        /// <summary>
        /// EN: When a mesh ends up with several slots holding the very same opaque material, the sub
        ///     meshes can be merged into one and the duplicate slots removed. This is only safe when no
        ///     animation swaps those slots individually, and only for opaque materials, because merging
        ///     changes draw order which is observable for transparent materials.
        /// ZH: 当一个网格最终有多个槽持有完全相同的不透明材质时，可以把这些子网格合并为一个并删掉多余的槽。
        ///     只有在没有动画单独切换这些槽时才安全，且只针对不透明材质——
        ///     因为合并会改变绘制顺序，而这对透明材质是可观测的。
        /// </summary>
        private void MergeMaterialSlots(IReadOnlyList<Renderer> renderers, IReadOnlyDictionary<string, HashSet<int>> animatedSlots)
        {
            foreach (var renderer in renderers)
            {
                if (renderer == null) continue;
                var mesh = GetMesh(renderer);
                if (mesh == null) continue;
                if (!_ctx.AssetSaver.IsTemporaryAsset(mesh)) continue;

                var mats = renderer.sharedMaterials;
                if (mats.Length < 2 || mesh.subMeshCount < 2) continue;
                if (mats.Length != mesh.subMeshCount) continue;

                var path = nadena.dev.ndmf.runtime.RuntimeUtil.RelativePath(_ctx.AvatarRootObject, renderer.gameObject);
                if (path != null && animatedSlots != null && animatedSlots.TryGetValue(path, out var animated) && animated.Count > 0)
                {
                    AtoLog.Debug_(Stage, $"'{renderer.name}': slots are animated, not merging.");
                    continue;
                }

                // EN: Group slots by material, keeping only opaque duplicates.
                // ZH: 按材质对槽分组，只保留不透明的重复项。
                var firstSlotOf = new Dictionary<Material, int>();
                var remap = new int[mats.Length];
                var newMaterials = new List<Material>();
                bool anyMerged = false;

                for (int slot = 0; slot < mats.Length; slot++)
                {
                    var m = mats[slot];
                    bool mergeable = m != null && IsOpaque(m);
                    if (mergeable && firstSlotOf.TryGetValue(m, out var target))
                    {
                        remap[slot] = target;
                        anyMerged = true;
                        SlotsMerged++;
                        continue;
                    }

                    int newIndex = newMaterials.Count;
                    newMaterials.Add(m);
                    remap[slot] = newIndex;
                    if (mergeable) firstSlotOf[m] = newIndex;
                }

                if (!anyMerged) continue;

                // EN: Rebuild the sub meshes according to the remap.
                // ZH: 按重映射重建子网格。
                var merged = new List<int>[newMaterials.Count];
                for (int i = 0; i < merged.Length; i++) merged[i] = new List<int>();
                for (int slot = 0; slot < mats.Length; slot++)
                    merged[remap[slot]].AddRange(mesh.GetTriangles(slot));

                mesh.subMeshCount = newMaterials.Count;
                for (int i = 0; i < newMaterials.Count; i++)
                    mesh.SetTriangles(merged[i], i, false);
                mesh.RecalculateBounds();

                renderer.sharedMaterials = newMaterials.ToArray();
                if (path != null) RemapAnimationSlotIndices(path, remap, newMaterials.Count);

                AtoLog.Info(Stage,
                    $"'{renderer.name}': merged {mats.Length} material slots into {newMaterials.Count}");
            }
        }

        /// <summary>
        /// EN: Rewrites <c>m_Materials.Array.data[N]</c> bindings so animations keep addressing the right
        ///     slot after merging. Bindings whose slot disappeared are dropped, which is correct: that
        ///     slot no longer exists.
        /// ZH: 重写 <c>m_Materials.Array.data[N]</c> 绑定，使动画在合并后仍能定位到正确的槽。
        ///     所指槽已消失的绑定会被丢弃，这是正确的：那个槽已经不存在了。
        /// </summary>
        private void RemapAnimationSlotIndices(string rendererPath, int[] remap, int newSlotCount)
        {
            AnimatorServicesContext asc;
            try
            {
                asc = _ctx.Extension<AnimatorServicesContext>();
            }
            catch (Exception e)
            {
                AtoLog.Warning(Stage, $"cannot remap animated material slots: {e.Message}");
                return;
            }

            int rewritten = 0;
            foreach (var clip in asc.AnimationIndex.GetClipsForObjectPath(rendererPath).ToList())
            {
                var bindings = clip.GetObjectCurveBindings().Where(b => b.path == rendererPath).ToList();
                var pending = new List<(UnityEditor.EditorCurveBinding from, UnityEditor.EditorCurveBinding to, UnityEditor.ObjectReferenceKeyframe[] curve)>();

                foreach (var binding in bindings)
                {
                    var match = MaterialSlotBinding.Match(binding.propertyName);
                    if (!match.Success) continue;
                    int oldIndex = int.Parse(match.Groups["idx"].Value);
                    if (oldIndex < 0 || oldIndex >= remap.Length) continue;
                    int newIndex = remap[oldIndex];
                    if (newIndex == oldIndex) continue;

                    var curve = clip.GetObjectCurve(binding);
                    var target = binding;
                    target.propertyName = $"m_Materials.Array.data[{newIndex}]";
                    pending.Add((binding, target, curve));
                }

                foreach (var (from, to, curve) in pending)
                {
                    clip.SetObjectCurve(from, null);
                    if (to.propertyName != null && curve != null)
                    {
                        clip.SetObjectCurve(to, curve);
                        rewritten++;
                    }
                }
            }

            if (rewritten > 0)
                AtoLog.Debug_(Stage, $"remapped {rewritten} animated material slot bindings on '{rendererPath}'");
        }

        private static bool IsOpaque(Material m)
        {
            ShaderAnalysisUtil.ResolveAlphaMode(m, out var mode, out _);
            return mode == AtoAlphaMode.Opaque;
        }

        #endregion

        private static IEnumerable<Material> EnumerateMaterials(IReadOnlyList<Renderer> renderers)
        {
            foreach (var r in renderers)
            {
                if (r == null) continue;
                foreach (var m in r.sharedMaterials)
                    if (m != null)
                        yield return m;
            }
        }

        private static Mesh GetMesh(Renderer r)
        {
            if (r is SkinnedMeshRenderer smr) return smr.sharedMesh;
            if (r is MeshRenderer && r.TryGetComponent<MeshFilter>(out var mf)) return mf.sharedMesh;
            return null;
        }
    }
}
