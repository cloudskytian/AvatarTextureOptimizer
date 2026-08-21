using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using nadena.dev.ndmf;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// Shared analyzer entry point for the current milestone.
    /// 当前里程碑的共享分析入口。
    /// </summary>
    internal static class AtoScanner
    {
        public static void Collect(BuildContext context, AtoSessionState session)
        {
            session.ScanResult = new AtoScanResult();
            var animationIndex = ScanAnimationClips(context, session, out var animatedAreaScaleByPath);
            ScanRenderersAndMaterials(context, session, animationIndex, animatedAreaScaleByPath);
            DetectPotentialDuplicates(session);
        }

        private static Dictionary<string, HashSet<string>> ScanAnimationClips(BuildContext context, AtoSessionState session, out Dictionary<string, float> animatedAreaScaleByPath)
        {
            var result = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            animatedAreaScaleByPath = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
            var clips = AnimationUtility.GetAnimationClips(context.AvatarRootObject)
                .Where(clip => clip != null)
                .Distinct()
                .OrderBy(clip => clip.name, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            session.Report.AnimationClipCount = clips.Length;

            foreach (var clip in clips)
            {
                var record = new AtoAnimationClipRecord
                {
                    Clip = clip,
                    AssetPath = clip.SafeAssetPath(),
                };
                var scaleExtentsByPath = new Dictionary<string, ScaleExtents>(StringComparer.OrdinalIgnoreCase);

                foreach (var binding in AnimationUtility.GetCurveBindings(clip))
                {
                    record.CurveBindingCount++;
                    if (!result.TryGetValue(binding.path, out var properties))
                    {
                        properties = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        result.Add(binding.path, properties);
                    }

                    properties.Add(binding.propertyName);

                    if (binding.propertyName.Equals("m_IsActive", StringComparison.OrdinalIgnoreCase))
                    {
                        record.ActivationBindingCount++;
                        record.AnimatedRendererPaths.Add(binding.path);
                    }

                    if (binding.propertyName.IndexOf("material.", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        record.MaterialBindingCount++;
                        record.AnimatedMaterialProperties.Add(binding.propertyName);
                    }

                    if (binding.propertyName.Equals("m_Enabled", StringComparison.OrdinalIgnoreCase))
                    {
                        record.AnimatedRendererPaths.Add(binding.path);
                    }

                    if (binding.propertyName.StartsWith("m_LocalScale.", StringComparison.OrdinalIgnoreCase))
                    {
                        var curve = AnimationUtility.GetEditorCurve(clip, binding);
                        if (curve == null)
                        {
                            continue;
                        }

                        if (!scaleExtentsByPath.TryGetValue(binding.path, out var extents))
                        {
                            extents = ScaleExtents.Default;
                        }

                        extents.Absorb(binding.propertyName, curve);
                        scaleExtentsByPath[binding.path] = extents;
                    }
                }

                foreach (var binding in AnimationUtility.GetObjectReferenceCurveBindings(clip))
                {
                    record.ObjectReferenceBindingCount++;
                    if (!result.TryGetValue(binding.path, out var properties))
                    {
                        properties = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        result.Add(binding.path, properties);
                    }

                    properties.Add(binding.propertyName);

                    if (binding.propertyName.IndexOf("material", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        record.MaterialBindingCount++;
                        record.AnimatedMaterialProperties.Add(binding.propertyName);
                    }
                }

                foreach (var pair in scaleExtentsByPath)
                {
                    var areaScale = pair.Value.ComputeAreaScale();
                    if (!animatedAreaScaleByPath.TryGetValue(pair.Key, out var existing) || areaScale > existing)
                    {
                        animatedAreaScaleByPath[pair.Key] = areaScale;
                    }
                }

                session.ScanResult.AnimationClips.Add(record);
            }

            return result;
        }

        private static void ScanRenderersAndMaterials(BuildContext context, AtoSessionState session, Dictionary<string, HashSet<string>> animationIndex, Dictionary<string, float> animatedAreaScaleByPath)
        {
            var whitelist = new HashSet<Object>(session.Component.Whitelist.Where(x => x != null));
            var uniqueMaterials = new HashSet<Material>();
            var uniqueTextures = new HashSet<Texture>();

            var renderers = context.AvatarRootObject
                .GetComponentsInChildren<Renderer>(true)
                .Where(renderer => renderer != null && !renderer.gameObject.IsEditorOnly())
                .OrderBy(renderer => renderer.transform.HierarchyPath(), StringComparer.OrdinalIgnoreCase)
                .ToArray();

            session.Report.RendererCount = renderers.Length;

            foreach (var renderer in renderers)
            {
                var relativePath = RelativePathFromRoot(session.Component.transform, renderer.transform);
                var rendererRecord = new AtoRendererRecord
                {
                    Renderer = renderer,
                    Path = renderer.transform.HierarchyPath(),
                    ActiveSelf = renderer.gameObject.activeSelf,
                    ActiveInHierarchy = renderer.gameObject.activeInHierarchy,
                    RendererEnabled = renderer.enabled,
                    MaterialSlotCount = renderer.sharedMaterials?.Length ?? 0,
                    IsSkinnedMeshRenderer = renderer is SkinnedMeshRenderer,
                    SharedMesh = GetSharedMesh(renderer),
                    LossyScale = renderer.transform.lossyScale,
                    AnimatedAreaScaleFactor = animatedAreaScaleByPath.TryGetValue(relativePath, out var animatedAreaScale) ? animatedAreaScale : 1.0f,
                };
                session.ScanResult.Renderers.Add(rendererRecord);
                session.Report.MaterialSlotCount += rendererRecord.MaterialSlotCount;

                var materials = renderer.sharedMaterials ?? Array.Empty<Material>();
                for (var materialSlotIndex = 0; materialSlotIndex < materials.Length; materialSlotIndex++)
                {
                    var material = materials[materialSlotIndex];
                    if (material == null)
                    {
                        continue;
                    }

                    uniqueMaterials.Add(material);
                    ScanMaterial(rendererRecord, material, materialSlotIndex, whitelist, animationIndex, session, uniqueTextures);
                }
            }

            session.Report.MaterialCount = uniqueMaterials.Count;
            session.Report.UniqueTextureCount = uniqueTextures.Count;
            session.Report.TextureCandidateCount = session.ScanResult.TextureUsages.Count;
            session.Report.UvIslandCount = session.ScanResult.UvGroups.Sum(group => group.Islands.Count);
        }

        private static void ScanMaterial(
            AtoRendererRecord rendererRecord,
            Material material,
            int materialSlotIndex,
            HashSet<Object> whitelist,
            Dictionary<string, HashSet<string>> animationIndex,
            AtoSessionState session,
            HashSet<Texture> uniqueTextures)
        {
            var renderer = rendererRecord.Renderer;
            var shader = material.shader;
            if (shader == null)
            {
                return;
            }

            animationIndex.TryGetValue(RelativePathFromRoot(session.Component.transform, renderer.transform), out var animatedProperties);
            animatedProperties ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var propertyCount = ShaderUtil.GetPropertyCount(shader);
            for (var i = 0; i < propertyCount; i++)
            {
                if (ShaderUtil.GetPropertyType(shader, i) != ShaderUtil.ShaderPropertyType.TexEnv)
                {
                    continue;
                }

                var propertyName = ShaderUtil.GetPropertyName(shader, i);
                var texture = material.GetTexture(propertyName);
                if (texture == null)
                {
                    continue;
                }

                uniqueTextures.Add(texture);
                var assetPath = AssetDatabase.GetAssetPath(texture);
                var importer = AssetImporter.GetAtPath(assetPath) as TextureImporter;
                var uvChannel = ResolveUvChannel(material, propertyName);
                var uvGroup = ResolveUvGroup(session.ScanResult, rendererRecord, materialSlotIndex, uvChannel);

                var record = new AtoTextureUsageRecord
                {
                    SourceObject = renderer,
                    Renderer = renderer,
                    Material = material,
                    Texture = texture,
                    RendererPath = rendererRecord.Path,
                    MaterialPath = material.SafeAssetPath(),
                    TexturePath = texture.SafeAssetPath(),
                    MaterialProperty = propertyName,
                    UvGroupKey = uvGroup.Key,
                    Scale = material.GetTextureScale(propertyName),
                    Offset = material.GetTextureOffset(propertyName),
                    WrapModeU = texture.wrapModeU,
                    WrapModeV = texture.wrapModeV,
                    FilterMode = texture.filterMode,
                    MaterialSlotIndex = materialSlotIndex,
                    UvChannel = uvChannel,
                    IsTexture2D = texture is Texture2D,
                    IsAnimatedProperty = IsAnimatedMaterialProperty(animatedProperties, propertyName),
                    IsAnimatedSt = IsAnimatedSt(animatedProperties, propertyName),
                    IsAnimatedMaterialReference = IsAnimatedMaterialReference(animatedProperties, materialSlotIndex),
                    IsPotentiallyActive = rendererRecord.PotentiallyActive,
                    IsWhitelisted = IsWhitelisted(renderer, material, texture, whitelist),
                    UsesIdentityTransform = ApproximatelyIdentity(material.GetTextureScale(propertyName), material.GetTextureOffset(propertyName)),
                    MayOverflowUvRange = texture.wrapModeU == TextureWrapMode.Repeat || texture.wrapModeV == TextureWrapMode.Repeat,
                    HasUvData = uvGroup.HasData,
                    UvInUnitSquare = uvGroup.InUnitSquareAlready,
                    UvCanTranslateIntoUnitSquare = uvGroup.CanTranslateIntoUnitSquare,
                    Semantic = InferSemantic(material, propertyName, importer),
                    ImporterFingerprint = BuildImporterFingerprint(importer, texture),
                    ContentFingerprint = BuildContentFingerprint(assetPath),
                    SourceBytes = SafeFileSize(assetPath),
                };

                uvGroup.Usages.Add(record);
                record.Decision = Decide(record, session.Report, material, texture);
                session.Report.TextureSourceBytes += record.SourceBytes;
                session.ScanResult.TextureUsages.Add(record);
            }
        }

        private static AtoTextureDecision Decide(AtoTextureUsageRecord record, AtoBuildReport report, Material material, Texture texture)
        {
            if (record.IsWhitelisted)
            {
                report.WhitelistHitCount++;
                record.DecisionReason = "Explicit whitelist reference matched renderer, material, texture, or ancestor object.";
                return AtoTextureDecision.ExplicitWhitelist;
            }

            if (!record.IsPotentiallyActive)
            {
                report.UnsupportedCount++;
                report.AddWarning($"Fallback: {record.RendererPath} -> renderer is disabled/inactive and not observed as animation-enabled in the current milestone analysis.");
                record.DecisionReason = "Renderer is not considered active or animation-enabled in the current milestone safe subset.";
                return AtoTextureDecision.SafeFallback;
            }

            if (!record.IsTexture2D)
            {
                report.UnsupportedCount++;
                report.AddWarning($"Fallback: {record.RendererPath} -> {record.Material.name}/{record.MaterialProperty} uses non-Texture2D asset {record.Texture.name}.");
                record.DecisionReason = "Only Texture2D is currently in the proven safe subset.";
                return AtoTextureDecision.SafeFallback;
            }

            if (record.IsAnimatedMaterialReference)
            {
                report.UnsupportedCount++;
                report.AddWarning($"Fallback: {record.RendererPath} -> slot {record.MaterialSlotIndex} has animated material reference changes.");
                record.DecisionReason = "Animated material reference changes are outside the current milestone safe subset.";
                return AtoTextureDecision.SafeFallback;
            }

            if (!record.UsesIdentityTransform || record.IsAnimatedProperty || record.IsAnimatedSt)
            {
                report.UnsupportedCount++;
                report.AddWarning($"Fallback: {record.RendererPath} -> {record.Material.name}/{record.MaterialProperty} has animated or non-identity ST.");
                record.DecisionReason = "Material ST animation / transform is outside the current milestone safe subset.";
                return AtoTextureDecision.SafeFallback;
            }

            if (!record.HasUvData)
            {
                report.UnsupportedCount++;
                report.AddWarning($"Fallback: {record.RendererPath} -> {record.Material.name}/{record.MaterialProperty} has no readable UV data for uv{record.UvChannel}.");
                record.DecisionReason = "Missing UV data is outside the current milestone safe subset.";
                return AtoTextureDecision.SafeFallback;
            }

            if (!record.UvInUnitSquare && !record.UvCanTranslateIntoUnitSquare)
            {
                report.UnsupportedCount++;
                report.AddWarning($"Fallback: {record.RendererPath} -> {record.Material.name}/{record.MaterialProperty} requires unsupported overflow or wrap-dependent UV behavior on uv{record.UvChannel}.");
                record.DecisionReason = "Out-of-range UVs that cannot be translated back into [0,1] are currently treated as safe fallback.";
                return AtoTextureDecision.SafeFallback;
            }

            if (record.Semantic == AtoTextureSemantic.Unknown)
            {
                report.UnsupportedCount++;
                report.AddWarning($"Fallback: {record.RendererPath} -> {record.Material.name}/{record.MaterialProperty} has unknown semantic role.");
                record.DecisionReason = "Unknown shader texture semantic currently falls back for safety.";
                return AtoTextureDecision.SafeFallback;
            }

            report.AddDetail($"Candidate: {record.RendererPath} | slot {record.MaterialSlotIndex} | {material.name} | {record.MaterialProperty} | {texture.name} | semantic={record.Semantic} | uv{record.UvChannel} | uvGroup={record.UvGroupKey} | unitSquare={record.UvInUnitSquare} | translatable={record.UvCanTranslateIntoUnitSquare}.");
            record.DecisionReason = "Inside the current analysis milestone candidate subset.";
            return AtoTextureDecision.Candidate;
        }

        private static void DetectPotentialDuplicates(AtoSessionState session)
        {
            var groups = session.ScanResult.TextureUsages
                .Where(x => x.Texture != null)
                .Where(x => !string.Equals(x.ContentFingerprint, "nofile", StringComparison.OrdinalIgnoreCase))
                .Where(x => !string.Equals(x.ContentFingerprint, "hash-error", StringComparison.OrdinalIgnoreCase))
                .GroupBy(x => $"{x.ImporterFingerprint}::{x.ContentFingerprint}")
                .Where(group => group.Count() > 1)
                .OrderByDescending(group => group.Count())
                .ToArray();

            foreach (var group in groups)
            {
                var duplicateGroup = new AtoDuplicateTextureGroup { Fingerprint = group.Key };
                duplicateGroup.Members.AddRange(group);
                session.ScanResult.DuplicateGroups.Add(duplicateGroup);
            }

            session.Report.PotentialDuplicateGroupCount = session.ScanResult.DuplicateGroups.Count;
            if (session.Report.PotentialDuplicateGroupCount > 0)
            {
                AtoIssues.ReportWarning(session.Component, "Warnings:PotentialDuplicateTextures", session.Component.gameObject);
            }
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

        private static AtoUvGroupRecord ResolveUvGroup(AtoScanResult scanResult, AtoRendererRecord rendererRecord, int materialSlotIndex, int uvChannel)
        {
            var key = $"{rendererRecord.Path}|slot{materialSlotIndex}|uv{uvChannel}";
            var existing = scanResult.UvGroups.FirstOrDefault(group => string.Equals(group.Key, key, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                return existing;
            }

            var group = new AtoUvGroupRecord
            {
                Key = key,
                Renderer = rendererRecord.Renderer,
                Mesh = rendererRecord.SharedMesh,
                MaterialSlotIndex = materialSlotIndex,
                UvChannel = uvChannel,
                AnimatedAreaScaleFactor = rendererRecord.AnimatedAreaScaleFactor,
            };

            AnalyzeUvChannel(group);
            scanResult.UvGroups.Add(group);
            return group;
        }

        private static void AnalyzeUvChannel(AtoUvGroupRecord group)
        {
            if (group.Mesh == null || group.MaterialSlotIndex < 0 || group.MaterialSlotIndex >= group.Mesh.subMeshCount)
            {
                group.HasData = false;
                return;
            }

            group.Islands.Clear();
            group.Islands.AddRange(AtoMeshAlgorithms.ExtractIslands(group.Mesh, group.MaterialSlotIndex, group.UvChannel, out var totalObjectArea, out var totalUvArea));
            group.TotalObjectSpaceArea = totalObjectArea;
            group.TotalUvArea = totalUvArea;
            if (group.Islands.Count == 0)
            {
                group.HasData = false;
                return;
            }

            var min = group.Islands[0].Min;
            var max = group.Islands[0].Max;
            for (var i = 1; i < group.Islands.Count; i++)
            {
                min = Vector2.Min(min, group.Islands[i].Min);
                max = Vector2.Max(max, group.Islands[i].Max);
            }

            group.HasData = true;
            group.Min = min;
            group.Max = max;
            group.Span = max - min;
            group.InUnitSquareAlready = min.x >= 0.0f && min.y >= 0.0f && max.x <= 1.0f && max.y <= 1.0f;

            var translation = new Vector2(-Mathf.Floor(min.x), -Mathf.Floor(min.y));
            var translatedMin = min + translation;
            var translatedMax = max + translation;
            const float epsilon = 0.0001f;
            group.Translation = translation;
            group.CanTranslateIntoUnitSquare = group.Span.x <= 1.0f + epsilon
                                              && group.Span.y <= 1.0f + epsilon
                                              && translatedMin.x >= -epsilon
                                              && translatedMin.y >= -epsilon
                                              && translatedMax.x <= 1.0f + epsilon
                                              && translatedMax.y <= 1.0f + epsilon;
        }

        private static Mesh GetSharedMesh(Renderer renderer)
        {
            if (renderer is SkinnedMeshRenderer smr)
            {
                return smr.sharedMesh;
            }

            if (renderer.TryGetComponent<MeshFilter>(out var filter))
            {
                return filter.sharedMesh;
            }

            return null;
        }

        private static bool ApproximatelyIdentity(Vector2 scale, Vector2 offset)
        {
            return Mathf.Approximately(scale.x, 1.0f)
                   && Mathf.Approximately(scale.y, 1.0f)
                   && Mathf.Approximately(offset.x, 0.0f)
                   && Mathf.Approximately(offset.y, 0.0f);
        }

        private static bool IsAnimatedMaterialProperty(HashSet<string> animatedProperties, string propertyName)
        {
            return animatedProperties.Contains($"material.{propertyName}")
                   || animatedProperties.Any(p => p.IndexOf($"material.{propertyName}", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static bool IsAnimatedSt(HashSet<string> animatedProperties, string propertyName)
        {
            var st = $"material.{propertyName}_ST";
            return animatedProperties.Any(p => p.IndexOf(st, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static bool IsAnimatedMaterialReference(HashSet<string> animatedProperties, int materialSlotIndex)
        {
            var slotPath = $"m_Materials.Array.data[{materialSlotIndex}]";
            return animatedProperties.Any(p => p.IndexOf(slotPath, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static bool IsWhitelisted(Renderer renderer, Material material, Texture texture, HashSet<Object> whitelist)
        {
            if (whitelist.Contains(renderer)
                || whitelist.Contains(renderer.gameObject)
                || whitelist.Contains(material)
                || whitelist.Contains(texture))
            {
                return true;
            }

            var current = renderer.transform;
            while (current != null)
            {
                if (whitelist.Contains(current.gameObject))
                {
                    return true;
                }

                current = current.parent;
            }

            return false;
        }

        private static int ResolveUvChannel(Material material, string propertyName)
        {
            foreach (var provider in AtoExtensionRegistry.RegisteredShaderSemanticProviders)
            {
                if (provider.TryDescribe(material, propertyName, out var description))
                {
                    return Mathf.Clamp(description.UvChannel, 0, 7);
                }
            }

            return 0;
        }

        private static AtoTextureSemantic InferSemantic(Material material, string propertyName, TextureImporter importer)
        {
            foreach (var provider in AtoExtensionRegistry.RegisteredShaderSemanticProviders)
            {
                if (provider.TryDescribe(material, propertyName, out var description))
                {
                    return description.Semantic;
                }
            }

            var normalized = propertyName?.ToLowerInvariant() ?? string.Empty;
            if (normalized.Contains("bump") || normalized.Contains("normal"))
            {
                return AtoTextureSemantic.Normal;
            }

            if (normalized.Contains("mask") || normalized.Contains("metal") || normalized.Contains("occlusion") || normalized.Contains("shadow") || normalized.Contains("parallax"))
            {
                return AtoTextureSemantic.Mask;
            }

            if (normalized.Contains("rough") || normalized.Contains("smooth") || normalized.Contains("gloss") || normalized.Contains("ao"))
            {
                return AtoTextureSemantic.Grayscale;
            }

            if (normalized.Contains("main") || normalized.Contains("base") || normalized.Contains("albedo") || normalized.Contains("emission") || normalized.Contains("color"))
            {
                return AtoTextureSemantic.Color;
            }

            if (importer != null && importer.textureType == TextureImporterType.NormalMap)
            {
                return AtoTextureSemantic.Normal;
            }

            return AtoTextureSemantic.Unknown;
        }

        private static string BuildImporterFingerprint(TextureImporter importer, Texture texture)
        {
            if (importer == null)
            {
                return $"builtin::{texture.width}x{texture.height}::{texture.filterMode}::{texture.wrapModeU}/{texture.wrapModeV}";
            }

            return string.Join("|",
                importer.textureType,
                importer.sRGBTexture,
                importer.alphaSource,
                importer.alphaIsTransparency,
                importer.mipmapEnabled,
                importer.streamingMipmaps,
                importer.filterMode,
                importer.wrapModeU,
                importer.wrapModeV,
                importer.anisoLevel,
                importer.textureCompression,
                importer.crunchedCompression,
                importer.compressionQuality,
                texture.width,
                texture.height);
        }

        private static string BuildContentFingerprint(string assetPath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(assetPath) || !File.Exists(assetPath))
                {
                    return "nofile";
                }

                using var stream = File.OpenRead(assetPath);
                using var sha = SHA256.Create();
                var hash = sha.ComputeHash(stream);
                var builder = new StringBuilder(hash.Length * 2);
                foreach (var b in hash)
                {
                    builder.Append(b.ToString("x2"));
                }

                return builder.ToString();
            }
            catch
            {
                return "hash-error";
            }
        }

        private static long SafeFileSize(string assetPath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(assetPath) || !File.Exists(assetPath))
                {
                    return 0;
                }

                return new FileInfo(assetPath).Length;
            }
            catch
            {
                return 0;
            }
        }

        private struct ScaleExtents
        {
            public float MaxX;
            public float MaxY;
            public float MaxZ;

            public static ScaleExtents Default => new ScaleExtents { MaxX = 1.0f, MaxY = 1.0f, MaxZ = 1.0f };

            public void Absorb(string propertyName, AnimationCurve curve)
            {
                var maxAbs = 1.0f;
                foreach (var key in curve.keys)
                {
                    maxAbs = Mathf.Max(maxAbs, Mathf.Abs(key.value));
                }

                if (propertyName.EndsWith(".x", StringComparison.OrdinalIgnoreCase))
                {
                    MaxX = Mathf.Max(MaxX, maxAbs);
                }
                else if (propertyName.EndsWith(".y", StringComparison.OrdinalIgnoreCase))
                {
                    MaxY = Mathf.Max(MaxY, maxAbs);
                }
                else if (propertyName.EndsWith(".z", StringComparison.OrdinalIgnoreCase))
                {
                    MaxZ = Mathf.Max(MaxZ, maxAbs);
                }
            }

            public float ComputeAreaScale()
            {
                var values = new[] { Mathf.Max(1.0f, MaxX), Mathf.Max(1.0f, MaxY), Mathf.Max(1.0f, MaxZ) }
                    .OrderByDescending(v => v)
                    .ToArray();
                return values[0] * values[1];
            }
        }
    }
}
