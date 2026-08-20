using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;
using Net.Fosa.AvatarTextureOptimizer;

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// Full bake pipeline. / 完整烘焙管线。
    /// </summary>
    public sealed class AtoPipeline
    {
        public void Run(BuildContext ndmf)
        {
            var swAll = AtoLog.Start("pipeline");
            using var ctx = new AtoContext { Ndmf = ndmf, Avatar = ndmf.AvatarRootObject };
            try
            {
                ctx.Anim = ndmf.Extension<AnimatorServicesContext>();
            }
            catch
            {
                ctx.Anim = null;
                AtoLog.Warn("AnimatorServicesContext not active");
            }

            using var progress = new AtoProgress(8);
            ctx.Progress = progress;

            try
            {
                ApplyLanguage(ctx);
                StageValidate(ctx);
                if (ctx.Component == null) return;
                StageCollect(ctx);
                StageDedupeImport(ctx);
                StageAnalyze(ctx);
                StageQuality(ctx);
                StageAtlas(ctx);
                StageApply(ctx);
                StageFinish(ctx);
            }
            catch (AtoCanceledException)
            {
                ctx.Canceled = true;
                ErrorReport.ReportError(AtoI18n.NdmfLocalizer, ErrorSeverity.Error, "error.canceled");
                AtoLog.Warn("Canceled by user / 用户取消");
            }
            catch (Exception e)
            {
                AtoLog.Error("Pipeline failed: " + e);
                ErrorReport.ReportException(e);
            }
            finally
            {
                ctx.Dispose();
                AtoLog.End("pipeline", swAll);
            }
        }

        private static void ApplyLanguage(AtoContext ctx)
        {
            var comps = ctx.Avatar.GetComponentsInChildren<AvatarTextureOptimizer>(true);
            if (comps.Length == 1 && comps[0].languageMode == AtoLanguageMode.Manual)
                AtoI18n.SetForcedLanguage(comps[0].manualLanguage);
            else
                AtoI18n.SetForcedLanguage(null);
        }

        private void StageValidate(AtoContext ctx)
        {
            ctx.Progress.Set(0, AtoI18n.T("progress.validate"));
            var sw = AtoLog.Start("validate");
            var comps = ctx.Avatar.GetComponentsInChildren<AvatarTextureOptimizer>(true);
            if (comps.Length == 0)
            {
                AtoLog.Info("No ATO component; skip.");
                AtoLog.End("validate", sw);
                return;
            }
            if (comps.Length > 1)
            {
                ErrorReport.ReportError(AtoI18n.NdmfLocalizer, ErrorSeverity.Error, "error.multiple",
                    comps.Length.ToString());
                AtoLog.Error($"Multiple ATO components: {comps.Length}");
                ctx.Component = null;
                AtoLog.End("validate", sw);
                return;
            }
            var c = comps[0];
            if (!AtoPlatformUtil.HasVrcAvatarDescriptor(c.gameObject) || c.gameObject != ctx.Avatar)
            {
                // Must be on the avatar root that has the descriptor.
                // 必须挂在带 Descriptor 的 Avatar 根上。
                if (!AtoPlatformUtil.HasVrcAvatarDescriptor(c.gameObject))
                {
                    ErrorReport.ReportError(AtoI18n.NdmfLocalizer, ErrorSeverity.Error, "error.noDescriptor");
                    AtoLog.Error("ATO not on VRCAvatarDescriptor object");
                    ctx.Component = null;
                    AtoLog.End("validate", sw);
                    return;
                }
            }
            ctx.Component = c;
            ctx.Platform = AtoPlatformUtil.Current();
            ctx.Settings = c.Resolve(ctx.Platform);
            AtoLog.Verbose = ctx.Settings.verboseLog;
            AtoLog.Info($"Platform={ctx.Platform} preset={ctx.Settings.qualityPreset} atlas={ctx.Settings.generateAtlas} npot={ctx.Settings.experimentalNpot}");
            ExpandWhitelist(ctx);
            AtoLog.End("validate", sw, $"wlTex={ctx.WhitelistTextures.Count}");
        }

        private static void ExpandWhitelist(AtoContext ctx)
        {
            foreach (var o in ctx.Component.whitelist)
            {
                if (o == null) continue;
                ctx.WhitelistObjects.Add(o);
                CollectRefs(o, ctx);
            }
        }

        private static void CollectRefs(Object o, AtoContext ctx, int depth = 0)
        {
            if (o == null || depth > 6) return;
            if (o is Texture2D t) { ctx.WhitelistTextures.Add(t); return; }
            if (o is Material m)
            {
                ctx.WhitelistMaterials.Add(m);
                foreach (var n in m.GetTexturePropertyNames())
                    if (m.GetTexture(n) is Texture2D tx) ctx.WhitelistTextures.Add(tx);
                return;
            }
            if (o is Renderer r)
            {
                ctx.WhitelistRenderers.Add(r);
                foreach (var mat in r.sharedMaterials)
                    CollectRefs(mat, ctx, depth + 1);
                return;
            }
            if (o is AnimationClip clip)
            {
                foreach (var b in AnimationUtility.GetObjectReferenceCurveBindings(clip))
                {
                    var curve = AnimationUtility.GetObjectReferenceCurve(clip, b);
                    if (curve == null) continue;
                    foreach (var kf in curve) CollectRefs(kf.value, ctx, depth + 1);
                }
            }
        }

        private void StageCollect(AtoContext ctx)
        {
            ctx.Progress.Set(1, AtoI18n.T("progress.collect"));
            var sw = AtoLog.Start("collect");
            var facts = AtoAnimationAnalyzer.Collect(ctx);
            foreach (var r in ctx.Avatar.GetComponentsInChildren<Renderer>(true))
            {
                if (AtoPlatformUtil.IsEditorOnly(r.transform)) continue;
                if (!(r is SkinnedMeshRenderer || r is MeshRenderer)) continue;
                if (!facts.CanEnable.Contains(r) && !r.enabled) continue;
                ctx.Renderers.Add(r);
            }
            ctx.Report.Renderers = ctx.Renderers.Count;
            AtoLog.End("collect", sw, $"renderers={ctx.Renderers.Count}");
            ctx.AnimFacts = facts;
        }

        private void StageDedupeImport(AtoContext ctx)
        {
            ctx.Progress.Set(2, AtoI18n.T("progress.dedupe"));
            var sw = AtoLog.Start("dedupe-import");
            var all = new List<Texture2D>();
            foreach (var r in ctx.Renderers)
            foreach (var m in r.sharedMaterials)
            {
                if (m == null) continue;
                foreach (var n in m.GetTexturePropertyNames())
                    if (m.GetTexture(n) is Texture2D t) all.Add(t);
            }
            var groups = new Dictionary<string, Texture2D>();
            var map = new Dictionary<Texture2D, Texture2D>();
            foreach (var t in all.Distinct())
            {
                Color32[] px = null;
                try { px = ctx.GetPixels(t); } catch { /* skip */ }
                var key = AtoTextureIO.ImporterKey(t) + "|" + (px != null ? AtoHash.Color32Span(px) : t.GetInstanceID().ToString());
                if (groups.TryGetValue(key, out var canon))
                {
                    if (canon != t) map[t] = canon;
                    if (ctx.WhitelistTextures.Contains(t) || ctx.WhitelistTextures.Contains(canon))
                    {
                        ctx.WhitelistTextures.Add(canon);
                        ctx.WhitelistTextures.Add(t);
                    }
                }
                else groups[key] = t;
            }
            if (map.Count > 0)
            {
                AtoLog.Info($"Import-time texture dedupe / 导入贴图去重: {map.Count}");
                AtoApply.ReplaceTextureRefs(ctx, map);
                foreach (var kv in map)
                    if (ctx.WhitelistTextures.Contains(kv.Key)) ctx.WhitelistTextures.Add(kv.Value);
            }
            ctx.Report.TexturesIn = groups.Count;
            AtoLog.End("dedupe-import", sw, $"unique={groups.Count} merged={map.Count}");
        }

        private void StageAnalyze(AtoContext ctx)
        {
            ctx.Progress.Set(3, AtoI18n.T("progress.analyze"));
            var sw = AtoLog.Start("analyze");
            var facts = ctx.AnimFacts ?? new AtoAnimFacts();
            var usesByUv = new Dictionary<AtoUvKey, List<AtoTextureUse>>();

            foreach (var r in ctx.Renderers)
            {
                var mesh = AtoApply.GetMesh(r);
                if (mesh == null) continue;
                var mats = r.sharedMaterials ?? Array.Empty<Material>();
                var extras = facts.ExtraMaterials.TryGetValue(r, out var em) ? em : null;
                var subCount = mesh.subMeshCount;
                for (var s = 0; s < subCount; s++)
                {
                    var slotMats = new List<Material>();
                    if (s < mats.Length && mats[s] != null) slotMats.Add(mats[s]);
                    if (extras != null)
                    {
                        foreach (var arr in extras)
                            if (s < arr.Length && arr[s] != null) slotMats.Add(arr[s]);
                    }
                    foreach (var mat in slotMats.Distinct())
                    {
                        if (ctx.WhitelistMaterials.Contains(mat) || ctx.WhitelistRenderers.Contains(r))
                        {
                            MarkMatTexturesWhite(ctx, mat);
                            continue;
                        }
                        var info = AtoShaderAnalyzer.Analyze(mat);
                        if (!info.Ok)
                        {
                            ErrorReport.ReportError(AtoI18n.NdmfLocalizer, ErrorSeverity.NonFatal, "warn.shader",
                                mat.name, mat.shader != null ? mat.shader.name : "?");
                            ctx.Report.Warnings++;
                            MarkMatTexturesWhite(ctx, mat);
                            continue;
                        }
                        foreach (var tp in info.Textures)
                        {
                            if (!AtoShaderAnalyzer.IsMeshUvSampled(tp.Name, mat)) continue;
                            var tex = mat.GetTexture(tp.Name) as Texture2D;
                            if (tex == null) continue;

                            var stBad = AtoShaderAnalyzer.HasNonIdentityST(tp.ST) ||
                                        (tp.HasScrollRotate && AtoShaderAnalyzer.HasNonZero(tp.ScrollRotate)) ||
                                        facts.TransformAnimated.Contains((mat, tp.Name));
                            var use = new AtoTextureUse
                            {
                                Texture = tex,
                                Material = mat,
                                Property = tp.Name,
                                Kind = RefineKind(tp, info, tex),
                                AlphaMode = StrictAlpha(info.Alpha, mat, facts),
                                Cutoff = StrictCutoff(info.Cutoff, mat, facts),
                                IsSrgb = AtoTextureIO.IsSrgb(tex) && tp.Kind != AtoTextureKind.Normal,
                                Filter = tex.filterMode,
                                UvChannel = tp.UvChannel,
                                HasNormalCompanion = info.HasNormal,
                                HasMaskCompanion = info.HasMask,
                                Whitelisted = stBad || ctx.WhitelistTextures.Contains(tex)
                            };
                            if (use.Kind == AtoTextureKind.OpaqueAlbedo && use.AlphaMode != AtoAlphaMode.Opaque)
                                use.Kind = AtoTextureKind.TransparentAlbedo;
                            if (use.Kind == AtoTextureKind.Gray || use.Kind == AtoTextureKind.Mask)
                                use.UsedGrayChannels = DetectGrayChannels(ctx, tex);

                            if (stBad)
                            {
                                ErrorReport.ReportError(AtoI18n.NdmfLocalizer, ErrorSeverity.NonFatal, "warn.st",
                                    tp.Name, mat.name);
                                ctx.Report.Warnings++;
                                ctx.WhitelistTextures.Add(tex);
                            }

                            foreach (var ext in AtoExtensionRegistry.All)
                            {
                                try
                                {
                                    if (!ext.ShouldProcessTexture(tex, mat, tp.Name))
                                    { use.Whitelisted = true; ctx.WhitelistTextures.Add(tex); }
                                    var k = ext.ClassifyTexture(tex, mat, tp.Name);
                                    if (k != AtoTextureKind.Unknown) use.Kind = k;
                                }
                                catch (Exception e) { AtoLog.Warn("Extension " + ext.Id + ": " + e.Message); }
                            }

                            // Animation extra textures on this property. / 动画在此属性上的额外贴图。
                            if (facts.ExtraTextures.TryGetValue(mat, out var map) &&
                                map.TryGetValue(tp.Name, out var extraTex))
                            {
                                foreach (var et in extraTex)
                                {
                                    var u2 = CloneUse(use);
                                    u2.Texture = et;
                                    u2.Whitelisted = u2.Whitelisted || ctx.WhitelistTextures.Contains(et);
                                    AddUse(ctx, usesByUv, r, s, u2);
                                }
                            }
                            AddUse(ctx, usesByUv, r, s, use);
                        }
                    }
                }
            }

            // Build UV groups. / 建立 UV 组。
            foreach (var kv in usesByUv)
            {
                var g = new AtoUvGroup { Id = ++ctx.UvGroupSerial, Key = kv.Key, Textures = kv.Value };
                g.Whitelisted = kv.Value.Any(u => u.Whitelisted);
                g.SkipAtlas = g.Whitelisted; // whitelist texture on this UV → companions skip atlas
                ctx.UvGroups[kv.Key] = g;
                if (g.Whitelisted) ctx.Report.Whitelisted += g.Textures.Count;
            }

            // Extract islands. / 提取岛。
            var blendCache = new Dictionary<Mesh, AtoIslandExtractor.BlendShapeArea>();
            foreach (var g in ctx.UvGroups.Values)
            {
                var mesh = AtoApply.GetMesh(g.Key.Renderer);
                if (mesh == null) continue;
                if (!blendCache.TryGetValue(mesh, out var blend))
                    blendCache[mesh] = blend = AtoIslandExtractor.BuildBlendMax(mesh);
                var tex = g.Textures.Select(t => t.Texture).FirstOrDefault(t => t != null);
                var tw = tex != null ? tex.width : 1024;
                var th = tex != null ? tex.height : 1024;
                var areaMul = AtoAnimationAnalyzer.HierarchyMaxScaleAreaMul(g.Key.Renderer.transform, facts);
                var extracted = AtoIslandExtractor.Extract(mesh, g.Key.Submesh, g.Key.UvChannel, tw, th, areaMul, blend);
                if (extracted.WrapCross)
                {
                    ErrorReport.ReportError(AtoI18n.NdmfLocalizer, ErrorSeverity.NonFatal, "warn.uvWrap",
                        g.Key.Renderer.name, g.Key.UvChannel.ToString());
                    ctx.Report.Warnings++;
                    g.Whitelisted = true;
                    g.SkipAtlas = true;
                    foreach (var u in g.Textures)
                    {
                        u.Whitelisted = true;
                        ctx.WhitelistTextures.Add(u.Texture);
                    }
                    continue;
                }
                foreach (var isl in extracted.Islands)
                {
                    isl.Id = ++ctx.IslandSerial;
                    isl.Uv = g.Key;
                    if (tex != null && AtoQualityEval.IsSolid(ctx, tex, out var sc))
                    {
                        isl.SolidColor = true;
                        isl.Solid = sc;
                    }
                    g.Islands.Add(isl);
                }
                ctx.Report.Islands += g.Islands.Count;
            }

            BuildTypeGroups(ctx);
            AtoLog.End("analyze", sw,
                $"uvGroups={ctx.UvGroups.Count} islands={ctx.Report.Islands} typeGroups={ctx.TypeGroups.Count}");
        }

        private static void BuildTypeGroups(AtoContext ctx)
        {
            var map = new Dictionary<(bool n, bool m, bool srgb, FilterMode f), AtoTypeGroup>();
            foreach (var g in ctx.UvGroups.Values)
            {
                var hasN = g.Textures.Any(t => t.HasNormalCompanion || t.Kind == AtoTextureKind.Normal);
                var hasM = g.Textures.Any(t => t.HasMaskCompanion || t.Kind == AtoTextureKind.Mask || t.Kind == AtoTextureKind.Gray);
                var srgb = g.Textures.Any(t => t.IsSrgb);
                var filter = g.Textures.Select(t => t.Filter).DefaultIfEmpty(FilterMode.Bilinear).Max();
                var key = (hasN, hasM, srgb, filter);
                if (!map.TryGetValue(key, out var tg))
                {
                    tg = new AtoTypeGroup
                    {
                        Id = ++ctx.TypeGroupSerial,
                        HasNormal = hasN, HasMask = hasM, IsSrgb = srgb, Filter = filter
                    };
                    map[key] = tg;
                    ctx.TypeGroups.Add(tg);
                }
                tg.UvGroups.Add(g);
            }
        }

        private void StageQuality(AtoContext ctx)
        {
            ctx.Progress.Set(4, AtoI18n.T("progress.quality"));
            var sw = AtoLog.Start("quality");
            var i = 0;
            var n = ctx.UvGroups.Count;
            foreach (var g in ctx.UvGroups.Values)
            {
                ctx.Progress.Inner((float)i++ / Math.Max(1, n));
                if (g.Whitelisted) continue;
                if (!ctx.Settings.generateAtlas || g.SkipAtlas)
                {
                    // Whole-texture scale. / 整图缩放。
                    foreach (var u in g.Textures.Select(t => t.Texture).Distinct())
                    {
                        if (u == null || ctx.WhitelistTextures.Contains(u)) continue;
                        AtoQualityEval.ScaleWholeTexture(ctx, u, g.Textures, out var w, out var h);
                        if (w == u.width && h == u.height) continue;
                        var scaled = ScaleTex(ctx, u, w, h);
                        ctx.TextureRemap[u] = scaled;
                    }
                }
                else
                {
                    AtoQualityEval.ScaleIslands(ctx, g);
                }
            }
            AtoLog.End("quality", sw);
        }

        private static Texture2D ScaleTex(AtoContext ctx, Texture2D src, int w, int h)
        {
            var srgb = AtoTextureIO.IsSrgb(src);
            var rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32,
                srgb ? RenderTextureReadWrite.sRGB : RenderTextureReadWrite.Linear);
            Graphics.Blit(ctx.GetReadable(src), rt);
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            var dst = new Texture2D(w, h, TextureFormat.RGBA32, true, !srgb) { name = "ATO_" + src.name };
            dst.ReadPixels(new Rect(0, 0, w, h), 0, 0, false);
            dst.Apply(true, false);
            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(rt);
            ctx.RegisterTemp(dst);
            ObjectRegistry.RegisterReplacedObject(src, dst);
            return dst;
        }

        private void StageAtlas(AtoContext ctx)
        {
            ctx.Progress.Set(5, AtoI18n.T("progress.atlas"));
            var sw = AtoLog.Start("atlas");
            if (!ctx.Settings.generateAtlas)
            {
                AtoLog.Info("Atlas generation disabled / 未生成图集");
                AtoLog.End("atlas", sw);
                return;
            }

            var pool = AtoAtlasPacker.CandidatePool(ctx);
            var minPad = (int)ctx.Settings.minPadding;

            foreach (var tg in ctx.TypeGroups)
            {
                var queue = tg.UvGroups
                    .Where(g => !g.SkipAtlas && !g.Whitelisted && g.Islands.Count > 0)
                    .OrderByDescending(g => g.Islands.Sum(i => i.TargetW * i.TargetH))
                    .ToList();

                while (queue.Count > 0)
                {
                    var g = queue[0];
                    var area = g.Islands.Sum(i => i.TargetW * i.TargetH);
                    var cands = AtoAtlasPacker.SortCandidates(pool, area);
                    var packed = false;
                    foreach (var cand in cands)
                    {
                        if (TryPackGroup(ctx, tg, g, cand.w, cand.h, minPad))
                        {
                            packed = true;
                            break;
                        }
                    }
                    if (!packed)
                    {
                        g.FailedAtlas = true;
                        g.SkipAtlas = true;
                        ErrorReport.ReportError(AtoI18n.NdmfLocalizer, ErrorSeverity.NonFatal, "warn.atlasFail",
                            g.Textures.FirstOrDefault()?.Texture?.name ?? "?", g.Key.ToString());
                        ctx.Report.Warnings++;
                        // Fallback: whole-texture scale already done if generateAtlas... we still scale whole.
                        foreach (var u in g.Textures.Select(t => t.Texture).Distinct())
                        {
                            if (u == null) continue;
                            AtoQualityEval.ScaleWholeTexture(ctx, u, g.Textures, out var w, out var h);
                            if (w != u.width || h != u.height)
                                ctx.TextureRemap[u] = ScaleTex(ctx, u, w, h);
                        }
                    }
                    queue.RemoveAt(0);
                }
            }
            AtoLog.End("atlas", sw, $"atlases={ctx.Atlases.Count}");
        }

        private static bool TryPackGroup(AtoContext ctx, AtoTypeGroup tg, AtoUvGroup g, int aw, int ah, int minPad)
        {
            var padPx = AtoAtlasPacker.PaddingFor(Mathf.Max(aw, ah), minPad);
            var padCells = Mathf.CeilToInt(padPx / (float)AtoAtlasPacker.Cell);
            var cellsW = aw / AtoAtlasPacker.Cell;
            var cellsH = ah / AtoAtlasPacker.Cell;
            if (cellsW <= 0 || cellsH <= 0) return false;
            var occ = new byte[cellsW * cellsH];

            var mesh = AtoApply.GetMesh(g.Key.Renderer);
            if (mesh == null) return false;
            var srcTex = g.Textures.FirstOrDefault(t => t.Texture != null)?.Texture;
            if (srcTex == null) return false;

            var places = new List<(AtoIsland isl, AtoAtlasPacker.Place place, NativeMaskRef mask)>();
            var ordered = g.Islands.OrderByDescending(i => i.TargetW * i.TargetH)
                .ThenByDescending(i => Mathf.Max(i.TargetW, i.TargetH)).ToList();
            foreach (var isl in ordered)
            {
                var full = AtoAtlasPacker.RasterIsland(ctx, mesh, g.Key.Submesh, g.Key.UvChannel, isl, srcTex.width, srcTex.height);
                var mask = AtoAtlasPacker.CropToIsland(full, isl, srcTex.width, srcTex.height);
                // Scale mask cells to target size. / 把 mask 缩放到目标尺寸。
                mask = ResampleMask(mask, Mathf.Max(1, isl.TargetW / AtoAtlasPacker.Cell),
                    Mathf.Max(1, isl.TargetH / AtoAtlasPacker.Cell));
                var mask90 = AtoAtlasPacker.Transpose(mask);
                var place = AtoAtlasPacker.FindPlace(occ, cellsW, cellsH, mask, mask90, padCells);
                if (!place.Ok) return false;
                AtoAtlasPacker.Stamp(occ, cellsW, place.Rot90 ? mask90 : mask, place.X, place.Y);
                places.Add((isl, place, mask));
            }

            // Commit. / 提交。
            var atlasId = ++ctx.AtlasSerial;
            foreach (var p in places)
            {
                p.isl.AtlasIndex = atlasId;
                p.isl.Rotated90 = p.place.Rot90;
                p.isl.AtlasPos = new Vector2Int(p.place.X * AtoAtlasPacker.Cell, p.place.Y * AtoAtlasPacker.Cell);
            }

            // One atlas per kind in this UV group (albedo / normal / mask) sharing positions.
            // 该 UV 组内每种贴图一张图集，位置相同。
            var kinds = g.Textures.Select(t => t.Kind).Distinct().ToList();
            foreach (var kind in kinds)
            {
                var uses = g.Textures.Where(t => t.Kind == kind && t.Texture != null).ToList();
                if (uses.Count == 0) continue;
                var primary = uses[0].Texture;
                var stamps = new List<(AtoIsland, Texture2D, bool, bool)>();
                foreach (var isl in g.Islands)
                    stamps.Add((isl, primary, kind == AtoTextureKind.Normal, isl.Rotated90));
                var hasAlpha = kind == AtoTextureKind.TransparentAlbedo;
                var name = $"ATO_{kind}_{atlasId}_{primary.name}";
                var tex = AtoApply.BuildAtlasTexture(ctx, aw, ah, uses[0].IsSrgb, hasAlpha, stamps, name);
                ApplyFormat(ctx, tex, kind, hasAlpha, uses[0].IsSrgb);
                var atlas = new AtoAtlas
                {
                    Id = atlasId, Name = name, Kind = kind, Width = aw, Height = ah, Texture = tex,
                    Islands = g.Islands, Sources = uses.Select(u => u.Texture).Distinct().ToList()
                };
                var used = g.Islands.Sum(i => i.TargetW * (long)i.TargetH);
                atlas.Utilization = (float)used / (aw * (long)ah);
                atlas.OrigBytes = atlas.Sources.Sum(s => s.width * (long)s.height);
                atlas.NewBytes = aw * (long)ah;
                ctx.Atlases.Add(atlas);
                tg.Atlases.Add(atlas);
                foreach (var u in uses) ctx.TextureRemap[u.Texture] = tex;
                ctx.Report.AtlasLines.Add(
                    $"{name} {aw}x{ah} util={atlas.Utilization:P1} src=[{string.Join(",", atlas.Sources.Select(s => s.name))}] islands={g.Islands.Count}");
                AtoLog.Info($"Atlas {name} {aw}x{ah} util={atlas.Utilization:P1} islands={g.Islands.Count}");
            }

            AtoApply.RemapMeshUv(ctx, g, aw, ah);
            ctx.Report.Atlases = ctx.Atlases.Count;
            return true;
        }

        private static NativeMaskRef ResampleMask(NativeMaskRef src, int w, int h)
        {
            w = Mathf.Max(1, w); h = Mathf.Max(1, h);
            var bits = new ulong[(w * h + 63) / 64];
            var r = new NativeMaskRef { CellsW = w, CellsH = h, Bits = bits };
            for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var sx = src.CellsW == 0 ? 0 : x * src.CellsW / w;
                var sy = src.CellsH == 0 ? 0 : y * src.CellsH / h;
                if (AtoAtlasPacker.Get(src, sx, sy))
                    AtoAtlasPacker.Set(bits, w, x, y, true);
            }
            return r;
        }

        private static void ApplyFormat(AtoContext ctx, Texture2D tex, AtoTextureKind kind, bool hasAlpha, bool srgb)
        {
            var f = ctx.Settings.formats;
            AtoSafeFormat want;
            bool mip;
            switch (kind)
            {
                case AtoTextureKind.Normal:
                    want = f.normalFormat; mip = f.normalMipStreaming; break;
                case AtoTextureKind.Gray:
                case AtoTextureKind.Mask:
                    want = f.grayFormat; mip = f.grayMipStreaming;
                    if (want == AtoSafeFormat.BC4 || want == AtoSafeFormat.DXT1)
                    {
                        ErrorReport.ReportError(AtoI18n.NdmfLocalizer, ErrorSeverity.NonFatal, "warn.grayFallback", tex.name);
                        ctx.Report.Warnings++;
                        want = AtoSafeFormat.Auto;
                    }
                    break;
                case AtoTextureKind.TransparentAlbedo:
                    want = f.transparentFormat; mip = f.transparentMipStreaming; break;
                default:
                    want = f.opaqueFormat; mip = f.opaqueMipStreaming; break;
            }
            if (ctx.Settings.experimentalNpot && !AtoTextureIO.FormatAllowedForNpot(want))
                want = AtoSafeFormat.Auto;
            tex.filterMode = FilterMode.Bilinear;
            tex.wrapMode = TextureWrapMode.Clamp;
            AtoTextureIO.ApplyImporterLike(tex, want, srgb, mip, ctx.Platform, kind == AtoTextureKind.Normal, hasAlpha);
        }

        private void StageApply(AtoContext ctx)
        {
            ctx.Progress.Set(6, AtoI18n.T("progress.apply"));
            var sw = AtoLog.Start("apply");
            if (ctx.TextureRemap.Count > 0)
                AtoApply.ReplaceTextureRefs(ctx, ctx.TextureRemap);
            AtoApply.DedupeTexturesAndMaterials(ctx);
            AtoLog.End("apply", sw, $"remap={ctx.TextureRemap.Count}");
        }

        private void StageFinish(AtoContext ctx)
        {
            ctx.Progress.Set(7, AtoI18n.T("progress.finish"));
            var sw = AtoLog.Start("finish");
            foreach (var ext in AtoExtensionRegistry.All)
            {
                try { ext.OnAfterOptimize(ctx.Avatar, ctx.Atlases.Select(a => a.Texture).ToList()); }
                catch (Exception e) { AtoLog.Warn("Extension finish " + ext.Id + ": " + e.Message); }
            }

            // Remove ATO component from baked avatar. / 从成品上移除 ATO 组件。
            foreach (var c in ctx.Avatar.GetComponentsInChildren<AvatarTextureOptimizer>(true))
                Object.DestroyImmediate(c);

            ctx.Report.TexturesOut = ctx.Atlases.Count + ctx.TextureRemap.Values.Distinct().Count();
            ctx.Report.OrigPixels = ctx.Report.TexturesIn; // rough
            var sb = new StringBuilder();
            sb.AppendLine($"renderers={ctx.Report.Renderers} islands={ctx.Report.Islands} atlases={ctx.Report.Atlases} warnings={ctx.Report.Warnings} whitelist={ctx.Report.Whitelisted}");
            foreach (var line in ctx.Report.AtlasLines) sb.AppendLine(line);
            var summary = sb.ToString();
            ErrorReport.ReportError(AtoI18n.NdmfLocalizer, ErrorSeverity.Information, "report", summary);
            AtoLog.Info("REPORT\n" + summary);
            AtoLog.End("finish", sw);
        }

        private static void MarkMatTexturesWhite(AtoContext ctx, Material mat)
        {
            if (mat == null) return;
            foreach (var n in mat.GetTexturePropertyNames())
                if (mat.GetTexture(n) is Texture2D t) ctx.WhitelistTextures.Add(t);
        }

        private static AtoTextureKind RefineKind(AtoShaderAnalyzer.TexProp tp, AtoShaderAnalyzer.MaterialInfo info, Texture2D tex)
        {
            if (tp.IsNormal || AtoTextureIO.IsNormalMap(tex)) return AtoTextureKind.Normal;
            if (tp.IsGray || tp.IsMask) return AtoTextureKind.Gray;
            if (info.Alpha != AtoAlphaMode.Opaque) return AtoTextureKind.TransparentAlbedo;
            return AtoTextureKind.OpaqueAlbedo;
        }

        private static AtoAlphaMode StrictAlpha(AtoAlphaMode a, Material mat, AtoAnimFacts facts)
        {
            var best = a;
            if (facts.ExtraAlpha.TryGetValue(mat, out var set))
            {
                if (set.Contains(AtoAlphaMode.Blend)) best = AtoAlphaMode.Blend;
                else if (set.Contains(AtoAlphaMode.Cutout) && best == AtoAlphaMode.Opaque) best = AtoAlphaMode.Cutout;
            }
            return best;
        }

        private static float StrictCutoff(float c, Material mat, AtoAnimFacts facts)
        {
            if (facts.StrictestCutoff.TryGetValue(mat, out var s)) return Mathf.Max(c, s);
            return c;
        }

        private static int DetectGrayChannels(AtoContext ctx, Texture2D tex)
        {
            var px = ctx.GetPixels(tex);
            var mask = 0;
            if (px == null || px.Length == 0) return 1;
            var r = false; var g = false; var b = false; var aVar = false;
            var r0 = px[0].r; var g0 = px[0].g; var b0 = px[0].b; var a0 = px[0].a;
            for (var i = 0; i < px.Length; i += Math.Max(1, px.Length / 2048))
            {
                if (px[i].r != r0) r = true;
                if (px[i].g != g0) g = true;
                if (px[i].b != b0) b = true;
                if (px[i].a != a0) aVar = true;
            }
            if (r) mask |= 1;
            if (g) mask |= 2;
            if (b) mask |= 4;
            if (aVar) mask |= 8;
            if (mask == 0) mask = 1;
            return mask;
        }

        private static AtoTextureUse CloneUse(AtoTextureUse u)
        {
            return new AtoTextureUse
            {
                Texture = u.Texture, Material = u.Material, Property = u.Property, Kind = u.Kind,
                AlphaMode = u.AlphaMode, Cutoff = u.Cutoff, IsSrgb = u.IsSrgb, Filter = u.Filter,
                UvChannel = u.UvChannel, HasNormalCompanion = u.HasNormalCompanion,
                HasMaskCompanion = u.HasMaskCompanion, Whitelisted = u.Whitelisted,
                SkipAtlas = u.SkipAtlas, UsedGrayChannels = u.UsedGrayChannels
            };
        }

        private static void AddUse(AtoContext ctx, Dictionary<AtoUvKey, List<AtoTextureUse>> map,
            Renderer r, int sub, AtoTextureUse use)
        {
            var key = new AtoUvKey(r, sub, use.UvChannel);
            if (!map.TryGetValue(key, out var list)) map[key] = list = new List<AtoTextureUse>();
            list.Add(use);
            ctx.Uses.Add(use);
        }
    }
}
