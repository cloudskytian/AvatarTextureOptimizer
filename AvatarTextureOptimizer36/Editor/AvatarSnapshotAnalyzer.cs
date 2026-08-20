using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Fosa.AvatarTextureOptimizer;

namespace Fosa.AvatarTextureOptimizer.Editor
{
    internal static class AvatarSnapshotAnalyzer
    {
        public static BuildSnapshot Analyze(ATOBuildSession.BuildContextAdapter context,
            AvatarTextureOptimizer component, ATOPlatformOptions platformOptions, ATOLogger logger, ATOProgress progress)
        {
            BuildSnapshot snapshot = new BuildSnapshot(context.Root);
            AnimationSafetyAnalyzer animation = AnimationSafetyAnalyzer.Analyze(context.Root, component, logger);
            snapshot.AnimationInfo.Clear();

            Renderer[] renderers = context.Root.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                if (renderer == null || IsEditorOnly(renderer.transform)) continue;
                progress.Step(0.05f + 0.90f * (i / (float)Math.Max(1, renderers.Length)),
                    "Analyze renderer " + (i + 1) + "/" + renderers.Length + " / 分析渲染器");

                RendererAnimationInfo animationInfo = animation.ForRenderer(renderer, context.Root.transform);
                if (animationInfo != null) snapshot.AnimationInfo[renderer] = animationInfo;
                bool consideredEnabled = renderer.enabled && renderer.gameObject.activeInHierarchy;
                if (!consideredEnabled && (animationInfo == null || !animationInfo.HasAnimatedEnable)) continue;

                Mesh mesh = GetMesh(renderer);
                if (mesh == null || mesh.vertexCount == 0) continue;

                RendererRecord record = new RendererRecord
                {
                    Renderer = renderer,
                    SourceMesh = mesh,
                    IsSkinned = renderer is SkinnedMeshRenderer,
                    AnimationAreaScale = animationInfo == null ? 1f : animationInfo.MaxAreaScale
                };
                if (component.IsWhitelisted(renderer.gameObject) || animationInfo != null && animationInfo.HasAnimatedTextureTransform)
                {
                    record.SkipAll = true;
                }
                snapshot.Renderers.Add(record);

                Material[] materials = renderer.sharedMaterials;
                for (int slot = 0; slot < materials.Length; slot++)
                {
                    Material material = materials[slot];
                    if (material == null) continue;
                    MaterialUse use = new MaterialUse
                    {
                        Owner = record,
                        Slot = slot,
                        SourceMaterial = material,
                        SkipAll = record.SkipAll || component.IsWhitelisted(material),
                        HasAnimatedMaterialSwitch = animationInfo != null && animationInfo.HasAnimatedMaterialSwitch,
                        HasAnimatedTextureTransform = animationInfo != null && animationInfo.HasAnimatedTextureTransform
                    };
                    record.Materials.Add(use);
                    snapshot.MaterialUses.Add(use);

                    if (use.SkipAll) continue;
                    ShaderTextureResolution resolution = ShaderTextureResolver.Resolve(material, logger);
                    use.ShaderRecognized = resolution.Recognized;
                    MaterialAlphaInspector.Apply(material, use);
                    if (!resolution.Recognized)
                    {
                        use.SkipAll = true;
                        logger.Warning("Unsupported or ambiguous shader on material '" + material.name + "'; skipped safely. / 材质使用不支持或有歧义的 Shader，已安全跳过。");
                        continue;
                    }

                    if (use.HasAnimatedMaterialSwitch || use.HasAnimatedTextureTransform)
                    {
                        use.SkipAll = true;
                        use.SkipAtlas = true;
                        logger.Warning("Animated material switch or texture transform detected on '" + material.name + "'; material use falls back without UV rewrite. / 检测到动画材质切换或纹理变换，已回退且不改写 UV。");
                    }

                    for (int refIndex = 0; refIndex < resolution.References.Count; refIndex++)
                    {
                        ResolvedTextureReference resolved = resolution.References[refIndex];
                        if (resolved.Texture == null) continue;
                        Texture2D texture = resolved.Texture as Texture2D;
                        if (texture == null)
                        {
                            use.SkipAll = true;
                            use.SkipAtlas = true;
                            logger.Warning("Non-Texture2D property '" + resolved.PropertyName + "' on '" + material.name + "' was skipped. / 非 Texture2D 属性已跳过。");
                            continue;
                        }

                        TextureAssetInfo textureInfo;
                        if (!snapshot.TextureMap.TryGetValue(texture, out textureInfo))
                        {
                            textureInfo = TextureAssetInspector.Create(texture, component, logger);
                            snapshot.AddTexture(textureInfo);
                        }
                        ATOTextureCategory actualCategory = resolved.Category == ATOTextureCategory.Opaque && textureInfo.HasAlpha
                            ? ATOTextureCategory.Transparent
                            : resolved.Category;
                        TextureReference reference = new TextureReference
                        {
                            PropertyName = resolved.PropertyName,
                            Texture = textureInfo,
                            Category = actualCategory,
                            UVChannel = resolved.UVChannel,
                            IsPrimary = resolved.IsPrimary,
                            IsWhitelisted = component.IsWhitelisted(texture) || textureInfo.IsWhitelisted,
                            IsAnimatedVariant = false,
                            TypeGroupKey = TextureAssetInspector.TypeGroupKey(textureInfo, actualCategory)
                        };
                        use.References.Add(reference);
                        textureInfo.References.Add(reference);
                        if (reference.IsWhitelisted) use.SkipAtlas = true;
                        if (resolved.Category == ATOTextureCategory.Normal) textureInfo.IsNormal = true;
                        if (resolved.Category == ATOTextureCategory.Grayscale) textureInfo.IsGrayscale = true;
                    }

                    if (use.References.Count == 0)
                    {
                        use.SkipAll = true;
                        continue;
                    }

                    // A material switch is handled conservatively; identical variants are safe, divergent variants are not.
                    // 材质切换采用保守策略；完全相同的变体安全，不同变体不做 UV 改写。
                    if (use.HasAnimatedMaterialSwitch && !AnimationSafetyAnalyzer.AllVariantsShareTextures(animationInfo, use))
                    {
                        use.SkipAll = true;
                        use.SkipAtlas = true;
                    }
                }
            }

            TextureAssetInspector.RebuildTypeGroups(snapshot);
            logger.Info("Analysis found renderers=" + snapshot.Renderers.Count + ", material uses=" +
                        snapshot.MaterialUses.Count + ", textures=" + snapshot.Textures.Count + ". / 分析完成。");
            return snapshot;
        }

        private static Mesh GetMesh(Renderer renderer)
        {
            SkinnedMeshRenderer skinned = renderer as SkinnedMeshRenderer;
            if (skinned != null) return skinned.sharedMesh;
            MeshFilter filter = renderer.GetComponent<MeshFilter>();
            return filter == null ? null : filter.sharedMesh;
        }

        private static bool IsEditorOnly(Transform transform)
        {
            Transform current = transform;
            while (current != null)
            {
                if (current.gameObject.CompareTag("EditorOnly")) return true;
                current = current.parent;
            }
            return false;
        }
    }

    internal sealed class ShaderTextureResolution
    {
        public bool Recognized;
        public readonly List<ResolvedTextureReference> References = new List<ResolvedTextureReference>();
    }

    internal sealed class ResolvedTextureReference
    {
        public string PropertyName;
        public Texture Texture;
        public ATOTextureCategory Category;
        public int UVChannel;
        public bool IsPrimary;
    }

    /// <summary>
    /// Shader metadata reader with explicit conservative fallbacks. / 带明确保守回退的 Shader 元数据读取器。
    /// </summary>
    internal static class ShaderTextureResolver
    {
        private static readonly string[] MainTokens = { "maintex", "basemap", "basecolor", "albedo", "colormap", "diffuse" };
        private static readonly string[] NormalTokens = { "normal", "bump", "nrm" };
        private static readonly string[] GrayTokens = { "mask", "metallic", "roughness", "smoothness", "occlusion", "ao", "ramp", "lookup", "shadow" };
        private static readonly string[] IgnoredTokens = { "lightmap", "reflection", "cube", "grab", "decal", "matcap", "parallax", "fur", "audio" };

        public static ShaderTextureResolution Resolve(Material material, ATOLogger logger)
        {
            ShaderTextureResolution result = new ShaderTextureResolution();
            if (material == null || material.shader == null) return result;
            if (ATOExtensionRegistry.TryResolveShader(material, result.References, logger))
            {
                result.Recognized = true;
                return result;
            }

            Shader shader = material.shader;
            string shaderName = shader.name ?? string.Empty;
            bool shaderKnown = IsKnownShader(shaderName);
            int propertyCount;
            try
            {
                propertyCount = ShaderUtil.GetPropertyCount(shader);
            }
            catch (Exception)
            {
                return result;
            }

            for (int i = 0; i < propertyCount; i++)
            {
                if (ShaderUtil.GetPropertyType(shader, i) != ShaderUtil.ShaderPropertyType.TexEnv) continue;
                string propertyName = ShaderUtil.GetPropertyName(shader, i);
                if (string.IsNullOrEmpty(propertyName) || ContainsAny(propertyName, IgnoredTokens)) continue;
                Texture texture = material.GetTexture(propertyName);
                if (texture == null) continue;
                string lower = propertyName.ToLowerInvariant();
                ATOTextureCategory category;
                bool primary;
                if (ContainsAny(lower, NormalTokens))
                {
                    category = ATOTextureCategory.Normal;
                    primary = false;
                }
                else if (ContainsAny(lower, GrayTokens))
                {
                    category = ATOTextureCategory.Grayscale;
                    primary = false;
                }
                else if (ContainsAny(lower, MainTokens))
                {
                    category = ATOTextureCategory.Opaque;
                    primary = true;
                }
                else
                {
                    // Unknown texture slots on unknown shaders are unsafe; standard/lilToon can still be recognized by property table.
                    // 未知 Shader 的未知纹理槽不安全；标准/lilToon 可依靠属性表识别。
                    if (!shaderKnown) continue;
                    category = ATOTextureCategory.Unknown;
                    primary = false;
                }

                Vector2 scale = material.GetTextureScale(propertyName);
                Vector2 offset = material.GetTextureOffset(propertyName);
                if (scale != Vector2.one || offset != Vector2.zero)
                {
                    result.Recognized = false;
                    result.References.Clear();
                    return result;
                }

                result.References.Add(new ResolvedTextureReference
                {
                    PropertyName = propertyName,
                    Texture = texture,
                    Category = category,
                    UVChannel = InferUVChannel(propertyName, shader, i),
                    IsPrimary = primary
                });
            }

            if (result.References.Count == 0) return result;
            result.Recognized = shaderKnown || result.References.Any(r => r.IsPrimary);
            return result;
        }

        private static bool IsKnownShader(string shaderName)
        {
            string lower = shaderName.ToLowerInvariant();
            return lower.Contains("standard") || lower.Contains("liltoon") || lower.Contains("poiyomi") ||
                   lower.Contains("vrchat") || lower.Contains("universal render pipeline") || lower.Contains("hdrp") ||
                   lower.Contains("toon") || lower.Contains("mobile");
        }

        private static int InferUVChannel(string propertyName, Shader shader, int propertyIndex)
        {
            string lower = propertyName.ToLowerInvariant();
            if (lower.Contains("uv1") || lower.Contains("texcoord1") || lower.EndsWith("_1")) return 1;
            if (lower.Contains("uv2") || lower.Contains("texcoord2") || lower.EndsWith("_2")) return 2;
            if (lower.Contains("uv3") || lower.Contains("texcoord3") || lower.EndsWith("_3")) return 3;
            try
            {
                string[] attributes = shader.GetPropertyAttributes(propertyIndex);
                if (attributes != null)
                {
                    for (int i = 0; i < attributes.Length; i++)
                    {
                        string attribute = attributes[i].ToLowerInvariant();
                        for (int channel = 0; channel < 8; channel++)
                        {
                            if (attribute.Contains("texcoord" + channel) || attribute.Contains("uv" + channel)) return channel;
                        }
                    }
                }
            }
            catch (Exception)
            {
                // Attribute APIs vary between Unity versions; UV0 is the safe default. / 不同 Unity 版本属性 API 可能不同，安全默认 UV0。
            }
            return 0;
        }

        private static bool ContainsAny(string value, string[] tokens)
        {
            string lower = value.ToLowerInvariant();
            for (int i = 0; i < tokens.Length; i++) if (lower.Contains(tokens[i])) return true;
            return false;
        }
    }

    internal static class TextureAssetInspector
    {
        public static TextureAssetInfo Create(Texture2D texture, AvatarTextureOptimizer component, ATOLogger logger)
        {
            TextureAssetInfo info = new TextureAssetInfo
            {
                Source = texture,
                Width = texture.width,
                Height = texture.height,
                FilterMode = texture.filterMode,
                WrapMode = texture.wrapMode,
                IsWhitelisted = component.IsWhitelisted(texture),
                HasAlpha = false,
                SRGB = true
            };
            string path = AssetDatabase.GetAssetPath(texture);
            TextureImporter importer = string.IsNullOrEmpty(path) ? null : AssetImporter.GetAtPath(path) as TextureImporter;
            if (importer != null)
            {
                try { info.HasAlpha = importer.DoesSourceTextureHaveAlpha(); } catch (Exception) { info.HasAlpha = false; }
                info.SRGB = importer.sRGBTexture;
                info.Fingerprint = new TextureImportFingerprint(texture.width, texture.height, texture.wrapMode,
                    texture.filterMode, importer.mipmapEnabled, importer.streamingMipmaps, importer.sRGBTexture,
                    importer.textureCompression, importer.maxTextureSize, path);
            }
            else
            {
                info.Fingerprint = new TextureImportFingerprint(texture.width, texture.height, texture.wrapMode,
                    texture.filterMode, true, false, true, TextureImporterCompression.Uncompressed,
                    texture.width, path);
            }

            // Fast content classification is deliberately conservative; the pixel reader can refine it later.
            // 快速内容分类故意保守，稍后像素读取器会进一步确认。
            info.IsGrayscale = false;
            logger.Detail("Texture " + texture.name + " " + texture.width + "x" + texture.height + " alpha=" + info.HasAlpha);
            return info;
        }

        public static string TypeGroupKey(TextureAssetInfo texture, ATOTextureCategory category)
        {
            if (texture == null) return "unknown";
            return category + ":" + texture.FilterMode + ":" + texture.SRGB + ":" + texture.WrapMode;
        }

        public static void RebuildTypeGroups(BuildSnapshot snapshot)
        {
            for (int i = 0; i < snapshot.Textures.Count; i++)
            {
                TextureAssetInfo texture = snapshot.Textures[i];
                HashSet<ATOTextureCategory> categories = new HashSet<ATOTextureCategory>();
                for (int j = 0; j < texture.References.Count; j++) categories.Add(texture.References[j].Category);
                string group = texture.FilterMode + ":" + texture.SRGB + ":" + texture.WrapMode + ":" +
                               string.Join(",", categories.OrderBy(c => (int)c).Select(c => c.ToString()).ToArray());
                texture.TypeGroupKey = group;
                for (int j = 0; j < texture.References.Count; j++) texture.References[j].TypeGroupKey = group;
            }
        }
    }
}
