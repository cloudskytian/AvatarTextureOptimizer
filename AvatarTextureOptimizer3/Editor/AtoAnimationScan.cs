// English: Walk animator controllers / clips for material, texture, enable, scale, ST curves.
// 中文：遍历动画控制器与片段，收集材质/贴图/启用/缩放/ST 曲线。
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace net.fosa.ato.editor
{
    public sealed class AtoAnimInfo
    {
        public readonly HashSet<Renderer> AnimatedEnable = new HashSet<Renderer>();
        public readonly HashSet<GameObject> AnimatedGoEnable = new HashSet<GameObject>();
        public readonly Dictionary<Renderer, float> MaxLossyScaleMul = new Dictionary<Renderer, float>();
        public readonly HashSet<string> StAnimatedProperties = new HashSet<string>(); // material.prop_ST
        public readonly List<MatSwap> MaterialSwaps = new List<MatSwap>();
        public readonly List<TexSwap> TextureSwaps = new List<TexSwap>();
        public readonly List<FloatAnim> FloatAnims = new List<FloatAnim>();

        public struct MatSwap
        {
            public Renderer Renderer;
            public int Slot;
            public Material Material;
        }
        public struct TexSwap
        {
            public Renderer Renderer;
            public int Slot;
            public string Property;
            public Texture2D Texture;
        }
        public struct FloatAnim
        {
            public Renderer Renderer;
            public int Slot;
            public string Property;
            public float Min, Max;
        }
    }

    public static class AtoAnimationScan
    {
        public static AtoAnimInfo Scan(GameObject root)
        {
            var info = new AtoAnimInfo();
            var clips = new HashSet<AnimationClip>();

            foreach (var anim in root.GetComponentsInChildren<Animator>(true))
                CollectFromAnimator(anim, clips);
            foreach (var a in root.GetComponentsInChildren<Animation>(true))
            {
                if (a.clip) clips.Add(a.clip);
                foreach (AnimationState st in a)
                    if (st != null && st.clip) clips.Add(st.clip);
            }

#if ATO_VRCSDK3
            CollectVrcClips(root, clips);
#endif
            CollectVrcClipsByReflection(root, clips);

            foreach (var clip in clips)
                ParseClip(root, clip, info);

            AtoLog.Info($"Animation scan: clips={clips.Count} matSwaps={info.MaterialSwaps.Count} texSwaps={info.TextureSwaps.Count} stAnims={info.StAnimatedProperties.Count}");
            return info;
        }

        private static void CollectFromAnimator(Animator anim, HashSet<AnimationClip> clips)
        {
            if (anim == null || anim.runtimeAnimatorController == null) return;
            foreach (var c in anim.runtimeAnimatorController.animationClips)
                if (c) clips.Add(c);
            if (anim.runtimeAnimatorController is AnimatorController ac)
                WalkAc(ac, clips);
        }

        private static void WalkAc(AnimatorController ac, HashSet<AnimationClip> clips)
        {
            if (ac == null) return;
            foreach (var layer in ac.layers)
                WalkSm(layer.stateMachine, clips, new HashSet<AnimatorStateMachine>());
        }

        private static void WalkSm(AnimatorStateMachine sm, HashSet<AnimationClip> clips, HashSet<AnimatorStateMachine> seen)
        {
            if (sm == null || !seen.Add(sm)) return;
            foreach (var s in sm.states)
            {
                var motion = s.state != null ? s.state.motion : null;
                CollectMotion(motion, clips);
            }
            foreach (var sub in sm.stateMachines)
                WalkSm(sub.stateMachine, clips, seen);
        }

        private static void CollectMotion(Motion m, HashSet<AnimationClip> clips)
        {
            if (m == null) return;
            if (m is AnimationClip c) { clips.Add(c); return; }
            if (m is BlendTree bt && bt.children != null)
            {
                foreach (var ch in bt.children)
                    CollectMotion(ch.motion, clips);
            }
        }

        private static void CollectVrcClipsByReflection(GameObject root, HashSet<AnimationClip> clips)
        {
            var t = Type.GetType("VRC.SDK3.Avatars.Components.VRCAvatarDescriptor, VRC.SDK3A");
            if (t == null) return;
            var desc = root.GetComponent(t);
            if (desc == null) return;
            try
            {
                var layers = t.GetField("baseAnimationLayers")?.GetValue(desc) as Array;
                AddLayerClips(layers, clips);
                layers = t.GetField("specialAnimationLayers")?.GetValue(desc) as Array;
                AddLayerClips(layers, clips);
            }
            catch (Exception e) { AtoLog.VerboseInfo("VRC clip reflect: " + e.Message); }
        }

        private static void AddLayerClips(Array layers, HashSet<AnimationClip> clips)
        {
            if (layers == null) return;
            foreach (var layer in layers)
            {
                if (layer == null) continue;
                var f = layer.GetType().GetField("animatorController");
                var c = f != null ? f.GetValue(layer) as RuntimeAnimatorController : null;
                if (c == null) continue;
                foreach (var clip in c.animationClips) if (clip) clips.Add(clip);
                if (c is AnimatorController ac) WalkAc(ac, clips);
            }
        }

#if ATO_VRCSDK3
        private static void CollectVrcClips(GameObject root, HashSet<AnimationClip> clips)
        {
            var d = root.GetComponent<VRC.SDK3.Avatars.Components.VRCAvatarDescriptor>();
            if (d == null) return;
            void add(VRC.SDK3.Avatars.Components.VRCAvatarDescriptor.CustomAnimLayer[] arr)
            {
                if (arr == null) return;
                foreach (var l in arr)
                    if (l.animatorController)
                    {
                        foreach (var c in l.animatorController.animationClips) if (c) clips.Add(c);
                        if (l.animatorController is AnimatorController ac) WalkAc(ac, clips);
                    }
            }
            add(d.baseAnimationLayers);
            add(d.specialAnimationLayers);
        }
#endif

        private static void ParseClip(GameObject root, AnimationClip clip, AtoAnimInfo info)
        {
            if (clip == null) return;
            foreach (var binding in AnimationUtility.GetCurveBindings(clip))
            {
                var curve = AnimationUtility.GetEditorCurve(clip, binding);
                if (curve == null) continue;
                var target = AnimationUtility.GetAnimatedObject(root, binding) as GameObject;
                Transform tr = null;
                if (target != null) tr = target.transform;
                else
                {
                    var path = binding.path ?? "";
                    tr = string.IsNullOrEmpty(path) ? root.transform : root.transform.Find(path);
                }

                var prop = binding.propertyName ?? "";
                var type = binding.type;

                if (type == typeof(GameObject) && prop == "m_IsActive" && tr != null)
                    info.AnimatedGoEnable.Add(tr.gameObject);

                if (typeof(Renderer).IsAssignableFrom(type) && (prop == "m_Enabled") && tr != null)
                {
                    var r = tr.GetComponent(type) as Renderer;
                    if (r) info.AnimatedEnable.Add(r);
                }

                if (tr != null && (prop.StartsWith("m_LocalScale") || prop.StartsWith("localScale")))
                {
                    float max = 1f;
                    foreach (var k in curve.keys) max = Mathf.Max(max, Mathf.Abs(k.value));
                    foreach (var r in tr.GetComponentsInChildren<Renderer>(true))
                    {
                        info.MaxLossyScaleMul.TryGetValue(r, out var cur);
                        info.MaxLossyScaleMul[r] = Mathf.Max(cur, max);
                    }
                }

                if (prop.Contains("_ST") || prop.Contains("ScrollRotate"))
                    info.StAnimatedProperties.Add(prop);

                if (prop.Contains("Cutoff") || prop.Contains("TransparentMode") || prop.Contains("Mode"))
                {
                    float mn = float.PositiveInfinity, mx = float.NegativeInfinity;
                    foreach (var k in curve.keys) { mn = Mathf.Min(mn, k.value); mx = Mathf.Max(mx, k.value); }
                    if (tr != null)
                    {
                        var r = tr.GetComponent<Renderer>();
                        if (r) info.FloatAnims.Add(new AtoAnimInfo.FloatAnim
                        {
                            Renderer = r, Slot = 0, Property = prop, Min = mn, Max = mx
                        });
                    }
                }
            }

            foreach (var binding in AnimationUtility.GetObjectReferenceCurveBindings(clip))
            {
                var keys = AnimationUtility.GetObjectReferenceCurve(clip, binding);
                if (keys == null) continue;
                var path = binding.path ?? "";
                var tr = string.IsNullOrEmpty(path) ? root.transform : root.transform.Find(path);
                if (tr == null) continue;
                var r = tr.GetComponent<Renderer>();
                int slot = ParseMaterialSlot(binding.propertyName);
                foreach (var k in keys)
                {
                    if (k.value is Material m)
                        info.MaterialSwaps.Add(new AtoAnimInfo.MatSwap { Renderer = r, Slot = slot, Material = m });
                    if (k.value is Texture2D tex)
                        info.TextureSwaps.Add(new AtoAnimInfo.TexSwap
                        {
                            Renderer = r, Slot = slot, Property = binding.propertyName, Texture = tex
                        });
                }
            }
        }

        private static int ParseMaterialSlot(string prop)
        {
            // m_Materials.Array.data[2]
            if (string.IsNullOrEmpty(prop)) return 0;
            var i = prop.IndexOf('[');
            var j = prop.IndexOf(']');
            if (i >= 0 && j > i && int.TryParse(prop.Substring(i + 1, j - i - 1), out var s))
                return s;
            return 0;
        }
    }
}
