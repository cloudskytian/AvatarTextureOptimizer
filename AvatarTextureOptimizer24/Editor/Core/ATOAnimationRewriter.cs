// ============================================================================
// ATOAnimationRewriter.cs — 动画引用重写 / Animation reference rewriter
// (EN) Rewrites object references inside animation clips after ATO replaces
//      materials/textures with atlases. Clones clips and (if needed) their
//      AnimatorController so source assets are never mutated. Handles nested
//      state machines and BlendTrees.
// (ZH) ATO 用图集替换材质/贴图后，重写动画片段内的对象引用。克隆 clip（必要时
//      连同其 AnimatorController），绝不污染源资产。处理嵌套状态机与 BlendTree。
// ============================================================================

using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Fosa.AvatarTextureOptimizer
{
    public static class ATOAnimationRewriter
    {
        /// <summary>(EN) Rewrite all animation clips that reference replaced objects.
        ///     mapping: old object -> new object (materials & textures). (ZH) 重写所有引用了被替换对象的动画片段。</summary>
        public static void Rewrite(ATOBuildContext ctx, Dictionary<Object, Object> mapping)
        {
            if (mapping.Count == 0) return;

            // 旧版 Animation 组件 / legacy Animation components
            foreach (var anim in ctx.AvatarRoot.GetComponentsInChildren<Animation>(true))
            {
                foreach (var state in AnimationUtility.GetAnimationClips(anim.gameObject))
                {
                    var clip = state;
                    if (clip == null) continue;
                    var rewritten = RewriteClip(clip, mapping, ctx);
                    if (rewritten != null && rewritten != clip)
                        AddClipToAnimation(anim, clip, rewritten);
                }
            }

            // Animator / AnimatorController
            foreach (var animator in ctx.AvatarRoot.GetComponentsInChildren<Animator>(true))
            {
                var controller = animator.runtimeAnimatorController;
                if (controller == null) continue;

                bool needsRewrite = ControllerReferencesMapped(controller, mapping, ctx);
                if (!needsRewrite) continue;

                var newController = CloneController(controller);
                RewriteController(newController, mapping, ctx);
                animator.runtimeAnimatorController = newController;
                ctx.Ndmf.ObjectRegistry.RegisterReplacedObject(controller, newController);
            }
        }

        // ---------------------------------------------------------------------
        // 片段重写 / clip rewriting
        // ---------------------------------------------------------------------
        private static AnimationClip RewriteClip(AnimationClip clip, Dictionary<Object, Object> mapping, ATOBuildContext ctx)
        {
            bool needsRewrite = false;
            var objectBindings = AnimationUtility.GetObjectReferenceCurveBindings(clip);
            foreach (var binding in objectBindings)
            {
                var curve = AnimationUtility.GetObjectReferenceCurve(clip, binding);
                if (curve == null) continue;
                foreach (var frame in curve)
                    if (frame.value != null && mapping.ContainsKey(frame.value)) { needsRewrite = true; break; }
                if (needsRewrite) break;
            }
            if (!needsRewrite) return clip;

            // 克隆片段 / clone clip
            var newClip = Object.Instantiate(clip);
            newClip.name = clip.name + "_ATO";
            ctx.Ndmf.ObjectRegistry.RegisterReplacedObject(clip, newClip);

            foreach (var binding in objectBindings)
            {
                var curve = AnimationUtility.GetObjectReferenceCurve(clip, binding);
                if (curve == null) continue;
                var newCurve = new ObjectReferenceKeyframe[curve.Length];
                for (int i = 0; i < curve.Length; i++)
                {
                    newCurve[i] = curve[i];
                    if (curve[i].value != null && mapping.TryGetValue(curve[i].value, out var mapped))
                        newCurve[i].value = mapped;
                }
                AnimationUtility.SetObjectReferenceCurve(newClip, binding, newCurve);
            }

            return newClip;
        }

        // ---------------------------------------------------------------------
        // Controller 克隆 / controller cloning
        // ---------------------------------------------------------------------
        private static bool ControllerReferencesMapped(RuntimeAnimatorController controller, Dictionary<Object, Object> mapping, ATOBuildContext ctx)
        {
            var clips = GetClips(controller);
            foreach (var clip in clips)
            {
                if (clip == null) continue;
                if (ClipReferencesMapped(clip, mapping)) return true;
            }
            return false;
        }

        private static bool ClipReferencesMapped(AnimationClip clip, Dictionary<Object, Object> mapping)
        {
            foreach (var binding in AnimationUtility.GetObjectReferenceCurveBindings(clip))
            {
                var curve = AnimationUtility.GetObjectReferenceCurve(clip, binding);
                if (curve == null) continue;
                foreach (var frame in curve)
                    if (frame.value != null && mapping.ContainsKey(frame.value)) return true;
            }
            return false;
        }

        private static RuntimeAnimatorController CloneController(RuntimeAnimatorController controller)
        {
            if (controller is AnimatorOverrideController aoc)
            {
                var newAoc = new AnimatorOverrideController(aoc.runtimeAnimatorController);
                // 复制覆盖 / copy overrides
                var overrides = new List<KeyValuePair<AnimationClip, AnimationClip>>();
                aoc.GetOverrides(overrides);
                foreach (var kv in overrides)
                    if (kv.Key != null && kv.Value != null)
                        newAoc[kv.Key] = kv.Value;
                return newAoc;
            }
            return Object.Instantiate(controller);
        }

        private static void RewriteController(RuntimeAnimatorController controller, Dictionary<Object, Object> mapping, ATOBuildContext ctx)
        {
            if (controller is AnimatorController ac)
            {
                foreach (var layer in ac.layers)
                    RewriteStateMachine(layer.stateMachine, mapping, ctx);
            }
            else if (controller is AnimatorOverrideController aoc)
            {
                // 覆盖 clip 也需要重写 / overridden clips need rewriting too
                var overrides = new List<KeyValuePair<AnimationClip, AnimationClip>>();
                aoc.GetOverrides(overrides);
                foreach (var kv in overrides)
                {
                    if (kv.Value == null) continue;
                    var rewritten = RewriteClip(kv.Value, mapping, ctx);
                    if (rewritten != kv.Value) aoc[kv.Key] = rewritten;
                }
                // 底层 controller 的 clip 通过 runtimeAnimatorController 处理 / underlying clips handled via base
                if (aoc.runtimeAnimatorController is AnimatorController baseAc)
                    RewriteController(baseAc, mapping, ctx);
            }
        }

        private static void RewriteStateMachine(AnimatorStateMachine sm, Dictionary<Object, Object> mapping, ATOBuildContext ctx)
        {
            foreach (var child in sm.states)
                RewriteState(child.state, mapping, ctx);
            foreach (var child in sm.stateMachines)
                RewriteStateMachine(child.stateMachine, mapping, ctx);
        }

        private static void RewriteState(AnimatorState state, Dictionary<Object, Object> mapping, ATOBuildContext ctx)
        {
            if (state.motion != null)
                state.motion = RewriteMotion(state.motion, mapping, ctx);
        }

        private static Motion RewriteMotion(Motion motion, Dictionary<Object, Object> mapping, ATOBuildContext ctx)
        {
            if (motion is AnimationClip clip)
            {
                var rewritten = RewriteClip(clip, mapping, ctx);
                return rewritten ?? motion;
            }
            if (motion is BlendTree tree)
            {
                var children = tree.children;
                for (int i = 0; i < children.Length; i++)
                {
                    if (children[i].motion != null)
                        children[i].motion = RewriteMotion(children[i].motion, mapping, ctx);
                }
                tree.children = children;
                return tree;
            }
            return motion;
        }

        // ---------------------------------------------------------------------
        // 获取 controller 下所有 clip / get all clips under a controller
        // ---------------------------------------------------------------------
        private static List<AnimationClip> GetClips(RuntimeAnimatorController controller)
        {
            var clips = new List<AnimationClip>();
            if (controller is AnimatorController ac)
            {
                foreach (var layer in ac.layers)
                    CollectClips(layer.stateMachine, clips);
            }
            else if (controller is AnimatorOverrideController aoc)
            {
                clips.AddRange(aoc.runtimeAnimatorController.animationClips);
                var overrides = new List<KeyValuePair<AnimationClip, AnimationClip>>();
                aoc.GetOverrides(overrides);
                foreach (var kv in overrides) if (kv.Value != null) clips.Add(kv.Value);
            }
            return clips;
        }

        private static void CollectClips(AnimatorStateMachine sm, List<AnimationClip> clips)
        {
            foreach (var child in sm.states)
                CollectMotion(child.state.motion, clips);
            foreach (var child in sm.stateMachines)
                CollectClips(child.stateMachine, clips);
        }

        private static void CollectMotion(Motion motion, List<AnimationClip> clips)
        {
            if (motion is AnimationClip c) clips.Add(c);
            else if (motion is BlendTree t)
                foreach (var child in t.children)
                    CollectMotion(child.motion, clips);
        }

        // ---------------------------------------------------------------------
        // 旧版 Animation 组件 / legacy Animation component
        // ---------------------------------------------------------------------
        private static void AddClipToAnimation(Animation anim, AnimationClip oldClip, AnimationClip newClip)
        {
            try
            {
                // 替换组件绑定的 clip / replace the clip bound to the component
                var clips = new List<AnimationClip>(AnimationUtility.GetAnimationClips(anim.gameObject));
                for (int i = 0; i < clips.Count; i++)
                    if (clips[i] == oldClip) clips[i] = newClip;
                AnimationUtility.SetAnimationClips(anim, clips.ToArray());
            }
            catch (System.Exception e)
            {
                ATOLog.Warn("[anim] legacy Animation clip rewrite failed: " + e.Message);
            }
        }
    }
}
