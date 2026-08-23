using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Fosa.AvatarTextureOptimizer.Editor.Core;
using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;
using UnityEditor;
using UnityEngine;

namespace Fosa.AvatarTextureOptimizer.Editor.Analysis
{
    /// <summary>EN: Texture object curve with its exact target binding. ZH: 带精确目标绑定的贴图对象曲线。</summary>
    internal readonly struct AnimatedTextureReference
    {
        public readonly EditorCurveBinding Binding;
        public readonly Texture2D Texture;
        public AnimatedTextureReference(EditorCurveBinding binding, Texture2D texture) { Binding = binding; Texture = texture; }
    }

    /// <summary>EN: Conservative merged-animation facts. ZH: 合并动画的保守事实集合。</summary>
    internal sealed class AnimationSnapshot
    {
        public readonly Dictionary<RendererSlot, HashSet<Material>> SlotMaterials = new Dictionary<RendererSlot, HashSet<Material>>();
        public readonly HashSet<(string path, string property)> AnimatedProperties = new HashSet<(string, string)>();
        public readonly Dictionary<(string path, string property), List<float>> FloatValues = new Dictionary<(string, string), List<float>>();
        public readonly List<AnimatedTextureReference> AnimatedTextures = new List<AnimatedTextureReference>();
        public readonly HashSet<string> PotentiallyEnabledPaths = new HashSet<string>();
        public readonly Dictionary<string, Vector3> MaximumLocalScale = new Dictionary<string, Vector3>();

        public bool IsAnimated(string path, string materialProperty)
        {
            if (AnimatedProperties.Contains((path, materialProperty))) return true;
            // EN: A material can be shared by renderers, so a global match is the safe fallback.
            // ZH: 材质可能被多个 Renderer 共享，因此全局属性匹配是安全回退。
            return AnimatedProperties.Any(x => x.property == materialProperty);
        }

        public IEnumerable<float> ValuesFor(string path, string materialProperty)
        {
            return FloatValues.TryGetValue((path, materialProperty), out var values) ? values : Enumerable.Empty<float>();
        }
    }

    /// <summary>EN: Reads NDMF's virtualized merged animation graph. ZH: 读取 NDMF 虚拟化后的合并动画图。</summary>
    internal static class AnimationAnalyzer
    {
        private static readonly Regex MaterialSlotRegex = new Regex(@"^m_Materials\.Array\.data\[(\d+)\]$", RegexOptions.Compiled);
        private const string MaterialPrefix = "material.";

        public static AnimationSnapshot Analyze(BuildContext context, BuildProgress progress)
        {
            var result = new AnimationSnapshot();
            var services = context.Extension<AnimatorServicesContext>();
            var controllers = services.ControllerContext.GetAllControllers().Where(x => x != null).ToList();
            var clips = controllers.SelectMany(x => x.AllReachableNodes()).OfType<VirtualClip>().Distinct().ToList();

            for (var clipIndex = 0; clipIndex < clips.Count; clipIndex++)
            {
                progress.Report("Scanning merged animations / 扫描合并动画", clipIndex, Math.Max(1, clips.Count));
                var clip = clips[clipIndex];
                AnalyzeObjectCurves(context.AvatarRootTransform, clip, result);
                AnalyzeFloatCurves(clip, result);
            }
            return result;
        }

        private static void AnalyzeObjectCurves(Transform root, VirtualClip clip, AnimationSnapshot result)
        {
            foreach (var binding in clip.GetObjectCurveBindings())
            {
                var curve = clip.GetObjectCurve(binding);
                if (curve == null) continue;
                var slotMatch = MaterialSlotRegex.Match(binding.propertyName ?? string.Empty);
                var targetRenderer = ResolveRenderer(root, binding);
                if (slotMatch.Success && targetRenderer != null && int.TryParse(slotMatch.Groups[1].Value, out var slot))
                {
                    var key = new RendererSlot(targetRenderer, slot);
                    if (!result.SlotMaterials.TryGetValue(key, out var materials))
                        result.SlotMaterials[key] = materials = new HashSet<Material>();
                    foreach (var frame in curve)
                        if (frame.value is Material material && material != null) materials.Add(material);
                }

                if (!string.IsNullOrEmpty(binding.propertyName) && binding.propertyName.StartsWith(MaterialPrefix, StringComparison.Ordinal))
                {
                    var property = NormalizeMaterialProperty(binding.propertyName);
                    result.AnimatedProperties.Add((binding.path ?? string.Empty, property));
                    foreach (var frame in curve)
                        if (frame.value is Texture2D texture && texture != null)
                            result.AnimatedTextures.Add(new AnimatedTextureReference(binding, texture));
                }
            }
        }

        private static void AnalyzeFloatCurves(VirtualClip clip, AnimationSnapshot result)
        {
            foreach (var binding in clip.GetFloatCurveBindings())
            {
                var curve = clip.GetFloatCurve(binding);
                if (curve == null) continue;
                var path = binding.path ?? string.Empty;
                var propertyName = binding.propertyName ?? string.Empty;

                if (propertyName.StartsWith(MaterialPrefix, StringComparison.Ordinal))
                {
                    var property = NormalizeMaterialProperty(propertyName);
                    result.AnimatedProperties.Add((path, property));
                    var key = (path, property);
                    if (!result.FloatValues.TryGetValue(key, out var values))
                        result.FloatValues[key] = values = new List<float>();
                    values.AddRange(SampleConservatively(curve));
                }

                if (propertyName == "m_IsActive" || propertyName == "m_Enabled")
                {
                    if (SampleConservatively(curve).Any(x => x > 0.5f)) result.PotentiallyEnabledPaths.Add(path);
                }

                const string scalePrefix = "m_LocalScale.";
                if (propertyName.StartsWith(scalePrefix, StringComparison.Ordinal))
                {
                    if (!result.MaximumLocalScale.TryGetValue(path, out var scale)) scale = Vector3.one;
                    var maximum = SampleConservatively(curve).Select(Mathf.Abs).DefaultIfEmpty(1f).Max();
                    switch (propertyName.Substring(scalePrefix.Length))
                    {
                        case "x": scale.x = Mathf.Max(scale.x, maximum); break;
                        case "y": scale.y = Mathf.Max(scale.y, maximum); break;
                        case "z": scale.z = Mathf.Max(scale.z, maximum); break;
                    }
                    result.MaximumLocalScale[path] = scale;
                }
            }
        }

        private static IEnumerable<float> SampleConservatively(AnimationCurve curve)
        {
            if (curve.length == 0) yield break;
            var keys = curve.keys;
            foreach (var key in keys) yield return key.value;
            for (var i = 0; i + 1 < keys.Length; i++)
            {
                var duration = keys[i + 1].time - keys[i].time;
                var outDuration = (keys[i].weightedMode & WeightedMode.Out) != 0 ? duration * keys[i].outWeight : duration / 3f;
                var inDuration = (keys[i + 1].weightedMode & WeightedMode.In) != 0 ? duration * keys[i + 1].inWeight : duration / 3f;
                var control1 = keys[i].value + keys[i].outTangent * outDuration;
                var control2 = keys[i + 1].value - keys[i + 1].inTangent * inDuration;
                // EN: Cubic Hermite values stay inside the Bezier control-value hull; controls give a conservative bound.
                // ZH: 三次 Hermite 值位于贝塞尔控制值凸包内；控制值可给出保守上界。
                if (!float.IsNaN(control1) && !float.IsInfinity(control1)) yield return control1;
                if (!float.IsNaN(control2) && !float.IsInfinity(control2)) yield return control2;
            }
            var start = curve.keys[0].time;
            var end = curve.keys[curve.length - 1].time;
            if (end <= start) yield break;
            // EN: Dense sampling catches common cubic overshoot; later safety margins prevent exact-bound reliance.
            // ZH: 密集采样可捕获常见三次曲线过冲；后续安全余量避免依赖精确边界。
            for (var i = 0; i <= 256; i++) yield return curve.Evaluate(Mathf.Lerp(start, end, i / 256f));
        }

        private static string NormalizeMaterialProperty(string animationName)
        {
            var property = animationName.StartsWith(MaterialPrefix, StringComparison.Ordinal)
                ? animationName.Substring(MaterialPrefix.Length) : animationName;
            var dot = property.IndexOf('.');
            return dot > 0 ? property.Substring(0, dot) : property;
        }

        private static Renderer ResolveRenderer(Transform root, EditorCurveBinding binding)
        {
            var transform = string.IsNullOrEmpty(binding.path) ? root : root.Find(binding.path);
            if (transform == null) return null;
            if (typeof(Renderer).IsAssignableFrom(binding.type)) return transform.GetComponent(binding.type) as Renderer;
            return transform.GetComponent<Renderer>();
        }
    }
}
