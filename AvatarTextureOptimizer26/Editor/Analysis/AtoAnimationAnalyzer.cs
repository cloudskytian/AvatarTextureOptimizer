using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using nadena.dev.ndmf.animator;
using UnityEditor;
using UnityEngine;
using Net.Fosa.AvatarTextureOptimizer;

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// Collects animation-driven material swaps, texture swaps, renderer enable, scale, cutoff / blend mode.
    /// 收集动画驱动的材质切换、贴图切换、渲染器启用、缩放、Cutoff / 混合模式。
    /// </summary>
    public sealed class AtoAnimFacts
    {
        public readonly HashSet<Renderer> CanEnable = new HashSet<Renderer>();
        public readonly Dictionary<Renderer, List<Material[]>> ExtraMaterials = new Dictionary<Renderer, List<Material[]>>();
        public readonly Dictionary<Material, Dictionary<string, HashSet<Texture2D>>> ExtraTextures =
            new Dictionary<Material, Dictionary<string, HashSet<Texture2D>>>();
        public readonly Dictionary<Material, HashSet<AtoAlphaMode>> ExtraAlpha = new Dictionary<Material, HashSet<AtoAlphaMode>>();
        public readonly Dictionary<Material, float> StrictestCutoff = new Dictionary<Material, float>();
        public readonly HashSet<(Material mat, string prop)> TransformAnimated = new HashSet<(Material, string)>();
        public readonly Dictionary<Transform, float> MaxScaleMul = new Dictionary<Transform, float>();
    }

    public static class AtoAnimationAnalyzer
    {
        private static readonly Regex MatSlot = new Regex(@"m_Materials\.Array\.data\[(\d+)\]", RegexOptions.Compiled);
        private static readonly Regex TexSt = new Regex(@"(.+)_ST\.(x|y|z|w)$", RegexOptions.Compiled);
        private static readonly Regex Scroll = new Regex(@"(.+)_ScrollRotate\.(x|y|z|w)$", RegexOptions.Compiled);

        public static AtoAnimFacts Collect(AtoContext ctx)
        {
            var facts = new AtoAnimFacts();
            var root = ctx.Avatar.transform;

            // Always consider currently enabled renderers. / 当前已启用的渲染器始终纳入。
            foreach (var r in ctx.Avatar.GetComponentsInChildren<Renderer>(true))
            {
                if (r.enabled && r.gameObject.activeInHierarchy) facts.CanEnable.Add(r);
            }

            try
            {
                if (ctx.Anim != null)
                {
                    var index = ctx.Anim.AnimationIndex;
                    foreach (var clip in index.ClipsWithObjectCurves)
                        ScanClip(ctx, facts, clip, root);
                    // Float curves: enable, scale, cutoff. Scan all clips via bindings.
                    // 浮点曲线：启用、缩放、cutoff。通过 binding 扫描全部 clip。
                    ScanFloatViaIndex(ctx, facts, index, root);
                }
            }
            catch (Exception e)
            {
                AtoLog.Warn("AnimatorServices scan failed, falling back to Animator.GetBehaviours-less clip walk: " + e.Message);
                FallbackScan(ctx, facts, root);
            }

            return facts;
        }

        private static void ScanClip(AtoContext ctx, AtoAnimFacts facts, VirtualClip clip, Transform root)
        {
            foreach (var b in clip.GetObjectCurveBindings())
            {
                var curve = clip.GetObjectCurve(b);
                if (curve == null) continue;
                var target = Resolve(root, b.path);
                foreach (var kf in curve)
                {
                    if (kf.value == null) continue;
                    if (kf.value is Material mat)
                    {
                        var r = target != null ? target.GetComponent<Renderer>() : null;
                        if (r != null)
                        {
                            if (!facts.ExtraMaterials.TryGetValue(r, out var list))
                                facts.ExtraMaterials[r] = list = new List<Material[]>();
                            var slot = ParseSlot(b.propertyName);
                            var arr = r.sharedMaterials != null
                                ? (Material[])r.sharedMaterials.Clone()
                                : Array.Empty<Material>();
                            if (slot >= 0 && slot < arr.Length) arr[slot] = mat;
                            list.Add(arr);
                        }
                    }
                    else if (kf.value is Texture2D tex)
                    {
                        var r = target != null ? target.GetComponent<Renderer>() : null;
                        var mats = r != null ? r.sharedMaterials : null;
                        if (mats != null)
                        {
                            foreach (var m in mats)
                            {
                                if (m == null) continue;
                                AddTex(facts, m, StripTexPrefix(b.propertyName), tex);
                            }
                        }
                    }
                }
            }
        }

        private static void ScanFloatViaIndex(AtoContext ctx, AtoAnimFacts facts, AnimationIndex index, Transform root)
        {
            // We cannot enumerate all float bindings from the index API; walk every clip.
            // 索引 API 不能枚举全部 float binding，改为遍历 clip。
            var seen = new HashSet<VirtualClip>();
            foreach (var clip in index.ClipsWithObjectCurves) seen.Add(clip);
            // GetClipsForObjectPath("") is not all clips. Use reflection on private cache if needed.
            // 用 ObjectCurve clips + 再扫 Animator 上的 clip 兜底。
            FallbackScan(ctx, facts, root, mergeOnly: true);
        }

        private static void FallbackScan(AtoContext ctx, AtoAnimFacts facts, Transform root, bool mergeOnly = false)
        {
            var clips = new HashSet<AnimationClip>();
            foreach (var anim in ctx.Avatar.GetComponentsInChildren<Animator>(true))
            {
                if (anim.runtimeAnimatorController == null) continue;
                foreach (var c in anim.runtimeAnimatorController.animationClips)
                    if (c != null) clips.Add(c);
            }
            CollectVrcClips(ctx.Avatar, clips);

            foreach (var clip in clips)
            {
                foreach (var b in AnimationUtility.GetObjectReferenceCurveBindings(clip))
                {
                    var curve = AnimationUtility.GetObjectReferenceCurve(clip, b);
                    if (curve == null) continue;
                    var target = Resolve(root, b.path);
                    foreach (var kf in curve)
                    {
                        if (kf.value is Material mat)
                        {
                            var r = target != null ? target.GetComponent<Renderer>() : null;
                            if (r == null) continue;
                            if (!facts.ExtraMaterials.TryGetValue(r, out var list))
                                facts.ExtraMaterials[r] = list = new List<Material[]>();
                            var slot = ParseSlot(b.propertyName);
                            var arr = r.sharedMaterials != null
                                ? (Material[])r.sharedMaterials.Clone()
                                : Array.Empty<Material>();
                            if (slot >= 0 && slot < arr.Length) arr[slot] = mat;
                            list.Add(arr);
                        }
                        else if (kf.value is Texture2D tex)
                        {
                            var r = target != null ? target.GetComponent<Renderer>() : null;
                            if (r == null) continue;
                            foreach (var m in r.sharedMaterials)
                            {
                                if (m == null) continue;
                                AddTex(facts, m, StripTexPrefix(b.propertyName), tex);
                            }
                        }
                    }
                }

                foreach (var b in AnimationUtility.GetCurveBindings(clip))
                {
                    var curve = AnimationUtility.GetEditorCurve(clip, b);
                    if (curve == null || curve.length == 0) continue;
                    var target = Resolve(root, b.path);
                    if (target == null) continue;

                    if (b.type == typeof(GameObject) && b.propertyName == "m_IsActive")
                    {
                        if (MaxAbs(curve) > 0.5f)
                        {
                            foreach (var r in target.GetComponents<Renderer>())
                                facts.CanEnable.Add(r);
                        }
                    }
                    else if (typeof(Renderer).IsAssignableFrom(b.type) &&
                             (b.propertyName == "m_Enabled" || b.propertyName == "m_enabled"))
                    {
                        var r = target.GetComponent<Renderer>();
                        if (r != null && MaxAbs(curve) > 0.5f) facts.CanEnable.Add(r);
                    }
                    else if (b.type == typeof(Transform) && b.propertyName.StartsWith("m_LocalScale", StringComparison.Ordinal))
                    {
                        var m = MaxAbs(curve);
                        if (!facts.MaxScaleMul.TryGetValue(target, out var cur) || m > cur)
                            facts.MaxScaleMul[target] = m;
                    }
                    else if (typeof(Material).IsAssignableFrom(b.type) || b.propertyName.StartsWith("material.", StringComparison.Ordinal))
                    {
                        HandleMaterialFloat(facts, target, b.propertyName, curve);
                    }
                }
            }
        }

        private static void HandleMaterialFloat(AtoAnimFacts facts, GameObject target, string prop, AnimationCurve curve)
        {
            var r = target.GetComponent<Renderer>();
            if (r == null) return;
            var p = prop.StartsWith("material.", StringComparison.Ordinal) ? prop.Substring("material.".Length) : prop;

            if (p.Contains("_ST.") || p.Contains("_ScrollRotate.") || p.Contains("Scale") && p.Contains("Tex") ||
                p.StartsWith("_MainTex_ST", StringComparison.Ordinal))
            {
                foreach (var m in r.sharedMaterials)
                {
                    if (m == null) continue;
                    var baseName = p;
                    var idx = baseName.IndexOf('.');
                    if (idx > 0) baseName = baseName.Substring(0, idx);
                    facts.TransformAnimated.Add((m, baseName.Replace("_ST", "").Replace("_ScrollRotate", "")));
                }
                return;
            }

            if (p.Contains("Cutoff") || p.Contains("_Cutoff"))
            {
                var max = float.MinValue;
                foreach (var k in curve.keys) max = Mathf.Max(max, k.value);
                foreach (var m in r.sharedMaterials)
                {
                    if (m == null) continue;
                    if (!facts.StrictestCutoff.TryGetValue(m, out var c) || max > c)
                        facts.StrictestCutoff[m] = max;
                }
            }

            if (p.Contains("_Mode") || p.Contains("TransparentMode") || p.Contains("BlendMode"))
            {
                foreach (var k in curve.keys)
                {
                    var mode = Mathf.RoundToInt(k.value) switch
                    {
                        1 => AtoAlphaMode.Cutout,
                        >= 2 => AtoAlphaMode.Blend,
                        _ => AtoAlphaMode.Opaque
                    };
                    foreach (var m in r.sharedMaterials)
                    {
                        if (m == null) continue;
                        if (!facts.ExtraAlpha.TryGetValue(m, out var set))
                            facts.ExtraAlpha[m] = set = new HashSet<AtoAlphaMode>();
                        set.Add(mode);
                    }
                }
            }
        }

        private static void CollectVrcClips(GameObject avatar, HashSet<AnimationClip> clips)
        {
            Component desc = null;
            foreach (var c in avatar.GetComponents<Component>())
            {
                if (c != null && c.GetType().Name == "VRCAvatarDescriptor") { desc = c; break; }
            }
            if (desc == null) return;
            var so = new SerializedObject(desc);
            var it = so.GetIterator();
            var enter = true;
            while (it.Next(enter))
            {
                enter = true;
                if (it.propertyType == SerializedPropertyType.ObjectReference &&
                    it.objectReferenceValue is AnimationClip clip)
                    clips.Add(clip);
                if (it.propertyType == SerializedPropertyType.ObjectReference &&
                    it.objectReferenceValue is RuntimeAnimatorController rac)
                {
                    foreach (var c in rac.animationClips)
                        if (c != null) clips.Add(c);
                }
            }
        }

        private static void AddTex(AtoAnimFacts facts, Material m, string prop, Texture2D tex)
        {
            if (m == null || tex == null) return;
            if (!facts.ExtraTextures.TryGetValue(m, out var map))
                facts.ExtraTextures[m] = map = new Dictionary<string, HashSet<Texture2D>>();
            if (!map.TryGetValue(prop, out var set))
                map[prop] = set = new HashSet<Texture2D>();
            set.Add(tex);
        }

        private static GameObject Resolve(Transform root, string path)
        {
            if (string.IsNullOrEmpty(path)) return root.gameObject;
            var t = root.Find(path);
            return t != null ? t.gameObject : null;
        }

        private static int ParseSlot(string property)
        {
            var m = MatSlot.Match(property ?? "");
            return m.Success ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
        }

        private static string StripTexPrefix(string p)
        {
            if (string.IsNullOrEmpty(p)) return p;
            if (p.StartsWith("material.", StringComparison.Ordinal)) p = p.Substring("material.".Length);
            var idx = p.IndexOf('.');
            if (idx > 0) p = p.Substring(0, idx);
            return p;
        }

        private static float MaxAbs(AnimationCurve c)
        {
            var m = 0f;
            foreach (var k in c.keys) m = Mathf.Max(m, Mathf.Abs(k.value));
            return m;
        }

        public static float HierarchyMaxScaleAreaMul(Transform t, AtoAnimFacts facts)
        {
            // Area scales with product of the two largest axis scales along the chain.
            // 面积随层级上两条最大轴缩放的乘积变化。
            var sx = 1f; var sy = 1f; var sz = 1f;
            while (t != null)
            {
                var ls = t.localScale;
                var mul = 1f;
                if (facts.MaxScaleMul.TryGetValue(t, out var a)) mul = Mathf.Max(1f, a);
                sx *= Mathf.Abs(ls.x) * mul;
                sy *= Mathf.Abs(ls.y) * mul;
                sz *= Mathf.Abs(ls.z) * mul;
                t = t.parent;
            }
            var arr = new[] { sx, sy, sz };
            Array.Sort(arr);
            return Mathf.Max(1e-8f, arr[1] * arr[2]);
        }
    }
}
