using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Net.Fosa.AvatarTextureOptimizer;
using nadena.dev.ndmf;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// Executes the currently proven-safe subset of texture optimization.
    /// 执行当前已证明安全子集的贴图优化。
    /// </summary>
    internal static class AtoExecutor
    {
        public static void Execute(BuildContext context, AtoSessionState session)
        {
            if (session.Component.GenerateAtlases)
            {
                ExecuteAtlasMode(context, session);
            }
            else
            {
                ExecuteDirectMode(context, session);
            }

            AtoAnimationRewriter.RewriteMaterialReferences(session);
            ApplyBasicDeduplication(context, session);
        }

        private static void ExecuteDirectMode(BuildContext context, AtoSessionState session)
        {
            var materialClones = new Dictionary<string, Material>(StringComparer.OrdinalIgnoreCase);
            var sourceCache = new Dictionary<Texture2D, Texture2D>();
            var uvPlans = session.Plan.UvGroupPlans.ToDictionary(plan => plan.Key, StringComparer.OrdinalIgnoreCase);

            var directGroups = session.ScanResult.TextureUsages
                .Where(CanDirectUsageExecute)
                .Where(usage => usage.Texture is Texture2D)
                .GroupBy(usage => $"{usage.Texture.GetInstanceID()}|{usage.Semantic}|{usage.FilterMode}|{usage.WrapModeU}|{usage.WrapModeV}", StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var group in directGroups)
            {
                var first = group.First();
                var sourceTexture = (Texture2D)first.Texture;
                var maxScale = 0.0f;
                foreach (var usage in group)
                {
                    if (!uvPlans.TryGetValue(usage.UvGroupKey, out var uvPlan))
                    {
                        continue;
                    }

                    var sourcePixels = uvPlan.EstimatedSourcePixels;
                    if (sourcePixels.x <= 0.0f || sourcePixels.y <= 0.0f)
                    {
                        continue;
                    }

                    var scale = Mathf.Max(
                        uvPlan.EstimatedTargetPixels.x / sourcePixels.x,
                        uvPlan.EstimatedTargetPixels.y / sourcePixels.y);
                    maxScale = Mathf.Max(maxScale, scale);
                }

                maxScale = Mathf.Clamp(maxScale, 0.0f, 1.0f);
                var targetWidth = Mathf.Max(4, Mathf.CeilToInt(sourceTexture.width * maxScale));
                var targetHeight = Mathf.Max(4, Mathf.CeilToInt(sourceTexture.height * maxScale));
                if (targetWidth >= sourceTexture.width && targetHeight >= sourceTexture.height)
                {
                    continue;
                }

                var generated = GenerateScaledTexture(sourceTexture, first.Semantic, first.FilterMode, targetWidth, targetHeight, sourceCache, session.Component);
                generated.name = MakeGeneratedName(sourceTexture.name, first.MaterialProperty);
                context.AssetSaver.SaveAsset(generated);
                session.Report.ExecutedTextureCount++;

                foreach (var usage in group)
                {
                    var material = GetWritableMaterial(context, session, usage.Renderer, usage.MaterialSlotIndex, materialClones, ref session.Report.ExecutedMaterialCount);
                    if (material != null)
                    {
                        material.SetTexture(usage.MaterialProperty, generated);
                    }
                }
            }
        }

        private static void ExecuteAtlasMode(BuildContext context, AtoSessionState session)
        {
            var atlasEligibleUvGroups = session.ScanResult.UvGroups.Where(CanAtlasExecute).ToList();
            if (atlasEligibleUvGroups.Count == 0)
            {
                AtoLog.Info("No UV groups qualified for safe atlas execution. Atlas mode fell back to no-op.");
                return;
            }

            var sharedLayouts = AtoAtlasPlanning.PlanSharedLayout(atlasEligibleUvGroups, session.Component);
            if (sharedLayouts.Count == 0)
            {
                AtoLog.Info("Shared atlas layout planning produced no atlas candidates. Atlas mode fell back to no-op.");
                return;
            }

            var uvGroupByKey = atlasEligibleUvGroups.ToDictionary(group => group.Key, StringComparer.OrdinalIgnoreCase);
            var layoutByUvGroup = new Dictionary<string, LayoutRef>(StringComparer.OrdinalIgnoreCase);
            for (var atlasIndex = 0; atlasIndex < sharedLayouts.Count; atlasIndex++)
            {
                foreach (var item in sharedLayouts[atlasIndex].Items)
                {
                    layoutByUvGroup[item.UvGroupKey] = new LayoutRef(atlasIndex, item);
                }
            }

            var materialClones = new Dictionary<string, Material>(StringComparer.OrdinalIgnoreCase);
            var meshClones = new Dictionary<Renderer, Mesh>();
            var aaoEvacuatedChannels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var remappedUvGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var sourceCache = new Dictionary<Texture2D, Texture2D>();

            foreach (var typeGroup in session.Plan.TextureTypeGroups)
            {
                var compatibleUsageGroups = typeGroup.Members
                    .Where(usage => usage.Decision == AtoTextureDecision.Candidate)
                    .Where(usage => layoutByUvGroup.ContainsKey(usage.UvGroupKey))
                    .GroupBy(usage => usage.UvGroupKey, StringComparer.OrdinalIgnoreCase)
                    .Where(group => group.Select(usage => usage.ContentFingerprint).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1)
                    .ToList();

                if (compatibleUsageGroups.Count == 0)
                {
                    continue;
                }

                var generatedAtlases = new Dictionary<int, Texture2D>();
                foreach (var usageGroup in compatibleUsageGroups)
                {
                    var usage = usageGroup.First();
                    if (usage.Texture is not Texture2D sourceTexture || !uvGroupByKey.TryGetValue(usageGroup.Key, out var uvGroup))
                    {
                        continue;
                    }

                    var layout = layoutByUvGroup[usage.UvGroupKey];
                    var atlasPlan = sharedLayouts[layout.AtlasIndex];
                    if (!generatedAtlases.TryGetValue(layout.AtlasIndex, out var atlasTexture))
                    {
                        atlasTexture = CreateBlankAtlasTexture(atlasPlan.Width, atlasPlan.Height, usage.Semantic, usage.FilterMode, session.Component);
                        atlasTexture.name = MakeGeneratedName(atlasPlan.Name ?? typeGroup.MaterialProperty, $"atlas_{layout.AtlasIndex:D3}");
                        generatedAtlases.Add(layout.AtlasIndex, atlasTexture);
                        session.Report.ExecutedAtlasCount++;
                    }

                    var patch = GenerateCroppedTexture(sourceTexture, uvGroup, usage.Semantic, usage.FilterMode, new Vector2(layout.Item.PixelWidth, layout.Item.PixelHeight), sourceCache, session.Component, useClamp: true);
                    CopyTexturePatch(atlasTexture, patch, layout.Item.PixelX, layout.Item.PixelY);
                }

                foreach (var atlasTexture in generatedAtlases.Values)
                {
                    AtoTexturePostprocess.DilateTransparentBorders(atlasTexture, typeGroup.Semantic, Mathf.Clamp(session.Component.General.MinimumPadding, 1, 16));
                    if (typeGroup.Semantic == AtoTextureSemantic.Normal)
                    {
                        AtoTexturePostprocess.RenormalizeNormalMap(atlasTexture);
                    }
                    atlasTexture.Apply(updateMipmaps: atlasTexture.mipmapCount > 1, makeNoLongerReadable: false);
                    AtoTextureCompression.Apply(atlasTexture, typeGroup.Semantic, session.Component);
                    context.AssetSaver.SaveAsset(atlasTexture);
                }

                foreach (var usageGroup in compatibleUsageGroups)
                {
                    var layout = layoutByUvGroup[usageGroup.Key];
                    if (!generatedAtlases.TryGetValue(layout.AtlasIndex, out var atlasTexture))
                    {
                        continue;
                    }

                    foreach (var usage in usageGroup)
                    {
                        var material = GetWritableMaterial(context, session, usage.Renderer, usage.MaterialSlotIndex, materialClones, ref session.Report.ExecutedMaterialCount);
                        if (material != null)
                        {
                            material.SetTexture(usage.MaterialProperty, atlasTexture);
                        }
                    }
                }
            }

            foreach (var rendererGroup in atlasEligibleUvGroups.GroupBy(group => group.Renderer))
            {
                var renderer = rendererGroup.Key;
                var sourceMesh = renderer is SkinnedMeshRenderer smr ? smr.sharedMesh : renderer.GetComponent<MeshFilter>()?.sharedMesh;
                if (renderer == null || sourceMesh == null)
                {
                    continue;
                }

                var remapPlans = new List<AtoMeshRebuilder.UvRemapPlan>();
                foreach (var uvGroup in rendererGroup)
                {
                    if (!layoutByUvGroup.TryGetValue(uvGroup.Key, out var layout))
                    {
                        continue;
                    }

                    var atlasPlan = sharedLayouts[layout.AtlasIndex];
                    var rect = new Rect(
                        layout.Item.PixelX / (float)atlasPlan.Width,
                        layout.Item.PixelY / (float)atlasPlan.Height,
                        layout.Item.PixelWidth / (float)atlasPlan.Width,
                        layout.Item.PixelHeight / (float)atlasPlan.Height);
                    remapPlans.Add(new AtoMeshRebuilder.UvRemapPlan(
                        uvGroup.MaterialSlotIndex,
                        uvGroup.UvChannel,
                        rect,
                        uvGroup.Min + (uvGroup.InUnitSquareAlready ? Vector2.zero : uvGroup.Translation),
                        uvGroup.Span,
                        uvGroup.InUnitSquareAlready ? Vector2.zero : uvGroup.Translation));
                }

                if (remapPlans.Count == 0)
                {
                    continue;
                }

                var originalChannels = remapPlans.Select(plan => plan.UvChannel).Distinct().ToArray();
                var rebuilt = AtoMeshRebuilder.RebuildWithIndependentSubmeshes(sourceMesh, remapPlans);
                if (rebuilt == null)
                {
                    continue;
                }

                var writableMesh = GetWritableMesh(context, renderer, meshClones, ref session.Report.ExecutedMeshCount);
                if (writableMesh == null)
                {
                    continue;
                }

                Object.DestroyImmediate(writableMesh);
                rebuilt.name = MakeGeneratedName(sourceMesh.name, "mesh");
                context.AssetSaver.SaveAsset(rebuilt);
                meshClones[renderer] = rebuilt;
                if (renderer is SkinnedMeshRenderer skinnedRenderer)
                {
                    skinnedRenderer.sharedMesh = rebuilt;
                }
                else if (renderer.TryGetComponent<MeshFilter>(out var meshFilter))
                {
                    meshFilter.sharedMesh = rebuilt;
                }

                foreach (var channel in originalChannels)
                {
                    TryPreserveOriginalUvForAao(renderer, rebuilt, channel, aaoEvacuatedChannels);
                }
            }
        }

        private static bool CanDirectUsageExecute(AtoTextureUsageRecord usage)
        {
            return usage != null
                   && usage.Decision == AtoTextureDecision.Candidate
                   && usage.IsTexture2D
                   && usage.IsPotentiallyActive
                   && !usage.IsWhitelisted
                   && !usage.IsAnimatedMaterialReference
                   && !usage.IsAnimatedProperty
                   && !usage.IsAnimatedSt
                   && usage.UsesIdentityTransform;
        }

        private static bool CanAtlasExecute(AtoUvGroupRecord uvGroup)
        {
            return uvGroup != null
                   && uvGroup.Renderer != null
                   && uvGroup.Mesh != null
                   && uvGroup.HasData
                   && uvGroup.Usages.Count > 0
                   && uvGroup.Usages.All(usage => usage.Decision == AtoTextureDecision.Candidate)
                   && (uvGroup.InUnitSquareAlready || uvGroup.CanTranslateIntoUnitSquare)
                   && uvGroup.Usages.GroupBy(usage => usage.MaterialProperty, StringComparer.OrdinalIgnoreCase)
                       .All(group => group.Select(usage => usage.ContentFingerprint).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1);
        }

        private static Texture2D GenerateScaledTexture(Texture2D sourceTexture, AtoTextureSemantic semantic, FilterMode filterMode, int width, int height, Dictionary<Texture2D, Texture2D> sourceCache, AvatarTextureOptimizer component)
        {
            var readable = GetReadableTexture(sourceTexture, sourceCache);
            var linear = semantic != AtoTextureSemantic.Color;
            var mipChain = ShouldUseMipChain(component, semantic);
            var output = new Texture2D(width, height, TextureFormat.RGBA32, mipChain, linear)
            {
                wrapMode = sourceTexture.wrapMode,
                filterMode = filterMode,
                anisoLevel = sourceTexture.anisoLevel,
            };

            var colors = new Color[width * height];
            for (var y = 0; y < height; y++)
            {
                var v = (y + 0.5f) / height;
                for (var x = 0; x < width; x++)
                {
                    var u = (x + 0.5f) / width;
                    colors[y * width + x] = readable.GetPixelBilinear(u, v);
                }
            }

            output.SetPixels(colors);
            if (semantic == AtoTextureSemantic.Normal)
            {
                AtoTexturePostprocess.RenormalizeNormalMap(output);
            }
            output.Apply(updateMipmaps: mipChain, makeNoLongerReadable: false);
            AtoTextureCompression.Apply(output, semantic, component);
            return output;
        }

        private static Texture2D GenerateCroppedTexture(Texture2D sourceTexture, AtoUvGroupRecord uvGroup, AtoTextureSemantic semantic, FilterMode filterMode, Vector2 targetPixels, Dictionary<Texture2D, Texture2D> sourceCache, AvatarTextureOptimizer component, bool useClamp)
        {
            var readable = GetReadableTexture(sourceTexture, sourceCache);
            var width = Mathf.Max(4, Mathf.CeilToInt(targetPixels.x));
            var height = Mathf.Max(4, Mathf.CeilToInt(targetPixels.y));
            var linear = semantic != AtoTextureSemantic.Color;
            var mipChain = ShouldUseMipChain(component, semantic);
            var output = new Texture2D(width, height, TextureFormat.RGBA32, mipChain, linear)
            {
                wrapMode = useClamp ? TextureWrapMode.Clamp : sourceTexture.wrapMode,
                filterMode = filterMode,
                anisoLevel = sourceTexture.anisoLevel,
            };

            var colors = AtoTextureRasterizer.RenderUvGroupPatch(readable, uvGroup, width, height, semantic);
            output.SetPixels(colors);
            AtoTexturePostproantic);
            output.SetPixels(colors);
            AtoTexturePostprocess.DilateTransparentBorders(output, semantic, Mathf.Clamp(component.General.MinimumPadding, 1, 16));
            if (semantic == AtoTextureSemantic.Normal)
            {
                AtoTexturePostprocess.RenormalizeNormalMap(output);
            }
            output.Apply(updateMipmaps: mipChain, makeNoLongerReadable: false);
            AtoTextureCompression.Apply(output, semantic, component);
            return output;
        }

        private static Texture2D CreateBlankAtlasTexture(int width, int height, AtoTextureSemantic semantic, FilterMode filterMode, AvatarTextureOptimizer component)
        {
            var linear = semantic != AtoTextureSemantic.Color;
            var mipChain = ShouldUseMipChain(component, semantic);
            var atlas = new Texture2D(width, height, TextureFormat.RGBA32, mipChain, linear)
            {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = filterMode,
                anisoLevel = 1,
            };

            var clear = semantic == AtoTextureSemantic.Normal ? new Color(0.5f, 0.5f, 1.0f, 1.0f) : Color.clear;
            var colors = new Color[width * height];
            for (var i = 0; i < colors.Length; i++)
            {
                colors[i] = clear;
            }
            atlas.SetPixels(colors);
            return atlas;
        }

        private static void CopyTexturePatch(Texture2D atlas, Texture2D patch, int pixelX, int pixelY)
        {
            atlas.SetPixels(pixelX, pixelY, patch.width, patch.height, patch.GetPixels());
        }

        private static Texture2D GetReadableTexture(Texture2D sourceTexture, IDictionary<Texture2D, Texture2D> sourceCache)
        {
            if (sourceCache.TryGetValue(sourceTexture, out var cached))
            {
                return cached;
            }

            var rt = RenderTexture.GetTemporary(sourceTexture.width, sourceTexture.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default);
            var previous = RenderTexture.active;
            Graphics.Blit(sourceTexture, rt);
            RenderTexture.active = rt;
            var readable = new Texture2D(sourceTexture.width, sourceTexture.height, TextureFormat.RGBA32, false, false);
            readable.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
            readable.Apply(false, false);
            RenderTexture.active = previous;
            RenderTexture.ReleaseTemporary(rt);
            sourceCache[sourceTexture] = readable;
            return readable;
        }

        private static Material GetWritableMaterial(BuildContext context, AtoSessionState session, Renderer renderer, int materialSlotIndex, IDictionary<string, Material> materialClones, ref int createdCount)
        {
            var key = $"{renderer.GetInstanceID()}#{materialSlotIndex}";
            if (materialClones.TryGetValue(key, out var existing))
            {
                return existing;
            }

            var sharedMaterials = renderer.sharedMaterials;
            if (materialSlotIndex < 0 || materialSlotIndex >= sharedMaterials.Length || sharedMaterials[materialSlotIndex] == null)
            {
                return null;
            }

            var original = sharedMaterials[materialSlotIndex];
            var clone = Object.Instantiate(original);
            clone.name = MakeGeneratedName(original.name, $"mat{materialSlotIndex}");
            sharedMaterials[materialSlotIndex] = clone;
            renderer.sharedMaterials = sharedMaterials;
            context.AssetSaver.SaveAsset(clone);
            materialClones[key] = clone;
            var relativePath = RelativePathFromRoot(session.Component.transform, renderer.transform);
            session.MaterialRewriteMap[AtoAnimationRewriter.BuildKey(relativePath, materialSlotIndex, original)] = clone;
            createdCount++;
            return clone;
        }

        private static Mesh GetWritableMesh(BuildContext context, Renderer renderer, IDictionary<Renderer, Mesh> meshClones, ref int createdCount)
        {
            if (meshClones.TryGetValue(renderer, out var existing))
            {
                return existing;
            }

            var source = renderer is SkinnedMeshRenderer smr ? smr.sharedMesh : renderer.GetComponent<MeshFilter>()?.sharedMesh;
            if (source == null)
            {
                return null;
            }

            var clone = Object.Instantiate(source);
            clone.name = MakeGeneratedName(source.name, "mesh");
            if (renderer is SkinnedMeshRenderer skinnedMeshRenderer)
            {
                skinnedMeshRenderer.sharedMesh = clone;
            }
            else if (renderer.TryGetComponent<MeshFilter>(out var meshFilter))
            {
                meshFilter.sharedMesh = clone;
            }

            context.AssetSaver.SaveAsset(clone);
            meshClones[renderer] = clone;
            createdCount++;
            return clone;
        }

        private static void RemapMeshUvGroupWholeChannel(Mesh mesh, AtoUvGroupRecord uvGroup, Rect targetRect)
        {
            if (mesh == null)
            {
                return;
            }

            var uvs = new List<Vector2>();
            mesh.GetUVs(uvGroup.UvChannel, uvs);
            if (uvs.Count == 0)
            {
                return;
            }

            var translation = uvGroup.InUnitSquareAlready ? Vector2.zero : uvGroup.Translation;
            var min = uvGroup.Min + translation;
            var size = uvGroup.Span;
            var width = Mathf.Max(size.x, 0.000001f);
            var height = Mathf.Max(size.y, 0.000001f);

            for (var i = 0; i < uvs.Count; i++)
            {
                var shifted = uvs[i] + translation;
                var nx = (shifted.x - min.x) / width;
                var ny = (shifted.y - min.y) / height;
                uvs[i] = new Vector2(
                    targetRect.xMin + targetRect.width * nx,
                    targetRect.yMin + targetRect.height * ny);
            }

            mesh.SetUVs(uvGroup.UvChannel, uvs);
        }

        private static string RelativePathFromRoot(Transform root, Transform target)
        {
            if (root == null || target == null)
            {
                return string.Empty;
            }

            if (root == target)
            {
                return string.Empty;
            }

            var names = new Stack<string>();
            var current = target;
            while (current != null && current != root)
            {
                names.Push(current.name);
                current = current.parent;
            }

            return string.Join("/", names);
        }

        private static void TryPreserveOriginalUvForAao(Renderer renderer, Mesh mesh, int originalChannel, ISet<string> alreadyEvacuated)
        {
            if (renderer is not SkinnedMeshRenderer skinnedMeshRenderer)
            {
                return;
            }

            var key = $"{renderer.GetInstanceID()}|uv{originalChannel}";
            if (alreadyEvacuated.Contains(key))
            {
                return;
            }

            if (!AtoReflection.TryIsAaoTexCoordUsed(skinnedMeshRenderer, originalChannel, out var usedByAao, out _) || !usedByAao)
            {
                return;
            }

            for (var savedChannel = 0; savedChannel < 8; savedChannel++)
            {
                if (savedChannel == originalChannel)
                {
                    continue;
                }

                if (AtoReflection.TryIsAaoTexCoordUsed(skinnedMeshRenderer, savedChannel, out var savedUsedByAao, out _) && savedUsedByAao)
                {
                    continue;
                }

                var originalUvs = new List<Vector2>();
                mesh.GetUVs(originalChannel, originalUvs);
                if (originalUvs.Count == 0)
                {
                    return;
                }

                mesh.SetUVs(savedChannel, originalUvs);
                if (AtoReflection.TryRegisterAaoUvEvacuation(skinnedMeshRenderer, originalChannel, savedChannel, out var failure))
                {
                    AtoLog.Info($"AAO UV evacuation registered for {renderer.name}: uv{originalChannel} -> uv{savedChannel}.");
                    alreadyEvacuated.Add(key);
                    return;
                }

                AtoLog.Warn($"AAO UV evacuation attempt failed for {renderer.name} uv{originalChannel} -> uv{savedChannel}: {failure}");
            }
        }

        private static bool ShouldUseMipChain(AvatarTextureOptimizer component, AtoTextureSemantic semantic)
        {
            return semantic switch
            {
                AtoTextureSemantic.Color => component.General.EnableMipMapAndStreamingForColor,
                AtoTextureSemantic.Normal => component.General.EnableMipMapAndStreamingForNormal,
                _ => component.General.EnableMipMapAndStreamingForMask,
            };
        }

        private static void ApplyBasicDeduplication(BuildContext context, AtoSessionState session)
        {
            DeduplicateMeshes(context);

            if (session.Component.DeduplicateTextures)
            {
                DeduplicateTextures(context, session);
            }

            if (session.Component.DeduplicateMaterials)
            {
                DeduplicateMaterials(session);
            }
        }

        private static void DeduplicateMeshes(BuildContext context)
        {
            var renderers = context.AvatarRootObject.GetComponentsInChildren<Renderer>(true);
            var canonicalBySignature = new Dictionary<string, Mesh>(StringComparer.OrdinalIgnoreCase);
            foreach (var renderer in renderers)
            {
                var mesh = renderer is SkinnedMeshRenderer smr ? smr.sharedMesh : renderer.GetComponent<MeshFilter>()?.sharedMesh;
                if (mesh == null || !mesh.name.StartsWith("ATO_", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var signature = BuildMeshSignature(mesh);
                if (!canonicalBySignature.TryGetValue(signature, out var canonical))
                {
                    canonicalBySignature.Add(signature, mesh);
                    continue;
                }

                if (canonical == mesh)
                {
                    continue;
                }

                if (renderer is SkinnedMeshRenderer skinnedMeshRenderer)
                {
                    skinnedMeshRenderer.sharedMesh = canonical;
                }
                else if (renderer.TryGetComponent<MeshFilter>(out var meshFilter))
                {
                    meshFilter.sharedMesh = canonical;
                }
            }
        }

        private static void DeduplicateTextures(BuildContext context, AtoSessionState session)
        {
            var renderers = context.AvatarRootObject.GetComponentsInChildren<Renderer>(true);
            var materials = renderers.SelectMany(renderer => renderer.sharedMaterials).Where(material => material != null).Distinct().ToList();
            var sourceCache = new Dictionary<Texture2D, Texture2D>();
            var canonicalBySignature = new Dictionary<string, Texture2D>(StringComparer.OrdinalIgnoreCase);

            foreach (var material in materials)
            {
                var shader = material.shader;
                if (shader == null)
                {
                    continue;
                }

                var propertyCount = ShaderUtil.GetPropertyCount(shader);
                for (var i = 0; i < propertyCount; i++)
                {
                    if (ShaderUtil.GetPropertyType(shader, i) != ShaderUtil.ShaderPropertyType.TexEnv)
                    {
                        continue;
                    }

                    var propertyName = ShaderUtil.GetPropertyName(shader, i);
                    if (material.GetTexture(propertyName) is not Texture2D texture || !texture.name.StartsWith("ATO_", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var readable = GetReadableTexture(texture, sourceCache);
                    var signature = BuildTextureSignature(texture, readable);
                    if (!canonicalBySignature.TryGetValue(signature, out var canonical))
                    {
                        canonicalBySignature.Add(signature, texture);
                        continue;
                    }

                    if (canonical != texture)
                    {
                        material.SetTexture(propertyName, canonical);
                    }
                }
            }
        }

        private static void DeduplicateMaterials(AtoSessionState session)
        {
            var renderers = session.Component.gameObject.GetComponentsInChildren<Renderer>(true);
            var canonicalBySignature = new Dictionary<string, Material>(StringComparer.OrdinalIgnoreCase);

            foreach (var renderer in renderers)
            {
                var materials = renderer.sharedMaterials;
                var changed = false;
                for (var i = 0; i < materials.Length; i++)
                {
                    var material = materials[i];
                    if (material == null)
                    {
                        continue;
                    }

                    var signature = BuildMaterialSignature(material);
                    if (!canonicalBySignature.TryGetValue(signature, out var canonical))
                    {
                        canonicalBySignature.Add(signature, material);
                        continue;
                    }

                    if (canonical != material)
                    {
                        materials[i] = canonical;
                        changed = true;
                    }
                }

                if (changed)
                {
                    renderer.sharedMaterials = materials;
                }
            }
        }

        private static string BuildMeshSignature(Mesh mesh)
        {
            using var sha = SHA256.Create();
            var builder = new StringBuilder();
            builder.Append(mesh.vertexCount).Append('|').Append(mesh.subMeshCount).Append('|');
            foreach (var vertex in mesh.vertices)
            {
                builder.Append(vertex.x).Append(',').Append(vertex.y).Append(',').Append(vertex.z).Append(';');
            }
            for (var uvChannel = 0; uvChannel < 8; uvChannel++)
            {
                var uvs = new List<Vector2>();
                mesh.GetUVs(uvChannel, uvs);
                builder.Append("|uv").Append(uvChannel).Append(':').Append(uvs.Count).Append(':');
                foreach (var uv in uvs)
                {
                    builder.Append(uv.x).Append(',').Append(uv.y).Append(';');
                }
            }
            for (var subMesh = 0; subMesh < mesh.subMeshCount; subMesh++)
            {
                builder.Append("|tri").Append(subMesh).Append(':');
                foreach (var index in mesh.GetTriangles(subMesh))
                {
                    builder.Append(index).Append(',');
                }
            }
            var bytes = Encoding.UTF8.GetBytes(builder.ToString());
            var hash = sha.ComputeHash(bytes);
            var hex = new StringBuilder(hash.Length * 2);
            foreach (var b in hash)
            {
                hex.Append(b.ToString("x2"));
            }
            return hex.ToString();
        }

        private static string BuildTextureSignature(Texture2D texture, Texture2D readable)
        {
            var buid('|').Append(texture.height).Append('|')
                .Append(texture.wrapMode).Append('|').Append(texture.filterMode).Append('|').Append(texture.mipmapCount).Append('|');

            using var sha = SHA256.Create();
            var pixels = readable.GetPixels32();
            var bytes = new byte[pixels.Length * 4];
            for (var i = 0; i < pixels.Length; i++)
            {
                var color = pixels[i];
                var offset = i * 4;
                bytes[offset] = color.r;
                bytes[offset + 1] = color.g;
                bytes[offset + 2] = color.b;
                bytes[offset + 3] = color.a;
            }
            var hash = sha.ComputeHash(bytes);
            foreach (var b in hash)
            {
                builder.Append(b.ToString("x2"));
            }
            return builder.ToString();
        }

        private static string BuildMaterialSignature(Material material)
        {
            var builder = new StringBuilder();
            builder.Append(material.shader != null ? material.shader.name : "<null-shader>")
                .Append('|').Append(material.renderQueue)
                .Append('|').Append(string.Join(",", material.shaderKeywords.OrderBy(keyword => keyword, StringComparer.OrdinalIgnoreCase)));

            var shader = material.shader;
            if (shader == null)
            {
                return builder.ToString();
            }

            var propertyCount = ShaderUtil.GetPropertyCount(shader);
            for (var i = 0; i < propertyCount; i++)
            {
                var propertyName = ShaderUtil.GetPropertyName(shader, i);
                builder.Append('|').Append(propertyName).Append('=');
                switch (ShaderUtil.GetPropertyType(shader, i))
                {
                    case ShaderUtil.ShaderPropertyType.Color:
                        builder.Append(material.GetColor(propertyName));
                        break;
                    case ShaderUtil.ShaderPropertyType.Vector:
                        builder.Append(material.GetVector(propertyName));
                        break;
                    case ShaderUtil.ShaderPropertyType.Float:
                    case ShaderUtil.ShaderPropertyType.Range:
                        builder.Append(material.GetFloat(propertyName));
                        break;
                    case ShaderUtil.ShaderPropertyType.TexEnv:
                        var texture = material.GetTexture(propertyName);
                        builder.Append(texture != null ? texture.name : "<null>")
                            .Append('@').Append(material.GetTextureScale(propertyName))
                            .Append('@').Append(material.GetTextureOffset(propertyName));
                        break;
                }
            }

            return builder.ToString();
        }

        private static string MakeGeneratedName(string sourceName, string suffix)
        {
            sourceName ??= "Generated";
            suffix ??= "asset";
            return $"ATO_{SanitizeName(sourceName)}_{SanitizeName(suffix)}";
        }

        private static string SanitizeName(string value)
        {
            var chars = value.Select(ch => char.IsLetterOrDigit(ch) ? ch : '_').ToArray();
            return new string(chars);
        }

        private readonly struct LayoutRef
        {
            public readonly int AtlasIndex;
            public readonly AtoAtlasItemPlan Item;

            public LayoutRef(int atlasIndex, AtoAtlasItemPlan item)
            {
                AtlasIndex = atlasIndex;
                Item = item;
            }
        }
    }
}
