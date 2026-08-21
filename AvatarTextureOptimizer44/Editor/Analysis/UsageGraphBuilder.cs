// UsageGraphBuilder.cs - Build the texture<->UV coverage graph after dedup & whitelist expansion.
// 去重与白名单扩展后构建 贴图<->UV 覆盖图。
// Graph nodes / 图节点: TexEntry (unique texture) & UvGroup (mesh,channel)
// Edges / 边: coverage = the texture is sampled (by an eligible context) over islands of the group
using System;
using System.Collections.Generic;
using System.Linq;
using Fosa.ATO.Editor.Core;
using Fosa.ATO.Runtime;
using nadena.dev.ndmf;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEditor;

namespace Fosa.ATO.Editor.Analysis
{
    /// <summary>The full processing graph. / 完整处理图。</summary>
    public sealed class UsageGraph
    {
        public AvatarScan scan;
        public readonly List<TexEntry> textures = new List<TexEntry>();
        public readonly Dictionary<Texture2D, TexEntry> entryOf = new Dictionary<Texture2D, TexEntry>();
        public readonly List<UvGroup> groups = new List<UvGroup>();
        public readonly Dictionary<UvKey, UvGroup> groupOf = new Dictionary<UvKey, UvGroup>();
        /// <summary>Coverage edges. / 覆盖边。</summary>
        public readonly Dictionary<TexEntry, HashSet<UvGroup>> coverageOf = new Dictionary<TexEntry, HashSet<UvGroup>>();
        /// <summary>Warnings accumulated (reported to ndmf console later). / 累计警告（稍后上报ndmf控制台）。</summary>
        public readonly List<(string key, object[] args)> warnings = new List<(string, object[])>();

        public IReadOnlyList<UvGroup> Coverage(TexEntry t) => coverageOf.TryGetValue(t, out var g) ? g.ToList() : (IReadOnlyList<UvGroup>)Array.Empty<UvGroup>();
    }

    public static class UsageGraphBuilder
    {
        /// <summary>Build the graph. / 构建图。</summary>
        public static UsageGraph Build(BuildContext ctx, AvatarScan scan, AvatarTextureOptimizer comp, ATOProgress progress)
        {
            using (ATOLog.Scope("BuildUsageGraph"))
            {
                var g = new UsageGraph { scan = scan };

                // 1) expand whitelist / 扩展白名单
                var wl = ExpandWhitelist(comp.whitelist, scan);
                ATOLog.Detail($"whitelist expanded to {wl.Count} objects / 白名单扩展为{wl.Count}个对象");

                // 2) analyze materials of every renderer slot (+ animated swaps) / 分析每个渲染器材质槽（含动画切换）
                var matAnalyses = new Dictionary<Material, ShaderAnalyzer.MaterialAnalysis>();
                var contexts = new List<(Renderer r, int slot, Material m, ShaderAnalyzer.MaterialAnalysis ma)>();
                CollectContexts(scan, matAnalyses, contexts);

                // 3) texture dedup (content + import settings) / 贴图去重（内容+导入设置）
                Dedup(ctx, g, contexts, wl);

                // 4) create UV groups / 创建UV组
                CreateGroups(g, scan, progress);

                // 5) link coverage edges / 连接覆盖边
                LinkCoverage(g, scan, contexts);

                progress?.Report(1f, "");
                ATOLog.Info($"graph: {g.textures.Count} unique textures / {g.groups.Count} uv groups");
                return g;
            }
        }

        // ------------------------------------------------------------------
        // Whitelist / 白名单
        // ------------------------------------------------------------------

        private static HashSet<UnityEngine.Object> ExpandWhitelist(List<UnityEngine.Object> list, AvatarScan scan)
        {
            var set = new HashSet<UnityEngine.Object>();
            if (list == null) return set;
            var queue = new Queue<UnityEngine.Object>(list.Where(o => o != null));
            while (queue.Count > 0)
            {
                var o = queue.Dequeue();
                if (!set.Add(o)) continue;
                switch (o)
                {
                    case Texture2D _: break; // itself / 自身
                    case Material m: EnqueueMaterialTextures(m, queue); break;
                    case Renderer r:
                        foreach (var m in r.sharedMaterials) if (m != null) queue.Enqueue(m);
                        break;
                    case Mesh mesh:
                        foreach (var r in scan.renderers)
                            if (r is SkinnedMeshRenderer smr && smr.sharedMesh == mesh || r.GetComponent<MeshFilter>() is MeshFilter mf && mf.sharedMesh == mesh)
                                foreach (var m in r.sharedMaterials) if (m != null) queue.Enqueue(m);
                        break;
                    case GameObject go:
                        foreach (var r in go.GetComponentsInChildren<Renderer>(true)) queue.Enqueue(r);
                        break;
                    case AnimationClip clip:
                        foreach (var b in AnimationUtility.GetObjectReferenceCurveBindings(clip))
                            foreach (var k in AnimationUtility.GetObjectReferenceCurve(clip, b))
                                if (k.value != null) queue.Enqueue(k.value);
                        break;
                    case RuntimeAnimatorController c:
                        foreach (var clip in c.animationClips) if (clip != null) queue.Enqueue(clip);
                        break;
                    default: ATOLog.Detail($"whitelist object of type {o.GetType().Name} treated as opaque marker / 视为不透明标记"); break;
                }
            }
            return set;
        }

        private static void EnqueueMaterialTextures(Material m, Queue<UnityEngine.Object> q)
        {
            if (m == null || m.shader == null) return;
            int n = m.shader.GetPropertyCount();
            for (int i = 0; i < n; i++)
            {
                if (m.shader.GetPropertyType(i) != ShaderPropertyType.Texture) continue;
                if (m.GetTexture(m.shader.GetPropertyName(i)) is Texture2D t) q.Enqueue(t);
            }
        }

        // ------------------------------------------------------------------
        // Contexts / 上下文
        // ------------------------------------------------------------------

        private static void CollectContexts(AvatarScan scan, Dictionary<Material, ShaderAnalyzer.MaterialAnalysis> cache, List<(Renderer, int, Material, ShaderAnalyzer.MaterialAnalysis)> contexts)
        {
            foreach (var r in scan.renderers)
            {
                string path = scan.paths[r];
                var mats = r.sharedMaterials;
                for (int slot = 0; slot < mats.Length; slot++)
                {
                    var set = new List<Material>();
                    if (mats[slot] != null) set.Add(mats[slot]);
                    if (scan.slotSwaps.TryGetValue((path, slot), out var swaps)) set.AddRange(swaps.Where(m => m != null));
                    foreach (var m in set.Distinct())
                    {
                        if (!cache.TryGetValue(m, out var ma)) cache[m] = ma = ShaderAnalyzer.Analyze(m, scan, path);
                        ShaderAnalyzer.AddAnimatedCutoffs(scan, m, ma, path); // merge animated cutoff range / 合并动画cutoff范围
                        contexts.Add((r, slot, m, ma));
                    }
                }
            }
        }

        // ------------------------------------------------------------------
        // Dedup / 去重
        // ------------------------------------------------------------------

        private static void Dedup(BuildContext ctx, UsageGraph g, List<(Renderer r, int slot, Material m, ShaderAnalyzer.MaterialAnalysis ma)> contexts, HashSet<UnityEngine.Object> wl)
        {
            using (ATOLog.Scope("DedupTextures"))
            {
                // collect every referenced texture / 收集全部被引用贴图
                var seen = new HashSet<Texture2D>();
                foreach (var (r, slot, m, ma) in contexts)
                    foreach (var p in ma.props) seen.Add(p.texture);

                var byKey = new Dictionary<TexKey, TexEntry>();
                foreach (var tex in seen)
                {
                    var snap = ImportSettingsUtil.Snap(tex);
                    var key = new TexKey(ImportSettingsUtil.ContentHash(tex), Hash128.Parse(snap.Fingerprint() + ":" + tex.format));
                    if (!byKey.TryGetValue(key, out var entry))
                    {
                        entry = new TexEntry
                        {
                            texture = tex,
                            key = key,
                            assetPath = AssetDatabase.GetAssetPath(tex),
                            import = snap,
                            hasAlphaChannel = UnityEngine.Experimental.Rendering.GraphicsFormatUtility.HasAlphaChannel(tex.graphicsFormat),
                        };
                        byKey[key] = entry;
                        g.textures.Add(entry);
                        g.entryOf[tex] = entry;
                    }
                    else
                    {
                        g.entryOf[tex] = entry;
                        entry.dedupGroup.Add(tex);
                    }
                }
                // whitelist propagation: any partner whitelisted -> entry whitelisted / 任一成员白名单->整组白名单
                foreach (var e in g.textures)
                {
                    if (wl.Contains(e.texture)) { e.whitelisted = true; continue; }
                    foreach (var t in e.dedupGroup)
                        if (wl.Contains(t)) { e.whitelisted = true; break; }
                    // textures referenced by whitelisted materials / 被白名单材质引用的贴图
                    if (wl.OfType<Material>().Any(m => m.GetTexture(e.assetPath) == e.texture)) e.whitelisted = true;
                }
                ATOLog.Info($"dedup: {seen.Count} refs -> {g.textures.Count} unique / 去重后唯一贴图数");
            }
        }

        // ------------------------------------------------------------------
        // UV groups / UV组
        // ------------------------------------------------------------------

        private static void CreateGroups(UsageGraph g, AvatarScan scan, ATOProgress progress)
        {
            // which (mesh, channel) pairs are referenced by eligible contexts / 合格上下文引用的(网格,通道)
            var needed = new HashSet<UvKey>();
            foreach (var r in scan.renderers)
            {
                var mesh = MeshOf(r);
                if (mesh == null) continue;
                for (int ch = 0; ch < 8; ch++)
                    if (mesh.HasVertexAttribute((VertexAttribute)(4 + ch))) needed.Add(new UvKey(mesh, ch)); // TexCoord0=4 / 枚举从TexCoord0=4开始
            }
            int done = 0;
            foreach (var key in needed)
            {
                progress?.Report(done / (float)needed.Count, "UV islands");
                done++;
                // renderer for area scale: pick the first using this mesh / 取首个使用该网格的渲染器估算面积
                var r = scan.renderers.FirstOrDefault(x => MeshOf(x) == key.mesh);
                var islands = MeshAnalysis.Extract(key.mesh, key.channel, r, scan, scan.paths.GetValueOrDefault(r));
                var grp = new UvGroup { key = key };
                grp.islands.AddRange(islands);
                foreach (var i in islands) i.group = grp;
                if (islands.Any(i => i.wrapped))
                {
                    grp.skipAtlas = true;
                    g.warnings.Add(("ato.warn.uv_wrap", new object[] { key.ToString(), islands.Count(i => i.wrapped) }));
                }
                g.groups.Add(grp);
                g.groupOf[key] = grp;
            }
        }

        internal static Mesh MeshOf(Renderer r)
            => r as SkinnedMeshRenderer != null ? ((SkinnedMeshRenderer)r).sharedMesh
             : r.GetComponent<MeshFilter>() is MeshFilter mf ? mf.sharedMesh : null;

        // ------------------------------------------------------------------
        // Coverage / 覆盖
        // ------------------------------------------------------------------

        private static void LinkCoverage(UsageGraph g, AvatarScan scan, List<(Renderer r, int slot, Material m, ShaderAnalyzer.MaterialAnalysis ma)> contexts)
        {
            using (ATOLog.Scope("LinkCoverage"))
            {
                // island coverage per submesh: (mesh, submesh) -> island ids / 每子网格覆盖的岛
                var subIslands = new Dictionary<(Mesh, int), HashSet<Island>>();
                foreach (var grp in g.groups)
                {
                    var mesh = grp.key.mesh;
                    for (int s = 0; s < mesh.subMeshCount; s++)
                    {
                        var tris = mesh.GetTriangles(s);
                        var ids = new HashSet<Island>(grp.islands.Where(i => i.triangles != null && Overlaps(i.vertices, tris)));
                        subIslands[(mesh, s)] = ids;
                    }
                }

                void AddCoverage(TexEntry e, UvGroup grp, ATOTextureRole role, UsageContext uc)
                {
                    if (!g.coverageOf.TryGetValue(e, out var set)) g.coverageOf[e] = set = new HashSet<UvGroup>();
                    set.Add(grp);
                    grp.textures.Add(e);
                    grp.renderers.Add(uc.renderer);
                    e.usages.Add(uc);
                    e.StrictestRole |= role;
                    // alpha strictness: any non-opaque usage -> transparent category / 任一非不透明用途->透明分类
                    if ((int)uc.alphaMode > (int)e.StrictestAlpha) e.StrictestAlpha = uc.alphaMode;
                    e.StrictestCutoff = uc.alphaMode == ATOAlphaMode.Cutout ? Mathf.Max(e.StrictestCutoff, 0) : e.StrictestCutoff;
                }

                foreach (var (r, slot, m, ma) in contexts)
                {
                    var mesh = MeshOf(r);
                    if (mesh == null) continue;
                    string path = scan.paths[r];
                    var alpha = ma.alphaMode; float cutoff = ma.cutoff;
                    if (ShaderAnalyzer.IsRenderModeAnimated(scan, path)) alpha = ATOAlphaMode.Blend; // strictest guess / 最严估计

                    foreach (var p in ma.props)
                    {
                        var entry = g.entryOf.GetValueOrDefault(p.texture);
                        if (entry == null) continue;
                        // ineligible usage -> whitelist whole texture / 不合格用途->整张贴图白名单
                        if (!p.eligible)
                        {
                            if (p.sampled) // gate off = never sampled, keep optimizable elsewhere / 开关关闭=从未采样，不因此白名单
                            {
                                if (!entry.whitelisted)
                                {
                                    entry.whitelisted = true;
                                    g.warnings.Add(("ato.warn.tex_ineligible", new object[] { entry.texture.name, p.prop, p.reason }));
                                }
                            }
                            continue;
                        }
                        if (!g.groupOf.TryGetValue(new UvKey(mesh, p.uvChannel), out var grp)) continue;
                        var uc = new UsageContext
                        {
                            material = m, prop = p.prop, renderer = r, slot = slot, submesh = Mathf.Min(slot, mesh.subMeshCount - 1),
                            role = p.role, srgb = entry.import.sRGB, alphaMode = alpha, cutoff = cutoff,
                            uvChannel = p.uvChannel,
                        };
                        AddCoverage(entry, grp, p.role, uc);
                    }
                }

                // animated texture swaps merge into the original texture's group / 动画切换的贴图并入原贴图所在组
                foreach (var kv in g.scan.propSwaps)
                {
                    foreach (var v in kv.Value)
                    {
                        if (!(v is Texture2D t)) continue;
                        var entry = g.entryOf.GetValueOrDefault(t);
                        if (entry == null || entry.whitelisted) continue;
                        string prop = kv.Key.prop; // material.<prop> / 材质属性
                        string bare = prop.StartsWith("material.") ? prop.Substring("material.".Length) : prop;
                        // find a context with same renderer & prop to locate the group / 用同渲染器同属性的上下文定位组
                        foreach (var e in g.textures)
                            foreach (var u in e.usages)
                                if (u.renderer != null && g.scan.paths.GetValueOrDefault(u.renderer) == kv.Key.path && u.prop == bare && !e.whitelisted)
                                {
                                    if (g.groupOf.TryGetValue(new UvKey(MeshOf(u.renderer), u.uvChannel), out var grp))
                                        AddCoverage(entry, grp, e.StrictestRole == ATOTextureRole.None ? ATOTextureRole.MainColor : e.StrictestRole, new UsageContext
                                        {
                                            material = u.material, prop = bare, renderer = u.renderer, slot = u.slot, submesh = u.submesh,
                                            role = e.StrictestRole, srgb = entry.import.sRGB, alphaMode = u.alphaMode, cutoff = u.cutoff, uvChannel = u.uvChannel,
                                        });
                                    goto next; // one group per texture is enough / 每贴图入一组即可
                                }
                    next: ;
                    }
                }

                // groups touched by a whitelisted texture: skip atlas, others stay whole-scalable / 白名单贴图所在组跳过图集
                foreach (var e in g.textures.Where(t => t.whitelisted))
                    foreach (var grp in g.Coverage(e))
                        grp.skipAtlas = true;

                ATOLog.Info($"coverage: {g.coverageOf.Count} textures with coverage / 有覆盖关系的贴图数");
            }
        }

        private static bool Overlaps(int[] islandVerts, int[] tris)
        {
            // quick: any vertex of tris inside island's vertex set / 快速判断：三角形任一顶点在岛顶点集中
            if (islandVerts == null || islandVerts.Length == 0) return false;
            var set = new HashSet<int>(islandVerts);
            for (int i = 0; i < tris.Length; i++) if (set.Contains(tris[i])) return true;
            return false;
        }
    }
}
