using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace net.fosa.avatar_texture_optimizer.editor
{
    /// <summary>
    /// Stage: material & atlas deduplication + material slot merging. / 阶段：材质/图集去重 + 材质槽合并。
    ///
    /// - Materials identical in content AND parameters are merged (references updated via the
    ///   remapper), UNLESS animations switch them individually or animate their properties. /
    ///   内容+参数完全相同的材质合并（引用经重映射器更新），动画单独切换或动画其属性者除外。
    /// - Identical atlases (same content + same parameters) are merged. / 完全相同的图集合并。
    /// - Opaque identical material slots on one mesh are merged (submesh rewrite + slot index
    ///   remap for animations). / 同网格不透明相同材质槽合并（子网格重写 + 动画槽索引重映射）。
    /// </summary>
    internal sealed class AtoStageDedupeAssets : IAtoStage
    {
        public string I18nKey => "dedupeAssets";

        public void Run(AtoContext ctx)
        {
            DedupeMaterials(ctx);
            DedupeAtlases(ctx);
            MergeMaterialSlots(ctx);
        }

        // ------------------------------------------------------------------
        // material dedupe
        // ------------------------------------------------------------------

        private static void DedupeMaterials(AtoContext ctx)
        {
            // Candidate materials: everything on renderer slots (incl. animated options). /
            // 候选材质：渲染器槽上的全部材质（含动画可切换材质）。
            var candidates = new HashSet<Material>();
            foreach (var data in ctx.Renderers)
            {
                foreach (var slot in data.Slots)
                {
                    foreach (var material in slot.AnimatedOptions)
                    {
                        if (material != null && !ctx.WhitelistObjects.Contains(material))
                        {
                            candidates.Add(material);
                        }
                    }
                }
            }

            // Exclusions: directly animated / property-animated / keyword-animated materials. /
            // 排除：直接动画、属性动画、关键字动画的材质。
            var excluded = new HashSet<Material>(ctx.Animations.DirectAnimatedMaterials);
            foreach (var (material, _) in ctx.Animations.AnimatedProperties) excluded.Add(material);
            foreach (var (material, _) in ctx.Animations.AnimatedKeywords) excluded.Add(material);

            var groups = new Dictionary<Material, List<Material>>();
            foreach (var material in candidates)
            {
                if (excluded.Contains(material)) continue;
                var found = false;
                foreach (var representative in groups.Keys.ToList())
                {
                    if (MaterialComparer.Equals(representative, material))
                    {
                        groups[representative].Add(material);
                        found = true;
                        break;
                    }
                }
                if (!found) groups[material] = new List<Material>();
            }

            var merged = 0;
            foreach (var kv in groups)
            {
                foreach (var duplicate in kv.Value)
                {
                    ctx.Remapper.Register(duplicate, kv.Key);
                    AtoLog.Verbose($"[ATO] material dedupe: {duplicate.name} == {kv.Key.name}");
                    merged++;
                }
            }
            AtoLog.Info($"[ATO] material dedupe: {merged} material(s) merged.");
        }

        // ------------------------------------------------------------------
        // atlas dedupe
        // ------------------------------------------------------------------

        private static void DedupeAtlases(AtoContext ctx)
        {
            var merged = 0;
            var seen = new List<AtoAtlas>();
            foreach (var group in ctx.TypeGroups)
            {
                var survivors = new List<AtoAtlas>();
                foreach (var atlas in group.Atlases)
                {
                    var duplicateOf = seen.FirstOrDefault(a => AtlasesEqual(a, atlas));
                    if (duplicateOf != null)
                    {
                        // Remap this atlas's texture to the existing one. / 把本图集贴图重映射到已有图集。
                        if (atlas.Result != null && duplicateOf.Result != null)
                        {
                            ctx.Remapper.Register(atlas.Result, duplicateOf.Result);
                        }
                        merged++;
                        AtoLog.Verbose($"[ATO] atlas dedupe: {atlas.Name} == {duplicateOf.Name}");
                    }
                    else
                    {
                        survivors.Add(atlas);
                        seen.Add(atlas);
                    }
                }
                group.Atlases = survivors;
            }
            AtoLog.Info($"[ATO] atlas dedupe: {merged} atlas(es) merged.");
        }

        private static bool AtlasesEqual(AtoAtlas a, AtoAtlas b)
        {
            if (a.Width != b.Width || a.Height != b.Height) return false;
            if (a.Result == null || b.Result == null) return false;
            var hashA = a.Result.imageContentsHash;
            var hashB = b.Result.imageContentsHash;
            if (hashA.isValid && hashB.isValid) return hashA == hashB;
            return false;
        }

        // ------------------------------------------------------------------
        // material slot merging (opaque identical slots on one mesh)
        // ------------------------------------------------------------------

        private static void MergeMaterialSlots(AtoContext ctx)
        {
            foreach (var data in ctx.Renderers)
            {
                if (data.Slots.Count <= 1) continue;

                var remapped = data.Slots
                    .Select(s => (s, material: ctx.Remapper.Resolve(s.Initial)))
                    .ToList();

                // Build keep/merge map: equal opaque materials merge into the first kept slot. /
                // 构建保留/合并映射：相同不透明材质合并到第一个保留槽。
                var kept = new List<AtoMaterialSlot>();
                var mergeTarget = new List<AtoMaterialSlot>(); // parallel to the original slots: target kept slot. / 与原始槽平行：合并目标保留槽。
                for (var i = 0; i < remapped.Count; i++)
                {
                    var (slot, material) = remapped[i];
                    if (material == null || slot.IndividuallyAnimated || !MaterialComparer.IsOpaque(material))
                    {
                        kept.Add(slot);
                        mergeTarget.Add(null);
                        continue;
                    }
                    var mergedInto = kept.FirstOrDefault(k =>
                        !k.IndividuallyAnimated &&
                        MaterialComparer.Equals(ctx.Remapper.Resolve(k.Initial), material));
                    if (mergedInto != null)
                    {
                        mergeTarget.Add(mergedInto);
                        AtoLog.Verbose($"[ATO] slot merge: {data.Renderer.name} slot {slot.Index} -> {mergedInto.Index}");
                    }
                    else
                    {
                        kept.Add(slot);
                        mergeTarget.Add(null);
                    }
                }

                // Old slot index → NEW index (position in the kept list). / 旧槽索引 → 新索引（保留列表中的位置）。
                var newIndexBySlot = new Dictionary<int, int>();
                for (var s = 0; s < kept.Count; s++) newIndexBySlot[kept[s].Index] = s;

                var slotMap = new Dictionary<int, int>();
                for (var i = 0; i < mergeTarget.Count; i++)
                {
                    if (mergeTarget[i] != null)
                    {
                        slotMap[i] = newIndexBySlot[mergeTarget[i].Index];
                    }
                    else
                    {
                        slotMap[i] = newIndexBySlot[remapped[i].s.Index];
                    }
                }
                data.SlotMap = slotMap;

                if (!slotMap.Any(kv => kv.Key != kv.Value)) continue;

                // ---- rewrite the mesh submeshes ----
                var mesh = data.ResultMesh;
                if (mesh == null)
                {
                    // Mesh was never rewritten: clone to avoid touching the original asset. /
                    // 网格未被重写过：克隆以保护原始资产。
                    mesh = UnityEngine.Object.Instantiate(data.Mesh);
                    mesh.name = data.Mesh.name + "_ATO";
                    data.ResultMesh = mesh;
                    nadena.dev.ndmf.ObjectRegistry.RegisterReplacedObject(data.Mesh, mesh);
                    if (data.Renderer is SkinnedMeshRenderer smrForMerge)
                    {
                        smrForMerge.sharedMesh = mesh;
                    }
                    else if (data.Renderer is MeshRenderer mrForMerge)
                    {
                        var filter = mrForMerge.GetComponent<MeshFilter>();
                        if (filter != null) filter.sharedMesh = mesh;
                    }
                }

                var newSubmeshes = new List<List<int>>();
                var submeshCount = mesh.subMeshCount;
                for (var s = 0; s < submeshCount; s++)
                {
                    newSubmeshes.Add(new List<int>(mesh.GetTriangles(s)));
                }

                // Merge triangles into the kept slots. / 把三角形并入保留槽。
                var mergedByKept = new Dictionary<int, List<int>>();
                for (var i = 0; i < mergeTarget.Count; i++)
                {
                    if (mergeTarget[i] == null) continue;
                    var keeperOld = mergeTarget[i].Index;
                    if (!mergedByKept.TryGetValue(keeperOld, out var list))
                    {
                        mergedByKept[keeperOld] = list = new List<int>();
                    }
                    list.AddRange(newSubmeshes[i]);
                }

                var mergedIndices = new HashSet<int>();
                for (var i = 0; i < mergeTarget.Count; i++)
                {
                    if (mergeTarget[i] != null) mergedIndices.Add(i);
                }

                // New submesh list: kept slots in order, with merged triangles appended. /
                // 新子网格列表：按序保留槽 + 并入三角形。
                var finalSubmeshes = new List<List<int>>();
                foreach (var slot in kept)
                {
                    var list = newSubmeshes[slot.Index];
                    if (mergedByKept.TryGetValue(slot.Index, out var extra))
                    {
                        list = new List<int>(list);
                        list.AddRange(extra);
                    }
                    finalSubmeshes.Add(list);
                }

                // Write back: submesh count & triangles. / 写回：子网格数与三角形。
                mesh.subMeshCount = finalSubmeshes.Count;
                for (var s = 0; s < finalSubmeshes.Count; s++)
                {
                    mesh.SetTriangles(finalSubmeshes[s], s);
                }

                // Renderer materials: one per KEPT slot (order preserved). / 渲染器材质：每个保留槽一个（保序）。
                var materials = new Material[kept.Count];
                for (var s = 0; s < kept.Count; s++)
                {
                    materials[s] = ctx.Remapper.Resolve(kept[s].Initial);
                }
                data.Renderer.sharedMaterials = materials;

            }
        }
    }
}
