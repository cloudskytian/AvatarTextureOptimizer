// SPDX-License-Identifier: MIT
// EN: Stage 1 - discover every texture the avatar can ever show, and decide what may be optimized.
// ZH: 阶段 1 —— 找出 Avatar 可能显示的每一张贴图，并判定哪些可以优化。

using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf;
using Net.Fosa.AvatarTextureOptimizer.Api;
using Net.Fosa.AvatarTextureOptimizer.Editor.Analysis;
using Net.Fosa.AvatarTextureOptimizer.Editor.Core;
using Net.Fosa.AvatarTextureOptimizer.Editor.Model;
using Net.Fosa.AvatarTextureOptimizer.Editor.Textures;
using UnityEngine;

namespace Net.Fosa.AvatarTextureOptimizer.Editor.Plugin
{
    /// <summary>
    /// EN: The collected state of one avatar, produced by <see cref="AtoCollector"/> and consumed by the
    ///     rest of the pipeline.
    /// ZH: 由 <see cref="AtoCollector"/> 产出、供管线其余部分消费的单个 Avatar 的收集状态。
    /// </summary>
    public sealed class AtoCollection
    {
        /// <summary>EN: Every texture the avatar can show, keyed by the canonical (deduplicated) asset. ZH: Avatar 可能显示的所有贴图，以规范化（去重后）资产为键。</summary>
        public readonly Dictionary<Texture2D, TextureEntry> Textures = new Dictionary<Texture2D, TextureEntry>();
        /// <summary>EN: Renderers that participate in the build. ZH: 参与构建的渲染器。</summary>
        public readonly List<Renderer> Renderers = new List<Renderer>();
        /// <summary>EN: Deduplication result mapping original assets to canonical ones. ZH: 去重结果，把原始资产映射到规范资产。</summary>
        public TextureDeduplicator Dedupe;
        /// <summary>EN: Grouping result. ZH: 分组结果。</summary>
        public readonly List<UvGroup> Groups = new List<UvGroup>();
    }

    /// <summary>
    /// EN: Walks the avatar and builds the UV to texture correspondence described in the specification.
    /// ZH: 遍历 Avatar 并建立规格中描述的 UV 与贴图的对应关系。
    /// </summary>
    public sealed class AtoCollector
    {
        private const string Stage = "Collect";

        private readonly BuildContext _ctx;
        private readonly AtoProfile _profile;
        private readonly WhitelistResolver _whitelist;
        private readonly ShaderAnalysisService _shaders;
        private readonly AnimationFacts _animation;

        /// <summary>EN: Creates a collector. ZH: 创建收集器。</summary>
        public AtoCollector(BuildContext ctx, AtoProfile profile, WhitelistResolver whitelist,
            ShaderAnalysisService shaders, AnimationFacts animation)
        {
            _ctx = ctx;
            _profile = profile;
            _whitelist = whitelist;
            _shaders = shaders;
            _animation = animation;
        }

        /// <summary>
        /// EN: Runs the collection.
        /// ZH: 执行收集。
        /// </summary>
        public AtoCollection Collect(AtoProgress progress)
        {
            var collection = new AtoCollection();
            var raw = new List<(Renderer renderer, int slot, Material material, Mesh mesh)>();

            foreach (var renderer in _ctx.AvatarRootObject.GetComponentsInChildren<Renderer>(true))
            {
                if (!(renderer is SkinnedMeshRenderer || renderer is MeshRenderer)) continue;
                if (IsEditorOnly(renderer.transform)) continue;
                if (!IsPotentiallyVisible(renderer)) continue;

                var mesh = GetMesh(renderer);
                if (mesh == null) continue;

                collection.Renderers.Add(renderer);
                var materials = renderer.sharedMaterials;
                for (int slot = 0; slot < materials.Length; slot++)
                {
                    foreach (var m in MaterialsForSlot(renderer, slot, materials))
                        raw.Add((renderer, slot, m, mesh));
                }
            }

            AtoLog.Info(Stage, $"{collection.Renderers.Count} renderers, {raw.Count} material slot bindings (animation included)");

            // EN: First pass - discover textures so that deduplication can run before anything else,
            //     exactly as the specification requires.
            // ZH: 第一遍 —— 先发现贴图，使去重可以先于其他一切执行，与规格要求一致。
            var discovered = new HashSet<Texture2D>();
            foreach (var (renderer, slot, material, mesh) in raw)
            {
                var analysis = _shaders.Analyze(material);
                if (analysis == null) continue;
                foreach (var r in analysis.Textures)
                    if (r.Texture is Texture2D t2d) discovered.Add(t2d);

                foreach (var t in AnimatedTexturesFor(renderer, material))
                    if (t is Texture2D t2d) discovered.Add(t2d);
            }
            progress?.Step(0.3f);

            collection.Dedupe = new TextureDeduplicator();
            collection.Dedupe.Run(discovered, t => _whitelist.IsProtected(t));
            progress?.Step(0.6f);

            // EN: Second pass - build the usage graph on canonical textures.
            // ZH: 第二遍 —— 在规范化贴图上构建引用关系图。
            foreach (var (renderer, slot, material, mesh) in raw)
            {
                var analysis = _shaders.Analyze(material);
                if (analysis == null) continue;

                // EN: Unity renders material slot i with sub mesh i, and when there are more materials
                //     than sub meshes the extra materials re-render the LAST sub mesh. Mapping with a
                //     clamp reproduces that exactly, and the resulting sharing of one UV slot by several
                //     textures is handled correctly by the UV group union.
                // ZH: Unity 用材质槽 i 渲染子网格 i；当材质数多于子网格数时，多出的材质会再次渲染
                //     最后一个子网格。用钳制来映射正是复现了这一行为，
                //     由此产生的多张贴图共享同一 UV 槽的情况由 UV 组的并查集正确处理。
                int subMesh = Mathf.Min(slot, Mathf.Max(0, mesh.subMeshCount - 1));
                if (slot >= mesh.subMeshCount)
                    AtoLog.Debug_(Stage, $"'{renderer.name}' slot {slot} exceeds {mesh.subMeshCount} sub meshes; it re-renders sub mesh {subMesh}.");

                foreach (var texRef in analysis.Textures)
                {
                    if (!(texRef.Texture is Texture2D tex)) continue;
                    var canonical = Canonical(collection, tex);
                    var entry = GetOrCreate(collection, canonical, texRef);

                    var usage = new TextureUsage
                    {
                        Material = material,
                        PropertyName = texRef.PropertyName,
                        Slot = new UvSlot(mesh, subMesh, texRef.UvChannel),
                        Renderer = renderer,
                        MaterialSlotIndex = slot,
                        Kind = texRef.Kind,
                        AlphaMode = analysis.AlphaMode,
                        Cutoff = StrictestCutoff(renderer, analysis),
                    };
                    entry.Usages.Add(usage);

                    ApplySkipRules(entry, texRef, analysis, renderer, canonical, collection);
                }
            }

            progress?.Step(0.9f);
            ProbeContent(collection);
            AtoLog.Info(Stage,
                $"{collection.Textures.Count} unique textures, " +
                $"{collection.Textures.Values.Count(e => e.IsOptimizable)} optimizable");
            return collection;
        }

        private void ApplySkipRules(TextureEntry entry, AtoTextureRef texRef, AtoMaterialAnalysis analysis,
            Renderer renderer, Texture2D canonical, AtoCollection collection)
        {
            if (entry.SkipReason != AtoSkipReason.None) return;

            if (_whitelist.IsProtected(canonical) || collection.Dedupe.WhitelistedCanonicals.Contains(canonical))
            {
                Skip(entry, AtoSkipReason.UserWhitelist, "whitelisted by the user or by deduplication");
                return;
            }
            if (_whitelist.IsProtected(renderer) || _whitelist.IsProtected(analysis == null ? null : renderer.sharedMaterial))
            {
                Skip(entry, AtoSkipReason.UserWhitelist, "renderer or material is whitelisted");
                return;
            }
            if (analysis.ForceWhitelist)
            {
                Skip(entry, AtoSkipReason.WrapDependentShaderFeature, analysis.ForceWhitelistReason);
                return;
            }
            if (texRef.Space != AtoSamplingSpace.MeshUV)
            {
                Skip(entry, AtoSkipReason.NonMeshUVSampling, $"'{texRef.PropertyName}' is not sampled with a plain mesh UV");
                return;
            }
            if (!texRef.IgnoresScaleOffset && !ShaderAnalysisUtil.HasIdentityScaleOffset(analysis == null ? null : entry.Usages[0].Material, texRef.PropertyName))
            {
                Skip(entry, AtoSkipReason.NonIdentityST, $"'{texRef.PropertyName}' has a non identity tiling/offset");
                return;
            }

            var path = nadena.dev.ndmf.runtime.RuntimeUtil.RelativePath(_ctx.AvatarRootObject, renderer.gameObject);
            if (path != null && _animation.UvCriticalAnimated.Contains(path))
            {
                Skip(entry, AtoSkipReason.AnimatedUVTransform, "an animation drives a UV transform on this renderer");
                return;
            }

            // EN: One texture may only ever be read through a single UV channel; two different channels
            //     would require two incompatible island layouts.
            // ZH: 一张贴图只能通过唯一一个 UV 通道读取；两个不同通道会要求两套互不兼容的岛布局。
            foreach (var u in entry.Usages)
            {
                if (u.Slot.Channel != texRef.UvChannel)
                {
                    Skip(entry, AtoSkipReason.ConflictingUVChannels,
                        $"sampled through UV{u.Slot.Channel} and UV{texRef.UvChannel}");
                    return;
                }
            }
        }

        private static void Skip(TextureEntry entry, AtoSkipReason reason, string detail)
        {
            entry.SkipReason = reason;
            entry.SkipDetail = detail;
            AtoLog.Debug_(Stage, $"skip '{entry.Texture.name}': {reason} ({detail})");
        }

        private TextureEntry GetOrCreate(AtoCollection collection, Texture2D canonical, AtoTextureRef texRef)
        {
            if (collection.Textures.TryGetValue(canonical, out var entry))
            {
                // EN: The strictest classification wins; a texture used as a normal anywhere is a normal.
                // ZH: 最严格的分类获胜；只要在任何地方被当作法线使用，它就是法线贴图。
                if (texRef.Kind == AtoTextureKind.Normal) entry.Kind = AtoTextureKind.Normal;
                entry.UsedChannelMask |= texRef.UsedChannelMask;
                return entry;
            }

            entry = new TextureEntry
            {
                Texture = canonical,
                Kind = texRef.Kind,
                UsedChannelMask = texRef.UsedChannelMask,
            };
            TextureProbe.ReadImportSettings(entry);
            collection.Textures[canonical] = entry;
            return entry;
        }

        private void ProbeContent(AtoCollection collection)
        {
            foreach (var entry in collection.Textures.Values)
            {
                if (!entry.IsOptimizable) continue;
                var facts = TextureProbe.Probe(entry.Texture);
                entry.HasAlpha = facts.HasAlpha;
                entry.IsSolidColor = facts.IsSolid;
                entry.SolidColor = facts.SolidColor;

                // EN: Final colour classification from actual content, as specified: a colour texture
                //     with meaningful alpha becomes ColorAlpha, and a monochrome linear texture becomes
                //     Grayscale even if the shader property name gave no hint.
                // ZH: 依据实际内容做最终颜色分类（与规格一致）：带有效 alpha 的颜色贴图归为 ColorAlpha，
                //     单色的线性贴图即使属性名没有暗示也归为 Grayscale。
                if (entry.Kind == AtoTextureKind.ColorOpaque && entry.HasAlpha)
                    entry.Kind = AtoTextureKind.ColorAlpha;
                if (entry.Kind != AtoTextureKind.Normal && !entry.SRgb && facts.IsMonochrome)
                    entry.Kind = AtoTextureKind.Grayscale;
                if (entry.UsedChannelMask == 0) entry.UsedChannelMask = facts.VaryingChannelMask == 0 ? 0xF : facts.VaryingChannelMask;
            }
        }

        private Texture2D Canonical(AtoCollection c, Texture2D tex)
            => c.Dedupe != null && c.Dedupe.Canonical.TryGetValue(tex, out var rep) ? rep : tex;

        /// <summary>
        /// EN: The strictest cutoff seen for a renderer, taking animation into account. Smaller cutoff
        ///     keeps more texels and therefore imposes the tighter requirement.
        /// ZH: 某渲染器上观察到的最严格 cutoff，已计入动画。cutoff 越小保留的像素越多，
        ///     因此要求越严格。
        /// </summary>
        private float StrictestCutoff(Renderer renderer, AtoMaterialAnalysis analysis)
        {
            float cutoff = analysis.Cutoff;
            var path = nadena.dev.ndmf.runtime.RuntimeUtil.RelativePath(_ctx.AvatarRootObject, renderer.gameObject);
            if (path != null && _animation.AnimatedCutoffMin.TryGetValue(path, out var animated))
                cutoff = Mathf.Min(cutoff, animated);
            return cutoff;
        }

        /// <summary>
        /// EN: Every material that can occupy a slot: the authored one plus anything an animation can
        ///     assign to it.
        /// ZH: 可能占据某个槽的全部材质：编辑时设置的那一个，加上动画可能赋予的任何材质。
        /// </summary>
        private IEnumerable<Material> MaterialsForSlot(Renderer renderer, int slot, Material[] authored)
        {
            var seen = new HashSet<Material>();
            if (slot < authored.Length && authored[slot] != null && seen.Add(authored[slot]))
                yield return authored[slot];

            var path = nadena.dev.ndmf.runtime.RuntimeUtil.RelativePath(_ctx.AvatarRootObject, renderer.gameObject);
            if (path == null) yield break;
            if (!_animation.AnimatedMaterials.TryGetValue(path, out var bySlot)) yield break;
            if (!bySlot.TryGetValue(slot, out var set)) yield break;
            foreach (var m in set)
                if (m != null && seen.Add(m))
                    yield return m;
        }

        private IEnumerable<Texture> AnimatedTexturesFor(Renderer renderer, Material material)
        {
            var path = nadena.dev.ndmf.runtime.RuntimeUtil.RelativePath(_ctx.AvatarRootObject, renderer.gameObject);
            if (path == null) yield break;
            if (!_animation.AnimatedTextures.TryGetValue(path, out var byProp)) yield break;
            foreach (var kv in byProp)
                foreach (var t in kv.Value)
                    if (t != null)
                        yield return t;
        }

        /// <summary>
        /// EN: A renderer is considered visible when it is enabled now, or when an animation can enable
        ///     it or its GameObject later.
        /// ZH: 当渲染器当前已启用，或动画可以在之后启用它或其 GameObject 时，视为可见。
        /// </summary>
        private bool IsPotentiallyVisible(Renderer renderer)
        {
            if (renderer.enabled && renderer.gameObject.activeInHierarchy) return true;
            var path = nadena.dev.ndmf.runtime.RuntimeUtil.RelativePath(_ctx.AvatarRootObject, renderer.gameObject);
            if (path == null) return false;
            return _animation.PossiblyEnabled.Contains(path) || _animation.RendererPossiblyEnabled.Contains(path);
        }

        private static bool IsEditorOnly(Transform t)
        {
            for (var cur = t; cur != null; cur = cur.parent)
                if (cur.CompareTag("EditorOnly"))
                    return true;
            return false;
        }

        private static Mesh GetMesh(Renderer r)
        {
            if (r is SkinnedMeshRenderer smr) return smr.sharedMesh;
            if (r is MeshRenderer && r.TryGetComponent<MeshFilter>(out var mf)) return mf.sharedMesh;
            return null;
        }
    }
}
