using System.Linq;
using Fosa.AvatarTextureOptimizer.Editor.Atlas;
using UnityEngine;
using UnityEngine.Rendering;

namespace Fosa.AvatarTextureOptimizer.Editor.Analysis
{
    internal static class UvAnalysisStage
    {
        public static void Execute(AvatarAnalysis analysis, bool requireWritableUv,
            ATOOptimizationSettings settings = null)
        {
            RejectAtlasRenderersWithPropertyBlocks(analysis, requireWritableUv);
            var extractor = new UvIslandExtractor();
            foreach (var group in analysis.UvGroups)
            {
                if (!group.AtlasSafe) continue;
                // A mip chain changes derivative-dependent runtime sampling. Without the complete renderer/camera
                // derivative field, neither removing a chain nor inventing one for a no-mip source can be proven
                // appearance-preserving by image metrics alone. Preserve mip presence exactly; streaming may still be
                // configured when the source already has mips.
                // mip 链会改变依赖导数的运行时采样；缺少完整渲染导数场时，新增或删除 mip 都无法仅靠图像指标证明安全。
                if (settings != null && group.Bindings.Any(binding =>
                        TextureFormatResolver.ClassSettings(binding.Kind, settings).mipmapsAndStreaming !=
                        (binding.Texture.mipmapCount > 1)))
                {
                    Reject(analysis, group,
                        "configured mip-map presence differs from the source and cannot preserve derivative-dependent sampling");
                    continue;
                }
                // Whole-texture rebuilding of a point-filtered chain also changes its discrete per-LOD texels.
                if (group.Bindings.Any(binding => binding.Texture.filterMode == FilterMode.Point &&
                                                  binding.Texture.mipmapCount > 1))
                {
                    Reject(analysis, group, "point-filtered source mip chains cannot yet be preserved safely");
                    continue;
                }
                if (group.Bindings.Any(binding => binding.Texture.wrapModeU != TextureWrapMode.Clamp ||
                                                  binding.Texture.wrapModeV != TextureWrapMode.Clamp))
                {
                    Reject(analysis, group,
                        "Repeat, Mirror, and MirrorOnce texture wrapping cannot be preserved by Clamp ATO output");
                    continue;
                }
                if (group.Bindings.Any(binding => float.IsNaN(binding.Texture.mipMapBias) ||
                                                  float.IsInfinity(binding.Texture.mipMapBias)))
                {
                    Reject(analysis, group, "non-finite texture mip-map bias cannot be preserved");
                    continue;
                }
                if (requireWritableUv && !WritableUv(group, out var formatReason))
                {
                    Reject(analysis, group, formatReason); continue;
                }
                if (extractor.Extract(group, out var reason, requireWritableUv)) continue;
                Reject(analysis, group, reason);
            }
        }

        internal static void RejectAtlasRenderersWithPropertyBlocks(AvatarAnalysis analysis, bool atlasMode)
        {
            if (analysis == null) throw new System.ArgumentNullException(nameof(analysis));
            if (!atlasMode) return;
            // Aggregate by the live Unity Renderer, not by RendererRecord identity. The analyzer normally interns
            // RendererRecord instances, but the safety boundary must remain renderer-wide for malformed/custom input.
            // 按真实 Unity Renderer 聚合；即使输入意外出现多个 RendererRecord，也只能写一条整 Renderer 回退。
            foreach (var renderer in analysis.UvGroups.Where(group => group != null && group.AtlasSafe &&
                         group.Renderer != null && group.Renderer.Renderer != null &&
                         group.Renderer.Renderer.HasPropertyBlock())
                         .Select(group => group.Renderer.Renderer).Distinct().ToArray())
            {
                // A block may override a texture, its ST, a UV selector, or another shader-specific sampling control.
                // Unity also allows per-Renderer and per-material blocks with precedence rules. Because Atlas changes UVs,
                // copying or inspecting only known texture properties would not close that state space: preserve the
                // complete Renderer instead. Whole mode does not remap UVs and intentionally does not use this gate.
                // block 可覆写纹理、ST、UV 选择器或其他采样控制；Atlas 无法完整证明其状态，故整 Renderer 回退。
                foreach (var group in analysis.UvGroups.Where(group => group != null && group.Renderer != null &&
                             group.Renderer.Renderer == renderer))
                {
                    group.AtlasSafe = false;
                    group.Islands.Clear();
                }
                analysis.Fallbacks.Add(new FallbackRecord(renderer,
                    "Renderer has a MaterialPropertyBlock whose texture/UV sampling overrides cannot be preserved by atlas remapping"));
            }
        }

        internal static void EnforceAnimatedTextureIdentityClosure(AvatarAnalysis analysis)
        {
            if (analysis == null) throw new System.ArgumentNullException(nameof(analysis));
            var groupByBinding = analysis.UvGroups.SelectMany(group => group.Bindings
                    .Select(binding => new { Binding = binding, Group = group }))
                .ToDictionary(value => value.Binding, value => value.Group);

            foreach (var slot in analysis.Renderers.SelectMany(renderer => renderer.Slots))
            {
                var visited = new System.Collections.Generic.HashSet<TextureBindingRecord>();
                foreach (var seed in slot.Bindings.Where(binding => binding != null && binding.IsAnimatedValue))
                {
                    if (!visited.Add(seed)) continue;
                    var candidates = slot.Bindings.Where(binding => binding != null && binding.IsAnimatedValue &&
                        binding.PropertyName == seed.PropertyName &&
                        object.ReferenceEquals(binding.OriginalTexture, seed.OriginalTexture)).ToArray();
                    foreach (var candidate in candidates) visited.Add(candidate);
                    var groups = candidates.Where(groupByBinding.ContainsKey).Select(binding => groupByBinding[binding])
                        .Distinct().ToArray();
                    if (groups.Length == 1 && candidates.All(groupByBinding.ContainsKey)) continue;

                    // One object-reference curve cannot select different atlas objects for different UV layouts.
                    // Reject every involved destructive rewrite before packing instead of failing after GPU generation.
                    // 同一对象曲线无法按材质状态选择不同图集对象；必须在打包前整体回退，而非生成后报错。
                    foreach (var group in groups.Where(group => group.AtlasSafe))
                        Reject(analysis, group,
                            "animated texture identity spans multiple UV layouts and cannot resolve to one atlas object");
                }
            }
        }

        private static bool WritableUv(UvGroupRecord group, out string reason)
        {
            var attribute = (VertexAttribute)((int)VertexAttribute.TexCoord0 + group.UvChannel);
            var mesh = group.Renderer.Mesh;
            if (!mesh.HasVertexAttribute(attribute) || mesh.GetVertexAttributeDimension(attribute) < 2)
            {
                reason = "required UV vertex attribute is missing or has fewer than two components"; return false;
            }
            var format = mesh.GetVertexAttributeFormat(attribute);
            // The final texture gate evaluates ideal float UVs. Re-encoding an atlas coordinate into Float16 or
            // normalized integers can move it by multiple atlas texels and is not covered by that proof. Until the
            // evaluator rasterizes the quantized mesh itself, preserve such meshes unchanged rather than silently
            // accepting an unmeasured loss. Float32 round-off is bounded far below one texel at supported page sizes.
            // 最终门禁尚未光栅化量化后的网格；低精度 UV 可能偏移多个图集像素，因此只允许 Float32。
            if (format != VertexAttributeFormat.Float32)
            {
                reason = "atlas remapping requires Float32 UVs; low-precision UV quantization is not covered by the final quality proof: " + format;
                return false;
            }
            reason = null; return true;
        }

        private static void Reject(AvatarAnalysis analysis, UvGroupRecord group, string reason)
        {
            group.AtlasSafe = false;
            group.Islands.Clear();
            analysis.Fallbacks.Add(new FallbackRecord(group.Renderer.Renderer, reason));
        }
    }
}
