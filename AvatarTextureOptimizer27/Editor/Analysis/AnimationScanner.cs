using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using Net.Fosa.AvatarTextureOptimizer;

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// Scans clips for material swaps, texture swaps, renderer enable, scale, ST, cutoff.
    /// 扫描动画中的材质/贴图切换、启用、缩放、ST、Cutoff。
    /// </summary>
    public sealed class AnimationImpact
    {
        public readonly HashSet<Texture2D> ExtraTextures = new HashSet<Texture2D>();
        public readonly HashSet<Material> ExtraMaterials = new HashSet<Material>();
        public readonly HashSet<string> TouchedRendererPaths = new HashSet<string>();
        public readonly Dictionary<string, float> MaxScale = new Dictionary<string, float>();
        public bool TouchesTextureST;
        public readonly List<AtoAlphaMode> ExtraAlphaModes = new List<AtoAlphaMode>();
        public readonly List<float> ExtraCutoffs = new List<float>();
        public readonly HashSet<int> IsolatedMaterialSlotSwitches = new HashSet<int>();
    }

    public static class AnimationScanner
    {
        public static AnimationImpact Scan(IEnumerable<AnimationClip> clips)
        {
            var impact = new AnimationImpact();
            if (clips == null) return impact;
            foreach (var clip in clips)
            {
                if (clip == null) continue;
                foreach (var binding in AnimationUtility.GetCurveBindings(clip))
                    InspectBinding(clip, binding, impact, false);
                foreach (var binding in AnimationUtility.GetObjectReferenceCurveBindings(clip))
                    InspectBinding(clip, binding, impact, true);
            }
            return impact;
        }

        static void InspectBinding(AnimationClip clip, EditorCurveBinding binding, AnimationImpact impact, bool isRef)
        {
            string path = binding.path ?? "";
            string prop = binding.propertyName ?? "";
            impact.TouchedRendererPaths.Add(path);

            if (prop.IndexOf("_ST", System.StringComparison.Ordinal) >= 0)
                impact.TouchesTextureST = true;

            if (prop.IndexOf("m_LocalScale", System.StringComparison.Ordinal) >= 0)
            {
                var curve = AnimationUtility.GetEditorCurve(clip, binding);
                if (curve != null)
                {
                    float max = 1f;
                    foreach (var k in curve.keys) max = Mathf.Max(max, Mathf.Abs(k.value));
                    if (!impact.MaxScale.TryGetValue(path, out var prev) || max > prev)
                        impact.MaxScale[path] = max;
                }
            }

            if (prop.IndexOf("_Cutoff", System.StringComparison.Ordinal) >= 0)
            {
                var curve = AnimationUtility.GetEditorCurve(clip, binding);
                if (curve != null)
                    foreach (var k in curve.keys) impact.ExtraCutoffs.Add(k.value);
            }

            if (prop.IndexOf("_Mode", System.StringComparison.Ordinal) >= 0 ||
                prop.IndexOf("TransparentMode", System.StringComparison.Ordinal) >= 0)
            {
                var curve = AnimationUtility.GetEditorCurve(clip, binding);
                if (curve != null)
                {
                    foreach (var k in curve.keys)
                    {
                        int m = Mathf.RoundToInt(k.value);
                        if (m == 1) impact.ExtraAlphaModes.Add(AtoAlphaMode.Cutout);
                        else if (m >= 2) impact.ExtraAlphaModes.Add(AtoAlphaMode.Blend);
                    }
                }
            }

            if (!isRef) return;
            var keys = AnimationUtility.GetObjectReferenceCurve(clip, binding);
            if (keys == null) return;
            foreach (var k in keys)
            {
                if (k.value is Texture2D t) impact.ExtraTextures.Add(t);
                if (k.value is Material mat)
                {
                    impact.ExtraMaterials.Add(mat);
                    if (prop.StartsWith("m_Materials.Array.data["))
                    {
                        int lb = prop.IndexOf('[');
                        int rb = prop.IndexOf(']');
                        if (lb >= 0 && rb > lb && int.TryParse(prop.Substring(lb + 1, rb - lb - 1), out int slot))
                            impact.IsolatedMaterialSlotSwitches.Add(slot);
                    }
                }
            }
        }
    }
}
