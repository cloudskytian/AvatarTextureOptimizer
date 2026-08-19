// Animation Analyzer - Analyzes animator controllers and clips for texture/material changes
// 动画分析器 - 分析动画控制器和片段以获取贴图/材质变化

using System.Collections.Generic;
using System.Linq;
using net.fosa.avatar_texture_optimizer.Editor.Core;
using UnityEngine;

#if NDMF_VRCSDK3_AVATARS
using VRC.SDK3.Avatars.Components;
using VRC.SDKBase;
#endif

namespace net.fosa.avatar_texture_optimizer.Editor.Analysis
{
    /// <summary>
    /// Analyzes all animation clips on the avatar to find material/texture changes,
    /// object enable/disable, scale changes, blend shape animations, etc.
    /// 分析Avatar上的所有动画片段，查找材质/贴图变化、对象启用/禁用、缩放变化、形态键动画等。
    /// </summary>
    public static class AnimationAnalyzer
    {
        public static AnimationAnalysisResult Analyze(GameObject root, ATOBuildContext atoCtx)
        {
            var result = new AnimationAnalysisResult
            {
                AnimationTextureOriginalMap = new Dictionary<Texture2D, Texture2D>()
            };
            var clips = CollectAllAnimationClips(root);

            ATOLog.Info($"Found {clips.Count} animation clips to analyze.");
            ATOLog.Info($"找到{clips.Count}个动画片段需要分析。");

            foreach (var clip in clips)
            {
                if (clip == null) continue;

                // Skip whitelisted clips
                if (atoCtx.WhitelistObjects.Contains(clip)) continue;

                AnalyzeClip(clip, root, result, atoCtx);
            }

            ATOLog.Info($"Animation analysis: {result.MaterialSwaps.Count} material swaps, " +
                        $"{result.TextureChanges.Count} texture changes, " +
                        $"{result.STTransformChanges.Count} ST transforms, " +
                        $"{result.RenderModeChanges.Count} render mode changes.");

            return result;
        }

        private static List<AnimationClip> CollectAllAnimationClips(GameObject root)
        {
            var clips = new HashSet<AnimationClip>();

#if NDMF_VRCSDK3_AVATARS
            var descriptor = root.GetComponent<VRCAvatarDescriptor>();
            if (descriptor != null)
            {
                // Collect from all animator layers
                if (descriptor.baseAnimationLayers != null)
                {
                    foreach (var layer in descriptor.baseAnimationLayers)
                    {
                        CollectClipsFromController(layer.animatorController, clips);
                    }
                }
                if (descriptor.specialAnimationLayers != null)
                {
                    foreach (var layer in descriptor.specialAnimationLayers)
                    {
                        CollectClipsFromController(layer.animatorController, clips);
                    }
                }
            }
#endif

            // Also collect from regular Animator components
            var animators = root.GetComponentsInChildren<Animator>(true);
            foreach (var animator in animators)
            {
                if (animator.runtimeAnimatorController != null)
                {
                    CollectClipsFromController(animator.runtimeAnimatorController, clips);
                }
            }

            return clips.ToList();
        }

        private static void CollectClipsFromController(RuntimeAnimatorController controller, HashSet<AnimationClip> clips)
        {
            if (controller == null) return;

            // Get all clips from the controller
            var allClips = controller.animationClips;
            if (allClips != null)
            {
                foreach (var clip in allClips)
                {
                    if (clip != null) clips.Add(clip);
                }
            }
        }

        private static void AnalyzeClip(AnimationClip clip, GameObject root,
            AnimationAnalysisResult result, ATOBuildContext atoCtx)
        {
            var bindings = AnimationUtility.GetCurveBindings(clip);

            foreach (var binding in bindings)
            {
                var curve = AnimationUtility.GetEditorCurve(clip, binding);
                if (curve == null) continue;

                string path = binding.path;
                string propName = binding.propertyName;
                System.Type type = binding.type;

                // Find the target object in the avatar hierarchy
                var targetObj = FindTargetObject(root, path, type);

                // Check for material property animations
                if (type == typeof(Material) || propName.StartsWith("material."))
                {
                    AnalyzeMaterialProperty(propName, targetObj, curve, result, clip, atoCtx);
                }
                // Check for GameObject enable/disable
                else if (type == typeof(GameObject) && propName == "m_IsActive")
                {
                    AnalyzeGameObjectToggle(targetObj, curve, result);
                }
                // Check for renderer enable/disable
                else if ((type == typeof(SkinnedMeshRenderer) || type == typeof(MeshRenderer))
                         && propName == "m_Enabled")
                {
                    AnalyzeRendererToggle(targetObj, curve, result);
                }
                // Check for transform scale
                else if (type == typeof(Transform) && propName.Contains("m_LocalScale"))
                {
                    AnalyzeTransformScale(targetObj, propName, curve, result);
                }
                // Check for blend shape animations
                else if (type == typeof(SkinnedMeshRenderer) && propName.StartsWith("blendShape."))
                {
                    // Track blend shape for area calculations
                }
            }

            // Also check object reference curves (for material swaps)
            var objBindings = AnimationUtility.GetObjectReferenceCurveBindings(clip);
            foreach (var binding in objBindings)
            {
                if (binding.propertyName.Contains("m_Materials") ||
                    binding.propertyName.Contains("material"))
                {
                    AnalyzeMaterialSwap(clip, binding, root, result, atoCtx);
                }
            }
        }

        private static void AnalyzeMaterialProperty(string propName, object targetObj,
            AnimationCurve curve, AnimationAnalysisResult result, AnimationClip clip, ATOBuildContext atoCtx)
        {
            // Clean up property name (remove "material." prefix if present)
            string cleanPropName = propName;
            if (cleanPropName.StartsWith("material."))
                cleanPropName = cleanPropName.Substring("material.".Length);

            // Get the material from the renderer
            Material mat = GetMaterialFromTarget(targetObj);
            if (mat == null) return;

            // Check if this is a texture property change
            if (cleanPropName.EndsWith(".x") || cleanPropName.EndsWith(".y") ||
                cleanPropName.EndsWith(".z") || cleanPropName.EndsWith(".w"))
            {
                // Could be ST transform or color
                string baseName = cleanPropName.Substring(0, cleanPropName.Length - 2);

                if (baseName.EndsWith("_ST") || baseName.EndsWith("_ScrollRotate"))
                {
                    // ST transform animation
                    string texPropName = baseName.Replace("_ST", "").Replace("_ScrollRotate", "");

                    var existing = result.STTransformChanges.FirstOrDefault(
                        s => s.Material == mat && s.PropertyName == texPropName);

                    if (existing == null)
                    {
                        existing = new STTransformChange
                        {
                            Material = mat,
                            PropertyName = texPropName
                        };
                        result.STTransformChanges.Add(existing);
                    }

                    // Check if the curve has any non-default values
                    var keys = curve.keys;
                    if (baseName.EndsWith("_ST"))
                    {
                        char axis = cleanPropName[cleanPropName.Length - 1];
                        bool hasChange = keys.Any(k =>
                            (axis == 'x' && !Approx(k.value, 1f)) ||
                            (axis == 'y' && !Approx(k.value, 1f)) ||
                            (axis == 'z' && !Approx(k.value, 0f)) ||
                            (axis == 'w' && !Approx(k.value, 0f)));
                        if (hasChange)
                        {
                            if (axis == 'x' || axis == 'y') existing.HasScaleChange = true;
                            if (axis == 'z' || axis == 'w') existing.HasOffsetChange = true;
                        }
                    }
                    else if (baseName.EndsWith("_ScrollRotate"))
                    {
                        bool hasChange = keys.Any(k => !Approx(k.value, 0f));
                        if (hasChange)
                        {
                            existing.HasOffsetChange = true;
                            existing.HasRotationChange = true;
                        }
                    }
                }
            }
            else
            {
                // Check for texture swap (object reference)
                // Check for render mode / cutoff changes
                if (cleanPropName == "_Cutoff")
                {
                    var keys = curve.keys;
                    var cutoffs = keys.Select(k => k.value).Distinct().ToList();

                    var existing = result.RenderModeChanges.FirstOrDefault(r => r.Material == mat);
                    if (existing == null)
                    {
                        existing = new RenderModeChange { Material = mat };
                        result.RenderModeChanges.Add(existing);
                    }
                    existing.PossibleCutoffs.AddRange(cutoffs);
                }
                else if (cleanPropName == "_TransparentMode" || cleanPropName == "_Mode")
                {
                    var keys = curve.keys;
                    var modes = keys.Select(k => (int)k.value).Distinct().ToList();

                    var existing = result.RenderModeChanges.FirstOrDefault(r => r.Material == mat);
                    if (existing == null)
                    {
                        existing = new RenderModeChange { Material = mat };
                        result.RenderModeChanges.Add(existing);
                    }
                    foreach (var mode in modes)
                    {
                        TransparencyMode tm;
                        switch (mode)
                        {
                            case 0: tm = TransparencyMode.Opaque; break;
                            case 1: tm = TransparencyMode.Cutout; break;
                            case 2: tm = TransparencyMode.Blend; break;
                            case 3: tm = TransparencyMode.Premultiply; break;
                            default: tm = TransparencyMode.Opaque; break;
                        }
                        if (!existing.PossibleModes.Contains(tm))
                            existing.PossibleModes.Add(tm);
                    }
                }
            }
        }

        private static void AnalyzeGameObjectToggle(object targetObj, AnimationCurve curve,
            AnimationAnalysisResult result)
        {
            var go = targetObj as GameObject;
            if (go == null) return;

            var keys = curve.keys;
            bool canBeDisabled = keys.Any(k => k.value < 0.5f);

            if (canBeDisabled)
            {
                result.CanBeDisabled.Add(go);
            }
        }

        private static void AnalyzeRendererToggle(object targetObj, AnimationCurve curve,
            AnimationAnalysisResult result)
        {
            var renderer = targetObj as Renderer;
            if (renderer == null) return;

            var keys = curve.keys;
            bool canBeDisabled = keys.Any(k => k.value < 0.5f);

            if (canBeDisabled)
            {
                result.CanBeDisabled.Add(renderer.gameObject);
            }
        }

        private static void AnalyzeTransformScale(object targetObj, string propName,
            AnimationCurve curve, AnimationAnalysisResult result)
        {
            var transform = targetObj as Transform;
            if (transform == null) return;

            var keys = curve.keys;
            float maxVal = keys.Max(k => Mathf.Abs(k.value));

            if (result.MaxScales.ContainsKey(transform))
            {
                result.MaxScales[transform] = Mathf.Max(result.MaxScales[transform], maxVal);
            }
            else
            {
                result.MaxScales[transform] = maxVal;
            }
        }

        private static void AnalyzeMaterialSwap(AnimationClip clip, EditorCurveBinding binding,
            GameObject root, AnimationAnalysisResult result, ATOBuildContext atoCtx)
        {
            var keyframes = AnimationUtility.GetObjectReferenceCurve(clip, binding);
            if (keyframes == null || keyframes.Length == 0) return;

            // Get the original renderer
            var path = binding.path;
            var targetObj = root.transform.Find(path);
            if (targetObj == null) return;

            var renderer = targetObj.GetComponent<Renderer>();
            if (renderer == null) return;

            // Determine material slot index from property name
            int slotIndex = 0;
            string propName = binding.propertyName;
            if (propName.Contains("["))
            {
                int start = propName.IndexOf('[') + 1;
                int end = propName.IndexOf(']');
                if (start > 0 && end > start)
                {
                    int.TryParse(propName.Substring(start, end - start), out slotIndex);
                }
            }

            var originalMat = renderer.sharedMaterials != null && slotIndex < renderer.sharedMaterials.Length
                ? renderer.sharedMaterials[slotIndex]
                : null;

            var swappedMats = new List<Material>();
            foreach (var kf in keyframes)
            {
                var mat = kf.value as Material;
                if (mat != null && !swappedMats.Contains(mat))
                {
                    swappedMats.Add(mat);
                }
            }

            if (swappedMats.Count > 0)
            {
                result.MaterialSwaps.Add(new MaterialSwapInfo
                {
                    Renderer = renderer,
                    MaterialSlot = slotIndex,
                    OriginalMaterial = originalMat,
                    SwappedMaterials = swappedMats
                });

                // Also analyze the swapped materials' textures
                // 同时分析切换材质的贴图
                foreach (var swapMat in swappedMats)
                {
                    if (swapMat == null) continue;
                    if (!atoCtx.ShaderAnalysisResults.ContainsKey(swapMat))
                    {
                        var shaderResult = ShaderAnalyzer.Analyze(swapMat, atoCtx);
                        atoCtx.ShaderAnalysisResults[swapMat] = shaderResult;
                    }

                    // Build animation texture → original texture mapping
                    // for type group merging (动画切换贴图并入原贴图所在组)
                    if (originalMat != null && originalMat.shader != null)
                    {
                        var shader = originalMat.shader;
                        int pc = shader.GetPropertyCount();
                        for (int pi = 0; pi < pc; pi++)
                        {
                            if (shader.GetPropertyType(pi) != UnityEngine.Rendering.ShaderPropertyType.Texture)
                                continue;
                            var pn = shader.GetPropertyName(pi);
                            var origTex = originalMat.GetTexture(pn) as Texture2D;
                            var swapTex = swapMat.GetTexture(pn) as Texture2D;
                            if (origTex != null && swapTex != null && origTex != swapTex)
                            {
                                result.AnimationTextureOriginalMap[swapTex] = origTex;
                            }
                        }
                    }
                }
            }
        }

        private static object FindTargetObject(GameObject root, string path, System.Type type)
        {
            if (string.IsNullOrEmpty(path))
                return root;

            var transform = root.transform.Find(path);
            if (transform == null) return null;

            if (type == typeof(GameObject))
                return transform.gameObject;
            if (type == typeof(Transform))
                return transform;

            var component = transform.GetComponent(type);
            return (object)component ?? transform.gameObject;
        }

        private static Material GetMaterialFromTarget(object target)
        {
            if (target is Renderer renderer)
            {
                return renderer.sharedMaterials?.FirstOrDefault();
            }
            if (target is Material mat)
            {
                return mat;
            }
            return null;
        }

        private static bool Approx(float a, float b, float eps = 0.001f) => Mathf.Abs(a - b) < eps;
    }
}
