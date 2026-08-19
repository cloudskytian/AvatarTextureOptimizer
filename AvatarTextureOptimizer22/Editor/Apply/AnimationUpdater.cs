// AvatarTextureOptimizer
// File: Editor/Apply/AnimationUpdater.cs
//
// Updates animation references after the optimization:
//   - object-reference curves whose value is a remapped texture (dedup, atlas,
//     whole-texture copy) get the new texture
//   - material-slot switch curves (m_Materials.Array.data[N]) get updated
//     values (material dedup) and indices (slot merging, per renderer)
//   - material-object reference curves get the deduplicated material
// Clips are collected from the same sources as AnimationScanner.
//
// 在优化后更新动画引用：
//   - 值为被重映射贴图（去重、图集、整图副本）的对象引用曲线获得新贴图
//   - 材质槽切换曲线（m_Materials.Array.data[N]）获得更新后的值（材质去重）
//     与索引（材质槽合并，按渲染器）
//   - 材质对象引用曲线获得去重后的材质
// 剪辑来源与 AnimationScanner 相同。

using System.Collections.Generic;
using System.Linq;
using net.fosa.avatar_texture_optimizer.editor.logging;
using net.fosa.avatar_texture_optimizer.editor.model;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
#if NDMF_VRCSDK3_AVATARS
using VRC.SDK3.Avatars.Components;
#endif

namespace net.fosa.avatar_texture_optimizer.editor.apply
{
    public static class AnimationUpdater
    {
        /// <summary>
        /// Remap caches built once per bake. / 每次烘焙构建一次的重映射缓存。
        /// </summary>
        private sealed class RemapCache
        {
            public readonly Dictionary<Object, Object> ObjectRemap = new Dictionary<Object, Object>();
        }

        public static void Update(ATOBuildState state)
        {
            var cache = new RemapCache();
            BuildRemaps(state, cache);

            // Slot-index renames apply to bindings directly (no value remap).
            // 槽索引重命名直接作用于绑定（无值重映射）。
            if (cache.ObjectRemap.Count > 0)
            {
                var clips = CollectClips(state);
                int modifiedClips = 0;
                foreach (var clip in clips)
                {
                    bool dirty = false;
                    foreach (var binding in AnimationUtility.GetObjectReferenceCurveBindings(clip))
                    {
                        var curve = AnimationUtility.GetObjectReferenceCurve(clip, binding);
                        bool changed = false;
                        var newCurve = new ObjectReferenceKeyframe[curve.Length];
                        for (int i = 0; i < curve.Length; i++)
                        {
                            newCurve[i] = curve[i];
                            if (newCurve[i].value != null &&
                                cache.ObjectRemap.TryGetValue(newCurve[i].value, out var rep))
                            {
                                newCurve[i].value = rep;
                                changed = true;
                            }
                        }
                        if (changed)
                        {
                            AnimationUtility.SetObjectReferenceCurve(clip, binding, newCurve);
                            dirty = true;
                        }
                    }
                    if (dirty) { EditorUtility.SetDirty(clip); modifiedClips++; }
                }
                if (modifiedClips > 0)
                    ATOLog.Info($"[ATO] Updated animation references in {modifiedClips} clips. / 更新了 {modifiedClips} 个剪辑中的动画引用。");
            }

            RenameSlotBindings(state);
        }

        private static void BuildRemaps(ATOBuildState state, RemapCache cache)
        {
            // Texture remaps: dedup + atlas + whole-texture copies.
            // 贴图重映射：去重 + 图集 + 整图副本。
            foreach (var kv in state.TextureRemap)
                cache.ObjectRemap[kv.Key] = kv.Value;

            foreach (var group in state.UVGroups)
            {
                if (group.Whitelisted) continue;
                foreach (var usage in group.Textures)
                {
                    if (usage.Texture == null) continue;
                    var rep = Applier.ResolveNewTexture(state, group, usage);
                    if (rep != null && rep != usage.Texture)
                        cache.ObjectRemap[usage.Texture] = rep;
                }
            }

            // Material remaps (from dedup). / 材质重映射（来自去重）。
            foreach (var kv in state.MaterialRemap)
                cache.ObjectRemap[kv.Key] = kv.Value;
        }

        /// <summary>
        /// Rename material-slot bindings according to the per-renderer merge
        /// map. / 按每渲染器的合并映射重命名材质槽绑定。
        /// </summary>
        public static void RenameSlotBindings(ATOBuildState state)
        {
            if (state.MaterialSlotMerge.Count == 0) return;
            var root = state.Component != null ? state.Component.gameObject : null;
            if (root == null) return;

            var clips = CollectClips(state);
            foreach (var clip in clips)
            {
                bool dirty = false;
                foreach (var binding in AnimationUtility.GetObjectReferenceCurveBindings(clip))
                {
                    if (!binding.propertyName.StartsWith("m_Materials.Array.data[")) continue;
                    int oldIndex = ParseSlot(binding.propertyName);
                    if (oldIndex < 0) continue;

                    // Resolve the renderer from the binding path.
                    // 从绑定路径解析渲染器。
                    var target = string.IsNullOrEmpty(binding.path)
                        ? root
                        : root.transform.Find(binding.path)?.gameObject;
                    if (target == null) continue;
                    var renderer = target.GetComponent<SkinnedMeshRenderer>();
                    if (renderer == null) renderer = target.GetComponent<MeshRenderer>();
                    if (renderer == null) continue;

                    if (!state.MaterialSlotMerge.TryGetValue((renderer, oldIndex), out int newIndex)) continue;
                    string newProp = $"m_Materials.Array.data[{newIndex}]";
                    if (newProp == binding.propertyName) continue;

                    var curve = AnimationUtility.GetObjectReferenceCurve(clip, binding);
                    AnimationUtility.SetObjectReferenceCurve(clip, new EditorCurveBinding
                    {
                        type = binding.type,
                        path = binding.path,
                        propertyName = newProp,
                    }, curve);
                    AnimationUtility.SetObjectReferenceCurve(clip, binding, null);
                    dirty = true;
                }
                if (dirty) EditorUtility.SetDirty(clip);
            }
        }

        private static int ParseSlot(string propertyName)
        {
            const string prefix = "m_Materials.Array.data[";
            if (propertyName.StartsWith(prefix))
            {
                int end = propertyName.IndexOf(']', prefix.Length);
                if (end > prefix.Length && int.TryParse(propertyName.Substring(prefix.Length, end - prefix.Length), out var idx))
                    return idx;
            }
            return -1;
        }

        private static List<AnimationClip> CollectClips(ATOBuildState state)
        {
            var seen = new HashSet<AnimationClip>();
            var root = state.Component != null ? state.Component.gameObject : null;
            if (root == null) return new List<AnimationClip>();

            var animator = root.GetComponentInChildren<Animator>(true);
            if (animator != null && animator.runtimeAnimatorController != null)
                CollectFromController(animator.runtimeAnimatorController, seen);

            foreach (var anim in root.GetComponentsInChildren<UnityEngine.Animation>(true))
            {
                var clips = new List<AnimationClip>();
                anim.GetClips(clips);
                foreach (var c in clips) if (c != null) seen.Add(c);
            }

#if NDMF_VRCSDK3_AVATARS
            var descriptor = root.GetComponent<VRCAvatarDescriptor>();
            if (descriptor != null)
            {
                var layers = new List<VRCAvatarDescriptor.CustomAnimLayer>();
                if (descriptor.baseAnimationLayers != null) layers.AddRange(descriptor.baseAnimationLayers);
                if (descriptor.specialAnimationLayers != null) layers.AddRange(descriptor.specialAnimationLayers);
                foreach (var layer in layers)
                    if (layer.animatorController != null)
                        CollectFromController(layer.animatorController, seen);
            }
#endif

            return seen.ToList();
        }

        private static void CollectFromController(RuntimeAnimatorController controller, HashSet<AnimationClip> seen)
        {
            switch (controller)
            {
                case AnimatorOverrideController oc:
                {
                    var overrides = new List<KeyValuePair<AnimationClip, AnimationClip>>();
                    oc.GetOverrides(overrides);
                    foreach (var kv in overrides)
                    {
                        if (kv.Key != null) seen.Add(kv.Key);
                        if (kv.Value != null) seen.Add(kv.Value);
                    }
                    break;
                }
                case AnimatorController ac:
                {
                    foreach (var layer in ac.layers)
                        CollectStates(layer.stateMachine, seen);
                    break;
                }
            }
        }

        private static void CollectStates(AnimatorStateMachine sm, HashSet<AnimationClip> seen)
        {
            foreach (var st in sm.states)
                CollectMotion(st.state.motion, seen);
            foreach (var child in sm.stateMachines)
                CollectStates(child.stateMachine, seen);
        }

        private static void CollectMotion(Motion motion, HashSet<AnimationClip> seen)
        {
            switch (motion)
            {
                case AnimationClip clip: seen.Add(clip); break;
                case BlendTree tree:
                    foreach (var child in tree.children)
                        if (child.motion != null) CollectMotion(child.motion, seen);
                    break;
            }
        }
    }
}
