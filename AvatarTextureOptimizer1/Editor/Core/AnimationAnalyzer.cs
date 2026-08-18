// AnimationAnalyzer.cs / AnimationAnalyzer.cs
// Deep animation analysis using NDMF AnimatorServices (post-MA-merge). Detects:
//  - Material switches on Renderer
//  - Texture property swaps
//  - _ST offset/scale/rotation / _ScrollRotate (-> whitelist)
//  - Render mode / Cutoff changes (take strictest)
//  - Object enable/disable (include inactive renderers that are enabled by animation)
//
// 使用NDMF AnimatorServices（MA合并后）的深度动画分析。检测：
//  - Renderer材质切换
//  - 贴图属性交换
//  - _ST偏移/缩放/旋转/_ScrollRotate（→白名单）
//  - 渲染模式/Cutoff变化（取最严格）
//  - 对象启用/禁用（包含被动画启用的非活跃渲染器）

using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEditor;
using System.Linq;
using nadena.dev.ndmf;
// Note: nadena.dev.ndmf.animator (AnimatorServicesContext) can be used for post-MA virtual clips,
// but we currently scan scene + playable layer clips directly as a robust fallback.
// 注意：nadena.dev.ndmf.animator（AnimatorServicesContext）可用于MA合并后的虚拟片段，
// 目前我们直接扫描场景+playable layer片段作为稳健回退。

namespace net.fosa.avatar_texture_optimizer.Editor.Core
{
    public class AnimationAnalysisResult
    {
        public Dictionary<(Renderer renderer, int slot), List<Material>> AnimatedMaterials = new();
        public Dictionary<(Renderer renderer, int slot, string propName), List<Texture2D>> AnimatedTextures = new();
        public HashSet<(Renderer renderer, int slot, string propName)> AnimatedST = new();
        public HashSet<Renderer> AnimationEnabledRenderers = new();
        public Dictionary<Material, float> MaxCutoff = new();
        public HashSet<Material> HasAnimatedAlpha = new();
        public Dictionary<(Renderer renderer, int slot), AlphaMode> MaxAlphaMode = new();
    }

    public static class AnimationAnalyzer
    {
        public static AnimationAnalysisResult Analyze(BuildContext context, AvatarAnalysisResult analysis)
        {
            var result = new AnimationAnalysisResult();
            var root = context.AvatarRootObject;

            // Scan clips reachable via scene animators + VRChat playable layers (safe fallback if NDMF not available)
            var clips = CollectClips(root);

            foreach (var clip in clips)
            {
                if (clip == null) continue;
                ProcessClip(clip, root, analysis, result);
            }

            return result;
        }

        private static void ProcessClip(AnimationClip clip, GameObject root, AvatarAnalysisResult analysis, AnimationAnalysisResult result)
        {
            // Object reference curves (materials/textures/GameObjects)
            foreach (var binding in AnimationUtility.GetObjectReferenceCurveBindings(clip))
            {
                var kfs = AnimationUtility.GetObjectReferenceCurve(clip, binding);
                foreach (var kf in kfs)
                {
                    if (kf.value == null) continue;
                    var go = FindOnPath(root, binding.path);
                    if (go == null) continue;

                    if (kf.value is Material mat)
                    {
                        var r = go.GetComponent<Renderer>();
                        if (r != null)
                        {
                            int slot = ParseMaterialSlot(binding.propertyName);
                            var key = (r, slot);
                            if (!result.AnimatedMaterials.TryGetValue(key, out var mats))
                                result.AnimatedMaterials[key] = mats = new List<Material>();
                            if (!mats.Contains(mat)) mats.Add(mat);
                        }
                    }
                    if (kf.value is Texture2D tex)
                    {
                        var r = go.GetComponent<Renderer>();
                        if (r != null && binding.propertyName.StartsWith("material."))
                        {
                            string prop = binding.propertyName.Substring("material.".Length);
                            int slot = 0;
                            if (prop.StartsWith("m_Materials.Array.data["))
                            {
                                int endBracket = prop.IndexOf(']');
                                if (endBracket > 0)
                                {
                                    int.TryParse(prop.Substring("m_Materials.Array.data[".Length, endBracket - "m_Materials.Array.data[".Length), out slot);
                                    // Array element PPtr bindings reference Material objects, not texture properties. Skip.
                                    continue;
                                }
                            }
                            var key = (r, slot, prop);
                            if (!result.AnimatedTextures.TryGetValue(key, out var texs))
                                result.AnimatedTextures[key] = texs = new List<Texture2D>();
                            if (!texs.Contains(tex)) texs.Add(tex);
                        }
                    }
                }
            }

            // Float curves for ST, cutoff, mode, m_IsActive
            foreach (var binding in AnimationUtility.GetCurveBindings(clip))
            {
                var curve = AnimationUtility.GetEditorCurve(clip, binding);
                if (curve == null) continue;
                var go = FindOnPath(root, binding.path);
                if (go == null) continue;

                if (binding.propertyName == "m_IsActive")
                {
                    foreach (var k in curve.keys)
                        if (k.value > 0.5f)
                            foreach (var r in go.GetComponentsInChildren<Renderer>(true))
                                result.AnimationEnabledRenderers.Add(r);
                    continue;
                }

                if (binding.propertyName.StartsWith("material."))
                {
                    string prop = binding.propertyName.Substring("material.".Length);
                    var r = go.GetComponent<Renderer>();
                    if (r == null) continue;
                    int slot = 0;
                    // Parse material array slot: material.m_Materials.Array.data[i]._Property
                    if (prop.StartsWith("m_Materials.Array.data["))
                    {
                        int endBracket = prop.IndexOf(']');
                        if (endBracket > 0)
                        {
                            int.TryParse(prop.Substring("m_Materials.Array.data[".Length, endBracket - "m_Materials.Array.data[".Length), out slot);
                            int dot = prop.IndexOf('.', endBracket);
                            if (dot >= 0) prop = prop.Substring(dot + 1);
                            else continue;
                        }
                    }

                    if (prop.EndsWith("_ST") || prop.Contains("_ScrollRotate") || prop.Contains("_Scroll"))
                    {
                        string texProp = prop.Split('.')[0];
                        if (texProp.EndsWith("_ST")) texProp = texProp.Substring(0, texProp.Length - 3);
                        result.AnimatedST.Add((r, slot, texProp));
                        var mat = r.sharedMaterials != null && slot < r.sharedMaterials.Length ? r.sharedMaterials[slot] : null;
                        var tex = mat != null && mat.HasProperty(texProp) ? mat.GetTexture(texProp) as Texture2D : null;
                        if (tex != null) analysis.WhitelistedTextures.Add(tex);
                    }
                    if (prop == "_Cutoff" || prop.EndsWith("._Cutoff"))
                    {
                        float maxC = 0;
                        foreach (var k in curve.keys) if (k.value > maxC) maxC = k.value;
                        var mat = r.sharedMaterials != null && slot < r.sharedMaterials.Length ? r.sharedMaterials[slot] : null;
                        if (mat != null)
                        {
                            if (!result.MaxCutoff.TryGetValue(mat, out var cur) || maxC > cur) result.MaxCutoff[mat] = Mathf.Max(cur, maxC);
                        }
                    }
                    if (prop == "_Mode" || prop == "_TransparentMode" || prop == "_SrcBlend" || prop == "_DstBlend" || prop.Contains("_RenderType"))
                    {
                        var mat = r.sharedMaterials != null && slot < r.sharedMaterials.Length ? r.sharedMaterials[slot] : null;
                        if (mat != null) result.HasAnimatedAlpha.Add(mat);
                    }
                }
            }
        }

        private static List<AnimationClip> CollectClips(GameObject root)
        {
            var set = new HashSet<AnimationClip>();
            var animators = root.GetComponentsInChildren<Animator>(true);
            var animations = root.GetComponentsInChildren<Animation>(true);
            foreach (var a in animators)
            {
                if (a.runtimeAnimatorController == null) continue;
                foreach (var c in a.runtimeAnimatorController.animationClips) if (c != null) set.Add(c);
            }
            foreach (var a in animations) if (a.clip != null) set.Add(a.clip);
#if ATO_VRCSDK_INSTALLED
            try
            {
                var desc = root.GetComponent<VRC.SDK3.Avatars.Components.VRCAvatarDescriptor>();
                if (desc != null)
                {
                    void Add(RuntimeAnimatorController c) { if (c != null) foreach (var cl in c.animationClips) if (cl != null) set.Add(cl); }
                    foreach (var l in desc.baseAnimationLayers) Add(l.animatorController);
                    foreach (var l in desc.specialAnimationLayers) Add(l.animatorController);
                }
            }
            catch { /* ignore */ }
#endif
            return set.ToList();
        }

        private static GameObject FindOnPath(GameObject root, string path)
        {
            if (string.IsNullOrEmpty(path)) return root;
            var t = root.transform.Find(path);
            return t != null ? t.gameObject : null;
        }

        private static int ParseMaterialSlot(string propertyName)
        {
            int idx = propertyName.IndexOf("m_Materials.Array.data[", StringComparison.Ordinal);
            if (idx < 0) return 0;
            int start = idx + "m_Materials.Array.data[".Length;
            int end = propertyName.IndexOf(']', start);
            if (end < 0) return 0;
            int.TryParse(propertyName.Substring(start, end - start), out int slot);
            return slot;
        }
    }
}
