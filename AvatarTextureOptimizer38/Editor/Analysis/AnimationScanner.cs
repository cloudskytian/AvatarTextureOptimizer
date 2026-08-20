using System;
using System.Collections.Generic;
using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;
using UnityEditor;
using UnityEngine;

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// Reads merged animator clips via NDMF AnimatorServicesContext (cloned after MA).
    /// 通过 NDMF AnimatorServicesContext 读取 MA 合并后的动画。
    /// </summary>
    public sealed class AnimationFacts
    {
        public readonly HashSet<string> EnabledPaths = new HashSet<string>();
        public readonly Dictionary<string, float> MaxAbsScale = new Dictionary<string, float>();
        public readonly Dictionary<string, List<Material>> PathSlotMaterials = new Dictionary<string, List<Material>>();
        public readonly HashSet<string> IndependentSlotSwitch = new HashSet<string>();
        public readonly Dictionary<string, List<Texture2D>> MaterialPropTextures = new Dictionary<string, List<Texture2D>>();
        public readonly HashSet<string> StAnimated = new HashSet<string>();
        public readonly Dictionary<string, List<float>> Cutoffs = new Dictionary<string, List<float>>();
        public readonly Dictionary<string, List<AlphaEvalMode>> AlphaModes = new Dictionary<string, List<AlphaEvalMode>>();
        public readonly HashSet<Texture2D> AnimatedTextures = new HashSet<Texture2D>();
    }

    public static class AnimationScanner
    {
        public static AnimationFacts Scan(BuildContext ctx, GameObject root)
        {
            var facts = new AnimationFacts();
            AnimatorServicesContext anim = null;
            try { anim = ctx.Extension<AnimatorServicesContext>(); }
            catch (Exception e) { AtoLog.VerboseLog($"AnimatorServicesContext not available: {e.Message}"); }

            if (anim != null)
            {
                foreach (var clip in anim.AnimationIndex.ClipsWithObjectCurves)
                    ScanClip(clip, facts);
                // Float curves: walk all controllers. / 扫描所有控制器的 float 曲线。
                foreach (var ctrl in anim.ControllerContext.GetAllControllers())
                    ScanControllerFloats(ctrl, facts);
            }

            // Also scan leftover Animation components. / 同时扫描遗留 Animation 组件。
            foreach (var a in root.GetComponentsInChildren<Animation>(true))
            {
                if (a == null) continue;
                foreach (AnimationState st in a)
                {
                    if (st != null && st.clip != null) ScanLegacyClip(st.clip, facts);
                }
            }

            AtoLog.Info($"Animation scan: enabledPaths={facts.EnabledPaths.Count} texSwaps={facts.AnimatedTextures.Count} stAnim={facts.StAnimated.Count}");
            return facts;
        }

        private static void ScanControllerFloats(VirtualAnimatorController ctrl, AnimationFacts facts)
        {
            if (ctrl == null) return;
            try
            {
                foreach (var layer in ctrl.Layers)
                {
                    var sm = layer.StateMachine;
                    if (sm == null) continue;
                    foreach (var st in sm.AllStates())
                        ScanMotion(st.Motion);
                    foreach (var kv in layer.SyncedLayerMotionOverrides)
                        ScanMotion(kv.Value);
                }
            }
            catch (Exception e)
            {
                AtoLog.VerboseLog($"Controller walk: {e.Message}");
            }

            void ScanMotion(VirtualMotion m)
            {
                if (m == null) return;
                if (m is VirtualClip clip) ScanClipFloats(clip, facts);
                else if (m is VirtualBlendTree bt)
                {
                    try
                    {
                        foreach (var child in bt.Children)
                            ScanMotion(child.Motion);
                    }
                    catch { /* blend tree shape varies */ }
                }
            }
        }

        private static void ScanClip(VirtualClip clip, AnimationFacts facts)
        {
            foreach (var b in clip.GetObjectCurveBindings())
            {
                var keys = clip.GetObjectCurve(b);
                if (keys == null) continue;
                var path = b.path ?? "";
                var prop = b.propertyName ?? "";

                if (prop == "m_IsActive" || prop == "m_Enabled")
                {
                    foreach (var k in keys)
                    {
                        if (k.value is GameObject go && go.activeSelf) facts.EnabledPaths.Add(path);
                        if (k.value is Renderer) facts.EnabledPaths.Add(path);
                    }
                    // Object curves for bools are rare; float path handles enable. / bool 多在 float 曲线。
                    facts.EnabledPaths.Add(path);
                }

                if (prop.StartsWith("m_Materials", StringComparison.Ordinal) ||
                    prop.IndexOf("m_SharedMaterial", StringComparison.Ordinal) >= 0)
                {
                    var key = path + "#" + prop;
                    if (!facts.PathSlotMaterials.TryGetValue(key, out var list))
                    {
                        list = new List<Material>();
                        facts.PathSlotMaterials[key] = list;
                    }
                    int distinct = 0;
                    foreach (var k in keys)
                    {
                        if (k.value is Material mat && mat != null)
                        {
                            if (!list.Contains(mat)) { list.Add(mat); distinct++; }
                        }
                    }
                    if (distinct > 1) facts.IndependentSlotSwitch.Add(key);
                }

                // material._MainTex object reference swaps. / 贴图对象引用切换。
                if (prop.StartsWith("material.", StringComparison.Ordinal))
                {
                    foreach (var k in keys)
                    {
                        if (k.value is Texture2D t && t != null)
                        {
                            facts.AnimatedTextures.Add(t);
                            var mk = path + "|" + prop;
                            if (!facts.MaterialPropTextures.TryGetValue(mk, out var tl))
                            {
                                tl = new List<Texture2D>();
                                facts.MaterialPropTextures[mk] = tl;
                            }
                            if (!tl.Contains(t)) tl.Add(t);
                        }
                    }
                }
            }

            ScanClipFloats(clip, facts);
        }

        private static void ScanClipFloats(VirtualClip clip, AnimationFacts facts)
        {
            foreach (var b in clip.GetFloatCurveBindings())
            {
                var curve = clip.GetFloatCurve(b);
                if (curve == null) continue;
                var path = b.path ?? "";
                var prop = b.propertyName ?? "";

                if (prop == "m_IsActive" || prop == "m_Enabled")
                {
                    foreach (var k in curve.keys)
                        if (k.value > 0.5f) facts.EnabledPaths.Add(path);
                }

                if (prop.IndexOf("localScale", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    prop.IndexOf("m_LocalScale", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    float max = 1f;
                    foreach (var k in curve.keys) max = Mathf.Max(max, Mathf.Abs(k.value));
                    if (!facts.MaxAbsScale.TryGetValue(path, out var prev) || max > prev)
                        facts.MaxAbsScale[path] = max;
                }

                if (prop.IndexOf("_ST", StringComparison.Ordinal) >= 0 ||
                    prop.IndexOf("Scale", StringComparison.Ordinal) >= 0 && prop.StartsWith("material.", StringComparison.Ordinal) ||
                    prop.IndexOf("Offset", StringComparison.Ordinal) >= 0 && prop.StartsWith("material.", StringComparison.Ordinal) ||
                    prop.IndexOf("ScrollRotate", StringComparison.Ordinal) >= 0)
                {
                    facts.StAnimated.Add(path + "|" + prop);
                }

                if (prop.IndexOf("_Cutoff", StringComparison.Ordinal) >= 0)
                {
                    var key = path + "|" + prop;
                    if (!facts.Cutoffs.TryGetValue(key, out var list))
                    {
                        list = new List<float>();
                        facts.Cutoffs[key] = list;
                    }
                    foreach (var k in curve.keys) list.Add(k.value);
                }

                if (prop.IndexOf("_Mode", StringComparison.Ordinal) >= 0 ||
                    prop.IndexOf("_TransparentMode", StringComparison.Ordinal) >= 0 ||
                    prop.IndexOf("SrcBlend", StringComparison.Ordinal) >= 0)
                {
                    var key = path + "|" + prop;
                    if (!facts.AlphaModes.TryGetValue(key, out var list))
                    {
                        list = new List<AlphaEvalMode>();
                        facts.AlphaModes[key] = list;
                    }
                    foreach (var k in curve.keys)
                        list.Add(GuessAlpha((int)k.value));
                }
            }
        }

        private static void ScanLegacyClip(AnimationClip clip, AnimationFacts facts)
        {
            foreach (var b in AnimationUtility.GetObjectReferenceCurveBindings(clip))
            {
                var keys = AnimationUtility.GetObjectReferenceCurve(clip, b);
                if (keys == null) continue;
                foreach (var k in keys)
                {
                    if (k.value is Texture2D t && t != null) facts.AnimatedTextures.Add(t);
                    if (k.value is Material) facts.IndependentSlotSwitch.Add((b.path ?? "") + "#" + (b.propertyName ?? ""));
                }
            }
        }

        private static AlphaEvalMode GuessAlpha(int mode)
        {
            // Standard shader _Mode: 0 Opaque, 1 Cutout, 2 Fade, 3 Transparent.
            // 标准着色器 _Mode。
            if (mode == 1) return AlphaEvalMode.Cutout;
            if (mode >= 2) return AlphaEvalMode.Blend;
            return AlphaEvalMode.Opaque;
        }
    }
}
