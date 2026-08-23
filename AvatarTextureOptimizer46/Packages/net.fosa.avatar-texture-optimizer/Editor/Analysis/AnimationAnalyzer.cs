// SPDX-License-Identifier: MIT
// EN: Extracts everything animations can do that affects texture optimization.
// ZH: 提取动画中一切会影响贴图优化的行为。

using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;
using Net.Fosa.AvatarTextureOptimizer.Editor.Core;
using UnityEditor;
using UnityEngine;
using UnityObject = UnityEngine.Object;

namespace Net.Fosa.AvatarTextureOptimizer.Editor.Analysis
{
    /// <summary>
    /// EN: Result of scanning all animator controllers of the avatar.
    /// ZH: 扫描 Avatar 全部动画控制器的结果。
    /// </summary>
    public sealed class AnimationFacts
    {
        /// <summary>EN: Renderer path to material slot index to the set of materials the animation can assign. ZH: 渲染器路径 -&gt; 材质槽索引 -&gt; 动画可能赋予的材质集合。</summary>
        public readonly Dictionary<string, Dictionary<int, HashSet<Material>>> AnimatedMaterials
            = new Dictionary<string, Dictionary<int, HashSet<Material>>>();

        /// <summary>EN: Renderer path to shader property name to the set of textures the animation can assign. ZH: 渲染器路径 -&gt; 着色器属性名 -&gt; 动画可能赋予的贴图集合。</summary>
        public readonly Dictionary<string, Dictionary<string, HashSet<Texture>>> AnimatedTextures
            = new Dictionary<string, Dictionary<string, HashSet<Texture>>>();

        /// <summary>EN: Renderer paths where an animation drives a UV critical material property. ZH: 动画驱动了 UV 关键材质属性的渲染器路径。</summary>
        public readonly HashSet<string> UvCriticalAnimated = new HashSet<string>();

        /// <summary>EN: Renderer path to the strictest cutoff value seen (minimum, i.e. most texels kept). ZH: 渲染器路径 -&gt; 观察到的最严格 cutoff（取最小值，即保留最多像素）。</summary>
        public readonly Dictionary<string, float> AnimatedCutoffMin = new Dictionary<string, float>();

        /// <summary>EN: Transform path to the maximum absolute uniform scale factor an animation can reach. ZH: Transform 路径 -&gt; 动画可达到的最大绝对均匀缩放系数。</summary>
        public readonly Dictionary<string, Vector3> MaxAnimatedScale = new Dictionary<string, Vector3>();

        /// <summary>EN: GameObject paths that an animation can enable, even if they start disabled. ZH: 动画可以启用的 GameObject 路径，即使初始为禁用。</summary>
        public readonly HashSet<string> PossiblyEnabled = new HashSet<string>();

        /// <summary>EN: Renderer paths whose enabled state an animation can turn on. ZH: 动画可以开启其启用状态的渲染器路径。</summary>
        public readonly HashSet<string> RendererPossiblyEnabled = new HashSet<string>();
    }

    /// <summary>
    /// EN: Scans every clip reachable from the avatar's animators through NDMF's
    ///     <see cref="AnimatorServicesContext"/>, which already flattens Modular Avatar's merges.
    /// ZH: 通过 NDMF 的 <see cref="AnimatorServicesContext"/> 扫描 Avatar 动画器可达的所有片段；
    ///     该上下文已经展平了 Modular Avatar 的合并结果。
    /// </summary>
    public static class AnimationAnalyzer
    {
        private const string Stage = "Animation";

        // EN: "material._MainTex" / "material._MainTex_ST.x" / "material._Color.r"
        // ZH: 形如 "material._MainTex" / "material._MainTex_ST.x" / "material._Color.r"
        private static readonly Regex MaterialProp = new Regex(@"^material\.(?<prop>[^.]+)(?:\.(?<comp>[xyzwrgba]))?$", RegexOptions.Compiled);
        // EN: "m_Materials.Array.data[3]"
        // ZH: 形如 "m_Materials.Array.data[3]"
        private static readonly Regex MaterialSlot = new Regex(@"^m_Materials\.Array\.data\[(?<idx>\d+)\]$", RegexOptions.Compiled);

        /// <summary>
        /// EN: Runs the scan. <paramref name="uvCriticalProps"/> is the union of every analyzer's list of
        ///     properties whose animation invalidates atlasing.
        /// ZH: 执行扫描。<paramref name="uvCriticalProps"/> 是所有分析器给出的、一旦被动画驱动
        ///     就会使图集化失效的属性名的并集。
        /// </summary>
        public static AnimationFacts Analyze(BuildContext ctx, ISet<string> uvCriticalProps)
        {
            var facts = new AnimationFacts();
            AnimatorServicesContext asc;
            try
            {
                asc = ctx.Extension<AnimatorServicesContext>();
            }
            catch (Exception e)
            {
                AtoLog.Warning(Stage, $"AnimatorServicesContext unavailable ({e.Message}); animations will not be analyzed.");
                return facts;
            }

            // EN: VirtualNode.AllReachableNodes() walks layers, state machines, states and blend trees,
            //     so every clip that can ever play is visited exactly once.
            // ZH: VirtualNode.AllReachableNodes() 会遍历层、状态机、状态与混合树，
            //     因此每个可能播放的片段都会被恰好访问一次。
            int clipCount = 0;
            var seen = new HashSet<VirtualClip>();
            foreach (var controller in asc.ControllerContext.GetAllControllers())
            {
                foreach (var node in controller.AllReachableNodes())
                {
                    if (node is not VirtualClip clip) continue;
                    if (!seen.Add(clip)) continue;
                    clipCount++;
                    ScanClip(clip, facts, uvCriticalProps);
                }
            }

            AtoLog.Info(Stage,
                $"scanned {clipCount} clips: {facts.AnimatedMaterials.Count} paths with material swaps, " +
                $"{facts.AnimatedTextures.Count} paths with texture swaps, " +
                $"{facts.UvCriticalAnimated.Count} paths with animated UV transforms, " +
                $"{facts.MaxAnimatedScale.Count} animated scales");
            return facts;
        }

        private static void ScanClip(VirtualClip clip, AnimationFacts facts, ISet<string> uvCriticalProps)
        {
            foreach (var binding in clip.GetObjectCurveBindings())
            {
                var curve = clip.GetObjectCurve(binding);
                if (curve == null) continue;

                var slotMatch = MaterialSlot.Match(binding.propertyName);
                if (slotMatch.Success && typeof(Renderer).IsAssignableFrom(binding.type))
                {
                    int idx = int.Parse(slotMatch.Groups["idx"].Value);
                    var map = Get(facts.AnimatedMaterials, binding.path);
                    if (!map.TryGetValue(idx, out var set)) map[idx] = set = new HashSet<Material>();
                    foreach (var kf in curve) if (kf.value is Material m && m != null) set.Add(m);
                    continue;
                }

                var propMatch = MaterialProp.Match(binding.propertyName);
                if (propMatch.Success)
                {
                    var prop = propMatch.Groups["prop"].Value;
                    var map = Get(facts.AnimatedTextures, binding.path);
                    if (!map.TryGetValue(prop, out var set)) map[prop] = set = new HashSet<Texture>();
                    foreach (var kf in curve) if (kf.value is Texture t && t != null) set.Add(t);
                }
            }

            foreach (var binding in clip.GetFloatCurveBindings())
            {
                var curve = clip.GetFloatCurve(binding);
                if (curve == null || curve.length == 0) continue;

                // EN: Object / renderer activation.
                // ZH: 物体与渲染器的启用状态。
                if (binding.propertyName == "m_IsActive")
                {
                    if (MaxValue(curve) > 0.5f) facts.PossiblyEnabled.Add(binding.path);
                    continue;
                }
                if (binding.propertyName == "m_Enabled" && typeof(Renderer).IsAssignableFrom(binding.type))
                {
                    if (MaxValue(curve) > 0.5f) facts.RendererPossiblyEnabled.Add(binding.path);
                    continue;
                }

                // EN: Scale animation - the largest scale defines the largest world space area.
                // ZH: 缩放动画 —— 最大缩放决定了最大的世界空间面积。
                if (binding.type == typeof(Transform) && binding.propertyName.StartsWith("m_LocalScale.", StringComparison.Ordinal))
                {
                    var axis = binding.propertyName["m_LocalScale.".Length];
                    float max = Mathf.Abs(MaxAbsValue(curve));
                    facts.MaxAnimatedScale.TryGetValue(binding.path, out var cur);
                    if (cur == default) cur = Vector3.one;
                    if (axis == 'x') cur.x = Mathf.Max(cur.x, max);
                    else if (axis == 'y') cur.y = Mathf.Max(cur.y, max);
                    else if (axis == 'z') cur.z = Mathf.Max(cur.z, max);
                    facts.MaxAnimatedScale[binding.path] = cur;
                    continue;
                }

                var propMatch = MaterialProp.Match(binding.propertyName);
                if (!propMatch.Success) continue;
                var prop = propMatch.Groups["prop"].Value;

                // EN: An animated cutoff changes the silhouette; keep the smallest value, which preserves
                //     the most texels and therefore imposes the strictest quality requirement.
                // ZH: 被动画驱动的 cutoff 会改变轮廓；取最小值，因为它保留最多像素，
                //     也就意味着最严格的质量要求。
                if (prop == "_Cutoff")
                {
                    float min = MinValue(curve);
                    facts.AnimatedCutoffMin.TryGetValue(binding.path, out var prev);
                    facts.AnimatedCutoffMin[binding.path] = facts.AnimatedCutoffMin.ContainsKey(binding.path) ? Mathf.Min(prev, min) : min;
                    continue;
                }

                // EN: Any animated _ST / scroll / rotate / UV mode kills atlasing for the renderer.
                // ZH: 任何被动画驱动的 _ST / 滚动 / 旋转 / UV 模式都会让该渲染器无法图集化。
                if (prop.EndsWith("_ST", StringComparison.Ordinal) || uvCriticalProps.Contains(prop))
                {
                    if (IsCurveNonConstantOrNonDefault(curve, prop))
                        facts.UvCriticalAnimated.Add(binding.path);
                }
            }
        }

        /// <summary>
        /// EN: An <c>_ST</c> curve that is constant at the identity value is harmless. Anything else is not.
        /// ZH: 恒定在单位值上的 <c>_ST</c> 曲线是无害的，其余情况都不是。
        /// </summary>
        private static bool IsCurveNonConstantOrNonDefault(AnimationCurve curve, string prop)
        {
            float min = MinValue(curve), max = MaxValue(curve);
            if (Mathf.Abs(max - min) > 1e-6f) return true;
            // EN: We do not know which component this is, so accept only 0 (offset) and 1 (scale).
            // ZH: 我们不知道这是哪个分量，因此只接受 0（偏移）与 1（缩放）。
            return Mathf.Abs(min) > 1e-6f && Mathf.Abs(min - 1f) > 1e-6f;
        }

        private static float MaxValue(AnimationCurve c)
        {
            float v = float.NegativeInfinity;
            foreach (var k in c.keys) v = Mathf.Max(v, k.value);
            return v;
        }

        private static float MinValue(AnimationCurve c)
        {
            float v = float.PositiveInfinity;
            foreach (var k in c.keys) v = Mathf.Min(v, k.value);
            return v;
        }

        private static float MaxAbsValue(AnimationCurve c)
        {
            float v = 0f;
            foreach (var k in c.keys) v = Mathf.Max(v, Mathf.Abs(k.value));
            return v;
        }

        private static Dictionary<TK, TV> Get<TK, TV>(Dictionary<string, Dictionary<TK, TV>> map, string key)
        {
            if (!map.TryGetValue(key, out var inner)) map[key] = inner = new Dictionary<TK, TV>();
            return inner;
        }
    }
}
