using System.Collections.Generic;
using UnityEngine;

namespace Fosa.AvatarTextureOptimizer.Editor.Analysis
{
    // 材质槽扫描器：遍历 Avatar 上所有 Renderer 的材质槽。
    // 仅处理 SkinnedMeshRenderer 与 MeshRenderer；跳过 EditorOnly 子树；记录网格引用供后续岛提取。
    // Material slot scanner: iterates all renderer material slots on the avatar.
    // Only SkinnedMeshRenderer and MeshRenderer are processed; EditorOnly subtrees are skipped.
    internal static class MaterialSlotScanner
    {
        public static void Scan(ATOContext ctx, ATOReport.Stage stage)
        {
            // EditorOnly 子树集合（NDMF 在 Resolving 已删除，这里做防御性检查）。EditorOnly subtree set (defensive; NDMF already removed them in Resolving).
            var editorOnly = new HashSet<Transform>();
            foreach (var t in ctx.avatarRoot.GetComponentsInChildren<Transform>(true))
            {
                var p = t;
                bool isEo = false;
                while (p != null)
                {
                    if (p.CompareTag("EditorOnly")) { isEo = true; break; }
                    p = p.parent;
                }
                if (isEo) editorOnly.Add(t);
            }

            var renderers = ctx.avatarRoot.GetComponentsInChildren<Renderer>(true);
            int skippedNonMesh = 0, skippedNoMesh = 0;
            foreach (var r in renderers)
            {
                ctx.CheckCancelled();
                if (editorOnly.Contains(r.transform))
                {
                    stage.AddLine(string.Format(ATOLocalization.Tr("log.skipEditorOnly"), r.name));
                    continue;
                }

                bool isSkinned = r is SkinnedMeshRenderer;
                bool isMeshR = r is MeshRenderer;
                if (!isSkinned && !isMeshR)
                {
                    // 其他渲染器类型（粒子等）不处理。Other renderer types (particles, etc.) are ignored.
                    skippedNonMesh++;
                    continue;
                }

                Mesh mesh = null;
                if (isSkinned) mesh = ((SkinnedMeshRenderer)r).sharedMesh;
                else
                {
                    var mf = r.GetComponent<MeshFilter>();
                    if (mf != null) mesh = mf.sharedMesh;
                }
                if (mesh == null)
                {
                    stage.AddLine(string.Format(ATOLocalization.Tr("warn.noMesh"), r.name));
                    skippedNoMesh++;
                    continue;
                }

                ctx.renderers.Add(r);
                var mats = r.sharedMaterials;
                // 槽位索引 = 子网格索引（Unity 规则：子网格 k 由材质 min(k, len-1) 渲染，
                // 材质数组短于子网格数时以最后一个材质填充）。这样槽位与子网格一一对应。
                // Slot index = submesh index (Unity rule: submesh k renders with material min(k, len-1);
                // short material arrays are padded with the last material). Slots map 1:1 to submeshes.
                int subMeshCount = mesh.subMeshCount;
                int slotCount = Mathf.Max(subMeshCount, mats.Length);
                for (int i = 0; i < slotCount; i++)
                {
                    Material mat = mats.Length > 0 ? mats[Mathf.Min(i, mats.Length - 1)] : null;
                    if (mat == null) continue;
                    var slot = new SlotEntry
                    {
                        renderer = r,
                        slotIndex = i,
                        material = mat,
                        mesh = mesh,
                        isSkinned = isSkinned
                    };
                    ctx.slots.Add(slot);
                    stage.AddLine(string.Format(ATOLocalization.Tr("log.slotFound"), slot.ToString()));
                }            }

            // 材质唯一化（确定性排序）。Unique materials (deterministic order).
            var matSet = new HashSet<Material>();
            foreach (var s in ctx.slots) matSet.Add(s.material);
            var matList = new List<Material>(matSet);
            matList.Sort((a, b) => string.CompareOrdinal(a.name, b.name));
            ctx.materials.AddRange(matList);

            stage.AddLine(string.Format(ATOLocalization.Tr("log.scanSummary"), renderers.Length, ctx.slots.Count, matList.Count, skippedNonMesh, skippedNoMesh));
        }
    }
}
