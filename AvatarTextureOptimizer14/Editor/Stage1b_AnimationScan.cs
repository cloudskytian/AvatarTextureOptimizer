// Stage1b_AnimationScan — enumerate & analyze all animations affecting the avatar / 枚举并分析影响 Avatar 的全部动画
// Sources: descriptor layers (MA already merged), child Animator components, legacy Animation components.<br>
// 来源：Descriptor 动画层（MA 已合并）、子级 Animator 组件、旧版 Animation 组件。
// Notes: we deliberately over-approximate (superset) — including an animation that never runs only
// enlarges texture sets; missing one would break safety. / 刻意超集近似：多算只增大贴图集，漏算才破坏安全。
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using Object = UnityEngine.Object;

namespace Fosa.ATO.Editor
{
    internal sealed class AnimationScanResult
    {
        internal readonly HashSet<string> enabledPaths = new HashSet<string>();
        internal readonly Dictionary<string, Vector3> scaleMaxByPath = new Dictionary<string, Vector3>();
        internal readonly Dictionary<string, List<Material>> slotMaterialSets = new Dictionary<string, List<Material>>();      // "path#slot"
        internal readonly Dictionary<string, List<Texture2D>> propTextureSets = new Dictionary<string, List<Texture2D>>();      // "path|prop"
        internal readonly HashSet<string> uvGuardPropsAnimated = new HashSet<string>();                                          // "path|propBase"
        internal readonly Dictionary<string, float> floatMaxOfPathProp = new Dictionary<string, float>();                        // "path|prop" → max value
        internal readonly Dictionary<string, HashSet<float>> valuesOfPathProp = new Dictionary<string, HashSet<float>>();        // "path|prop" → values
        internal readonly HashSet<string> materialCurvePaths = new HashSet<string>();                                            // blocks slot merge
        internal HashSet<AnimationClip> clips = new HashSet<AnimationClip>();
    }

    internal static class AnimationScan
    {
        internal static AnimationScanResult Run(GameObject root, VRCAvatarDescriptor desc)
        {
            var res = new AnimationScanResult();
            var clips = new HashSet<AnimationClip>();

            // Descriptor playable layers (base + special) / Descriptor 可播放层
            if (desc != null)
            {
                foreach (var layer in desc.baseAnimationLayers) CollectController(layer.animatorController, clips);
                foreach (var layer in desc.specialAnimationLayers) CollectController(layer.animatorController, clips);
            }
            // Child animators (props etc.) / 子级 Animator（道具等）
            foreach (var a in root.GetComponentsInChildren<Animator>(true)) CollectController(a.runtimeAnimatorController, clips);
            // Legacy Animation components / 旧版 Animation
            foreach (var legacy in root.GetComponentsInChildren<Animation>(true))
                try { foreach (var c in AnimationUtility.GetAnimationClips(legacy.gameObject)) if (c != null) clips.Add(c); }
                catch { /* legacy read issues are non-fatal / 读取失败不致命 */ }
            // Also every clip already referenced by the VRC gesture/emote menus is inside layers above. / 其余表情动画已在层中
            res.clips = clips;
            ATOLog.V($"animation clips collected: {clips.Count}");

            foreach (var clip in clips) try { ScanClip(clip, res); } catch (Exception e) { ATOLog.Warn($"clip scan failed '{clip?.name}': {e.Message}"); }
            return res;
        }

        // ---------------------------------------------------------------- controllers
        private static void CollectController(RuntimeAnimatorController rac, HashSet<AnimationClip> clips, int depth = 0)
        {
            if (rac == null || depth > 8) return;
            if (rac is AnimatorController ac)
                foreach (var layer in ac.layers) CollectStateMachine(layer.stateMachine, clips, depth + 1);
            else if (rac is AnimatorOverrideController aoc)
            {
                CollectController(aoc.runtimeAnimatorController, clips, depth + 1);   // underlying (superset) / 底层一并收集
                var list = new List<KeyValuePair<AnimationClip, AnimationClip>>(aoc.overridesCount);
                aoc.GetOverrides(list);
                foreach (var kv in list) if (kv.Value != null) clips.Add(kv.Value);
            }
        }

        private static void CollectStateMachine(AnimatorStateMachine sm, HashSet<AnimationClip> clips, int depth)
        {
            if (sm == null || depth > 16) return;
            foreach (var cs in sm.states) CollectMotion(cs.state?.motion, clips, depth + 1);
            foreach (var csm in sm.stateMachines) CollectStateMachine(csm.stateMachine, clips, depth + 1);
        }

        private static void CollectMotion(Motion m, HashSet<AnimationClip> clips, int depth)
        {
            if (m == null || depth > 16) return;
            if (m is AnimationClip clip) { clips.Add(clip); return; }
            if (m is BlendTree bt) foreach (var child in bt.children) CollectMotion(child.motion, clips, depth + 1);
        }

        // ---------------------------------------------------------------- clips
        private static void ScanClip(AnimationClip clip, AnimationScanResult res)
        {
            // Object reference curves: material slot swap & texture swap / 对象引用曲线：材质槽切换与贴图切换
            foreach (var binding in AnimationUtility.GetObjectReferenceCurveBindings(clip))
            {
                var keys = AnimationUtility.GetObjectReferenceCurve(clip, binding);
                var name = binding.propertyName;
                if (keys == null) continue;

                if (name.StartsWith("m_Materials.Array.data[", StringComparison.Ordinal))
                {
                    res.materialCurvePaths.Add(binding.path);
                    int slot = ParseSlotIndex(name);
                    var key = binding.path + "#" + slot;
                    if (!res.slotMaterialSets.TryGetValue(key, out var list)) res.slotMaterialSets[key] = list = new List<Material>();
                    foreach (var k in keys) if (k.value is Material m && !list.Contains(m)) list.Add(m);
                }
                else if (name.StartsWith("material.", StringComparison.Ordinal))
                {
                    res.materialCurvePaths.Add(binding.path);
                    var prop = StripComponentSuffix(name.Substring("material.".Length));
                    if (prop.EndsWith("_ST", StringComparison.Ordinal) || prop.Contains("ScrollRotate")) { /* float curves handle ST / ST由float曲线处理 */ }
                    var key = binding.path + "|" + prop;
                    if (!res.propTextureSets.TryGetValue(key, out var list)) res.propTextureSets[key] = list = new List<Texture2D>();
                    foreach (var k in keys) if (k.value is Texture2D t && !list.Contains(t)) list.Add(t);
                }
            }

            // Float curves / 浮点曲线
            foreach (var binding in AnimationUtility.GetCurveBindings(clip))
            {
                var attr = binding.propertyName;
                if (attr == "m_IsActive" && binding.type == typeof(GameObject))
                {
                    res.enabledPaths.Add(binding.path);
                    continue;
                }
                if (attr == "m_Enabled" && typeof(Renderer).IsAssignableFrom(binding.type))
                {
                    res.enabledPaths.Add(binding.path);
                    continue;
                }
                if (attr.StartsWith("material.", StringComparison.Ordinal))
                {
                    res.materialCurvePaths.Add(binding.path);
                    var prop = StripComponentSuffix(attr.Substring("material.".Length));
                    var pp = binding.path + "|" + prop;
                    var curve = AnimationUtility.GetEditorCurve(clip, binding);
                    if (curve == null || curve.keys.Length == 0) continue;
                    float max = float.NegativeInfinity;
                    foreach (var k in curve.keys) max = Mathf.Max(max, k.value);
                    if (prop == "_Cutoff" || prop == "_TransparentMode" || prop == "_Mode" || prop == "_Surface" || prop == "_AlphaClip")
                    {
                        if (!res.floatMaxOfPathProp.TryGetValue(pp, out var cur) || max > cur) res.floatMaxOfPathProp[pp] = max;
                        if (!res.valuesOfPathProp.TryGetValue(pp, out var set)) res.valuesOfPathProp[pp] = set = new HashSet<float>();
                        foreach (var k in curve.keys) set.Add(k.value);
                    }
                    // anything touching UV-transform-ish props must be tracked / 记录触碰UV变换类属性
                    if (prop.Contains("_ST") || prop.Contains("ScrollRotate") || prop.Contains("UVMode") || prop.Contains("Decal") || prop == "_ShiftBackfaceUV")
                        res.uvGuardPropsAnimated.Add(pp);
                    continue;
                }
                if (attr.StartsWith("m_LocalScale.", StringComparison.Ordinal))
                {
                    var curve = AnimationUtility.GetEditorCurve(clip, binding);
                    if (curve == null) continue;
                    if (!res.scaleMaxByPath.TryGetValue(binding.path, out var v)) v = Vector3.negativeInfinity;
                    float max = 0f;
                    foreach (var k in curve.keys) max = Mathf.Max(max, Mathf.Abs(k.value));
                    switch (attr)
                    {
                        case "m_LocalScale.x": v.x = Mathf.Max(v.x, max); break;
                        case "m_LocalScale.y": v.y = Mathf.Max(v.y, max); break;
                        case "m_LocalScale.z": v.z = Mathf.Max(v.z, max); break;
                    }
                    res.scaleMaxByPath[binding.path] = v;
                }
            }
        }

        private static int ParseSlotIndex(string propertyName)
        {
            var a = propertyName.IndexOf('[', StringComparison.Ordinal);
            var b = propertyName.IndexOf(']', Math.Max(0, a));
            if (a >= 0 && b > a && int.TryParse(propertyName.Substring(a + 1, b - a - 1), out var v)) return v;
            return 0;
        }

        private static string StripComponentSuffix(string prop)
        {
            // "_MainTex_ST.x" → base "_MainTex_ST"; "_Color.r" → "_Color" / 去掉分量后缀
            var dot = prop.LastIndexOf('.');
            if (dot > 0) prop = prop.Substring(0, dot);
            return prop;
        }
    }
}
