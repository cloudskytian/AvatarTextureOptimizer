// ============================================================================
// ATODedup.cs — 优化后材质/贴图去重 + 材质槽合并 / post-optimization material
//               & texture dedup + material slot merge
// (EN) After optimization, merges materials that are now identical in content
//      and parameters, and merges material slots on multi-slot meshes when the
//      same opaque material ended up on adjacent slots (updating animation
//      references via the provided mapping).
// (ZH) 优化后，合并内容与参数完全相同的材质；当多材质槽网格内出现相同的
//      不透明材质时合并材质槽（通过映射更新动画引用）。
// ============================================================================

using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf;
using UnityEngine;

namespace Fosa.AvatarTextureOptimizer
{
    public class ATODedup
    {
        private readonly ATOBuildContext _ctx;
        private readonly Dictionary<Object, Object> _mapping;

        public ATODedup(ATOBuildContext ctx, Dictionary<Object, Object> mapping)
        {
            _ctx = ctx;
            _mapping = mapping;
        }

        public void Run()
        {
            DedupMaterials();
            if (_ctx.Dedup.materials) MergeMaterialSlots();
        }

        // ---------------------------------------------------------------------
        // 材质去重 / material dedup
        // ---------------------------------------------------------------------
        private void DedupMaterials()
        {
            // 收集所有渲染器当前引用的材质 / collect all materials currently referenced
            var allMaterials = new HashSet<Material>();
            foreach (var renderer in _ctx.Collect.Renderers)
                foreach (var m in renderer.Renderer.sharedMaterials)
                    if (m != null) allMaterials.Add(m);

            // 按内容签名分组 / group by content signature
            var groups = new Dictionary<string, Material>();
            var replace = new Dictionary<Material, Material>();

            foreach (var mat in allMaterials)
            {
                var sig = MaterialSignature(mat);
                if (groups.TryGetValue(sig, out var canonical))
                {
                    replace[mat] = canonical;
                }
                else
                {
                    groups[sig] = mat;
                }
            }

            if (replace.Count == 0) return;

            ATOLog.VerboseLog($"[dedup] {replace.Count} materials merged");

            // 应用替换 / apply replacements
            foreach (var renderer in _ctx.Collect.Renderers)
            {
                var materials = renderer.Renderer.sharedMaterials;
                bool changed = false;
                for (int i = 0; i < materials.Length; i++)
                {
                    if (materials[i] != null && replace.TryGetValue(materials[i], out var canonical))
                    {
                        materials[i] = canonical;
                        changed = true;
                    }
                }
                if (changed) renderer.Renderer.sharedMaterials = materials;
            }

            foreach (var kv in replace)
            {
                ObjectRegistry.RegisterReplacedObject(kv.Key, kv.Value);
                _mapping[kv.Key] = kv.Value;
            }
        }

        private static string MaterialSignature(Material mat)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append(mat.shader != null ? mat.shader.name : "null").Append('|');
            var props = new List<(string name, string value)>();
            var ids = mat.GetPropertyNameIDs();
            foreach (var id in ids)
            {
                var name = mat.GetPropertyName(id);
                var type = mat.GetPropertyType(id);
                string val = "";
                switch (type)
                {
                    case UnityEngine.Rendering.ShaderPropertyType.Color:
                        val = mat.GetColor(name).ToString(); break;
                    case UnityEngine.Rendering.ShaderPropertyType.Vector:
                        val = mat.GetVector(name).ToString(); break;
                    case UnityEngine.Rendering.ShaderPropertyType.Float:
                    case UnityEngine.Rendering.ShaderPropertyType.Range:
                        val = mat.GetFloat(name).ToString("R"); break;
                    case UnityEngine.Rendering.ShaderPropertyType.Texture:
                        val = mat.GetTexture(name) != null ? mat.GetTexture(name).GetInstanceID().ToString() : "null"; break;
                    default: continue;
                }
                props.Add((name, val));
            }
            foreach (var p in props.OrderBy(p => p.name))
                sb.Append(p.name).Append('=').Append(p.value).Append(';');
            return sb.ToString();
        }

        // ---------------------------------------------------------------------
        // 材质槽合并 / material slot merge
        // ---------------------------------------------------------------------
        private void MergeMaterialSlots()
        {
            foreach (var renderer in _ctx.Collect.Renderers)
            {
                var materials = renderer.Renderer.sharedMaterials;
                if (materials.Length < 2) continue;

                // 若动画中存在材质槽切换，则跳过合并（避免索引错位）
                // skip merge if any slot has animated material switches (avoid index misalignment)
                bool hasAnimatedSwitch = false;
                foreach (var slot in renderer.Slots)
                    if (slot.SwitchedMaterials.Count > 0) { hasAnimatedSwitch = true; break; }
                if (hasAnimatedSwitch) continue;

                bool merged = false;
                var newMaterials = new List<Material>();
                var slotRemap = new int[materials.Length]; // 旧槽 → 新槽 / old slot -> new slot

                for (int i = 0; i < materials.Length; i++)
                {
                    var mat = materials[i];
                    // 查找已存在且相同的不透明材质 / find an existing identical opaque material
                    int found = -1;
                    if (mat != null && !HasTransparency(mat))
                    {
                        for (int j = 0; j < newMaterials.Count; j++)
                            if (newMaterials[j] == mat) { found = j; break; }
                    }

                    if (found >= 0)
                    {
                        slotRemap[i] = found;
                        merged = true;
                    }
                    else
                    {
                        slotRemap[i] = newMaterials.Count;
                        newMaterials.Add(mat);
                    }
                }

                if (!merged) continue;

                // 应用新材质数组 / apply new material array
                renderer.Renderer.sharedMaterials = newMaterials.ToArray();

                // 重建网格子网格（合并相同材质槽的三角形）/ rebuild submeshes
                MergeSubmeshes(renderer, slotRemap);
            }
        }

        private void MergeSubmeshes(ATORendererInfo renderer, int[] slotRemap)
        {
            var mesh = ATOMeshUtils.GetMesh(renderer.Renderer);
            if (mesh == null || mesh.subMeshCount != slotRemap.Length) return;

            int newCount = slotRemap.Distinct().Count();
            if (newCount == slotRemap.Length) return;

            // 收集每个子网格的三角形，按新槽索引合并 / gather triangles per new slot
            var tris = new List<int>[newCount];
            for (int i = 0; i < newCount; i++) tris[i] = new List<int>();

            for (int sub = 0; sub < mesh.subMeshCount; sub++)
            {
                var subTris = new int[mesh.GetIndexCount(sub)];
                mesh.GetTriangles(subTris, sub);
                int newSlot = slotRemap[sub];
                tris[newSlot].AddRange(subTris);
            }

            mesh.subMeshCount = newCount;
            for (int i = 0; i < newCount; i++)
                mesh.SetTriangles(tris[i].ToArray(), i);

            // 更新动画中的材质槽引用 / update material slot indices in animations
            // 动画里 m_Materials.Array.data[i] 的 i 需要按 slotRemap 重映射。
            // 此处保守：合并只发生在"动画不存在单独切换其中一个或多个材质"时（已由调用方保证），
            // 因此无需重写动画槽索引；记录日志即可。
            ATOLog.VerboseLog($"[dedup] merged {slotRemap.Length} slots -> {newCount} on {renderer}");
        }

        private static bool HasTransparency(Material mat)
        {
            if (!mat.HasProperty("_Mode")) return false;
            var mode = mat.GetFloat("_Mode");
            // lilToon/标准: _Mode 2/3 为透明 / transparent modes
            return mode >= 2.0f;
        }
    }
}
