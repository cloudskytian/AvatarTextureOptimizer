// Stage 1-3: scene scan, animation analysis, whitelist expansion, texture dedup,
// and the UV<->texture usage graph.
// 阶段1-3：场景扫描、动画分析、白名单展开、贴图去重、UV-贴图映射图构建。
using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf.animator;
using UnityEditor;
using UnityEngine;

namespace net.fosa.ato.editor
{
    public static class ScanStage
    {
        public static void Run(AtoContext ctx)
        {
            using (AtoLog.Time("ScanStage", (l, ms) => ctx.Stats.StageTimes.Add((l, ms))))
            {
                AtoProgress.BeginStage(AtoL10n.Tr("stage.scan"));
                ExpandWhitelist(ctx);
                ScanRenderers(ctx);
                ScanAnimations(ctx);
                DedupSourceTextures(ctx);
                BuildUsageGraph(ctx);
            }
        }

        // ---- Whitelist: any object type -> all textures in its dependency closure skip ALL opts.
        // 白名单：任意对象 → 依赖闭包内全部贴图跳过所有优化。 ----
        private static void ExpandWhitelist(AtoContext ctx)
        {
            var roots = ctx.Settings.whitelist.Where(o => o != null).ToArray();
            foreach (var o in roots) ctx.WhitelistObjects.Add(o);
            if (roots.Length == 0) return;
            foreach (var dep in EditorUtility.CollectDependencies(roots))
                if (dep is Texture2D t) ctx.WhitelistTextures.Add(t);
            AtoLog.Info($"whitelist: {roots.Length} objects -> {ctx.WhitelistTextures.Count} textures excluded");
        }

        // ---- Renderers: enabled or animation-enabled SMR/MR, skip EditorOnly. ----
        private static void ScanRenderers(AtoContext ctx)
        {
            var animActive = CollectAnimationActivatedPaths(ctx);
            foreach (var r in ctx.Ndmf.AvatarRootObject.GetComponentsInChildren<Renderer>(true))
            {
                if (!(r is SkinnedMeshRenderer || r is MeshRenderer)) continue;
                if (IsEditorOnly(r.transform, ctx.Ndmf.AvatarRootTransform)) continue;

                var mesh = r is SkinnedMeshRenderer smr ? smr.sharedMesh : r.GetComponent<MeshFilter>()?.sharedMesh;
                if (mesh == null) continue;

                var path = RelPath(ctx.Ndmf.AvatarRootTransform, r.transform);
                bool active = (r.gameObject.activeInHierarchy && r.enabled) || animActive.Contains(path);
                if (!active)
                {
                    AtoLog.Debugf($"skip inactive renderer: {path}");
                    continue;
                }

                var info = new RendererInfo { Renderer = r, Mesh = mesh, ActiveOrAnimated = true };
                info.MaterialVariants.Add(r.sharedMaterials);
                info.BlendshapeAreaFactor = BlendshapeAreaFactor(mesh);
                ctx.Renderers.Add(info);
            }
            AtoLog.Info($"renderers scanned: {ctx.Renderers.Count}");
        }

        private static bool IsEditorOnly(Transform t, Transform root)
        {
            for (; t != null && t != root; t = t.parent)
                if (t.CompareTag("EditorOnly")) return true;
            return false;
        }

        internal static string RelPath(Transform root, Transform t)
        {
            var parts = new List<string>();
            for (; t != null && t != root; t = t.parent) parts.Add(t.name);
            parts.Reverse();
            return string.Join("/", parts);
        }

        /// <summary>Max blendshape area inflation: per-shape 0 vs 100 only. / 形态键面积放大系数（仅0/100）。</summary>
        private static float BlendshapeAreaFactor(Mesh mesh)
        {
            int shapes = mesh.blendShapeCount;
            if (shapes == 0) return 1f;
            var baseVerts = mesh.vertices;
            var tris = mesh.triangles;
            float baseArea = TriArea(baseVerts, tris);
            if (baseArea <= 1e-12f) return 1f;
            float factor = 1f;
            var delta = new Vector3[baseVerts.Length];
            var moved = new Vector3[baseVerts.Length];
            for (int s = 0; s < shapes; s++)
            {
                int frame = mesh.GetBlendShapeFrameCount(s) - 1; // full weight frame / 满权重帧
                mesh.GetBlendShapeFrameVertices(s, frame, delta, null, null);
                for (int i = 0; i < baseVerts.Length; i++) moved[i] = baseVerts[i] + delta[i];
                factor = Mathf.Max(factor, TriArea(moved, tris) / baseArea);
            }
            return factor;
        }

        private static float TriArea(Vector3[] v, int[] tris)
        {
            double area = 0;
            for (int i = 0; i + 2 < tris.Length; i += 3)
                area += Vector3.Cross(v[tris[i + 1]] - v[tris[i]], v[tris[i + 2]] - v[tris[i]]).magnitude * 0.5f;
            return (float)area;
        }

        // ---- Animation: activation paths, material swaps, scale anim, material prop anim. ----
        private static HashSet<string> CollectAnimationActivatedPaths(AtoContext ctx)
        {
            var set = new HashSet<string>();
            ForEachClip(ctx, clip =>
            {
                foreach (var b in clip.GetFloatCurveBindings())
                {
                    if (b.type == typeof(GameObject) && b.propertyName == "m_IsActive")
                    {
                        var c = clip.GetFloatCurve(b);
                        if (c != null && c.keys.Any(k => k.value > 0.5f)) set.Add(b.path);
                    }
                    else if (typeof(Renderer).IsAssignableFrom(b.type) && b.propertyName == "m_Enabled")
                    {
                        var c = clip.GetFloatCurve(b);
                        if (c != null && c.keys.Any(k => k.value > 0.5f)) set.Add(b.path);
                    }
                }
            });
            return set;
        }

        private static void ScanAnimations(AtoContext ctx)
        {
            var root = ctx.Ndmf.AvatarRootTransform;
            int swaps = 0;

            // per-transform animated max scale / 动画最大缩放
            var scaleMax = new Dictionary<string, float>();

            ForEachClip(ctx, clip =>
            {
                // material swap curves / 材质切换
                foreach (var b in clip.GetObjectCurveBindings())
                {
                    if (!typeof(Renderer).IsAssignableFrom(b.type)) continue;
                    if (!b.propertyName.StartsWith("m_Materials.Array.data[")) continue;
                    var t = root.Find(b.path);
                    var r = t ? t.GetComponent<Renderer>() : null;
                    var info = ctx.Renderers.FirstOrDefault(x => x.Renderer == r);
                    if (info == null) continue;
                    int slot = ParseSlotIndex(b.propertyName);
                    var frames = clip.GetObjectCurve(b);
                    if (frames == null) continue;
                    foreach (var f in frames)
                    {
                        if (!(f.value is Material m) || slot < 0 || slot >= mats.Length) continue;
                        var variant = (Material[])info.Renderer.sharedMaterials.Clone();
                        variant[slot] = m;
                        if (!info.MaterialVariants.Any(v => v.SequenceEqual(variant)))
                        {
                            info.MaterialVariants.Add(variant);
                            swaps++;
                        }
                    }
                }

                foreach (var b in clip.GetFloatCurveBindings())
                {
                    // animated scale / 缩放动画
                    if (b.type == typeof(Transform) && b.propertyName.StartsWith("m_LocalScale."))
                    {
                        var c = clip.GetFloatCurve(b);
                        if (c == null || c.keys.Length == 0) continue;
                        float mx = c.keys.Max(k => Mathf.Abs(k.value));
                        scaleMax.TryGetValue(b.path, out var cur);
                        scaleMax[b.path] = Mathf.Max(cur, mx);
                    }
                    // material property animation: ST/scroll => unsafe; cutoff/mode => strictest
                    // 材质属性动画：ST/滚动 → 不安全；Cutoff/模式 → 取最严苛
                    else if (typeof(Renderer).IsAssignableFrom(b.type) && b.propertyName.StartsWith("material."))
                    {
                        var prop = b.propertyName.Substring("material.".Length);
                        var t = root.Find(b.path);
                        var r = t ? t.GetComponent<Renderer>() : null;
                        var info = ctx.Renderers.FirstOrDefault(x => x.Renderer == r);
                        if (info == null) continue;
                        if (prop.Contains("_ST.") || prop.Contains("ScrollRotate") || prop.Contains("Angle"))
                        {
                            info.AnimatedStUnsafe = true;
                            info.AnimatedStProperty = prop;
                        }
                        else if (prop.StartsWith("_Cutoff"))
                        {
                            var c = clip.GetFloatCurve(b);
                            if (c != null)
                                foreach (var k in c.keys) info.AnimatedCutoffs.Add(k.value);
                        }
                        else if (prop.Contains("TransparentMode") || prop.Contains("_Mode"))
                        {
                            info.AnimatedAlphaModeChanges = true;
                        }
                    }
                }
            });

            // fold scale anim into renderers (max over ancestor chain) / 折算到渲染器
            foreach (var info in ctx.Renderers)
            {
                float f = 1f;
                var t = info.Renderer.transform;
                while (t != null && t != root.parent)
                {
                    var p = RelPath(root, t);
                    if (scaleMax.TryGetValue(p, out var mx))
                    {
                        var cur = Mathf.Max(Mathf.Abs(t.localScale.x),
                            Mathf.Max(Mathf.Abs(t.localScale.y), Mathf.Abs(t.localScale.z)));
                        if (cur > 1e-6f) f *= Mathf.Max(1f, mx / cur);
                    }
                    if (t == root) break;
                    t = t.parent;
                }
                info.MaxAnimScale = f;
            }
            AtoLog.Info($"animation scan done: {swaps} material swap variants found");
        }

        private static int ParseSlotIndex(string propertyName)
        {
            int a = propertyName.IndexOf('[') + 1, b = propertyName.IndexOf(']');
            return (a > 0 && b > a && int.TryParse(propertyName.Substring(a, b - a), out var i)) ? i : -1;
        }

        internal static void ForEachClip(AtoContext ctx, Action<VirtualClip> action)
        {
            var asc = ctx.Ndmf.Extension<AnimatorServicesContext>();
            var seen = new HashSet<VirtualClip>();
            foreach (var controller in asc.ControllerContext.GetAllControllers())
                foreach (var node in controller.AllReachableNodes())
                    if (node is VirtualClip clip && seen.Add(clip) && !clip.IsMarkerClip)
                        action(clip);
        }

        // ---- Texture dedup by pixels + importer settings. / 按像素+导入设置去重。 ----
        private static void DedupSourceTextures(AtoContext ctx)
        {
            AtoProgress.Step(0.3f, AtoL10n.Tr("stage.dedup"));
            var all = new HashSet<Texture2D>();
            foreach (var info in ctx.Renderers)
                foreach (var mats in info.MaterialVariants)
                    foreach (var m in mats)
                    {
                        if (m == null) continue;
                        foreach (var prop in m.GetTexturePropertyNames())
                            if (m.GetTexture(prop) is Texture2D t) all.Add(t);
                    }

            var byHash = new Dictionary<string, Texture2D>();
            int deduped = 0;
            int i = 0;
            foreach (var t in all)
            {
                AtoProgress.Step(0.3f + 0.4f * (++i / (float)Math.Max(1, all.Count)), t.name);
                string hash;
                try { hash = TexturePixels.PixelHash(t) + "|" + ImporterHash(t); }
                catch (Exception e) { AtoLog.Warn($"hash failed for {t.name}: {e.Message}"); continue; }
                if (byHash.TryGetValue(hash, out var canonical) && canonical != t)
                {
                    ctx.DedupMap[t] = canonical;
                    // dedup involving whitelist -> result also whitelisted / 去重涉及白名单则结果亦白名单
                    if (ctx.WhitelistTextures.Contains(t)) ctx.WhitelistTextures.Add(canonical);
                    if (ctx.WhitelistTextures.Contains(canonical)) ctx.WhitelistTextures.Add(t);
                    deduped++;
                }
                else byHash[hash] = t;
            }
            ctx.Stats.TexturesSeen = all.Count;
            ctx.Stats.TexturesDeduped = deduped;
            if (deduped > 0) ApplyDedupReferences(ctx);
            AtoLog.Info($"texture dedup: {deduped}/{all.Count} duplicates unified");
        }

        private static string ImporterHash(Texture2D t)
        {
            var path = AssetDatabase.GetAssetPath(t);
            if (string.IsNullOrEmpty(path)) return $"rt_{t.format}_{t.wrapMode}_{t.filterMode}_{t.mipmapCount}_{t.anisoLevel}";
            var imp = AssetImporter.GetAtPath(path) as TextureImporter;
            if (imp == null) return "noimp";
            return EditorJsonUtility.ToJson(imp).GetHashCode().ToString("x8");
        }

        /// <summary>Clone materials to retarget deduped texture refs; update slots and clips.
        /// 克隆材质替换去重贴图引用，并同步材质槽与动画。</summary>
        private static void ApplyDedupReferences(AtoContext ctx)
        {
            var matClones = new Dictionary<Material, Material>();

            Material Retarget(Material m)
            {
                if (m == null) return null;
                if (matClones.TryGetValue(m, out var c)) return c;
                bool needs = m.GetTexturePropertyNames()
                    .Any(p => m.GetTexture(p) is Texture2D t && ctx.DedupMap.ContainsKey(t));
                if (!needs) { matClones[m] = m; return m; }
                var clone = UnityEngine.Object.Instantiate(m);
                clone.name = m.name;
                nadena.dev.ndmf.ObjectRegistry.RegisterReplacedObject(m, clone);
                foreach (var p in clone.GetTexturePropertyNames())
                    if (clone.GetTexture(p) is Texture2D t && ctx.DedupMap.TryGetValue(t, out var canon))
                        clone.SetTexture(p, canon); // ONLY texture refs are touched / 只改贴图引用
                matClones[m] = clone;
                return clone;
            }

            foreach (var info in ctx.Renderers)
            {
                var mats = info.Renderer.sharedMaterials;
                for (int s = 0; s < mats.Length; s++) mats[s] = Retarget(mats[s]);
                info.Renderer.sharedMaterials = mats;
                for (int v = 0; v < info.MaterialVariants.Count; v++)
                    for (int s = 0; s < info.MaterialVariants[v].Length; s++)
                        info.MaterialVariants[v][s] = Retarget(info.MaterialVariants[v][s]);
            }

            var asc = ctx.Ndmf.Extension<AnimatorServicesContext>();
            asc.AnimationIndex.RewriteObjectCurves(o =>
                o is Material m && matClones.TryGetValue(m, out var c) ? c : o);
        }

        // ---- Usage graph: TexInfo + mappings. / 映射图构建。 ----
        private static void BuildUsageGraph(AtoContext ctx)
        {
            AtoProgress.Step(0.8f, AtoL10n.Tr("stage.graph"));
            foreach (var info in ctx.Renderers)
            {
                foreach (var mats in info.MaterialVariants)
                {
                    for (int slot = 0; slot < mats.Length; slot++)
                    {
                        var mat = mats[slot];
                        if (mat == null) continue;
                        var sem = ShaderSemantics.Analyze(mat);
                        if (!sem.Supported)
                        {
                            WhitelistMaterialTextures(ctx, mat, sem.UnsupportedReason);
                            continue;
                        }
                        foreach (var ps in sem.Props)
                        {
                            var tex = mat.GetTexture(ps.Property) as Texture2D;
                            if (tex == null) continue;
                            if (ctx.DedupMap.TryGetValue(tex, out var canon)) tex = canon;
                            var ti = ctx.GetOrAddTex(tex);

                            var alpha = sem.Alpha;
                            var cutoff = sem.Cutoff;
                            var use = new TexUse
                            {
                                Material = mat, Property = ps.Property, Role = ps.Role,
                                UvChannel = ps.UvChannel, Alpha = alpha, Cutoff = cutoff,
                                UsedChannels = ps.UsedChannels, Renderer = info.Renderer, SlotIndex = slot,
                                FromAnimation = !ReferenceEquals(mats, info.MaterialVariants[0])
                            };
                            ti.Uses.Add(use);

                            // strictest requirements accumulation / 最严苛要求累积
                            if (ps.Role == TexRole.Gray) ti.UsedChannels |= ps.UsedChannels;
                            if (RolePriority(ps.Role) > RolePriority(ti.Role)) ti.Role = ps.Role;
                            switch (alpha)
                            {
                                case AlphaMode.Cutout: ti.AnyCutout = true; ti.Cutoffs.Add(cutoff); break;
                                case AlphaMode.Blend: ti.AnyBlend = true; break;
                                default: ti.AnyOpaqueUse = true; break;
                            }
                            foreach (var co in info.AnimatedCutoffs) { ti.AnyCutout = true; ti.Cutoffs.Add(co); }
                            if (info.AnimatedAlphaModeChanges) { ti.AnyCutout = true; ti.AnyBlend = true; ti.Cutoffs.Add(cutoff); }

                            // whitelist decisions / 白名单判定
                            if (ctx.WhitelistTextures.Contains(tex))
                                MarkWhitelist(ti, "user whitelist");
                            else if (!ps.Safe)
                                MarkWhitelist(ti, ps.UnsafeReason);
                            else if (info.AnimatedStUnsafe)
                                MarkWhitelist(ti, $"animated UV transform ({info.AnimatedStProperty})");
                            else if (ps.UvChannel >= 0)
                            {
                                var key = new MappingKey(info.Mesh, ps.UvChannel);
                                ti.Mappings.Add(key);
                                int sub = Mathf.Min(slot, info.Mesh.subMeshCount - 1);
                                ti.SubmeshMask.TryGetValue(key, out var mask);
                                ti.SubmeshMask[key] = mask | (1UL << Mathf.Min(sub, 63));
                            }

                            // companion flags for type groups / 类型组伴随标记
                            foreach (var other in sem.Props)
                            {
                                if (other == ps) continue;
                                if (other.Role == TexRole.Normal) ti.CompanionNormal = true;
                                if (other.Role == TexRole.Gray) ti.CompanionMask = true;
                            }
                        }
                    }
                }
            }

            foreach (var kv in ctx.Textures)
            {
                foreach (var m in kv.Value.Mappings)
                {
                    if (!ctx.MappingTextures.TryGetValue(m, out var list))
                        ctx.MappingTextures[m] = list = new List<TexInfo>();
                    if (!list.Contains(kv.Value)) list.Add(kv.Value);
                }
            }
            ctx.Stats.TexturesWhitelisted = ctx.Textures.Values.Count(t => t.Whitelisted);
            AtoLog.Info($"usage graph: {ctx.Textures.Count} textures, {ctx.MappingTextures.Count} mesh-uv mappings, " +
                        $"{ctx.Stats.TexturesWhitelisted} whitelisted");
        }

        private static int RolePriority(TexRole r) => r == TexRole.Normal ? 2 : r == TexRole.Gray ? 1 : 0;

        private static void WhitelistMaterialTextures(AtoContext ctx, Material mat, string reason)
        {
            foreach (var p in mat.GetTexturePropertyNames())
                if (mat.GetTexture(p) is Texture2D t)
                {
                    var canon = ctx.DedupMap.TryGetValue(t, out var c) ? c : t;
                    MarkWhitelist(ctx.GetOrAddTex(canon), reason);
                }
            nadena.dev.ndmf.ErrorReport.ReportError(AtoL10n.Localizer,
                nadena.dev.ndmf.ErrorSeverity.Information, "warn.unknown_shader", mat.name, reason);
        }

        internal static void MarkWhitelist(TexInfo ti, string reason)
        {
            if (ti.Whitelisted) return;
            ti.Whitelisted = true;
            ti.WhitelistReason = reason;
            AtoLog.Debugf($"whitelist texture '{(ti.Tex ? ti.Tex.name : "?")}': {reason}");
        }
    }
}
