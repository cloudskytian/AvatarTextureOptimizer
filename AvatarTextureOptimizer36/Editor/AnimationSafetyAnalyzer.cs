using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// Finds animation-driven states that can invalidate a texture/UV rewrite. / 查找会使纹理/UV 改写失效的动画状态。
    /// </summary>
    internal sealed class AnimationSafetyAnalyzer
    {
        private readonly Dictionary<Renderer, RendererAnimationInfo> _byRenderer = new Dictionary<Renderer, RendererAnimationInfo>();
        private readonly List<Renderer> _allRenderers = new List<Renderer>();

        private AnimationSafetyAnalyzer()
        {
        }

        public static AnimationSafetyAnalyzer Analyze(GameObject root, AvatarTextureOptimizer component, ATOLogger logger)
        {
            AnimationSafetyAnalyzer result = new AnimationSafetyAnalyzer();
            Renderer[] renderers = root.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < renderers.Length; i++)
            {
                if (renderers[i] != null) result._allRenderers.Add(renderers[i]);
            }

            HashSet<AnimationClip> clips = CollectClips(root);
            if (!component.scanAnimationReferences && clips.Count > 0)
            {
                result.MarkAllTextureTransforms();
                logger.Warning("Animation scanning is disabled while animation clips exist; all affected texture optimization falls back safely. / 存在动画但关闭了动画扫描，相关纹理优化全部安全回退。");
                return result;
            }
            foreach (AnimationClip clip in clips)
            {
                if (clip == null || component.IsWhitelisted(clip)) continue;
                result.ScanClip(root.transform, clip, logger);
            }

            return result;
        }

        public RendererAnimationInfo ForRenderer(Renderer renderer, Transform root)
        {
            RendererAnimationInfo info;
            if (_byRenderer.TryGetValue(renderer, out info)) return info;
            return null;
        }

        private void ScanClip(Transform root, AnimationClip clip, ATOLogger logger)
        {
            EditorCurveBinding[] bindings;
            try
            {
                bindings = AnimationUtility.GetCurveBindings(clip);
            }
            catch (Exception exception)
            {
                logger.Warning("Could not read curves from animation '" + clip.name + "'; all texture optimization depending on it is skipped. / 无法读取动画曲线，相关纹理优化已跳过。 " + exception.Message);
                MarkAllTextureTransforms();
                return;
            }

            for (int i = 0; i < bindings.Length; i++)
            {
                EditorCurveBinding binding = bindings[i];
                Renderer renderer = FindRenderer(root, binding.path);
                string property = binding.propertyName ?? string.Empty;
                string lower = property.ToLowerInvariant();
                if (renderer != null)
                {
                    RendererAnimationInfo info = GetOrCreate(renderer);
                    info.SourceClips.Add(clip.name);
                    if (lower.Contains("isenabled") || lower.Contains("m_isactive") || lower.Contains("active"))
                        info.HasAnimatedEnable = true;
                    if (IsTextureTransformProperty(lower)) info.HasAnimatedTextureTransform = true;
                    if (lower.Contains("m_localscale.x")) info.MaxScaleX = MaxCurveValue(binding, clip, info.MaxScaleX);
                    if (lower.Contains("m_localscale.y")) info.MaxScaleY = MaxCurveValue(binding, clip, info.MaxScaleY);
                    if (lower.Contains("m_localscale.z")) info.MaxScaleZ = MaxCurveValue(binding, clip, info.MaxScaleZ);
                    if (lower.Contains("material") || lower.Contains("m_materials") || lower.Contains("m_texture"))
                        info.HasAnimatedMaterialSwitch = true;
                }
                else if (IsTextureTransformProperty(lower) || lower.Contains("material") || lower.Contains("m_texture"))
                {
                    MarkAllTextureTransforms();
                }
            }

            EditorCurveBinding[] objectBindings;
            try
            {
                objectBindings = AnimationUtility.GetObjectReferenceCurveBindings(clip);
            }
            catch (Exception exception)
            {
                logger.Warning("Could not read object-reference curves from animation '" + clip.name + "'; safe fallback enabled. / 无法读取对象引用曲线，已启用安全回退。 " + exception.Message);
                MarkAllTextureTransforms();
                return;
            }

            for (int i = 0; i < objectBindings.Length; i++)
            {
                EditorCurveBinding binding = objectBindings[i];
                Renderer renderer = FindRenderer(root, binding.path);
                string property = (binding.propertyName ?? string.Empty).ToLowerInvariant();
                if (renderer == null)
                {
                    if (property.Contains("material") || property.Contains("texture")) MarkAllTextureTransforms();
                    continue;
                }

                RendererAnimationInfo info = GetOrCreate(renderer);
                info.SourceClips.Add(clip.name);
                if (property.Contains("material"))
                {
                    info.HasAnimatedMaterialSwitch = true;
                    ObjectReferenceKeyframe[] keys = AnimationUtility.GetObjectReferenceCurve(clip, binding);
                    if (keys != null)
                    {
                        for (int keyIndex = 0; keyIndex < keys.Length; keyIndex++)
                        {
                            Material material = keys[keyIndex].value as Material;
                            if (material != null && !info.MaterialVariants.Contains(material)) info.MaterialVariants.Add(material);
                        }
                    }
                }
                if (property.Contains("texture")) info.HasAnimatedTextureTransform = true;
            }
        }

        private static HashSet<AnimationClip> CollectClips(GameObject root)
        {
            HashSet<AnimationClip> clips = new HashSet<AnimationClip>();
            UnityEngine.Object[] dependencies = EditorUtility.CollectDependencies(new UnityEngine.Object[] { root });
            for (int i = 0; i < dependencies.Length; i++)
            {
                AnimationClip clip = dependencies[i] as AnimationClip;
                if (clip != null) clips.Add(clip);
            }

            Animator[] animators = root.GetComponentsInChildren<Animator>(true);
            for (int i = 0; i < animators.Length; i++)
            {
                RuntimeAnimatorController controller = animators[i].runtimeAnimatorController;
                if (controller == null) continue;
                AnimationClip[] controllerClips = controller.animationClips;
                for (int j = 0; j < controllerClips.Length; j++) if (controllerClips[j] != null) clips.Add(controllerClips[j]);
            }

            Animation[] legacyAnimations = root.GetComponentsInChildren<Animation>(true);
            for (int i = 0; i < legacyAnimations.Length; i++)
            {
                foreach (AnimationState state in legacyAnimations[i])
                    if (state != null && state.clip != null) clips.Add(state.clip);
            }
            return clips;
        }

        private Renderer FindRenderer(Transform root, string path)
        {
            Transform target = string.IsNullOrEmpty(path) ? root : root.Find(path);
            return target == null ? null : target.GetComponent<Renderer>();
        }

        private RendererAnimationInfo GetOrCreate(Renderer renderer)
        {
            RendererAnimationInfo info;
            if (!_byRenderer.TryGetValue(renderer, out info))
            {
                info = new RendererAnimationInfo();
                _byRenderer.Add(renderer, info);
            }
            return info;
        }

        private void MarkAllTextureTransforms()
        {
            for (int i = 0; i < _allRenderers.Count; i++) GetOrCreate(_allRenderers[i]).HasAnimatedTextureTransform = true;
        }

        private static bool IsTextureTransformProperty(string lower)
        {
            return lower.Contains("_st") || lower.Contains("tiling") || lower.Contains("offset") ||
                   lower.Contains("m_scale") || lower.Contains("m_offset") || lower.Contains("texenv") ||
                   lower.Contains("texturetransform");
        }

        private static float MaxCurveValue(EditorCurveBinding binding, AnimationClip clip, float fallback)
        {
            AnimationCurve curve = AnimationUtility.GetEditorCurve(clip, binding);
            if (curve == null || curve.length == 0) return fallback;
            float maximum = fallback;
            for (int i = 0; i < curve.length; i++) maximum = Mathf.Max(maximum, Mathf.Abs(curve.keys[i].value));
            return maximum;
        }

        public static bool AllVariantsShareTextures(RendererAnimationInfo info, MaterialUse use)
        {
            if (info == null || info.MaterialVariants.Count == 0 || use == null) return false;
            for (int variantIndex = 0; variantIndex < info.MaterialVariants.Count; variantIndex++)
            {
                Material variant = info.MaterialVariants[variantIndex];
                if (variant == null) return false;
                for (int referenceIndex = 0; referenceIndex < use.References.Count; referenceIndex++)
                {
                    TextureReference reference = use.References[referenceIndex];
                    if (reference == null || string.IsNullOrEmpty(reference.PropertyName)) return false;
                    if (variant.GetTexture(reference.PropertyName) != reference.Texture.Source) return false;
                }
            }
            return true;
        }
    }
}
