using System;
using System.Collections.Generic;
using System.Linq;
using Fosa.AvatarTextureOptimizer.Editor.Analysis;
using Fosa.AvatarTextureOptimizer.Editor.API;
using Fosa.AvatarTextureOptimizer.Editor.Core;
using Fosa.AvatarTextureOptimizer.Editor.Reporting;
using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace Fosa.AvatarTextureOptimizer.Editor.Atlas
{
    /// <summary>EN: Batched GPU atlas rendering, normal-safe rotation, and jump-flood pull-push filling. ZH: 批量 GPU 图集渲染、法线安全旋转及跳洪式 pull-push 填充。</summary>
    internal static class AtlasGenerator
    {
        private sealed class LayerDefinition
        {
            public string Property;
            public TextureSemantic Semantic;
            public readonly Dictionary<UvGroup, TextureUsage> Selection = new Dictionary<UvGroup, TextureUsage>();
            public readonly Dictionary<Material, HashSet<string>> MaterialProperties = new Dictionary<Material, HashSet<string>>();
            public readonly List<AnimatedLayerMapping> Animations = new List<AnimatedLayerMapping>();
            public string Signature(IReadOnlyList<UvGroup> ordered) => string.Join("|", ordered.Select(x =>
                Selection.TryGetValue(x, out var usage) ? usage.Texture.GetInstanceID().ToString() : "null"));
        }

        public static void Generate(BuildContext context, BuildPlan plan, BuildProgress progress,
            ResourceScope resources, AtoBuildReport report)
        {
            var shader = Shader.Find("Hidden/ATO/AtlasBlit");
            var finalizeShader = Shader.Find("Hidden/ATO/Finalize");
            var compute = Resources.Load<ComputeShader>("ATO_PullPush");
            if (shader == null || finalizeShader == null || compute == null) throw new InvalidOperationException("ATO atlas GPU resources are missing.");
            var material = resources.Own(new Material(shader) { hideFlags = HideFlags.HideAndDontSave });
            var finalizeMaterial = resources.Own(new Material(finalizeShader) { hideFlags = HideFlags.HideAndDontSave });
            var totalLayouts = plan.TypeGroups.Sum(x => x.Layouts.Count); var completed = 0;

            foreach (var typeGroup in plan.TypeGroups)
            foreach (var layout in typeGroup.Layouts)
            {
                progress.Report("Rendering atlas layers / 渲染图集层", completed++, Math.Max(1, totalLayouts));
                var uvGroups = typeGroup.UvGroups
                    .Where(x => x.Islands.Any(i => i.Placement.AtlasIndex == layout.Index)).OrderBy(x => x.Id).ToList();
                var propertyGroups = uvGroups.SelectMany(x => x.Usages.Where(u => !u.Protected))
                    .GroupBy(x => (Role: ShaderAnalyzer.CanonicalRole(x.PropertyName), x.Semantic)).ToList();
                foreach (var propertyGroup in propertyGroups)
                {
                    var relevant = uvGroups.Where(x => x.Usages.Any(u => !u.Protected &&
                        ShaderAnalyzer.CanonicalRole(u.PropertyName) == propertyGroup.Key.Role && u.Semantic == propertyGroup.Key.Semantic)).ToList();
                    var definitions = BuildDefinitions(relevant, propertyGroup.Key.Role, propertyGroup.Key.Semantic);
                    var definitionIndex = 0;
                    foreach (var definition in definitions)
                    {
                        var output = RenderLayer(material, finalizeMaterial, compute, definition, layout,
                            plan.Profile.ForSemantic(definition.Semantic).mipmapsAndStreaming);
                        output.name = $"ATO_{Sanitize(definition.Property)}_{layout.Index}_{definitionIndex++}";
                        AtoExtensionRegistry.Postprocess(output, definition.Semantic);
                        context.AssetSaver.SaveAsset(output);
                        var layer = new GeneratedTextureLayer
                        {
                            Output = output,
                            PropertyName = definition.Property,
                            Semantic = definition.Semantic,
                            AtlasIndex = layout.Index,
                            TypeGroup = typeGroup,
                        };
                        foreach (var assignment in definition.MaterialProperties)
                            layer.AssignedProperties[assignment.Key] = new HashSet<string>(assignment.Value);
                        layer.AnimatedMappings.AddRange(definition.Animations);
                        layer.Sources.UnionWith(definition.Selection.Values.Select(x => x.Texture));
                        layer.Renderers.UnionWith(definition.Selection.Keys.Select(x => x.Renderer.Renderer));
                        plan.GeneratedLayers.Add(layer);
                        report.ProcessedTextureCount++;

                        var distinctSources = definition.Selection.Values.Select(x => x.Texture).Distinct().ToList();
                        var statistic = new AtlasStatistic
                        {
                            Name = output.name,
                            Width = layout.Width,
                            Height = layout.Height,
                            IslandCount = definition.Selection.Keys.SelectMany(x => x.Islands)
                                .Count(x => x.Placement.AtlasIndex == layout.Index),
                            Utilization = layout.Utilization,
                            BeforeBytes = distinctSources.Sum(x => (long)x.width * x.height * 4),
                            AfterBytes = (long)layout.Width * layout.Height * 4,
                        };
                        statistic.Sources.AddRange(distinctSources.Select(x => x.name + ":" + definition.Property));
                        report.Atlases.Add(statistic);
                    }
                }
            }
            AssignGeneratedTextures(context, plan, report);
        }

        private static List<LayerDefinition> BuildDefinitions(IReadOnlyList<UvGroup> groups, string property,
            TextureSemantic semantic)
        {
            var baseline = new LayerDefinition { Property = property, Semantic = semantic };
            foreach (var group in groups)
            {
                var usage = group.Usages.Where(x => !x.Protected && ShaderAnalyzer.CanonicalRole(x.PropertyName) == property && x.Semantic == semantic && !x.IsAnimated)
                    .OrderBy(x => x.Material != null ? x.Material.GetInstanceID() : int.MaxValue).ThenBy(x => x.Texture.GetInstanceID()).FirstOrDefault()
                    ?? group.Usages.Where(x => !x.Protected && ShaderAnalyzer.CanonicalRole(x.PropertyName) == property && x.Semantic == semantic)
                        .OrderBy(x => x.Texture.GetInstanceID()).First();
                baseline.Selection[group] = usage;
            }

            var bySignature = new Dictionary<string, LayerDefinition>(StringComparer.Ordinal);
            void Add(LayerDefinition definition)
            {
                var signature = definition.Signature(groups);
                if (!bySignature.TryGetValue(signature, out var existing)) bySignature[signature] = definition;
                else
                {
                    foreach (var assignment in definition.MaterialProperties)
                    {
                        if (!existing.MaterialProperties.TryGetValue(assignment.Key, out var properties))
                            existing.MaterialProperties[assignment.Key] = properties = new HashSet<string>();
                        properties.UnionWith(assignment.Value);
                    }
                    foreach (var animation in definition.Animations)
                    {
                        var same = existing.Animations.FirstOrDefault(x => x.Source == animation.Source && x.PropertyName == animation.PropertyName);
                        if (same == null) existing.Animations.Add(animation); else same.Renderers.UnionWith(animation.Renderers);
                    }
                }
            }

            var roleUsages = groups.SelectMany(x => x.Usages).Where(x => !x.Protected &&
                ShaderAnalyzer.CanonicalRole(x.PropertyName) == property && x.Semantic == semantic).ToList();
            var materials = roleUsages.Select(x => x.Material).Where(x => x != null).Distinct().ToList();
            foreach (var material in materials)
            {
                var definition = Copy(baseline);
                definition.MaterialProperties[material] = roleUsages.Where(x => x.Material == material && !x.IsAnimated)
                    .Select(x => x.PropertyName).ToHashSet();
                foreach (var group in groups)
                {
                    var selected = group.Usages.FirstOrDefault(x => !x.Protected && !x.IsAnimated && x.Material == material &&
                        ShaderAnalyzer.CanonicalRole(x.PropertyName) == property && x.Semantic == semantic);
                    if (selected != null) definition.Selection[group] = selected;
                }
                Add(definition);
            }

            var animatedUsages = groups.SelectMany(x => x.Usages).Where(x => !x.Protected && x.IsAnimated &&
                ShaderAnalyzer.CanonicalRole(x.PropertyName) == property && x.Semantic == semantic).ToList();
            foreach (var source in animatedUsages.Select(x => x.Texture).Distinct())
            foreach (var renderer in animatedUsages.Where(x => x.Texture == source).SelectMany(x => x.Renderers).Distinct())
            {
                var definition = Copy(baseline);
                foreach (var group in groups.Where(x => x.Renderer.Renderer == renderer))
                {
                    var selected = group.Usages.FirstOrDefault(x => !x.Protected && x.IsAnimated && x.Texture == source &&
                        ShaderAnalyzer.CanonicalRole(x.PropertyName) == property && x.Semantic == semantic && x.Renderers.Contains(renderer));
                    if (selected != null) definition.Selection[group] = selected;
                }
                var sourceUsage = animatedUsages.First(x => x.Texture == source && x.Renderers.Contains(renderer));
                var mapping = new AnimatedLayerMapping { Source = source, PropertyName = sourceUsage.PropertyName };
                mapping.Renderers.Add(renderer); definition.Animations.Add(mapping); Add(definition);
            }

            if (bySignature.Count == 0) Add(baseline);
            return bySignature.Values.ToList();
        }

        private static LayerDefinition Copy(LayerDefinition source)
        {
            var output = new LayerDefinition { Property = source.Property, Semantic = source.Semantic };
            foreach (var pair in source.Selection) output.Selection[pair.Key] = pair.Value;
            return output;
        }

        private static Texture2D RenderLayer(Material material, Material finalizeMaterial, ComputeShader compute,
            LayerDefinition definition, AtlasLayout layout, bool mipmaps)
        {
            using (var resources = new ResourceScope())
            {
            var color = resources.Own(CreateRt(layout.Width, layout.Height, RenderTextureFormat.ARGBFloat));
            var mask = resources.Own(CreateRt(layout.Width, layout.Height, RenderTextureFormat.RFloat));
            var command = new CommandBuffer { name = "[ATO] Atlas layer" };
            var meshes = new Dictionary<UvIsland, Mesh>();
            try
            {
            command.SetViewProjectionMatrices(Matrix4x4.identity,
                GL.GetGPUProjectionMatrix(Matrix4x4.Ortho(-1f, 1f, -1f, 1f, -1f, 1f), true));
            command.SetRenderTarget(color); command.ClearRenderTarget(false, true, Color.clear);
            var propertyBlock = new MaterialPropertyBlock();
            foreach (var pair in definition.Selection)
            foreach (var island in pair.Key.Islands.Where(x => x.Placement.AtlasIndex == layout.Index))
            {
                var mesh = resources.Own(BuildIslandMesh(pair.Key, island, layout)); meshes[island] = mesh;
                propertyBlock.Clear(); propertyBlock.SetTexture("_MainTex", pair.Value.Texture);
                propertyBlock.SetVector("_MainTex_TexelSize", new Vector4(1f / pair.Value.Texture.width,
                    1f / pair.Value.Texture.height, pair.Value.Texture.width, pair.Value.Texture.height));
                propertyBlock.SetInt("_Semantic", (int)definition.Semantic);
                var sourceWidth = Mathf.Max(1, Mathf.CeilToInt(island.UvBounds.width * pair.Value.Texture.width));
                var sourceHeight = Mathf.Max(1, Mathf.CeilToInt(island.UvBounds.height * pair.Value.Texture.height));
                propertyBlock.SetInt("_AreaSample", island.TargetPixelSize.x < sourceWidth || island.TargetPixelSize.y < sourceHeight ? 1 : 0);
                command.DrawMesh(mesh, Matrix4x4.identity, material, 0, 0, propertyBlock);
            }
            command.SetRenderTarget(mask); command.ClearRenderTarget(false, true, Color.clear);
            foreach (var mesh in meshes.Values) command.DrawMesh(mesh, Matrix4x4.identity, material, 0, 1);
            Graphics.ExecuteCommandBuffer(command);
            }
            finally { command.Release(); }

            var filled = PullPush(compute, color, mask, definition.Semantic == TextureSemantic.ColorAlpha, resources);
            var sourceTextures = definition.Selection.Values.Select(x => x.Texture).ToList();
            var encodeSrgb = definition.Semantic != TextureSemantic.Normal && definition.Semantic != TextureSemantic.Grayscale &&
                             sourceTextures.All(x => x.isDataSRGB);
            var finalized = resources.Own(CreateRt(layout.Width, layout.Height, RenderTextureFormat.ARGB32));
            finalizeMaterial.SetInt("_EncodeSrgb", encodeSrgb ? 1 : 0);
            Graphics.Blit(filled, finalized, finalizeMaterial, 0);
            var output = new Texture2D(layout.Width, layout.Height, TextureFormat.RGBA32, mipmaps, !encodeSrgb)
            {
                filterMode = sourceTextures.Max(x => x.filterMode),
                wrapModeU = TextureWrapMode.Clamp,
                wrapModeV = TextureWrapMode.Clamp,
                anisoLevel = sourceTextures.Max(x => x.anisoLevel),
                mipMapBias = sourceTextures.Min(x => x.mipMapBias),
            };
            resources.Own(output);
            var previous = RenderTexture.active;
            try
            {
                RenderTexture.active = finalized;
                output.ReadPixels(new Rect(0, 0, layout.Width, layout.Height), 0, 0, false);
                output.Apply(mipmaps, false);
            }
            finally { RenderTexture.active = previous; }
            resources.Commit(output);
            return output;
            }
        }

        private static Mesh BuildIslandMesh(UvGroup group, UvIsland island, AtlasLayout layout)
        {
            var sourceUvs = new List<Vector2>(); group.Renderer.SourceMesh.GetUVs(group.UvChannel, sourceUvs);
            var vertices = new List<Vector3>(island.Triangles.Count * 3);
            var uvs = new List<Vector2>(island.Triangles.Count * 3);
            var triangles = new List<int>(island.Triangles.Count * 3);
            foreach (var triangle in island.Triangles) { Add(triangle.A); Add(triangle.B); Add(triangle.C); }
            void Add(int vertex)
            {
                var source = sourceUvs[vertex];
                var normalized = new Vector2(
                    (source.x + group.IntegerTranslation.x - island.UvBounds.x) / Mathf.Max(1e-8f, island.UvBounds.width),
                    (source.y + group.IntegerTranslation.y - island.UvBounds.y) / Mathf.Max(1e-8f, island.UvBounds.height));
                float px, py;
                if (!island.Placement.Rotated)
                { px = island.Placement.X + normalized.x * island.TargetPixelSize.x; py = island.Placement.Y + normalized.y * island.TargetPixelSize.y; }
                else
                { px = island.Placement.X + (1f - normalized.y) * island.TargetPixelSize.y; py = island.Placement.Y + normalized.x * island.TargetPixelSize.x; }
                vertices.Add(new Vector3(px / layout.Width * 2f - 1f, py / layout.Height * 2f - 1f, 0f));
                uvs.Add(source); triangles.Add(triangles.Count);
            }
            var mesh = new Mesh { name = "ATO_AtlasIsland", hideFlags = HideFlags.HideAndDontSave,
                indexFormat = vertices.Count > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16 };
            mesh.SetVertices(vertices); mesh.SetUVs(0, uvs); mesh.SetTriangles(triangles, 0, false); mesh.RecalculateBounds(); return mesh;
        }

        private static RenderTexture PullPush(ComputeShader compute, RenderTexture color, RenderTexture mask,
            bool keepBlankAlphaZero, ResourceScope resources)
        {
            var a = resources.Own(CreateRt(color.width, color.height, RenderTextureFormat.RGFloat, true));
            var b = resources.Own(CreateRt(color.width, color.height, RenderTextureFormat.RGFloat, true));
            var output = resources.Own(CreateRt(color.width, color.height, RenderTextureFormat.ARGBFloat, true));
            var init = compute.FindKernel("InitSeeds"); var jumpKernel = compute.FindKernel("JumpFlood"); var fill = compute.FindKernel("Fill");
            compute.SetInt("_Width", color.width); compute.SetInt("_Height", color.height);
            compute.SetTexture(init, "_Mask", mask); compute.SetTexture(init, "_SeedWrite", a); Dispatch(compute, init, color.width, color.height);
            var jump = Mathf.NextPowerOfTwo(Mathf.Max(color.width, color.height)) / 2;
            var read = a; var write = b;
            while (jump >= 1)
            {
                compute.SetInt("_Jump", jump); compute.SetTexture(jumpKernel, "_SeedRead", read); compute.SetTexture(jumpKernel, "_SeedWrite", write);
                Dispatch(compute, jumpKernel, color.width, color.height);
                var swap = read; read = write; write = swap; jump >>= 1;
            }
            compute.SetInt("_KeepBlankAlphaZero", keepBlankAlphaZero ? 1 : 0);
            compute.SetTexture(fill, "_Mask", mask); compute.SetTexture(fill, "_Color", color);
            compute.SetTexture(fill, "_SeedRead", read); compute.SetTexture(fill, "_Output", output); Dispatch(compute, fill, color.width, color.height);
            return output;
        }

        private static void AssignGeneratedTextures(BuildContext context, BuildPlan plan, AtoBuildReport report)
        {
            foreach (var layer in plan.GeneratedLayers)
            foreach (var assignment in layer.AssignedProperties)
            foreach (var property in assignment.Value)
                if (assignment.Key != null && assignment.Key.HasProperty(property)) assignment.Key.SetTexture(property, layer.Output);

            context.Extension<AnimatorServicesContext>().AnimationIndex.RewriteObjectCurves((binding, obj) =>
            {
                if (!(obj is Texture2D source)) return obj;
                var property = NormalizeProperty(binding.propertyName);
                var target = string.IsNullOrEmpty(binding.path) ? context.AvatarRootTransform : context.AvatarRootTransform.Find(binding.path);
                var renderer = target != null ? target.GetComponent<Renderer>() : null;
                var layers = plan.GeneratedLayers.Where(x => x.AnimatedMappings.Any(m =>
                    m.Source == source && m.PropertyName == property && (renderer == null || m.Renderers.Contains(renderer))))
                    .Select(x => x.Output).Distinct().ToList();
                if (layers.Count > 1) report.Warn($"Animated texture '{source.name}' maps to multiple atlas compositions; curve retained.", source);
                return layers.Count == 1 ? layers[0] : obj;
            });

            foreach (var source in plan.GeneratedLayers.SelectMany(x => x.Sources).Distinct())
            {
                var outputs = plan.GeneratedLayers.Where(x => x.Sources.Contains(source)).Select(x => x.Output).Distinct().ToList();
                if (outputs.Count == 1) ObjectRegistry.RegisterReplacedObject(source, outputs[0]);
            }
        }

        private static string NormalizeProperty(string animationProperty)
        {
            const string prefix = "material.";
            var value = animationProperty != null && animationProperty.StartsWith(prefix, StringComparison.Ordinal)
                ? animationProperty.Substring(prefix.Length) : animationProperty ?? string.Empty;
            var dot = value.IndexOf('.'); return dot > 0 ? value.Substring(0, dot) : value;
        }

        private static RenderTexture CreateRt(int width, int height, RenderTextureFormat format, bool randomWrite = false)
        {
            var rt = new RenderTexture(new RenderTextureDescriptor(width, height, format, 0)
            { sRGB = false, useMipMap = false, autoGenerateMips = false, enableRandomWrite = randomWrite })
            { hideFlags = HideFlags.HideAndDontSave, filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
            rt.Create(); return rt;
        }

        private static void Dispatch(ComputeShader shader, int kernel, int width, int height) =>
            shader.Dispatch(kernel, Mathf.CeilToInt(width / 8f), Mathf.CeilToInt(height / 8f), 1);
        private static string Sanitize(string value) => string.Concat((value ?? "Texture").Select(c => char.IsLetterOrDigit(c) || c == '_' ? c : '_'));
    }
}
