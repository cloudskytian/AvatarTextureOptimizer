// Avatar Texture Optimizer (ATO)
// Rewrites animation clips after dedup/merge: object-reference remaps (material & texture
// swaps), material-property curve path remaps, and material-slot index remaps. Clips and
// controllers are cloned so the user's original assets are never mutated.
// 去重/合并后改写动画片段：对象引用重映射（材质与贴图切换）、材质属性曲线路径重映射、
// 材质槽索引重映射。片段与控制器都会被克隆，绝不修改用户原始资产。

using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace NetFosa.ATO
{
    /// <summary>
    /// Stage 7c: apply animation remaps. / 阶段 7c：应用动画重映射。
    /// </summary>
    public static class ATOAnimationRewriter
    {
        public static void Apply(ATOBuildContext build, ATOProgress progress)
        {
            var arm = build.animRemap;
            if (arm.textureRemap.Count == 0 && arm.materialRemap.Count == 0 && arm.materialCloneByRenderer.Count == 0
                && arm.slotRemap.Count == 0 && build.materialPathRemap.Count == 0)
                return;

            var clips = CollectClips(build.avatarRoot);
            progress.Begin(clips.Count);

            var clipRemap = new Dictionary<AnimationClip, AnimationClip>();
            foreach (var clip in clips)
            {
                if (clip == null) { progress.Advance(1); continue; }
                var clone = RewriteClip(build, clip);
                if (clone != null) clipRemap[clip] = clone;
                progress.Advance(1, clip.name);
            }

            if (clipRemap.Count == 0) return;

            // Update animators & legacy animations to reference cloned clips. / 让动画器引用克隆片段。
            foreach (var anim in build.avatarRoot.GetComponentsInChildren<Animator>(true))
                UpdateAnimator(build, anim, clipRemap);
            foreach (var legacy in build.avatarRoot.GetComponentsInChildren<Animation>(true))
                UpdateLegacy(build, legacy, clipRemap);
        }

        private static List<AnimationClip> CollectClips(GameObject root)
        {
            var result = new List<AnimationClip>();
            var seen = new HashSet<AnimationClip>();
            void Add(AnimationClip c) { if (c != null && seen.Add(c)) result.Add(c); }

            foreach (var anim in root.GetComponentsInChildren<Animator>(true))
            {
                var rc = anim.runtimeAnimatorController;
                if (rc == null) continue;
                if (rc is AnimatorOverrideController aoc)
                {
                    CollectFromController(aoc.runtimeAnimatorController, Add);
                    foreach (var op in aoc.overrides) { Add(op.Key); Add(op.Value); }
                }
                else CollectFromController(rc, Add);
            }
            foreach (var legacy in root.GetComponentsInChildren<Animation>(true))
            {
                foreach (var c in AnimationUtility.GetAnimationClips(legacy.gameObject)) Add(c);
                if (legacy.clip != null) Add(legacy.clip);
            }
            return result;
        }

        private static void CollectFromController(RuntimeAnimatorController rc, System.Action<AnimationClip> add)
        {
            if (rc is not AnimatorController ac) return;
            foreach (var layer in ac.layers) CollectFromStateMachine(layer.stateMachine, add);
        }

        private static void CollectFromStateMachine(AnimatorStateMachine sm, System.Action<AnimationClip> add)
        {
            foreach (var st in sm.states) CollectFromMotion(st.state.motion, add);
            foreach (var child in sm.stateMachines) CollectFromStateMachine(child.stateMachine, add);
        }

        private static void CollectFromMotion(Motion m, System.Action<AnimationClip> add)
        {
            if (m == null) return;
            if (m is AnimationClip c) { add(c); return; }
            if (m is BlendTree bt) foreach (var ch in bt.children) CollectFromMotion(ch.motion, add);
        }

        private static AnimationClip RewriteClip(ATOBuildContext build, AnimationClip clip)
        {
            var arm = build.animRemap;
            bool changed = false;

            // Object-reference curves. / 对象引用曲线。
            var objRefBindings = AnimationUtility.GetObjectReferenceCurveBindings(clip);
            foreach (var b in objRefBindings)
            {
                var curve = AnimationUtility.GetObjectReferenceCurve(clip, b);
                if (curve == null) continue;
                bool bChanged = false;
                foreach (var k in curve)
                {
                    var v = k.value;
                    if (v == null) continue;
                    if (v is Texture t && arm.textureRemap.TryGetValue(t, out var nt)) { k.value = nt; bChanged = true; }
                    else if (v is Material m)
                    {
                        // Compose: original material -> base clone -> dedup canonical / per-renderer clone.
                        // 组合解析：原始材质 -> 基础克隆 -> 去重规范实例 / 逐渲染器克隆。
                        var resolved = m;
                        if (build.baseMaterialClone.TryGetValue(m, out var bc)) resolved = bc;
                        if (arm.materialRemap.TryGetValue(resolved, out var nm)) { k.value = nm; bChanged = true; }
                        else if (arm.materialCloneByRenderer.TryGetValue(resolved, out var byR))
                        {
                            var rr = FindRendererByPath(build, b.path);
                            if (rr != null && byR.TryGetValue(rr.rendererId, out var clone)) { k.value = clone; bChanged = true; }
                        }
                    }
                }
                // Slot index remap for material slot swaps. / 材质槽切换的槽索引重映射。
                EditorCurveBinding newBinding = b;
                bool slotMoved = b.propertyName.StartsWith("m_Materials.Array.data[")
                    && RemapSlotProperty(build, b.path, ref newBinding.propertyName);

                if (bChanged || slotMoved)
                {
                    changed = true;
                    if (slotMoved)
                    {
                        AnimationUtility.SetObjectReferenceCurve(clip, b, null);
                        AnimationUtility.SetObjectReferenceCurve(clip, newBinding, curve);
                    }
                    else
                    {
                        AnimationUtility.SetObjectReferenceCurve(clip, b, curve);
                    }
                }
            }

            // Float/vector curves: material property paths + slot indices. / 浮点/向量曲线：材质属性路径 + 槽索引。
            var bindings = AnimationUtility.GetCurveBindings(clip);
            foreach (var b in bindings)
            {
                EditorCurveBinding newBinding = b;
                bool remapPath = b.type == typeof(Material) && build.materialPathRemap.TryGetValue(b.path, out var newPath);
                if (remapPath) newBinding.path = newPath;

                bool remapSlot = b.propertyName.StartsWith("m_Materials.Array.data[") && RemapSlotProperty(build, b.path, ref newBinding.propertyName);

                if (remapPath || remapSlot)
                {
                    var curve = AnimationUtility.GetEditorCurve(clip, b);
                    AnimationUtility.SetEditorCurve(clip, b, null);
                    if (curve != null) AnimationUtility.SetEditorCurve(clip, newBinding, curve);
                    changed = true;
                }
            }

            if (!changed) return null;

            var clone = Object.Instantiate(clip);
            clone.name = clip.name + "_ato";
            try { build.ndmf.AssetSaver.SaveAsset(clone); } catch (System.Exception) { }
            return clone;
        }

        private static bool RemapSlotProperty(ATOBuildContext build, string path, ref string propertyName)
        {
            var rr = FindRendererByPath(build, path);
            if (rr == null || !build.animRemap.slotRemap.TryGetValue(rr.rendererId, out var remap)) return false;
            int open = propertyName.IndexOf('[');
            int close = propertyName.IndexOf(']');
            if (open < 0 || close < 0) return false;
            var idxStr = propertyName.Substring(open + 1, close - open - 1);
            if (!int.TryParse(idxStr, out var idx) || !remap.TryGetValue(idx, out var newIdx)) return false;
            propertyName = propertyName.Substring(0, open + 1) + newIdx + propertyName.Substring(close);
            return true;
        }

        private static ATORendererRef FindRendererByPath(ATOBuildContext build, string path)
        {
            foreach (var rr in build.renderers)
                if (rr.path == path) return rr;
            return null;
        }

        private static void UpdateAnimator(ATOBuildContext build, Animator anim, Dictionary<AnimationClip, AnimationClip> remap)
        {
            var rc = anim.runtimeAnimatorController;
            if (rc == null) return;
            if (rc is AnimatorOverrideController aoc)
            {
                var newAoc = new AnimatorOverrideController(aoc.runtimeAnimatorController);
                var overrides = new List<KeyValuePair<AnimationClip, AnimationClip>>(aoc.overrides);
                for (int i = 0; i < overrides.Count; i++)
                    if (overrides[i].Value != null && remap.TryGetValue(overrides[i].Value, out var nv))
                        overrides[i] = new KeyValuePair<AnimationClip, AnimationClip>(overrides[i].Key, nv);
                newAoc.ApplyOverrides(overrides);
                try { build.ndmf.AssetSaver.SaveAsset(newAoc); } catch (System.Exception) { }
                anim.runtimeAnimatorController = newAoc;
            }
            else if (rc is AnimatorController ac)
            {
                var newAc = Object.Instantiate(ac);
                foreach (var layer in newAc.layers)
                    RemapStateMachine(layer.stateMachine, remap);
                try { build.ndmf.AssetSaver.SaveAsset(newAc); } catch (System.Exception) { }
                anim.runtimeAnimatorController = newAc;
            }
        }

        private static void RemapStateMachine(AnimatorStateMachine sm, Dictionary<AnimationClip, AnimationClip> remap)
        {
            foreach (var st in sm.states)
            {
                if (st.state.motion is AnimationClip c && remap.TryGetValue(c, out var nc))
                    st.state.motion = nc;
                else if (st.state.motion is BlendTree bt) RemapBlendTree(bt, remap);
            }
            foreach (var child in sm.stateMachines) RemapStateMachine(child.stateMachine, remap);
        }

        private static void RemapBlendTree(BlendTree bt, Dictionary<AnimationClip, AnimationClip> remap)
        {
            var children = bt.children; // struct array; copy-modify-write back / 结构体数组；拷贝-修改-写回
            for (int i = 0; i < children.Length; i++)
            {
                var child = children[i].motion;
                if (child is AnimationClip c && remap.TryGetValue(c, out var nc))
                {
                    children[i].motion = nc;
                }
                else if (child is BlendTree sub)
                {
                    RemapBlendTree(sub, remap);
                }
            }
            bt.children = children;
        }

        private static void UpdateLegacy(ATOBuildContext build, Animation legacy, Dictionary<AnimationClip, AnimationClip> remap)
        {
            var clips = AnimationUtility.GetAnimationClips(legacy.gameObject);
            for (int i = 0; i < clips.Length; i++)
                if (clips[i] != null && remap.TryGetValue(clips[i], out var nc))
                    clips[i] = nc;
            if (clips.Length > 0) AnimationUtility.SetAnimationClips(legacy, clips);
        }
    }
}
