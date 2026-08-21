using System;
using System.Collections.Generic;
using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;
using UnityEditor;
using UnityEngine;
#if ATO_VRCSDK3
using VRC.SDK3.Avatars.Components;
using Fosa.ATO;
#endif

namespace Fosa.ATO.Editor
{
    /// <summary>
    /// Collects animation-driven enable, scale, material/texture swaps, ST/scroll, cutoff/mode.
    /// After MA so merged FX is already on the avatar.
    /// 收集动画对启用、缩放、材质/贴图切换、ST、Cutoff 的影响。发生在 MA 之后。
    /// </summary>
    public sealed class AtoAnimInfo
    {
        public readonly HashSet<Transform> EnabledByAnim = new HashSet<Transform>();
        public readonly HashSet<Renderer> RenderersEnabledByAnim = new HashSet<Renderer>();
        public readonly Dictionary<Transform, float> MaxAbsScale = new Dictionary<Transform, float>();
        public readonly Dictionary<Renderer, List<Material[]>> ExtraMaterialSets = new Dictionary<Renderer, List<Material[]>>();
        public readonly Dictionary<(Renderer, int slot, string prop), List<Texture2D>> ExtraTextures
            = new Dictionary<(Renderer, int, string), List<Texture2D>>();
        public readonly HashSet<(Renderer, string prop)> HasTexTransformAnim = new HashSet<(Renderer, string)>();
        public readonly Dictionary<(Renderer, int slot), List<(AtoAlphaMode mode, float cutoff)>> ExtraAlpha
            = new Dictionary<(Renderer, int), List<(AtoAlphaMode, float)>>();
        public readonly List<AnimationClip> Clips = new List<AnimationClip>();
        public readonly List<VirtualClip> VirtualClips = new List<VirtualClip>();
    }

    public static class AtoAnimationScanner
    {
        public static AtoAnimInfo Scan(BuildContext ctx)
        {
            var info = new AtoAnimInfo();
            var root = ctx.AvatarRootTransform;

            AnimatorServicesContext asc = null;
            try { asc = ctx.Extension<AnimatorServicesContext>(); }
            catch { /* not active */ }

            if (asc != null)
            {
                foreach (var clip in CollectVirtual(asc))
                {
                    info.VirtualClips.Add(clip);
                    ScanVirtual(clip, root, info);
                }
            }
            // Always also scan serialized clips on the avatar (covers float ST/scale/cutoff
            // curves that are not in ClipsWithObjectCurves).
            // 同时扫描 Avatar 上的序列化 clip，覆盖纯 float 曲线。
            foreach (var clip in CollectRaw(ctx.AvatarRootObject))
            {
                info.Clips.Add(clip);
                ScanRaw(clip, root, info);
            }

            AtoLog.Detail("Animation clips scanned: v=" + info.VirtualClips.Count + " raw=" + info.Clips.Count);
            return info;
        }

        static IEnumerable<VirtualClip> CollectVirtual(AnimatorServicesContext asc)
        {
            var set = new HashSet<VirtualClip>();
            foreach (var c in asc.AnimationIndex.ClipsWithObjectCurves) set.Add(c);
            // Also float curves (scale, cutoff, ST). 也收集 float 曲线。
            try
            {
                foreach (var ctrl in asc.ControllerContext.GetAllControllers())
                    CollectFromNode(ctrl, set);
            }
            catch (Exception e)
            {
                AtoLog.Detail("Virtual controller walk: " + e.Message);
            }
            return set;
        }

        static void CollectFromNode(VirtualNode node, HashSet<VirtualClip> set)
        {
            if (node == null) return;
            if (node is VirtualClip vc) { set.Add(vc); return; }
            // VirtualNode does not expose a universal children API; AnimationIndex already covers object curves.
            // VirtualClip.From walk is best-effort. 其余 clip 由 AnimationIndex 覆盖。
        }

        static IEnumerable<AnimationClip> CollectRaw(GameObject root)
        {
            var set = new HashSet<AnimationClip>();
            foreach (var an in root.GetComponentsInChildren<Animator>(true))
                CollectFromRuntime(an.runtimeAnimatorController, set);
#if ATO_VRCSDK3
            var desc = root.GetComponent<VRCAvatarDescriptor>();
            if (desc != null)
            {
                if (desc.baseAnimationLayers != null)
                    foreach (var l in desc.baseAnimationLayers)
                        CollectFromRuntime(l.animatorController, set);
                if (desc.specialAnimationLayers != null)
                    foreach (var l in desc.specialAnimationLayers)
                        CollectFromRuntime(l.animatorController, set);
            }
#endif
            return set;
        }

        static void CollectFromRuntime(RuntimeAnimatorController rac, HashSet<AnimationClip> set)
        {
            if (rac == null) return;
            var ac = rac as AnimatorControllerProxy;
            foreach (var c in rac.animationClips)
                if (c != null) set.Add(c);
            if (rac is UnityEditor.Animations.AnimatorController editor)
            {
                foreach (var layer in editor.layers)
                    WalkSm(layer.stateMachine, set, new HashSet<UnityEditor.Animations.AnimatorStateMachine>());
            }
        }

        // Dummy to keep name; Unity's AnimatorOverrideController is covered by animationClips.
        class AnimatorControllerProxy { }

        static void WalkSm(UnityEditor.Animations.AnimatorStateMachine sm, HashSet<AnimationClip> set,
            HashSet<UnityEditor.Animations.AnimatorStateMachine> seen)
        {
            if (sm == null || !seen.Add(sm)) return;
            foreach (var s in sm.states)
            {
                var mot = s.state?.motion;
                AddMotion(mot, set);
            }
            foreach (var sub in sm.stateMachines)
                WalkSm(sub.stateMachine, set, seen);
        }

        static void AddMotion(Motion mot, HashSet<AnimationClip> set)
        {
            if (mot is AnimationClip c) set.Add(c);
            else if (mot is UnityEditor.Animations.BlendTree bt)
            {
                foreach (var ch in bt.children) AddMotion(ch.motion, set);
            }
        }

        static void ScanVirtual(VirtualClip clip, Transform root, AtoAnimInfo info)
        {
            foreach (var b in clip.GetFloatCurveBindings())
                HandleFloat(b, clip.GetFloatCurve(b), root, info);
            foreach (var b in clip.GetObjectCurveBindings())
                HandleObject(b, clip.GetObjectCurve(b), root, info);
        }

        static void ScanRaw(AnimationClip clip, Transform root, AtoAnimInfo info)
        {
            foreach (var b in AnimationUtility.GetCurveBindings(clip))
                HandleFloat(b, AnimationUtility.GetEditorCurve(clip, b), root, info);
            foreach (var b in AnimationUtility.GetObjectReferenceCurveBindings(clip))
                HandleObject(b, AnimationUtility.GetObjectReferenceCurve(clip, b), root, info);
        }

        static void HandleFloat(EditorCurveBinding b, AnimationCurve curve, Transform root, AtoAnimInfo info)
        {
            if (curve == null || curve.length == 0) return;
            var t = Resolve(root, b.path);
            if (t == null) return;

            if (b.propertyName == "m_IsActive" || b.propertyName == "m_Enabled")
            {
                foreach (var k in curve.keys)
                    if (k.value > 0.5f)
                    {
                        info.EnabledByAnim.Add(t);
                        var r = t.GetComponent<Renderer>();
                        if (r != null) info.RenderersEnabledByAnim.Add(r);
                    }
            }

            if (b.propertyName.StartsWith("m_LocalScale", StringComparison.Ordinal))
            {
                float max = 0;
                foreach (var k in curve.keys) max = Mathf.Max(max, Mathf.Abs(k.value));
                if (!info.MaxAbsScale.TryGetValue(t, out var prev) || max > prev)
                    info.MaxAbsScale[t] = max;
            }

            var renderer = t.GetComponent<Renderer>();
            if (renderer == null) return;

            var prop = StripMaterialPrefix(b.propertyName);
            if (prop.EndsWith("_ST.x") || prop.EndsWith("_ST.y") || prop.EndsWith("_ST.z") || prop.EndsWith("_ST.w")
                || prop.Contains("_ScrollRotate") || prop.EndsWith("_ST"))
            {
                var texProp = prop;
                int dot = texProp.IndexOf('.');
                if (dot > 0) texProp = texProp.Substring(0, dot);
                if (texProp.EndsWith("_ST")) texProp = texProp.Substring(0, texProp.Length - 3);
                if (texProp.EndsWith("_ScrollRotate")) texProp = texProp.Substring(0, texProp.Length - "_ScrollRotate".Length);
                info.HasTexTransformAnim.Add((renderer, texProp));
            }

            if (prop == "_Cutoff" || prop == "_CutoffA" || prop == "_Mode" || prop == "_TransparentMode" || prop == "_Surface")
            {
                // Record strictest alpha later when we evaluate keys. 稍后与材质合并取最严。
                float maxC = 0, minC = 1;
                foreach (var k in curve.keys)
                {
                    maxC = Mathf.Max(maxC, k.value);
                    minC = Mathf.Min(minC, k.value);
                }
                if (!info.ExtraAlpha.TryGetValue((renderer, 0), out var list))
                {
                    list = new List<(AtoAlphaMode, float)>();
                    info.ExtraAlpha[(renderer, 0)] = list;
                }
                if (prop.Contains("Mode") || prop == "_Surface")
                {
                    foreach (var k in curve.keys)
                    {
                        int v = Mathf.RoundToInt(k.value);
                        if (v == 1) list.Add((AtoAlphaMode.Cutout, 0.5f));
                        else if (v >= 2) list.Add((AtoAlphaMode.Blend, 0f));
                    }
                }
                else
                {
                    list.Add((AtoAlphaMode.Cutout, maxC));
                }
            }
        }

        static void HandleObject(EditorCurveBinding b, ObjectReferenceKeyframe[] keys, Transform root, AtoAnimInfo info)
        {
            if (keys == null || keys.Length == 0) return;
            var t = Resolve(root, b.path);
            if (t == null) return;
            var renderer = t.GetComponent<Renderer>();
            if (renderer == null) return;

            if (b.propertyName != null && b.propertyName.StartsWith("m_Materials", StringComparison.Ordinal))
            {
                int slot = ParseSlot(b.propertyName);
                if (!info.ExtraMaterialSets.TryGetValue(renderer, out var sets))
                {
                    sets = new List<Material[]>();
                    info.ExtraMaterialSets[renderer] = sets;
                }
                var arr = renderer.sharedMaterials != null
                    ? (Material[])renderer.sharedMaterials.Clone()
                    : Array.Empty<Material>();
                foreach (var k in keys)
                {
                    if (k.value is Material m)
                    {
                        if (slot >= 0 && slot < arr.Length) arr[slot] = m;
                        var copy = (Material[])arr.Clone();
                        sets.Add(copy);
                    }
                }
            }

            var prop = StripMaterialPrefix(b.propertyName ?? "");
            foreach (var k in keys)
            {
                if (k.value is Texture2D tex)
                {
                    int slot = 0;
                    var key = (renderer, slot, prop);
                    if (!info.ExtraTextures.TryGetValue(key, out var list))
                    {
                        list = new List<Texture2D>();
                        info.ExtraTextures[key] = list;
                    }
                    list.Add(tex);
                }
                if (k.value is Material matTex)
                {
                    // already handled
                }
            }
        }

        static string StripMaterialPrefix(string p)
        {
            if (p.StartsWith("material.", StringComparison.Ordinal)) return p.Substring("material.".Length);
            return p;
        }

        static int ParseSlot(string p)
        {
            // m_Materials.Array.data[N]
            int a = p.LastIndexOf('[');
            int b = p.LastIndexOf(']');
            if (a >= 0 && b > a && int.TryParse(p.Substring(a + 1, b - a - 1), out var n)) return n;
            return 0;
        }

        static Transform Resolve(Transform root, string path)
        {
            if (string.IsNullOrEmpty(path)) return root;
            var t = root.Find(path);
            if (t != null) return t;
            // Animator paths are relative to animator GO, usually the avatar root after MA.
            return root.Find(path);
        }

        /// <summary>
        /// World-scale multiplier from bind pose to the max animated hierarchy scale.
        /// 从绑定姿态到动画最大层级缩放的倍率。
        /// </summary>
        public static float MaxHierarchyScale(Transform t, Transform root, AtoAnimInfo info)
        {
            float mul = 1f;
            var x = t;
            while (x != null)
            {
                float local = MaxAbs(x.localScale);
                if (info.MaxAbsScale.TryGetValue(x, out var anim))
                    local = Mathf.Max(local, anim);
                mul *= local;
                if (x == root) break;
                x = x.parent;
            }
            return Mathf.Max(mul, 1e-6f);
        }

        static float MaxAbs(Vector3 v) => Mathf.Max(Mathf.Abs(v.x), Mathf.Abs(v.y), Mathf.Abs(v.z));
    }
}
