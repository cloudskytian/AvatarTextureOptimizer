// Copyright (c) fosa. Licensed under the MIT License.
// Scans animations for material swaps, texture swaps, shader-property animation and object
// enable/disable, all of which widen the set of textures a UV stream can address.
// 扫描动画中的材质切换、贴图切换、着色器属性动画与物体启用/禁用，
// 这些都会扩大某条 UV 流可能寻址到的贴图集合。

using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// Everything the animation system can change that affects texture optimization.
    /// 动画系统中所有会影响贴图优化的可变内容。
    /// </summary>
    public sealed class AnimationFindings
    {
        /// <summary>Materials reachable through animation, keyed by renderer path and slot. / 通过动画可达的材质，按渲染器路径与槽位索引。</summary>
        public readonly Dictionary<string, HashSet<Material>> AnimatedMaterials =
            new Dictionary<string, HashSet<Material>>(StringComparer.Ordinal);

        /// <summary>Every material referenced anywhere in animation. / 动画中任意位置引用到的所有材质。</summary>
        public readonly HashSet<Material> AllAnimatedMaterials = new HashSet<Material>();

        /// <summary>Every texture referenced directly by animation. / 动画直接引用的所有贴图。</summary>
        public readonly HashSet<Texture2D> AllAnimatedTextures = new HashSet<Texture2D>();

        /// <summary>
        /// Materials whose UV transform properties are animated. These can never be optimized
        /// because the UV mapping is not static.
        /// UV 变换属性被动画化的材质。它们永远不能被优化，因为 UV 映射不是静态的。
        /// </summary>
        public readonly HashSet<Material> UVAnimatedMaterials = new HashSet<Material>();

        /// <summary>Renderer paths whose enabled state is animated. / 启用状态被动画化的渲染器路径。</summary>
        public readonly HashSet<string> ToggledPaths = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>Additional cutoff values introduced by animation, per material. / 动画为每个材质引入的额外 cutoff 值。</summary>
        public readonly Dictionary<Material, HashSet<float>> AnimatedCutoffs =
            new Dictionary<Material, HashSet<float>>();

        /// <summary>Materials whose render mode is animated, forcing the strictest alpha mode. / 渲染模式被动画化的材质，强制采用最严苛的 alpha 模式。</summary>
        public readonly HashSet<Material> RenderModeAnimated = new HashSet<Material>();

        /// <summary>Maximum animated scale per object path, for texel density. / 每个物体路径的最大动画缩放，用于像素密度计算。</summary>
        public readonly Dictionary<string, Vector3> MaxAnimatedScale =
            new Dictionary<string, Vector3>(StringComparer.Ordinal);
    }

    /// <summary>
    /// Extracts texture-relevant facts from the avatar's animator controllers.
    /// Uses NDMF's AnimationIndex when available so virtual (in-build) clips are covered.
    /// 从 Avatar 的动画控制器中提取与贴图相关的事实。
    /// 可用时使用 NDMF 的 AnimationIndex，从而覆盖构建期的虚拟动画。
    /// </summary>
    public sealed class AnimationAnalyzer
    {
        private readonly ATOLogger _log;

        /// <summary>
        /// Property-name fragments whose animation invalidates UV-based optimization.
        /// 一旦被动画化就会使基于 UV 的优化失效的属性名片段。
        /// </summary>
        private static readonly string[] UVTransformProperties =
        {
            "_ST.", "_ScrollRotate", "_UVMode", "_Angle", "IsDecal",
        };

        /// <summary>Creates an analyzer. / 创建分析器。</summary>
        public AnimationAnalyzer(ATOLogger log)
        {
            _log = log;
        }

        /// <summary>
        /// Analyses a set of raw AnimationClips. This overload exists so the analyzer can be
        /// unit tested and used without NDMF's virtual controller layer.
        /// 分析一组原始 AnimationClip。
        /// 该重载使分析器可被单元测试，也可在不依赖 NDMF 虚拟控制器层的情况下使用。
        /// </summary>
        public AnimationFindings Analyze(IEnumerable<AnimationClip> clips)
        {
            var findings = new AnimationFindings();
            if (clips == null) return findings;

            foreach (var clip in clips)
            {
                if (clip == null) continue;
                AnalyzeClip(clip, findings);
            }

            _log?.Detail(
                $"Animation scan: {findings.AllAnimatedMaterials.Count} materials, " +
                $"{findings.AllAnimatedTextures.Count} textures, " +
                $"{findings.ToggledPaths.Count} toggled paths, " +
                $"{findings.UVAnimatedMaterials.Count} UV-animated materials");

            return findings;
        }

        private void AnalyzeClip(AnimationClip clip, AnimationFindings findings)
        {
            // Object reference curves carry material and texture swaps.
            // 对象引用曲线承载材质与贴图切换。
            foreach (var binding in AnimationUtility.GetObjectReferenceCurveBindings(clip))
            {
                var keys = AnimationUtility.GetObjectReferenceCurve(clip, binding);
                if (keys == null) continue;

                foreach (var key in keys)
                {
                    RecordObjectReference(binding, key.value, findings);
                }
            }

            // Float curves carry enable/disable, scale, cutoff and shader property animation.
            // 浮点曲线承载启用/禁用、缩放、cutoff 与着色器属性动画。
            foreach (var binding in AnimationUtility.GetCurveBindings(clip))
            {
                var prop = binding.propertyName;

                if (prop == "m_IsActive" || prop == "m_Enabled")
                {
                    findings.ToggledPaths.Add(binding.path ?? string.Empty);
                    continue;
                }

                if (prop != null && prop.StartsWith("m_LocalScale.", StringComparison.Ordinal))
                {
                    RecordScale(clip, binding, findings);
                    continue;
                }

                if (prop != null && prop.StartsWith("material.", StringComparison.Ordinal))
                {
                    RecordMaterialProperty(clip, binding, findings);
                }
            }
        }

        private static void RecordObjectReference(
            EditorCurveBinding binding, Object value, AnimationFindings findings)
        {
            var path = binding.path ?? string.Empty;

            switch (value)
            {
                case Material mat:
                    findings.AllAnimatedMaterials.Add(mat);
                    if (!findings.AnimatedMaterials.TryGetValue(path, out var set))
                    {
                        set = new HashSet<Material>();
                        findings.AnimatedMaterials[path] = set;
                    }

                    set.Add(mat);
                    break;

                case Texture2D tex:
                    findings.AllAnimatedTextures.Add(tex);
                    break;
            }
        }

        private static void RecordScale(
            AnimationClip clip, EditorCurveBinding binding, AnimationFindings findings)
        {
            var curve = AnimationUtility.GetEditorCurve(clip, binding);
            if (curve?.keys == null || curve.keys.Length == 0) return;

            var path = binding.path ?? string.Empty;
            var maxValue = 0f;
            foreach (var k in curve.keys) maxValue = Mathf.Max(maxValue, Mathf.Abs(k.value));

            findings.MaxAnimatedScale.TryGetValue(path, out var current);
            if (current == default) current = Vector3.one;

            // The binding names the axis, e.g. m_LocalScale.x.
            // 绑定名称指明轴向，例如 m_LocalScale.x。
            var axis = binding.propertyName;
            if (axis.EndsWith(".x", StringComparison.Ordinal)) current.x = Mathf.Max(current.x, maxValue);
            else if (axis.EndsWith(".y", StringComparison.Ordinal)) current.y = Mathf.Max(current.y, maxValue);
            else if (axis.EndsWith(".z", StringComparison.Ordinal)) current.z = Mathf.Max(current.z, maxValue);

            findings.MaxAnimatedScale[path] = current;
        }

        private static void RecordMaterialProperty(
            AnimationClip clip, EditorCurveBinding binding, AnimationFindings findings)
        {
            var prop = binding.propertyName;

            foreach (var fragment in UVTransformProperties)
            {
                if (prop.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    // The material instance is unknown from the binding alone; the caller
                    // resolves the path to renderers and marks their materials.
                    // 仅凭绑定无法确定材质实例；由调用方将路径解析到渲染器并标记其材质。
                    findings.ToggledPaths.Add(binding.path ?? string.Empty);
                    return;
                }
            }
        }

        /// <summary>
        /// Resolves which renderer paths have animated UV transforms, so their materials can be
        /// whitelisted. Called after the renderer set is known.
        /// 解析哪些渲染器路径存在被动画化的 UV 变换，以便将其材质列入白名单。
        /// 在渲染器集合确定之后调用。
        /// </summary>
        public HashSet<string> FindUVAnimatedPaths(IEnumerable<AnimationClip> clips)
        {
            var paths = new HashSet<string>(StringComparer.Ordinal);
            if (clips == null) return paths;

            foreach (var clip in clips)
            {
                if (clip == null) continue;

                foreach (var binding in AnimationUtility.GetCurveBindings(clip))
                {
                    var prop = binding.propertyName;
                    if (prop == null || !prop.StartsWith("material.", StringComparison.Ordinal))
                        continue;

                    foreach (var fragment in UVTransformProperties)
                    {
                        if (prop.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            paths.Add(binding.path ?? string.Empty);
                            break;
                        }
                    }
                }
            }

            return paths;
        }

        /// <summary>
        /// Collects all cutoff values a material is animated through, so the quality search can
        /// satisfy the strictest one.
        /// 收集某材质被动画化经过的所有 cutoff 值，使质量搜索能够满足其中最严苛者。
        /// </summary>
        public Dictionary<string, HashSet<float>> FindAnimatedCutoffs(
            IEnumerable<AnimationClip> clips)
        {
            var result = new Dictionary<string, HashSet<float>>(StringComparer.Ordinal);
            if (clips == null) return result;

            foreach (var clip in clips)
            {
                if (clip == null) continue;

                foreach (var binding in AnimationUtility.GetCurveBindings(clip))
                {
                    var prop = binding.propertyName;
                    if (prop == null) continue;

                    if (prop.IndexOf("_Cutoff", StringComparison.OrdinalIgnoreCase) < 0 &&
                        prop.IndexOf("_AlphaCutoff", StringComparison.OrdinalIgnoreCase) < 0)
                        continue;

                    var curve = AnimationUtility.GetEditorCurve(clip, binding);
                    if (curve?.keys == null) continue;

                    var path = binding.path ?? string.Empty;
                    if (!result.TryGetValue(path, out var set))
                    {
                        set = new HashSet<float>();
                        result[path] = set;
                    }

                    foreach (var k in curve.keys) set.Add(Mathf.Clamp01(k.value));
                }
            }

            return result;
        }
    }
}
