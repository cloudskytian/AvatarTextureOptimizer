using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
#if ATO_VRCSDK3_AVATARS
using VRC.SDK3.Avatars.Components;
#endif
using FOSA.AvatarTextureOptimizer;

namespace FOSA.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// Walks every animator on the avatar (VRC layers + child Animators) after MA has merged them.
    /// 遍历 Avatar 上每一个 Animator（VRC 层 + 子级 Animator）。此时 MA 已经合并完毕。
    /// </summary>
    internal static class ATOAnimationAnalyzer
    {
        public static void Run(ATOContext ctx)
        {
            var clips = new HashSet<AnimationClip>();
            foreach (var ac in CollectControllers(ctx))
            {
                CollectClips(ac, clips);
            }

            ctx.Log.Info($"Animation clips: {clips.Count}");

            var pathToTransform = BuildPathMap(ctx.Build.AvatarRootTransform);

            foreach (var clip in clips)
            {
                if (clip == null) continue;
                AnalyzeClip(ctx, clip, pathToTransform);
            }

            // Renderers that stay disabled and are never enabled by animation are skipped later.
            // 一直关闭且动画也不会打开的 Renderer 会在后续被跳过。
            foreach (var ri in ctx.Renderers)
            {
                ctx.Log.Detail($"Renderer '{ri.Renderer.name}' enabledNow={ri.EnabledNow} enabledAnim={ri.EnabledByAnimation} maxScale={ri.MaxWorldScale:F3}");
            }
        }

        public static IEnumerable<RuntimeAnimatorController> CollectControllers(ATOContext ctx)
        {
            var set = new HashSet<RuntimeAnimatorController>();
            var root = ctx.Build.AvatarRootObject;

#if ATO_VRCSDK3_AVATARS
            var desc = root.GetComponent<VRCAvatarDescriptor>();
            if (desc != null)
            {
                AddLayers(desc.baseAnimationLayers, set);
                AddLayers(desc.specialAnimationLayers, set);
            }
#endif
            foreach (var anim in root.GetComponentsInChildren<Animator>(true))
            {
                if (anim != null && anim.runtimeAnimatorController != null)
                    set.Add(anim.runtimeAnimatorController);
            }
            return set;
        }

#if ATO_VRCSDK3_AVATARS
        private static void AddLayers(VRCAvatarDescriptor.CustomAnimLayer[] layers, HashSet<RuntimeAnimatorController> set)
        {
            if (layers == null) return;
            foreach (var layer in layers)
            {
                if (layer.isDefault) continue;
                if (layer.animatorController != null) set.Add(layer.animatorController);
            }
        }
#endif

        public static void CollectClips(RuntimeAnimatorController rac, HashSet<AnimationClip> dst)
        {
            if (rac == null) return;
            foreach (var c in rac.animationClips)
            {
                if (c != null) dst.Add(c);
            }

            var ac = rac as AnimatorController;
            if (ac == null && rac is AnimatorOverrideController aoc)
                ac = aoc.runtimeAnimatorController as AnimatorController;
            if (ac == null) return;

            foreach (var layer in ac.layers)
            {
                WalkStateMachine(layer.stateMachine, dst, new HashSet<AnimatorStateMachine>());
            }
        }

        private static void WalkStateMachine(AnimatorStateMachine sm, HashSet<AnimationClip> dst, HashSet<AnimatorStateMachine> seen)
        {
            if (sm == null || !seen.Add(sm)) return;
            foreach (var s in sm.states)
            {
                CollectMotion(s.state.motion, dst, new HashSet<Motion>());
            }
            foreach (var sub in sm.stateMachines)
            {
                WalkStateMachine(sub.stateMachine, dst, seen);
            }
        }

        private static void CollectMotion(Motion m, HashSet<AnimationClip> dst, HashSet<Motion> seen)
        {
            if (m == null || !seen.Add(m)) return;
            if (m is AnimationClip clip) { dst.Add(clip); return; }
            if (m is BlendTree bt)
            {
                foreach (var c in bt.children) CollectMotion(c.motion, dst, seen);
            }
        }

        private static Dictionary<string, Transform> BuildPathMap(Transform root)
        {
            var map = new Dictionary<string, Transform>();
            var stack = new Stack<Transform>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                var t = stack.Pop();
                var path = AnimationUtility.CalculateTransformPath(t, root);
                if (!map.ContainsKey(path)) map[path] = t;
                for (int i = 0; i < t.childCount; i++) stack.Push(t.GetChild(i));
            }
            return map;
        }

        private static void AnalyzeClip(ATOContext ctx, AnimationClip clip, Dictionary<string, Transform> pathMap)
        {
            var root = ctx.Build.AvatarRootTransform;

            foreach (var binding in AnimationUtility.GetCurveBindings(clip))
            {
                var curves = AnimationUtility.GetEditorCurve(clip, binding);
                if (curves == null) continue;

                var target = Resolve(pathMap, binding.path);
                if (target == null) continue;

                // GameObject active / Renderer enabled. / 物体激活 / Renderer 启用。
                if (binding.propertyName == "m_IsActive" || binding.propertyName == "m_Enabled")
                {
                    if (CurveMax(curves) > 0.5f)
                    {
                        foreach (var r in target.GetComponentsInChildren<Renderer>(true))
                        {
                            var ri = Find(ctx, r);
                            if (ri != null) ri.EnabledByAnimation = true;
                        }
                    }
                }

                // Scale. / 缩放。
                if (binding.propertyName.StartsWith("m_LocalScale", StringComparison.Ordinal))
                {
                    var max = Mathf.Abs(CurveMaxAbs(curves));
                    foreach (var r in target.GetComponentsInChildren<Renderer>(true))
                    {
                        var ri = Find(ctx, r);
                        if (ri == null) continue;
                        // Approximate: animated local scale multiplies current lossy scale.
                        // 近似：动画局部缩放乘到当前 lossyScale 上。
                        ri.MaxWorldScale = Mathf.Max(ri.MaxWorldScale, ATOAvatarScanner.MaxAxis(r.transform.lossyScale) * Mathf.Max(1f, max));
                    }
                }

                // Material float properties that change quality constraints.
                // 会改变质量约束的材质浮点属性。
                if (binding.propertyName.IndexOf("material.", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    HandleMaterialFloat(ctx, target, binding, curves);
                }
            }

            foreach (var binding in AnimationUtility.GetObjectReferenceCurveBindings(clip))
            {
                var keys = AnimationUtility.GetObjectReferenceCurve(clip, binding);
                if (keys == null) continue;
                var target = Resolve(pathMap, binding.path);
                if (target == null) continue;

                foreach (var k in keys)
                {
                    if (k.value is Material mat)
                    {
                        HandleAnimatedMaterial(ctx, target, binding, mat);
                    }
                    else if (k.value is Texture2D tex)
                    {
                        HandleAnimatedTexture(ctx, target, binding, tex);
                    }
                }
            }
        }

        private static void HandleMaterialFloat(ATOContext ctx, Transform target, EditorCurveBinding binding, AnimationCurve curves)
        {
            var prop = binding.propertyName;
            var isSt = prop.IndexOf("_ST", StringComparison.Ordinal) >= 0 ||
                       prop.IndexOf("ScrollRotate", StringComparison.Ordinal) >= 0 ||
                       prop.IndexOf("Rotation", StringComparison.OrdinalIgnoreCase) >= 0;
            var isCutoff = prop.IndexOf("_Cutoff", StringComparison.Ordinal) >= 0;
            var isMode = prop.IndexOf("_Mode", StringComparison.Ordinal) >= 0 ||
                         prop.IndexOf("_TransparentMode", StringComparison.Ordinal) >= 0 ||
                         prop.IndexOf("_Surface", StringComparison.Ordinal) >= 0;

            foreach (var r in target.GetComponents<Renderer>())
            {
                foreach (var use in ctx.Uses)
                {
                    if (use.Renderer == null || use.Renderer.Renderer != r) continue;
                    if (isSt)
                    {
                        use.Slot.eligible = false;
                        use.Slot.hasTransform = true;
                        use.Slot.ineligibleReason = "animated ST / rotate on " + prop;
                        if (use.Slot.texture != null)
                            ctx.WarnWhitelist(use.Slot.texture, use.Slot.ineligibleReason);
                    }
                    if (isCutoff)
                    {
                        var v = CurveExtremum(curves, true);
                        // Most stringent cutout = highest cutoff. / 最严苛 cutout = 最大 cutoff。
                        use.Slot.cutoff = Mathf.Max(use.Slot.cutoff, v);
                    }
                    if (isMode)
                    {
                        var v = Mathf.RoundToInt(CurveMax(curves));
                        if (v == 1) use.Slot.alphaMode = MostStrict(use.Slot.alphaMode, ATOAlphaMode.Cutout);
                        if (v >= 2) use.Slot.alphaMode = MostStrict(use.Slot.alphaMode, ATOAlphaMode.Blend);
                    }
                }
            }
        }

        private static void HandleAnimatedMaterial(ATOContext ctx, Transform target, EditorCurveBinding binding, Material mat)
        {
            if (mat == null) return;
            var renderers = target.GetComponents<Renderer>();
            foreach (var r in renderers)
            {
                var ri = Find(ctx, r);
                if (ri == null) continue;
                var sub = GuessSubmeshFromBinding(binding, ri.SharedMaterials.Length);
                var slots = ATOShaderHub.AnalyzeMaterial(ctx, ri, mat, sub);
                foreach (var slot in slots)
                {
                    ctx.Uses.Add(new ATOTextureUse { Slot = slot, Renderer = ri });
                    ctx.Log.Detail($"Anim material swap '{mat.name}' on '{r.name}' prop={slot.propertyName} tex={slot.texture?.name}");
                }
            }
        }

        private static void HandleAnimatedTexture(ATOContext ctx, Transform target, EditorCurveBinding binding, Texture2D tex)
        {
            if (tex == null) return;
            var prop = ExtractMaterialProperty(binding.propertyName);
            foreach (var r in target.GetComponents<Renderer>())
            {
                var ri = Find(ctx, r);
                if (ri == null) continue;
                // Attach as an extra use of the same UV as the original property.
                // 作为同一属性原 UV 的额外引用挂上去。
                ATOTextureUse host = null;
                foreach (var u in ctx.Uses)
                {
                    if (u.Renderer == ri && u.Slot.propertyName == prop) { host = u; break; }
                }
                var slot = new ATOTextureSlotInfo
                {
                    material = host?.Slot.material,
                    renderer = r,
                    submeshIndex = host?.Slot.submeshIndex ?? 0,
                    propertyName = prop ?? binding.propertyName,
                    texture = tex,
                    uvChannel = host?.Slot.uvChannel ?? 0,
                    category = host?.Slot.category ?? ATOTextureCategory.Unknown,
                    alphaMode = host?.Slot.alphaMode ?? ATOAlphaMode.Opaque,
                    cutoff = host?.Slot.cutoff ?? 0.5f,
                    eligible = host?.Slot.eligible ?? true,
                    hasNormalCompanion = host?.Slot.hasNormalCompanion ?? false,
                    hasMaskCompanion = host?.Slot.hasMaskCompanion ?? false,
                    colorSpace = ATOTextureUtil.GuessLinear(tex) ? ColorSpace.Linear : ColorSpace.Gamma,
                    filterMode = tex.filterMode
                };
                if (host != null && host.Slot.hasTransform)
                {
                    slot.eligible = false;
                    slot.hasTransform = true;
                    slot.ineligibleReason = "animated texture inherits ST transform";
                }
                ctx.Uses.Add(new ATOTextureUse { Slot = slot, Renderer = ri });
                ctx.Log.Detail($"Anim texture swap '{tex.name}' on '{r.name}' {prop}");
            }
        }

        private static string ExtractMaterialProperty(string bindingName)
        {
            // Typical: "material._MainTex" / 常见形式。
            var idx = bindingName.LastIndexOf('.');
            if (idx >= 0 && idx < bindingName.Length - 1) return bindingName.Substring(idx + 1);
            return bindingName;
        }

        private static int GuessSubmeshFromBinding(EditorCurveBinding binding, int matCount)
        {
            var n = binding.propertyName;
            // m_Materials.Array.data[N]
            var open = n.LastIndexOf('[');
            var close = n.LastIndexOf(']');
            if (open >= 0 && close > open)
            {
                if (int.TryParse(n.Substring(open + 1, close - open - 1), out var i))
                    return Mathf.Clamp(i, 0, Math.Max(0, matCount - 1));
            }
            return 0;
        }

        private static ATORendererInfo Find(ATOContext ctx, Renderer r)
        {
            foreach (var ri in ctx.Renderers)
                if (ri.Renderer == r) return ri;
            return null;
        }

        private static Transform Resolve(Dictionary<string, Transform> map, string path)
        {
            if (path == null) path = "";
            return map.TryGetValue(path, out var t) ? t : null;
        }

        private static float CurveMax(AnimationCurve c)
        {
            var m = float.NegativeInfinity;
            foreach (var k in c.keys) if (k.value > m) m = k.value;
            return float.IsNegativeInfinity(m) ? 0f : m;
        }

        private static float CurveMaxAbs(AnimationCurve c)
        {
            var m = 0f;
            foreach (var k in c.keys) m = Mathf.Max(m, Mathf.Abs(k.value));
            return m;
        }

        private static float CurveExtremum(AnimationCurve c, bool max)
        {
            var m = max ? float.NegativeInfinity : float.PositiveInfinity;
            foreach (var k in c.keys)
            {
                if (max) { if (k.value > m) m = k.value; }
                else { if (k.value < m) m = k.value; }
            }
            return float.IsInfinity(m) ? 0f : m;
        }

        private static ATOAlphaMode MostStrict(ATOAlphaMode a, ATOAlphaMode b)
        {
            // Blend evaluates RMSE; Cutout evaluates IoU. Keep both by taking the "higher" enum as Blend > Cutout > Opaque
            // when both exist we store Blend and also keep cutoff (evaluator runs both if needed).
            // Blend 走 RMSE，Cutout 走 IoU。并存时记 Blend，cutoff 仍保留，评估器会两边都跑。
            return (ATOAlphaMode)Math.Max((int)a, (int)b);
        }
    }
}
