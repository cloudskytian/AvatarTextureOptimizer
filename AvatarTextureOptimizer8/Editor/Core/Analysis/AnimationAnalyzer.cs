// AnimationAnalyzer.cs
// Extracts every animation fact ATO needs: material/texture swaps, object toggles,
// material float keyframes (ST/cutoff/render mode), transform scale, blendshapes.
// 提取 ATO 需要的全部动画事实:材质/贴图切换、物体开关、材质浮点关键帧(ST/阈值/渲染模式)、缩放、形态键。
// Copyright (c) 2026 fosa. Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;
using UnityEngine;

namespace net.fosa.ato
{
    internal static partial class AnimationAnalyzer
    {
        /// <summary>
        /// Walk all virtual controllers in the build context and fill the animation database.
        /// / 遍历构建上下文中全部虚拟控制器,填充动画数据库。
        /// </summary>
        internal static void Collect(BuildContext ctx, AnimationDatabase db)
        {
            var asc = ctx.Extension<AnimatorServicesContext>();
            var seen = new HashSet<VirtualClip>();
            foreach (var ctrl in asc.ControllerContext.GetAllControllers())
            foreach (var node in ctrl.AllReachableNodes())
            {
                if (node is VirtualClip clip && seen.Add(clip)) AnalyzeClip(clip, db);
            }
            ATOLog.V($"animation database: {seen.Count} clips, {db.MaterialSwaps.Count} swap targets, " +
                     $"{db.AnimatedActivePaths.Count} animated-active paths");
        }

        private static void AnalyzeClip(VirtualClip clip, AnimationDatabase db)
        {
            // ---------- Object reference curves / 对象引用曲线 ----------
            foreach (var b in clip.GetObjectCurveBindings())
            {
                var values = clip.GetObjectCurve(b);
                if (values == null) continue;

                if (b.type == typeof(GameObject) && b.propertyName == "m_IsActive")
                {
                    db.AnimatedActivePaths.Add(b.path);
                    continue;
                }

                if (!typeof(Renderer).IsAssignableFrom(b.type)) continue;
                if (b.propertyName.StartsWith("m_Materials.Array.data[", StringComparison.Ordinal))
                {
                    if (!TryParseSlot(b.propertyName, out var slot)) continue;
                    foreach (var kf in values)
                        if (kf.value is Material m)
                            Add(db.MaterialSwaps, b.path, slot, m);
                    db.AnimatedSlots.Add((b.path, slot));
                }
                else if (b.propertyName.StartsWith("material.", StringComparison.Ordinal))
                {
                    // texture property swap, e.g. material._MainTex / 动画切换贴图属性
                    var prop = b.propertyName.Substring("material.".Length);
                    foreach (var kf in values)
                        if (kf.value is Texture2D t)
                            AddTex(db.TextureSwaps, b.path, prop, t);
                }
            }

            // ---------- Float curves / 浮点曲线 ----------
            foreach (var b in clip.GetFloatCurveBindings())
            {
                var curve = clip.GetFloatCurve(b);
                if (curve == null || curve.keys.Length == 0) continue;
                var lo = curve.keys.Min(k => k.value);
                var hi = curve.keys.Max(k => k.value);

                if (b.type == typeof(Transform) &&
                    (b.propertyName == "m_LocalScale.x" || b.propertyName == "m_LocalScale.y" ||
                     b.propertyName == "m_LocalScale.z"))
                {
                    // record per-path max volume scale; combined later per renderer / 记录路径最大缩放,稍后按渲染器合并
                    db.MaxScaleByPath.TryGetValue(b.path, out var cur);
                    db.MaxScaleByPath[b.path] = Math.Max(cur, Math.Abs(hi));
                    continue;
                }

                if (b.type == typeof(SkinnedMeshRenderer) && b.propertyName.StartsWith("blendShape.", StringComparison.Ordinal))
                {
                    var shape = b.propertyName.Substring("blendShape.".Length);
                    if (!db.BlendshapeCurves.TryGetValue(b.path, out var byShape))
                        db.BlendshapeCurves[b.path] = byShape = new Dictionary<string, float[]>();
                    byShape[shape] = new[] { lo, hi };
                    continue;
                }

                if (typeof(Renderer).IsAssignableFrom(b.type) &&
                    b.propertyName.StartsWith("material.", StringComparison.Ordinal))
                {
                    var prop = b.propertyName.Substring("material.".Length);
                    var slot = GuessSlotFromPropertyName(prop);
                    if (!db.MaterialFloatKeyframes.TryGetValue(b.path, out var bySlot))
                        db.MaterialFloatKeyframes[b.path] = bySlot = new Dictionary<int, Dictionary<string, float[]>>();
                    if (!bySlot.TryGetValue(slot, out var byProp))
                        bySlot[slot] = byProp = new Dictionary<string, float[]>();
                    if (byProp.TryGetValue(prop, out var old))
                        byProp[prop] = new[] { Math.Min(old[0], lo), Math.Max(old[1], hi) };
                    else
                        byProp[prop] = new[] { lo, hi };
                    db.AnimatedSlots.Add((b.path, slot));
                }
            }
        }

        private static bool TryParseSlot(string propertyName, out int slot)
        {
            // "m_Materials.Array.data[3]" → 3
            var s = propertyName.Substring("m_Materials.Array.data[".Length);
            var close = s.IndexOf(']');
            slot = close > 0 && int.TryParse(s.Substring(0, close), out var v) ? v : -1;
            return slot >= 0;
        }

        private static int GuessSlotFromPropertyName(string prop) => 0; // conservative: slot 0 / 保守:槽0

        private static void Add(Dictionary<string, Dictionary<int, List<Material>>> map, string path, int slot, Material m)
        {
            if (!map.TryGetValue(path, out var bySlot)) map[path] = bySlot = new Dictionary<int, List<Material>>();
            if (!bySlot.TryGetValue(slot, out var list)) bySlot[slot] = list = new List<Material>();
            if (!list.Contains(m)) list.Add(m);
        }

        private static void AddTex(Dictionary<string, List<TexSwapEntry>> map, string path, string prop, Texture2D t)
        {
            if (!map.TryGetValue(path, out var list)) map[path] = list = new List<TexSwapEntry>();
            if (list.All(e => e.Prop != prop || e.Tex != t)) list.Add(new TexSwapEntry(prop, t));
        }
    }
}
