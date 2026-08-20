// ATOAnimationAnalyzer.cs — 动画分析器 / Animation analyzer.
// 说明：扫描 Avatar 上全部动画（VRC 播放层/Animator/Animation 组件），提取：
//  - 对象启用/禁用（含动画启用）
//  - 物体缩放的最大值（面积计算用，"按最大缩放时的面积算"）
//  - 材质槽切换（m_Materials.Array.data[i] 对象引用曲线）
//  - 材质属性动画：贴图切换（对象引用曲线）、Cutoff/ST/关键字等 float 曲线
// 绑定解析：float 属性（如 _MainTex_ST.x 分量后缀会被剥离）；场景路径绑定（path 非空）按渲染器路径汇总，
// 由扫描器在构建材质槽时落实到具体材质（保守处理：槽内所有具备该属性的材质都标记）。
// Note: scans all animations on the avatar (VRC playable layers / Animator / Animation components) to extract
// enable/disable state, max object scale, material slot swaps, and material property animations.
// Binding resolution: float-property component suffixes (e.g. "_MainTex_ST.x") are stripped; scene-path bindings
// are aggregated per renderer path and applied conservatively to all slot materials that have the property.

using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

namespace Fosa.AvatarTextureOptimizer
{
    /// <summary>动画扫描结果。/ Animation scan results.</summary>
    internal sealed class ATOAnimationData
    {
        public List<AnimationClip> clips = new List<AnimationClip>();
        public HashSet<AnimationClip> clipSet = new HashSet<AnimationClip>();
        public HashSet<string> mayBeActivePaths = new HashSet<string>();     // 可能被启用的对象路径 / paths of objects that may be activated
        public HashSet<string> mayBeEnabledRendererPaths = new HashSet<string>(); // 渲染器 enabled 动画路径 / renderer enabled-animated paths
        public Dictionary<string, float> maxScaleFactorByPath = new Dictionary<string, float>(); // 路径→最大缩放面积系数 / path → max scale area factor
        public Dictionary<string, HashSet<int>> slotAnimsByPath = new Dictionary<string, HashSet<int>>(); // 渲染器路径→被动画的槽索引 / renderer path → animated slot indices
        public Dictionary<string, List<Material>> slotMaterialsByPath = new Dictionary<string, List<Material>>(); // 渲染器路径→动画引用的全部材质 / renderer path → materials referenced by animation
        public Dictionary<string, HashSet<string>> floatPropsByPath = new Dictionary<string, HashSet<string>>(); // 渲染器路径→被动画的 float 属性名 / renderer path → animated float props
        public Dictionary<Material, HashSet<string>> floatPropsByMaterial = new Dictionary<Material, HashSet<string>>(); // 材质资产→被动画属性 / material asset → animated props
        public Dictionary<(string path, string prop), HashSet<Texture2D>> animatedTexturesByPath = new Dictionary<(string, string), HashSet<Texture2D>>(); // 场景路径贴图切换 / scene-path texture swaps
        public Dictionary<(Material mat, string prop), HashSet<Texture2D>> animatedTexturesByMaterial = new Dictionary<(Material, string), HashSet<Texture2D>>(); // 材质资产贴图切换 / material-asset texture swaps
        public HashSet<string> blendShapePaths = new HashSet<string>(); // 含形态键动画的渲染器路径 / renderer paths with blendshape animation
    }

    /// <summary>动画分析器。/ Animation analyzer.</summary>
    internal static class ATOAnimationAnalyzer
    {
        /// <summary>扫描 Avatar 的全部动画。/ Scan all animations of the avatar.</summary>
        public static ATOAnimationData Scan(GameObject avatarRoot)
        {
            var data = new ATOAnimationData();

            // 1. VRC 播放层（FX/Gesture/Action 等）/ VRC playable layers
            var descriptor = avatarRoot.GetComponent<VRCAvatarDescriptor>();
            if (descriptor != null)
            {
                foreach (var layer in descriptor.baseAnimationLayers)
                {
                    if (layer.animatorController == null) continue;
                    CollectClips(layer.animatorController, data);
                }
            }

            // 2. 全部 Animator 组件 / all Animator components
            foreach (var animator in avatarRoot.GetComponentsInChildren<Animator>(true))
            {
                if (animator.runtimeAnimatorController == null) continue;
                CollectClips(animator.runtimeAnimatorController, data);
            }

            // 3. 旧版 Animation 组件 / legacy Animation components
            foreach (var animation in avatarRoot.GetComponentsInChildren<Animation>(true))
            {
                if (animation.clip != null && data.clipSet.Add(animation.clip))
                    data.clips.Add(animation.clip);
            }

            // 4. 解析每条曲线 / interpret all curves
            foreach (var clip in data.clips)
            {
                if (clip == null) continue;
                ParseClip(clip, data);
            }
            return data;
        }

        private static void CollectClips(RuntimeAnimatorController controller, ATOAnimationData data)
        {
            foreach (var clip in controller.animationClips)
                if (clip != null && data.clipSet.Add(clip)) data.clips.Add(clip);
        }

        private static void ParseClip(AnimationClip clip, ATOAnimationData data)
        {
            // ---- float 曲线 / float curves ----
            var bindings = AnimationUtility.GetCurveBindings(clip);
            foreach (var binding in bindings)
            {
                var curve = AnimationUtility.GetEditorCurve(clip, binding);
                if (curve == null) continue;
                var prop = binding.propertyName;
                var path = binding.path;

                if (prop == "m_IsActive")
                {
                    if (CurveHasValueAbove(curve, 0.5f)) data.mayBeActivePaths.Add(path);
                }
                else if (prop == "m_Enabled" && (binding.type == typeof(SkinnedMeshRenderer) || binding.type == typeof(MeshRenderer)))
                {
                    if (CurveHasValueAbove(curve, 0.5f)) data.mayBeEnabledRendererPaths.Add(path);
                }
                else if (prop.StartsWith("m_LocalScale.", StringComparison.Ordinal))
                {
                    var max = MaxAbs(curve);
                    if (max > 1.00001f)
                    {
                        var prev = data.maxScaleFactorByPath.TryGetValue(path, out var p) ? p : 1f;
                        // 保守：以"两两轴乘积的最大值"作为面积放大系数 / conservative: max pairwise axis product as area factor
                        data.maxScaleFactorByPath[path] = Mathf.Max(prev, max * max);
                    }
                }
                else if (prop.StartsWith("m_Materials.Array.data[", StringComparison.Ordinal) &&
                         (binding.type == typeof(SkinnedMeshRenderer) || binding.type == typeof(MeshRenderer)))
                {
                    var idx = ParseSlotIndex(prop);
                    if (idx >= 0)
                    {
                        if (!data.slotAnimsByPath.TryGetValue(path, out var s))
                        {
                            s = new HashSet<int>();
                            data.slotAnimsByPath[path] = s;
                        }
                        s.Add(idx);
                    }
                }
                else if (binding.type == typeof(Material))
                {
                    // 材质属性 float 曲线：剥离分量后缀（_MainTex_ST.x → _MainTex_ST）/
                    // material float curves: strip component suffix (_MainTex_ST.x → _MainTex_ST)
                    var baseProp = StripComponent(prop);
                    if (string.IsNullOrEmpty(path))
                    {
                        // 资产绑定：解析目标材质资产 / asset binding: resolve the target material asset
                        var mat = ResolveMaterialAsset(clip, binding);
                        if (mat != null)
                        {
                            if (!data.floatPropsByMaterial.TryGetValue(mat, out var set))
                            {
                                set = new HashSet<string>();
                                data.floatPropsByMaterial[mat] = set;
                            }
                            set.Add(baseProp);
                        }
                    }
                    else
                    {
                        // 场景路径绑定：按路径汇总 / scene-path binding: aggregate by path
                        if (!data.floatPropsByPath.TryGetValue(path, out var set))
                        {
                            set = new HashSet<string>();
                            data.floatPropsByPath[path] = set;
                        }
                        set.Add(baseProp);
                    }
                }
                else if (prop.StartsWith("blendShape.", StringComparison.Ordinal))
                {
                    data.blendShapePaths.Add(path);
                }
            }

            // ---- 对象引用曲线（材质槽与贴图切换）/ object-reference curves (slot & texture swaps) ----
            var refBindings = AnimationUtility.GetObjectReferenceCurveBindings(clip);
            foreach (var binding in refBindings)
            {
                var prop = binding.propertyName;
                var frames = AnimationUtility.GetObjectReferenceCurve(clip, binding);
                if (frames == null || frames.Length == 0) continue;

                if (prop.StartsWith("m_Materials.Array.data[", StringComparison.Ordinal) &&
                    (binding.type == typeof(SkinnedMeshRenderer) || binding.type == typeof(MeshRenderer)))
                {
                    var idx = ParseSlotIndex(prop);
                    if (idx >= 0)
                    {
                        if (!data.slotAnimsByPath.TryGetValue(binding.path, out var s))
                        {
                            s = new HashSet<int>();
                            data.slotAnimsByPath[binding.path] = s;
                        }
                        s.Add(idx);
                        if (!data.slotMaterialsByPath.TryGetValue(binding.path, out var list))
                        {
                            list = new List<Material>();
                            data.slotMaterialsByPath[binding.path] = list;
                        }
                        foreach (var f in frames)
                            if (f.value is Material mat && mat != null) list.Add(mat);
                    }
                }
                else if (binding.type == typeof(Material))
                {
                    // 贴图切换（材质属性对象引用曲线）/ texture swaps (material-property object-reference curves)
                    foreach (var f in frames)
                    {
                        if (!(f.value is Texture2D tex) || tex == null) continue;
                        if (string.IsNullOrEmpty(binding.path))
                        {
                            var mat = ResolveMaterialAsset(clip, binding);
                            if (mat == null) continue;
                            var key = (mat, prop);
                            if (!data.animatedTexturesByMaterial.TryGetValue(key, out var set))
                            {
                                set = new HashSet<Texture2D>();
                                data.animatedTexturesByMaterial[key] = set;
                            }
                            set.Add(tex);
                        }
                        else
                        {
                            var key = (binding.path, prop);
                            if (!data.animatedTexturesByPath.TryGetValue(key, out var set))
                            {
                                set = new HashSet<Texture2D>();
                                data.animatedTexturesByPath[key] = set;
                            }
                            set.Add(tex);
                        }
                    }
                }
            }
        }

        /// <summary>剥离 float 曲线分量后缀（".x"/".y"/".z"/".w"）。/ Strip the component suffix of a float curve property.</summary>
        public static string StripComponent(string prop)
        {
            if (prop.Length >= 2 && prop[prop.Length - 2] == '.' &&
                (prop[prop.Length - 1] == 'x' || prop[prop.Length - 1] == 'y' ||
                 prop[prop.Length - 1] == 'z' || prop[prop.Length - 1] == 'w' ||
                 prop[prop.Length - 1] == 'r' || prop[prop.Length - 1] == 'g' ||
                 prop[prop.Length - 1] == 'b' || prop[prop.Length - 1] == 'a'))
                return prop.Substring(0, prop.Length - 2);
            return prop;
        }

        /// <summary>解析材质资产绑定（path 为空时）。/ Resolve material-asset bindings (empty path).</summary>
        private static Material ResolveMaterialAsset(AnimationClip clip, EditorCurveBinding binding)
        {
            var frames = AnimationUtility.GetObjectReferenceCurve(clip, binding);
            if (frames != null && frames.Length > 0 && frames[0].value is Material m) return m;
            return null;
        }

        /// <summary>曲线是否存在大于阈值的值。/ Whether the curve has any value above a threshold.</summary>
        public static bool CurveHasValueAbove(AnimationCurve curve, float threshold)
        {
            foreach (var key in curve.keys)
                if (key.value > threshold) return true;
            return false;
        }

        /// <summary>曲线绝对值最大值。/ Max absolute value of a curve.</summary>
        public static float MaxAbs(AnimationCurve curve)
        {
            var max = 0f;
            foreach (var key in curve.keys)
            {
                var v = Mathf.Abs(key.value);
                if (v > max) max = v;
            }
            return max;
        }

        /// <summary>解析材质槽索引（"m_Materials.Array.data[3]" → 3）。/ Parse slot index from a property name.</summary>
        private static int ParseSlotIndex(string prop)
        {
            var open = prop.IndexOf('[', StringComparison.Ordinal);
            var close = prop.IndexOf(']', StringComparison.Ordinal);
            if (open < 0 || close <= open) return -1;
            if (int.TryParse(prop.Substring(open + 1, close - open - 1), out var idx)) return idx;
            return -1;
        }
    }
}
