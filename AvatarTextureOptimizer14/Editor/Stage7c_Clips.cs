// Stage7c_Clips — animation retarget (material/texture curves) / 动画重定向（材质/贴图曲线）
// Controllers are deep-cloned via Object.Instantiate (deep-copies states/state machines while keeping
// external clip references), then clip motions pointing at clones are swapped. Clips are cloned before
// rewriting curves — user assets are never mutated.<br>
// 控制器经 Object.Instantiate 深克隆（状态/状态机随拷贝，外部 clip 引用保持共享），随后替换指向克隆
// clip 的 Motion；clip 一律先克隆再改写曲线——绝不改用户资产。
using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using Object = UnityEngine.Object;

namespace Fosa.ATO.Editor
{
    internal static class Stage7c_Clips
    {
        private static readonly Dictionary<AnimationClip, AnimationClip> ClipMap = new Dictionary<AnimationClip, AnimationClip>();
        private static readonly Dictionary<RuntimeAnimatorController, RuntimeAnimatorController> CtrlMap =
            new Dictionary<RuntimeAnimatorController, RuntimeAnimatorController>();
        private static readonly List<Object> ToSave = new List<Object>();

        internal static void Run(BuildContext ctx, ATOPipeContext pipe, StageProgress progress)
        {
            ClipMap.Clear(); CtrlMap.Clear(); ToSave.Clear();
            if (pipe.materialReplacements.Count == 0 && pipe.wholeTexReplacement.Count == 0 && pipe.atlasPlaneOf.Count == 0)
            {
                ATOLog.V("nothing to retarget in clips");
                return;
            }
            var desc = ctx.AvatarRootObject.GetComponent<VRCAvatarDescriptor>();
            var controllers = new HashSet<RuntimeAnimatorController>();
            if (desc != null)
            {
                foreach (var layer in desc.baseAnimationLayers) if (layer.animatorController != null) controllers.Add(layer.animatorController);
                foreach (var layer in desc.specialAnimationLayers) if (layer.animatorController != null) controllers.Add(layer.animatorController);
            }
            foreach (var a in ctx.AvatarRootObject.GetComponentsInChildren<Animator>(true))
                if (a.runtimeAnimatorController != null) controllers.Add(a.runtimeAnimatorController);

            int ci = 0;
            foreach (var ctrl in controllers)
            {
                ci++;
                pipe.CancelCheck(progress, ATOL10n.T("ato.stage.clips"), (float)ci / Mathf.Max(1, controllers.Count));
                ProcessController(pipe, ctrl);
            }

            // reassignment / 重新指派
            if (desc != null)
            {
                ReassignLayers(desc, false);
                ReassignLayers(desc, true);
            }
            foreach (var a in ctx.AvatarRootObject.GetComponentsInChildren<Animator>(true))
                if (a.runtimeAnimatorController != null && CtrlMap.TryGetValue(a.runtimeAnimatorController, out var nc))
                    a.runtimeAnimatorController = nc;

            if (ToSave.Count > 0) ctx.AssetSaver?.SaveAssets(ToSave);
            ATOLog.Info(ATOL10n.T("ato.log.clips_done", ClipMap.Count, CtrlMap.Count));
            ATOEvents.Raise("clips", pipe, ctx.AvatarRootObject);
            ATOHookRegistry.Notify("clips", pipe);
        }

        private static void ReassignLayers(VRCAvatarDescriptor desc, bool special)
        {
            var layers = special ? desc.specialAnimationLayers : desc.baseAnimationLayers;
            for (int i = 0; i < layers.Length; i++)
            {
                var c = layers[i].animatorController;
                if (c != null && CtrlMap.TryGetValue(c, out var nc)) layers[i].animatorController = nc;
            }
            if (special) desc.specialAnimationLayers = layers; else desc.baseAnimationLayers = layers;
        }

        // ---------------------------------------------------------------- controllers
        private static RuntimeAnimatorController ProcessController(ATOPipeContext pipe, RuntimeAnimatorController ctrl)
        {
            if (ctrl == null) return null;
            if (CtrlMap.TryGetValue(ctrl, out var cached)) return cached;

            // collect clips first to decide whether a clone is needed / 先收集clip判断是否需要克隆
            var clips = new HashSet<AnimationClip>();
            CollectControllerClips(ctrl, clips);
            var needsWork = clips.Any(c => RewriteIfNeeded(pipe, c) != c);
            if (!needsWork) { CtrlMap[ctrl] = ctrl; return ctrl; }

            RuntimeAnimatorController clone;
            if (ctrl is AnimatorController ac)
            {
                var nc = Object.Instantiate(ac);
                nc.name = ac.name + "_ATO";
                SwapMotions(nc);
                clone = nc;
            }
            else if (ctrl is AnimatorOverrideController aoc)
            {
                var nc = Object.Instantiate(aoc);
                nc.name = aoc.name + "_ATO";
                // rewire base controller first / 先重接基础控制器
                var newBase = ProcessController(pipe, aoc.runtimeAnimatorController);
                nc.runtimeAnimatorController = newBase;
                // remap override values / 重映射覆盖值
                var list = new List<KeyValuePair<AnimationClip, AnimationClip>>(nc.overridesCount);
                nc.GetOverrides(list);
                var outList = list.Select(kv => new KeyValuePair<AnimationClip, AnimationClip>(kv.Key, kv.Value != null ? RewriteIfNeeded(pipe, kv.Value) : null)).ToList();
                nc.ApplyOverrides(outList);
                clone = nc;
            }
            else { CtrlMap[ctrl] = ctrl; return ctrl; }

            CtrlMap[ctrl] = clone;
            ToSave.Add(clone);
            ObjectRegistry.RegisterReplacedObject(ctrl, clone);
            return clone;
        }

        private static void SwapMotions(AnimatorController nc)
        {
            foreach (var layer in nc.layers)
                SwapSm(layer.stateMachine);

            static void SwapSm(AnimatorStateMachine sm)
            {
                if (sm == null) return;
                foreach (var child in sm.states)
                {
                    if (child.state == null) continue;
                    child.state.motion = Swap(child.state.motion);
                }
                foreach (var csm in sm.stateMachines) SwapSm(csm.stateMachine);
            }

            static Motion Swap(Motion m)
            {
                if (m is AnimationClip c && ClipMap.TryGetValue(c, out var nc)) return nc;
                if (m is BlendTree bt)
                {
                    var children = bt.children;
                    for (int i = 0; i < children.Length; i++) children[i].motion = Swap(children[i].motion);
                    bt.children = children;
                }
                return m;
            }
        }

        private static void CollectControllerClips(RuntimeAnimatorController ctrl, HashSet<AnimationClip> clips, int depth = 0)
        {
            if (ctrl == null || depth > 8) return;
            if (ctrl is AnimatorController ac)
                foreach (var layer in ac.layers) CollectSm(layer.stateMachine, clips, depth + 1);
            else if (ctrl is AnimatorOverrideController aoc)
            {
                CollectControllerClips(aoc.runtimeAnimatorController, clips, depth + 1);
                var list = new List<KeyValuePair<AnimationClip, AnimationClip>>(aoc.overridesCount);
                aoc.GetOverrides(list);
                foreach (var kv in list) if (kv.Value != null) clips.Add(kv.Value);
            }
        }
        private static void CollectSm(AnimatorStateMachine sm, HashSet<AnimationClip> clips, int depth)
        {
            if (sm == null || depth > 16) return;
            foreach (var st in sm.states) CollectMotion(st.state?.motion, clips, depth + 1);
            foreach (var c in sm.stateMachines) CollectSm(c.stateMachine, clips, depth + 1);
        }
        private static void CollectMotion(Motion m, HashSet<AnimationClip> clips, int depth)
        {
            if (m is AnimationClip c) clips.Add(c);
            else if (m is BlendTree bt && depth < 16)
                foreach (var ch in bt.children) CollectMotion(ch.motion, clips, depth + 1);
        }

        // ---------------------------------------------------------------- clip rewriting
        private static AnimationClip RewriteIfNeeded(ATOPipeContext pipe, AnimationClip clip)
        {
            if (clip == null) return null;
            if (ClipMap.TryGetValue(clip, out var cached)) return cached;

            var bindings = AnimationUtility.GetObjectReferenceCurveBindings(clip);
            var rewrites = new List<(EditorCurveBinding binding, ObjectReferenceKeyframe[] keys)>();
            foreach (var b in bindings)
            {
                var keys = AnimationUtility.GetObjectReferenceCurve(clip, b);
                if (keys == null) continue;
                var newKeys = new ObjectReferenceKeyframe[keys.Length];
                bool changed = false;
                for (int i = 0; i < keys.Length; i++)
                {
                    newKeys[i] = keys[i];
                    var v = keys[i].value;
                    if (v is Material m && pipe.materialReplacements.TryGetValue(m, out var nm) && nm != null)
                    {
                        newKeys[i].value = nm; changed = true;
                    }
                    else if (v is Texture2D t)
                    {
                        var target = ResolveClipTexture(pipe, b.propertyName, t);
                        if (target != null && target != t) { newKeys[i].value = target; changed = true; }
                    }
                }
                if (changed) rewrites.Add((b, newKeys));
            }

            if (rewrites.Count == 0) { ClipMap[clip] = clip; return clip; }
            var clone = Object.Instantiate(clip);
            clone.name = clip.name + "_ATO";
            foreach (var (b, keys) in rewrites)
                AnimationUtility.SetObjectReferenceCurve(clone, b, keys);
            ClipMap[clip] = clone;
            ToSave.Add(clone);
            ObjectRegistry.RegisterReplacedObject(clip, clone);
            return clone;
        }

        /// <summary>Resolve a clip curve (prop, sourceTex) to its optimized replacement. / 解析动画曲线的贴图替换。</summary>
        private static Texture2D ResolveClipTexture(ATOPipeContext pipe, string propertyName, Texture2D source)
        {
            if (!pipe.infoOf.TryGetValue(source, out var info)) return null;
            // blocked (texture, any class) → never retarget (its UV slots kept original) / 被锁存的贴图绝不重指向
            foreach (var b in pipe.blockedTex) if (ReferenceEquals(b.Item1, info)) return null;
            if (info.whitelisted) return null;
            var prop = propertyName.StartsWith("material.", StringComparison.Ordinal)
                ? propertyName.Substring("material.".Length) : propertyName;
            // class via any matching slot ref / 经任一匹配槽引用判定类型
            foreach (var kv in pipe.slotRefs)
            {
                foreach (var r in kv.Value)
                {
                    if (r.property != prop || !r.textures.Contains(source)) continue;
                    var t = Stage7_Apply.ResolveTexture(pipe, info, r.cls);
                    if (t != null) return t;
                }
            }
            if (pipe.wholeTexReplacement.TryGetValue(info, out var whole)) return whole;
            return null;
        }
    }
}
