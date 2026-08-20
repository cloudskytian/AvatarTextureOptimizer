// Avatar Texture Optimizer (ATO)
// Bilingual comments: English + Simplified Chinese. 双语注释：英文 + 简体中文。

using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf.animator;
using UnityEditor;
using UnityEngine;

namespace AvatarTextureOptimizer.Editor.Analysis
{
    /// <summary>
    /// Helpers to query the NDMF AnimationIndex for material/texture switches and
    /// material-property / object-active animations.
    ///
    /// 查询 NDMF AnimationIndex 的辅助类：材质/贴图切换、材质属性动画、物体启停动画。
    /// </summary>
    public sealed class ATOAnimationQueries
    {
        private readonly AnimationIndex _index;

        public ATOAnimationQueries(AnimationIndex index) { _index = index; }

        /// <summary>
        /// All (binding, object) pairs for object curves (material/texture switches).
        /// 所有对象曲线（材质/贴图切换）的 (binding, object) 对。
        /// </summary>
        public IEnumerable<(EditorCurveBinding binding, Object obj)> ObjectReferences =>
            _index.GetPPtrReferencedObjectsWithBinding();

        /// <summary>
        /// True if the given material property on the given path is animated by any clip.
        /// 给定路径上的材质属性是否被任意动画曲线驱动。
        /// </summary>
        public bool IsMaterialPropertyAnimated(string path, string propertyName)
        {
            var binding = new EditorCurveBinding
            {
                path = path,
                type = typeof(Material),
                propertyName = propertyName,
            };
            return _index.GetClipsForBinding(binding).Any();
        }

        /// <summary>
        /// True if the GameObject's active state (m_IsActive) is animated.
        /// 物体的 active 状态（m_IsActive）是否被动画驱动。
        /// </summary>
        public bool IsGameObjectActiveAnimated(string path)
        {
            var binding = new EditorCurveBinding
            {
                path = path,
                type = typeof(GameObject),
                propertyName = "m_IsActive",
            };
            return _index.GetClipsForBinding(binding).Any();
        }

        /// <summary>
        /// True if a Behaviour's enabled state (m_Enabled) is animated.
        /// 组件的 enabled 状态（m_Enabled）是否被动画驱动。
        /// </summary>
        public bool IsEnabledAnimated(string path, System.Type type)
        {
            var binding = new EditorCurveBinding
            {
                path = path,
                type = type,
                propertyName = "m_Enabled",
            };
            return _index.GetClipsForBinding(binding).Any();
        }

        /// <summary>
        /// Returns the maximum animated value of a float property on a material path, or
        /// <paramref name="fallback"/> if not animated. Used to take the strictest cutoff /
        /// render-mode requirements across animation.
        ///
        /// 返回材质路径上某 float 属性被动画驱动的最大值；未动画则返回 fallback。
        /// 用于跨动画取最严苛的 cutoff / 渲染模式要求。
        /// </summary>
        public float GetMaxMaterialFloat(string path, string propertyName, float fallback)
        {
            float best = fallback;
            foreach (var prop in new[] { "material." + propertyName, propertyName })
            {
                var binding = new EditorCurveBinding
                {
                    path = path,
                    type = typeof(Material),
                    propertyName = prop,
                };
                foreach (var clip in _index.GetClipsForBinding(binding))
                {
                    var curve = clip.GetFloatCurve(binding);
                    if (curve == null) continue;
                    foreach (var kf in curve.keys)
                        if (kf.value > best) best = kf.value;
                }
            }
            return best;
        }

        /// <summary>
        /// True if the material's render mode / blend / queue properties are animated.
        /// 材质的渲染模式/混合/队列相关属性是否被动画驱动。
        /// </summary>
        public bool IsRenderModeAnimated(string path)
        {
            foreach (var prop in new[]
                     {
                         "material._Mode", "material._SrcBlend", "material._DstBlend",
                         "material._ZWrite", "material._AlphaToMask",
                         "_Mode", "_SrcBlend", "_DstBlend", "_ZWrite", "_AlphaToMask",
                     })
            {
                var binding = new EditorCurveBinding
                {
                    path = path,
                    type = typeof(Material),
                    propertyName = prop,
                };
                if (_index.GetClipsForBinding(binding).Any()) return true;
            }
            return false;
        }

        /// <summary>
        /// Compute the maximum world-area scale factor (≥1) introduced by animation on a
        /// renderer's own scale or any ancestor scale. Conservative: takes per-axis max
        /// across the curve, then multiplies the two largest axes (area).
        ///
        /// 计算动画对渲染器自身或任一父级 scale 引入的最大世界面积放大系数（≥1）。
        /// 保守处理：取曲线各轴最大值，用面积相关两轴相乘。
        /// </summary>
        public float GetMaxAnimatedAreaFactor(string rendererPath)
        {
            // Walk the path upward. 沿路径向上。
            var parts = rendererPath.Split('/');
            float sx = 1f, sy = 1f;

            for (int i = parts.Length; i >= 1; i--)
            {
                string subPath = string.Join("/", parts, 0, i);
                foreach (var axis in new[] { "m_LocalScale.x", "m_LocalScale.y", "m_LocalScale.z" })
                {
                    var binding = new EditorCurveBinding
                    {
                        path = subPath,
                        type = typeof(Transform),
                        propertyName = axis,
                    };
                    float axisMax = 1f;
                    foreach (var clip in _index.GetClipsForBinding(binding))
                    {
                        var curve = clip.GetFloatCurve(binding);
                        if (curve == null) continue;
                        foreach (var kf in curve.keys)
                            if (kf.value > axisMax) axisMax = kf.value;
                    }
                    if (axis == "m_LocalScale.x") sx = Mathf.Max(sx, axisMax);
                    else if (axis == "m_LocalScale.y") sy = Mathf.Max(sy, axisMax);
                }
            }

            return Mathf.Max(1f, sx * sy);
        }
    }
}
