// English: Full bake pipeline. Only mesh UVs + texture references are mutated.
// 中文：完整烘焙流水线。只改网格 UV 与贴图引用。
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using nadena.dev.ndmf;
using net.fosa.ato;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace net.fosa.ato.editor
{
    public sealed class AtoPipeline
    {
        private readonly BuildContext _ctx;
        private readonly AvatarTextureOptimizer _comp;
        private readonly AtoPlatformSettings _s;
        private readonly AtoPlatform _platform;
        private readonly AtoProgress _progress;
        private readonly AtoCancel _cancel;
        private readonly AtoBakeReport _report = new AtoBakeReport();

        public AtoPipeline(BuildContext ctx, AvatarTextureOptimizer comp, AtoPlatformSettings s,
            AtoPlatform platform, AtoProgress progress, AtoCancel cancel)
        {
            _ctx = ctx; _comp = comp; _s = s; _platform = platform;
            _progress = progress; _cancel = cancel;
        }

        public void Run()
        {
            var sw = Stopwatch.StartNew();
            var root = _ctx.AvatarRootObject;
            using var cache = new AtoTextureCache();

            _progress.Report(AtoI18n.T("stage.scan"), 0.02f);
            var anim = AtoAnimationScan.Scan(root);
            var whitelist = BuildWhitelist(root, _comp);

            _progress.Report(AtoI18n.T("stage.collect"), 0.08f);
            var renderers = CollectRenderers(root, anim);
            AtoLog.Info($"Renderers considered: {renderers.Count}");

            var pctx = new AtoPipelineContext
            {
                AvatarRoot = root, Settings = _s, Report = _report,
                UvGroups = new List<AtoUvGroup>(), TypeGroups = new List<AtoTypeGroup>()
            };
            AtoHooks.RaiseBeforeAnalyze(pctx);

            var bindings = new List<AtoUvBinding>();
            foreach (var r in renderers)
            {
                _cancel.ThrowIfCanceled();
                CollectBindings(r, anim, whitelist, bindings);
            }

            // Animation material / texture swaps
            foreach (var swap in anim.MaterialSwaps)
            {
                if (swap.Renderer == null || swap.Material == null) continue;
                AddMaterialBindings(swap.Renderer, swap.Material, swap.Slot, anim, whitelist, bindings, true);
            }
            foreach (var ts in anim.TextureSwaps)
            {
                if (ts.Texture == null || ts.Renderer == null) continue;
                bindings.Add(new AtoUvBinding
                {
                    Renderer = ts.Renderer,
                    Mesh = GetMesh(ts.Renderer),
                    Submesh = ts.Slot,
                    UvChannel = 0,
                    PropertyName = ts.Property,
                    Texture = ts.Texture,
                    Class = AtoTextureClass.Unknown,
                    Animated = true,
                    Eligible = !whitelist.Contains(ts.Texture)
                });
            }

            _progress.Report(AtoI18n.T("stage.dedupe"), 0.18f);
            DedupSourceTextures(bindings, cache, whitelist);

            // UV groups
            var uvGroups = new Dictionary<string, AtoUvGroup>();
            foreach (var b in bindings)
            {
                if (b.Mesh == null || b.Texture == null) continue;
                var key = $"{b.Renderer.GetInstanceID()}|{b.Submesh}|{b.UvChannel}";
                if (!uvGroups.TryGetValue(key, out var g))
                {
                    g = new AtoUvGroup
                    {
                        Id = uvGroups.Count, Renderer = b.Renderer, Mesh = b.Mesh,
                        Submesh = b.Submesh, UvChannel = b.UvChannel,
                        Whitelisted = false
                    };
                    uvGroups[key] = g;
                }
                g.Bindings.Add(b);
                if (!b.Eligible)
                {
                    if (b.IneligibleReason == "whitelist" || b.IneligibleReason == "hook")
                        g.SkipAtlasOnly = true;
                    else
                        g.Whitelisted = true;
                }
            }

            // Type groups
            var typeGroups = BuildTypeGroups(uvGroups.Values);
            pctx.UvGroups = uvGroups.Values.ToList();
            pctx.TypeGroups = typeGroups;
            AtoHooks.RaiseAfterAnalyze(pctx);

            _progress.Report(AtoI18n.T("stage.islands"), 0.28f);
            int islandCount = 0;
            foreach (var g in uvGroups.Values)
            {
                _cancel.ThrowIfCanceled();
                if (g.Whitelisted) continue;
                float area = AtoIslands.MeshWorldArea(g.Renderer, g.Mesh, g.Submesh, anim);
                var ext = AtoIslands.Extract(g.Mesh, g.Submesh, g.UvChannel, area);
                if (ext.CrossesWrap)
                {
                    g.Whitelisted = true;
                    var msg = $"UV wrap-cross {g.Renderer.name} sm={g.Submesh} uv{g.UvChannel} → whitelist";
                    _report.Warnings.Add(msg);
                    AtoLog.Warn(msg);
                    ErrorReport.ReportError(AtoErrors.Localizer, ErrorSeverity.NonFatal, "warn.uv_wrap",
                        g.Renderer.name);
                    continue;
                }
                if (ext.Normalized)
                    AtoLog.Info($"Normalized UV translate {ext.Translate} on {g.Renderer.name}");
                foreach (var isl in ext.Islands)
                {
                    isl.Renderer = g.Renderer;
                    isl.MeshId = g.Mesh.GetInstanceID();
                    g.Islands.Add(isl);
                    islandCount++;
                }
            }
            _report.Islands = islandCount;
            AtoLog.Info($"Islands extracted: {islandCount}");

            _progress.Report(AtoI18n.T("stage.quality"), 0.40f);
            foreach (var g in uvGroups.Values)
            {
                if (g.Whitelisted) continue;
                var decs = g.Bindings.Select(b => cache.Get(b.Texture)).Where(d => d != null).ToArray();
                var binds = g.Bindings.ToArray();
                if (!_s.generateAtlas || g.SkipAtlasOnly)
                    continue;
                foreach (var isl in g.Islands)
                {
                    _cancel.ThrowIfCanceled();
                    AtoQuality.ScaleIsland(isl, decs, binds, _s.thresholds, _s.qualityPreset,
                        (int)_s.minDensity, (int)_s.maxDensity);
                    // UV group barrel: take max required size
                    int maxW = 0, maxH = 0;
                    foreach (var d in decs)
                    {
                        maxW = Mathf.Max(maxW, d.W);
                        maxH = Mathf.Max(maxH, d.H);
                    }
                    var sz = isl.Max - isl.Min;
                    int capW = Mathf.Max(1, Mathf.CeilToInt(sz.x * maxW));
                    int capH = Mathf.Max(1, Mathf.CeilToInt(sz.y * maxH));
                    if (isl.PixelRect.width > capW) isl.PixelRect.width = capW;
                    if (isl.PixelRect.height > capH) isl.PixelRect.height = capH;
                }
            }

            var texReplace = new Dictionary<Texture2D, Texture2D>();
            var atlasResults = new List<AtoAtlasResult>();

            AtoHooks.RaiseBeforePack(pctx);
            if (_s.generateAtlas)
            {
                _progress.Report(AtoI18n.T("stage.pack"), 0.62f);
                atlasResults = AtoAtlasBuilder.Build(_ctx, _s, _platform, typeGroups, cache, texReplace, _report, _cancel);
                ScaleNoAtlas(uvGroups.Values.Where(g => g.SkipAtlasOnly), cache, texReplace);
            }
            else
            {
                _progress.Report(AtoI18n.T("stage.scale"), 0.62f);
                ScaleNoAtlas(uvGroups.Values, cache, texReplace);
            }

            _progress.Report(AtoI18n.T("stage.remap"), 0.80f);
            RemapAndAssign(uvGroups.Values, atlasResults, texReplace, cache, anim);

            if (_s.dedupeTextures || _s.dedupeMaterials)
            {
                _progress.Report(AtoI18n.T("stage.dedupe2"), 0.90f);
                PostDedupe(root, anim);
                if (_s.dedupeMaterials) AtoSlotMerge.Run(root, anim);
            }

            AtoHooks.RaiseAfterApply(pctx);

            sw.Stop();
            _report.TotalMs = sw.ElapsedMilliseconds;
            _report.Atlases = atlasResults.Count;
            _report.TexturesOut = texReplace.Count;
            EmitReport(atlasResults);
            _progress.Report(AtoI18n.T("stage.done"), 1f);
        }

        private HashSet<Object> BuildWhitelist(GameObject root, AvatarTextureOptimizer comp)
        {
            var set = new HashSet<Object>();
            if (comp.whitelist == null) return set;
            foreach (var e in comp.whitelist)
            {
                if (e == null || e.target == null) continue;
                CollectRefs(e.target, set);
            }
            return set;
        }

        private static void CollectRefs(Object o, HashSet<Object> set)
        {
            if (o == null || !set.Add(o)) return;
            if (o is Texture2D) return;
            if (o is Material m)
            {
                foreach (var t in m.GetTexturePropertyNames())
                    if (m.GetTexture(t) is Texture2D td) set.Add(td);
                return;
            }
            if (o is Renderer r)
            {
                foreach (var mat in r.sharedMaterials) CollectRefs(mat, set);
                return;
            }
            if (o is GameObject go)
            {
                foreach (var c in go.GetComponentsInChildren<Renderer>(true)) CollectRefs(c, set);
                return;
            }
            if (o is AnimationClip clip)
            {
                foreach (var b in AnimationUtility.GetObjectReferenceCurveBindings(clip))
                {
                    var ks = AnimationUtility.GetObjectReferenceCurve(clip, b);
                    if (ks == null) continue;
                    foreach (var k in ks)
                    {
                        if (k.value is Texture2D t) set.Add(t);
                        if (k.value is Material mat) CollectRefs(mat, set);
                    }
                }
            }
        }

        private static List<Renderer> CollectRenderers(GameObject root, AtoAnimInfo anim)
        {
            var list = new List<Renderer>();
            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
            {
                if (!(r is MeshRenderer || r is SkinnedMeshRenderer)) continue;
                if (r.CompareTag("EditorOnly")) continue;
                bool enabled = r.enabled && r.gameObject.activeInHierarchy;
                if (!enabled && !anim.AnimatedEnable.Contains(r) && !IsGoAnimated(r.gameObject, anim))
                    continue;
                list.Add(r);
            }
            return list;
        }

        private static bool IsGoAnimated(GameObject go, AtoAnimInfo anim)
        {
            var t = go.transform;
            while (t != null)
            {
                if (anim.AnimatedGoEnable.Contains(t.gameObject)) return true;
                t = t.parent;
            }
            return false;
        }

        private void CollectBindings(Renderer r, AtoAnimInfo anim, HashSet<Object> whitelist, List<AtoUvBinding> dst)
        {
            var mats = r.sharedMaterials;
            if (mats == null) return;
            for (int i = 0; i < mats.Length; i++)
                AddMaterialBindings(r, mats[i], i, anim, whitelist, dst, false);
        }

        private void AddMaterialBindings(Renderer r, Material mat, int slot, AtoAnimInfo anim,
            HashSet<Object> whitelist, List<AtoUvBinding> dst, bool animated)
        {
            if (mat == null) return;
            var slots = AtoShaderAnalysis.CollectSlots(mat, out var ok, out var warn);
            if (!ok)
            {
                AtoLog.Warn($"Incompatible shader on {mat.name}: {warn}");
                ErrorReport.ReportError(AtoErrors.Localizer, ErrorSeverity.Information, "warn.shader", mat.name);
            }
            var alpha = AtoShaderAnalysis.ReadAlphaMode(mat, out var cutoff);
            foreach (var fa in anim.FloatAnims)
            {
                if (fa.Renderer != r) continue;
                if (fa.Property.Contains("Cutoff")) cutoff = Mathf.Min(cutoff, fa.Min);
                if (fa.Property.Contains("TransparentMode") && fa.Max >= 1f)
                    alpha = fa.Max >= 2f ? AtoAlphaMode.Blend : AtoAlphaMode.Cutout;
            }

            foreach (var sl in slots)
            {
                var tex = mat.GetTexture(sl.Name) as Texture2D;
                if (tex == null) continue;
                var hook = AtoHooks.TryClassify(mat, sl.Name);
                var cls = hook ?? sl.Class;
                bool eligible = true;
                string reason = null;
                if (whitelist.Contains(tex) || whitelist.Contains(mat) || whitelist.Contains(r))
                { eligible = false; reason = "whitelist"; }
                var extra = AtoHooks.TryExtraWhitelist(tex);
                if (extra == true) { eligible = false; reason = "hook"; }
                if (AtoShaderAnalysis.HasNonIdentityST(mat, sl.Name))
                { eligible = false; reason = "material ST/scroll"; }
                foreach (var p in anim.StAnimatedProperties)
                    if (p.Contains(sl.Name))
                    { eligible = false; reason = "animated ST"; }
                if (r is SkinnedMeshRenderer smr && AtoAaoCompat.IsTexCoordUsed(smr, sl.UvChannel))
                {
                    AtoLog.VerboseInfo($"AAO uses UV{sl.UvChannel} on {r.name}");
                }
                if (!eligible)
                {
                    AtoLog.Warn($"Treat as whitelist: {tex.name} ({reason})");
                    _report.Warnings.Add($"{tex.name}: {reason}");
                }
                dst.Add(new AtoUvBinding
                {
                    Renderer = r, Mesh = GetMesh(r), Submesh = slot, UvChannel = sl.UvChannel,
                    Material = mat, PropertyName = sl.Name, Texture = tex, Class = cls,
                    AlphaMode = alpha, Cutoff = cutoff, Animated = animated,
                    Eligible = eligible, IneligibleReason = reason
                });
            }
        }

        private static Mesh GetMesh(Renderer r)
        {
            if (r is SkinnedMeshRenderer s) return s.sharedMesh;
            if (r is MeshRenderer)
            {
                var mf = r.GetComponent<MeshFilter>();
                return mf != null ? mf.sharedMesh : null;
            }
            return null;
        }

        private void DedupSourceTextures(List<AtoUvBinding> bindings, AtoTextureCache cache, HashSet<Object> whitelist)
        {
            var groups = new Dictionary<string, Texture2D>();
            var map = new Dictionary<Texture2D, Texture2D>();
            foreach (var b in bindings)
            {
                if (b.Texture == null) continue;
                var dec = cache.Get(b.Texture);
                var key = dec.Fingerprint + "|" + AtoTextureCache.ContentHash(dec);
                if (!groups.TryGetValue(key, out var canon))
                {
                    groups[key] = b.Texture;
                    continue;
                }
                if (canon != b.Texture)
                {
                    map[b.Texture] = canon;
                    if (whitelist.Contains(b.Texture) || whitelist.Contains(canon))
                        whitelist.Add(canon);
                }
            }
            if (map.Count == 0) return;
            AtoLog.Info($"Texture content dedupe: {map.Count} redirected");
            foreach (var b in bindings)
                if (b.Texture != null && map.TryGetValue(b.Texture, out var n))
                    b.Texture = n;
            foreach (var r in _ctx.AvatarRootObject.GetComponentsInChildren<Renderer>(true))
            {
                var mats = r.sharedMaterials;
                if (mats == null) continue;
                foreach (var m in mats)
                {
                    if (m == null) continue;
                    foreach (var p in m.GetTexturePropertyNames())
                    {
                        if (m.GetTexture(p) is Texture2D t && map.TryGetValue(t, out var n))
                            m.SetTexture(p, n);
                    }
                }
            }
        }

        private List<AtoTypeGroup> BuildTypeGroups(IEnumerable<AtoUvGroup> groups)
        {
            var dict = new Dictionary<string, AtoTypeGroup>();
            foreach (var g in groups)
            {
                bool hasN = g.Bindings.Any(b => b.Class == AtoTextureClass.Normal);
                bool hasM = g.Bindings.Any(b => b.Class == AtoTextureClass.Mask || b.Class == AtoTextureClass.Gray);
                bool linear = false;
                FilterMode filter = FilterMode.Bilinear;
                foreach (var b in g.Bindings)
                {
                    if (b.Texture == null) continue;
                    var path = AssetDatabase.GetAssetPath(b.Texture);
                    var imp = AssetImporter.GetAtPath(path) as TextureImporter;
                    if (imp != null && !imp.sRGBTexture) linear = true;
                    filter = b.Texture.filterMode;
                }
                var key = $"{linear}|{filter}|N{hasN}|M{hasM}";
                if (!dict.TryGetValue(key, out var tg))
                {
                    tg = new AtoTypeGroup { Key = key, Linear = linear, Filter = filter, HasNormal = hasN, HasMask = hasM };
                    dict[key] = tg;
                }
                tg.UvGroups.Add(g);
                foreach (var b in g.Bindings)
                    if (b.Texture && !tg.Textures.Contains(b.Texture)) tg.Textures.Add(b.Texture);
            }
            // Promote: if a texture sits in a weaker group and a stronger (has normal/mask) group, merge into stronger.
            var list = dict.Values.ToList();
            bool changed = true;
            while (changed)
            {
                changed = false;
                for (int i = 0; i < list.Count; i++)
                for (int j = i + 1; j < list.Count; j++)
                {
                    bool share = list[i].Textures.Any(t => list[j].Textures.Contains(t));
                    if (!share) continue;
                    var keep = (list[i].HasNormal || list[i].HasMask) ? list[i] : list[j];
                    var drop = keep == list[i] ? list[j] : list[i];
                    keep.HasNormal |= drop.HasNormal;
                    keep.HasMask |= drop.HasMask;
                    foreach (var g in drop.UvGroups) if (!keep.UvGroups.Contains(g)) keep.UvGroups.Add(g);
                    foreach (var t in drop.Textures) if (!keep.Textures.Contains(t)) keep.Textures.Add(t);
                    list.Remove(drop);
                    changed = true;
                    break;
                }
            }
            AtoLog.Info($"Type groups: {list.Count}");
            return list;
        }

        private void ScaleNoAtlas(IEnumerable<AtoUvGroup> groups, AtoTextureCache cache,
            Dictionary<Texture2D, Texture2D> texReplace)
        {
            var done = new HashSet<Texture2D>();
            foreach (var g in groups)
            {
                if (g.Whitelisted) continue;
                foreach (var b in g.Bindings)
                {
                    if (b.Texture == null || !b.Eligible) continue;
                    if (texReplace.ContainsKey(b.Texture)) continue;
                    if (!done.Add(b.Texture)) continue;
                    var dec = cache.Get(b.Texture);
                    float scale = 1f;
                    if (_s.qualityPreset != AtoQualityPreset.Lossless && !NearlyOne(_s.thresholds))
                    {
                        if (dec.SolidColor) scale = Mathf.Min(4f, Mathf.Min(dec.W, dec.H)) / Mathf.Min(dec.W, dec.H);
                        else
                        {
                            // reuse island scaler on full-quad
                            var isl = new AtoIsland
                            {
                                Min = Vector2.zero, Max = Vector2.one,
                                WorldArea = 1f, PixelRect = new RectInt(0, 0, dec.W, dec.H)
                            };
                            AtoQuality.ScaleIsland(isl, new[] { dec }, new[] { b }, _s.thresholds, _s.qualityPreset,
                                (int)_s.minDensity, (int)_s.maxDensity);
                            scale = Mathf.Min(isl.ScaleU, isl.ScaleV);
                        }
                    }
                    var n = AtoApply.ScaleWhole(_ctx, dec, scale, "ATO_" + b.Texture.name, dec.Linear);
                    var choice = b.Class == AtoTextureClass.Normal ? _s.compression.normal
                        : (dec.HasAlpha ? _s.compression.transparent : _s.compression.opaque);
                    bool mip = b.Class == AtoTextureClass.Normal ? _s.compression.mipStreamingNormal
                        : (dec.HasAlpha ? _s.compression.mipStreamingTransparent : _s.compression.mipStreamingOpaque);
                    AtoFormat.Apply(n, _platform, choice, b.Class, dec.HasAlpha, dec.Linear, _s.experimentalNpot, mip);
                    texReplace[b.Texture] = n;
                    _report.Add($"Scale {b.Texture.name} x{scale:0.00} -> {n.width}x{n.height}");
                }
            }
        }

        private static bool NearlyOne(AtoQualityThresholds th) =>
            th.msSsim >= 0.9999f && th.ciede2000 <= 1e-4f;

        private void RemapAndAssign(IEnumerable<AtoUvGroup> groups, List<AtoAtlasResult> atlases,
            Dictionary<Texture2D, Texture2D> texReplace, AtoTextureCache cache, AtoAnimInfo anim)
        {
            var matMap = new Dictionary<Material, Material>();
            Material CloneMat(Material m)
            {
                if (m == null) return null;
                if (matMap.TryGetValue(m, out var c)) return c;
                c = new Material(m) { name = m.name + "_ATO" };
                _ctx.AssetSaver.SaveAsset(c);
                matMap[m] = c;
                return c;
            }

            foreach (var r in _ctx.AvatarRootObject.GetComponentsInChildren<Renderer>(true))
            {
                var mats = r.sharedMaterials;
                if (mats == null) continue;
                bool changed = false;
                var nm = (Material[])mats.Clone();
                for (int i = 0; i < nm.Length; i++)
                {
                    var m = nm[i];
                    if (m == null) continue;
                    Material cm = null;
                    foreach (var p in m.GetTexturePropertyNames())
                    {
                        if (m.GetTexture(p) is Texture2D t && texReplace.TryGetValue(t, out var nt))
                        {
                            cm ??= CloneMat(m);
                            cm.SetTexture(p, nt);
                            changed = true;
                        }
                    }
                    if (cm != null) nm[i] = cm;
                }
                if (changed) r.sharedMaterials = nm;
            }

            // Animation object-reference curves
            var clips = new HashSet<AnimationClip>();
            foreach (var a in _ctx.AvatarRootObject.GetComponentsInChildren<Animator>(true))
                if (a.runtimeAnimatorController)
                    foreach (var c in a.runtimeAnimatorController.animationClips) if (c) clips.Add(c);
            foreach (var clip in clips)
            {
                foreach (var b in AnimationUtility.GetObjectReferenceCurveBindings(clip))
                {
                    var keys = AnimationUtility.GetObjectReferenceCurve(clip, b);
                    if (keys == null) continue;
                    bool ch = false;
                    for (int i = 0; i < keys.Length; i++)
                    {
                        if (keys[i].value is Texture2D t && texReplace.TryGetValue(t, out var nt))
                        { keys[i].value = nt; ch = true; }
                        if (keys[i].value is Material m && matMap.TryGetValue(m, out var nm))
                        { keys[i].value = nm; ch = true; }
                    }
                    if (ch) AnimationUtility.SetObjectReferenceCurve(clip, b, keys);
                }
            }
        }

        private static void AssignMesh(Renderer r, Mesh mesh)
        {
            if (r is SkinnedMeshRenderer s) s.sharedMesh = mesh;
            else
            {
                var mf = r.GetComponent<MeshFilter>();
                if (mf) mf.sharedMesh = mesh;
            }
        }

        private void ApplyImporter(Texture2D tex, AtoTypeGroup tg, bool hasAlpha, bool linear,
            AtoTextureClass cls = AtoTextureClass.Unknown)
        {
            tex.wrapMode = TextureWrapMode.Clamp;
            var cset = _s.compression;
            bool mip = true;
            if (cls == AtoTextureClass.Normal || (tg != null && tg.HasNormal))
                mip = cset.mipStreamingNormal;
            else if (hasAlpha) mip = cset.mipStreamingTransparent;
            else mip = cset.mipStreamingOpaque;
            // VRC: mipmap <=> streaming bound
            tex.Apply(mip, false);
            AtoLog.VerboseInfo($"Import apply {tex.name} clamp linear={linear} alpha={hasAlpha} mip={mip}");
        }

        private void PostDedupe(GameObject root, AtoAnimInfo anim)
        {
            if (!_s.dedupeMaterials) return;
            var mats = new List<Material>();
            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
                if (r.sharedMaterials != null) mats.AddRange(r.sharedMaterials.Where(m => m != null));
            var map = new Dictionary<Material, Material>();
            for (int i = 0; i < mats.Count; i++)
            for (int j = i + 1; j < mats.Count; j++)
            {
                if (map.ContainsKey(mats[j])) continue;
                if (SameMat(mats[i], mats[j])) map[mats[j]] = mats[i];
            }
            if (map.Count == 0) return;
            AtoLog.Info($"Material dedupe {map.Count}");
            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
            {
                var arr = r.sharedMaterials;
                if (arr == null) continue;
                bool ch = false;
                for (int i = 0; i < arr.Length; i++)
                    if (arr[i] != null && map.TryGetValue(arr[i], out var n)) { arr[i] = n; ch = true; }
                if (ch)
                {
                    // Merge identical opaque slots if animation never swaps a single slot independently
                    r.sharedMaterials = arr;
                }
            }
        }

        private static bool SameMat(Material a, Material b)
        {
            if (a == b) return true;
            if (a == null || b == null || a.shader != b.shader) return false;
            if (a.renderQueue != b.renderQueue) return false;
            var pa = a.GetTexturePropertyNames();
            var pb = b.GetTexturePropertyNames();
            if (pa.Length != pb.Length) return false;
            for (int i = 0; i < pa.Length; i++)
                if (a.GetTexture(pa[i]) != b.GetTexture(pb[i])) return false;
            return true;
        }

        private void EmitReport(List<AtoAtlasResult> atlases)
        {
            var summary = AtoI18n.T("report.summary",
                _report.TotalMs, _report.Islands, _report.Atlases, _report.Warnings.Count);
            AtoLog.Info(summary);
            ErrorReport.ReportError(AtoErrors.Localizer, ErrorSeverity.Information, "report.summary",
                _report.TotalMs, _report.Islands, _report.Atlases, _report.Warnings.Count);
            foreach (var d in _report.Details)
                AtoLog.VerboseInfo(d);
            foreach (var w in _report.Warnings)
                AtoLog.Warn(w);
        }
    }
}
