// Animation scanning: discovers clips from all animators + avatar playable layers and extracts facts
// relevant to texture optimization: animated texture refs, ST transforms, object scale, enable/disable,
// material slot switching, and material property animations (render mode / cutoff).
// / 动画扫描：从所有 Animator 与 Avatar 可播放层发现剪辑，提取与贴图优化相关的要素：
// 动画贴图引用、ST 变换、物体缩放、启用/禁用、材质槽切换、材质属性动画（渲染模式/Cutoff）。

using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

namespace net.fosa.avatar_texture_optimizer.editor.analysis
{
    /// <summary>Animated texture reference: (path, slotIndex, propertyName, texture). / 动画贴图引用。</summary>
    public sealed class AnimatedTexRef
    {
        public string Path;
        public int SlotIndex;
        public string PropertyName;
        public Texture2D Texture;
    }

    /// <summary>Animated ST transform on a texture property -> unsafe. / 贴图属性的 ST 变换动画 → 不安全。</summary>
    public sealed class AnimatedStRef
    {
        public string Path;
        public int SlotIndex;
        public string PropertyName;
    }

    /// <summary>Animated float material property (e.g. _Cutoff). / 材质浮点属性动画。</summary>
    public sealed class AnimatedMatProp
    {
        public string Path;
        public int SlotIndex;
        public string PropertyName;
        public float MinValue = float.MaxValue;
        public float MaxValue = float.MinValue;
    }

    /// <summary>Everything the animation pass extracts. / 动画扫描的全部结果。</summary>
    public sealed class AnimationFacts
    {
        public readonly List<AnimatedTexRef> TextureRefs = new List<AnimatedTexRef>();
        public readonly List<AnimatedStRef> StRefs = new List<AnimatedStRef>();
        public readonly List<AnimatedMatProp> MaterialProps = new List<AnimatedMatProp>();
        public readonly Dictionary<string, float> MaxScaleByPath = new Dictionary<string, float>();
        public readonly HashSet<string> AnimatedActiveObjects = new HashSet<string>();
        public readonly HashSet<(string, int)> AnimatedMaterialSlots = new HashSet<(string, int)>();

        public float MaxScaleFor(string path) => MaxScaleByPath.TryGetValue(path, out var v) ? v : 1f;
    }

    /// <summary>
    /// Scans all animation clips reachable from the avatar. / 扫描 Avatar 可达的全部动画剪辑。
    /// </summary>
    public static class AnimationScanner
    {
        /// <summary>Scan the avatar. / 扫描 Avatar。</summary>
        public static AnimationFacts Scan(Transform avatarRoot)
        {
            var facts = new AnimationFacts();
            var roots = new List<Transform> { avatarRoot };
            var seenClips = new HashSet<AnimationClip>();

            // 1) Animators on the avatar / Avatar 上的 Animator
            foreach (var animator in avatarRoot.GetComponentsInChildren<Animator>(true))
            {
                roots.Add(animator.transform);
                if (animator.runtimeAnimatorController is AnimatorController ac)
                {
                    CollectClips(ac, seenClips);
                }
            }

            // 2) Avatar playable layers / Avatar 可播放层
            var desc = avatarRoot.GetComponent<VRCAvatarDescriptor>();
            if (desc != null)
            {
                var layers = desc.baseAnimationLayers;
                if (layers != null)
                {
                    foreach (var layer in layers)
                    {
                        if (layer != null && layer.animatorController != null && layer.isDefault == false &&
                            layer.animatorController is AnimatorController ac)
                        {
                            CollectClips(ac, seenClips);
                        }
                    }
                }
                var specials = desc.specialAnimationLayers;
                if (specials != null)
                {
                    foreach (var layer in specials)
                    {
                        if (layer != null && layer.animatorController is AnimatorController ac)
                        {
                            CollectClips(ac, seenClips);
                        }
                    }
                }
            }

            // 3) Analyze each clip / 分析每个剪辑
            foreach (var clip in seenClips)
            {
                AnalyzeClip(clip, roots, facts);
            }

            return facts;
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
                    if (state.state == null) continue;
                    CollectMotion(state.state.motion, into);
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
            if (motion is AnimationClip clip)
            {
                into.Add(clip);
            }
            else if (motion is BlendTree tree)
            {
                var children = tree.children;
                if (children == null) return;
                foreach (var c in children) CollectMotion(c.motion, into);
            }
        }

        private static void AnalyzeClip(AnimationClip clip, List<Transform> roots, AnimationFacts facts)
        {
            // Float curves / 浮点曲线
            foreach (var binding in AnimationUtility.GetCurveBindings(clip))
            {
                var curve = AnimationUtility.GetEditorCurve(clip, binding);
                if (curve == null || curve.length == 0) continue;

                var prop = binding.propertyName ?? "";

                // Transform scale / 物体缩放
                if (binding.type == typeof(Transform) && prop.StartsWith("m_LocalScale", StringComparison.Ordinal))
                {
                    float maxAbs = 0f;
                    for (int i = 0; i < curve.keys.Length; i++)
                    {
                        maxAbs = Mathf.Max(maxAbs, Mathf.Abs(curve.keys[i].value));
                    }
                    facts.MaxScaleByPath.TryGetValue(binding.path, out var cur);
                    facts.MaxScaleByPath[binding.path] = Mathf.Max(cur, maxAbs);
                    continue;
                }

                // GameObject / Renderer active / 启用与禁用
                if ((binding.type == typeof(GameObject) && prop == "m_IsActive") ||
                    (typeof(Renderer).IsAssignableFrom(binding.type) && prop == "m_Enabled"))
                {
                    facts.AnimatedActiveObjects.Add(binding.path);
                    continue;
                }

                // Material property float curves (e.g. _Cutoff, _Mode, ST components)
                if (IsMaterialPropBinding(binding))
                {
                    ParseMaterialPropBinding(binding, prop, out int slot, out string matProp);
                    if (matProp.IndexOf("_ST", StringComparison.Ordinal) >= 0)
                    {
                        // animated ST -> unsafe for that texture property / ST 动画 → 该贴图属性不安全
                        facts.StRefs.Add(new AnimatedStRef { Path = binding.path, SlotIndex = slot, PropertyName = matProp.Replace("_ST", "") });
                        continue;
                    }

                    float min = float.MaxValue, max = float.MinValue;
                    for (int i = 0; i < curve.keys.Length; i++)
                    {
                        min = Mathf.Min(min, curve.keys[i].value);
                        max = Mathf.Max(max, curve.keys[i].value);
                    }
                    facts.MaterialProps.Add(new AnimatedMatProp
                    {
                        Path = binding.path,
                        SlotIndex = slot,
                        PropertyName = matProp,
                        MinValue = min,
                        MaxValue = max,
                    });
                }
            }

            // Object reference curves (textures / materials) / 对象引用曲线（贴图/材质）
            foreach (var binding in AnimationUtility.GetObjectReferenceCurveBindings(clip))
            {
                var curve = AnimationUtility.GetObjectReferenceCurve(clip, binding);
                if (curve == null || curve.Length == 0) continue;
                var prop = binding.propertyName ?? "";

                // Material slot material switch / 材质槽的材质切换
                if (typeof(Renderer).IsAssignableFrom(binding.type) && prop.StartsWith("m_Materials.Array.data[", StringComparison.Ordinal))
                {
                    if (TryParseSlotIndex(prop, out int slot))
                    {
                        facts.AnimatedMaterialSlots.Add((binding.path, slot));
                    }
                    continue;
                }

                // Material texture reference / 材质贴图引用
                if (IsMaterialPropBinding(binding))
                {
                    ParseMaterialPropBinding(binding, prop, out int slot, out string matProp);
                    if (matProp.Length == 0) continue;
                    foreach (var frame in curve)
                    {
                        if (frame.value is Texture2D tex)
                        {
                            facts.TextureRefs.Add(new AnimatedTexRef
                            {
                                Path = binding.path,
                                SlotIndex = slot,
                                PropertyName = matProp,
                                Texture = tex,
                            });
                        }
                    }
                }
            }
        }

        private static bool IsMaterialPropBinding(EditorCurveBinding binding)
        {
            if (binding.type == typeof(Material)) return true;
            if (typeof(Renderer).IsAssignableFrom(binding.type))
            {
                var prop = binding.propertyName ?? "";
                return prop.StartsWith("m_Materials.Array.data[", StringComparison.Ordinal)
                       && prop.IndexOf("._", StringComparison.Ordinal) > 0;
            }
            return false;
        }

        /// <summary>
        /// Extract slot index and shader property name from a material curve property.
        /// Supports "material._MainTex" (type Material) and "m_Materials.Array.data[i]._MainTex" (type Renderer).
        /// / 从材质曲线属性解析槽索引与着色器属性名。
        /// </summary>
        private static void ParseMaterialPropBinding(EditorCurveBinding binding, string prop,
            out int slot, out string matProp)
        {
            slot = -1;
            matProp = prop;
            if (binding.type == typeof(Material))
            {
                if (matProp.StartsWith("material.", StringComparison.Ordinal))
                    matProp = matProp.Substring("material.".Length);
                return;
            }
            // renderer form / 渲染器形式
            int br = prop.IndexOf('[', StringComparison.Ordinal);
            int dot = prop.IndexOf("._", StringComparison.Ordinal);
            if (br > 0 && dot > br)
            {
                int close = prop.IndexOf(']', br);
                if (close > br && int.TryParse(prop.Substring(br + 1, close - br - 1), out slot))
                {
                    matProp = prop.Substring(dot + 1);
                }
            }
        }

        private static bool TryParseSlotIndex(string prop, out int slot)
        {
            slot = -1;
            int br = prop.IndexOf('[', StringComparison.Ordinal);
            int close = prop.IndexOf(']', br + 1);
            if (br > 0 && close > br)
            {
                return int.TryParse(prop.Substring(br + 1, close - br - 1), out slot);
            }
            return false;
        }
    }
}
