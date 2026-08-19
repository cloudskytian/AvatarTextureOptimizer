using System;
using System.Collections.Generic;
using System.Linq;
using NetFosa.AvatarTextureOptimizer.Editor.Logging;
using NetFosa.AvatarTextureOptimizer.Editor.Utils;
using UnityEditor;
using UnityEngine;
using NetFosa.AvatarTextureOptimizer;

namespace NetFosa.AvatarTextureOptimizer.Editor.Analysis
{
    /// <summary>
    /// UV↔贴图映射构建器：核心分析流程。
    /// 1) 遍历材质槽收集贴图引用（着色器属性表 + UV 通道 + ST 变换 + 渲染模式）
    /// 2) 合并动画（材质槽切换 / 贴图切换 / 属性动画）
    /// 3) 白名单解析与违规判定
    /// 4) 像素内容分析、去重
    /// 5) 构建 UV 组 与 贴图类型组
    /// </summary>
    public sealed class TextureMappingBuilder
    {
        private readonly GameObject _root;
        private readonly AvatarScanner _scanner;
        private readonly AnimationAnalysis _animation;
        private readonly TextureCache _cache;
        private readonly ATOLogger _logger;
        private readonly IReadOnlyList<UnityEngine.Object> _whitelist;

        public readonly List<TextureInfo> AllTextures = new List<TextureInfo>();
        public readonly Dictionary<Texture, TextureInfo> InfoByTexture = new Dictionary<Texture, TextureInfo>();
        public readonly List<UvGroup> UvGroups = new List<UvGroup>();
        public readonly List<TextureTypeGroup> TypeGroups = new List<TextureTypeGroup>();

        private int _nextGroupId;

        public TextureMappingBuilder(GameObject root, AvatarScanner scanner, AnimationAnalysis animation,
            TextureCache cache, ATOLogger logger, IReadOnlyList<UnityEngine.Object> whitelist)
        {
            _root = root;
            _scanner = scanner;
            _animation = animation;
            _cache = cache;
            _logger = logger;
            _whitelist = whitelist;
        }

        public void Build()
        {
            CollectMaterialUsages();
            MergeAnimationSwaps();
            ApplyAnimatedPropertyRequirements();
            ResolveWhitelistAndViolations();
            AnalyzePixels();
            TextureDeduplicator.Deduplicate(AllTextures, _cache, _logger);
            BuildUvGroups();
            ResolveTypeGroups();
            ResolveCategories();
        }

        // ------------------------------------------------------------------
        // 1) 材质槽贴图收集
        // ------------------------------------------------------------------

        private TextureInfo GetOrCreateInfo(Texture tex)
        {
            if (InfoByTexture.TryGetValue(tex, out var existing)) return existing;
            var info = new TextureInfo { texture = tex };
            info.debugPath = AssetDatabase.GetAssetPath(tex);
            var (cs, fm) = ReadImportSettings(tex);
            info.colorSpace = cs;
            info.filterMode = fm;
            InfoByTexture[tex] = info;
            AllTextures.Add(info);
            return info;
        }

        private void CollectMaterialUsages()
        {
            foreach (var slot in _scanner.Slots)
            {
                var mat = slot.material;
                if (mat == null || mat.shader == null)
                {
                    _logger.Warn($"Slot {slot.slotIndex} on '{slot.renderer.name}' has null material/shader; skipped.");
                    continue;
                }
                CollectUsagesFromMaterial(mat, slot, isSwap: false);
            }
        }

        private void CollectUsagesFromMaterial(Material mat, SlotSnapshot slot, bool isSwap)
        {
            var props = ShaderAnalyzer.GetTextureProperties(mat.shader);
            foreach (var prop in props)
            {
                var tex = mat.GetTexture(prop.id);
                if (tex == null) continue;

                var info = GetOrCreateInfo(tex);
                var usage = new TextureUsage
                {
                    info = info,
                    material = mat,
                    propertyName = prop.name,
                    propertyId = prop.id,
                    kind = prop.kind,
                };

                // 非 Texture2D → 白名单
                if (!(tex is Texture2D))
                {
                    MarkWhitelist(usage, ATOWhitelistLevel.Full, $"texture '{tex.name}' is {tex.GetType().Name}, not Texture2D");
                }

                // UV 通道
                int uvChannel = ShaderAnalyzer.ResolveUvChannel(mat, prop);
                if (uvChannel < 0)
                {
                    MarkWhitelist(usage, ATOWhitelistLevel.Full, $"property '{prop.name}' on '{mat.name}' is not plain UV sampled (MatCap/Rim/Screen/etc.)");
                }
                else
                {
                    // 网格必须有该通道
                    var mesh = _scanner.RendererMesh.TryGetValue(slot.renderer, out var m) ? m : null;
                    if (mesh != null && UvIslandChannelExists(mesh, uvChannel) == false)
                    {
                        MarkWhitelist(usage, ATOWhitelistLevel.Full,
                            $"mesh '{mesh.name}' has no UV channel {uvChannel} for property '{prop.name}'");
                    }
                    usage.uvChannel = uvChannel;
                }

                // ST 变换
                if (ShaderAnalyzer.HasSTTransform(mat, prop))
                {
                    MarkWhitelist(usage, ATOWhitelistLevel.Full, $"property '{prop.name}' has ST/UV transform on material '{mat.name}'");
                    usage.hasSTTransform = true;
                }

                // 渲染模式
                var modeInfo = RenderModeResolver.Resolve(mat);
                usage.renderMode = modeInfo.mode;
                usage.cutoff = modeInfo.cutoff;
                usage.anyTransparent = RenderModeResolver.RequiresAlphaMetrics(modeInfo.mode);
                usage.anyCutout = modeInfo.mode == RenderMode.Cutout;
                usage.minCutoff = modeInfo.cutoff;
                usage.maxCutoff = modeInfo.cutoff;
                if (isSwap) info.isAnimationSwap = true;

                info.usages.Add(usage);
            }
        }

        private static bool UvIslandChannelExists(Mesh mesh, int channel)
        {
            switch (channel)
            {
                case 0: return mesh.uv != null && mesh.uv.Length > 0;
                case 1: return mesh.uv2 != null && mesh.uv2.Length > 0;
                case 2: return mesh.uv3 != null && mesh.uv3.Length > 0;
                case 3: return mesh.uv4 != null && mesh.uv4.Length > 0;
                case 4: return mesh.uv5 != null && mesh.uv5.Length > 0;
                case 5: return mesh.uv6 != null && mesh.uv6.Length > 0;
                case 6: return mesh.uv7 != null && mesh.uv7.Length > 0;
                case 7: return mesh.uv8 != null && mesh.uv8.Length > 0;
                default: return false;
            }
        }

        private static void MarkWhitelist(TextureUsage usage, ATOWhitelistLevel level, string reason)
        {
            if ((int)usage.whitelistLevel < (int)level)
            {
                usage.whitelistLevel = level;
                usage.whitelistReason = reason;
            }
        }

        // ------------------------------------------------------------------
        // 2) 动画合并
        // ------------------------------------------------------------------

        private void MergeAnimationSwaps()
        {
            // 材质槽切换：把切换材质的贴图加入对应槽（先建立 usage，组阶段自然合并）
            foreach (var kv in _animation.SlotMaterialSwaps)
            {
                var (renderer, slotIndex) = kv.Key;
                var snapshot = _scanner.Slots.FirstOrDefault(s => s.renderer == renderer && s.slotIndex == slotIndex);
                if (snapshot == null) continue;
                foreach (var mat in kv.Value)
                {
                    if (mat == null || mat.shader == null) continue;
                    CollectUsagesFromMaterial(mat, snapshot, isSwap: true);
                }
            }

            // 贴图切换：为切换贴图建立 usage（挂在槽原始材质下）
            foreach (var kv in _animation.TextureSwaps)
            {
                var (renderer, slotIndex, propName) = kv.Key;
                var snapshot = _scanner.Slots.FirstOrDefault(s => s.renderer == renderer && s.slotIndex == slotIndex);
                if (snapshot == null) continue;
                var mat = snapshot.material;
                if (mat == null || mat.shader == null) continue;
                foreach (var tex in kv.Value)
                {
                    if (tex == null) continue;
                    var info = GetOrCreateInfo(tex);
                    info.isAnimationSwap = true;
                    // 解析该属性对应的 UV 通道（避免误把通道 1 的贴图挂到通道 0）
                    int uvChannel = 0;
                    if (mat.shader != null)
                    {
                        foreach (var p in ShaderAnalyzer.GetTextureProperties(mat.shader))
                        {
                            if (p.name == propName)
                            {
                                uvChannel = Mathf.Max(0, ShaderAnalyzer.ResolveUvChannel(mat, p));
                                break;
                            }
                        }
                    }
                    var usage = new TextureUsage
                    {
                        info = info,
                        material = mat,
                        propertyName = propName,
                        propertyId = Shader.PropertyToID(propName),
                        kind = ATOUsageKind.Main,
                        uvChannel = uvChannel,
                        renderMode = RenderModeResolver.Resolve(mat).mode,
                        cutoff = mat.HasProperty("_Cutoff") ? mat.GetFloat("_Cutoff") : 0.5f,
                    };
                    if (usage.renderMode == RenderMode.Cutout) { usage.anyCutout = true; }
                    else if (usage.renderMode == RenderMode.Blend) { usage.anyTransparent = true; }
                    info.usages.Add(usage);
                }
            }
        }

        // ------------------------------------------------------------------
        // 3) 动画属性需求
        // ------------------------------------------------------------------

        private static readonly HashSet<string> BlendRelatedProps = new HashSet<string>
        {
            "_SrcBlend", "_DstBlend", "_ZWrite", "_ZTest", "_AlphaClip", "_RenderingMode", "_Surface",
            "_Cutoff", "_Mode", "_BlendMode",
        };

        private void ApplyAnimatedPropertyRequirements()
        {
            foreach (var (renderer, slotIndex, propName) in _animation.AnimatedMaterialProperties)
            {
                var lower = propName.ToLowerInvariant();
                bool isST = lower.Contains("_st") || lower.Contains("offset") || lower.Contains("scale") ||
                            lower.Contains("scrollrotate") || lower.Contains("uvmode") || lower.Contains("hsvg");
                bool isBlendRelated = BlendRelatedProps.Contains(propName) ||
                                      lower.Contains("srcblend") || lower.Contains("dstblend") || lower.Contains("zwrite") ||
                                      lower.Contains("cutoff") || lower.Contains("alphaclip") || lower.Contains("renderingmode");

                foreach (var info in AllTextures)
                {
                    foreach (var usage in info.usages)
                    {
                        if (usage.material == null) continue;
                        if (!string.Equals(usage.propertyName, propName, StringComparison.OrdinalIgnoreCase))
                            continue;
                        if (usage.material.name == null) continue;
                        // 匹配材质：动画路径对应 renderer；usage 的材质来自该 renderer 的槽
                        // （材质引用相同即可，简化处理）
                        usage.animatedProperties = true;
                        if (isST)
                        {
                            MarkWhitelist(usage, ATOWhitelistLevel.Full, $"property '{propName}' is animated (ST/UV transform by animation)");
                        }
                        if (isBlendRelated)
                        {
                            // 渲染模式相关被动画修改 → 同时考虑透明/裁剪需求
                            usage.anyTransparent = true;
                            usage.anyCutout = true;
                            if (_animation.AnimatedFloatRanges.TryGetValue((renderer, slotIndex, propName), out var range))
                            {
                                if (lower.Contains("cutoff"))
                                {
                                    usage.minCutoff = Mathf.Min(usage.minCutoff, range.min);
                                    usage.maxCutoff = Mathf.Max(usage.maxCutoff, range.max);
                                }
                            }
                        }
                    }
                }
            }
        }

        // ------------------------------------------------------------------
        // 4) 白名单
        // ------------------------------------------------------------------

        private void ResolveWhitelistAndViolations()
        {
            WhitelistResolver.Resolve(_whitelist, _root, AllTextures, _logger);

            int marked = 0;
            foreach (var info in AllTextures)
            {
                foreach (var usage in info.usages)
                {
                    if (usage.whitelistLevel != ATOWhitelistLevel.Normal)
                    {
                        info.whitelisted = true;
                        marked++;
                        if ((int)usage.whitelistLevel < (int)info.whitelistLevel)
                            info.whitelistLevel = usage.whitelistLevel;
                    }
                }
            }
            if (marked > 0)
                _logger.Info($"{marked} texture usage(s) marked whitelisted due to constraints.");
        }

        // ------------------------------------------------------------------
        // 5) 像素内容
        // ------------------------------------------------------------------

        private void AnalyzePixels()
        {
            foreach (var info in AllTextures)
            {
                if (info.texture is not Texture2D) continue;
                try
                {
                    var px = _cache.GetPixels(info.texture, out _, out _);
                    AnalyzePixels(px, out bool hasAlpha, out bool isGray);
                    info.hasAlpha = hasAlpha;
                    info.isGrayscale = isGray;
                }
                catch (Exception e)
                {
                    _logger.Warn($"Failed to analyze pixels of '{info.texture?.name}': {e.Message}");
                }
            }
        }

        private static void AnalyzePixels(Color32[] px, out bool hasAlpha, out bool isGray)
        {
            hasAlpha = false;
            isGray = true;
            if (px == null || px.Length == 0) return;
            int step = Math.Max(1, px.Length / 65536);
            for (int i = 0; i < px.Length; i += step)
            {
                var c = px[i];
                if (c.a < 254) { hasAlpha = true; }
                if (isGray && (Math.Abs(c.r - c.g) > 3 || Math.Abs(c.g - c.b) > 3))
                {
                    isGray = false;
                }
                if (hasAlpha && !isGray) break;
            }
        }

        // ------------------------------------------------------------------
        // 6) UV 组
        // ------------------------------------------------------------------

        private void BuildUvGroups()
        {
            var groupMap = new Dictionary<(Renderer, int, int), UvGroup>();

            foreach (var slot in _scanner.Slots)
            {
                var mat = slot.material;
                if (mat == null || mat.shader == null) continue;

                // 该槽材质引用的所有贴图（按 uvChannel 分组）
                var channelTextures = new Dictionary<int, List<(TextureInfo, List<MetricRequirement>)>>();

                foreach (var info in AllTextures)
                {
                    foreach (var usage in info.usages)
                    {
                        if (usage.material != mat) continue;
                        if (usage.uvChannel < 0) continue; // 违规已白名单
                        if (!channelTextures.TryGetValue(usage.uvChannel, out var list))
                        {
                            list = new List<(TextureInfo, List<MetricRequirement>)>();
                            channelTextures[usage.uvChannel] = list;
                        }
                        if (!list.Any(e => e.Item1 == info))
                        {
                            list.Add((info, new List<MetricRequirement>()));
                        }
                        var req = new MetricRequirement
                        {
                            kind = usage.kind,
                            mode = usage.renderMode,
                            cutoff = usage.cutoff,
                        };
                        var entry = list.First(e => e.Item1 == info);
                        entry.Item2.Add(req);
                        if (usage.anyTransparent || usage.anyCutout)
                        {
                            entry.Item2.Add(new MetricRequirement { kind = usage.kind, mode = RenderMode.Blend, cutoff = usage.cutoff });
                        }
                        if (usage.anyCutout)
                        {
                            entry.Item2.Add(new MetricRequirement { kind = usage.kind, mode = RenderMode.Cutout, cutoff = usage.cutoff });
                        }
                    }
                }

                // 动画贴图切换（加入对应通道）
                foreach (var kv in _animation.TextureSwaps)
                {
                    if (kv.Key.Item1 != slot.renderer || kv.Key.Item2 != slot.slotIndex) continue;
                    var propName = kv.Key.Item3;
                    // 找到该属性对应通道（查主材质属性表）
                    int ch = 0;
                    if (mat.shader != null)
                    {
                        foreach (var p in ShaderAnalyzer.GetTextureProperties(mat.shader))
                        {
                            if (p.name == propName)
                            {
                                ch = Mathf.Max(0, ShaderAnalyzer.ResolveUvChannel(mat, p));
                                break;
                            }
                        }
                    }
                    foreach (var tex in kv.Value)
                    {
                        if (!InfoByTexture.TryGetValue(tex, out var info)) continue;
                        if (!channelTextures.TryGetValue(ch, out var list))
                        {
                            list = new List<(TextureInfo, List<MetricRequirement>)>();
                            channelTextures[ch] = list;
                        }
                        if (!list.Any(e => e.Item1 == info))
                        {
                            list.Add((info, new List<MetricRequirement>
                            {
                                new MetricRequirement { kind = ATOUsageKind.Main, mode = RenderMode.Opaque, cutoff = 0.5f },
                            }));
                        }
                    }
                }

                foreach (var kv in channelTextures)
                {
                    int channel = kv.Key;
                    var key = (slot.renderer, slot.slotIndex, channel);
                    if (!groupMap.TryGetValue(key, out var group))
                    {
                        group = new UvGroup
                        {
                            id = _nextGroupId++,
                            renderer = slot.renderer,
                            slotIndex = slot.slotIndex,
                            uvChannel = channel,
                            mesh = _scanner.RendererMesh.TryGetValue(slot.renderer, out var m) ? m : null,
                        };
                        groupMap[key] = group;
                        UvGroups.Add(group);
                    }

                    foreach (var (info, reqs) in kv.Value)
                    {
                        if (group.textures.Any(t => t.info == info)) continue;
                        var gt = new UvGroupTexture { info = info, active = true };
                        foreach (var r in reqs)
                        {
                            if (!gt.requirements.Contains(r)) gt.requirements.Add(r);
                        }
                        group.textures.Add(gt);
                    }
                }
            }

            // 白名单传播：组内含 Full 白名单贴图 → 其他贴图 NoAtlas
            foreach (var group in UvGroups)
            {
                bool hasFull = group.textures.Any(t => t.info.EffectiveWhitelistLevel == ATOWhitelistLevel.Full);
                if (!hasFull) continue;
                foreach (var gt in group.textures)
                {
                    if (gt.info.EffectiveWhitelistLevel != ATOWhitelistLevel.Full)
                    {
                        gt.info.whitelistLevel = ATOWhitelistLevel.NoAtlas;
                    }
                }
                group.noAtlas = true;
            }

            // 清理无贴图或全白名单组
            UvGroups.RemoveAll(g => g.textures.Count == 0);
            foreach (var g in UvGroups)
            {
                bool allFull = g.textures.All(t => t.info.EffectiveWhitelistLevel == ATOWhitelistLevel.Full);
                if (allFull)
                {
                    g.failed = true;
                    g.failReason = "all textures in this UV group are whitelisted; no optimization";
                }
            }
            _logger.Info($"Built {UvGroups.Count} UV group(s).");
        }

        // ------------------------------------------------------------------
        // 7) 类型组
        // ------------------------------------------------------------------

        private void ResolveTypeGroups()
        {
            // 动画切换贴图并入原贴图所在组
            foreach (var info in AllTextures)
            {
                if (!info.isAnimationSwap) continue;
                // 找同属性非切换贴图
                var original = AllTextures.FirstOrDefault(o => !o.isAnimationSwap &&
                    o.usages.Any(u => info.usages.Any(u2 =>
                        u.material == u2.material && u.propertyName == u2.propertyName)));
                if (original != null)
                {
                    info.swapTarget = original;
                }
            }

            var groupMap = new Dictionary<string, TextureTypeGroup>();
            foreach (var info in AllTextures)
            {
                if (info.dedupTarget != null) continue;
                if (info.swapTarget != null)
                {
                    // 并入原贴图所在类型组
                    if (info.swapTarget.typeGroup != null)
                    {
                        info.typeGroup = info.swapTarget.typeGroup;
                        if (!info.typeGroup.textures.Contains(info)) info.typeGroup.textures.Add(info);
                        continue;
                    }
                }

                var baseKind = ResolveBaseKind(info);
                var colorSpace = baseKind == ATOUsageKind.Normal ? ATOColorSpace.Linear : info.colorSpace;
                var filterMode = info.filterMode;
                var hasNormal = HasCompanion(info, ATOUsageKind.Normal);
                var hasMask = HasCompanion(info, ATOUsageKind.GrayMask);

                var key = $"{baseKind}|{colorSpace}|{filterMode}|{hasNormal}|{hasMask}";
                if (!groupMap.TryGetValue(key, out var tg))
                {
                    tg = new TextureTypeGroup
                    {
                        id = TypeGroups.Count,
                        baseKind = baseKind,
                        colorSpace = colorSpace,
                        filterMode = filterMode,
                        hasNormalCompanion = hasNormal,
                        hasMaskCompanion = hasMask,
                        key = key,
                    };
                    groupMap[key] = tg;
                    TypeGroups.Add(tg);
                }
                info.typeGroup = tg;
                if (!tg.textures.Contains(info)) tg.textures.Add(info);
            }

            _logger.Info($"Resolved {TypeGroups.Count} texture type group(s): {string.Join(", ", TypeGroups.Select(t => t.DisplayKey))}");
        }

        private ATOUsageKind ResolveBaseKind(TextureInfo info)
        {
            bool anyNormal = info.usages.Any(u => u.kind == ATOUsageKind.Normal);
            bool anyMain = info.usages.Any(u => u.kind == ATOUsageKind.Main);
            bool anyAlpha = info.usages.Any(u => u.anyTransparent || u.anyCutout) || info.hasAlpha;
            bool anyMask = info.usages.Any(u => u.kind == ATOUsageKind.GrayMask);

            if (anyNormal) return ATOUsageKind.Normal;
            if (anyMain && anyAlpha) return ATOUsageKind.MainAlpha;
            if (anyMain) return ATOUsageKind.Main;
            if (anyMask) return ATOUsageKind.GrayMask;
            return ATOUsageKind.Other;
        }

        private bool HasCompanion(TextureInfo info, ATOUsageKind kind)
        {
            foreach (var group in UvGroups)
            {
                if (!group.textures.Any(t => t.info == info)) continue;
                if (group.textures.Any(t => t.info != info && t.info.usages.Any(u => u.kind == kind))) return true;
            }
            return false;
        }

        // ------------------------------------------------------------------
        // 8) 压缩类别
        // ------------------------------------------------------------------

        private void ResolveCategories()
        {
            foreach (var info in AllTextures)
            {
                if (info.dedupTarget != null) continue;
                switch (ResolveBaseKind(info))
                {
                    case ATOUsageKind.Normal:
                        info.category = ATOTextureCategory.Normal;
                        break;
                    case ATOUsageKind.GrayMask:
                        info.category = ATOTextureCategory.GrayMask;
                        break;
                    case ATOUsageKind.MainAlpha:
                        info.category = ATOTextureCategory.MainTransparent;
                        break;
                    default:
                        info.category = info.hasAlpha ? ATOTextureCategory.MainTransparent : ATOTextureCategory.MainOpaque;
                        break;
                }
            }
        }

        // ------------------------------------------------------------------

        private static (ATOColorSpace, ATOFilterMode) ReadImportSettings(Texture tex)
        {
            var colorSpace = ATOColorSpace.SRGB;
            var filterMode = ATOFilterMode.Bilinear;
            try
            {
                var path = AssetDatabase.GetAssetPath(tex);
                if (!string.IsNullOrEmpty(path))
                {
                    var importer = AssetImporter.GetAtPath(path) as TextureImporter;
                    if (importer != null)
                    {
                        colorSpace = importer.sRGBTexture ? ATOColorSpace.SRGB : ATOColorSpace.Linear;
                        switch (importer.filterMode)
                        {
                            case FilterMode.Point: filterMode = ATOFilterMode.Point; break;
                            case FilterMode.Trilinear: filterMode = ATOFilterMode.Trilinear; break;
                            default: filterMode = ATOFilterMode.Bilinear; break;
                        }
                    }
                }
                if (tex is Texture2D t2d)
                {
                    if (t2d.colorSpace == ColorSpace.Linear) colorSpace = ATOColorSpace.Linear;
                    if (t2d.filterMode == FilterMode.Point) filterMode = ATOFilterMode.Point;
                    else if (t2d.filterMode == FilterMode.Trilinear) filterMode = ATOFilterMode.Trilinear;
                }
            }
            catch (Exception) { }
            return (colorSpace, filterMode);
        }
    }
}
