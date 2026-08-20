// AvatarScanner.cs
// Phase 1: Scans the avatar for all renderers, material slots, textures,
// animations, and whitelist objects. Determines which textures are eligible
// for optimization based on the strict criteria.
// 阶段1：扫描 Avatar 的所有渲染器、材质槽、贴图、动画和白名单对象。
//
// Copyright (c) 2024 fosa. Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Animations;
using UnityEditor;
using UnityEditor.Animations;

namespace Fosa.AvatarTextureOptimizer.Core
{
    /// <summary>
    /// Scans the avatar hierarchy to build a complete picture of what needs optimizing.
    /// 扫描 Avatar 层级以建立需要优化的完整图景。
    /// </summary>
    internal sealed class AvatarScanner
    {
        private readonly GameObject _avatarRoot;
        private readonly ATOComponent _component;
        private readonly AdvancedSettings _settings;
        private readonly ATOLogger _log;
        private readonly AvatarScanResult _result;

        internal AvatarScanner(GameObject avatarRoot, ATOComponent component, AdvancedSettings settings, ATOLogger log)
        {
            _avatarRoot = avatarRoot;
            _component = component;
            _settings = settings;
            _log = log;
            _result = new AvatarScanResult();
        }

        internal AvatarScanResult Scan()
        {
            // Build whitelist set
            BuildWhitelist();

            // Scan all renderers
            ScanRenderers();

            // Scan animations for material/texture swaps and renderer toggles
            ScanAnimations();

            // Finalize texture references
            FinalizeTextureReferences();

            _log.Info($"Scan complete: {_result.Renderers.Count} renderers, {_result.MaterialSlots.Count} material slots, {_result.TextureReferences.Count} unique textures.");
            _log.Verbose($"Whitelisted objects: {_result.WhitelistedObjects.Count}, Whitelisted textures: {_result.WhitelistedTextures.Count}");

            foreach (var w in _result.Warnings)
                _log.Warning(w);

            return _result;
        }

        private void BuildWhitelist()
        {
            foreach (var obj in _component._whitelist)
            {
                if (obj == null) continue;
                _result.WhitelistedObjects.Add(obj);

                // If it's a texture, whitelist directly
                if (obj is Texture2D tex)
                {
                    _result.WhitelistedTextures.Add(tex);
                    _log.Verbose($"Whitelisted texture: {tex.name}");
                }

                // If it's a material, whitelist all its textures
                if (obj is Material mat)
                {
                    foreach (var t in GetMaterialTextures(mat))
                    {
                        _result.WhitelistedTextures.Add(t);
                        _log.Verbose($"Whitelisted texture (via material {mat.name}): {t.name}");
                    }
                }

                // If it's a GameObject or Component, whitelist textures on its renderers
                var go = obj as GameObject ?? (obj as Component)?.gameObject;
                if (go != null)
                {
                    foreach (var r in go.GetComponentsInChildren<Renderer>(true))
                    {
                        _result.WhitelistedObjects.Add(r);
                        foreach (var mat in r.sharedMaterials)
                        {
                            if (mat == null) continue;
                            foreach (var t in GetMaterialTextures(mat))
                                _result.WhitelistedTextures.Add(t);
                        }
                    }
                }
            }
        }

        private void ScanRenderers()
        {
            var renderers = _avatarRoot.GetComponentsInChildren<Renderer>(true);

            foreach (var renderer in renderers)
            {
                // Skip EditorOnly
                if (renderer.CompareTag("EditorOnly")) continue;

                // Only handle SkinnedMeshRenderer and MeshRenderer
                if (!(renderer is SkinnedMeshRenderer || renderer is MeshRenderer)) continue;

                _result.Renderers.Add(renderer);

                var materials = renderer.sharedMaterials;
                for (int slot = 0; slot < materials.Length; slot++)
                {
                    var mat = materials[slot];
                    if (mat == null) continue;

                    var slotInfo = new MaterialSlotInfo
                    {
                        Renderer = renderer,
                        SlotIndex = slot,
                        CurrentMaterial = mat
                    };

                    // Check if renderer is enabled or animated
                    slotInfo.IsEnabled = renderer.gameObject.activeInHierarchy || renderer.enabled;

                    // Collect textures from this material
                    int propCount = ShaderUtil.GetPropertyCount(mat.shader);
                    for (int p = 0; p < propCount; p++)
                    {
                        if (ShaderUtil.GetPropertyType(mat.shader, p) != ShaderUtil.ShaderPropertyType.TexEnv) continue;
                        var propName = ShaderUtil.GetPropertyName(mat.shader, p);
                        var tex = mat.GetTexture(propName);
                        if (tex is Texture2D t2d)
                        {
                            slotInfo.AllReferencedTextures.Add(t2d);
                            RegisterTextureReference(t2d, mat, renderer, slot, propName);
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Registers a texture reference if it passes all eligibility criteria.
        /// If any criterion fails, the texture is added to the whitelist (skipped).
        /// 如果贴图通过所有条件则注册引用，否则加入白名单跳过。
        /// </summary>
        private void RegisterTextureReference(Texture2D tex, Material mat, Renderer renderer, int slot, string propName)
        {
            // Already whitelisted?
            if (_result.WhitelistedTextures.Contains(tex))
            {
                _log.Verbose($"Texture {tex.name} is whitelisted, skipping.");
                return;
            }

            // Check: must be a Texture2D
            if (tex == null)
            {
                return;
            }

            // Check: no ST offset/scale/rotation on this texture property
            if (HasSTTransform(mat, tex))
            {
                _log.Verbose($"Texture {tex.name} has ST transform, whitelisting.");
                _result.WhitelistedTextures.Add(tex);
                return;
            }

            // Determine category
            var category = DetermineCategory(mat, tex);

            var ref_ = _result.TextureReferences.TryGetValue(tex, out var existing) ? existing : new TextureReference
            {
                Texture = tex,
                Category = category,
                ImportHash = ComputeImportHash(tex),
            };

            ref_.Material = mat;
            ref_.PropertyName = propName;
            ref_.RendererId = renderer.GetInstanceID();
            ref_.SlotIndex = slot;
            ref_.Category = DetermineCategory(mat, tex);

            // Track alpha mode (take strictest)
            var (alphaMode, cutoff) = DetermineAlphaMode(mat);
            ref_.AlphaMode = TakeStrictestAlphaMode(ref_.AlphaMode, alphaMode);
            ref_.Cutoff = Mathf.Max(ref_.Cutoff, cutoff);

            if (!_result.TextureReferences.ContainsKey(tex))
                _result.TextureReferences[tex] = ref_;
        }

        /// <summary>
        /// Scans all animation clips referenced by the avatar's animator controllers
        /// for material swaps, texture swaps, and renderer enable/disable.
        /// 扫描动画控制器引用的所有动画剪辑。
        /// </summary>
        private void ScanAnimations()
        {
            var animators = _avatarRoot.GetComponentsInChildren<Animator>(true);
            var processedClips = new HashSet<AnimationClip>();

            foreach (var animator in animators)
            {
                var controller = animator.runtimeAnimatorController;
                if (controller == null) continue;
                CollectClips(controller, processedClips, animator);
            }

            // Also scan VRC SDK layers
            #if ATO_VRCSDK_PRESENT
            var descriptor = _avatarRoot.GetComponent<VRC.SDK3.Avatars.Components.VRCAvatarDescriptor>();
            if (descriptor != null)
            {
                foreach (var layer in descriptor.baseAnimationLayers)
                {
                    if (layer.animatorController != null)
                        CollectClips(layer.animatorController, processedClips, null);
                }
                foreach (var layer in descriptor.specialAnimationLayers)
                {
                    if (layer.animatorController != null)
                        CollectClips(layer.animatorController, processedClips, null);
                }
            }
            #endif

            _log.Verbose($"Scanned {processedClips.Count} unique animation clips.");
        }

        private void CollectClips(RuntimeAnimatorController controller, HashSet<AnimationClip> processed, Animator source)
        {
            foreach (var clip in controller.animationClips)
            {
                if (processed.Contains(clip)) continue;
                processed.Add(clip);
                ProcessAnimationClip(clip);
            }
        }

        private void ProcessAnimationClip(AnimationClip clip)
        {
            // Material/texture property animation
            foreach (var binding in UnityEditor.AnimationUtility.GetObjectReferenceCurveBindings(clip))
            {
                var curves = UnityEditor.AnimationUtility.GetObjectReferenceCurve(clip, binding);
                foreach (var curve in curves)
                {
                    if (curve.value is Material animMat)
                    {
                        // Material swap detected
                        AddAnimationMaterial(animMat);
                    }
                }
            }

            // Check for renderer enable/disable and ST transforms
            foreach (var binding in UnityEditor.AnimationUtility.GetCurveBindings(clip))
            {
                var path = binding.path;
                var propName = binding.propertyName;

                // Check for ST offset/scale/rotation animation → whitelist affected textures
                if (propName.Contains("_ST") || propName.Contains("TextureST") ||
                    propName.Contains("_ScrollRotate") || propName.Contains("_Angle"))
                {
                    // This animates texture transform - affected textures should be whitelisted
                    _result.Warnings.Add($"Animation '{clip.name}' animates texture ST '{propName}'. " +
                        "Affected textures will be whitelisted for safety.");
                    MarkSTAnimatedTextures(binding);
                }
            }
        }

        private void AddAnimationMaterial(Material mat)
        {
            // Find the material slot that could be swapped to this material
            // and add the animation material's textures
            foreach (var tex in GetMaterialTextures(mat))
            {
                if (tex is Texture2D t2d && !_result.WhitelistedTextures.Contains(t2d))
                {
                    // Register the animation-switched texture
                    if (!_result.TextureReferences.ContainsKey(t2d))
                    {
                        _result.TextureReferences[t2d] = new TextureReference
                        {
                            Texture = t2d,
                            Category = DetermineCategory(mat, t2d),
                            ImportHash = ComputeImportHash(t2d),
                            AlphaMode = DetermineAlphaMode(mat).Item1,
                            Cutoff = DetermineAlphaMode(mat).Item2,
                        };
                    }
                }
            }
        }

        private void MarkSTAnimatedTextures(EditorCurveBinding binding)
        {
            // Find textures affected by ST animation and whitelist them
            var propName = binding.propertyName.Replace("_ST", "");
            foreach (var kvp in _result.TextureReferences.ToList())
            {
                if (kvp.Key != null && kvp.Value.PropertyName != null &&
                    kvp.Value.PropertyName.StartsWith(propName))
                {
                    _result.WhitelistedTextures.Add(kvp.Key);
                    _result.TextureReferences.Remove(kvp.Key);
                    _log.Verbose($"Whitelisted {kvp.Key.name} due to ST animation.");
                }
            }
        }

        private void FinalizeTextureReferences()
        {
            // Remove whitelisted textures from references
            foreach (var wt in _result.WhitelistedTextures.ToList())
            {
                _result.TextureReferences.Remove(wt);
            }

            // Record original texture bytes
            foreach (var kvp in _result.TextureReferences)
            {
                var tex = kvp.Key;
                if (tex != null)
                {
                    // Approximate memory usage
                    var bytesPerPixel = GraphicsFormatUtility.GetBlockSize(tex.graphicsFormat);
                    if (bytesPerPixel == 0) bytesPerPixel = 4;
                }
            }
        }

        // ──────────────────────────────────────────────
        // Helper methods
        // 辅助方法
        // ──────────────────────────────────────────────

        internal static IEnumerable<Texture2D> GetMaterialTextures(Material mat)
        {
            if (mat == null || mat.shader == null) yield break;
            int count = ShaderUtil.GetPropertyCount(mat.shader);
            for (int i = 0; i < count; i++)
            {
                if (ShaderUtil.GetPropertyType(mat.shader, i) == ShaderUtil.ShaderPropertyType.TexEnv)
                {
                    var name = ShaderUtil.GetPropertyName(mat.shader, i);
                    var tex = mat.GetTexture(name);
                    if (tex is Texture2D t2d)
                        yield return t2d;
                }
            }
        }

        private bool HasSTTransform(Material mat, Texture2D tex)
        {
            if (mat == null) return false;
            int count = ShaderUtil.GetPropertyCount(mat.shader);
            for (int i = 0; i < count; i++)
            {
                if (ShaderUtil.GetPropertyType(mat.shader, i) == ShaderUtil.ShaderPropertyType.TexEnv)
                {
                    var name = ShaderUtil.GetPropertyName(mat.shader, i);
                    var t = mat.GetTexture(name);
                    if (t == tex)
                    {
                        var st = mat.GetTextureOffset(name);
                        var scale = mat.GetTextureScale(name);
                        // Default is (0,0) offset and (1,1) scale
                        if (st != Vector2.zero || scale != Vector2.one)
                        {
                            _log.Verbose($"  ST mismatch on {name}: offset={st} scale={scale}");
                            return true;
                        }
                        // Check for ScrollRotate-type properties (lilToon)
                        var srName = name + "_ScrollRotate";
                        if (mat.HasProperty(srName))
                        {
                            var sr = mat.GetVector(srName);
                            if (sr.x != 0 || sr.y != 0 || sr.z != 0)
                                return true;
                        }
                    }
                }
            }
            return false;
        }

        internal TextureCategory DetermineCategory(Material mat, Texture2D tex)
        {
            if (mat == null) return TextureCategory.Other;
            int count = ShaderUtil.GetPropertyCount(mat.shader);
            for (int i = 0; i < count; i++)
            {
                if (ShaderUtil.GetPropertyType(mat.shader, i) == ShaderUtil.ShaderPropertyType.TexEnv)
                {
                    var name = ShaderUtil.GetPropertyName(mat.shader, i);
                    var t = mat.GetTexture(name);
                    if (t == tex)
                    {
                        // Normal map?
                        if (name.Contains("BumpMap") || name.Contains("NormalMap") || name.Contains("_Normal") || name.Contains("_Bump"))
                        {
                            // Verify via import settings
                            var path = AssetDatabase.GetAssetPath(tex);
                            if (!string.IsNullOrEmpty(path))
                            {
                                var importer = AssetImporter.GetAtPath(path) as TextureImporter;
                                if (importer != null && importer.textureType == TextureImporterType.NormalMap)
                                    return TextureCategory.Normal;
                            }
                            return TextureCategory.Normal;
                        }

                        // Mask (grayscale)?
                        var lower = name.ToLower();
                        if (lower.Contains("mask") || lower.Contains("smoothness") || lower.Contains("metallic") ||
                            lower.Contains("ao") || lower.Contains("occlusion") || lower.Contains("roughness"))
                        {
                            return TextureCategory.Mask;
                        }

                        if (lower.Contains("emission") || lower.Contains("_emi"))
                            return TextureCategory.Emission;

                        // Main color / albedo
                        if (name == "_MainTex" || name == "_MainColor" || lower.Contains("maintex") ||
                            lower.Contains("albedo") || lower.Contains("basecolor") || lower.Contains("base"))
                        {
                            // Check alpha
                            if (tex != null && HasAlpha(tex))
                                return TextureCategory.Color;
                            return TextureCategory.ColorOpaque;
                        }

                        // Check texture format for alpha
                        if (tex != null && HasAlpha(tex))
                            return TextureCategory.Color;
                        return TextureCategory.ColorOpaque;
                    }
                }
            }
            return TextureCategory.Other;
        }

        private static bool HasAlpha(Texture2D tex)
        {
            return GraphicsFormatUtility.HasAlphaChannel(tex.graphicsFormat) ||
                   tex.format == TextureFormat.RGBA32 || tex.format == TextureFormat.RGBA64 ||
                   tex.format == TextureFormat.DXT5 || tex.format == TextureFormat.BC7;
        }

        internal static (AlphaMode, float) DetermineAlphaMode(Material mat)
        {
            if (mat == null) return (AlphaMode.Opaque, 0.5f);

            // Check for blend mode property (lilToon, Unity standard)
            float cutoff = 0.5f;
            if (mat.HasProperty("_Cutoff"))
                cutoff = mat.GetFloat("_Cutoff");

            // lilToon uses _TransparentMode
            if (mat.HasProperty("_TransparentMode"))
            {
                var mode = mat.GetFloat("_TransparentMode");
                // 0=opaque, 1=cutout, 2=transparent, 3=fur
                if (mode == 0) return (AlphaMode.Opaque, cutoff);
                if (mode == 1) return (AlphaMode.Cutout, cutoff);
                if (mode == 2) return (AlphaMode.Blend, cutoff);
                return (AlphaMode.TransClipping, cutoff);
            }

            // Unity standard render queue
            int queue = mat.renderQueue;
            if (queue >= 3000) return (AlphaMode.Blend, cutoff);
            if (queue >= 2450) return (AlphaMode.Cutout, cutoff);
            return (AlphaMode.Opaque, cutoff);
        }

        internal static AlphaMode TakeStrictestAlphaMode(AlphaMode a, AlphaMode b)
        {
            // Blend is strictest (preserves most alpha info), then Cutout, then Opaque
            int rank(AlphaMode m) => m switch
            {
                AlphaMode.Blend => 3,
                AlphaMode.TransClipping => 2,
                AlphaMode.Cutout => 1,
                _ => 0
            };
            return rank(a) >= rank(b) ? a : b;
        }

        internal static string ComputeImportHash(Texture2D tex)
        {
            if (tex == null) return "";
            var path = AssetDatabase.GetAssetPath(tex);
            var format = tex.format.ToString();
            var w = tex.width;
            var h = tex.height;
            var mip = tex.mipmapCount;
            var filter = tex.filterMode.ToString();
            var wrap = tex.wrapMode.ToString();
            var aniso = tex.anisoLevel;
            return $"{path}|{format}|{w}x{h}|mip{mip}|{filter}|{wrap}|aniso{aniso}";
        }
    }
}
