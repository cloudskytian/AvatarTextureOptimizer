// SPDX-License-Identifier: MIT
// EN: Animation analysis built on NDMF's AnimatorServicesContext: which objects can become active, which
//     materials can be swapped in, which material properties are animated and how large objects can grow.
// ZH: 基于 NDMF AnimatorServicesContext 的动画分析：哪些对象可能被启用、哪些材质可能被切换进来、
//     哪些材质属性被动画修改，以及对象最大会被放大到多少。

using System;
using System.Collections.Generic;
using nadena.dev.ndmf.animator;
using UnityEditor;
using UnityEngine;

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// EN: Everything the pipeline needs to know about animations.
    /// ZH: 管线需要了解的全部动画信息。
    /// </summary>
    public sealed class ATOAnimationInfo
    {
        /// <summary>EN: Object paths that any clip can switch on. ZH: 任意动画可以启用的对象路径。</summary>
        public readonly HashSet<string> ActivatablePaths = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>EN: Object paths whose Renderer.enabled can be animated on. ZH: Renderer.enabled 可能被动画开启的路径。</summary>
        public readonly HashSet<string> RendererEnabledPaths = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>EN: (path, slot) -&gt; materials the animation can assign. ZH: (路径, 材质槽) -&gt; 动画可能赋予的材质。</summary>
        public readonly Dictionary<(string path, int slot), HashSet<Material>> MaterialSwaps =
            new Dictionary<(string, int), HashSet<Material>>();

        /// <summary>EN: (path, slot) with any animated swap. ZH: 存在动画切换的 (路径, 材质槽)。</summary>
        public readonly HashSet<(string path, int slot)> AnimatedSlots = new HashSet<(string, int)>();

        /// <summary>EN: path -&gt; animated material property names (without the "material." prefix). ZH: 路径 -&gt; 被动画修改的材质属性名（去掉 "material." 前缀）。</summary>
        public readonly Dictionary<string, HashSet<string>> AnimatedMaterialProperties =
            new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        /// <summary>EN: path -&gt; every cutoff value the animation can produce. ZH: 路径 -&gt; 动画可能产生的所有 cutoff 值。</summary>
        public readonly Dictionary<string, List<float>> AnimatedCutoffs =
            new Dictionary<string, List<float>>(StringComparer.Ordinal);

        /// <summary>EN: path -&gt; largest absolute local scale reachable through animation. ZH: 路径 -&gt; 动画可达的最大绝对局部缩放。</summary>
        public readonly Dictionary<string, Vector3> MaxLocalScale =
            new Dictionary<string, Vector3>(StringComparer.Ordinal);

        /// <summary>EN: All materials referenced by any clip. ZH: 所有被动画引用到的材质。</summary>
        public readonly HashSet<Material> AnimatedMaterials = new HashSet<Material>();

        /// <summary>
        /// EN: Returns the animated scale multiplier for a path (1,1,1 when never animated).
        /// ZH: 返回某路径的动画缩放倍率（未被动画修改时为 (1,1,1)）。
        /// </summary>
        public Vector3 GetMaxScale(string path, Vector3 current)
        {
            if (!MaxLocalScale.TryGetValue(path, out var animated)) return current;
            return new Vector3(
                Mathf.Max(Mathf.Abs(current.x), Mathf.Abs(animated.x)),
                Mathf.Max(Mathf.Abs(current.y), Mathf.Abs(animated.y)),
                Mathf.Max(Mathf.Abs(current.z), Mathf.Abs(animated.z)));
        }
    }

    /// <summary>
    /// EN: Fills an <see cref="ATOAnimationInfo"/> from the virtual animator controllers of the build.
    /// ZH: 从构建过程中的虚拟动画控制器填充 <see cref="ATOAnimationInfo"/>。
    /// </summary>
    public static class ATOAnimationAnalyzer
    {
        private const string MaterialPrefix = "material.";

        /// <summary>
        /// EN: Analyses every clip reachable from the build's virtual animator controllers.
        /// ZH: 分析构建过程中虚拟动画控制器可达的所有动画片段。
        /// </summary>
        public static ATOAnimationInfo Analyze(IEnumerable<VirtualClip> clips, ATOLog log)
        {
            var info = new ATOAnimationInfo();
            if (clips == null)
            {
                log.Warning("anim", "no animation index available, animations were not analysed");
                return info;
            }

            var clipList = new List<VirtualClip>(clips);
            var clipCount = clipList.Count;

            foreach (var clip in clipList)
            {
                foreach (var binding in clip.GetObjectCurveBindings())
                {
                    var curve = clip.GetObjectCurve(binding);
                    if (curve == null) continue;

                    if (!TryParseMaterialSlot(binding.propertyName, out var slot)) continue;
                    if (!typeof(Renderer).IsAssignableFrom(binding.type)) continue;

                    var key = (binding.path, slot);
                    info.AnimatedSlots.Add(key);
                    if (!info.MaterialSwaps.TryGetValue(key, out var set))
                    {
                        set = new HashSet<Material>();
                        info.MaterialSwaps[key] = set;
                    }

                    foreach (var kf in curve)
                    {
                        if (kf.value is Material mat && mat != null)
                        {
                            set.Add(mat);
                            info.AnimatedMaterials.Add(mat);
                        }
                    }
                }
            }

            // EN: Float curves: activation, renderer enabled, scale, material properties.
            // ZH: 浮点曲线：启用状态、渲染器启用、缩放、材质属性。
            foreach (var clip in clipList)
            {
                foreach (var binding in clip.GetFloatCurveBindings())
                {
                    var curve = clip.GetFloatCurve(binding);
                    if (curve == null || curve.length == 0) continue;

                    var prop = binding.propertyName;

                    if (binding.type == typeof(GameObject) && prop == "m_IsActive")
                    {
                        if (CurveReachesTrue(curve)) info.ActivatablePaths.Add(binding.path);
                        continue;
                    }

                    if (typeof(Renderer).IsAssignableFrom(binding.type) && prop == "m_Enabled")
                    {
                        if (CurveReachesTrue(curve)) info.RendererEnabledPaths.Add(binding.path);
                        continue;
                    }

                    if (binding.type == typeof(Transform) && prop.StartsWith("m_LocalScale.", StringComparison.Ordinal))
                    {
                        var axis = prop["m_LocalScale.".Length];
                        var maxValue = MaxAbs(curve);
                        info.MaxLocalScale.TryGetValue(binding.path, out var v);
                        switch (axis)
                        {
                            case 'x': v.x = Mathf.Max(v.x, maxValue); break;
                            case 'y': v.y = Mathf.Max(v.y, maxValue); break;
                            case 'z': v.z = Mathf.Max(v.z, maxValue); break;
                        }

                        info.MaxLocalScale[binding.path] = v;
                        continue;
                    }

                    if (typeof(Renderer).IsAssignableFrom(binding.type) &&
                        prop.StartsWith(MaterialPrefix, StringComparison.Ordinal))
                    {
                        var name = prop.Substring(MaterialPrefix.Length);
                        if (!info.AnimatedMaterialProperties.TryGetValue(binding.path, out var set))
                        {
                            set = new HashSet<string>(StringComparer.Ordinal);
                            info.AnimatedMaterialProperties[binding.path] = set;
                        }

                        set.Add(name);

                        if (name == "_Cutoff" || name == "_AlphaCutoff" || name == "_Cutout")
                        {
                            if (!info.AnimatedCutoffs.TryGetValue(binding.path, out var list))
                            {
                                list = new List<float>();
                                info.AnimatedCutoffs[binding.path] = list;
                            }

                            foreach (var k in curve.keys) list.Add(k.value);
                        }
                    }
                }
            }

            log.Info("anim",
                $"analysed {clipCount} object-curve clips; activatable={info.ActivatablePaths.Count}, " +
                $"materialSwapSlots={info.MaterialSwaps.Count}, animatedMaterials={info.AnimatedMaterials.Count}, " +
                $"scaledPaths={info.MaxLocalScale.Count}");
            return info;
        }

        /// <summary>
        /// EN: Enumerates every clip reachable from the given virtual animator controllers.
        /// ZH: 枚举给定虚拟动画控制器可达的所有动画片段。
        /// </summary>
        public static IEnumerable<VirtualClip> EnumerateClips(IEnumerable<VirtualNode> roots)
        {
            if (roots == null) yield break;

            var seen = new HashSet<VirtualClip>();
            foreach (var root in roots)
            {
                if (root == null) continue;
                foreach (var node in root.AllReachableNodes())
                {
                    if (node is VirtualClip clip && seen.Add(clip)) yield return clip;
                }
            }
        }

        private static bool TryParseMaterialSlot(string propertyName, out int slot)
        {
            slot = -1;
            const string prefix = "m_Materials.Array.data[";
            if (!propertyName.StartsWith(prefix, StringComparison.Ordinal)) return false;
            var end = propertyName.IndexOf(']', prefix.Length);
            if (end < 0) return false;
            return int.TryParse(propertyName.Substring(prefix.Length, end - prefix.Length), out slot);
        }

        private static bool CurveReachesTrue(AnimationCurve curve)
        {
            foreach (var k in curve.keys)
                if (k.value > 0.5f)
                    return true;
            return false;
        }

        private static float MaxAbs(AnimationCurve curve)
        {
            var max = 0f;
            foreach (var k in curve.keys) max = Mathf.Max(max, Mathf.Abs(k.value));
            return max;
        }
    }
}
