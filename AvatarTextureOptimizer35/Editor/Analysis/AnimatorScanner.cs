using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace net.fosa.avatar_texture_optimizer.editor
{
    /// <summary>
    /// Animation & animator analysis. / 动画与 Animator 分析。
    ///
    /// Self-implemented parser (modeled after AAO's AnimatorParserV2 and MA's animation database,
    /// both read from source before writing this). Collects all reachable clips and analyzes curves
    /// for: material/texture swaps, ST animation, renderer/object enable, object scale, cutout
    /// animation. Conservative: any value seen in any state counts as "possible". /
    /// 自实现解析器（参照 AAO AnimatorParserV2 与 MA 动画数据库的设计，两者源码已先读后写）。
    /// 收集全部可达剪辑并分析曲线：材质/贴图切换、ST 动画、渲染器/物体启用、物体缩放、Cutout 动画。
    /// 保守策略：任何状态中出现的任何值都计为“可能”。
    /// </summary>
    internal static class AnimatorScanner
    {
        /// <summary>
        /// Collect all clips and parse all curves into ctx.Animations. / 收集全部剪辑并把曲线解析进 ctx.Animations。
        /// </summary>
        public static void Collect(AtoContext ctx)
        {
            var info = ctx.Animations;

            // ---- 1. controllers: VRC descriptor layers + all Animator components ----
            var controllers = new HashSet<RuntimeAnimatorController>();
            foreach (var entry in AtoVrcSdkIntegration.GetAvatarAnimatorControllers(ctx.AvatarRoot.transform))
            {
                if (entry.Controller != null) controllers.Add(entry.Controller);
            }
            foreach (var animator in ctx.AvatarRoot.GetComponentsInChildren<Animator>(true))
            {
                if (animator.runtimeAnimatorController != null)
                    controllers.Add(animator.runtimeAnimatorController);
            }

            // ---- 2. clips from controllers (animationClips accounts for override controllers) ----
            var clips = new HashSet<AnimationClip>();
            foreach (var controller in controllers)
            {
                foreach (var clip in controller.animationClips)
                {
                    if (clip != null) clips.Add(clip);
                }
            }

            // ---- 3. clips from legacy Animation components ----
            foreach (var animation in ctx.AvatarRoot.GetComponentsInChildren<Animation>(true))
            {
                foreach (var clip in AnimationUtility.GetAnimationClips(animation))
                {
                    if (clip != null) clips.Add(clip);
                }
            }

            info.Clips = clips.ToList();
            AtoLog.Info($"[ATO] animations: {info.Clips.Count} clip(s) from {controllers.Count} controller(s).");

            // ---- 4. parse curves ----
            var clipIndex = 0;
            foreach (var clip in info.Clips)
            {
                ctx.State.SetProgress($"parsing {clip.name}",
                    (float)clipIndex / Mathf.Max(1, info.Clips.Count));
                ParseFloatCurves(ctx, clip);
                ParseObjectCurves(ctx, clip);
                clipIndex++;
            }
        }

        // ------------------------------------------------------------------
        // float curves
        // ------------------------------------------------------------------

        private static void ParseFloatCurves(AtoContext ctx, AnimationClip clip)
        {
            var info = ctx.Animations;
            foreach (var binding in AnimationUtility.GetCurveBindings(clip))
            {
                var curve = AnimationUtility.GetEditorCurve(clip, binding);
                if (curve == null || curve.keys.Length == 0) continue;

                var prop = binding.propertyName;

                // GameObject active state. / 物体激活状态。
                if (binding.type == typeof(GameObject) && prop == "m_IsActive")
                {
                    var go = ResolveObject(ctx, binding, clip) as GameObject;
                    if (go != null) info.AnimatedActive.Add(go);
                    continue;
                }

                // Renderer (or Behaviour) enabled state. / 渲染器（或 Behaviour）启用状态。
                if (prop == "m_Enabled" && IsRendererType(binding.type))
                {
                    if (ResolveObject(ctx, binding, clip) is Renderer renderer)
                        info.AnimatedEnabled.Add(renderer);
                    continue;
                }

                // Transform local scale (per axis). / Transform 局部缩放（逐轴）。
                if (binding.type == typeof(Transform) && prop.StartsWith("m_LocalScale."))
                {
                    var go = ResolveObject(ctx, binding, clip) as GameObject;
                    if (go == null) continue;
                    var axis = prop[prop.Length - 1] - 'x'; // 0..2
                    if (axis is < 0 or > 2) continue;
                    var max = 0f;
                    foreach (var key in curve.keys) max = Mathf.Max(max, Mathf.Abs(key.value));
                    if (!info.MaxLocalScale.TryGetValue(go, out var scale)) scale = Vector3.one;
                    scale[axis] = Mathf.Max(scale[axis], max);
                    info.MaxLocalScale[go] = scale;
                    info.AnimatedScaleObjects.Add(go);
                    continue;
                }

                // Material property curves (direct material asset animation). / 材质属性曲线（直接动画材质资产）。
                if (binding.type == typeof(Material))
                {
                    var material = ResolveObject(ctx, binding, clip) as Material;
                    if (material != null) info.DirectAnimatedMaterials.Add(material);
                    HandleMaterialFloatCurve(ctx, material, prop, curve);
                    continue;
                }

                // Renderer-material property curves ("material._X" / "m_Materials.Array.data[i]._X"). /
                // 渲染器材质属性曲线。
                if (IsRendererType(binding.type))
                {
                    var (material, slotIndex) = ResolveRendererMaterial(ctx, binding, clip, prop);
                    if (material != null)
                    {
                        if (slotIndex >= 0 && ResolveObject(ctx, binding, clip) is Renderer slotRenderer)
                        {
                            info.AnimatedSlotProperties.Add((slotRenderer, slotIndex));
                        }
                        HandleMaterialFloatCurve(ctx, material, ExtractSubProperty(prop), curve);
                    }
                }
            }
        }

        private static void HandleMaterialFloatCurve(AtoContext ctx, Material material, string prop, AnimationCurve curve)
        {
            if (material == null) return;
            var info = ctx.Animations;

            // ST scale/offset animation → the texture is treated as whitelist. / ST 动画 → 贴图视作白名单。
            if (prop.EndsWith("_ST.x") || prop.EndsWith("_ST.y") || prop.EndsWith("_ST.z") || prop.EndsWith("_ST.w"))
            {
                var baseProp = prop.Substring(0, prop.Length - 2); // "_MainTex_ST.x" → "_MainTex_ST"
                var textureProp = baseProp.Substring(0, baseProp.Length - 3); // → "_MainTex"
                info.AnimatedSt.Add((material, textureProp));
                return;
            }

            // Cutoff / render-mode related: track all animated values (worst wins later). /
            // Cutoff/渲染模式相关：记录全部动画值（后续取最严）。
            if (prop == "_Cutoff")
            {
                info.AnimatesRenderingMode = true;
                if (!info.AnimatedCutoffs.TryGetValue(material, out var values))
                    info.AnimatedCutoffs[material] = values = new List<float>();
                foreach (var key in curve.keys)
                {
                    var v = key.value;
                    if (!values.Contains(v)) values.Add(v);
                }
                return;
            }

            // Keyword-like animation (conservative: render mode may change). / 关键字类动画（保守：渲染模式可能变化）。
            if (prop.Contains("m_ShaderKeywords") || prop.Contains("_ON") || prop.Contains("_OFF"))
            {
                info.AnimatesRenderingMode = true;
                info.AnimatedKeywords.Add((material, prop));
                return;
            }

            // Any other animated property (merge-safety). / 其他动画属性（合并安全判定）。
            info.AnimatedProperties.Add((material, prop));
        }

        // ------------------------------------------------------------------
        // object reference curves
        // ------------------------------------------------------------------

        private static void ParseObjectCurves(AtoContext ctx, AnimationClip clip)
        {
            var info = ctx.Animations;
            foreach (var binding in AnimationUtility.GetObjectReferenceCurveBindings(clip))
            {
                var refs = AnimationUtility.GetObjectReferenceCurve(clip, binding);
                if (refs == null || refs.Length == 0) continue;

                var prop = binding.propertyName;

                // Renderer material slot swaps: m_Materials.Array.data[i]. / 渲染器材质槽切换。
                if (IsRendererType(binding.type) && TryParseSlotIndex(prop, out var slotIndex))
                {
                    if (ResolveObject(ctx, binding, clip) is Renderer renderer)
                    {
                        var options = info.SlotMaterialOptions.TryGetValue((renderer, slotIndex), out var list)
                            ? list
                            : info.SlotMaterialOptions[(renderer, slotIndex)] = new List<Material>();
                        foreach (var reference in refs)
                        {
                            if (reference.value is Material material && !options.Contains(material))
                                options.Add(material);
                        }
                    }
                    continue;
                }

                // Texture swaps on material properties (direct material binding). / 材质属性上的贴图切换（直接材质绑定）。
                if (binding.type == typeof(Material) && refs.Any(r => r.value is Texture2D))
                {
                    var material = ResolveObject(ctx, binding, clip) as Material;
                    if (material != null) info.DirectAnimatedMaterials.Add(material);
                    if (material == null)
                    {
                        ctx.Warn($"[ATO] unresolved material binding in '{clip.name}' ({prop}); " +
                                 "texture swaps cannot be tracked — matching textures are treated as whitelist.");
                        // Conservative: the ST/swap may target any material → flag globally. /
                        // 保守：可能指向任意材质 → 全局标记。
                        info.HasUnresolvedMaterialBinding = true;
                        continue;
                    }
                    AddTextureSwaps(info, material, prop, refs);
                    continue;
                }

                // Renderer-material texture swaps ("material._MainTex" etc.). / 渲染器材质贴图切换。
                if (IsRendererType(binding.type) && refs.Any(r => r.value is Texture2D))
                {
                    var (material, _) = ResolveRendererMaterial(ctx, binding, clip, prop);
                    if (material != null) AddTextureSwaps(info, material, ExtractSubProperty(prop), refs);
                }
            }
        }

        private static void AddTextureSwaps(AtoAnimationInfo info, Material material, string prop,
            ObjectReferenceKeyframe[] refs)
        {
            var key = (material, prop);
            if (!info.TextureSwaps.TryGetValue(key, out var list))
                info.TextureSwaps[key] = list = new List<Texture2D>();
            foreach (var reference in refs)
            {
                if (reference.value is Texture2D texture && !list.Contains(texture))
                    list.Add(texture);
            }
        }

        // ------------------------------------------------------------------
        // resolution helpers
        // ------------------------------------------------------------------

        /// <summary>
        /// Resolve the animated object for a binding: official API first (handles material PPtrs),
        /// then path resolution fallback. / 解析绑定指向的对象：优先官方 API（处理材质 PPtr），再按路径回退。
        /// </summary>
        private static UnityEngine.Object ResolveObject(AtoContext ctx, EditorCurveBinding binding, AnimationClip clip)
        {
            if (binding.type == typeof(UnityEngine.Object)) return null; // broken tools. / 损坏的工具产物。

            try
            {
                var obj = AnimationUtility.GetAnimatedObject(ctx.AvatarRoot, binding);
                if (obj != null) return obj;
            }
            catch (Exception)
            {
                // fall through to path resolution. / 继续走路径解析。
            }

            // Path resolution: avatar root first, then animator roots (slash-safe). / 路径解析：先 Avatar 根，再 Animator 根（兼容斜杠名）。
            Transform go = ResolveAnyPath(ctx, binding.path);
            if (go == null) return null;
            if (binding.type == typeof(GameObject)) return go.gameObject;
            return go.GetComponent(binding.type);
        }

        private static Transform ResolveAnyPath(AtoContext ctx, string path)
        {
            var t = ResolveAnimationPath(ctx.AvatarRoot.transform, path);
            if (t != null) return t;
            foreach (var animator in ctx.AvatarRoot.GetComponentsInChildren<Animator>(true))
            {
                t = ResolveAnimationPath(animator.transform, path);
                if (t != null) return t;
            }
            foreach (var animation in ctx.AvatarRoot.GetComponentsInChildren<Animation>(true))
            {
                t = ResolveAnimationPath(animation.transform, path);
                if (t != null) return t;
            }
            return null;
        }

        /// <summary>
        /// Resolve an animation path that may contain slashes in object names. / 解析可能含斜杠名的动画路径。
        /// </summary>
        private static Transform ResolveAnimationPath(Transform root, string path)
        {
            if (string.IsNullOrEmpty(path)) return root;
            foreach (Transform child in root)
            {
                var name = child.name;
                if (name == path) return child;
                if (path.StartsWith(name + "/", StringComparison.Ordinal))
                {
                    var rest = path.Substring(name.Length + 1);
                    var found = ResolveAnimationPath(child, rest);
                    if (found != null) return found;
                }
            }
            return null;
        }

        /// <summary>
        /// Resolve a renderer-material binding: returns the material for the target slot. /
        /// 解析渲染器材质绑定：返回目标槽的材质。
        /// </summary>
        private static (Material, int) ResolveRendererMaterial(AtoContext ctx, EditorCurveBinding binding,
            AnimationClip clip, string prop)
        {
            if (!(ResolveObject(ctx, binding, clip) is Renderer renderer)) return (null, -1);

            // "material._X" → slot 0; "m_Materials.Array.data[i]._X" → slot i. /
            if (prop.StartsWith("material.", StringComparison.Ordinal))
            {
                var shared = renderer.sharedMaterials;
                return shared.Length > 0 ? (shared[0], 0) : (null, -1);
            }
            if (TryParseSlotIndex(prop, out var slotIndex))
            {
                var shared = renderer.sharedMaterials;
                return slotIndex < shared.Length ? (shared[slotIndex], slotIndex) : (null, -1);
            }
            return (null, -1);
        }

        private static string ExtractSubProperty(string prop)
        {
            // "material._Color" → "_Color"; "m_Materials.Array.data[0]._Color" → "_Color". /
            if (prop.StartsWith("material.", StringComparison.Ordinal))
                return prop.Substring("material.".Length);
            var dot = prop.IndexOf(".", StringComparison.Ordinal);
            return dot >= 0 ? prop.Substring(dot + 1) : prop;
        }

        private static bool TryParseSlotIndex(string prop, out int slotIndex)
        {
            slotIndex = -1;
            // m_Materials.Array.data[0]
            const string prefix = "m_Materials.Array.data[";
            if (!prop.StartsWith(prefix, StringComparison.Ordinal)) return false;
            var close = prop.IndexOf(']', prefix.Length);
            if (close < 0) return false;
            var num = prop.Substring(prefix.Length, close - prefix.Length);
            return int.TryParse(num, out slotIndex);
        }

        private static bool IsRendererType(Type type) =>
            type == typeof(SkinnedMeshRenderer) || type == typeof(MeshRenderer);
    }
}
