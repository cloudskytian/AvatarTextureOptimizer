// ATOAnimationRewriter.cs — 动画曲线重写器 / Animation curve rewriter.
// 说明：修改过的动画必须重写引用（贴图→图集、材质→去重代表、材质槽→合并后索引），
// 否则动画会在运行时引用旧资产导致渲染错误。模式与 AAO 一致（读其源码验证）：
//  - new AnimationClip + ObjectRegistry.RegisterReplacedObject(clip, newClip)
//  - 拷贝 m_UseHighQualityCurve 与全部 float 曲线（原样）
//  - 对象引用曲线：按 (path, propertyName) 的贴图/材质映射替换值；槽索引变化时重绑 propertyName
// Note: modified animations must have their references rewritten (texture→atlas, material→dedup representative,
// slot→merged index), otherwise animations would reference stale assets and render incorrectly. Same pattern as AAO
// (verified against its source): new AnimationClip + RegisterReplacedObject + curve copying.

using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Fosa.AvatarTextureOptimizer
{
    /// <summary>动画重写器。/ Animation rewriter.</summary>
    internal static class ATOAnimationRewriter
    {
        /// <summary>
        /// 重写动画：贴图替换（按材质属性/场景路径）、材质替换、材质槽索引重绑。
        /// Rewrite animations: texture replacements (per scene path / global), material replacements, slot index rebinding.
        /// </summary>
        public static int Rewrite(List<AnimationClip> clips,
            Dictionary<Texture2D, Texture2D> textureReplacements,     // 原贴图 → 新贴图（整图路径/去重）/ texture → new texture (whole-texture / dedup)
            Dictionary<Material, Material> materialReplacements,       // 原材质 → 新材质（去重）/ material → new material (dedup)
            Dictionary<(string path, string prop), Dictionary<Texture2D, Texture2D>> pathPropAtlases, // 场景路径 (path, prop) → 贴图 → 图集 / scene-path texture → atlas
            Dictionary<(string path, int slot), int> slotRebinds,      // (path, 旧槽) → 新槽 / (path, old slot) → new slot
            Func<string, string> resolvePath)                          // 绑定路径 → 渲染器路径 / binding path → renderer path
        {
            int rewritten = 0;
            foreach (var clip in clips)
            {
                if (clip == null) continue;
                if (!NeedsRewrite(clip, textureReplacements, materialReplacements, pathPropAtlases, slotRebinds))
                    continue;

                var newClip = new AnimationClip();
                nadena.dev.ndmf.ObjectRegistry.RegisterReplacedObject(clip, newClip);
                newClip.name = clip.name;
                newClip.legacy = clip.legacy;
                newClip.frameRate = clip.frameRate;
                newClip.wrapMode = clip.wrapMode;

                // 拷贝 m_UseHighQualityCurve（无公开 API）/ copy m_UseHighQualityCurve (no public API)
                using (var soSrc = new SerializedObject(clip))
                using (var soDst = new SerializedObject(newClip))
                {
                    var srcProp = soSrc.FindProperty("m_UseHighQualityCurve");
                    var dstProp = soDst.FindProperty("m_UseHighQualityCurve");
                    if (srcProp != null && dstProp != null)
                    {
                        dstProp.boolValue = srcProp.boolValue;
                        soDst.ApplyModifiedPropertiesWithoutUndo();
                    }
                }

                // float 曲线原样拷贝 / copy float curves as-is
                foreach (var binding in AnimationUtility.GetCurveBindings(clip))
                {
                    AnimationUtility.SetEditorCurve(newClip, binding, AnimationUtility.GetEditorCurve(clip, binding));
                }

                // 对象引用曲线：替换值 + 槽重绑 / object-reference curves: replace values + slot rebinding
                foreach (var binding in AnimationUtility.GetObjectReferenceCurveBindings(clip))
                {
                    var frames = AnimationUtility.GetObjectReferenceCurve(clip, binding);
                    if (frames == null || frames.Length == 0) continue;
                    var prop = binding.propertyName;
                    var newBinding = binding;
                    var rebound = false;

                    // 槽索引重绑 / slot index rebinding
                    if (prop.StartsWith("m_Materials.Array.data[", StringComparison.Ordinal))
                    {
                        var idx = ParseSlotIndex(prop);
                        if (idx >= 0 && slotRebinds != null && slotRebinds.TryGetValue((binding.path, idx), out var newIdx))
                        {
                            newBinding.propertyName = "m_Materials.Array.data[" + newIdx + "]";
                            rebound = true;
                        }
                    }

                    for (int i = 0; i < frames.Length; i++)
                    {
                        var value = frames[i].value;
                        if (value is Texture2D tex && tex != null)
                        {
                            // 场景路径贴图切换（每张贴图 → 其图集；绑定路径先解析到渲染器路径）/ scene-path texture swaps (binding path resolved to a renderer path first)
                            var resolvedPath = resolvePath != null ? resolvePath(binding.path) : binding.path;
                            if (!string.IsNullOrEmpty(binding.path) && pathPropAtlases != null &&
                                pathPropAtlases.TryGetValue((resolvedPath, prop), out var texMap) &&
                                texMap.TryGetValue(tex, out var atlas))
                            {
                                frames[i].value = atlas;
                            }
                            // 全局替换（去重 / 整图路径）/ global replacements (dedup / whole-texture path)
                            else if (textureReplacements != null && textureReplacements.TryGetValue(tex, out var rep))
                            {
                                frames[i].value = rep;
                            }
                        }
                        else if (value is Material mat && mat != null && materialReplacements != null &&
                                 materialReplacements.TryGetValue(mat, out var newMat))
                        {
                            frames[i].value = newMat;
                        }
                    }
                    AnimationUtility.SetObjectReferenceCurve(newClip, rebound ? newBinding : binding, frames);
                }
                rewritten++;
                ATOLog.Verbose($"Rewrote animation clip '{clip.name}'");
            }
            return rewritten;
        }

        private static bool NeedsRewrite(AnimationClip clip,
            Dictionary<Texture2D, Texture2D> textureReplacements,
            Dictionary<Material, Material> materialReplacements,
            Dictionary<(string, string), Dictionary<Texture2D, Texture2D>> pathPropAtlases,
            Dictionary<(string, int), int> slotRebinds)
        {
            // NeedsRewrite 保守判定：全局替换已覆盖整图/去重场景；路径映射此处按原样匹配（重写时再解析）
            // conservative: global replacements cover whole-texture/dedup; path mappings are re-matched at rewrite time
            foreach (var binding in AnimationUtility.GetObjectReferenceCurveBindings(clip))
            {
                var frames = AnimationUtility.GetObjectReferenceCurve(clip, binding);
                if (frames == null) continue;
                var prop = binding.propertyName;
                if (prop.StartsWith("m_Materials.Array.data[", StringComparison.Ordinal))
                {
                    var idx = ParseSlotIndex(prop);
                    if (idx >= 0 && slotRebinds != null && slotRebinds.ContainsKey((binding.path, idx))) return true;
                }
                foreach (var f in frames)
                {
                    if (f.value is Texture2D t && t != null)
                    {
                        if (textureReplacements != null && textureReplacements.ContainsKey(t)) return true;
                        if (pathPropAtlases != null && pathPropAtlases.TryGetValue((binding.path, prop), out var m) && m.ContainsKey(t)) return true;
                    }
                    if (f.value is Material mm && mm != null && materialReplacements != null && materialReplacements.ContainsKey(mm)) return true;
                }
            }
            return false;
        }

        private static int ParseSlotIndex(string prop)
        {
            var open = prop.IndexOf('[', StringComparison.Ordinal);
            var close = prop.IndexOf(']', StringComparison.Ordinal);
            if (open < 0 || close <= open) return -1;
            if (int.TryParse(prop.Substring(open + 1, close - open - 1), out var idx)) return idx;
            return -1;
        }
    }
}
