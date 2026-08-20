// ATODedup.cs — 材质去重与材质槽合并 / Material deduplication & slot merging.
// 说明：优化后对内容与参数完全相同的材质去重并更新引用；多材质槽网格内相同的不透明材质合并时
// 同步合并材质槽与网格子网格（更新动画引用与槽索引）。安全约束（保守）：
//  - 材质存在资产级动画（path 为空的材质属性曲线）→ 不参与去重
//  - 渲染器存在槽动画/材质属性动画/贴图切换动画 → 该渲染器的槽不合并
// Note: post-optimization material dedup (identical content & parameters) with reference updates; identical opaque
// materials on multi-slot meshes also merge slots AND mesh submeshes (updating animation refs & slot indices).
// Conservative safety rules: materials with asset-level animations (empty-path material curves) are never merged;
// renderers with slot/material/texture animations never merge their slots.

using System;
using System.Collections.Generic;
using System.Text;
using nadena.dev.ndmf;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Fosa.AvatarTextureOptimizer
{
    /// <summary>材质去重与槽合并。/ Material dedup & slot merge.</summary>
    internal sealed class ATODedup
    {
        /// <summary>材质替换映射（去重结果）。/ Material replacement map (dedup results).</summary>
        public Dictionary<Material, Material> MaterialReplacements { get; } = new Dictionary<Material, Material>();

        /// <summary>槽重绑：(渲染器路径, 旧槽) → 新槽。/ Slot rebinds: (renderer path, old slot) → new slot.</summary>
        public Dictionary<(string, int), int> SlotRebinds { get; } = new Dictionary<(string, int), int>();

        /// <summary>合并的材质槽数量（报告）。/ Number of merged slots (reporting).</summary>
        public int MergedSlots;

        private List<ATOIsland> AllIslands = new List<ATOIsland>();

        private readonly bool _deduplicateMaterials;
        private readonly ATOAnimationData _anim;

        public ATODedup(bool deduplicateMaterials, ATOAnimationData anim)
        {
            _deduplicateMaterials = deduplicateMaterials;
            _anim = anim;
        }

        /// <summary>材质是否被资产级动画绑定（path 为空）。/ Whether a material has asset-level animated bindings (empty path).</summary>
        private bool HasAssetLevelAnimation(Material m)
        {
            if (_anim == null) return false;
            if (_anim.floatPropsByMaterial.TryGetValue(m, out var set) && set.Count > 0) return true;
            foreach (var kv in _anim.animatedTexturesByMaterial)
            {
                if (kv.Key.mat == m) return true;
            }
            return false;
        }

        /// <summary>
        /// 执行材质去重（内容 + 参数完全相同的材质合并；动画约束见文件头）。
        /// effectiveMaterials: 原材质 → 优化后的克隆（去重按"优化后"的内容与参数比较）。
        /// Run material dedup (identical content & parameters; animation constraints in the file header).
        /// effectiveMaterials: original → optimized clone (dedup compares the POST-optimization content).
        /// </summary>
        public void DeduplicateMaterials(List<ATORendererInfo> renderers, BuildContext context,
            Dictionary<Material, Material> effectiveMaterials)
        {
            if (!_deduplicateMaterials) return;

            // 全部参与材质（槽内去重）/ all participating materials (slot-level distinct)
            var all = new HashSet<Material>();
            foreach (var renderer in renderers)
                foreach (var slot in renderer.slots)
                    foreach (var m in slot)
                        if (m != null) all.Add(m);

            Material Effective(Material m) =>
                effectiveMaterials != null && effectiveMaterials.TryGetValue(m, out var clone) ? clone : m;

            var groups = new Dictionary<string, List<Material>>();
            foreach (var m in all)
            {
                var key = MaterialContentKey(Effective(m));
                if (key == null) continue;
                if (!groups.TryGetValue(key, out var list))
                {
                    list = new List<Material>();
                    groups[key] = list;
                }
                list.Add(m);
            }

            foreach (var kv in groups)
            {
                var list = kv.Value;
                if (list.Count < 2) continue;
                var rep = list[0];
                for (int i = 1; i < list.Count; i++)
                {
                    var m = list[i];
                    // 资产级动画的材质不合并 / materials with asset-level animations never merge
                    if (HasAssetLevelAnimation(m) || HasAssetLevelAnimation(rep)) continue;
                    MaterialReplacements[m] = Effective(rep);
                    ATOLog.Verbose($"Dedup material '{m.name}' → '{rep.name}'");
                }
            }

            if (MaterialReplacements.Count > 0)
                ATOLog.Info($"Material dedup: {MaterialReplacements.Count} materials merged. (材质去重：合并 {MaterialReplacements.Count} 个)");
        }

        /// <summary>
        /// 合并多材质槽网格内相同的（不透明）材质槽：合并槽 + 合并子网格 + 重绑动画。
        /// Merge identical (opaque) material slots on multi-slot meshes: merge slots + submeshes + rebind animations.
        /// </summary>
        public void MergeSlots(List<ATORendererInfo> renderers, BuildContext context)
        {
            MergeSlots(renderers, context, null);
        }

        /// <summary>带岛列表的槽合并（同步岛引用到合并后的网格）。/ Slot merge with the island list (syncs island refs to the merged mesh).</summary>
        public void MergeSlots(List<ATORendererInfo> renderers, BuildContext context, List<ATOIsland> allIslands)
        {
            AllIslands = allIslands ?? new List<ATOIsland>();
            if (!_deduplicateMaterials) return;

            foreach (var renderer in renderers)
            {
                var shared = renderer.renderer.sharedMaterials;
                if (shared.Length < 2) continue;

                // 动画独立性检查 / animation-independence checks
                var path = renderer.path;
                bool animated = false;
                if (_anim != null)
                {
                    animated |= _anim.slotAnimsByPath.TryGetValue(path, out var s1) && s1.Count > 0;
                    animated |= _anim.slotMaterialsByPath.TryGetValue(path, out var l1) && l1.Count > 0;
                    animated |= _anim.floatPropsByPath.TryGetValue(path, out var p1) && p1.Count > 0;
                    animated |= _anim.animatedTexturesByPath.ContainsKey((path, null));
                    foreach (var kv in _anim.animatedTexturesByPath)
                        if (kv.Key.path == path) { animated = true; break; }
                }
                if (animated) continue;

                // 可合并判定：两槽材质相同（去重后）且不透明 / mergeable: same material (post-dedup) & opaque
                var mapped = new Material[shared.Length];
                for (int i = 0; i < shared.Length; i++)
                {
                    var m = shared[i];
                    if (m != null && MaterialReplacements.TryGetValue(m, out var rep)) m = rep;
                    mapped[i] = m;
                }
                if (!IsMergeable(mapped)) continue;

                // 构建 旧槽 → 新槽 / old slot → new slot
                var firstIdx = new Dictionary<Material, int>();
                var newSlots = new List<Material>();
                var remap = new int[shared.Length];
                for (int i = 0; i < mapped.Length; i++)
                {
                    var m = mapped[i];
                    if (m == null)
                    {
                        remap[i] = -1;
                        continue;
                    }
                    if (!firstIdx.TryGetValue(m, out var ni))
                    {
                        ni = newSlots.Count;
                        firstIdx[m] = ni;
                        newSlots.Add(m);
                    }
                    remap[i] = ni;
                }
                if (newSlots.Count >= shared.Length) continue; // 无合并 / nothing to merge

                // 合并子网格（三角形拼接）/ merge submeshes (triangle concatenation)
                var oldMesh = renderer.mesh;
                var newMesh = MergeSubmeshes(renderer.mesh, remap, newSlots.Count);
                if (newMesh != null)
                {
                    nadena.dev.ndmf.ObjectRegistry.RegisterReplacedObject(renderer.mesh, newMesh);
                    if (renderer.renderer is SkinnedMeshRenderer smr) smr.sharedMesh = newMesh;
                    else
                    {
                        var mf = renderer.renderer.GetComponent<MeshFilter>();
                        if (mf != null) mf.sharedMesh = newMesh;
                    }
                    // 同步渲染器信息与岛引用（后续网格 UV 写入必须作用于合并后的网格，避免重复注册替换）/
                    // sync the renderer info & island refs (the later mesh-UV writer must act on the merged mesh to avoid duplicate replacement registrations)
                    renderer.mesh = newMesh;
                    foreach (var island in AllIslands)
                    {
                        if (island.mesh == oldMesh) island.mesh = newMesh;
                    }
                }

                renderer.renderer.sharedMaterials = newSlots.ToArray();
                MergedSlots += shared.Length - newSlots.Count;
                for (int i = 0; i < remap.Length; i++)
                {
                    if (remap[i] != i)
                        SlotRebinds[(path, i)] = remap[i];
                }
                ATOLog.Verbose($"Merged slots on '{path}': {shared.Length} → {newSlots.Count}");
            }
        }

        /// <summary>是否可合并（全部槽同材质可两两归并、且全不透明）。/ Mergeable (duplicate materials exist & all opaque).</summary>
        private static bool IsMergeable(Material[] mapped)
        {
            var seen = new HashSet<Material>();
            bool dup = false;
            foreach (var m in mapped)
            {
                if (m == null) return false; // 空槽不参与合并 / null slots never merge
                if (!seen.Add(m)) dup = true;
            }
            if (!dup) return false;
            foreach (var m in seen)
                if (IsTransparent(m)) return false;
            return true;
        }

        /// <summary>材质是否透明（含关键字动画的保守判定）。/ Whether a material is transparent (conservative with keyword animation).</summary>
        private static bool IsTransparent(Material m)
        {
            var alpha = ATOAlphaUsage.Opaque;
            foreach (var k in m.shaderKeywords)
            {
                var u = k.ToUpperInvariant();
                if (u.Contains("_ALPHATEST_ON") || u.Contains("_CUTOFF") || u.Contains("_ALPHACLIP")) alpha |= ATOAlphaUsage.Cutout;
                if (u.Contains("_ALPHABLEND_ON") || u.Contains("_ALPHAPREMULTIPLY_ON") || u.Contains("_SURFACE_TYPE_TRANSPARENT")) alpha |= ATOAlphaUsage.Blend;
            }
            var name = m.shader.name.ToLowerInvariant();
            if (name.Contains("liltoon") || name.StartsWith("lts"))
            {
                if (name.Contains("cutout") || name.Contains("trans") || name.Contains("fake")) return true;
            }
            if (m.HasProperty("_Mode") && Mathf.RoundToInt(m.GetFloat("_Mode")) >= 1) return true;
            if (m.HasProperty("_Surface") && Mathf.Abs(m.GetFloat("_Surface") - 1f) < 0.01f) return true;
            return alpha != ATOAlphaUsage.Opaque;
        }

        /// <summary>合并子网格。/ Merge submeshes.</summary>
        private static Mesh MergeSubmeshes(Mesh mesh, int[] remap, int newCount)
        {
            if (mesh.subMeshCount != remap.Length) return null;
            var submeshCount = mesh.subMeshCount;
            var newMesh = Object.Instantiate(mesh);
            newMesh.name = mesh.name;
            newMesh.subMeshCount = newCount;
            var outTris = new List<int>[newCount];
            for (int i = 0; i < newCount; i++) outTris[i] = new List<int>();
            for (int i = 0; i < submeshCount; i++)
            {
                if (remap[i] < 0) return null;
                outTris[remap[i]].AddRange(mesh.GetTriangles(i));
            }
            for (int i = 0; i < newCount; i++)
                newMesh.SetTriangles(outTris[i], i);
            return newMesh;
        }

        /// <summary>材质内容键（着色器 + 关键字 + 全部属性 + 渲染参数）。/ Material content key (shader + keywords + all props + render params).</summary>
        public static string MaterialContentKey(Material m)
        {
            if (m == null || m.shader == null) return null;
            var sb = new StringBuilder();
            sb.Append(AssetDatabase.GetAssetPath(m.shader)).Append('|').Append(m.shader.name).Append('|');
            var keywords = new List<string>(m.shaderKeywords);
            keywords.Sort();
            foreach (var k in keywords) sb.Append(k).Append(';');
            sb.Append('|');
            // 全部已保存属性 / all saved properties
            var so = new SerializedObject(m);
            var floats = so.FindProperty("m_SavedProperties.m_Floats");
            AppendPairs(sb, floats);
            var colors = so.FindProperty("m_SavedProperties.m_Colors");
            AppendPairs(sb, colors);
            var texEnvs = so.FindProperty("m_SavedProperties.m_TexEnvs");
            if (texEnvs != null && texEnvs.isArray)
            {
                for (int i = 0; i < texEnvs.arraySize; i++)
                {
                    var p = texEnvs.GetArrayElementAtIndex(i);
                    var name = p.FindPropertyRelative("first.name")?.stringValue;
                    var tex = p.FindPropertyRelative("second.m_Texture")?.objectReferenceValue;
                    sb.Append("T:").Append(name).Append('=').Append(tex != null ? tex.name + ":" + tex.GetInstanceID() : "null").Append(';');
                }
            }
            so.Dispose();
            sb.Append("|rq").Append(m.renderQueue)
              .Append("|gi").Append((int)m.globalIlluminationFlags)
              .Append("|ds").Append(m.doubleSidedGI ? 1 : 0)
              .Append("|ei").Append(m.enableInstancing ? 1 : 0);
            return sb.ToString();
        }

        private static void AppendPairs(StringBuilder sb, SerializedProperty arrayProp)
        {
            if (arrayProp == null || !arrayProp.isArray) return;
            var entries = new List<string>();
            for (int i = 0; i < arrayProp.arraySize; i++)
            {
                var p = arrayProp.GetArrayElementAtIndex(i);
                var name = p.FindPropertyRelative("first.name")?.stringValue;
                var val = p.FindPropertyRelative("second");
                if (name == null || val == null) continue;
                switch (val.propertyType)
                {
                    case SerializedPropertyType.Float: entries.Add(name + "=" + val.floatValue.ToString("R")); break;
                    case SerializedPropertyType.Color:
                        var c = val.colorValue;
                        entries.Add(name + "=" + $"{c.r:R},{c.g:R},{c.b:R},{c.a:R}");
                        break;
                    case SerializedPropertyType.Vector4:
                        var v = val.vector4Value;
                        entries.Add(name + "=" + $"{v.x:R},{v.y:R},{v.z:R},{v.w:R}");
                        break;
                    case SerializedPropertyType.Integer: entries.Add(name + "=" + val.intValue); break;
                }
            }
            entries.Sort();
            foreach (var e in entries) sb.Append(e).Append(';');
        }
    }
}
