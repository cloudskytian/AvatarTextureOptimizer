using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using nadena.dev.ndmf;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;
using Fosa.ATO;

namespace Fosa.ATO.Editor
{
    /// <summary>
    /// Main bake pipeline. Runs after MA, before AAO.
    /// 主烘焙管线。MA 之后、AAO 之前。
    /// </summary>
    public static class AtoPipeline
    {
        public static void Run(BuildContext ctx)
        {
            var report = new AtoReport();
            var swAll = Stopwatch.StartNew();
            using var progress = new AtoProgress();
            var gpu = new List<RenderTexture>();
            var native = new List<IDisposable>();
            var cache = new AtoCache();
            native.Add(cache);
            AtoShaderAnalyzer.ClearBakeCache();

            try
            {
                progress.Set(AtoLoc.T("ato.progress.validate"), 0.02f);
                var components = ctx.AvatarRootObject.GetComponentsInChildren<AvatarTextureOptimizer>(true);
                if (components.Length == 0)
                {
                    AtoLog.Detail("No AvatarTextureOptimizer on avatar, skip");
                    return;
                }
                if (components.Length > 1)
                {
                    ErrorReport.ReportError(AtoLoc.NdmfLocalizer, ErrorSeverity.Error, "ato.error.multiple");
                    return;
                }
                var comp = components[0];
                if (comp.gameObject != ctx.AvatarRootObject)
                {
                    ErrorReport.ReportError(AtoLoc.NdmfLocalizer, ErrorSeverity.Error, "ato.error.notRoot", comp.gameObject.name);
                    return;
                }
                if (!comp.HasAvatarDescriptor())
                {
                    ErrorReport.ReportError(AtoLoc.NdmfLocalizer, ErrorSeverity.Error, "ato.error.noDescriptor");
                    return;
                }

                AtoLoc.SetOverride(comp.languageMode == AtoLanguageMode.Manual ? comp.languageCode : null);
                AtoLog.Verbose = comp.verboseLog;
                if (comp.platform == AtoBuildPlatform.Auto)
                {
                    switch (EditorUserBuildSettings.activeBuildTarget)
                    {
                        case BuildTarget.Android: comp.platform = AtoBuildPlatform.Android; break;
                        case BuildTarget.iOS: comp.platform = AtoBuildPlatform.iOS; break;
                        default: comp.platform = AtoBuildPlatform.PC; break;
                    }
                    // Do not serialize — this is the bake clone. 只改烘焙克隆，不写回工程。
                }
                var settings = comp.ResolveSettings();
                AtoLog.Info("Bake start platform=" + settings.platform + " atlas=" + settings.generateAtlas
                            + " preset=" + settings.qualityPreset + " npot=" + settings.experimentalNpot);

                var bake = new AtoBakeContext
                {
                    AvatarRoot = ctx.AvatarRootObject,
                    Component = comp,
                    Settings = settings,
                    Report = report
                };
                AtoApi.RaiseBeforeAnalyze(bake);

                progress.Set(AtoLoc.T("ato.progress.scan"), 0.08f);
                AtoAnimInfo anim;
                using (AtoLog.Time("scan-animation"))
                    anim = AtoAnimationScanner.Scan(ctx);

                progress.Set(AtoLoc.T("ato.progress.collect"), 0.15f);
                var renderers = CollectRenderers(ctx.AvatarRootObject, anim);
                report.Renderers = renderers.Count;
                AtoLog.Info("Renderers=" + renderers.Count);

                var whitelistSet = BuildWhitelist(comp, ctx.AvatarRootObject);
                var refs = new List<AtoTextureRef>();
                var texSeen = new Dictionary<Texture2D, Texture2D>();

                using (AtoLog.Time("analyze-materials"))
                {
                    foreach (var r in renderers)
                    {
                        progress.Set(AtoLoc.T("ato.progress.analyze") + " " + r.name, 0.15f + 0.15f * refs.Count / 64f);
                        CollectFromRenderer(ctx, r, anim, whitelistSet, refs, report);
                    }
                }

                var texRemap = new Dictionary<Texture2D, Texture2D>();
                var matRemap = new Dictionary<Material, Material>();
                var meshRemap = new Dictionary<Mesh, Mesh>();

                // Dedup textures by content+importer before mapping. 先按内容+导入设置去重。
                using (AtoLog.Time("dedup-textures"))
                    DedupIncoming(ctx, refs, whitelistSet, report, texRemap);

                report.TexturesSeen = refs.Select(x => x.Texture).Where(t => t != null).Distinct().Count();
                report.MaterialsSeen = refs.Select(x => x.Material).Where(m => m != null).Distinct().Count();
                bake.TextureRefs = refs;
                AtoApi.RaiseAfterAnalyze(bake);

                var eligible = refs.Where(x => x.Eligible && !x.Whitelisted && x.Texture != null).ToList();
                report.Whitelisted = refs.Count(x => x.Whitelisted);
                report.SkippedIneligible = refs.Count(x => !x.Eligible);
                foreach (var x in refs.Where(x => !x.Eligible && !string.IsNullOrEmpty(x.SkipReason)))
                    report.Warnings.Add(x.Renderer.name + " " + x.PropertyName + ": " + x.SkipReason);

                progress.Set(AtoLoc.T("ato.progress.optimize"), 0.35f);
                AtoApi.RaiseBeforeApply(bake);

                if (eligible.Count == 0 && refs.Count == 0)
                {
                    AtoLog.Info("Nothing eligible to optimize");
                }
                else if (!settings.generateAtlas)
                {
                    using (AtoLog.Time("scale-whole-textures"))
                        ScaleWholeTextures(ctx, eligible, settings, texRemap, report, progress, cache);
                }
                else
                {
                    using (AtoLog.Time("atlas"))
                        AtoAtlasBuilder.Run(ctx, eligible, refs, anim, settings, texRemap, meshRemap, report, progress, cache);
                }

                progress.Set(AtoLoc.T("ato.progress.rebind"), 0.82f);
                using (AtoLog.Time("rebind"))
                    RebindAll(ctx, refs, texRemap, matRemap, meshRemap);

                AtoApply.RewriteAnimationTextures(ctx, texRemap, matRemap);

                progress.Set(AtoLoc.T("ato.progress.dedup"), 0.90f);
                var solo = new HashSet<Renderer>();
                foreach (var kv in anim.ExtraMaterialSets) solo.Add(kv.Key);
                using (AtoLog.Time("post-dedup"))
                    AtoApply.DedupMaterialsAndTextures(ctx, settings.dedupMaterials, settings.dedupTextures,
                        texRemap, matRemap, solo, report);

                AtoApply.RewriteAnimationTextures(ctx, texRemap, matRemap);

                progress.Set(AtoLoc.T("ato.progress.cleanup"), 0.96f);
                foreach (var c in ctx.AvatarRootObject.GetComponentsInChildren<AvatarTextureOptimizer>(true))
                    Object.DestroyImmediate(c);

                AtoApi.RaiseAfterApply(bake);
            }
            catch (OperationCanceledException)
            {
                AtoLog.Warn("Cancelled by user — temp assets kept, CPU/GPU/memory released");
                ErrorReport.ReportError(AtoLoc.NdmfLocalizer, ErrorSeverity.Error, "ato.error.cancelled");
            }
            catch (Exception e)
            {
                AtoLog.Error("Pipeline failed: " + e);
                ErrorReport.ReportException(e);
            }
            finally
            {
                foreach (var rt in gpu) if (rt != null) rt.Release();
                foreach (var d in native) try { d.Dispose(); } catch { /* ignore */ }
                EditorUtility.ClearProgressBar();
                swAll.Stop();
                report.TotalMs = swAll.ElapsedMilliseconds;
                AtoLog.Info("DONE " + report.TotalMs + " ms atlases=" + report.Atlases
                            + " islands=" + report.Islands
                            + " before=" + AtoLog.Bytes(report.BytesBefore)
                            + " after=" + AtoLog.Bytes(report.BytesAfter));
                try { report.EmitToNdmf(); }
                catch (Exception e) { AtoLog.Warn("Report emit failed: " + e.Message); }
            }
        }

        static List<Renderer> CollectRenderers(GameObject root, AtoAnimInfo anim)
        {
            var list = new List<Renderer>();
            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
            {
                if (!(r is MeshRenderer || r is SkinnedMeshRenderer)) continue;
                if (r.CompareTag("EditorOnly") || r.gameObject.CompareTag("EditorOnly")) continue;
                bool enabled = r.gameObject.activeInHierarchy && r.enabled;
                if (!enabled && !anim.RenderersEnabledByAnim.Contains(r) && !anim.EnabledByAnim.Contains(r.transform))
                    continue;
                list.Add(r);
            }
            return list;
        }

        static HashSet<Object> BuildWhitelist(AvatarTextureOptimizer comp, GameObject root)
        {
            var set = new HashSet<Object>();
            if (comp.whitelist == null) return set;
            foreach (var o in comp.whitelist)
            {
                if (o == null) continue;
                set.Add(o);
                ExpandRefs(o, set, 0);
            }
            return set;
        }

        static void ExpandRefs(Object o, HashSet<Object> set, int depth)
        {
            if (depth > 4 || o == null) return;
            if (o is Renderer r)
            {
                foreach (var m in r.sharedMaterials) if (m != null && set.Add(m)) ExpandRefs(m, set, depth + 1);
                if (r is SkinnedMeshRenderer sm && sm.sharedMesh != null) set.Add(sm.sharedMesh);
                if (r is MeshRenderer mr)
                {
                    var mf = mr.GetComponent<MeshFilter>();
                    if (mf && mf.sharedMesh) set.Add(mf.sharedMesh);
                }
            }
            else if (o is Material m)
            {
                if (m.shader == null) return;
                int n = m.shader.GetPropertyCount();
                for (int i = 0; i < n; i++)
                {
                    if (m.shader.GetPropertyType(i) != UnityEngine.Rendering.ShaderPropertyType.Texture) continue;
                    var t = m.GetTexture(m.shader.GetPropertyName(i));
                    if (t != null) set.Add(t);
                }
            }
            else if (o is AnimationClip clip)
            {
                foreach (var b in AnimationUtility.GetObjectReferenceCurveBindings(clip))
                {
                    var keys = AnimationUtility.GetObjectReferenceCurve(clip, b);
                    if (keys == null) continue;
                    foreach (var k in keys) if (k.value != null) set.Add(k.value);
                }
            }
        }

        static void CollectFromRenderer(
            BuildContext ctx, Renderer r, AtoAnimInfo anim,
            HashSet<Object> whitelist, List<AtoTextureRef> refs, AtoReport report)
        {
            Mesh mesh = null;
            if (r is SkinnedMeshRenderer smr) mesh = smr.sharedMesh;
            else
            {
                var mf = r.GetComponent<MeshFilter>();
                mesh = mf ? mf.sharedMesh : null;
            }
            if (mesh == null) return;

            var mats = r.sharedMaterials ?? Array.Empty<Material>();
            var extra = new List<Material[]>();
            if (anim.ExtraMaterialSets.TryGetValue(r, out var sets)) extra.AddRange(sets);
            extra.Add(mats);

            var considered = new HashSet<Material>();
            foreach (var arr in extra)
            foreach (var mat in arr)
            {
                if (mat == null || !considered.Add(mat)) continue;
                var info = AtoShaderAnalyzer.Analyze(mat);
                if (!info.Compatible)
                {
                    report.Warnings.Add(mat.name + " shader incompatible: " + info.Warning);
                    continue;
                }
                int slot = Array.IndexOf(mats, mat);
                if (slot < 0) slot = 0;
                foreach (var s in info.Slots)
                {
                    var tex = mat.GetTexture(s.PropertyName) as Texture2D;
                    if (tex == null) continue;

                    var tr = new AtoTextureRef
                    {
                        Texture = tex,
                        Material = mat,
                        Renderer = r,
                        Mesh = mesh,
                        MaterialSlot = slot,
                        UvChannel = s.UvChannel,
                        PropertyName = s.PropertyName,
                        Class = s.Class,
                        AlphaMode = info.AlphaMode,
                        Cutoff = info.Cutoff,
                        Linear = !tex.isDataSRGB,
                        Filter = tex.filterMode,
                        WrapU = tex.wrapModeU,
                        WrapV = tex.wrapModeV,
                        Eligible = true
                    };

                    bool wl = whitelist.Contains(tex) || whitelist.Contains(mat) || whitelist.Contains(r)
                              || whitelist.Contains(mesh) || whitelist.Contains(r.gameObject);
                    if (wl)
                    {
                        tr.Whitelisted = true;
                        tr.Eligible = false;
                        tr.SkipReason = "whitelist";
                    }
                    else if (s.SpecialPurpose || s.UvChannel < 0)
                    {
                        tr.Eligible = false;
                        tr.Whitelisted = true;
                        tr.SkipReason = "special UV/purpose";
                        report.Warnings.Add(r.name + "." + s.PropertyName + " special-purpose → whitelist");
                    }
                    else if (AtoShaderAnalyzer.HasNonIdentityST(mat, s.PropertyName)
                             || anim.HasTexTransformAnim.Contains((r, s.PropertyName)))
                    {
                        tr.Eligible = false;
                        tr.Whitelisted = true;
                        tr.SkipReason = "ST/scroll/rotate";
                        report.Warnings.Add(r.name + "." + s.PropertyName + " has ST/transform → whitelist");
                    }
                    else if (tex.dimension != UnityEngine.Rendering.TextureDimension.Tex2D)
                    {
                        tr.Eligible = false;
                        tr.Whitelisted = true;
                        tr.SkipReason = "not Texture2D";
                    }

                    // Refine opaque vs transparent from pixels + alpha mode.
                    if (tr.Eligible && tr.Class == AtoTextureClass.Opaque && info.AlphaMode != AtoAlphaMode.Opaque)
                        tr.Class = AtoTextureClass.Transparent;

                    refs.Add(tr);

                    // Animation extra textures for this property. 动画额外贴图并入同一 UV。
                    foreach (var kv in anim.ExtraTextures)
                    {
                        if (kv.Key.Item1 != r) continue;
                        if (kv.Key.Item3 != s.PropertyName && kv.Key.Item3 != "material." + s.PropertyName) continue;
                        foreach (var extraTex in kv.Value)
                        {
                            if (extraTex == null || extraTex == tex) continue;
                            var tr2 = new AtoTextureRef
                            {
                                Texture = extraTex,
                                Material = mat,
                                Renderer = r,
                                Mesh = mesh,
                                MaterialSlot = slot,
                                UvChannel = s.UvChannel,
                                PropertyName = s.PropertyName,
                                Class = tr.Class,
                                AlphaMode = tr.AlphaMode,
                                Cutoff = tr.Cutoff,
                                Linear = !extraTex.isDataSRGB,
                                Filter = extraTex.filterMode,
                                Eligible = tr.Eligible,
                                Whitelisted = tr.Whitelisted
                            };
                            refs.Add(tr2);
                        }
                    }
                }
            }

            // Strictest alpha from animation. 动画里取最严透明要求。
            if (anim.ExtraAlpha.TryGetValue((r, 0), out var alphas))
            {
                foreach (var tr in refs)
                {
                    if (tr.Renderer != r) continue;
                    foreach (var a in alphas)
                    {
                        if (a.mode > tr.AlphaMode) tr.AlphaMode = a.mode;
                        tr.Cutoff = Mathf.Max(tr.Cutoff, a.cutoff);
                    }
                }
            }
        }

        static void DedupIncoming(BuildContext ctx, List<AtoTextureRef> refs, HashSet<Object> whitelist, AtoReport report,
            Dictionary<Texture2D, Texture2D> texRemap)
        {
            var map = new Dictionary<string, Texture2D>();
            var remap = new Dictionary<Texture2D, Texture2D>();
            foreach (var tr in refs)
            {
                if (tr.Texture == null) continue;
                string h;
                try { h = AtoTextureUtil.ContentHash(tr.Texture); }
                catch { continue; }
                if (!map.TryGetValue(h, out var canon))
                    map[h] = tr.Texture;
                else if (canon != tr.Texture)
                    remap[tr.Texture] = canon;
            }
            int n = 0;
            foreach (var tr in refs)
            {
                if (tr.Texture != null && remap.TryGetValue(tr.Texture, out var c))
                {
                    if (whitelist.Contains(tr.Texture) || whitelist.Contains(c))
                    {
                        whitelist.Add(c);
                        tr.Whitelisted = true;
                        tr.Eligible = false;
                    }
                    if (texRemap != null) texRemap[tr.Texture] = c;
                    tr.Texture = c;
                    n++;
                }
            }
            report.TexturesDeduped = n;
            AtoLog.Info("Incoming texture dedup replacements=" + n);
        }

        internal static void ScaleWholeTexturesPublic(
            BuildContext ctx, List<AtoTextureRef> eligible, AtoResolvedSettings settings,
            Dictionary<Texture2D, Texture2D> texRemap, AtoReport report, AtoCache cache)
        {
            ScaleWholeTextures(ctx, eligible, settings, texRemap, report, null, cache);
        }

        static void ScaleWholeTextures(
            BuildContext ctx, List<AtoTextureRef> eligible, AtoResolvedSettings settings,
            Dictionary<Texture2D, Texture2D> texRemap, AtoReport report, AtoProgress progress, AtoCache cache)
        {
            var unique = eligible.Select(e => e.Texture).Distinct().ToList();
            int i = 0;
            foreach (var tex in unique)
            {
                progress?.Set("scale " + tex.name, 0.35f + 0.4f * i++ / Math.Max(1, unique.Count));
                var users = eligible.Where(e => e.Texture == tex).ToList();
                var cls = StrictestClass(users);
                var alpha = StrictestAlpha(users, out float cutoff);
                report.BytesBefore += AtoTextureUtil.UncompressedBytes(tex);

                var px = cache != null ? cache.Get(tex) : AtoTextureUtil.ReadPixels(tex);
                int w = tex.width, h = tex.height;
                bool lossless = settings.quality.IsLossless || settings.qualityPreset == AtoQualityPreset.Lossless;
                bool solid = AtoTextureUtil.IsSolidColor(px);
                float minS = 4f / Math.Max(w, h);
                var sc = AtoQuality.SearchScale(px, w, h, tex.isDataSRGB, cls, alpha, cutoff,
                    settings.quality, minS, lossless, solid);
                int nw = Math.Max(1, Mathf.RoundToInt(w * sc.x));
                int nh = Math.Max(1, Mathf.RoundToInt(h * sc.y));
                // Pixel density clamp is per-island; whole-texture path clamps to original size.
                nw = Math.Min(nw, w); nh = Math.Min(nh, h);
                Color[] outPx = px;
                if (nw != w || nh != h)
                {
                    if (cls == AtoTextureClass.Normal)
                        outPx = AtoTextureUtil.ResampleNormal(px, w, h, nw, nh);
                    else
                    {
                        bool premul = cls == AtoTextureClass.Transparent || alpha != AtoAlphaMode.Opaque;
                        outPx = AtoGpu.ResampleOrCpu(px, w, h, nw, nh, premul, tex.isDataSRGB && cls != AtoTextureClass.Normal);
                    }
                }
                else if (lossless)
                    outPx = px;

                var mips = settings.formats.ForClass(cls).mipAndStreaming;
                var nt = AtoTextureUtil.Create(AvatarTextureOptimizer.AtlasNamePrefix + tex.name, nw, nh, outPx,
                    !tex.isDataSRGB || cls == AtoTextureClass.Normal, mips);
                nt.filterMode = tex.filterMode;
                nt.anisoLevel = tex.anisoLevel;
                nt.wrapMode = TextureWrapMode.Clamp;
                bool linearOut = !tex.isDataSRGB || cls == AtoTextureClass.Normal || cls == AtoTextureClass.Gray;
                nt = AtoExport.Commit(ctx, nt, cls, settings, report, nt.filterMode, nt.anisoLevel, linearOut);
                ObjectRegistry.RegisterReplacedObject(tex, nt);
                texRemap[tex] = nt;
                report.BytesAfter += AtoTextureUtil.UncompressedBytes(nt);
                AtoLog.Detail("Scaled " + tex.name + " " + w + "x" + h + " -> " + nw + "x" + nh);
            }
        }

        static void RebindAll(
            BuildContext ctx, List<AtoTextureRef> refs,
            Dictionary<Texture2D, Texture2D> texRemap,
            Dictionary<Material, Material> matRemap,
            Dictionary<Mesh, Mesh> meshRemap)
        {
            var cloned = new Dictionary<Material, Material>();
            Texture2D Resolve(Texture2D t)
            {
                var seen = new HashSet<Texture2D>();
                while (t != null && texRemap.TryGetValue(t, out var n) && n != t && seen.Add(t)) t = n;
                return t;
            }
            foreach (var tr in refs)
            {
                if (tr.Material == null || tr.Texture == null) continue;
                var nt = Resolve(tr.Texture);
                if (nt == null || nt == tr.Texture && !texRemap.ContainsKey(tr.Texture)) continue;
                if (!cloned.TryGetValue(tr.Material, out var cm))
                {
                    cm = AtoApply.CloneMaterial(ctx, tr.Material);
                    cloned[tr.Material] = cm;
                    matRemap[tr.Material] = cm;
                }
                AtoApply.RebindTexture(cm, tr.PropertyName, nt);
            }
            foreach (var r in ctx.AvatarRootObject.GetComponentsInChildren<Renderer>(true))
            {
                var mats = r.sharedMaterials;
                bool ch = false;
                for (int i = 0; i < mats.Length; i++)
                {
                    if (mats[i] != null && matRemap.TryGetValue(mats[i], out var nm))
                    { mats[i] = nm; ch = true; }
                }
                if (ch) r.sharedMaterials = mats;
            }
        }

        static AtoTextureClass StrictestClass(List<AtoTextureRef> users)
        {
            if (users.Any(u => u.Class == AtoTextureClass.Normal)) return AtoTextureClass.Normal;
            if (users.Any(u => u.Class == AtoTextureClass.Transparent)) return AtoTextureClass.Transparent;
            if (users.Any(u => u.Class == AtoTextureClass.Gray)) return AtoTextureClass.Gray;
            return AtoTextureClass.Opaque;
        }

        static AtoAlphaMode StrictestAlpha(List<AtoTextureRef> users, out float cutoff)
        {
            cutoff = 0f;
            var m = AtoAlphaMode.Opaque;
            foreach (var u in users)
            {
                if (u.AlphaMode > m) m = u.AlphaMode;
                cutoff = Mathf.Max(cutoff, u.Cutoff);
            }
            return m;
        }

        static Color[] Crop(Color[] src, int w, int h, int x, int y, int cw, int ch)
        {
            var d = new Color[cw * ch];
            for (int yy = 0; yy < ch; yy++)
            for (int xx = 0; xx < cw; xx++)
            {
                int sx = Mathf.Clamp(x + xx, 0, w - 1);
                int sy = Mathf.Clamp(y + yy, 0, h - 1);
                d[yy * cw + xx] = src[sy * w + sx];
            }
            return d;
        }
    }
}
