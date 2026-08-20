using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace Fosa.ATO.Editor
{
    /// <summary>
    /// 材质去重 + 材质槽合并 + 动画引用/索引更新。
    ///
    /// 关键事实（已读 AAO MergeMaterialSlots / OptimizeTexture 源码取证）：
    /// - AAO 用 internal 的 `RecordMoveProperties`/`GetAnimationComponent` 更新动画引用，我们不可用；
    ///   因此直接扫描并改写动画剪辑中的 `m_Materials.Array.data[i]` object reference curves。
    /// - 安全前提（需求原文）：动画中"不存在单独切换其中一个或多个材质"时才能去重/合并。
    ///
    /// Material dedup + slot merge + animation reference/index rewrite (self-contained, no AAO internal).
    /// </summary>
    public class ATODedup
    {
        private readonly nadena.dev.ndmf.BuildContext _ctx;
        private readonly ATOBuildData _data;
        private readonly AvatarTextureOptimizer _comp;

        // 被动画单独切换的材质槽：(renderer, slotIndex)。
        // Material slots switched by animation (must not be merged).
        private HashSet<(Renderer, int)> _animatedSlots;

        // 被动画修改属性的材质（material._XXX 曲线）。Materials with animated properties.
        private HashSet<Material> _animatedMaterials;

        public ATODedup(nadena.dev.ndmf.BuildContext ctx, ATOBuildData data)
        {
            _ctx = ctx;
            _data = data;
            _comp = data.component;
        }

        public void Run()
        {
            if (!_comp.dedupMaterials && !_comp.dedupTextures) return;
            using var step = ATOLogger.Step("Dedup materials & merge slots");

            ScanAnimatedRefs();

            if (_comp.dedupMaterials)
                DedupMaterialAssets();

            if (_comp.dedupMaterials)
                MergeMaterialSlots();
        }

        // ---- 扫描动画引用 ----
        private void ScanAnimatedRefs()
        {
            _animatedSlots = new HashSet<(Renderer, int)>();
            _animatedMaterials = new HashSet<Material>();

            foreach (var animator in _ctx.AvatarRootObject.GetComponentsInChildren<Animator>(true))
            {
                var controller = animator.runtimeAnimatorController;
                if (controller == null) continue;
                foreach (var clip in controller.animationClips)
                {
                    if (clip == null) continue;

                    // 对象引用曲线：材质切换。Object ref curves: material switches.
                    foreach (var binding in AnimationUtility.GetObjectReferenceCurveBindings(clip))
                    {
                        if (!binding.propertyName.StartsWith("m_Materials.Array.data[")) continue;
                        int idx = ParseSlotIndex(binding.propertyName);
                        var go = FindAtPath(_ctx.AvatarRootObject, binding.path);
                        var renderer = go?.GetComponent<Renderer>();
                        if (renderer != null && idx >= 0)
                            _animatedSlots.Add((renderer, idx));
                    }

                    // 编辑器曲线：材质属性修改（material._XXX）。Editor curves: material property animation.
                    foreach (var binding in AnimationUtility.GetCurveBindings(clip))
                    {
                        if (!binding.propertyName.StartsWith("material.")) continue;
                        var go = FindAtPath(_ctx.AvatarRootObject, binding.path);
                        var renderer = go?.GetComponent<Renderer>();
                        if (renderer != null && renderer.sharedMaterials != null)
                            foreach (var m in renderer.sharedMaterials)
                                if (m != null) _animatedMaterials.Add(m);
                    }
                }
            }
        }

        // ---- 材质资产去重 ----
        private void DedupMaterialAssets()
        {
            // 收集所有材质（渲染器 + 动画引用）。
            var all = new HashSet<Material>();
            foreach (var renderer in _data.renderers)
                if (renderer.sharedMaterials != null)
                    foreach (var m in renderer.sharedMaterials)
                        if (m != null) all.Add(m);
            // 动画中引用的材质也要纳入（作为可能被替换的目标）。
            foreach (var animator in _ctx.AvatarRootObject.GetComponentsInChildren<Animator>(true))
            {
                var controller = animator.runtimeAnimatorController;
                if (controller == null) continue;
                foreach (var clip in controller.animationClips)
                {
                    if (clip == null) continue;
                    foreach (var binding in AnimationUtility.GetObjectReferenceCurveBindings(clip))
                    {
                        var curve = AnimationUtility.GetObjectReferenceCurve(clip, binding);
                        if (curve == null) continue;
                        foreach (var kv in curve)
                            if (kv.value is Material m) all.Add(m);
                    }
                }
            }

            // 指纹分组。
            var byFingerprint = new Dictionary<string, List<Material>>();
            foreach (var m in all)
            {
                if (_animatedMaterials.Contains(m)) continue; // 被动画修改属性的材质不参与合并
                var fp = MaterialFingerprint(m);
                if (!byFingerprint.TryGetValue(fp, out var list))
                    byFingerprint[fp] = list = new List<Material>();
                list.Add(m);
            }

            // 建立替换表。Build replacement map.
            var replace = new Dictionary<Material, Material>();
            foreach (var kv in byFingerprint)
            {
                var list = kv.Value;
                if (list.Count < 2) continue;
                var canonical = list[0];
                for (int i = 1; i < list.Count; i++)
                {
                    var m = list[i];
                    // 安全前提：canonical 与 m 都不能被动画单独切换（槽级）。
                    if (IsSwitchedIndependently(canonical, m)) continue;
                    replace[m] = canonical;
                    ATOLogger.VerboseLog($"Material dedup: {m.name} -> {canonical.name}");
                }
            }

            if (replace.Count == 0) return;

            // 更新渲染器引用。Update renderer references.
            foreach (var renderer in _data.renderers)
            {
                var mats = renderer.sharedMaterials;
                bool changed = false;
                for (int i = 0; i < mats.Length; i++)
                    if (mats[i] != null && replace.TryGetValue(mats[i], out var c))
                    {
                        mats[i] = c;
                        changed = true;
                    }
                if (changed) renderer.sharedMaterials = mats;
            }

            // 更新动画对象引用曲线。Update animation object-reference curves.
            foreach (var animator in _ctx.AvatarRootObject.GetComponentsInChildren<Animator>(true))
            {
                var controller = animator.runtimeAnimatorController;
                if (controller == null) continue;
                foreach (var clip in controller.animationClips)
                {
                    if (clip == null) continue;
                    foreach (var binding in AnimationUtility.GetObjectReferenceCurveBindings(clip))
                    {
                        var curve = AnimationUtility.GetObjectReferenceCurve(clip, binding);
                        if (curve == null) continue;
                        bool changed = false;
                        for (int i = 0; i < curve.Length; i++)
                            if (curve[i].value is Material m && replace.TryGetValue(m, out var c))
                            {
                                curve[i].value = c;
                                changed = true;
                            }
                        if (changed) AnimationUtility.SetObjectReferenceCurve(clip, binding, curve);
                    }
                }
            }
        }

        /// <summary>两个材质是否被动画"单独切换"（安全前提检查）。</summary>
        private bool IsSwitchedIndependently(Material a, Material b)
        {
            // 若两个材质分别出现在不同动画槽中，说明可能被单独切换。
            var slotsA = new HashSet<(Renderer, int)>();
            var slotsB = new HashSet<(Renderer, int)>();
            foreach (var (r, i) in _animatedSlots)
            {
                if (r.sharedMaterials != null && i < r.sharedMaterials.Length)
                {
                    if (r.sharedMaterials[i] == a) slotsA.Add((r, i));
                    if (r.sharedMaterials[i] == b) slotsB.Add((r, i));
                }
            }
            return slotsA.Count > 0 && slotsB.Count > 0;
        }

        // ---- 材质槽合并 ----
        private void MergeMaterialSlots()
        {
            foreach (var renderer in _data.renderers)
            {
                var mats = renderer.sharedMaterials;
                if (mats == null || mats.Length < 2) continue;

                // 建立槽 → 首个相同材质槽的映射。
                var firstOccurrence = new Dictionary<Material, int>();
                var newMats = new List<Material>();
                var slotMap = new int[mats.Length]; // 旧槽 → 新槽（-1 表示删除）

                for (int i = 0; i < mats.Length; i++)
                {
                    var m = mats[i];
                    if (m == null) { newMats.Add(null); slotMap[i] = newMats.Count - 1; continue; }

                    if (_animatedSlots.Contains((renderer, i)))
                    {
                        // 被动画切换的槽不能合并。
                        newMats.Add(m);
                        slotMap[i] = newMats.Count - 1;
                        continue;
                    }

                    if (firstOccurrence.TryGetValue(m, out var first))
                    {
                        slotMap[i] = first; // 合并到首个相同材质的槽
                    }
                    else
                    {
                        firstOccurrence[m] = newMats.Count;
                        newMats.Add(m);
                        slotMap[i] = newMats.Count - 1;
                    }
                }

                if (newMats.Count == mats.Length) continue; // 无合并

                // 合并 submesh。Merge submeshes.
                MergeSubmeshes(renderer, slotMap, newMats.Count);

                // 更新动画索引。Update animation slot indices.
                RewriteAnimationSlotIndices(renderer, slotMap);

                renderer.sharedMaterials = newMats.ToArray();
                ATOLogger.VerboseLog($"Merged material slots on {renderer.name}: {mats.Length} -> {newMats.Count}");
            }
        }

        private void MergeSubmeshes(Renderer renderer, int[] slotMap, int newCount)
        {
            Mesh mesh = renderer is SkinnedMeshRenderer smr ? smr.sharedMesh
                      : renderer is MeshRenderer mr ? mr.GetComponent<MeshFilter>()?.sharedMesh
                      : null;
            if (mesh == null || mesh.subMeshCount <= 1) return;

            var newSubmeshes = new List<int[]>[newCount];
            for (int i = 0; i < newCount; i++) newSubmeshes[i] = new List<int[]>();

            for (int old = 0; old < mesh.subMeshCount; old++)
            {
                if (old >= slotMap.Length) break;
                int target = slotMap[old];
                if (target < 0) continue;
                newSubmeshes[target].Add(mesh.GetTriangles(old));
            }

            mesh.subMeshCount = newCount;
            for (int i = 0; i < newCount; i++)
            {
                var tris = new List<int>();
                foreach (var t in newSubmeshes[i]) tris.AddRange(t);
                mesh.SetTriangles(tris.ToArray(), i);
            }
        }

        private void RewriteAnimationSlotIndices(Renderer renderer, int[] slotMap)
        {
            foreach (var animator in _ctx.AvatarRootObject.GetComponentsInChildren<Animator>(true))
            {
                var controller = animator.runtimeAnimatorController;
                if (controller == null) continue;
                foreach (var clip in controller.animationClips)
                {
                    if (clip == null) continue;
                    foreach (var binding in AnimationUtility.GetObjectReferenceCurveBindings(clip))
                    {
                        if (!binding.propertyName.StartsWith("m_Materials.Array.data[")) continue;
                        if (binding.path != GetPath(_ctx.AvatarRootObject, renderer.transform)) continue;

                        int oldIdx = ParseSlotIndex(binding.propertyName);
                        if (oldIdx < 0 || oldIdx >= slotMap.Length) continue;
                        int newIdx = slotMap[oldIdx];
                        if (newIdx < 0) continue;
                        if (newIdx == oldIdx) continue;

                        var curve = AnimationUtility.GetObjectReferenceCurve(clip, binding);
                        AnimationUtility.SetObjectReferenceCurve(clip, binding, null); // 移除旧绑定

                        var newBinding = new EditorCurveBinding
                        {
                            path = binding.path,
                            type = binding.type,
                            propertyName = $"m_Materials.Array.data[{newIdx}]",
                        };
                        AnimationUtility.SetObjectReferenceCurve(clip, newBinding, curve);
                    }
                }
            }
        }

        // ---- 工具 ----
        private static int ParseSlotIndex(string propName)
        {
            const string prefix = "m_Materials.Array.data[";
            if (!propName.StartsWith(prefix)) return -1;
            int end = propName.IndexOf(']', prefix.Length);
            if (end < 0) return -1;
            return int.TryParse(propName.Substring(prefix.Length, end - prefix.Length), out var idx) ? idx : -1;
        }

        private static string GetPath(GameObject root, Transform t)
        {
            if (t == root.transform) return "";
            var parts = new List<string>();
            while (t != root.transform && t != null)
            {
                parts.Insert(0, t.name);
                t = t.parent;
            }
            return string.Join("/", parts);
        }

        private static GameObject FindAtPath(GameObject root, string path)
        {
            if (string.IsNullOrEmpty(path)) return root;
            var t = root.transform.Find(path);
            return t != null ? t.gameObject : null;
        }

        private static string MaterialFingerprint(Material m)
        {
            var shader = m.shader;
            if (shader == null) return m.name;
            var sb = new StringBuilder(shader.name);
            sb.Append('|');
            try
            {
                int cnt = ShaderUtil.GetPropertyCount(shader);
                for (int i = 0; i < cnt; i++)
                {
                    var name = ShaderUtil.GetPropertyName(shader, i);
                    var type = ShaderUtil.GetPropertyType(shader, i);
                    switch (type)
                    {
                        case ShaderUtil.ShaderPropertyType.Color:
                            sb.Append(name).Append('=').Append(m.GetColor(name)).Append(';'); break;
                        case ShaderUtil.ShaderPropertyType.Float:
                        case ShaderUtil.ShaderPropertyType.Range:
                            sb.Append(name).Append('=').Append(m.GetFloat(name)).Append(';'); break;
                        case ShaderUtil.ShaderPropertyType.Vector:
                            sb.Append(name).Append('=').Append(m.GetVector(name)).Append(';'); break;
                        case ShaderUtil.ShaderPropertyType.TexEnv:
                            sb.Append(name).Append('=')
                              .Append(m.GetTexture(name) ? m.GetTexture(name).GetInstanceID() : 0).Append(';');
                            break;
                    }
                }
            }
            catch { /* 某些着色器枚举可能失败 */ }
            foreach (var k in m.shaderKeywords) sb.Append(k).Append(';');
            sb.Append("rq=").Append(m.renderQueue);
            return sb.ToString();
        }
    }
}
