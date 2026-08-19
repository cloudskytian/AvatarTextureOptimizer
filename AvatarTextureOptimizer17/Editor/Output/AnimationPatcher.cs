// ============================================================================
// AvatarTextureOptimizer (net.fosa.avatar-texture-optimizer)
// Output/AnimationPatcher.cs — 动画修补 / Animation clip patching
//
// 需求: 动画中切换贴图/材质的工作在贴图优化后更新对应引用；
//       若同一网格有不透明材质合并，则应合并材质槽并更新如动画之类的相应引用与材质槽索引。
// 实现:
//  - 只克隆需要修改的 clip，绝不原地修改共享资产。
//  - 通过 NDMF ObjectRegistry.RegisterReplacedObject 登记替换，并直接在控制器内
//    替换 motion 引用（双保险）。
//  - 材质槽索引重映射由 FinalDeduper 统一处理（合并槽位后调用本类补丁）。
// ============================================================================
using System.Collections.Generic;
using nadena.dev.ndmf;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace net.fosa.avatar_texture_optimizer.editor
{
    /// <summary>
    /// 动画修补器 / Animation patcher.
    /// </summary>
    public static class AnimationPatcher
    {
        /// <summary>
        /// 修补全部 clip / Patch all clips.
        /// </summary>
        public static int Patch(GameObject root, AnimationData anim, MaterialPatchResult matResult, BuildContext ctx)
        {
            int patched = 0;
            foreach (var clip in anim.clips)
            {
                if (clip == null) continue;
                if (TryPatchClip(root, clip, matResult, out var newClip))
                {
                    // 克隆 + 替换引用 / clone + replace references
                    if (newClip == null)
                    {
                        newClip = Object.Instantiate(clip);
                        newClip.name = clip.name + " (ATO)";
                    }
                    ctx.ObjectRegistry.RegisterReplacedObject(clip, newClip);
                    ReplaceClipInControllers(anim.controllers, clip, newClip);
                    patched++;
                }
            }
            return patched;
        }

        /// <summary>
        /// 检查 clip 是否需要修补；若需要则原地修改并返回 true（随后调用方克隆）/
        /// Check & mutate the clip in place (caller clones afterwards).
        /// </summary>
        private static bool TryPatchClip(GameObject root, AnimationClip clip, MaterialPatchResult matResult,
            out AnimationClip result)
        {
            result = null;
            bool changed = false;

            // 对象引用曲线（贴图切换/材质切换）/ object reference curves
            var bindings = AnimationUtility.GetObjectReferenceCurveBindings(clip);
            foreach (var binding in bindings)
            {
                var obj = AnimationUtility.GetAnimatedObject(root, binding);
                if (!(obj is Renderer r)) continue;

                var keyframes = AnimationUtility.GetObjectReferenceCurve(clip, binding);
                if (keyframes == null || keyframes.Length == 0) continue;

                bool bindingChanged = false;
                var newKeyframes = (ObjectReferenceKeyframe[])keyframes.Clone();

                if (TryParseMaterialBinding(binding.propertyName, out var slot, out var matProp))
                {
                    // 贴图属性切换 / texture property swap
                    if (matResult.bindingTexture.TryGetValue((r, slot, matProp), out var newTex))
                    {
                        for (int i = 0; i < newKeyframes.Length; i++)
                        {
                            if (newKeyframes[i].value is Texture2D oldTex && oldTex != newTex)
                            {
                                newKeyframes[i].value = newTex;
                                bindingChanged = true;
                            }
                        }
                    }
                }
                else if (binding.propertyName.StartsWith("m_Materials.Array.data[", System.StringComparison.Ordinal))
                {
                    // 材质槽整体切换 / material slot swap
                    int slotIdx = ParseSlotIndex(binding.propertyName.Substring("m_Materials.Array.data[".Length));
                    if (slotIdx >= 0)
                    {
                        for (int i = 0; i < newKeyframes.Length; i++)
                        {
                            if (newKeyframes[i].value is Material oldMat &&
                                matResult.materialMap.TryGetValue(oldMat, out var newMat) && newMat != oldMat)
                            {
                                newKeyframes[i].value = newMat;
                                bindingChanged = true;
                            }
                        }
                    }
                }

                if (bindingChanged)
                {
                    AnimationUtility.SetObjectReferenceCurve(clip, binding, newKeyframes);
                    changed = true;
                }
            }

            return changed;
        }

        /// <summary>解析 "materials[N]._Prop" / parse "materials[N]._Prop"</summary>
        private static bool TryParseMaterialBinding(string prop, out int slot, out string matProp)
        {
            slot = -1;
            matProp = null;
            if (!prop.StartsWith("materials[", System.StringComparison.Ordinal)) return false;
            int close = prop.IndexOf(']');
            if (close < 0) return false;
            if (!int.TryParse(prop.Substring("materials[".Length, close - "materials[".Length), out slot)) return false;
            if (close + 1 >= prop.Length || prop[close + 1] != '.') return false;
            matProp = prop.Substring(close + 2);
            return true;
        }

        private static int ParseSlotIndex(string s)
        {
            int end = s.IndexOf(']');
            if (end < 0) return -1;
            return int.TryParse(s.Substring(0, end), out var i) ? i : -1;
        }

        /// <summary>在控制器中替换 clip 引用 / replace clip references in controllers</summary>
        private static void ReplaceClipInControllers(List<RuntimeAnimatorController> controllers,
            AnimationClip oldClip, AnimationClip newClip)
        {
            foreach (var controller in controllers)
            {
                if (!(controller is AnimatorController ac)) continue;
                foreach (var layer in ac.layers)
                {
                    ReplaceInStateMachine(layer.stateMachine, oldClip, newClip);
                }
            }
        }

        private static void ReplaceInStateMachine(AnimatorStateMachine sm, AnimationClip oldClip, AnimationClip newClip)
        {
            if (sm == null) return;
            foreach (var state in sm.states)
            {
                if (state.state.motion == oldClip) state.state.motion = newClip;
            }
            foreach (var tree in sm.states)
            {
                if (tree.state.motion is BlendTree bt)
                {
                    ReplaceInBlendTree(bt, oldClip, newClip);
                }
            }
            foreach (var sub in sm.stateMachines)
            {
                ReplaceInStateMachine(sub.stateMachine, oldClip, newClip);
            }
        }

        private static void ReplaceInBlendTree(BlendTree tree, AnimationClip oldClip, AnimationClip newClip)
        {
            var children = tree.children;
            for (int i = 0; i < children.Length; i++)
            {
                if (children[i].motion == oldClip) children[i].motion = newClip;
                else if (children[i].motion is BlendTree sub)
                {
                    ReplaceInBlendTree(sub, oldClip, newClip);
                }
            }
            tree.children = children;
        }
    }
}
