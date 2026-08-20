// ============================================================================
// ATO - animation rewriter
// ATO - 动画改写器
//
// Rebuilds each affected AnimationClip (pattern verified against AAO's
// ObjectMapping implementation):
//   - float curves copied unchanged (ST/cutoff/render-mode curves keep
//     working: we never rename material properties);
//   - object reference curves: material slot indices remapped, texture
//     values remapped via the (material, property) final texture table,
//     material values remapped via the material dedup table;
//   - m_UseHighQualityCurve preserved via SerializedObject;
//   - the rebuilt clip replaces the old one through
//     ObjectRegistry.RegisterReplacedObject so every reference is rebound.
// ============================================================================

#region

using System.Collections.Generic;
using nadena.dev.ndmf;
using net.fosa.AvatarTextureOptimizer.Editor.Analysis;
using net.fosa.AvatarTextureOptimizer.Editor.Core;
using UnityEditor;
using UnityEngine;

#endregion

namespace net.fosa.AvatarTextureOptimizer.Editor.Atlas
{
    public static class AnimationRewriter
    {
        public sealed class Rewriter
        {
            public readonly ATOAnalysis An;
            /// <summary>(material, prop) -> final texture. 材质属性 -> 最终贴图。</summary>
            public readonly Dictionary<(Material, string), Texture2D> FinalTextures;
            /// <summary>material -> replacement (dedup). 材质 -> 替身（去重）。</summary>
            public readonly Dictionary<Material, Material> MaterialDedup;
            /// <summary>renderer -> old slot index -> new slot index.
            /// 渲染器 -> 旧槽索引 -> 新槽索引。</summary>
            public readonly Dictionary<Renderer, Dictionary<int, int>> SlotRemap;
            public readonly HashSet<AnimationClip> Touched = new();

            public Rewriter(ATOAnalysis an)
            {
                An = an;
                FinalTextures = an.FinalTextures;
                MaterialDedup = an.MaterialDedupMap;
                SlotRemap = an.SlotRemap;
            }
        }

        /// <summary>Rebuilds all clips that need remapping.
        /// 重建所有需要重映射的 clip。</summary>
        public static void Rewrite(ATOContext ctx, Rewriter r)
        {
            var log = ctx.Log;
            var anim = ctx.Anim;
            if (anim == null) return;

            foreach (var clip in anim.Clips)
            {
                ctx.Session.Check("Apply 应用");
                if (!NeedsRewrite(clip, r, out var slotChanged)) continue;
                var newClip = Rebuild(clip, r);
                if (newClip == null) continue;
                ObjectRegistry.RegisterReplacedObject(clip, newClip);
                r.Touched.Add(clip);
            }
            log.Info(ATOLogMask.Atlas,
                $"animation rewrite: {r.Touched.Count} clips remapped. 动画改写完成。");
        }

        private static bool NeedsRewrite(AnimationClip clip, Rewriter r, out bool slotChanged)
        {
            slotChanged = false;
            foreach (var b in AnimationUtility.GetObjectReferenceCurveBindings(clip))
            {
                if (b.target is Material mat)
                {
                    // texture swap on a property that changed  属性贴图发生变化
                    if (r.FinalTextures.ContainsKey((mat, b.propertyName))) return true;
                    // material dedup on the target material itself
                    // 目标材质本身被去重
                    if (r.MaterialDedup.ContainsKey(mat)) return true;
                }
                else if (b.target is Renderer rend && b.propertyName.StartsWith("m_Materials.Array.data["))
                {
                    int bracket = b.propertyName.LastIndexOf(']');
                    int slot = int.Parse(b.propertyName.Substring("m_Materials.Array.data[".Length,
                        bracket - "m_Materials.Array.data[".Length));
                    if (r.SlotRemap.TryGetValue(rend, out var map) && map.ContainsKey(slot))
                    {
                        slotChanged = true;
                        return true;
                    }
                    if (r.MaterialDedup.Count > 0) return true; // values may change 值可能变化
                }
            }
            return false;
        }

        private static AnimationClip Rebuild(AnimationClip clip, Rewriter r)
        {
            var newClip = new AnimationClip { name = clip.name };

            // preserve high-quality curve flag  保留高质量曲线标志
            using (var so = new SerializedObject(clip))
            using (var soNew = new SerializedObject(newClip))
            {
                var p = so.FindProperty("m_UseHighQualityCurve");
                var pNew = soNew.FindProperty("m_UseHighQualityCurve");
                if (p != null && pNew != null)
                {
                    pNew.boolValue = p.boolValue;
                    soNew.ApplyModifiedPropertiesWithoutUndo();
                }
            }

            // float + vector curves unchanged  浮点/向量曲线原样
            foreach (var b in AnimationUtility.GetCurveBindings(clip))
            {
                // vector properties are stored as separate component curves;
                // GetEditorCurve would only copy the first component, so use
                // the vector API when it applies.
                // 向量属性按分量存储；GetEditorCurve 只复制第一个分量，
                // 因此适用时用向量 API。
                if (AnimationUtility.GetEditorVectorCurve(clip, b, out var vecs) && vecs != null)
                {
                    AnimationUtility.SetEditorVectorCurve(newClip, b, vecs);
                }
                else
                {
                    AnimationUtility.SetEditorCurve(newClip, b, AnimationUtility.GetEditorCurve(clip, b));
                }
            }

            // object reference curves  对象引用曲线
            foreach (var b in AnimationUtility.GetObjectReferenceCurveBindings(clip))
            {
                var frames = AnimationUtility.GetObjectReferenceCurve(clip, b);
                if (frames == null || frames.Length == 0) continue;

                var binding = b;
                // slot index remap  槽索引重映射
                if (b.target is Renderer rend && b.propertyName.StartsWith("m_Materials.Array.data["))
                {
                    int bracket = b.propertyName.LastIndexOf(']');
                    int slot = int.Parse(b.propertyName.Substring("m_Materials.Array.data[".Length,
                        bracket - "m_Materials.Array.data[".Length));
                    if (r.SlotRemap.TryGetValue(rend, out var map) && map.TryGetValue(slot, out int newSlot))
                    {
                        binding.propertyName = $"m_Materials.Array.data[{newSlot}]";
                    }
                }

                var newFrames = new ObjectReferenceKeyframe[frames.Length];
                for (int i = 0; i < frames.Length; i++)
                {
                    var f = frames[i];
                    var v = f.value;
                    if (f.value is Material m)
                    {
                        if (r.MaterialDedup.TryGetValue(m, out var m2)) v = m2;
                    }
                    else if (f.value is Texture2D tex && b.target is Material mat)
                    {
                        if (r.FinalTextures.TryGetValue((mat, b.propertyName), out var t2)) v = t2;
                    }
                    newFrames[i] = new ObjectReferenceKeyframe { time = f.time, value = v };
                }
                AnimationUtility.SetObjectReferenceCurve(newClip, binding, newFrames);
            }
            return newClip;
        }
    }
}
