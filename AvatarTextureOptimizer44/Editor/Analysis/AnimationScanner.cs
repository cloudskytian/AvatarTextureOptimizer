// AnimationScanner.cs - Extract every animation influence on materials/textures/activation/scale.
// 提取动画对材质/贴图/启用状态/缩放的全部影响。
// Binding shapes handled / 处理的绑定形态:
//   m_Materials.Array.data[N]  (ObjectRef)  -> slot material swap / 材质槽切换
//   material.<prop>            (ObjectRef)  -> texture swap on the material / 材质贴图切换
//   material.<name>.<prop>     (ObjectRef)  -> named-material texture swap / 具名材质贴图切换
//   material.* floats / ST     (Float)      -> cutoff, blend props, ST transform / 浮点属性与ST变换
//   m_IsActive                 (Float)      -> activation / 启用
//   m_LocalScale.*             (Float)      -> scale / 缩放
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using Fosa.ATO.Editor.Core;

namespace Fosa.ATO.Editor.Analysis
{
    public static class AnimationScanner
    {
        /// <summary>Fill animation-derived fields of the scan. / 填充扫描结果的动画相关字段。</summary>
        public static void AnalyzeClips(AvatarScan scan)
        {
            using (ATOLog.Scope("AnalyzeAnimations"))
            {
                foreach (var clip in scan.clips)
                {
                    try { AnalyzeClip(scan, clip); }
                    catch (Exception e) { ATOLog.Warn($"clip analysis failed / 片段分析失败: {clip.name}: {e.Message}"); }
                }
            }
        }

        private static void AnalyzeClip(AvatarScan scan, AnimationClip clip)
        {
            // ---- object reference curves / 对象引用曲线 ----
            foreach (var b in AnimationUtility.GetObjectReferenceCurveBindings(clip))
            {
                var keys = AnimationUtility.GetObjectReferenceCurve(clip, b);
                foreach (var k in keys)
                {
                    if (k.value == null) continue;
                    switch (b.propertyName)
                    {
                        case var p when p.StartsWith("m_Materials.Array.data[", StringComparison.Ordinal):
                        {
                            int slot = ParseSlot(p);
                            Add(scan.slotSwaps, (b.path, slot), k.value as Material);
                            if (k.value is Material m) scan.materialsInAnimations.Add(m);
                            break;
                        }
                        case var p when p.StartsWith("material.", StringComparison.Ordinal):
                        {
                            Add(scan.propSwaps, (b.path, p), k.value);
                            if (k.value is Material m) scan.materialsInAnimations.Add(m);
                            break;
                        }
                    }
                }
            }

            // ---- float curves / 浮点曲线 ----
            foreach (var b in AnimationUtility.GetCurveBindings(clip))
            {
                var curve = AnimationUtility.GetEditorCurve(clip, b);
                if (curve == null || curve.keys.Length == 0) continue;
                string p = b.propertyName;
                if (b.type == typeof(GameObject) && p == "m_IsActive")
                {
                    if (HasKeyAbove(curve, 0.5f)) scan.animatedActivePaths.Add(b.path);
                }
                else if (p.StartsWith("m_LocalScale.", StringComparison.Ordinal))
                {
                    float max = MaxAbs(curve);
                    scan.maxAnimScale.TryGetValue(b.path, out float cur);
                    scan.maxAnimScale[b.path] = Mathf.Max(cur, max);
                }
                else if (p.StartsWith("material.", StringComparison.Ordinal))
                {
                    float min = MinVal(curve), max = MaxVal(curve);
                    var key = (b.path, p);
                    scan.floatProps.TryGetValue(key, out var old);
                    scan.floatProps[key] = new Vector2(Mathf.Min(old.x, min), Mathf.Max(old.y, max));
                }
            }
        }

        private static int ParseSlot(string prop)
        {
            // m_Materials.Array.data[3] -> 3
            int s = prop.IndexOf('['), e = prop.IndexOf(']');
            if (s >= 0 && e > s && int.TryParse(prop.Substring(s + 1, e - s - 1), out int slot)) return slot;
            return -1;
        }

        private static bool HasKeyAbove(AnimationCurve c, float t)
        {
            foreach (var k in c.keys) if (k.value > t) return true;
            return false;
        }

        private static float MaxAbs(AnimationCurve c) { float m = 0; foreach (var k in c.keys) m = Mathf.Max(m, Mathf.Abs(k.value)); return m; }
        private static float MaxVal(AnimationCurve c) { float m = float.MinValue; foreach (var k in c.keys) m = Mathf.Max(m, k.value); return m; }
        private static float MinVal(AnimationCurve c) { float m = float.MaxValue; foreach (var k in c.keys) m = Mathf.Min(m, k.value); return m; }

        private static void Add<K, V>(Dictionary<K, HashSet<V>> d, K k, V v) where V : class
        {
            if (v == null) return;
            if (!d.TryGetValue(k, out var set)) d[k] = set = new HashSet<V>();
            set.Add(v);
        }

        /// <summary>Does any animation animate a transform/scale/ST of the given renderer path? / 动画是否修改指定渲染器的变换或ST？</summary>
        public static bool HasScaleAnimation(AvatarScan scan, string path)
            => scan.maxAnimScale.ContainsKey(path) || scan.maxAnimScale.ContainsKey(path + "/…");

        /// <summary>Max animated scale factor along the chain root->renderer. / 根到渲染器链路上的最大动画缩放。</summary>
        public static float MaxScaleOnChain(AvatarScan scan, Transform root, Renderer r)
        {
            float f = 1f;
            // walk renderer up to root multiplying per-path maxima / 沿路径逐级乘以每段最大缩放
            var chain = new List<string>();
            var sb = new System.Text.StringBuilder();
            for (var t = r.transform; t != null && t != root; t = t.parent)
            {
                if (sb.Length > 0) sb.Insert(0, '/');
                sb.Insert(0, t.name);
                chain.Add(sb.ToString());
            }
            foreach (var p in chain)
                if (scan.maxAnimScale.TryGetValue(p, out float m))
                    f *= Mathf.Max(1f, m);
            return f;
        }
    }
}
