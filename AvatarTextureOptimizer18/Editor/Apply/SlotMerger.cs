using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace Fosa.AvatarTextureOptimizer.Editor.Apply
{
    // 材质槽合并器：同网格上材质相同的槽位合并为单一子网格/槽位，并重写动画中的槽位索引绑定。
    // Material slot merger: slots of a renderer sharing the same material merge into one submesh/slot;
    // animation slot-index bindings are rewritten accordingly.
    // 安全条件：涉及该渲染器材质槽绑定的动画剪辑必须全部为临时资产（NDMF 克隆），否则放弃该渲染器的合并并告警。
    // Safety: every clip binding this renderer's material slots must be a temporary asset (NDMF clone); otherwise skip + warn.
    internal static class SlotMerger
    {
        private static readonly Regex SlotPattern = new Regex(@"^m_Materials\.Array\.data\[(\d+)\](?:\.(.*))?$", RegexOptions.Compiled);

        public static void Merge(ATOContext ctx, ATOReport.Stage stage)
        {
            if (!ctx.settings.mergeMaterialSlots) return;
            int mergedRenderers = 0, mergedSlots = 0;

            foreach (var r in ctx.renderers)
            {
                ctx.CheckCancelled();
                var slots = new List<Analysis.SlotEntry>();
                foreach (var s in ctx.slots)
                {
                    if (s.renderer == r) slots.Add(s);
                }
                if (slots.Count <= 1) continue;
                slots.Sort((a, b) => a.slotIndex.CompareTo(b.slotIndex));

                // 安全条件：槽位必须与子网格一一对应（索引连续且数量相等），否则跳过（保守）。
                // Safety: slots must map 1:1 to submeshes (contiguous indices, equal count); otherwise skip (conservative).
                var mesh0 = GetMesh(r);
                if (mesh0 == null || slots.Count != mesh0.subMeshCount) continue;
                bool contiguous = true;
                for (int i = 0; i < slots.Count; i++)
                {
                    if (slots[i].slotIndex != i) { contiguous = false; break; }
                }
                if (!contiguous) continue;

                // 唯一材质（首次出现顺序）。Unique materials in first-occurrence order.
                var unique = new List<Material>();
                var remap = new Dictionary<int, int>();
                foreach (var s in slots)
                {
                    int idx = unique.IndexOf(s.material);
                    if (idx < 0)
                    {
                        idx = unique.Count;
                        unique.Add(s.material);
                    }
                    remap[s.slotIndex] = idx;
                }
                if (unique.Count == slots.Count) continue; // 无可合并。Nothing to merge.

                // 动画安全：绑定必须都在临时剪辑上。Animation safety: bindings must live on temporary clips.
                if (!AllBindingsTemporary(ctx, r))
                {
                    ATOLog.Warn(string.Format(ATOLocalization.Tr("warn.slotMergeSkipped"), r.name));
                    continue;
                }

                // 网格（优先复用已克隆网格）。Mesh (reuse the clone if any).
                var mesh = mesh0;
                Mesh newMesh;
                if (!ctx.meshReplacements.TryGetValue(mesh, out newMesh))
                {
                    newMesh = Object.Instantiate(mesh);
                    newMesh.name = mesh.name + "_ATO";
                    ctx.ndmf.ObjectRegistry.RegisterReplacedObject(mesh, newMesh);
                    ctx.meshReplacements[mesh] = newMesh;
                    if (r is SkinnedMeshRenderer sr2) sr2.sharedMesh = newMesh;
                    else
                    {
                        var mf2 = r.GetComponent<MeshFilter>();
                        if (mf2 != null) mf2.sharedMesh = newMesh;
                    }
                }

                // 子网格重建：按新材质索引合并三角形（槽位与子网格一一对应，直接按槽位索引映射）。
                // Submesh rebuild: concatenate triangles by new material index (slots map 1:1 to submeshes here).
                int oldSubMeshCount = mesh.subMeshCount;
                var newTris = new List<int>[unique.Count];
                for (int i = 0; i < unique.Count; i++) newTris[i] = new List<int>();
                for (int sub = 0; sub < oldSubMeshCount; sub++)
                {
                    int newIdx = remap[sub];
                    var tris = mesh.GetTriangles(sub);
                    newTris[newIdx].AddRange(tris);
                }
                newMesh.subMeshCount = unique.Count;
                for (int i = 0; i < unique.Count; i++)
                {
                    newMesh.SetTriangles(newTris[i].ToArray(), i);
                }
                newMesh.UploadMeshData(false);

                // 渲染器材质数组。Renderer material array.
                r.sharedMaterials = unique.ToArray();

                // 更新槽位索引并重写动画绑定。Update slot indices and rewrite animation bindings.
                foreach (var s in slots)
                {
                    int newIdx;
                    if (remap.TryGetValue(s.slotIndex, out newIdx)) s.slotIndex = newIdx;
                }
                RewriteBindings(ctx, r, remap);
                mergedRenderers++;
                mergedSlots += slots.Count - unique.Count;
            }

            stage.AddLine(string.Format(ATOLocalization.Tr("log.slotMerge"), mergedRenderers, mergedSlots));
        }

        private static Mesh GetMesh(Renderer r)
        {
            var sr = r as SkinnedMeshRenderer;
            if (sr != null) return sr.sharedMesh;
            var mr = r as MeshRenderer;
            if (mr == null) return null;
            var mf = mr.GetComponent<MeshFilter>();
            return mf != null ? mf.sharedMesh : null;
        }

        // 所有涉及该渲染器材质槽绑定的剪辑是否全部为临时资产。Whether every clip binding this renderer's slots is temporary.
        private static bool AllBindingsTemporary(ATOContext ctx, Renderer r)
        {
            foreach (var kv in ctx.animations.clipRefs)
            {
                var clip = kv.Key;
                Transform baseT;
                if (!ctx.animations.clipBase.TryGetValue(clip, out baseT)) continue;
                foreach (var binding in AnimationUtility.GetCurveBindings(clip))
                {
                    if (binding.type != typeof(Material)) continue;
                    var m = SlotPattern.Match(binding.propertyName);
                    if (!m.Success) continue;
                    var target = ResolvePath(baseT, binding.path);
                    if (target != null && target.GetComponent<Renderer>() == r)
                    {
                        if (!ctx.ndmf.IsTemporaryAsset(clip)) return false;
                        break;
                    }
                }
                foreach (var binding in AnimationUtility.GetObjectReferenceCurveBindings(clip))
                {
                    if (binding.type != typeof(Material)) continue;
                    var m = SlotPattern.Match(binding.propertyName);
                    if (!m.Success) continue;
                    var target = ResolvePath(baseT, binding.path);
                    if (target != null && target.GetComponent<Renderer>() == r)
                    {
                        if (!ctx.ndmf.IsTemporaryAsset(clip)) return false;
                        break;
                    }
                }
            }
            return true;
        }

        // 重写临时剪辑上的槽位索引绑定。Rewrites slot-index bindings on temporary clips.
        private static void RewriteBindings(ATOContext ctx, Renderer r, Dictionary<int, int> remap)
        {
            foreach (var kv in ctx.animations.clipRefs)
            {
                var clip = kv.Key;
                if (!ctx.ndmf.IsTemporaryAsset(clip)) continue;
                Transform baseT;
                if (!ctx.animations.clipBase.TryGetValue(clip, out baseT)) continue;

                foreach (var binding in AnimationUtility.GetCurveBindings(clip))
                {
                    if (binding.type != typeof(Material)) continue;
                    var m = SlotPattern.Match(binding.propertyName);
                    if (!m.Success) continue;
                    int oldIdx;
                    if (!int.TryParse(m.Groups[1].Value, out oldIdx)) continue;
                    int newIdx;
                    if (!remap.TryGetValue(oldIdx, out newIdx) || newIdx == oldIdx) continue;
                    var target = ResolvePath(baseT, binding.path);
                    if (target == null || target.GetComponent<Renderer>() != r) continue;
                    var curve = AnimationUtility.GetEditorCurve(clip, binding);
                    var newBinding = binding;
                    newBinding.propertyName = "m_Materials.Array.data[" + newIdx + "]" +
                        (string.IsNullOrEmpty(m.Groups[2].Value) ? "" : "." + m.Groups[2].Value);
                    AnimationUtility.SetEditorCurve(clip, binding, null);
                    AnimationUtility.SetEditorCurve(clip, newBinding, curve);
                }

                foreach (var binding in AnimationUtility.GetObjectReferenceCurveBindings(clip))
                {
                    if (binding.type != typeof(Material)) continue;
                    var m = SlotPattern.Match(binding.propertyName);
                    if (!m.Success) continue;
                    int oldIdx;
                    if (!int.TryParse(m.Groups[1].Value, out oldIdx)) continue;
                    int newIdx;
                    if (!remap.TryGetValue(oldIdx, out newIdx) || newIdx == oldIdx) continue;
                    var target = ResolvePath(baseT, binding.path);
                    if (target == null || target.GetComponent<Renderer>() != r) continue;
                    var curve = AnimationUtility.GetObjectReferenceCurve(clip, binding);
                    var newBinding = binding;
                    newBinding.propertyName = "m_Materials.Array.data[" + newIdx + "]" +
                        (string.IsNullOrEmpty(m.Groups[2].Value) ? "" : "." + m.Groups[2].Value);
                    AnimationUtility.SetObjectReferenceCurve(clip, binding, null);
                    AnimationUtility.SetObjectReferenceCurve(clip, newBinding, curve);
                }
            }
        }

        private static Transform ResolvePath(Transform baseT, string path)
        {
            if (string.IsNullOrEmpty(path)) return baseT;
            return baseT.Find(path);
        }
    }
}
