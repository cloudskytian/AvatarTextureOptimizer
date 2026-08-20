// ============================================================================
// ATO - AnalysisStage implementation (stage 1)
// ATO - AnalysisStage 实现（阶段 1）
//
// Pipeline 管线：
//   1. whitelist resolution (user objects + third-party contributors)
//      白名单解析（用户对象 + 第三方贡献者）
//   2. renderer collection (enabled OR animation-enabled; skip EditorOnly)
//      渲染器收集（启用或被动画启用；跳过 EditorOnly）
//   3. material collection (slot materials + animation-swapped)
//      材质收集（槽位材质 + 动画切换材质）
//   4. per-material shader analysis + ST/special-use/transform detection
//      逐材质着色器分析 + ST/特殊用途/变换检测
//   5. texture registration + dedup (content + import settings)
//      贴图注册 + 去重（内容+导入设置）
//   6. mesh UV-set + island extraction (overlap merge, out-of-range
//      normalization, repeat-wrap detection)
//      网格 UV 集合 + 岛提取（重叠合并、越界归一、repeat 包裹检测）
//   7. UV groups / type groups / atlas-disable propagation
//      UV 组 / 类型组 / 图集禁用传播
// ============================================================================

#region

using System.Collections.Generic;
using nadena.dev.ndmf;
using net.fosa.AvatarTextureOptimizer.Editor.Core;
using UnityEngine;
using UnityEngine.Rendering;

#endregion

namespace net.fosa.AvatarTextureOptimizer.Editor.Analysis
{
    public static class AnalysisStageImpl
    {
        public static void Execute(ATOContext ctx, BuildContext context)
        {
            var c = ctx.Component;
            var log = ctx.Log;
            var avatarRoot = context.AvatarRootObject;

            var analysis = new ATOAnalysis();
            ctx.Analysis = analysis;
            ctx.Anim = ATOAnimationScan.Scan(avatarRoot);
            log.V(ATOLogMask.Analysis, $"animation scan: {ctx.Anim.ClipCount} clips, " +
                $"{ctx.Anim.SwappedMaterials.Count} material swaps, " +
                $"{ctx.Anim.SwappedTextures.Count} texture swaps");

            // ----------------------------------------------------------
            // 1. whitelist  白名单
            // ----------------------------------------------------------
            var wlObjects = new List<Object>();
            wlObjects.AddRange(c.Whitelist);
            foreach (var contributor in Api.ATOApiRegistry.WhitelistContributors)
            {
                try { contributor.ContributeWhitelist(avatarRoot, wlObjects); }
                catch (System.Exception e)
                {
                    log.Warn(ATOLogMask.Analysis, "Whitelist contributor failed: " + e.Message);
                }
            }
            ResolveWhitelist(analysis, wlObjects, avatarRoot, log);

            // ----------------------------------------------------------
            // 2. renderers  渲染器
            // ----------------------------------------------------------
            var renderers = new List<Renderer>();
            foreach (var r in avatarRoot.GetComponentsInChildren<Renderer>(true))
            {
                if (r.gameObject.CompareTag("EditorOnly")) continue;
                // only enabled OR animation-enabled renderers are processed
                // 仅处理启用或被动画启用的渲染器
                if (!r.enabled && !ctx.Anim.AnimationEnabled.Contains(r)) continue;
                renderers.Add(r);
            }
            foreach (var r in ctx.Anim.AnimationEnabled)
            {
                if (r != null && !renderers.Contains(r))
                {
                    renderers.Add(r);
                    log.V(ATOLogMask.Analysis, $"renderer enabled by animation: {r.name}");
                }
            }
            ctx.Renderers.AddRange(renderers);

            // ----------------------------------------------------------
            // 3. materials  材质
            // ----------------------------------------------------------
            var materialSet = new HashSet<Material>(new ObjectIdentityEqualityComparer());
            foreach (var r in renderers)
            {
                foreach (var m in r.sharedMaterials)
                {
                    if (m != null) materialSet.Add(m);
                }
            }
            foreach (var sm in ctx.Anim.SwappedMaterials)
            {
                if (sm.SwappedIn != null) materialSet.Add(sm.SwappedIn);
            }
            ctx.Materials.AddRange(materialSet);

            // ----------------------------------------------------------
            // 4. per-material analysis  逐材质分析
            // ----------------------------------------------------------
            foreach (var mat in materialSet)
            {
                AnalyzeMaterial(analysis, ctx, mat, log);
            }

            // Animation ST whitelists  动画 ST 白名单
            foreach (var (mat, prop) in ctx.Anim.StAnimated)
            {
                if (analysis.Materials.TryGetValue(mat, out var info) &&
                    info.Textures.TryGetValue(prop, out var tex))
                {
                    WhitelistTexture(analysis, tex, "ST animated by animation 动画修改 ST", log);
                }
            }

            // Animation-swapped textures  动画切换贴图
            foreach (var sw in ctx.Anim.SwappedTextures)
            {
                if (analysis.Materials.TryGetValue(sw.Material, out var info) &&
                    info.PropertyRefs.TryGetValue(sw.Property, out var pref) &&
                    !pref.SpecialUse)
                {
                    // include as an additional texture for this property
                    // 作为该属性的附加贴图并入
                    if (!info.Textures.ContainsKey(sw.Property) ||
                        !SameObject(info.Textures[sw.Property], sw.SwappedIn))
                    {
                        info.Textures[sw.Property] = sw.SwappedIn;
                    }
                }
            }

            // ----------------------------------------------------------
            // 5. texture registration + dedup  贴图注册 + 去重
            // ----------------------------------------------------------
            var allTextures = new List<Texture2D>();
            var seen = new HashSet<Texture2D>(new ObjectIdentityEqualityComparer());
            foreach (var mat in materialSet)
            {
                if (!analysis.Materials.TryGetValue(mat, out var info)) continue;
                foreach (var tex in info.Textures.Values)
                {
                    if (tex is Texture2D t2d && seen.Add(t2d)) allTextures.Add(t2d);
                }
            }

            var repByContent = new Dictionary<string, int>();
            int nextTexId = 0;
            foreach (var tex in allTextures)
            {
                var importer = AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(tex), out var imp) ? imp as TextureImporter : null;
                var content = TextureDeduplicator.ContentHash(tex);
                var importSig = TextureDeduplicator.ImportSignature(tex);
                var key = content + "#" + importSig;

                int id;
                if (repByContent.TryGetValue(key, out id))
                {
                    analysis.TextureDedupMap[tex] = id; // dedup 去重
                }
                else
                {
                    id = nextTexId++;
                    var ref0 = new ATOTextureRef
                    {
                        Id = id,
                        Texture = tex,
                        ContentHash = content,
                        ImportSignature = importSig,
                        Width = tex.width,
                        Height = tex.height,
                        sRGB = importer != null && importer.sRGB,
                    };
                    analysis.Textures[id] = ref0;
                    repByContent[key] = id;
                    analysis.TextureDedupMap[tex] = id;
                }
            }

            // link referring materials + whitelist propagation
            // 链接引用材质 + 白名单传播
            foreach (var mat in materialSet)
            {
                if (!analysis.Materials.TryGetValue(mat, out var info)) continue;
                foreach (var (prop, tex) in info.Textures)
                {
                    if (!(tex is Texture2D t2d)) continue;
                    if (!analysis.TextureDedupMap.TryGetValue(t2d, out var id)) continue;
                    var ref0 = analysis.Textures[id];
                    if (!ref0.ReferringMaterials.Contains(mat)) ref0.ReferringMaterials.Add(mat);
                    if (info.Whitelisted)
                    {
                        WhitelistTexture(analysis, t2d, "material whitelisted 材质被白名单", log);
                    }
                    if (analysis.WhitelistedTextures.ContainsKey(t2d))
                    {
                        ref0.Whitelisted = true;
                        if (ref0.WhitelistReason == null) ref0.WhitelistReason = analysis.WhitelistedTextures[t2d];
                    }
                }
            }

            // dedup with whitelist member -> whole group whitelisted
            // 去重组内含白名单 -> 整组白名单
            foreach (var ref0 in analysis.Textures.Values)
            {
                if (ref0.Whitelisted) continue;
                // find all textures deduped into this ref
                foreach (var (orig, id) in analysis.TextureDedupMap)
                {
                    if (id == ref0.Id && analysis.WhitelistedTextures.ContainsKey(orig))
                    {
                        ref0.Whitelisted = true;
                        ref0.WhitelistReason = "dedup group contains whitelist 去重组含白名单";
                        break;
                    }
                }
            }

            // ----------------------------------------------------------
            // 6. mesh UV sets + islands  网格 UV 集合 + 岛
            // ----------------------------------------------------------
            int nextIslandId = 0;
            foreach (var r in renderers)
            {
                var smr = r as SkinnedMeshRenderer;
                var mr = r as MeshRenderer;
                Mesh mesh = smr != null ? smr.sharedMesh : mr != null ? mr.sharedMesh : null;
                if (mesh == null) continue;
                if (mesh.subMeshCount == 0) continue;

                Material[] mats = r.sharedMaterials;
                float maxScale = ChainMaxScale(avatarRoot, r.transform, ctx.Anim.MaxScaleArea);
                float shapeKeyFactor = ComputeShapeKeyAreaFactor(mesh);

                // AAO channel blocking: if AAO uses a channel without a free
                // evacuation channel, whitelist that channel's textures.
                // SMR: official UVUsageCompabilityAPI; MR: reflection-based
                // detection (conservative).
                // AAO 通道阻塞：AAO 使用某通道且无空闲撤离通道时，白名单化该
                // 通道贴图。SMR 走官方 API；MR 走反射检测（保守）。
                var aaoBlocked = new bool[4];
                for (int ch = 0; ch < 4; ch++)
                {
                    bool used = smr != null && Interop.AAOInterop.Available
                        ? Interop.AAOInterop.IsTexCoordUsed(smr, ch)
                        : Interop.AAOInterop.RendererUsesAaoChannel(r as MeshRenderer, ch);
                    if (!used) continue;
                    bool hasFree = true;
                    for (int m = 0; m < 8; m++)
                    {
                        if (m == ch) continue;
                        bool usedM = smr != null && Interop.AAOInterop.Available
                            ? Interop.AAOInterop.IsTexCoordUsed(smr, m)
                            : Interop.AAOInterop.RendererUsesAaoChannel(r as MeshRenderer, m);
                        if (!usedM)
                        {
                            hasFree = true;
                            break;
                        }
                    }
                    if (!hasFree)
                    {
                        aaoBlocked[ch] = true;
                        log.Warn(ATOLogMask.Analysis,
                            $"AAO uses UV channel {ch} on \"{r.name}\" without a free " +
                            $"evacuation channel - channel textures whitelisted. " +
                            "AAO 占用该通道且无空闲撤离通道，通道贴图已白名单化。");
                    }
                }

                for (int sub = 0; sub < mesh.subMeshCount && sub < mats.Length; sub++)
                {
                    var mat = mats[sub];
                    if (mat == null) continue;
                    if (!analysis.Materials.TryGetValue(mat, out var info)) continue;
                    if (info.Whitelisted) continue;

                    for (int ch = 0; ch < 4; ch++)
                    {
                        if (aaoBlocked[ch])
                        {
                            // whitelist this channel's textures  白名单化该通道贴图
                            foreach (var (prop, pref) in info.PropertyRefs)
                            {
                                if (pref.UVChannel != ch) continue;
                                if (!info.Textures.TryGetValue(prop, out var wtex)) continue;
                                if (wtex is Texture2D wt2d)
                                {
                                    WhitelistTexture(analysis, wt2d,
                                        "AAO uses the UV channel (no free evacuation) AAO 占用 UV 通道",
                                        log);
                                }
                            }
                            continue;
                        }

                        // properties of this material sampling channel ch
                        // 该材质采样通道 ch 的属性
                        var sampled = new List<Api.ATOShaderTextureRef>();
                        foreach (var (prop, pref) in info.PropertyRefs)
                        {
                            if (pref.UVChannel != ch) continue;
                            if (pref.SpecialUse) continue;
                            if (pref.EnableProperty != null && mat.GetFloat(pref.EnableProperty) < 0.5f) continue;
                            if (!info.Textures.TryGetValue(prop, out var tex) || !(tex is Texture2D t2d)) continue;
                            if (!analysis.TextureDedupMap.TryGetValue(t2d, out var tid)) continue;
                            var tref = analysis.Textures[tid];
                            if (tref.Whitelisted) continue;
                            sampled.Add(pref);
                        }
                        if (sampled.Count == 0) continue;

                        var extent = UVIslandExtractor.UVExtent(mesh, ch);
                        if (extent.x <= 1e-6f && extent.y <= 1e-6f) continue;
                        var bounds = mesh.bounds;
                        float boundsAvg = (bounds.size.x + bounds.size.y + bounds.size.z) / 3f;
                        float metersPerUV = boundsAvg / Mathf.Max(extent.x, extent.y, 1e-6f);
                        if (metersPerUV <= 1e-6f) continue;

                        var uvSet = new ATOMeshUVSet
                        {
                            Renderer = r,
                            Mesh = mesh,
                            Submesh = sub,
                            Channel = ch,
                            MaterialSlot = sub,
                            Material = mat,
                            IsSkinned = smr != null,
                            MetersPerUV = metersPerUV,
                            MaxScaleArea = maxScale,
                            ShapeKeyArea = shapeKeyFactor,
                        };
                        analysis.MeshUVSets.Add(uvSet);

                        var islands = UVIslandExtractor.Extract(mesh, sub, ch, uvSet, out bool repeat);
                        if (repeat)
                        {
                            // wrap seam dependency -> whitelist the textures of
                            // this channel + warning 跨 wrap 缝 -> 白名单+警告
                            foreach (var pref in sampled)
                            {
                                if (info.Textures.TryGetValue(pref.Property, out var tex) &&
                                    tex is Texture2D t2d)
                                {
                                    WhitelistTexture(analysis, t2d,
                                        "UV spans wrap seam (repeat) 岛跨 wrap 缝依赖 repeat", log);
                                }
                            }
                            continue;
                        }

                        foreach (var island in islands)
                        {
                            island.Id = nextIslandId++;
                            // sampled texture ids  采样贴图 id
                            var seenInIsland = new HashSet<int>();
                            foreach (var pref in sampled)
                            {
                                if (info.Textures.TryGetValue(pref.Property, out var tex) &&
                                    tex is Texture2D t2d &&
                                    analysis.TextureDedupMap.TryGetValue(t2d, out var tid) &&
                                    seenInIsland.Add(tid))
                                {
                                    island.SampledTextureIds.Add(tid);
                                }
                            }
                            if (island.SampledTextureIds.Count == 0) continue;
                            island.TexRefId = island.SampledTextureIds[0];
                            uvSet.Islands.Add(island);
                            analysis.Islands.Add(island);
                        }
                    }
                }
            }

            // ----------------------------------------------------------
            // 7. overlap merge (per anchor texture)  按锚贴图重叠合并
            // ----------------------------------------------------------
            var byAnchor = new Dictionary<int, List<ATOUVIsland>>();
            foreach (var island in analysis.Islands)
            {
                if (!byAnchor.TryGetValue(island.TexRefId, out var list))
                {
                    list = new List<ATOUVIsland>();
                    byAnchor[island.TexRefId] = list;
                }
                list.Add(island);
            }
            foreach (var (texId, islands) in byAnchor)
            {
                var clusters = UVIslandExtractor.MergeOverlaps(islands);
                int nextCluster = 0;
                var clusterOf = new Dictionary<int, int>();
                foreach (var island in islands)
                {
                    int root = clusters[island];
                    if (!clusterOf.TryGetValue(root, out var cid))
                    {
                        cid = nextCluster++;
                        clusterOf[root] = cid;
                    }
                    island.ClusterId = cid;
                }
            }

            // ----------------------------------------------------------
            // 8. UV groups  UV 组
            // ----------------------------------------------------------
            UVIslandExtractor.BuildUVGroups(analysis.Islands, analysis.UVGroups);
            foreach (var group in analysis.UVGroups)
            {
                var seenTex = new HashSet<int>();
                foreach (var island in group.Islands)
                {
                    foreach (var tid in island.SampledTextureIds)
                    {
                        if (seenTex.Add(tid)) group.TextureIds.Add(tid);
                    }
                }
            }

            // ----------------------------------------------------------
            // 9. type groups  类型组
            // ----------------------------------------------------------
            BuildTypeGroups(analysis, log);

            // ----------------------------------------------------------
            // 10. atlas-disable propagation (UV group has whitelist)
            //     图集禁用传播（UV 组含白名单贴图）
            // ----------------------------------------------------------
            foreach (var group in analysis.UVGroups)
            {
                bool anyWhitelisted = false;
                foreach (var tid in group.TextureIds)
                {
                    if (analysis.Textures.TryGetValue(tid, out var tref) && tref.Whitelisted)
                    {
                        anyWhitelisted = true;
                        break;
                    }
                }
                if (!anyWhitelisted) continue;
                foreach (var tid in group.TextureIds)
                {
                    if (!analysis.Textures.TryGetValue(tid, out var tref)) continue;
                    if (tref.Whitelisted) continue;
                    tref.AtlasDisabled = true;
                    log.V(ATOLogMask.Analysis,
                        $"texture #{tid} ({tref.Texture.name}) shares UV with whitelist - atlas disabled 图集禁用");
                }
                // islands of this group keep their original UVs (the
                // whitelisted texture must keep its mapping); all of the
                // group's non-whitelisted textures fall back to whole-image
                // scaling.
                // 该组岛保持原 UV（白名单贴图须保持原映射）；组内非白名单贴图
                // 回退整图缩放。
                foreach (var island in group.Islands)
                {
                    island.NoRemap = true;
                }
            }

            // ----------------------------------------------------------
            // summary  汇总
            // ----------------------------------------------------------
            analysis.TextureCount = analysis.Textures.Count;
            analysis.MaterialCount = materialSet.Count;
            analysis.IslandCount = analysis.Islands.Count;
            foreach (var t in analysis.Textures.Values)
            {
                if (t.Whitelisted) analysis.WhitelistedTextureCount++;
            }

            log.Info(ATOLogMask.Analysis,
                $"analyze done: {analysis.IslandCount} islands, {analysis.TextureCount} textures " +
                $"({analysis.WhitelistedTextureCount} whitelisted), {analysis.UVGroups.Count} UV groups, " +
                $"{analysis.TypeGroups.Count} type groups, {ctx.Renderers.Count} renderers. " +
                "分析完成。");
        }

        // ------------------------------------------------------------------
        private static bool SameObject(UnityEngine.Object a, UnityEngine.Object b) => a == b;

        private static void AnalyzeMaterial(ATOAnalysis analysis, ATOContext ctx, Material mat,
            ATOLog log)
        {
            var info = new ATOMaterialInfo { Material = mat };
            analysis.Materials[mat] = info;

            // user-whitelisted material?  用户白名单材质？
            if (ctx.WhitelistObjects.Contains(mat))
            {
                info.Whitelisted = true;
                info.WhitelistReason = "user whitelist 用户白名单";
            }

            var analysisResult = ShaderAnalysisService.Analyze(mat.shader, mat);
            if (analysisResult == null)
            {
                // unsupported shader -> whitelist all its textures
                // 不支持的着色器 -> 其全部贴图白名单
                info.Whitelisted = true;
                info.WhitelistReason = "unsupported shader 不支持的着色器";
                log.Warn(ATOLogMask.Analysis,
                    $"shader \"{mat.shader.name}\" not understood by any ATO analyzer - " +
                    $"material \"{mat.name}\" textures whitelisted. 着色器无分析器可理解，其贴图已白名单化。");
                foreach (var tex in AllMaterialTextures(mat))
                {
                    WhitelistTexture(analysis, tex, "unsupported shader 不支持的着色器", log);
                }
                return;
            }

            info.Analysis = analysisResult;
            info.AlphaMode = analysisResult.AlphaMode;

            // strictest alpha mode from animation  动画最严透明模式
            if (ctx.Anim.TransparentModes.TryGetValue(mat, out var modes))
            {
                int strictest = 0;
                foreach (var m in modes) strictest = Mathf.Max(strictest, m == 4 ? 3 : m);
                info.AlphaMode = Mathf.Max(info.AlphaMode, strictest);
            }

            // cutoff ranges  裁剪阈值
            if (analysisResult.CutoffProperty != null && mat.HasProperty(analysisResult.CutoffProperty))
            {
                float v = mat.GetFloat(analysisResult.CutoffProperty);
                info.CutoffMin = info.CutoffMax = v;
                if (ctx.Anim.Cutoffs.TryGetValue(mat, out var cr))
                {
                    info.CutoffMin = Mathf.Min(v, cr.min);
                    info.CutoffMax = Mathf.Max(v, cr.max);
                }
            }
            if (analysisResult.SubpassCutoffProperty != null && mat.HasProperty(analysisResult.SubpassCutoffProperty))
            {
                float v = mat.GetFloat(analysisResult.SubpassCutoffProperty);
                info.SubpassCutoffMin = info.SubpassCutoffMax = v;
                if (ctx.Anim.SubpassCutoffs.TryGetValue(mat, out var cr2))
                {
                    info.SubpassCutoffMin = Mathf.Min(v, cr2.min);
                    info.SubpassCutoffMax = Mathf.Max(v, cr2.max);
                }
            }

            foreach (var pref in analysisResult.Textures)
            {
                info.PropertyRefs[pref.Property] = pref;
                if (!mat.HasProperty(pref.Property)) continue;
                if (!(mat.GetTexture(pref.Property) is Texture2D tex)) continue;
                info.Textures[pref.Property] = tex;

                // static ST transform  静态 ST 变换
                Vector2 offset = mat.GetTextureOffset(pref.Property);
                Vector2 scale = mat.GetTextureScale(pref.Property);
                if (!Mathf.Approximately(offset.x, 0f) || !Mathf.Approximately(offset.y, 0f) ||
                    !Mathf.Approximately(scale.x, 1f) || !Mathf.Approximately(scale.y, 1f))
                {
                    WhitelistTexture(analysis, tex, "ST transform on material 材质上有 ST 变换", log);
                }
                // UV scroll/rotate  UV 滚动/旋转
                if (pref.ScrollRotateProperty != null && mat.HasProperty(pref.ScrollRotateProperty))
                {
                    var v = mat.GetVector(pref.ScrollRotateProperty);
                    if (!Mathf.Approximately(v.x, 0f) || !Mathf.Approximately(v.y, 0f) ||
                        !Mathf.Approximately(v.z, 0f) || !Mathf.Approximately(v.w, 0f))
                    {
                        WhitelistTexture(analysis, tex, "UV scroll/rotate animation 有 UV 滚动/旋转", log);
                    }
                }
                // UV mode selection  UV 模式选择
                if (pref.UVModeProperty != null && mat.HasProperty(pref.UVModeProperty))
                {
                    int mode = (int) Mathf.Round(mat.GetFloat(pref.UVModeProperty));
                    if (mode < 0 || mode > 3)
                    {
                        WhitelistTexture(analysis, tex,
                            $"UV mode {mode} is special-use 特殊用途 UV 模式", log);
                    }
                }
                // special use  特殊用途
                if (pref.SpecialUse)
                {
                    WhitelistTexture(analysis, tex, "special-use texture 特殊用途贴图", log);
                }
                // whitelisted material -> all its textures 白名单材质 -> 全部贴图
                if (info.Whitelisted)
                {
                    WhitelistTexture(analysis, tex, "material whitelisted 材质被白名单", log);
                }
            }
        }

        // ------------------------------------------------------------------
        private static IEnumerable<Texture> AllMaterialTextures(Material mat)
        {
            int count = ShaderUtil.GetPropertyCount(mat.shader);
            for (int i = 0; i < count; i++)
            {
                if (ShaderUtil.GetPropertyType(mat.shader, i) != ShaderUtil.ShaderPropertyType.Texture) continue;
                var t = mat.GetTexture(ShaderUtil.GetPropertyName(mat.shader, i));
                if (t != null) yield return t;
            }
        }

        // ------------------------------------------------------------------
        private static void WhitelistTexture(ATOAnalysis analysis, Texture tex, string reason, ATOLog log)
        {
            if (analysis.WhitelistedTextures.ContainsKey(tex)) return;
            analysis.WhitelistedTextures[tex] = reason;
            // mark every dedup representative of this texture
            // 标记该贴图的每个去重代表
            foreach (var (orig, id) in analysis.TextureDedupMap)
            {
                if (orig == tex)
                {
                    var ref0 = analysis.Textures[id];
                    ref0.Whitelisted = true;
                    if (ref0.WhitelistReason == null) ref0.WhitelistReason = reason;
                }
            }
            log.V(ATOLogMask.Analysis, $"whitelist texture \"{tex.name}\": {reason}");
        }

        // ------------------------------------------------------------------
        private static void ResolveWhitelist(ATOAnalysis analysis, List<Object> objects,
            GameObject avatarRoot, ATOLog log)
        {
            var allRenderers = avatarRoot.GetComponentsInChildren<Renderer>(true);
            foreach (var obj in objects)
            {
                if (obj == null) continue;
                if (obj is Texture t)
                {
                    WhitelistTexture(analysis, t, "user whitelist 用户白名单", log);
                }
                else if (obj is Material m)
                {
                    foreach (var tex in AllMaterialTextures(m))
                    {
                        WhitelistTexture(analysis, tex, "whitelisted material 白名单材质", log);
                    }
                }
                else if (obj is Mesh mesh)
                {
                    foreach (var r in allRenderers)
                    {
                        Mesh rm = (r as SkinnedMeshRenderer)?.sharedMesh ?? (r as MeshRenderer)?.sharedMesh;
                        if (rm != mesh) continue;
                        foreach (var mm in r.sharedMaterials)
                        {
                            if (mm == null) continue;
                            foreach (var tex in AllMaterialTextures(mm))
                            {
                                WhitelistTexture(analysis, tex, "whitelisted mesh 白名单网格", log);
                            }
                        }
                    }
                }
                else if (obj is Renderer r)
                {
                    foreach (var mm in r.sharedMaterials)
                    {
                        if (mm == null) continue;
                        foreach (var tex in AllMaterialTextures(mm))
                        {
                            WhitelistTexture(analysis, tex, "whitelisted renderer 白名单渲染器", log);
                        }
                    }
                }
                else if (obj is GameObject go)
                {
                    foreach (var rr in go.GetComponentsInChildren<Renderer>(true))
                    {
                        foreach (var mm in rr.sharedMaterials)
                        {
                            if (mm == null) continue;
                            foreach (var tex in AllMaterialTextures(mm))
                            {
                                WhitelistTexture(analysis, tex, "whitelisted game object 白名单对象", log);
                            }
                        }
                    }
                }
                else if (obj is Animator anim)
                {
                    var rr = anim.GetComponent<Renderer>();
                    if (rr != null)
                    {
                        foreach (var mm in rr.sharedMaterials)
                        {
                            if (mm == null) continue;
                            foreach (var tex in AllMaterialTextures(mm))
                            {
                                WhitelistTexture(analysis, tex, "whitelisted animator 白名单动画器", log);
                            }
                        }
                    }
                }
                else if (obj is Component comp)
                {
                    // any other component: whitelist textures of its object's
                    // renderers  其他组件：白名单化其对象上渲染器的贴图
                    foreach (var rr in comp.GetComponents<Renderer>())
                    {
                        foreach (var mm in rr.sharedMaterials)
                        {
                            if (mm == null) continue;
                            foreach (var tex in AllMaterialTextures(mm))
                            {
                                WhitelistTexture(analysis, tex, "whitelisted component 白名单组件", log);
                            }
                        }
                    }
                }
            }
        }

        // ------------------------------------------------------------------
        /// <summary>Product of animated max-scale area factors along the
        /// transform chain from the renderer up to the avatar root.
        /// 渲染器到 Avatar 根的变换链上动画最大缩放面积系数乘积。</summary>
        private static float ChainMaxScale(GameObject root, Transform t,
            Dictionary<GameObject, float> perGO)
        {
            float f = 1f;
            var cur = t;
            int depth = 0;
            while (cur != null && cur.gameObject != root && depth < 256)
            {
                if (perGO.TryGetValue(cur.gameObject, out var v)) f *= v;
                cur = cur.parent;
                depth++;
            }
            if (cur != null && perGO.TryGetValue(cur.gameObject, out var v2)) f *= v2;
            return f;
        }

        // ------------------------------------------------------------------
        /// <summary>Max triangle-area ratio between shape keys 0 and 100
        /// (only those two values are considered, per spec).
        /// 形态键 0 与 100 之间三角面积比最大值（按规范只考虑这两个值）。</summary>
        public static float ComputeShapeKeyAreaFactor(Mesh mesh)
        {
            if (mesh.blendShapeCount == 0) return 1f;
            var indices = mesh.GetIndices(0);
            if (indices.Length == 0) return 1f;
            var baseVerts = mesh.vertices;
            float baseArea = TriangleAreaSum(baseVerts, indices);
            if (baseArea < 1e-8f) return 1f;

            float maxRatio = 1f;
            var verts100 = new Vector3[baseVerts.Length];
            for (int s = 0; s < mesh.blendShapeCount; s++)
            {
                // Use the frame with the highest weight (spec: only 0 and 100
                // are considered). 使用最高权重帧（规范只考虑 0 与 100）。
                int frameCount = mesh.GetBlendShapeFrameCount(s);
                int frame = 0;
                float wMax = 0f;
                for (int f = 0; f < frameCount; f++)
                {
                    float w = mesh.GetBlendShapeFrameWeight(s, f);
                    if (w > wMax)
                    {
                        wMax = w;
                        frame = f;
                    }
                }
                if (wMax < 1e-3f) continue;
                var disp = mesh.GetBlendShapeFrameVertexData(s, frame);
                float scale = 100f / wMax; // extrapolate to weight 100 外推到权重 100
                for (int i = 0; i < baseVerts.Length; i++)
                {
                    verts100[i] = baseVerts[i] + disp[i] * scale;
                }
                float area = TriangleAreaSum(verts100, indices);
                if (area / baseArea > maxRatio) maxRatio = area / baseArea;
            }
            return maxRatio;
        }

        private static float TriangleAreaSum(Vector3[] verts, int[] indices)
        {
            float sum = 0f;
            for (int i = 0; i < indices.Length; i += 3)
            {
                var a = verts[indices[i]];
                var b = verts[indices[i + 1]];
                var c = verts[indices[i + 2]];
                sum += Vector3.Cross(b - a, c - a).magnitude * 0.5f;
            }
            return sum;
        }

        // ------------------------------------------------------------------
        /// <summary>Builds texture type groups from albedo textures.
        /// 由主色贴图构建类型组。</summary>
        private static void BuildTypeGroups(ATOAnalysis analysis, ATOLog log)
        {
            var groups = new Dictionary<string, ATOTexTypeGroup>();

            // first pass: albedo textures  主色贴图
            foreach (var (tid, tref) in analysis.Textures)
            {
                if (tref.Whitelisted) continue;
                if (!IsAlbedo(tref, analysis)) continue;

                bool hasNormal = false, hasMask = false, hasEmission = false;
                // special textures paired through referring materials
                // 通过引用材质配对的特殊贴图
                foreach (var mat in tref.ReferringMaterials)
                {
                    if (!analysis.Materials.TryGetValue(mat, out var info)) continue;
                    foreach (var (prop, pref) in info.PropertyRefs)
                    {
                        if (!info.Textures.TryGetValue(prop, out var tex) || !(tex is Texture2D t2d)) continue;
                        if (!analysis.TextureDedupMap.TryGetValue(t2d, out var sid)) continue;
                        var sref = analysis.Textures[sid];
                        if (sref.Whitelisted) continue;
                        switch (pref.Role)
                        {
                            case Api.ATOTextureRole.Normal: hasNormal = true; break;
                            case Api.ATOTextureRole.Mask: hasMask = true; break;
                            case Api.ATOTextureRole.Emission: hasEmission = true; break;
                        }
                    }
                }

                tref.HasNormal = hasNormal;
                tref.HasMask = hasMask;
                tref.HasEmission = hasEmission;

                var key = $"{tref.sRGB}|{(int) GetFilterMode(tref.Texture)}|{hasNormal}|{hasMask}|{hasEmission}";
                if (!groups.TryGetValue(key, out var group))
                {
                    group = new ATOTexTypeGroup
                    {
                        Id = analysis.TypeGroups.Count,
                        sRGB = tref.sRGB,
                        Filter = GetFilterMode(tref.Texture),
                        HasNormal = hasNormal,
                        HasMask = hasMask,
                        HasEmission = hasEmission,
                    };
                    groups[key] = group;
                    analysis.TypeGroups.Add(group);
                }
                group.TextureIds.Add(tid);
                if (!group.SpecialTextures.ContainsKey(tid))
                {
                    group.SpecialTextures[tid] = new Dictionary<Api.ATOTextureRole, int>();
                }
                foreach (var mat in tref.ReferringMaterials)
                {
                    if (!analysis.Materials.TryGetValue(mat, out var info)) continue;
                    foreach (var (prop, pref) in info.PropertyRefs)
                    {
                        if (pref.Role == Api.ATOTextureRole.Albedo) continue;
                        if (!info.Textures.TryGetValue(prop, out var tex) || !(tex is Texture2D t2d)) continue;
                        if (!analysis.TextureDedupMap.TryGetValue(t2d, out var sid)) continue;
                        if (analysis.Textures[sid].Whitelisted) continue;
                        // strictest: any material carrying it counts 最严：任一材质携带即计入
                        if (!group.SpecialTextures[tid].ContainsKey(pref.Role))
                        {
                            group.SpecialTextures[tid][pref.Role] = sid;
                        }
                    }
                }
            }

            // second pass: non-albedo textures without an albedo pair
            // 第二遍：无主色配对的非主色贴图（单独成组）
            foreach (var (tid, tref) in analysis.Textures)
            {
                if (tref.Whitelisted || IsAlbedo(tref, analysis)) continue;
                bool grouped = false;
                foreach (var g in analysis.TypeGroups)
                {
                    if (g.SpecialTextures.Values.Contains(tref.Id)) grouped = true;
                }
                if (grouped) continue;
                // standalone group 单独成组（该贴图作为布局驱动）
                var group = new ATOTexTypeGroup
                {
                    Id = analysis.TypeGroups.Count,
                    sRGB = tref.sRGB,
                    Filter = GetFilterMode(tref.Texture),
                };
                group.TextureIds.Add(tid);
                analysis.TypeGroups.Add(group);
            }

            // link UV groups to type groups  UV 组关联类型组
            var texToGroup = new Dictionary<int, int>();
            foreach (var g in analysis.TypeGroups)
            {
                foreach (var tid in g.TextureIds) texToGroup[tid] = g.Id;
            }
            foreach (var uvGroup in analysis.UVGroups)
            {
                var seen = new HashSet<int>();
                foreach (var tid in uvGroup.TextureIds)
                {
                    if (texToGroup.TryGetValue(tid, out var gid) && seen.Add(gid))
                    {
                        uvGroup.TypeGroupIds.Add(gid);
                    }
                }
            }
        }

        private static bool IsAlbedo(ATOTextureRef tref, ATOAnalysis analysis)
        {
            foreach (var mat in tref.ReferringMaterials)
            {
                if (!analysis.Materials.TryGetValue(mat, out var info)) continue;
                foreach (var (prop, pref) in info.PropertyRefs)
                {
                    if (info.Textures.TryGetValue(prop, out var tex) && tex == tref.Texture &&
                        pref.Role == Api.ATOTextureRole.Albedo)
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        private static FilterMode GetFilterMode(Texture tex)
        {
            var path = AssetDatabase.GetAssetPath(tex);
            if (!string.IsNullOrEmpty(path) &&
                AssetImporter.GetAtPath(path, out var imp) && imp is TextureImporter ti)
            {
                return ti.filterMode;
            }
            return TextureImporterFilterMode.Bilinear;
        }
    }
}
