using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

// Animation analysis: collects all animation clips that affect the avatar and indexes
// the properties they animate (material switching, texture switching, ST transforms,
// renderer enable/disable, blend shapes, local scale, cutoff / render-mode toggles).
// 动画分析：收集影响 Avatar 的全部动画剪辑，并索引其动画属性（材质切换、贴图切换、ST 变换、
// 渲染器启停、形态键、局部缩放、Cutoff/渲染模式开关）。

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    public sealed class AnimatedBinding
    {
        public string Path;               // Absolute path from avatar root. 相对 Avatar 根的绝对路径。
        public string Property;           // e.g. "m_Materials.Array.data[0]._MainTex" / "m_LocalScale.x" / "m_Enabled".
        public bool IsObjectReference;    // Object reference curve (material/texture). 对象引用曲线。
        public float MinValue = float.MaxValue, MaxValue = float.MinValue;   // float curve extrema. 浮点曲线极值。
        public List<UnityEngine.Object> ReferenceValues = new List<UnityEngine.Object>(); // object ref targets. 对象引用目标。
        public AnimationClip Clip;
    }

    /// <summary>
    /// Indexed animation analysis for one avatar.
    /// 单个 Avatar 的索引化动画分析。
    /// </summary>
    public sealed class AnimationAnalysis
    {
        private readonly Dictionary<string, List<AnimatedBinding>> _byPath = new Dictionary<string, List<AnimatedBinding>>();
        private readonly Dictionary<(string path, string prop), List<AnimatedBinding>> _byPathProp = new Dictionary<(string, string), List<AnimatedBinding>>();
        public readonly List<AnimationClip> Clips = new List<AnimationClip>();

        public void Add(AnimatedBinding b)
        {
            if (!_byPath.TryGetValue(b.Path, out var l)) { l = new List<AnimatedBinding>(); _byPath[b.Path] = l; }
            l.Add(b);
            var key = (b.Path, b.Property);
            if (!_byPathProp.TryGetValue(key, out var l2)) { l2 = new List<AnimatedBinding>(); _byPathProp[key] = l2; }
            l2.Add(b);
        }

        public bool TryGet(string path, string prop, out List<AnimatedBinding> bindings)
            => _byPathProp.TryGetValue((path, prop), out bindings);

        /// <summary>Worst-case local scale magnitude per axis for a transform (max over animated & static). 变换轴向上的最差缩放（动画与静态取最大）。</summary>
        public Vector3 WorstLocalScale(Transform t, GameObject root)
        {
            Vector3 scale = t.localScale;
            scale.x = Mathf.Max(scale.x, WorstAxis(t, root, "m_LocalScale.x"));
            scale.y = Mathf.Max(scale.y, WorstAxis(t, root, "m_LocalScale.y"));
            scale.z = Mathf.Max(scale.z, WorstAxis(t, root, "m_LocalScale.z"));
            return scale;
        }

        private static float WorstAxis(Transform t, GameObject root, string prop)
        {
            if (!TryGet(AbsPath(t, root), prop, out var bindings)) return 0f;
            float worst = 0f;
            foreach (var b in bindings)
                worst = Mathf.Max(worst, Mathf.Max(Mathf.Abs(b.MinValue), Mathf.Abs(b.MaxValue)));
            return worst;
        }

        /// <summary>True if the transform is enabled at least sometimes (static enabled or m_Enabled curve exists). 变换是否至少有时启用。</summary>
        public bool IsEverEnabled(Transform t, GameObject root)
        {
            if (t.gameObject.activeSelf)
            {
                // An ancestor might be disabled by animation; check each ancestor chain statically. 静态链检查。
                var cur = t;
                while (cur != null && cur != root.transform)
                {
                    if (TryGet(AbsPath(cur, root), "m_Enabled", out _)) return true;
                    cur = cur.parent;
                }
                return true;
            }
            // Inactive by default: enabled only if an animation turns it on. 默认关闭：仅当动画打开。
            return TryGet(AbsPath(t, root), "m_Enabled", out _);
        }

        /// <summary>Animated blend shape names of a SkinnedMeshRenderer (from "blendShape.X" curves). 皮肤网格渲染器的动画形态键名。</summary>
        public HashSet<string> AnimatedBlendShapes(SkinnedMeshRenderer r, GameObject root)
        {
            var set = new HashSet<string>();
            if (TryGet(AbsPath(r.transform, root), "m_BlendShapeWeights", out _)) return set; // weight array property: treat all as animated. 权重数组属性：视为全部动画。
            foreach (var kv in _byPathProp)
                if (kv.Key.path == AbsPath(r.transform, root) && kv.Key.prop.StartsWith("blendShape.", StringComparison.Ordinal))
                    set.Add(kv.Key.prop.Substring("blendShape.".Length));
            return set;
        }

        /// <summary>True if any curve can change the material/texture property on this slot. 是否有曲线可改变该槽位的材质/贴图属性。</summary>
        public bool SlotPropertyAnimated(Transform obj, GameObject root, int slot, string prop)
        {
            string p = $"m_Materials.Array.data[{slot}].{prop}";
            return TryGet(AbsPath(obj, root), p, out _);
        }

        /// <summary>Object-reference candidates for a material/texture property on a slot. 槽位上材质/贴图属性的对象引用候选。</summary>
        public List<UnityEngine.Object> SlotReferenceCandidates(Transform obj, GameObject root, int slot, string prop)
        {
            string p = $"m_Materials.Array.data[{slot}].{prop}";
            var result = new List<UnityEngine.Object>();
            if (TryGet(AbsPath(obj, root), p, out var bindings))
                foreach (var b in bindings)
                    if (b.IsObjectReference)
                        foreach (var v in b.ReferenceValues)
                            if (v != null) result.Add(v);
            return result;
        }

        /// <summary>Float extrema for a material property on a slot (e.g. _Cutoff, _MainTex_ST.x). 槽位上材质属性的浮点极值。</summary>
        public bool TryGetSlotFloatRange(Transform obj, GameObject root, int slot, string prop, out float min, out float max)
        {
            min = float.MaxValue; max = float.MinValue;
            string p = $"m_Materials.Array.data[{slot}].{prop}";
            if (TryGet(AbsPath(obj, root), p, out var bindings))
            {
                foreach (var b in bindings)
                {
                    if (b.IsObjectReference) continue;
                    if (b.MinValue < min) min = b.MinValue;
                    if (b.MaxValue > max) max = b.MaxValue;
                }
                return min <= max;
            }
            return false;
        }

        /// <summary>Relative path from the avatar root. 相对 Avatar 根的路径。</summary>
        public static string AbsPath(Transform t, GameObject root)
        {
            if (t == root.transform) return "";
            var names = new List<string>();
            var cur = t;
            while (cur != null && cur != root.transform) { names.Add(cur.name); cur = cur.parent; }
            names.Reverse();
            return string.Join("/", names);
        }
    }

    public static class AnimationAnalyzer
    {
        /// <summary>
        /// Collects every clip reachable from animators (Mecanim) and legacy Animation components.
        /// 收集来自 Animator（Mecanim）与旧版 Animation 组件的全部可达剪辑。
        /// </summary>
        public static List<AnimationClip> CollectClips(GameObject root)
        {
            var clips = new List<AnimationClip>();
            var seen = new HashSet<AnimationClip>();
            void AddClip(AnimationClip c) { if (c != null && seen.Add(c)) clips.Add(c); }

            foreach (var animator in root.GetComponentsInChildren<Animator>(true))
            {
                var ac = animator.runtimeAnimatorController as AnimatorController;
                if (ac == null) continue;
                foreach (var layer in ac.layers)
                    CollectMotion(layer.stateMachine, AddClip);
            }
            foreach (var anim in root.GetComponentsInChildren<Animation>(true))
                foreach (AnimationState state in anim)
                    AddClip(state.clip);
            return clips;
        }

        private static void CollectMotion(AnimatorStateMachine sm, Action<AnimationClip> add)
        {
            foreach (var state in sm.states)
            {
                var clip = state.state.motion as AnimationClip;
                if (clip != null) add(clip);
                else if (state.state.motion is BlendTree bt) CollectBlendTree(bt, add);
            }
            foreach (var sub in sm.stateMachines) CollectMotion(sub.stateMachine, add);
        }

        private static void CollectBlendTree(BlendTree bt, Action<AnimationClip> add)
        {
            foreach (var child in bt.children)
            {
                if (child.motion is AnimationClip c) add(c);
                else if (child.motion is BlendTree sub) CollectBlendTree(sub, add);
            }
        }

        /// <summary>
        /// Builds the animation index for the avatar. clipRoot = object the clip's paths are relative to
        /// (the GameObject holding the Animator/Animation); we convert to avatar-root-absolute paths.
        /// 为 Avatar 建立动画索引。clipRoot=剪辑路径相对的对象（持有 Animator/Animation 的 GameObject）；
        /// 转换为相对 Avatar 根的绝对路径。
        /// </summary>
        public static AnimationAnalysis Analyze(GameObject root, List<AnimationClip> clips)
        {
            var analysis = new AnimationAnalysis();
            foreach (var clip in clips) analysis.Clips.Add(clip);

            foreach (var animator in root.GetComponentsInChildren<Animator>(true))
            {
                var ac = animator.runtimeAnimatorController as AnimatorController;
                if (ac == null) continue;
                foreach (var clip in clips)
                    IndexClip(analysis, animator.transform, clip);
            }
            foreach (var anim in root.GetComponentsInChildren<Animation>(true))
                foreach (AnimationState state in anim)
                    if (state.clip != null)
                        IndexClip(analysis, anim.transform, state.clip);
            return analysis;
        }

        private static void IndexClip(AnimationAnalysis analysis, Transform clipRoot, AnimationClip clip)
        {
            foreach (var binding in AnimationUtility.GetCurveBindings(clip))
            {
                string abs = Join(clipRoot, binding.path);
                var b = new AnimatedBinding
                {
                    Path = abs, Property = binding.propertyName, IsObjectReference = false, Clip = clip,
                    MinValue = float.MaxValue, MaxValue = float.MinValue,
                };
                var curve = AnimationUtility.GetEditorCurve(clip, binding);
                if (curve != null && curve.length > 0)
                {
                    foreach (var k in curve.keys)
                    {
                        if (k.value < b.MinValue) b.MinValue = k.value;
                        if (k.value > b.MaxValue) b.MaxValue = k.value;
                    }
                }
                analysis.Add(b);
            }
            foreach (var binding in AnimationUtility.GetObjectReferenceCurveBindings(clip))
            {
                var b = new AnimatedBinding
                {
                    Path = Join(clipRoot, binding.path), Property = binding.propertyName, IsObjectReference = true, Clip = clip,
                };
                var keyframes = AnimationUtility.GetObjectReferenceCurve(clip, binding);
                if (keyframes != null)
                    foreach (var kf in keyframes)
                        if (kf.value != null) b.ReferenceValues.Add(kf.value);
                analysis.Add(b);
            }
        }

        private static string Join(Transform clipRoot, string relativePath)
        {
            string rootPath = AnimationAnalysis.AbsPath(clipRoot, clipRoot.root.gameObject);
            // Note: clipRoot might not be under the avatar root in exotic setups; use the transform chain anyway.
            // 注意：clipRoot 在特殊情况下可能不在 Avatar 根下；仍按变换链处理。
            return string.IsNullOrEmpty(relativePath) ? rootPath : rootPath + "/" + relativePath;
        }
    }
}
