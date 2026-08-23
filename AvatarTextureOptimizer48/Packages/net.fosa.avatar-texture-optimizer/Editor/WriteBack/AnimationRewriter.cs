// Rewrites animation clips: texture references -> optimized textures, material references -> deduped materials.
// / 重写动画剪辑：贴图引用 → 优化后的贴图，材质引用 → 去重后的材质。

using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using net.fosa.avatar_texture_optimizer.editor.analysis;
using net.fosa.avatar_texture_optimizer.editor.pipeline;

namespace net.fosa.avatar_texture_optimizer.editor.writeback
{
    /// <summary>
    /// Rewrites animation clips after optimization. / 优化后重写动画剪辑。
    /// </summary>
    public static class AnimationRewriter
    {
        /// <summary>Rewrite all clips reachable from the avatar. / 重写 Avatar 可达的全部剪辑。</summary>
        public static void Rewrite(Transform avatarRoot, AnalysisResult analysis,
            Dictionary<Material, Material> materialMap, List<string> warnings)
        {
            var recordByTexture = new Dictionary<Texture2D, TexRecord>();
            foreach (var r in analysis.Textures)
            {
                if (!recordByTexture.ContainsKey(r.Texture)) recordByTexture[r.Texture] = r;
            }

            var seen = new HashSet<AnimationClip>();
            foreach (var animator in avatarRoot.GetComponentsInChildren<Animator>(true))
            {
                if (animator.runtimeAnimatorController is AnimatorController ac)
                {
                    CollectClips(ac, seen);
                }
            }
            var desc = avatarRoot.GetComponent<VRC.SDK3.Avatars.Components.VRCAvatarDescriptor>();
            if (desc != null)
            {
                CollectLayerClips(desc.baseAnimationLayers, seen);
                CollectLayerClips(desc.specialAnimationLayers, seen);
            }

            foreach (var clip in seen)
            {
                RewriteClip(clip, recordByTexture, materialMap);
            }
        }

        private static void CollectLayerClips(VRC.SDK3.Avatars.Components.VRCAvatarDescriptor.CustomAnimLayer[] layers,
            HashSet<AnimationClip> into)
        {
            if (layers == null) return;
            foreach (var layer in layers)
            {
                if (layer == null || layer.animatorController == null) continue;
                if (layer.animatorController is AnimatorController ac) CollectClips(ac, into);
            }
        }

        private static void CollectClips(AnimatorController ac, HashSet<AnimationClip> into)
        {
            var layers = ac.layers;
            if (layers == null) return;
            foreach (var layer in layers)
            {
                var sm = layer.stateMachine;
                if (sm == null) continue;
                foreach (var state in sm.states)
                {
                    if (state.state != null) CollectMotion(state.state.motion, into);
                }
                foreach (var sub in sm.stateMachines)
                {
                    if (sub.stateMachine == null) continue;
                    foreach (var state in sub.stateMachine.states)
                    {
                        if (state.state != null) CollectMotion(state.state.motion, into);
                    }
                }
            }
        }

        private static void CollectMotion(Motion motion, HashSet<AnimationClip> into)
        {
            if (motion is AnimationClip clip) into.Add(clip);
            else if (motion is BlendTree tree)
            {
                var children = tree.children;
                if (children == null) return;
                foreach (var c in children) CollectMotion(c.motion, into);
            }
        }

        private static void RewriteClip(AnimationClip clip, Dictionary<Texture2D, TexRecord> recordByTexture,
            Dictionary<Material, Material> materialMap)
        {
            bool changed = false;

            foreach (var binding in AnimationUtility.GetObjectReferenceCurveBindings(clip))
            {
                var curve = AnimationUtility.GetObjectReferenceCurve(clip, binding);
                if (curve == null || curve.Length == 0) continue;

                bool bindingChanged = false;
                for (int i = 0; i < curve.Length; i++)
                {
                    var val = curve[i].value;
                    if (val is Texture2D tex && recordByTexture.TryGetValue(tex, out var record))
                    {
                        if (record.ResultTexture != null && record.ResultTexture != tex)
                        {
                            curve[i].value = record.ResultTexture;
                            bindingChanged = true;
                        }
                    }
                    else if (val is Material mat && materialMap != null &&
                             materialMap.TryGetValue(mat, out var rep) && rep != mat)
                    {
                        curve[i].value = rep;
                        bindingChanged = true;
                    }
                }

                if (bindingChanged)
                {
                    AnimationUtility.SetObjectReferenceCurve(clip, binding, curve);
                    changed = true;
                }
            }

            if (changed)
            {
                AtoLog.VerboseLog("Rewrote animation clip: " + clip.name);
                EditorUtility.SetDirty(clip);
            }
        }
    }
}
