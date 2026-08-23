using System;
using System.Collections.Generic;
using System.Linq;
using Fosa.AvatarTextureOptimizer.Editor.Core;
using Fosa.AvatarTextureOptimizer.Editor.Reporting;
using Fosa.AvatarTextureOptimizer.Editor.Processing;
using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;
using UnityEditor;
using UnityEngine;

namespace Fosa.AvatarTextureOptimizer.Editor.Analysis
{
    /// <summary>EN: Creates the complete conservative avatar texture analysis plan. ZH: 创建完整且保守的 Avatar 贴图分析计划。</summary>
    internal static class AvatarAnalyzer
    {
        public static BuildPlan Analyze(BuildContext context,
            Fosa.AvatarTextureOptimizer.AvatarTextureOptimizer component, OptimizerPlatform platform,
            BuildProgress progress, ResourceScope resources, AtoBuildReport report)
        {
            ShaderAnalyzer.BeginAnalysis();
            var plan = new BuildPlan
            {
                Component = component,
                Platform = platform,
                Profile = component.settings.Resolve(platform),
            };
            plan.Profile.Validate(platform);
            plan.ProtectedTextures.UnionWith(WhitelistResolver.Resolve(component.settings.whitelist));

            var animation = AnimationAnalyzer.Analyze(context, progress);
            CollectRenderers(context.AvatarRootTransform, animation, plan, report);
            CloneMaterials(context, animation, plan);
            AnalyzeMaterials(context.AvatarRootTransform, animation, plan, report);
            AddAnimatedTextureVariants(context.AvatarRootTransform, animation, plan);
            ProtectComponentTextureReferences(context.AvatarRootObject, plan, report);

            var allTextures = plan.Materials.Values.SelectMany(x => x.Usages).Select(x => x.Texture)
                .Concat(animation.AnimatedTextures.Select(x => x.Texture)).Where(x => x != null).Distinct().ToList();
            report.SourceTextureCount = allTextures.Count;

            if (component.settings.deduplicateTextures)
            {
                var dedupe = TextureDeduplicator.Deduplicate(context, allTextures, plan.ProtectedTextures,
                    progress, resources, report);
                ApplyTextureDedupe(plan, dedupe);
            }

            var allUsages = plan.Materials.Values.SelectMany(x => x.Usages).ToList();
            foreach (var semanticConflict in allUsages.GroupBy(x => x.Texture)
                         .Where(x => x.Select(u => u.Semantic).Distinct().Count() > 1))
            {
                plan.ProtectedTextures.Add(semanticConflict.Key);
                report.Warn($"Texture '{semanticConflict.Key.name}' is used with incompatible semantic types; it was protected to preserve every use.", semanticConflict.Key);
            }
            foreach (var usage in allUsages)
            {
                if (!usage.Safe) plan.ProtectedTextures.Add(usage.Texture);
                usage.Protected = plan.ProtectedTextures.Contains(usage.Texture) || !usage.Safe;
            }

            report.RendererCount = plan.Renderers.Count;
            report.MaterialCount = plan.Materials.Count;
            return plan;
        }

        private static void CollectRenderers(Transform root, AnimationSnapshot animation, BuildPlan plan, AtoBuildReport report)
        {
            foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
            {
                if (!(renderer is SkinnedMeshRenderer) && !(renderer is MeshRenderer)) continue;
                if (IsEditorOnly(renderer.transform, root)) continue;
                var path = RelativePath(root, renderer.transform);
                if (renderer.HasPropertyBlock())
                {
                    report.Warn($"Renderer '{path}' uses a MaterialPropertyBlock; it was skipped because texture overrides cannot be proven safe.", renderer);
                    continue;
                }
                if (!MayBecomeEnabled(renderer, path, animation)) continue;
                var mesh = GetMesh(renderer);
                if (mesh == null || !mesh.isReadable)
                {
                    report.Warn($"Renderer '{path}' has no readable mesh and was skipped.", renderer);
                    continue;
                }

                var record = new RendererRecord
                {
                    Renderer = renderer,
                    SourceMesh = mesh,
                    MaximumAreaScale = ComputeMaximumAreaScale(root, renderer, animation),
                };
                var materials = renderer.sharedMaterials;
                for (var slot = 0; slot < Math.Min(mesh.subMeshCount, materials.Length); slot++)
                {
                    var set = new HashSet<Material>();
                    if (materials[slot] != null) set.Add(materials[slot]);
                    if (animation.SlotMaterials.TryGetValue(new RendererSlot(renderer, slot), out var animated)) set.UnionWith(animated);
                    record.PossibleMaterials[slot] = set;
                }
                plan.Renderers.Add(record);
            }
        }

        private static void CloneMaterials(BuildContext context, AnimationSnapshot animation, BuildPlan plan)
        {
            var originals = plan.Renderers.SelectMany(x => x.PossibleMaterials.Values).SelectMany(x => x)
                .Where(x => x != null).Distinct().ToList();
            foreach (var original in originals)
            {
                var clone = UnityEngine.Object.Instantiate(original);
                clone.name = original.name + "_ATO";
                plan.Materials[original] = new MaterialRecord { Original = original, Working = clone };
                plan.MaterialReplacements[original] = clone;
                ObjectRegistry.RegisterReplacedObject(original, clone);
            }

            foreach (var rendererRecord in plan.Renderers)
            {
                var materials = rendererRecord.Renderer.sharedMaterials;
                for (var i = 0; i < materials.Length; i++)
                    if (materials[i] != null && plan.MaterialReplacements.TryGetValue(materials[i], out var replacement)) materials[i] = replacement;
                rendererRecord.Renderer.sharedMaterials = materials;
            }

            context.Extension<AnimatorServicesContext>().AnimationIndex.RewriteObjectCurves(obj =>
                obj is Material material && plan.MaterialReplacements.TryGetValue(material, out var replacement) ? replacement : obj);
            SerializedReferenceRewriter.Rewrite(context.AvatarRootObject, plan.MaterialReplacements);
        }

        private static void AnalyzeMaterials(Transform root, AnimationSnapshot animation, BuildPlan plan, AtoBuildReport report)
        {
            foreach (var rendererRecord in plan.Renderers)
            foreach (var pair in rendererRecord.PossibleMaterials)
            foreach (var original in pair.Value)
            {
                if (original == null || !plan.Materials.TryGetValue(original, out var record)) continue;
                var slot = new RendererSlot(rendererRecord.Renderer, pair.Key);
                record.Slots.Add(slot);
                var path = RelativePath(root, rendererRecord.Renderer.transform);
                var usages = ShaderAnalyzer.Analyze(record.Working, rendererRecord.Renderer, pair.Key, path, animation,
                    out var materialUnsafeReason);
                if (!string.IsNullOrEmpty(materialUnsafeReason)) record.Whitelisted = true;
                foreach (var usage in usages)
                {
                    var existing = record.Usages.FirstOrDefault(x => x.PropertyName == usage.PropertyName && x.Texture == usage.Texture &&
                                                                     x.UvChannel == usage.UvChannel && x.Semantic == usage.Semantic);
                    if (existing == null) record.Usages.Add(usage);
                    else
                    {
                        existing.Renderers.Add(rendererRecord.Renderer);
                        existing.AlphaConstraints.AddRange(usage.AlphaConstraints);
                        if (!usage.Safe) existing.UnsafeReason = usage.UnsafeReason;
                    }
                    if (!usage.Safe) report.Warn($"'{original.name}' {usage.PropertyName} is protected: {usage.UnsafeReason}.", original);
                }
            }
        }

        private static void AddAnimatedTextureVariants(Transform root, AnimationSnapshot animation, BuildPlan plan)
        {
            foreach (var animated in animation.AnimatedTextures)
            {
                var transform = string.IsNullOrEmpty(animated.Binding.path) ? root : root.Find(animated.Binding.path);
                var renderer = transform != null ? transform.GetComponent<Renderer>() : null;
                if (renderer == null) { plan.ProtectedTextures.Add(animated.Texture); continue; }
                var property = NormalizeProperty(animated.Binding.propertyName);
                var rendererRecord = plan.Renderers.FirstOrDefault(x => x.Renderer == renderer);
                if (rendererRecord == null) { plan.ProtectedTextures.Add(animated.Texture); continue; }

                foreach (var original in rendererRecord.PossibleMaterials.Values.SelectMany(x => x).Distinct())
                {
                    if (original == null || !plan.Materials.TryGetValue(original, out var materialRecord)) continue;
                    var template = materialRecord.Usages.FirstOrDefault(x => x.PropertyName == property);
                    if (template == null) { plan.ProtectedTextures.Add(animated.Texture); continue; }
                    if (materialRecord.Usages.Any(x => x.PropertyName == property && x.Texture == animated.Texture)) continue;
                    var variant = CloneUsage(template, animated.Texture);
                    variant.IsAnimated = true;
                    materialRecord.Usages.Add(variant);
                }
            }
        }

        private static TextureUsage CloneUsage(TextureUsage source, Texture2D texture)
        {
            var clone = new TextureUsage
            {
                Material = source.Material,
                PropertyName = source.PropertyName,
                Texture = texture,
                Semantic = source.Semantic,
                UvChannel = source.UvChannel,
                UsedChannelMask = source.UsedChannelMask,
                FilterMode = texture.filterMode,
                IsSrgb = texture.isDataSRGB,
                UnsafeReason = source.UnsafeReason,
            };
            foreach (var renderer in source.Renderers) clone.Renderers.Add(renderer);
            clone.AlphaConstraints.AddRange(source.AlphaConstraints);
            return clone;
        }

        private static void ApplyTextureDedupe(BuildPlan plan, Dictionary<Texture2D, Texture2D> replacements)
        {
            if (replacements.Count == 0) return;
            foreach (var record in plan.Materials.Values)
            foreach (var usage in record.Usages)
            {
                if (!replacements.TryGetValue(usage.Texture, out var replacement)) continue;
                if (record.Working.GetTexture(usage.PropertyName) == usage.Texture) record.Working.SetTexture(usage.PropertyName, replacement);
                usage.Texture = replacement;
            }
        }

        private static void ProtectComponentTextureReferences(GameObject root, BuildPlan plan, AtoBuildReport report)
        {
            var candidates = plan.Materials.Values.SelectMany(x => x.Usages).Select(x => x.Texture).ToHashSet();
            foreach (var component in root.GetComponentsInChildren<Component>(true))
            {
                if (component == null || component is Renderer || component is Fosa.AvatarTextureOptimizer.AvatarTextureOptimizer) continue;
                try
                {
                    using (var serialized = new SerializedObject(component))
                    {
                        var iterator = serialized.GetIterator();
                        while (iterator.Next(true))
                        {
                            if (iterator.propertyType != SerializedPropertyType.ObjectReference) continue;
                            if (iterator.objectReferenceValue is Texture2D texture && candidates.Contains(texture))
                            {
                                if (plan.ProtectedTextures.Add(texture))
                                    report.Warn($"Texture '{texture.name}' is also referenced by component '{component.GetType().Name}' and was protected.", component);
                            }
                            else if (iterator.objectReferenceValue is Material material)
                            {
                                var record = plan.Materials.Values.FirstOrDefault(x => x.Working == material);
                                if (record == null) continue;
                                record.Whitelisted = true;
                                foreach (var usage in record.Usages) plan.ProtectedTextures.Add(usage.Texture);
                                report.Warn($"Material '{record.Original.name}' is referenced by non-renderer component '{component.GetType().Name}' and was protected.", component);
                            }
                        }
                    }
                }
                catch (Exception ex) { report.Warn($"Could not inspect component '{component.name}': {ex.Message}", component); }
            }
        }

        private static bool MayBecomeEnabled(Renderer renderer, string path, AnimationSnapshot animation)
        {
            if (renderer.enabled && renderer.gameObject.activeInHierarchy) return true;
            if (animation.PotentiallyEnabledPaths.Contains(path)) return true;
            foreach (var enabledPath in animation.PotentiallyEnabledPaths)
                if (string.IsNullOrEmpty(enabledPath) || path == enabledPath || path.StartsWith(enabledPath + "/", StringComparison.Ordinal)) return true;
            return false;
        }

        private static float ComputeMaximumAreaScale(Transform root, Renderer renderer, AnimationSnapshot animation)
        {
            var target = renderer.transform;
            var absolute = target.lossyScale;
            var factors = new Vector3(Mathf.Abs(absolute.x), Mathf.Abs(absolute.y), Mathf.Abs(absolute.z));
            for (var current = target; current != null; current = current.parent)
            {
                var path = RelativePath(root, current);
                if (!animation.MaximumLocalScale.TryGetValue(path, out var maximum)) { if (current == root) break; continue; }
                var local = current.localScale;
                factors.x *= maximum.x / Mathf.Max(1e-5f, Mathf.Abs(local.x));
                factors.y *= maximum.y / Mathf.Max(1e-5f, Mathf.Abs(local.y));
                factors.z *= maximum.z / Mathf.Max(1e-5f, Mathf.Abs(local.z));
                if (current == root) break;
            }
            var axes = new[] { factors.x, factors.y, factors.z }.OrderByDescending(x => x).ToArray();
            var areaScale = axes[0] * axes[1];
            if (renderer is SkinnedMeshRenderer skinned)
            {
                var maximumBoneScale = 1f;
                foreach (var bone in skinned.bones.Where(x => x != null))
                {
                    var path = RelativePath(root, bone);
                    if (!animation.MaximumLocalScale.TryGetValue(path, out var animated)) continue;
                    var local = bone.localScale;
                    maximumBoneScale = Mathf.Max(maximumBoneScale,
                        animated.x / Mathf.Max(1e-5f, Mathf.Abs(local.x)),
                        animated.y / Mathf.Max(1e-5f, Mathf.Abs(local.y)),
                        animated.z / Mathf.Max(1e-5f, Mathf.Abs(local.z)));
                }
                areaScale *= maximumBoneScale * maximumBoneScale;
            }
            return Mathf.Max(1e-6f, areaScale * 1.05f);
        }

        internal static string RelativePath(Transform root, Transform target)
        {
            if (target == root) return string.Empty;
            var names = new Stack<string>();
            for (var current = target; current != null && current != root; current = current.parent) names.Push(current.name);
            return string.Join("/", names);
        }

        private static string NormalizeProperty(string animationProperty)
        {
            const string prefix = "material.";
            var value = animationProperty.StartsWith(prefix, StringComparison.Ordinal) ? animationProperty.Substring(prefix.Length) : animationProperty;
            var dot = value.IndexOf('.');
            return dot > 0 ? value.Substring(0, dot) : value;
        }

        private static Mesh GetMesh(Renderer renderer)
        {
            if (renderer is SkinnedMeshRenderer skinned) return skinned.sharedMesh;
            return renderer.GetComponent<MeshFilter>()?.sharedMesh;
        }

        private static bool IsEditorOnly(Transform transform, Transform root)
        {
            for (var current = transform; current != null; current = current.parent)
            {
                if (current.CompareTag("EditorOnly")) return true;
                if (current == root) break;
            }
            return false;
        }
    }
}
