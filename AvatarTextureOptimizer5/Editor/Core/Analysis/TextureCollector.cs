// Copyright (c) fosa. Licensed under the MIT License.
// Walks the avatar, gathers every texture reference and builds the UV groups that later stages
// pack. This is the stage that decides what is safe to touch at all.
// 遍历 Avatar，收集所有贴图引用并构建后续阶段用于装箱的 UV 组。
// 本阶段决定了究竟哪些内容可以被安全地修改。

using System;
using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// The complete analysis result handed to the quality and packing stages.
    /// 交付给质量与装箱阶段的完整分析结果。
    /// </summary>
    public sealed class CollectionResult
    {
        /// <summary>All textures found, keyed by the deduplicated representative. / 找到的所有贴图，按去重代表索引。</summary>
        public readonly Dictionary<Texture2D, TextureInfo> Textures =
            new Dictionary<Texture2D, TextureInfo>();

        /// <summary>UV groups eligible for optimization. / 可参与优化的 UV 组。</summary>
        public readonly List<UVGroup> Groups = new List<UVGroup>();

        /// <summary>Maps duplicate textures onto their representative. / 将重复贴图映射到其代表。</summary>
        public Dictionary<Texture2D, Texture2D> DedupMapping =
            new Dictionary<Texture2D, Texture2D>();

        /// <summary>Renderers considered by the pipeline. / 管线纳入考虑的渲染器。</summary>
        public readonly List<Renderer> Renderers = new List<Renderer>();

        /// <summary>Human-readable reasons textures were skipped. / 贴图被跳过的可读原因。</summary>
        public readonly List<string> SkipReasons = new List<string>();
    }

    /// <summary>
    /// Builds the texture/UV model for an avatar.
    /// 为 Avatar 构建贴图/UV 模型。
    /// </summary>
    public sealed class TextureCollector
    {
        private readonly ATOLogger _log;
        private readonly ShaderAnalyzer _shaderAnalyzer;
        private readonly TextureCache _cache;

        /// <summary>Creates a collector. / 创建收集器。</summary>
        public TextureCollector(ATOLogger log, ShaderAnalyzer shaderAnalyzer, TextureCache cache)
        {
            _log = log;
            _shaderAnalyzer = shaderAnalyzer;
            _cache = cache;
        }

        /// <summary>
        /// Collects every texture reference under the avatar root.
        /// 收集 Avatar 根节点下的所有贴图引用。
        /// </summary>
        public CollectionResult Collect(
            GameObject avatarRoot,
            HashSet<Texture2D> whitelistedTextures,
            IEnumerable<Object> whitelistEntries,
            AnimationFindings animationFindings)
        {
            var result = new CollectionResult();
            if (avatarRoot == null) return result;

            var whitelistEntryList = new List<Object>();
            if (whitelistEntries != null) whitelistEntryList.AddRange(whitelistEntries);

            var renderers = avatarRoot.GetComponentsInChildren<Renderer>(true);
            var rawTextures = new List<Texture2D>();

            // Pass 1: discover every texture so deduplication can run before grouping.
            // 第 1 遍：发现所有贴图，使去重可以在分组之前执行。
            foreach (var renderer in renderers)
            {
                if (!IsSupportedRenderer(renderer)) continue;
                result.Renderers.Add(renderer);

                foreach (var mat in renderer.sharedMaterials)
                {
                    if (mat == null || mat.shader == null) continue;
                    CollectTexturesFromMaterial(mat, rawTextures);
                }
            }

            // Materials only reachable through animation still address the same textures.
            // 仅通过动画可达的材质同样会寻址到这些贴图。
            if (animationFindings != null)
            {
                foreach (var mat in animationFindings.AllAnimatedMaterials)
                {
                    if (mat == null || mat.shader == null) continue;
                    CollectTexturesFromMaterial(mat, rawTextures);
                }

                // Textures swapped in by animation must never be optimized: the UV layout they
                // will be sampled with is not knowable at build time.
                // 由动画切换进来的贴图绝不能被优化：
                // 构建期无法得知它们将被哪套 UV 布局采样。
                foreach (var tex in animationFindings.AllAnimatedTextures)
                {
                    if (tex != null) whitelistedTextures.Add(tex);
                }
            }

            var dedup = new TextureDeduplicator(_cache, _log);
            result.DedupMapping = dedup.BuildMapping(rawTextures, whitelistedTextures);

            // Pass 2: build usages against the deduplicated representatives.
            // 第 2 遍：针对去重后的代表构建引用信息。
            var streamGroups = new Dictionary<string, UVGroup>(StringComparer.Ordinal);
            var nextGroupId = 0;

            foreach (var renderer in result.Renderers)
            {
                var rendererWhitelisted =
                    WhitelistResolver.IsRendererWhitelistedByMesh(renderer, whitelistEntryList);

                var materials = renderer.sharedMaterials;
                for (var slot = 0; slot < materials.Length; slot++)
                {
                    var mat = materials[slot];
                    if (mat == null || mat.shader == null) continue;

                    var props = _shaderAnalyzer.Analyze(mat.shader);
                    var alphaMode = MaterialAnalyzer.ResolveAlphaMode(mat);
                    var cutoff = MaterialAnalyzer.ResolveCutoff(mat);

                    foreach (var kv in props)
                    {
                        var propName = kv.Key;
                        var propInfo = kv.Value;

                        if (!(mat.GetTexture(propName) is Texture2D rawTex) || rawTex == null)
                            continue;

                        var tex = Represent(result.DedupMapping, rawTex);
                        var info = GetOrCreateInfo(result, tex);

                        var usage = new TextureUsage
                        {
                            Material = mat,
                            PropertyName = propName,
                            Category = propInfo.Category,
                            AlphaMode = alphaMode,
                            Cutoff = cutoff,
                            IsSRGB = !propInfo.IsLinear,
                            UsedChannels = propInfo.UsedChannels,
                        };

                        info.Usages.Add(usage);
                        info.Cutoffs.Add(cutoff);
                        info.UsedChannels |= propInfo.UsedChannels;
                        info.StrictestAlphaMode =
                            MaterialAnalyzer.Stricter(info.StrictestAlphaMode, alphaMode);

                        // Category resolution: a normal map used anywhere as a normal map must
                        // be treated as one everywhere, since its encoding is not a colour.
                        // 分类判定：只要某处被当作法线贴图使用，
                        // 就必须在所有地方都按法线处理，因为其编码不是颜色。
                        if (propInfo.Category == TextureCategory.NormalMap)
                            info.Category = TextureCategory.NormalMap;
                        else if (info.Category != TextureCategory.NormalMap)
                            info.Category = propInfo.Category;

                        // Safety gates. Any one of these means we must not move this texture.
                        // 安全闸门。任意一条成立都意味着不能移动这张贴图。
                        if (rendererWhitelisted)
                        {
                            MarkWhitelisted(info, "renderer or mesh is whitelisted");
                        }
                        else if (whitelistedTextures.Contains(tex) ||
                                 whitelistedTextures.Contains(rawTex))
                        {
                            MarkWhitelisted(info, "explicitly whitelisted");
                        }
                        else if (!propInfo.IsSafe)
                        {
                            MarkWhitelisted(info, propInfo.UnsafeReason ?? "unsafe shader property");
                        }
                        else if (!_shaderAnalyzer.IsMaterialUsageSafe(mat, propName, out var reason))
                        {
                            MarkWhitelisted(info, reason);
                        }

                        if (info.Width == 0)
                        {
                            info.Width = tex.width;
                            info.Height = tex.height;
                            info.IsSRGB = TextureCache.IsSRGB(tex);
                            info.FilterMode = tex.filterMode;
                        }

                        // Group by the UV stream this property samples with.
                        // 按该属性所使用的 UV 流进行分组。
                        var uvChannel = _shaderAnalyzer.ResolveUVChannel(mat, propName);
                        var streamKey = new UVStreamKey(renderer, uvChannel);
                        var group = GetOrCreateGroup(
                            streamGroups, streamKey, ref nextGroupId, result);

                        if (!group.Textures.Contains(info)) group.Textures.Add(info);
                        if (info.Whitelisted)
                        {
                            group.Whitelisted = true;
                            group.SkipReason = info.WhitelistReason;
                        }
                    }
                }
            }

            FinalizeGroups(result);

            _log?.Info(
                $"Collected {result.Textures.Count} unique textures in {result.Groups.Count} UV groups");

            return result;
        }

        /// <summary>
        /// Only Texture2D with no scale/offset on a supported renderer can be optimized.
        /// 只有位于受支持渲染器上、且无缩放/偏移的 Texture2D 才能被优化。
        /// </summary>
        private static bool IsSupportedRenderer(Renderer renderer)
        {
            if (renderer == null) return false;
            if (!(renderer is SkinnedMeshRenderer) && !(renderer is MeshRenderer)) return false;
            if (renderer.sharedMaterials == null || renderer.sharedMaterials.Length == 0)
                return false;

            return true;
        }

        private static void CollectTexturesFromMaterial(Material mat, List<Texture2D> sink)
        {
            foreach (var propName in mat.GetTexturePropertyNames())
            {
                if (mat.GetTexture(propName) is Texture2D tex && tex != null) sink.Add(tex);
            }
        }

        private static Texture2D Represent(
            Dictionary<Texture2D, Texture2D> mapping, Texture2D tex)
        {
            return mapping.TryGetValue(tex, out var rep) && rep != null ? rep : tex;
        }

        private static TextureInfo GetOrCreateInfo(CollectionResult result, Texture2D tex)
        {
            if (result.Textures.TryGetValue(tex, out var info)) return info;

            info = new TextureInfo { Texture = tex, Width = tex.width, Height = tex.height };
            result.Textures[tex] = info;
            return info;
        }

        private static void MarkWhitelisted(TextureInfo info, string reason)
        {
            info.Whitelisted = true;
            if (string.IsNullOrEmpty(info.WhitelistReason)) info.WhitelistReason = reason;
        }

        private static UVGroup GetOrCreateGroup(
            Dictionary<string, UVGroup> groups,
            UVStreamKey key,
            ref int nextId,
            CollectionResult result)
        {
            var id = key.ToString();
            if (groups.TryGetValue(id, out var group)) return group;

            group = new UVGroup { Id = nextId++ };
            group.Streams.Add(key);
            groups[id] = group;
            result.Groups.Add(group);
            return group;
        }

        /// <summary>
        /// Computes each group's type signature and size clamp once membership is final.
        /// 在组成员确定后，计算每个组的类型签名与尺寸钳制值。
        /// </summary>
        private static void FinalizeGroups(CollectionResult result)
        {
            foreach (var group in result.Groups)
            {
                var categories = new SortedSet<string>(StringComparer.Ordinal);
                var maxSize = 0;
                var filterModes = new SortedSet<string>(StringComparer.Ordinal);
                var colorSpaces = new SortedSet<string>(StringComparer.Ordinal);

                foreach (var tex in group.Textures)
                {
                    categories.Add(tex.Category.ToString());
                    filterModes.Add(tex.FilterMode.ToString());
                    colorSpaces.Add(tex.IsSRGB ? "sRGB" : "linear");
                    maxSize = Mathf.Max(maxSize, Mathf.Max(tex.Width, tex.Height));
                }

                // The signature keeps normal maps, masks, colour spaces and filter modes apart,
                // because these cannot share one atlas without changing how they are sampled.
                // 签名将法线、蒙版、色彩空间与过滤模式区分开，
                // 因为它们无法在不改变采样方式的前提下共用同一张图集。
                group.TypeSignature = string.Join("+", categories) + "|" +
                                      string.Join("+", colorSpaces) + "|" +
                                      string.Join("+", filterModes);
                group.MaxOriginalSize = maxSize;
            }
        }
    }
}
