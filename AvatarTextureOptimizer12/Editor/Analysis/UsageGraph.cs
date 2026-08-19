// SPDX-License-Identifier: MIT
// AvatarTextureOptimizer (ATO) - UV <-> texture usage graph, texture dedup, UV groups and type groups.
// AvatarTextureOptimizer (ATO) - UV <-> 贴图 关系图、贴图去重、UV 组与贴图类型组。

using System;
using System.Collections.Generic;
using System.Linq;
using Net.Fosa.AvatarTextureOptimizer.Editor.Core;
using UnityEngine;

namespace Net.Fosa.AvatarTextureOptimizer.Editor.Analysis
{
    /// <summary>
    /// EN: The complete analysis result handed to the optimisation stages.
    /// ZH: 交给优化阶段的完整分析结果。
    /// </summary>
    public sealed class UsageGraph
    {
        public readonly List<RendererEntry> Renderers = new List<RendererEntry>();

        /// <summary>EN: All textures we know about, keyed by the (possibly deduplicated) canonical texture.
        ///     ZH: 我们已知的全部贴图，键为（可能经过去重的）规范贴图。</summary>
        public readonly Dictionary<Texture2D, TextureUsage> Textures = new Dictionary<Texture2D, TextureUsage>();

        /// <summary>EN: original texture -&gt; canonical texture after dedup. ZH: 去重后 原贴图 -&gt; 规范贴图 的映射。</summary>
        public readonly Dictionary<Texture2D, Texture2D> Canonical = new Dictionary<Texture2D, Texture2D>();

        /// <summary>EN: UV stream -&gt; textures sampled through it. ZH: UV 流 -&gt; 通过它采样的贴图。</summary>
        public readonly Dictionary<UVSlotKey, HashSet<Texture2D>> UvToTextures =
            new Dictionary<UVSlotKey, HashSet<Texture2D>>();

        /// <summary>EN: UV group id -&gt; member textures. All members must share identical atlas placement.
        ///     ZH: UV 组 id -&gt; 成员贴图。同组成员在图集中的位置必须完全相同。</summary>
        public readonly Dictionary<int, List<TextureUsage>> UvGroups = new Dictionary<int, List<TextureUsage>>();

        /// <summary>EN: type-group key -&gt; member textures. ZH: 类型组键 -&gt; 成员贴图。</summary>
        public readonly Dictionary<string, List<TextureUsage>> TypeGroups =
            new Dictionary<string, List<TextureUsage>>();

        public TextureUsage Get(Texture2D tex)
        {
            if (tex == null) return null;
            var c = Canonical.TryGetValue(tex, out var v) ? v : tex;
            return Textures.TryGetValue(c, out var u) ? u : null;
        }
    }

    public static class UsageGraphBuilder
    {
        /// <summary>
        /// EN: Build the whole graph: dedup textures, analyse every material slot on every renderer,
        ///     fold in animated material/texture swaps, then derive UV groups and texture type groups.
        /// ZH: 构建完整关系图：先对贴图去重，再分析每个渲染器上每个材质槽，
        ///     并入动画中的材质/贴图切换，最后推导 UV 组与贴图类型组。
        /// </summary>
        public static UsageGraph Build(List<RendererEntry> renderers, AnimationFacts facts,
            HashSet<Texture2D> whitelistTextures)
        {
            var graph = new UsageGraph();
            graph.Renderers.AddRange(renderers);

            // ---- 1. Collect every candidate texture, then deduplicate / 收集全部候选贴图并去重 ----
            var candidates = new HashSet<Texture2D>();
            foreach (var r in renderers)
            foreach (var set in r.SlotMaterials)
            foreach (var m in set)
                CollectMaterialTextures(m, candidates);

            foreach (var byProp in facts.TextureSwaps.Values)
            foreach (var texes in byProp.Values)
            foreach (var t in texes)
                if (t is Texture2D t2) candidates.Add(t2);

            Deduplicate(candidates, graph, whitelistTextures);

            // ---- 2. Analyse material slots per renderer / 逐渲染器分析材质槽 ----
            foreach (var r in renderers)
            {
                for (int slot = 0; slot < r.SlotMaterials.Count; slot++)
                {
                    int subMesh = Mathf.Min(slot, Mathf.Max(0, r.Mesh.subMeshCount - 1));
                    foreach (var material in r.SlotMaterials[slot])
                    {
                        if (material == null) continue;
                        var analysed = ShaderAnalysis.Analyse(material, r.UvChannelCount);
                        var alphaMode = MaterialAlpha.Infer(material, out var cutoff);

                        // EN: An animated cutoff/render-mode makes us take the strictest variant.
                        // ZH: 动画修改 Cutoff / 渲染模式时，取最严苛的变体。
                        if (facts.AnimatedCutoffs.TryGetValue(r.Path, out var animCutoffs) && animCutoffs.Count > 0)
                        {
                            alphaMode = ATOAlphaMode.Cutout;
                        }

                        foreach (var s in analysed)
                        {
                            if (s.Texture == null) continue;

                            // EN: Animation on any transform-sensitive property invalidates the slot.
                            // ZH: 任何与变换相关的属性被动画修改都会让该槽失效。
                            if (s.Reject == SlotRejectReason.None && IsTransformAnimated(facts, r.Path, s.PropertyName))
                                s.Reject = SlotRejectReason.AnimatedTransform;

                            RegisterSlot(graph, r, subMesh, s, alphaMode, cutoff, animCutoffs);
                        }
                    }
                }
            }

            // ---- 3. Fold animated texture swaps into the UV streams / 将动画贴图切换并入 UV 流 ----
            foreach (var kv in facts.TextureSwaps)
            {
                var entry = renderers.FirstOrDefault(r => r.Path == kv.Key);
                if (entry == null) continue;

                foreach (var propKv in kv.Value)
                {
                    foreach (var raw in propKv.Value)
                    {
                        if (!(raw is Texture2D tex2d)) continue;
                        var canonical = graph.Canonical.TryGetValue(tex2d, out var c) ? c : tex2d;
                        if (!graph.Textures.TryGetValue(canonical, out var usage)) continue;

                        // EN: The swapped-in texture inherits the UV streams of the slots that use this property.
                        // ZH: 被切换进来的贴图继承使用该属性的材质槽所对应的 UV 流。
                        foreach (var slotKey in graph.UvToTextures.Keys.ToList())
                        {
                            if (!ReferenceEquals(slotKey.Renderer, entry.Renderer)) continue;
                            usage.UvSlots.Add(slotKey);
                            graph.UvToTextures[slotKey].Add(canonical);
                        }
                    }
                }
            }

            // ---- 4. Content classification / 内容分类 ----
            foreach (var usage in graph.Textures.Values)
            {
                usage.Content = TextureIntrospection.AnalyseContent(usage.Texture);
                bool alphaMatters = usage.AlphaMode != ATOAlphaMode.Opaque;
                usage.Class = TextureIntrospection.Classify(usage.Texture, usage.IsNormalMap, alphaMatters);
            }

            BuildUvGroups(graph);
            BuildTypeGroups(graph);

            ATOLog.Info($"usage graph: {graph.Textures.Count} texture(s), {graph.UvToTextures.Count} UV stream(s), " +
                        $"{graph.UvGroups.Count} UV group(s), {graph.TypeGroups.Count} type group(s)");
            return graph;
        }

        private static void CollectMaterialTextures(Material m, HashSet<Texture2D> into)
        {
            if (m == null || m.shader == null) return;
            int count = m.shader.GetPropertyCount();
            for (int i = 0; i < count; i++)
            {
                if (m.shader.GetPropertyType(i) != UnityEngine.Rendering.ShaderPropertyType.Texture) continue;
                if (m.GetTexture(m.shader.GetPropertyName(i)) is Texture2D t) into.Add(t);
            }
        }

        /// <summary>
        /// EN: Deduplicate by (pixel content + import settings). If any member of a duplicate class is
        ///     whitelisted the canonical result is whitelisted too, exactly as specified.
        /// ZH: 按（像素内容 + 导入设置）去重。若某个重复组内存在白名单成员，
        ///     则去重结果也视为白名单，与需求一致。
        /// </summary>
        private static void Deduplicate(HashSet<Texture2D> candidates, UsageGraph graph,
            HashSet<Texture2D> whitelistTextures)
        {
            var byKey = new Dictionary<string, Texture2D>(StringComparer.Ordinal);
            int duplicates = 0;

            foreach (var tex in candidates.OrderBy(t => t.name, StringComparer.Ordinal))
            {
                if (tex == null) continue;
                string key;
                try
                {
                    key = TextureIntrospection.DedupKey(tex);
                }
                catch (Exception e)
                {
                    ATOLog.Warn($"dedup key failed for '{tex.name}': {e.Message}");
                    key = Guid.NewGuid().ToString();
                }

                if (byKey.TryGetValue(key, out var canonical))
                {
                    graph.Canonical[tex] = canonical;
                    duplicates++;
                    if (whitelistTextures.Contains(tex)) whitelistTextures.Add(canonical);
                }
                else
                {
                    byKey[key] = tex;
                    graph.Canonical[tex] = tex;
                }
            }

            foreach (var canonical in byKey.Values)
            {
                graph.Textures[canonical] = new TextureUsage
                {
                    Texture = canonical,
                    Excluded = whitelistTextures.Contains(canonical),
                };
            }

            // EN: Second pass - a texture may have been whitelisted after its canonical entry was created.
            // ZH: 第二遍——某张贴图可能在其规范条目创建之后才被判定为白名单。
            foreach (var kv in graph.Canonical)
            {
                if (!whitelistTextures.Contains(kv.Key)) continue;
                if (graph.Textures.TryGetValue(kv.Value, out var u)) u.Excluded = true;
            }

            if (duplicates > 0) ATOLog.Info($"deduplicated {duplicates} texture reference(s) before optimisation");
        }

        private static bool IsTransformAnimated(AnimationFacts facts, string path, string propName)
        {
            if (!facts.AnimatedMaterialFloats.TryGetValue(path, out var animated)) return false;
            foreach (var sensitive in ShaderAnalysis.TransformSensitiveProperties(propName))
            {
                if (animated.Contains(sensitive)) return true;
                if (animated.Contains(sensitive + ".x") || animated.Contains(sensitive + ".y") ||
                    animated.Contains(sensitive + ".z") || animated.Contains(sensitive + ".w")) return true;
            }
            return false;
        }

        private static void RegisterSlot(UsageGraph graph, RendererEntry r, int subMesh, MaterialTextureSlot s,
            ATOAlphaMode alphaMode, float cutoff, HashSet<float> animatedCutoffs)
        {
            var canonical = graph.Canonical.TryGetValue(s.Texture, out var c) ? c : s.Texture;
            if (!graph.Textures.TryGetValue(canonical, out var usage))
            {
                usage = new TextureUsage { Texture = canonical };
                graph.Textures[canonical] = usage;
            }

            usage.Slots.Add(s);
            usage.IsNormalMap |= s.IsNormalMap;
            usage.IsMainTexture |= s.IsMainTexture;
            usage.SRGB |= s.SRGB;

            // EN: Take the strictest alpha requirement across all referencing materials.
            // ZH: 取所有引用材质中最严苛的 alpha 要求。
            if (alphaMode > usage.AlphaMode) usage.AlphaMode = alphaMode;
            if (alphaMode == ATOAlphaMode.Cutout) usage.Cutoffs.Add(cutoff);
            if (animatedCutoffs != null) foreach (var v in animatedCutoffs) usage.Cutoffs.Add(v);

            if (s.Reject != SlotRejectReason.None)
            {
                usage.Excluded = true;
                if (usage.Reject == SlotRejectReason.None) usage.Reject = s.Reject;
                return;
            }

            var key = new UVSlotKey(r.Renderer, subMesh, s.UvChannel);
            usage.UvSlots.Add(key);
            if (!graph.UvToTextures.TryGetValue(key, out var set))
                graph.UvToTextures[key] = set = new HashSet<Texture2D>();
            set.Add(canonical);
        }

        /// <summary>
        /// EN: Textures that share a UV stream must share atlas placement, so union-find over the UV streams
        ///     produces the UV groups. This is what prevents a main texture with a normal map and one without
        ///     from landing at different atlas positions for the same mesh UV.
        /// ZH: 共享同一 UV 流的贴图必须共享图集位置，因此对 UV 流做并查集即可得到 UV 组。
        ///     这正是防止“有法线的主色贴图”与“无法线的主色贴图”在同一网格 UV 上落到不同图集位置的机制。
        /// </summary>
        private static void BuildUvGroups(UsageGraph graph)
        {
            var list = graph.Textures.Values.Where(u => !u.Excluded).ToList();
            var index = new Dictionary<TextureUsage, int>();
            for (int i = 0; i < list.Count; i++) index[list[i]] = i;

            var parent = Enumerable.Range(0, list.Count).ToArray();

            int Find(int x)
            {
                while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; }
                return x;
            }

            foreach (var kv in graph.UvToTextures)
            {
                int? first = null;
                foreach (var tex in kv.Value)
                {
                    if (!graph.Textures.TryGetValue(tex, out var u) || u.Excluded) continue;
                    if (!index.TryGetValue(u, out var i)) continue;
                    if (first == null) { first = i; continue; }
                    parent[Find(i)] = Find(first.Value);
                }
            }

            for (int i = 0; i < list.Count; i++)
            {
                int root = Find(i);
                list[i].UvGroupId = root;
                if (!graph.UvGroups.TryGetValue(root, out var members))
                    graph.UvGroups[root] = members = new List<TextureUsage>();
                members.Add(list[i]);
            }
        }

        /// <summary>
        /// EN: Texture type groups. Two textures belong to the same group when the *set of companion texture
        ///     roles* on the materials that use them matches, and when their colour space and filter mode
        ///     match. This is what stops a normal-map atlas from being 90% empty.
        /// ZH: 贴图类型组。当两张贴图所在材质上的“伴随贴图角色集合”相同，
        ///     且色彩空间与 filterMode 相同时，它们归入同一组。这正是避免法线图集 90% 被浪费的机制。
        /// </summary>
        private static void BuildTypeGroups(UsageGraph graph)
        {
            // EN: Companion roles are computed per UV group so that animation-swapped textures join the
            //     group of the texture they replace, as required.
            // ZH: 伴随角色按 UV 组计算，使动画切换的贴图并入被它替换的贴图所在的组，符合需求。
            foreach (var group in graph.UvGroups.Values)
            {
                var roles = new SortedSet<string>(StringComparer.Ordinal);
                foreach (var u in group)
                {
                    if (u.IsNormalMap) roles.Add("normal");
                    else if (u.Class == ATOTextureClass.Grayscale) roles.Add("mask");
                    else if (u.IsMainTexture) roles.Add("main");
                    else roles.Add("aux");
                }
                var companionKey = string.Join("+", roles);

                foreach (var u in group)
                {
                    var filter = u.Texture != null ? u.Texture.filterMode.ToString() : "Bilinear";
                    var space = u.SRGB ? "srgb" : "linear";
                    var role = u.IsNormalMap ? "normal"
                        : u.Class == ATOTextureClass.Grayscale ? "mask"
                        : u.IsMainTexture ? "main" : "aux";

                    u.TypeGroupKey = $"{companionKey}|{role}|{space}|{filter}";
                    if (!graph.TypeGroups.TryGetValue(u.TypeGroupKey, out var members))
                        graph.TypeGroups[u.TypeGroupKey] = members = new List<TextureUsage>();
                    members.Add(u);
                }
            }
        }
    }

    /// <summary>
    /// EN: Infers a material's alpha behaviour without touching any shader parameter.
    /// ZH: 在不修改任何着色器参数的前提下推断材质的透明行为。
    /// </summary>
    public static class MaterialAlpha
    {
        public static ATOAlphaMode Infer(Material material, out float cutoff)
        {
            cutoff = 0.5f;
            if (material == null) return ATOAlphaMode.Opaque;

            // EN: Cutoff value, if the shader exposes one.
            // ZH: 若着色器暴露了 Cutoff 值则读取。
            foreach (var name in new[] { "_Cutoff", "_AlphaClip", "_AlphaCutoff", "_Cutout" })
            {
                if (material.HasProperty(name)) { cutoff = material.GetFloat(name); break; }
            }

            var tag = material.GetTag("RenderType", false, "");
            var queue = material.renderQueue;

            if (tag.Equals("TransparentCutout", StringComparison.OrdinalIgnoreCase)) return ATOAlphaMode.Cutout;
            if (tag.Equals("Transparent", StringComparison.OrdinalIgnoreCase)) return ATOAlphaMode.Blend;

            // EN: Shader keywords used by the standard shader family and by lilToon variants.
            // ZH: 标准着色器族与 lilToon 变体使用的着色器关键字。
            foreach (var kw in material.shaderKeywords)
            {
                if (kw.Contains("ALPHATEST") || kw.Contains("_ALPHATEST_ON")) return ATOAlphaMode.Cutout;
                if (kw.Contains("ALPHABLEND") || kw.Contains("ALPHAPREMULTIPLY") || kw.Contains("_SURFACE_TYPE_TRANSPARENT"))
                    return ATOAlphaMode.Blend;
            }

            // EN: lilToon encodes the mode in the shader *name* of its generated variants.
            // ZH: lilToon 把模式编码在其生成变体的着色器名称中。
            var shaderName = material.shader != null ? material.shader.name : "";
            if (shaderName.IndexOf("Cutout", StringComparison.OrdinalIgnoreCase) >= 0) return ATOAlphaMode.Cutout;
            if (shaderName.IndexOf("Transparent", StringComparison.OrdinalIgnoreCase) >= 0 ||
                shaderName.IndexOf("Trans", StringComparison.OrdinalIgnoreCase) >= 0) return ATOAlphaMode.Blend;

            if (queue >= 2450 && queue < 2500) return ATOAlphaMode.Cutout;
            if (queue >= 2500) return ATOAlphaMode.Blend;

            return ATOAlphaMode.Opaque;
        }
    }
}
