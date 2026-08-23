using System;
using System.Collections.Generic;
using nadena.dev.ndmf.animator;
using UnityEditor;
using UnityEngine;

namespace Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// Walks every animation clip (via NDMF AnimatorServices) and extracts facts that affect
    /// texture processing: renderer enable/state, transforms scale, material swaps, texture swaps,
    /// animated ST/scroll/UVMode props, and animated alpha/cutoff values.
    /// / 遍历全部动画（NDMF 动画服务）：渲染器启用状态、最大缩放、材质切换、贴图切换、
    /// ST/滚动/UVMode 动画、透明度与 Cutoff 动画。
    /// </summary>
    internal class AnimationScanner
    {
        // output / 输出
        internal readonly Dictionary<Transform, Vector3> MaxAnimScale = new Dictionary<Transform, Vector3>();
        /// <summary>Renderers whose GameObject/renderer might get enabled by animation. / 可能被动画启用的渲染器。</summary>
        internal readonly HashSet<Renderer> PossiblyEnabledRenderers = new HashSet<Renderer>();
        /// <summary>path → facts. / 路径 → 事实。</summary>
        internal readonly Dictionary<string, RendererInfo> FactsByPath = new Dictionary<string, RendererInfo>();

        private readonly GameObject _root;
        private readonly Dictionary<Transform, string> _pathCache = new Dictionary<Transform, string>();

        internal AnimationScanner(GameObject root) => _root = root;

        internal void Scan(AnimatorServicesContext asc, List<RendererInfo> renderers)
        {
            foreach (var info in renderers)
            {
                FactsByPath[AbsolutePath(info.renderer.transform)] = info;
            }

            var clips = new HashSet<VirtualClip>();
            foreach (var controller in asc.ControllerContext.GetAllControllers())
            {
                foreach (var node in controller.AllReachableNodes())
                {
                    if (node is VirtualClip c) clips.Add(c);
                }
            }

            ATOLog.Info($"animation scan: {clips.Count} clips");
            foreach (var clip in clips) ScanClip(clip);
        }

        private void ScanClip(VirtualClip clip)
        {
            foreach (var binding in clip.GetFloatCurveBindings())
                ScanFloatBinding(clip, binding);

            foreach (var binding in clip.GetObjectCurveBindings())
                ScanObjectBinding(clip, binding);
        }

        // ------------------------------------------------------------------ float curves
        private void ScanFloatBinding(VirtualClip clip, EditorCurveBinding binding)
        {
            var curve = clip.GetFloatCurve(binding);
            if (curve == null || curve.keys == null || curve.keys.Length == 0) return;

            switch (binding.type.Name)
            {
                case "Transform" when binding.propertyName == "m_LocalScale":
                    TrackScale(binding, curve);
                    break;
                case "GameObject" when binding.propertyName == "m_IsActive":
                    TrackEnabled(binding);
                    break;
                case var _ when typeof(Renderer).IsAssignableFrom(binding.type) &&
                                 binding.propertyName == "m_Enabled":
                    TrackEnabled(binding);
                    break;
                default:
                    if (binding.propertyName.StartsWith("material.", StringComparison.Ordinal))
                        ScanMaterialFloatCurve(binding, curve);
                    else if (binding.propertyName.StartsWith("blendShape.", StringComparison.Ordinal))
                    {
                        // Blendshape weights: area evaluation always takes max(0,100) anyway.
                        // 形态键：面积评估固定取 0/100 二者最大，无需处理。
                    }
                    break;
            }
        }

        private void TrackScale(EditorCurveBinding binding, AnimationCurve curve)
        {
            var t = Find(binding.path);
            if (t == null) return;
            Vector3 max = MaxAnimScale.TryGetValue(t, out var m) ? m : Vector3.zero;
            foreach (var k in curve.keys)
            {
                // channel is x/y/z appended like "m_LocalScale.x" / 通道后缀
                if (binding.propertyName.EndsWith(".x")) max.x = Mathf.Max(max.x, Mathf.Abs(k.value));
                else if (binding.propertyName.EndsWith(".y")) max.y = Mathf.Max(max.y, Mathf.Abs(k.value));
                else if (binding.propertyName.EndsWith(".z")) max.z = Mathf.Max(max.z, Mathf.Abs(k.value));
            }
            MaxAnimScale[t] = max;
        }

        private void TrackEnabled(EditorCurveBinding binding)
        {
            var t = Find(binding.path);
            if (t == null) return;
            // include the object itself and all children renderers / 自身与子级渲染器
            foreach (var r in t.GetComponentsInChildren<Renderer>(true))
                PossiblyEnabledRenderers.Add(r);
        }

        private void ScanMaterialFloatCurve(EditorCurveBinding binding, AnimationCurve curve)
        {
            // strip "material." / "material[N]." prefix / 去前缀
            var prop = binding.propertyName.Substring("material.".Length);
            var bracket = prop.IndexOf(']');
            if (prop.StartsWith("[") && bracket > 0) prop = prop.Substring(bracket + 2); // "material[1]._X" form

            var info = FactsByPath.TryGetValue(binding.path, out var v) ? v : null;
            var lower = prop.ToLowerInvariant();

            if (lower.EndsWith("_st") || lower.EndsWith("_scrollrotate") || lower.EndsWith("angle") ||
                lower.EndsWith("uvmode") || lower.EndsWith("shiftbackfaceuv"))
            {
                if (info != null) info.unsafeAnimatedProps.Add(prop);
                return;
            }

            if (lower == "_cutoff" || lower == "_mode" || lower == "_alphamode" || lower == "_srblend" ||
                lower == "_dstblend" || lower == "_zwrite" || lower == "_transparent")
            {
                // Animated transparency: evaluate cutout at every cutoff keyframe + blend mode,
                // strictest-wins. / 透明动画：对每个关键帧取值按 Cutout+Blend 全部评估，取最严。
                if (info == null) return;
                foreach (var k in curve.keys)
                {
                    if (lower == "_cutoff")
                    {
                        info.animatedAlpha.Add((AlphaMode.Cutout, Mathf.Clamp01(k.value)));
                        info.animatedAlpha.Add((AlphaMode.Blend, Mathf.Clamp01(k.value)));
                    }
                    else
                    {
                        info.animatedAlpha.Add((AlphaMode.Cutout, 0.5f));
                        info.animatedAlpha.Add((AlphaMode.Blend, 0.5f));
                    }
                }
            }
        }

        // ------------------------------------------------------------------ object (pptr) curves
        private void ScanObjectBinding(VirtualClip clip, EditorCurveBinding binding)
        {
            var keys = clip.GetObjectCurve(binding);
            if (keys == null || keys.Length == 0) return;

            // material slot swaps: "m_Materials.Array.data[N]" / 材质槽切换
            if (binding.propertyName.StartsWith("m_Materials.Array.data[", StringComparison.Ordinal) &&
                int.TryParse(SubstringBetween(binding.propertyName, '[', ']'), out var slotIndex))
            {
                var info = FactsByPath.TryGetValue(binding.path, out var v) ? v : null;
                if (info == null) return;
                EnsureSlotTracking(info, slotIndex);
                foreach (var k in keys)
                {
                    if (k.value is Material mat && !info.slotSwapMaterials[slotIndex].Contains(mat))
                        info.slotSwapMaterials[slotIndex].Add(mat);
                }
                return;
            }

            // texture property swaps: "material._MainTex" / 贴图属性切换
            if (binding.propertyName.StartsWith("material.", StringComparison.Ordinal))
            {
                var info = FactsByPath.TryGetValue(binding.path, out var v) ? v : null;
                if (info == null) return;
                var prop = binding.propertyName.Substring("material.".Length);
                var bracket = prop.IndexOf(']');
                if (prop.StartsWith("[") && bracket > 0) prop = prop.Substring(bracket + 2);

                var textures = new List<Texture2D>();
                foreach (var k in keys)
                    if (k.value is Texture2D t && !textures.Contains(t))
                        textures.Add(t);
                if (textures.Count > 0)
                    info.textureSwaps.Add((prop, textures));
            }
        }

        private void EnsureSlotTracking(RendererInfo info, int slotIndex)
        {
            if (!info.slotSwapMaterials.ContainsKey(slotIndex))
                info.slotSwapMaterials[slotIndex] = new List<Material>();
            if (slotIndex >= info.slots.Length)
            {
                // animation may address more slots than currently present / 动画可能引用更多槽位
                var resized = new Material[slotIndex + 1];
                info.slots.CopyTo(resized, 0);
                info.slots = resized;
            }
        }

        // ------------------------------------------------------------------ helpers
        private Transform Find(string path)
        {
            if (string.IsNullOrEmpty(path)) return _root.transform;
            return _root.transform.Find(path);
        }

        private string AbsolutePath(Transform t)
        {
            if (_pathCache.TryGetValue(t, out var p)) return p;
            if (t == _root.transform) { p = ""; }
            else
            {
                var names = new System.Collections.Generic.List<string>();
                var cur = t;
                while (cur != null && cur != _root.transform)
                {
                    names.Add(cur.name);
                    cur = cur.parent;
                }
                names.Reverse();
                p = string.Join("/", names);
            }
            _pathCache[t] = p;
            return p;
        }

        private static string SubstringBetween(string s, char a, char b)
        {
            int i = s.IndexOf(a), j = s.IndexOf(b);
            if (i < 0 || j <= i) return "";
            return s.Substring(i + 1, j - i - 1);
        }
    }
}
