// ============================================================================
// AvatarTextureOptimizer (net.fosa.avatar-texture-optimizer)
// Analysis/AnimationAnalyzer.cs — 动画分析 / Animation analysis
//
// 需求: 遍历模型身上所有动画（Animator + VRC Descriptor 各层），找出对材质有影响的
//       部分（贴图切换/材质切换/ST变换/渲染模式/Cutoff/物体启用禁用/网格切换/缩放）。
// 共识:
//  - 同时解析 Animator 组件与 VRCAvatarDescriptor 的 playable layers (Base/Gesture/Action/FX/Additive)。
//  - 材质属性绑定格式: Renderer 上 "materials[N]._Prop"（float 曲线）或对象引用曲线。
//  - 动画切换的贴图/材质会并入 UV 映射（由 AvatarAnalyzer 消费）。
//  - 动画修改 ST → 该贴图白名单；动画修改 m_Mesh → 该渲染器槽白名单。
// ============================================================================
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

namespace net.fosa.avatar_texture_optimizer.editor
{
    /// <summary>
    /// 渲染器上材质槽动画信息 / Animated material slot info.
    /// </summary>
    public sealed class SlotAnimInfo
    {
        /// <summary>该槽位动画切换过的贴图 (property → textures) / Textures swapped on this slot</summary>
        public Dictionary<string, HashSet<Texture2D>> textureSwaps = new Dictionary<string, HashSet<Texture2D>>();

        /// <summary>该槽位动画切换过的材质 / Materials swapped onto this slot</summary>
        public HashSet<Material> materialSwaps = new HashSet<Material>();

        /// <summary>动画修改过的 float 属性（如 _Cutoff、_MainTex_ST 分量）/ Animated float properties</summary>
        public HashSet<string> floatProps = new HashSet<string>();

        /// <summary>动画中的 _Cutoff 范围（最严苛评估用） / Animated _Cutoff range (for strictest evaluation)</summary>
        public float cutoffMin = float.MaxValue;
        public float cutoffMax = float.MinValue;
    }

    /// <summary>
    /// 整个 Avatar 的动画分析结果 / Animation analysis result for the whole avatar.
    /// </summary>
    public sealed class AnimationData
    {
        public List<AnimationClip> clips = new List<AnimationClip>();

        /// <summary>涉及的全部控制器（补丁阶段替换 clip 引用用）/
        /// All involved controllers (for clip reference replacement during patching)</summary>
        public List<RuntimeAnimatorController> controllers = new List<RuntimeAnimatorController>();

        /// <summary>渲染器 → 槽位动画信息 / Renderer → slot animation info</summary>
        public Dictionary<Renderer, Dictionary<int, SlotAnimInfo>> slotAnims = new Dictionary<Renderer, Dictionary<int, SlotAnimInfo>>();

        /// <summary>渲染器 m_Enabled 被动画过（且至少一次为 true 才算"有动画启用"）/
        /// Renderer whose m_Enabled is animated (eligible if ever enabled by animation)</summary>
        public HashSet<Renderer> animatedEnabledRenderers = new HashSet<Renderer>();

        /// <summary>被动画启用过的渲染器（m_Enabled 曲线中存在 true 值的帧）/
        /// Renderers that get enabled by animation at some point</summary>
        public HashSet<Renderer> everEnabledByAnimation = new HashSet<Renderer>();

        /// <summary>渲染器 m_Mesh 被动画切换 → 槽位白名单 / Renderer with animated m_Mesh → slot whitelist</summary>
        public HashSet<Renderer> animatedMeshSwap = new HashSet<Renderer>();

        /// <summary>Transform 最大动画缩放（各轴分别取最大，面积用最大轴平方，保守）/
        /// Max animated scale per transform (per-axis max; area uses max-axis squared, conservative)</summary>
        public Dictionary<Transform, Vector3> maxScale = new Dictionary<Transform, Vector3>();

        /// <summary>Transform 基础缩放是否被动画过 / Whether local scale is animated</summary>
        public HashSet<Transform> animatedScale = new HashSet<Transform>();
    }

    /// <summary>
    /// 动画分析器 / Animation analyzer.
    /// </summary>
    public static class AnimationAnalyzer
    {
        private static readonly char[] DotSplit = { '.' };

        /// <summary>
        /// 分析 Avatar 上全部动画 / Analyze all animations on the avatar.
        /// </summary>
        public static AnimationData Analyze(GameObject root, ShaderAnalyzer.LogContext ctx)
        {
            var data = new AnimationData();
            var seenClips = new HashSet<AnimationClip>();

            var controllers = CollectControllers(root);
            data.controllers = controllers;
            foreach (var controller in controllers)
            {
                if (controller == null) continue;
                if (controller is AnimatorController ac)
                {
                    foreach (var layer in ac.layers)
                    {
                        CollectClipsFromStateMachine(layer.stateMachine, seenClips, data.clips);
                    }
                }
                else if (controller is AnimatorOverrideController oc)
                {
                    foreach (var clip in oc.animationClips)
                    {
                        if (clip != null && seenClips.Add(clip)) data.clips.Add(clip);
                    }
                }
            }

            // 解析每个 clip / Parse each clip
            foreach (var clip in data.clips)
            {
                ParseClip(root, clip, data, ctx);
            }

            return data;
        }

        /// <summary>
        /// 收集全部 RuntimeAnimatorController（Animator + VRC playable layers）/
        /// Collect all animator controllers (Animator components + VRC playable layers).
        /// </summary>
        private static List<RuntimeAnimatorController> CollectControllers(GameObject root)
        {
            var controllers = new List<RuntimeAnimatorController>();
            var seen = new HashSet<RuntimeAnimatorController>();

            foreach (var animator in root.GetComponentsInChildren<Animator>(true))
            {
                if (animator.runtimeAnimatorController != null && seen.Add(animator.runtimeAnimatorController))
                {
                    controllers.Add(animator.runtimeAnimatorController);
                }
            }

            var descriptor = root.GetComponent<VRCAvatarDescriptor>();
            if (descriptor != null)
            {
                CollectLayerControllers(descriptor.baseAnimationLayers, seen, controllers);
                CollectLayerControllers(descriptor.specialAnimationLayers, seen, controllers);
                if (descriptor.customizeAnimationLayers)
                {
                    CollectLayerControllers(descriptor.baseAnimationLayers, seen, controllers);
                }
            }

            return controllers;
        }

        private static void CollectLayerControllers(IEnumerable<VRCAvatarDescriptor.CustomAnimLayer> layers,
            HashSet<RuntimeAnimatorController> seen, List<RuntimeAnimatorController> controllers)
        {
            foreach (var layer in layers)
            {
                if (layer.animatorController != null && seen.Add(layer.animatorController))
                {
                    controllers.Add(layer.animatorController);
                }
            }
        }

        private static void CollectClipsFromStateMachine(AnimatorStateMachine sm,
            HashSet<AnimationClip> seen, List<AnimationClip> clips)
        {
            if (sm == null) return;
            foreach (var state in sm.states)
            {
                CollectMotion(state.state.motion, seen, clips);
            }
            foreach (var sub in sm.stateMachines)
            {
                CollectClipsFromStateMachine(sub.stateMachine, seen, clips);
            }
        }

        private static void CollectMotion(Motion motion, HashSet<AnimationClip> seen, List<AnimationClip> clips)
        {
            if (motion is AnimationClip clip)
            {
                if (seen.Add(clip)) clips.Add(clip);
            }
            else if (motion is BlendTree tree)
            {
                foreach (var child in tree.children)
                {
                    CollectMotion(child.motion, seen, clips);
                }
            }
        }

        private static void ParseClip(GameObject root, AnimationClip clip, AnimationData data, ShaderAnalyzer.LogContext ctx)
        {
            try
            {
                // float 曲线 / Float curves
                foreach (var binding in AnimationUtility.GetCurveBindings(clip))
                {
                    var obj = AnimationUtility.GetAnimatedObject(root, binding);
                    if (obj == null) continue;

                    if (obj is Transform tr)
                    {
                        var prop = binding.propertyName;
                        if (prop.StartsWith("m_LocalScale.", System.StringComparison.Ordinal))
                        {
                            data.animatedScale.Add(tr);
                            var curve = AnimationUtility.GetEditorCurve(clip, binding);
                            if (curve != null && curve.keys.Length > 0)
                            {
                                float maxV = float.MinValue;
                                foreach (var k in curve.keys) maxV = Mathf.Max(maxV, k.value);
                                if (!data.maxScale.TryGetValue(tr, out var s))
                                {
                                    s = tr.localScale;
                                    data.maxScale[tr] = s;
                                }
                                // 合并到对应轴 / Merge into the matching axis
                                var axis = prop.Substring("m_LocalScale.".Length);
                                var v = data.maxScale[tr];
                                if (axis == "x") v.x = Mathf.Max(Mathf.Abs(v.x), Mathf.Abs(maxV));
                                else if (axis == "y") v.y = Mathf.Max(Mathf.Abs(v.y), Mathf.Abs(maxV));
                                else if (axis == "z") v.z = Mathf.Max(Mathf.Abs(v.z), Mathf.Abs(maxV));
                                data.maxScale[tr] = v;
                            }
                        }
                    }
                    else if (obj is Renderer r)
                    {
                        var prop = binding.propertyName;
                        if (prop == "m_Enabled")
                        {
                            data.animatedEnabledRenderers.Add(r);
                            var curve = AnimationUtility.GetEditorCurve(clip, binding);
                            if (curve != null)
                            {
                                foreach (var k in curve.keys)
                                {
                                    if (k.value > 0.5f) { data.everEnabledByAnimation.Add(r); break; }
                                }
                            }
                        }
                        else if (TryParseMaterialBinding(prop, out var slot, out var matProp))
                        {
                            var info = GetSlotInfo(data, r, slot);
                            info.floatProps.Add(matProp);

                            // _Cutoff 范围（最严苛评估） / _Cutoff range
                            if (matProp == "_Cutoff")
                            {
                                var curve = AnimationUtility.GetEditorCurve(clip, binding);
                                if (curve != null)
                                {
                                    foreach (var k in curve.keys)
                                    {
                                        info.cutoffMin = Mathf.Min(info.cutoffMin, k.value);
                                        info.cutoffMax = Mathf.Max(info.cutoffMax, k.value);
                                    }
                                }
                            }

                            // ST 变换动画记录在 floatProps 中（形如 "_MainTex_ST"），
                            // 由 AvatarAnalyzer 检测并白名单对应贴图 /
                            // ST animation is recorded in floatProps (e.g. "_MainTex_ST");
                            // AvatarAnalyzer will whitelist the corresponding texture.
                        }
                    }
                }

                // 对象引用曲线 / Object reference curves
                foreach (var binding in AnimationUtility.GetObjectReferenceCurveBindings(clip))
                {
                    var obj = AnimationUtility.GetAnimatedObject(root, binding);
                    if (obj == null) continue;

                    if (obj is Renderer r)
                    {
                        var prop = binding.propertyName;
                        if (prop == "m_Mesh")
                        {
                            data.animatedMeshSwap.Add(r);
                            continue;
                        }
                        if (prop.StartsWith("m_Materials.Array.data[", System.StringComparison.Ordinal) && prop.EndsWith("]", System.StringComparison.Ordinal))
                        {
                            // 材质槽整体切换 / Material slot swap
                            int slot = ParseSlotIndex(prop.Substring("m_Materials.Array.data[".Length));
                            if (slot >= 0)
                            {
                                var info = GetSlotInfo(data, r, slot);
                                foreach (var kf in AnimationUtility.GetObjectReferenceCurve(clip, binding))
                                {
                                    if (kf.value is Material m) info.materialSwaps.Add(m);
                                }
                            }
                            continue;
                        }
                        if (TryParseMaterialBinding(prop, out var slot2, out var matProp2))
                        {
                            // 贴图属性对象曲线（贴图切换）/ Texture property object curve
                            var info = GetSlotInfo(data, r, slot2);
                            if (!info.textureSwaps.TryGetValue(matProp2, out var set))
                            {
                                set = new HashSet<Texture2D>();
                                info.textureSwaps[matProp2] = set;
                            }
                            foreach (var kf in AnimationUtility.GetObjectReferenceCurve(clip, binding))
                            {
                                if (kf.value is Texture2D t) set.Add(t);
                            }
                        }
                    }
                }
            }
            catch (System.Exception e)
            {
                Log.Warning($"Failed to parse clip '{clip.name}': {e.Message}");
            }
        }

        /// <summary>
        /// 解析 "materials[N]._Prop" 绑定 / Parse "materials[N]._Prop" binding.
        /// </summary>
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

        private static SlotAnimInfo GetSlotInfo(AnimationData data, Renderer r, int slot)
        {
            if (!data.slotAnims.TryGetValue(r, out var map))
            {
                map = new Dictionary<int, SlotAnimInfo>();
                data.slotAnims[r] = map;
            }
            if (!map.TryGetValue(slot, out var info))
            {
                info = new SlotAnimInfo();
                map[slot] = info;
            }
            return info;
        }
    }
}
