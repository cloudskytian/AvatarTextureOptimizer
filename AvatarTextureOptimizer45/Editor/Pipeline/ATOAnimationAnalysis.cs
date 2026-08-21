using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

namespace net.fosa.ato
{
    /// <summary>
    /// 动画分析 / Animation analysis.
    ///
    /// 遍历 Avatar 上所有 Animator(含子级)与 VRCAvatarDescriptor 的自定义动画层, 收集全部 AnimationClip,
    /// 并扫描所有与材质/贴图/渲染器/变换/形态键相关的曲线绑定:
    /// Collects every AnimationClip reachable from all Animators (incl. children) and the descriptor's custom
    /// layers, then scans all bindings that affect materials, textures, renderers, transforms and blendshapes:
    ///
    ///  * 材质槽切换 (m_Materials.Array.data[N] 的 PPtr 曲线) / material slot switches
    ///  * 材质贴图属性动画 (_MainTex 等 PPtr 曲线) / texture property animations
    ///  * ST 变换动画 / ST transform animations (whitelist trigger)
    ///  * Cutoff / 渲染模式 / 关键字动画 / cutoff / render-mode / keyword animations (strictest quality)
    ///  * 渲染器启用 (m_Enabled) / 物体启用 (m_IsActive) / renderer & object enable state
    ///  * 变换缩放 (m_LocalScale) — 面积计算 / scale — area computation
    ///  * 形态键 (blendShape.*) — 面积计算 / blendshapes — area computation
    ///
    /// 绑定以 (clip, binding) 原始记录保存, 由收集阶段按路径解析到具体对象
    /// (动画路径可能相对 Avatar 根或相对子 Animator 根).
    /// Bindings are kept as raw (clip, binding) records and resolved to objects by path during collection
    /// (paths may be relative to the avatar root or to a child Animator root).
    /// </summary>
    internal static class ATOAnimationAnalysis
    {
        private static readonly Regex SlotRegex = new Regex(@"^m_Materials\.Array\.data\[(\d+)\]$");
        private static readonly Regex StRegex = new Regex(@"^(.+)_ST\.[xyzw]$");
        private static readonly Regex BlendShapeRegex = new Regex(@"^blendShape\.");

        public static ATOAnimAnalysis Analyze(GameObject avatarRoot, VRCAvatarDescriptor descriptor)
        {
            var result = new ATOAnimAnalysis();

            // 1. 收集所有 Animator 控制器与 override / collect all animator controllers & overrides
            var controllers = new HashSet<RuntimeAnimatorController>();
            var overrideClips = new HashSet<AnimationClip>();

            foreach (var anim in avatarRoot.GetComponentsInChildren<Animator>(true))
            {
                if (anim.runtimeAnimatorController == null) continue;
                var rc = anim.runtimeAnimatorController;
                if (rc is AnimatorOverrideController aoc)
                {
                    CollectOverride(aoc, controllers, overrideClips);
                }
                else
                {
                    controllers.Add(rc);
                }
            }

            // 描述符上的自定义动画层 / custom animation layers on the descriptor
            if (descriptor != null)
            {
                foreach (var layer in descriptor.baseAnimationLayers)
                {
                    if (layer.animatorController == null) continue;
                    if (layer.animatorController is AnimatorOverrideController laoc)
                    {
                        CollectOverride(laoc, controllers, overrideClips);
                    }
                    else
                    {
                        controllers.Add(layer.animatorController);
                    }
                }
            }

            foreach (var c in controllers) CollectClipsFromController(c, result.clips);
            foreach (var c in overrideClips) result.clips.Add(c);

            // 2. 扫描每个 clip 的绑定 / scan bindings in each clip
            foreach (var clip in result.clips)
            {
                if (clip == null) continue;
                ScanClip(clip, result);
            }

            return result;
        }

        private static void CollectOverride(AnimatorOverrideController aoc, HashSet<RuntimeAnimatorController> controllers, HashSet<AnimationClip> overrideClips)
        {
            if (aoc.runtimeAnimatorController is AnimatorOverrideController nested)
            {
                CollectOverride(nested, controllers, overrideClips);
            }
            else if (aoc.runtimeAnimatorController != null)
            {
                controllers.Add(aoc.runtimeAnimatorController);
            }

            foreach (var pair in aoc.GetOverridesUnsafe())
            {
                if (pair.Value != null) overrideClips.Add(pair.Value);
            }
        }

        private static void CollectClipsFromController(RuntimeAnimatorController controller, List<AnimationClip> clips)
        {
            if (controller == null) return;
            foreach (var layer in controller.layers)
            {
                CollectFromStateMachine(layer.stateMachine, clips);
            }
        }

        private static void CollectFromStateMachine(AnimatorStateMachine sm, List<AnimationClip> clips)
        {
            foreach (var s in sm.states)
            {
                CollectMotion(s.state.motion, clips);
            }

            foreach (var child in sm.stateMachines) CollectFromStateMachine(child.stateMachine, clips);
        }

        private static void CollectMotion(Motion motion, List<AnimationClip> clips)
        {
            if (motion == null) return;
            if (motion is AnimationClip c)
            {
                clips.Add(c);
                return;
            }

            if (motion is BlendTree bt)
            {
                foreach (var child in bt.children) CollectMotion(child.motion, clips);
            }
        }

        private static void ScanClip(AnimationClip clip, ATOAnimAnalysis result)
        {
            foreach (var binding in AnimationUtility.GetCurveBindings(clip))
            {
                var type = binding.type;

                // 材质槽切换 / material slot switching
                if (binding.isPPtrCurve && typeof(Renderer).IsAssignableFrom(type))
                {
                    var m = SlotRegex.Match(binding.propertyName);
                    if (m.Success)
                    {
                        var curve = AnimationUtility.GetObjectReferenceCurve(clip, binding);
                        if (curve == null || curve.Length == 0) continue;
                        result.slotBindingRecords.Add(new ATOAnimRecord(clip, binding));
                        foreach (var key in curve)
                        {
                            if (key.value is Material mat) result.allMaterials.Add(mat);
                        }
                    }

                    continue;
                }

                // 材质贴图属性动画 / texture property animations
                if (binding.isPPtrCurve && type == typeof(Material))
                {
                    var curve = AnimationUtility.GetObjectReferenceCurve(clip, binding);
                    if (curve != null && curve.Length > 0)
                    {
                        result.texturePropRecords.Add(new ATOAnimRecord(clip, binding));
                        foreach (var key in curve)
                        {
                            if (key.value is Material mat) result.allMaterials.Add(mat);
                        }
                    }

                    continue;
                }

                // 材质数值属性动画 / material float-prop animations
                if (type == typeof(Material) || type == typeof(ShaderKeyword) || binding.propertyName == "m_SyncKeywords")
                {
                    var st = StRegex.Match(binding.propertyName);
                    if (st.Success)
                    {
                        result.stRecords.Add(new ATOAnimRecord(clip, binding));
                    }
                    else if (binding.propertyName.StartsWith("_Cutoff"))
                    {
                        result.cutoffRecords.Add(new ATOAnimRecord(clip, binding));
                    }
                    else
                    {
                        // 关键字/渲染模式/其他材质属性 -> 一律按最严苛处理 / keywords, render mode, other props -> strictest
                        result.renderModeRecords.Add(new ATOAnimRecord(clip, binding));
                    }

                    continue;
                }

                // 渲染器启用 / renderer enabled
                if (binding.propertyName == "m_Enabled" && typeof(Renderer).IsAssignableFrom(type))
                {
                    result.enabledRecords.Add(new ATOAnimRecord(clip, binding));
                    continue;
                }

                // 物体启用 / GameObject active
                if (binding.propertyName == "m_IsActive" && type == typeof(GameObject))
                {
                    result.activeRecords.Add(new ATOAnimRecord(clip, binding));
                    continue;
                }

                // 缩放 / scale
                if (type == typeof(Transform) && (binding.propertyName == "m_LocalScale.x"
                                                  || binding.propertyName == "m_LocalScale.y"
                                                  || binding.propertyName == "m_LocalScale.z"))
                {
                    result.scaleRecords.Add(new ATOAnimRecord(clip, binding));
                    continue;
                }

                // 形态键 / blendshapes
                if (typeof(SkinnedMeshRenderer).IsAssignableFrom(type) && BlendShapeRegex.IsMatch(binding.propertyName))
                {
                    result.blendShapeRecords.Add(new ATOAnimRecord(clip, binding));
                }
            }
        }
    }

    /// <summary>动画绑定原始记录 / Raw animation binding record.</summary>
    public readonly struct ATOAnimRecord
    {
        public readonly AnimationClip Clip;
        public readonly EditorCurveBinding Binding;

        public ATOAnimRecord(AnimationClip clip, EditorCurveBinding binding)
        {
            Clip = clip;
            Binding = binding;
        }
    }

    /// <summary>动画分析结果 / Animation analysis results.</summary>
    public sealed class ATOAnimAnalysis
    {
        public readonly List<AnimationClip> clips = new List<AnimationClip>();

        // 原始记录(收集阶段解析路径) / raw records (paths resolved during collection)
        public readonly List<ATOAnimRecord> slotBindingRecords = new List<ATOAnimRecord>();
        public readonly List<ATOAnimRecord> texturePropRecords = new List<ATOAnimRecord>();
        public readonly List<ATOAnimRecord> stRecords = new List<ATOAnimRecord>();
        public readonly List<ATOAnimRecord> cutoffRecords = new List<ATOAnimRecord>();
        public readonly List<ATOAnimRecord> renderModeRecords = new List<ATOAnimRecord>();
        public readonly List<ATOAnimRecord> enabledRecords = new List<ATOAnimRecord>();
        public readonly List<ATOAnimRecord> activeRecords = new List<ATOAnimRecord>();
        public readonly List<ATOAnimRecord> scaleRecords = new List<ATOAnimRecord>();
        public readonly List<ATOAnimRecord> blendShapeRecords = new List<ATOAnimRecord>();

        // 解析结果 / resolved results
        public readonly Dictionary<Renderer, Dictionary<int, List<ATOAnimRecord>>> slotBindings =
            new Dictionary<Renderer, Dictionary<int, List<ATOAnimRecord>>>();

        public readonly HashSet<Renderer> animatedRenderers = new HashSet<Renderer>();
        public readonly HashSet<Renderer> animatedEnabledRenderers = new HashSet<Renderer>();
        public readonly HashSet<GameObject> animatedActiveObjects = new HashSet<GameObject>();
        public readonly Dictionary<Transform, List<EditorCurveBinding>> scaleBindings =
            new Dictionary<Transform, List<EditorCurveBinding>>();

        public readonly Dictionary<Renderer, Dictionary<string, List<EditorCurveBinding>>> blendShapeBindings =
            new Dictionary<Renderer, Dictionary<string, List<EditorCurveBinding>>>();

        // 贴图属性动画: binding -> clip / texture property animations
        public readonly Dictionary<EditorCurveBinding, AnimationClip> texturePropBindings =
            new Dictionary<EditorCurveBinding, AnimationClip>();

        // ST 动画影响的 (renderer, propName) / ST-animated (renderer, propName)
        public readonly HashSet<(Renderer renderer, string prop)> stAnimatedProps = new HashSet<(Renderer, string)>();

        // Cutoff 动画: material -> 曲线值集合 / cutoff animations: material -> candidate cutoff values
        public readonly Dictionary<Material, List<float>> animatedCutoffs = new Dictionary<Material, List<float>>();

        // 渲染模式被动画修改的材质(按最严苛评估 alpha) / materials whose render mode is animated (strictest alpha evaluation)
        public readonly HashSet<Material> animatedRenderModeMaterials = new HashSet<Material>();

        // 收集到的全部材质(含动画切换出的) / all materials referenced (incl. via animation)
        public readonly HashSet<Material> allMaterials = new HashSet<Material>();

        /// <summary>绑定路径解析 / Resolve a binding path against the avatar root and all animator roots.</summary>
        public Transform ResolvePath(GameObject avatarRoot, string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            var root = avatarRoot.transform;
            var t = root.Find(path);
            if (t != null) return t;
            foreach (var anim in avatarRoot.GetComponentsInChildren<Animator>(true))
            {
                t = anim.transform.Find(path);
                if (t != null) return t;
            }

            return null;
        }
    }
}
