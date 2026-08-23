using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Fosa.AvatarTextureOptimizer.Editor.Analysis;
using Fosa.AvatarTextureOptimizer.Editor.Pipeline;
using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;
using UnityEditor;
using UnityEngine;

namespace Fosa.AvatarTextureOptimizer.Editor.Atlas
{
    internal sealed class MaterialAnimationRewriter
    {
        private readonly IAssetSaver _assetSaver;
        private readonly AnimationIndex _animationIndex;
        private readonly bool _deduplicateMaterials;
        private readonly bool _mergeOpaqueSlots;

        public MaterialAnimationRewriter(IAssetSaver assetSaver, AnimationIndex animationIndex,
            bool deduplicateMaterials, bool mergeOpaqueSlots)
        {
            _assetSaver = assetSaver; _animationIndex = animationIndex;
            _deduplicateMaterials = deduplicateMaterials; _mergeOpaqueSlots = mergeOpaqueSlots;
        }

        private readonly struct MaterialCloneKey : IEquatable<MaterialCloneKey>
        {
            public readonly MaterialSlotRecord Slot;
            public readonly Material Material;
            public MaterialCloneKey(MaterialSlotRecord slot, Material material) { Slot = slot; Material = material; }
            public bool Equals(MaterialCloneKey other) => ReferenceEquals(Slot, other.Slot) && Material == other.Material;
            public override bool Equals(object obj) => obj is MaterialCloneKey other && Equals(other);
            public override int GetHashCode() => (Slot.GetHashCode() * 397) ^ (Material == null ? 0 : Material.GetHashCode());
        }

        internal sealed class RendererCommit
        {
            public RendererRecord Record;
            public Material[] BeforeMaterials, AfterMaterials;
            public Mesh BeforeMesh, AfterMesh;
        }

        private sealed class SlotMap
        {
            public readonly Dictionary<int, int> OldToNew = new Dictionary<int, int>();
            public int this[int old] => OldToNew.TryGetValue(old, out var value) ? value : old;
            public bool Changed => OldToNew.Any(pair => pair.Key != pair.Value);
        }

        private sealed class ObjectCurveCommit
        {
            public VirtualClip Clip; public EditorCurveBinding Source, Destination;
            public ObjectReferenceKeyframe[] BeforeSource, BeforeDestination, After;
            public bool Moved => !Source.Equals(Destination);
            public void Apply()
            {
                if (!Moved) Clip.SetObjectCurve(Source, After);
                else
                {
                    Clip.SetObjectCurve(Destination, After);
                    Clip.SetObjectCurve(Source, null);
                }
            }
            public bool Rollback()
            {
                var restored = TryRollback("object source", () => Clip.SetObjectCurve(Source, BeforeSource));
                if (Moved) restored &= TryRollback("object destination", () => Clip.SetObjectCurve(Destination, BeforeDestination));
                return restored;
            }
        }

        private sealed class FloatCurveCommit
        {
            public VirtualClip Clip; public EditorCurveBinding Source, Destination;
            public AnimationCurve BeforeSource, BeforeDestination, After;
            public void Apply()
            {
                Clip.SetFloatCurve(Destination, After);
                Clip.SetFloatCurve(Source, null);
            }
            public bool Rollback()
            {
                var restored = TryRollback("float source", () => Clip.SetFloatCurve(Source, BeforeSource));
                restored &= TryRollback("float destination", () => Clip.SetFloatCurve(Destination, BeforeDestination));
                return restored;
            }
        }

        public IATOCommitTransaction Apply(AvatarAnalysis analysis, AtlasPlan plan, AtlasBuildResult atlases,
            IReadOnlyDictionary<Renderer, Mesh> meshes)
        {
            Dictionary<MaterialCloneKey, Material> clones = null;
            List<RendererCommit> rendererCommits = null;
            MaterialCommitTransaction transaction = null;
            try
            {
                clones = BuildMaterialClones(analysis, plan, atlases, meshes.Keys);
                Deduplicate(clones);
                rendererCommits = BuildRendererCommits(analysis, meshes, clones);
                var slotMaps = _mergeOpaqueSlots ? MergeSafeOpaqueSlots(rendererCommits) : IdentityMaps(rendererCommits);
                var objectCurves = BuildObjectCurveCommits(analysis, atlases, clones, meshes.Keys, slotMaps);
                var floatCurves = BuildFloatCurveCommits(analysis, meshes.Keys, slotMaps);
                PruneUnreferencedMaterialClones(clones, rendererCommits, objectCurves);

                // Complete every identity/curve/slot preflight before persisting newly cloned materials or merged meshes.
                // NDMF cannot delete a saved asset, so delaying SaveAsset reduces residue on a rejected commit plan.
                // 先完成身份、曲线和槽位门禁，再持久化新材质及合并网格。
                PersistCommitAssets(atlases.AllTextures, clones.Values, rendererCommits);
                transaction = new MaterialCommitTransaction(rendererCommits, objectCurves, floatCurves,
                    clones.Values.Distinct().ToArray());
                transaction.Apply();
                return transaction;
            }
            catch (Exception exception)
            {
                // Once Apply starts, the transaction itself owns rollback and only destroys generated objects when
                // every Avatar reference was restored. Before that point no Avatar mutation has occurred.
                if (transaction == null)
                {
                    DestroyTransient(clones == null ? null : clones.Values);
                    DestroyTransientMeshes(rendererCommits);
                }
                else if (!transaction.ApplyRollbackRestored)
                {
                    throw new ATORollbackIncompleteException(
                        "ATO commit failed and at least one Avatar reference could not be restored; generated assets were retained.",
                        exception);
                }
                throw;
            }
        }

        private Dictionary<MaterialCloneKey, Material> BuildMaterialClones(AvatarAnalysis analysis, AtlasPlan plan,
            AtlasBuildResult atlases, IEnumerable<Renderer> remappedRenderers)
        {
            var remapped = new HashSet<Renderer>(remappedRenderers);
            var planned = new HashSet<UvGroupRecord>(plan.Pages.SelectMany(page => page.Groups));
            var result = new Dictionary<MaterialCloneKey, Material>();
            try
            {
                foreach (var renderer in analysis.Renderers.Where(value => remapped.Contains(value.Renderer)))
                foreach (var slot in renderer.Slots)
                foreach (var source in slot.Materials.Where(value => value != null))
                {
                    ATOProgress.Checkpoint("Rewriting material " + source.name);
                    var clone = UnityEngine.Object.Instantiate(source);
                    try
                    {
                        clone.name = "ATO_" + source.name + "_S" + slot.Slot;
                        foreach (var group in analysis.UvGroups.Where(value => value.Slot == slot && planned.Contains(value)))
                        {
                            var layout = plan.GroupLayouts[group];
                            if (!layout.MaterialLayers.TryGetValue(source, out var layers)) continue;
                            for (var layer = 0; layer < layers.Count; layer++)
                            {
                                var binding = layers[layer];
                                if (binding.Initial == null) continue; // Preserve an originally null texture reference.
                                if (atlases.MaterialVariants.TryGetValue(
                                        new GroupMaterialLayerKey(group, source, layer), out var texture))
                                    clone.SetTexture(binding.PropertyName, texture);
                            }
                        }
                        result.Add(new MaterialCloneKey(slot, source), clone); clone = null;
                    }
                    finally { if (clone != null) UnityEngine.Object.DestroyImmediate(clone); }
                }
                return result;
            }
            catch
            {
                DestroyTransient(result.Values); throw;
            }
        }

        private void Deduplicate(Dictionary<MaterialCloneKey, Material> materials)
        {
            if (!_deduplicateMaterials) return;
            var canonical = new Dictionary<string, Material>(StringComparer.Ordinal);
            foreach (var key in materials.Keys.ToArray())
            {
                var material = materials[key];
                var identity = MaterialIdentity(material);
                if (canonical.TryGetValue(identity, out var existing))
                { materials[key] = existing; UnityEngine.Object.DestroyImmediate(material); continue; }
                canonical.Add(identity, material);
            }
        }

        private void PersistCommitAssets(IEnumerable<Texture2D> textures, IEnumerable<Material> materials,
            IEnumerable<RendererCommit> renderers)
        {
            foreach (var texture in (textures ?? Enumerable.Empty<Texture2D>()).Where(value => value != null).Distinct())
                if (!EditorUtility.IsPersistent(texture)) _assetSaver.SaveAsset(texture);
            foreach (var renderer in renderers ?? Enumerable.Empty<RendererCommit>())
                if (renderer?.AfterMesh != null && renderer.AfterMesh != renderer.BeforeMesh &&
                    !EditorUtility.IsPersistent(renderer.AfterMesh))
                    _assetSaver.SaveAsset(renderer.AfterMesh);
            foreach (var material in (materials ?? Enumerable.Empty<Material>()).Where(value => value != null).Distinct())
                if (!EditorUtility.IsPersistent(material)) _assetSaver.SaveAsset(material);
        }

        internal static string MaterialIdentity(Material material)
        {
            if (material == null) throw new ArgumentNullException(nameof(material));
            return Regex.Replace(EditorJsonUtility.ToJson(material),
                "\"m_Name\"\\s*:\\s*\"(?:\\\\.|[^\"])*\"\\s*,?", string.Empty);
        }

        private static List<RendererCommit> BuildRendererCommits(AvatarAnalysis analysis,
            IReadOnlyDictionary<Renderer, Mesh> meshes, IReadOnlyDictionary<MaterialCloneKey, Material> clones)
        {
            var result = new List<RendererCommit>();
            foreach (var renderer in analysis.Renderers.Where(value => meshes.ContainsKey(value.Renderer)))
            {
                var beforeMaterials = renderer.Renderer.sharedMaterials; var afterMaterials = (Material[])beforeMaterials.Clone();
                foreach (var slot in renderer.Slots)
                    if (slot.Slot < afterMaterials.Length && afterMaterials[slot.Slot] != null &&
                        clones.TryGetValue(new MaterialCloneKey(slot, afterMaterials[slot.Slot]), out var clone)) afterMaterials[slot.Slot] = clone;
                result.Add(new RendererCommit { Record = renderer, BeforeMaterials = beforeMaterials, AfterMaterials = afterMaterials,
                    BeforeMesh = GetMesh(renderer.Renderer), AfterMesh = meshes[renderer.Renderer] });
            }
            return result;
        }

        private static void PruneUnreferencedMaterialClones(Dictionary<MaterialCloneKey, Material> clones,
            IEnumerable<RendererCommit> renderers, IEnumerable<ObjectCurveCommit> objectCurves)
        {
            if (clones == null || clones.Count == 0) return;
            var live = new HashSet<Material>((renderers ?? Enumerable.Empty<RendererCommit>())
                .Where(value => value != null && value.AfterMaterials != null)
                .SelectMany(value => value.AfterMaterials)
                .Concat((objectCurves ?? Enumerable.Empty<ObjectCurveCommit>())
                    .Where(value => value != null && value.After != null)
                    .SelectMany(value => value.After)
                    .Select(value => value.value as Material))
                .Where(value => value != null));
            var unusedKeys = clones.Where(value => value.Value != null && !live.Contains(value.Value))
                .Select(value => value.Key).ToArray();
            var unusedMaterials = unusedKeys.Select(value => clones[value]).Where(value => value != null)
                .Distinct().ToArray();

            // Do this before the first SaveAsset call: NDMF has no deletion API for an orphan persisted clone.
            // 必须在首次持久化前清理；NDMF 无法删除已经保存的孤立 clone。
            foreach (var material in unusedMaterials)
                if (!EditorUtility.IsPersistent(material)) UnityEngine.Object.DestroyImmediate(material);
            foreach (var key in unusedKeys) clones.Remove(key);
        }

        private Dictionary<RendererRecord, SlotMap> MergeSafeOpaqueSlots(IReadOnlyList<RendererCommit> renderers)
        {
            var maps = IdentityMaps(renderers);
            foreach (var renderer in renderers)
            {
                var mesh = renderer.AfterMesh; var materials = renderer.AfterMaterials;
                // Skinning/blend shapes can invalidate static separation, and any property block can carry
                // material-index-specific state which would be lost when the index disappears.
                if (!(renderer.Record.Renderer is MeshRenderer) || renderer.Record.Renderer.HasPropertyBlock() ||
                    mesh == null || materials.Length != mesh.subMeshCount ||
                    materials.Length != renderer.Record.Slots.Count) continue;

                var animated = AnimatedSlots(renderer.Record); var map = maps[renderer.Record];
                // Slot merging is an independent output option. Compare the same complete serialized identity used by
                // global material deduplication, but only inside the already proven-safe merge gate for this Renderer.
                // 槽合并与全局材质去重是独立开关；这里只在既有安全门禁内比较完整序列化身份。
                var representatives = new Dictionary<string, int>(StringComparer.Ordinal);
                var representativeMembers = new Dictionary<int, List<int>>();
                var bounds = new Dictionary<int, Bounds>(); var next = 0;
                for (var slot = 0; slot < materials.Length; slot++)
                {
                    var slotRecord = renderer.Record.Slots.FirstOrDefault(value => value.Slot == slot);
                    var safe = slotRecord != null && IsMergeSafeOpaque(slotRecord, materials[slot]) &&
                               !animated.Contains(slot) && mesh.GetTopology(slot) == MeshTopology.Triangles;
                    var identity = safe ? MaterialIdentity(materials[slot]) : null;
                    if (safe && representatives.TryGetValue(identity, out var existing) &&
                        representativeMembers[existing].All(member =>
                            SubmeshBoundsAreStrictlySeparated(mesh, member, slot, bounds)))
                    {
                        map.OldToNew[slot] = existing;
                        representativeMembers[existing].Add(slot);
                    }
                    else
                    {
                        map.OldToNew[slot] = next;
                        if (safe)
                        {
                            representatives[identity] = next;
                            representativeMembers[next] = new List<int> { slot };
                        }
                        next++;
                    }
                }
                if (!map.Changed) continue;
                Mesh merged = null;
                try
                {
                    var indices = Enumerable.Range(0, next).Select(_ => new List<int>()).ToArray();
                    var topologies = new MeshTopology[next]; var newMaterials = new Material[next];
                    var mergedBounds = new Bounds[next]; var hasBounds = new bool[next];
                    for (var old = 0; old < materials.Length; old++)
                    {
                        var target = map[old]; indices[target].AddRange(mesh.GetIndices(old));
                        if (newMaterials[target] == null) { newMaterials[target] = materials[old]; topologies[target] = mesh.GetTopology(old); }
                        var sourceBounds = mesh.GetSubMesh(old).bounds;
                        if (!hasBounds[target]) { mergedBounds[target] = sourceBounds; hasBounds[target] = true; }
                        else mergedBounds[target].Encapsulate(sourceBounds);
                    }
                    merged = UnityEngine.Object.Instantiate(mesh); merged.name = mesh.name + "_MergedOpaque"; merged.subMeshCount = next;
                    for (var submesh = 0; submesh < next; submesh++)
                    {
                        merged.SetIndices(indices[submesh], topologies[submesh], submesh, false);
                        var descriptor = merged.GetSubMesh(submesh); descriptor.bounds = mergedBounds[submesh];
                        merged.SetSubMesh(submesh, descriptor,
                            UnityEngine.Rendering.MeshUpdateFlags.DontRecalculateBounds |
                            UnityEngine.Rendering.MeshUpdateFlags.DontValidateIndices);
                    }
                    merged.bounds = mesh.bounds;
                    // Submesh merging changes the per-submesh sampling set used by mip streaming metrics.
                    // 合并子网格后重新计算 mip streaming 所依赖的 UV 分布指标。
                    merged.RecalculateUVDistributionMetrics();
                    ReplaceAfterMeshWithMerged(renderer, merged);
                    renderer.AfterMaterials = newMaterials; merged = null;
                }
                finally { if (merged != null && !EditorUtility.IsPersistent(merged)) UnityEngine.Object.DestroyImmediate(merged); }
            }
            return maps;
        }

        internal static void ReplaceAfterMeshWithMerged(RendererCommit renderer, Mesh merged)
        {
            if (renderer == null) throw new ArgumentNullException(nameof(renderer));
            if (merged == null) throw new ArgumentNullException(nameof(merged));
            var superseded = renderer.AfterMesh;
            renderer.AfterMesh = merged;
            // The Pipeline's original dictionary still aliases this pre-merge transient, but successful completion
            // disables its broad cleanup because other dictionary values are Avatar-owned. Reclaim only the object
            // whose ownership was actually superseded here. / 成功路径不会全量清理 Mesh 字典，因此在替换点精确释放旧对象。
            if (superseded != null && superseded != renderer.BeforeMesh && superseded != merged &&
                !EditorUtility.IsPersistent(superseded)) UnityEngine.Object.DestroyImmediate(superseded);
        }

        private HashSet<int> AnimatedSlots(RendererRecord renderer)
        {
            var result = new HashSet<int>();
            foreach (var clip in _animationIndex.GetClipsForObjectPath(renderer.Path))
            {
                foreach (var binding in clip.GetObjectCurveBindings())
                    if (AnimationAnalyzer.BindingTargetsRenderer(binding, renderer.Path, renderer.Renderer) &&
                        TryParseBinding(binding.propertyName, out var slot, out _)) result.Add(slot);
                foreach (var binding in clip.GetFloatCurveBindings())
                    if (AnimationAnalyzer.BindingTargetsRenderer(binding, renderer.Path, renderer.Renderer) &&
                        AnimationAnalyzer.TryGetMaterialProperty(binding.propertyName, out var slot, out _)) result.Add(slot);
            }
            return result;
        }

        private static bool IsMergeSafeOpaque(MaterialSlotRecord slot, Material generatedMaterial)
        {
            if (generatedMaterial == null || slot.AtlasUnsafe || slot.Materials.Count != 1 ||
                !slot.Materials.All(ShaderTextureAnalyzer.IsVerifiedOpaqueMergeShader) ||
                slot.Bindings.Count == 0 || slot.Bindings.Any(value => !value.AtlasSafe || value.Whitelisted ||
                    value.AlphaMode != ATOAlphaMode.Opaque || value.EvaluateBlend || value.EvaluateCutout)) return false;

            // Reject stale/tampered Standard state even when tags still claim Opaque. Known VRC shaders have
            // fixed states, while these checks also cover every state property they expose to a material.
            if (generatedMaterial.renderQueue > (int)UnityEngine.Rendering.RenderQueue.GeometryLast ||
                generatedMaterial.IsKeywordEnabled("_ALPHATEST_ON") ||
                generatedMaterial.IsKeywordEnabled("_ALPHABLEND_ON") ||
                generatedMaterial.IsKeywordEnabled("_ALPHAPREMULTIPLY_ON") ||
                !MatchesIntWhenPresent(generatedMaterial, "_ZWrite", 1) ||
                !MatchesIntWhenPresent(generatedMaterial, "_ZTest", (int)UnityEngine.Rendering.CompareFunction.LessEqual) ||
                !MatchesIntWhenPresent(generatedMaterial, "_SrcBlend", (int)UnityEngine.Rendering.BlendMode.One) ||
                !MatchesIntWhenPresent(generatedMaterial, "_DstBlend", (int)UnityEngine.Rendering.BlendMode.Zero) ||
                !MatchesIntWhenPresent(generatedMaterial, "_ColorMask", (int)UnityEngine.Rendering.ColorWriteMask.All) ||
                !MatchesIntWhenPresent(generatedMaterial, "_StencilPass", (int)UnityEngine.Rendering.StencilOp.Keep) ||
                !MatchesIntWhenPresent(generatedMaterial, "_StencilFail", (int)UnityEngine.Rendering.StencilOp.Keep) ||
                !MatchesIntWhenPresent(generatedMaterial, "_StencilZFail", (int)UnityEngine.Rendering.StencilOp.Keep) ||
                !MatchesIntWhenPresent(generatedMaterial, "_StencilComp", (int)UnityEngine.Rendering.CompareFunction.Always))
                return false;
            var renderType = generatedMaterial.GetTag("RenderType", false, string.Empty);
            return string.IsNullOrEmpty(renderType) || string.Equals(renderType, "Opaque", StringComparison.Ordinal);
        }

        private static bool MatchesIntWhenPresent(Material material, string property, int expected) =>
            !material.HasProperty(property) || material.GetInt(property) == expected;

        internal static bool SubmeshBoundsAreStrictlySeparated(Mesh mesh, int first, int second,
            IDictionary<int, Bounds> cache = null)
        {
            if (mesh == null || first < 0 || second < 0 || first >= mesh.subMeshCount || second >= mesh.subMeshCount)
                return false;
            cache = cache ?? new Dictionary<int, Bounds>();
            if (!cache.TryGetValue(first, out var a)) { a = CalculateSubmeshBounds(mesh, first); cache[first] = a; }
            if (!cache.TryGetValue(second, out var b)) { b = CalculateSubmeshBounds(mesh, second); cache[second] = b; }
            var scale = Mathf.Max(1f, a.extents.magnitude, b.extents.magnitude);
            var epsilon = 1e-6f * scale;
            return a.max.x + epsilon < b.min.x || b.max.x + epsilon < a.min.x ||
                   a.max.y + epsilon < b.min.y || b.max.y + epsilon < a.min.y ||
                   a.max.z + epsilon < b.min.z || b.max.z + epsilon < a.min.z;
        }

        private static Bounds CalculateSubmeshBounds(Mesh mesh, int submesh)
        {
            var indices = mesh.GetIndices(submesh, true);
            var vertices = mesh.vertices;
            if (indices.Length == 0) return new Bounds(Vector3.zero, Vector3.zero);
            var first = indices[0];
            if (first < 0 || first >= vertices.Length) throw new InvalidOperationException("ATO encountered an invalid submesh index.");
            var bounds = new Bounds(vertices[first], Vector3.zero);
            for (var index = 1; index < indices.Length; index++)
            {
                var vertex = indices[index];
                if (vertex < 0 || vertex >= vertices.Length) throw new InvalidOperationException("ATO encountered an invalid submesh index.");
                bounds.Encapsulate(vertices[vertex]);
            }
            return bounds;
        }

        private static Dictionary<RendererRecord, SlotMap> IdentityMaps(IEnumerable<RendererCommit> renderers)
        {
            var result = new Dictionary<RendererRecord, SlotMap>();
            foreach (var renderer in renderers)
            {
                var map = new SlotMap();
                for (var slot = 0; slot < renderer.AfterMaterials.Length; slot++) map.OldToNew[slot] = slot;
                result[renderer.Record] = map;
            }
            return result;
        }

        private List<ObjectCurveCommit> BuildObjectCurveCommits(AvatarAnalysis analysis, AtlasBuildResult atlases,
            IReadOnlyDictionary<MaterialCloneKey, Material> clones, IEnumerable<Renderer> remappedRenderers,
            IReadOnlyDictionary<RendererRecord, SlotMap> slotMaps)
        {
            var remapped = new HashSet<Renderer>(remappedRenderers); var result = new List<ObjectCurveCommit>();
            var clips = analysis.Renderers.Where(value => remapped.Contains(value.Renderer))
                .SelectMany(value => _animationIndex.GetClipsForObjectPath(value.Path)).Distinct();
            foreach (var clip in clips)
            foreach (var binding in clip.GetObjectCurveBindings().ToArray())
            {
                ATOProgress.Checkpoint("Rewriting animated object curves");
                var renderer = AnimationAnalyzer.ResolveRendererRecord(analysis.Renderers, binding, out var ambiguous);
                if (ambiguous)
                    throw new InvalidOperationException("ATO cannot safely resolve an animated Renderer on a duplicate hierarchy path.");
                if (renderer == null || !remapped.Contains(renderer.Renderer) ||
                    !TryParseBinding(binding.propertyName, out var slotIndex, out var property) || slotIndex < 0) continue;
                var slot = renderer.Slots.FirstOrDefault(value => value.Slot == slotIndex);
                if (slot == null) continue;
                var before = clip.GetObjectCurve(binding); if (before == null) continue;
                var after = (ObjectReferenceKeyframe[])before.Clone(); var changed = false;
                for (var frame = 0; frame < after.Length; frame++)
                {
                    if (property == null && after[frame].value is Material sourceMaterial &&
                        clones.TryGetValue(new MaterialCloneKey(slot, sourceMaterial), out var replacementMaterial))
                    { after[frame].value = replacementMaterial; changed = true; continue; }
                    if (property == null || !(after[frame].value is Texture sourceTexture)) continue;
                    var resolution = AnimatedTextureResolver.Resolve(slot, property, sourceTexture,
                        atlases.AnimatedTextureVariants, out var replacementTexture);
                    if (resolution == AnimatedTextureResolution.Unmapped) continue;
                    if (resolution == AnimatedTextureResolution.Ambiguous)
                        throw new InvalidOperationException(
                            "ATO cannot rewrite an animated texture curve to one complete and unambiguous atlas variant.");
                    after[frame].value = replacementTexture; changed = true;
                }
                var destination = RemapBinding(binding, slotMaps[renderer][slotIndex], property);
                if (!changed && destination.Equals(binding)) continue;
                EnsureMutableClipForRewrite(clip);
                result.Add(new ObjectCurveCommit { Clip = clip, Source = binding, Destination = destination,
                    BeforeSource = before, BeforeDestination = destination.Equals(binding) ? before : clip.GetObjectCurve(destination), After = after });
            }
            return result.OrderBy(value => ParsedSlot(value.Source)).ToList();
        }

        private List<FloatCurveCommit> BuildFloatCurveCommits(AvatarAnalysis analysis, IEnumerable<Renderer> remappedRenderers,
            IReadOnlyDictionary<RendererRecord, SlotMap> slotMaps)
        {
            var remapped = new HashSet<Renderer>(remappedRenderers); var result = new List<FloatCurveCommit>();
            var clips = analysis.Renderers.Where(value => remapped.Contains(value.Renderer))
                .SelectMany(value => _animationIndex.GetClipsForObjectPath(value.Path)).Distinct();
            foreach (var clip in clips)
            foreach (var binding in clip.GetFloatCurveBindings().ToArray())
            {
                ATOProgress.Checkpoint("Rewriting animated float curves");
                var renderer = AnimationAnalyzer.ResolveRendererRecord(analysis.Renderers, binding, out var ambiguous);
                if (ambiguous)
                    throw new InvalidOperationException("ATO cannot safely resolve an animated Renderer on a duplicate hierarchy path.");
                if (renderer == null || !remapped.Contains(renderer.Renderer) ||
                    !AnimationAnalyzer.TryGetMaterialProperty(binding.propertyName, out var slot, out var property) ||
                    slot < 0 || !renderer.Slots.Any(value => value.Slot == slot)) continue;
                var mapped = slotMaps[renderer][slot]; if (mapped == slot) continue;
                var destination = RemapBinding(binding, mapped, property); var curve = clip.GetFloatCurve(binding); if (curve == null) continue;
                EnsureMutableClipForRewrite(clip);
                result.Add(new FloatCurveCommit { Clip = clip, Source = binding, Destination = destination,
                    BeforeSource = curve, BeforeDestination = clip.GetFloatCurve(destination), After = curve });
            }
            return result.OrderBy(value => ParsedSlot(value.Source)).ToList();
        }

        internal static void CommitRendererChangesForTests(IReadOnlyList<RendererCommit> renderers)
        {
            using (var transaction = new MaterialCommitTransaction(renderers,
                       Array.Empty<ObjectCurveCommit>(), Array.Empty<FloatCurveCommit>(), Array.Empty<Material>()))
            {
                transaction.Apply();
                transaction.Complete();
            }
        }

        private sealed class MaterialCommitTransaction : IATOCommitTransaction
        {
            private readonly IReadOnlyList<RendererCommit> _renderers;
            private readonly IReadOnlyList<ObjectCurveCommit> _objects;
            private readonly IReadOnlyList<FloatCurveCommit> _floats;
            private readonly IReadOnlyList<Material> _materials;
            private bool _applied;
            private bool _finished;
            internal bool ApplyRollbackRestored { get; private set; }

            public MaterialCommitTransaction(IReadOnlyList<RendererCommit> renderers,
                IReadOnlyList<ObjectCurveCommit> objects, IReadOnlyList<FloatCurveCommit> floats,
                IReadOnlyList<Material> materials)
            {
                _renderers = renderers ?? Array.Empty<RendererCommit>();
                _objects = objects ?? Array.Empty<ObjectCurveCommit>();
                _floats = floats ?? Array.Empty<FloatCurveCommit>();
                _materials = materials ?? Array.Empty<Material>();
            }

            public void Apply()
            {
                if (_applied || _finished) throw new InvalidOperationException("ATO commit transaction cannot be applied twice.");
                var objectCount = 0; var floatCount = 0; var rendererCount = 0;
                var currentObject = false; var currentFloat = false; var currentRenderer = false;
                try
                {
                    for (; objectCount < _objects.Count; objectCount++)
                    {
                        ATOProgress.Checkpoint("Committing animated object curves");
                        currentObject = true; _objects[objectCount].Apply(); currentObject = false;
                    }
                    for (; floatCount < _floats.Count; floatCount++)
                    {
                        ATOProgress.Checkpoint("Committing animated float curves");
                        currentFloat = true; _floats[floatCount].Apply(); currentFloat = false;
                    }
                    for (; rendererCount < _renderers.Count; rendererCount++)
                    {
                        ATOProgress.Checkpoint("Committing remapped renderer");
                        currentRenderer = true;
                        var change = _renderers[rendererCount];
                        SetMesh(change.Record.Renderer, change.AfterMesh);
                        change.Record.Renderer.sharedMaterials = change.AfterMaterials;
                        currentRenderer = false;
                    }
                    _applied = true;
                }
                catch
                {
                    // The current setter may throw after mutation, so restore it as well as every completed item.
                    // Cleanup is only safe if no Avatar/curve reference to a generated object remains.
                    var restored = true;
                    if (currentRenderer)
                        restored &= RollbackRenderer(_renderers[rendererCount], "current renderer");
                    if (currentFloat) restored &= _floats[floatCount].Rollback();
                    if (currentObject) restored &= _objects[objectCount].Rollback();
                    restored &= RollbackCompleted(rendererCount, floatCount, objectCount);
                    ApplyRollbackRestored = restored;
                    _finished = true;
                    if (restored) DestroyGeneratedObjects();
                    throw;
                }
            }

            public void Complete()
            {
                if (_finished) return;
                // Apply() is completed before this transaction is returned to the pipeline. Completion is therefore
                // deliberately non-throwing so component removal can be the final fallible build mutation.
                // Apply 成功后事务才会交给流水线；Complete 必须无异常，确保标记组件删除位于最后的可失败边界。
                Debug.Assert(_applied, "ATO material transaction completed before Apply.");
                _finished = true;
            }

            public bool Rollback()
            {
                if (_finished) return ApplyRollbackRestored;
                if (!_applied) { _finished = true; return true; }
                var restored = RollbackCompleted(_renderers.Count, _floats.Count, _objects.Count);
                ApplyRollbackRestored = restored;
                _finished = true;
                if (restored) DestroyGeneratedObjects();
                return restored;
            }

            public void Dispose()
            {
                if (!_finished) Rollback();
            }

            private bool RollbackCompleted(int rendererCount, int floatCount, int objectCount)
            {
                var restored = true;
                for (var index = rendererCount - 1; index >= 0; index--)
                    restored &= RollbackRenderer(_renderers[index], "renderer");
                for (var index = floatCount - 1; index >= 0; index--)
                    restored &= _floats[index].Rollback();
                for (var index = objectCount - 1; index >= 0; index--)
                    restored &= _objects[index].Rollback();
                return restored;
            }

            private static bool RollbackRenderer(RendererCommit change, string operation)
            {
                var restored = TryRollback(operation + " mesh", () => SetMesh(change.Record.Renderer, change.BeforeMesh));
                restored &= TryRollback(operation + " materials",
                    () => change.Record.Renderer.sharedMaterials = change.BeforeMaterials);
                return restored;
            }

            private void DestroyGeneratedObjects()
            {
                DestroyTransient(_materials);
                DestroyTransientMeshes(_renderers);
            }
        }

        private static bool TryRollback(string operation, Action rollback)
        {
            try { rollback(); return true; }
            catch (Exception exception)
            {
                Debug.LogError("[ATO] Transaction rollback failed for " + operation + ": " + exception);
                return false;
            }
        }

        private static void DestroyTransient(IEnumerable<Material> materials)
        {
            if (materials == null) return;
            foreach (var material in materials.Where(value => value != null).Distinct())
                if (!EditorUtility.IsPersistent(material)) UnityEngine.Object.DestroyImmediate(material);
        }

        private static void DestroyTransientMeshes(IEnumerable<RendererCommit> renderers)
        {
            if (renderers == null) return;
            foreach (var mesh in renderers.Where(value => value != null && value.AfterMesh != value.BeforeMesh)
                         .Select(value => value.AfterMesh).Where(value => value != null).Distinct())
                if (!EditorUtility.IsPersistent(mesh)) UnityEngine.Object.DestroyImmediate(mesh);
        }

        internal static void EnsureMutableClipForRewrite(VirtualClip clip)
        {
            if (clip == null) throw new ArgumentNullException(nameof(clip));
            if (clip.IsMarkerClip)
                throw new InvalidOperationException(
                    "ATO refused to rewrite an immutable NDMF marker clip; analysis should have preserved its complete material slot.");
        }

        internal static EditorCurveBinding RemapBinding(EditorCurveBinding binding, int slot, string property)
        {
            var result = binding;
            result.propertyName = property == null ? "m_Materials.Array.data[" + slot + "]" :
                slot == 0 ? "material." + property : "materials.Array.data[" + slot + "]." + property;
            return result;
        }

        private static int ParsedSlot(EditorCurveBinding binding) => TryParseBinding(binding.propertyName, out var slot, out _) ? slot : int.MaxValue;

        internal static bool TryParseBinding(string name, out int slot, out string property)
        {
            const string materialArray = "m_Materials.Array.data[";
            if (name.StartsWith(materialArray, StringComparison.Ordinal))
            {
                var close = name.IndexOf(']', materialArray.Length);
                if (close > materialArray.Length && int.TryParse(name.Substring(materialArray.Length, close - materialArray.Length), out slot))
                { property = null; return close == name.Length - 1; }
            }
            return AnimationAnalyzer.TryGetMaterialProperty(name, out slot, out property);
        }

        private static Mesh GetMesh(Renderer renderer)
        {
            if (renderer is SkinnedMeshRenderer skinned) return skinned.sharedMesh;
            var filter = renderer.GetComponent<MeshFilter>(); return filter == null ? null : filter.sharedMesh;
        }

        private static void SetMesh(Renderer renderer, Mesh mesh)
        {
            if (renderer is SkinnedMeshRenderer skinned) skinned.sharedMesh = mesh;
            else
            {
                var filter = renderer.GetComponent<MeshFilter>();
                if (filter == null) throw new InvalidOperationException("ATO MeshRenderer lost its MeshFilter before commit.");
                filter.sharedMesh = mesh;
            }
        }
    }
}
