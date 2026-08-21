// MeshMaterialRewriter.cs - Apply packing results back onto the avatar:
// 将装箱结果写回Avatar：
//  - mesh UV remap (placed islands) / 网格UV重映射（已放置的岛）
//  - material texture reference rewrite (atlases / rescaled) / 材质贴图引用改写（图集/缩放图）
//  - animation object-curve rewrite via ndmf AnimatorServices / 动画对象曲线改写（ndmf AnimatorServices）
//  - material & texture dedup + opaque material-slot merging / 材质与贴图去重 + 不透明材质槽合并
// Only texture references are touched; no other shader parameters are ever modified.
// 只改贴图引用，绝不修改材质的其他任何着色器参数。
using System;
using System.Collections.Generic;
using System.Linq;
using Fosa.ATO.Editor.Analysis;
using Fosa.ATO.Editor.Atlas;
using Fosa.ATO.Editor.Core;
using Fosa.ATO.Runtime;
using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;
using UnityEngine;

namespace Fosa.ATO.Editor.Pipeline
{
    public sealed class RewriteResult
    {
        public readonly Dictionary<Mesh, Mesh> meshMap = new Dictionary<Mesh, Mesh>();
        public readonly Dictionary<Material, Material> materialMap = new Dictionary<Material, Material>();
        public readonly Dictionary<Texture2D, Texture2D> textureMap = new Dictionary<Texture2D, Texture2D>();
        public int mergedSlots;
    }

    public static class MeshMaterialRewriter
    {
        /// <summary>Run all rewrites. / 执行全部改写。</summary>
        public static RewriteResult Run(BuildContext ctx, UsageGraph g, PackResult pack, List<AtlasImage> images, List<Texture2D> rescaled, ATOSettings st, ATOProgress progress)
        {
            using (ATOLog.Scope("Rewrite"))
            {
                var r = new RewriteResult();
                // texture -> replacement (atlas image or rescaled) / 贴图->替换物
                BuildTextureMap(g, images, rescaled, r);
                progress?.Report(0.2f, "Materials");

                // materials & meshes / 材质与网格
                BuildMaterialMap(ctx, g, r.textureMap, r);
                RewriteMeshes(g, r);
                progress?.Report(0.6f, "Renderers");

                ApplyToRenderers(ctx, g, r);
                if (st.materialDedup || st.textureDedup) DedupPass(ctx, g, r, st);
                progress?.Report(0.9f, "Animations");

                // animations: rewrite object curves via AnimatorServices / 动画：经AnimatorServices改写对象曲线
                var asc = ctx.Extension<AnimatorServicesContext>();
                asc.AnimationIndex.RewriteObjectCurves(obj => MapObject(obj, r));
                ATOLog.Info($"rewrite: meshes={r.meshMap.Count} materials={r.materialMap.Count} textures={r.textureMap.Count} mergedSlots={r.mergedSlots}");
                return r;
            }
        }

        private static UnityEngine.Object MapObject(UnityEngine.Object o, RewriteResult r)
        {
            switch (o)
            {
                case Texture2D t: return r.textureMap.TryGetValue(t, out var t2) ? t2 : o;
                case Material m: return r.materialMap.TryGetValue(m, out var m2) ? m2 : o;
                default: return o;
            }
        }

        // ------------------------------------------------------------------
        // Texture map / 贴图映射
        // ------------------------------------------------------------------

        private static void BuildTextureMap(UsageGraph g, List<AtlasImage> images, List<Texture2D> rescaled, RewriteResult r)
        {
            foreach (var img in images)
                foreach (var e in img.sources)
                    foreach (var orig in e.dedupGroup.Append(e.texture))
                        r.textureMap[orig] = img.output;
            foreach (var t in rescaled)
            {
                // rescaled named ATO_R_<orig>: map originals / 由命名找回原图
                var entry = g.textures.FirstOrDefault(x => x.texture != null && t.name == "ATO_R_" + x.texture.name);
                if (entry != null)
                    foreach (var orig in entry.dedupGroup.Append(entry.texture))
                        r.textureMap[orig] = t;
            }
        }

        // ------------------------------------------------------------------
        // Materials / 材质
        // ------------------------------------------------------------------

        private static void BuildMaterialMap(BuildContext ctx, UsageGraph g, Dictionary<Texture2D, Texture2D> texMap, RewriteResult r)
        {
            foreach (var e in g.textures)
            {
                if (e.whitelisted) continue;
                foreach (var u in e.usages)
                {
                    var m = u.material;
                    if (m == null) continue;
                    if (!texMap.TryGetValue(e.texture, out var replacement)) continue;
                    if (r.materialMap.ContainsKey(m)) continue; // already cloned / 已克隆
                    if (!ctx.AssetSaver.IsTemporaryAsset(m))
                    {
                        var clone = UnityEngine.Object.Instantiate(m);
                        clone.name = m.name + "(ATO)";
                        ctx.AssetSaver.SaveAsset(clone);
                        r.materialMap[m] = clone;
                    }
                    else r.materialMap[m] = m; // temporary: edit in place / 临时材质就地改
                }
            }
            // apply texture replacements on clones / 在克隆上应用贴图替换
            foreach (var kv in r.materialMap)
            {
                var m = kv.Value;
                var sh = m.shader;
                if (sh == null) continue;
                int n = sh.GetPropertyCount();
                for (int i = 0; i < n; i++)
                {
                    if (sh.GetPropertyType(i) != UnityEngine.Rendering.ShaderPropertyType.Texture) continue;
                    string p = sh.GetPropertyName(i);
                    if (m.GetTexture(p) is Texture2D t && texMap.TryGetValue(t, out var rep)) m.SetTexture(p, rep);
                }
            }
        }

        // ------------------------------------------------------------------
        // Meshes / 网格
        // ------------------------------------------------------------------

        private static void RewriteMeshes(UsageGraph g, RewriteResult r)
        {
            foreach (var grp in g.groups)
            {
                if (!grp.Processable || !grp.islands.Any(i => i.placed)) continue;
                var mesh = grp.key.mesh;
                if (!r.meshMap.TryGetValue(mesh, out var nm) && !ReferenceEquals(nm, mesh))
                {
                    nm = UnityEngine.Object.Instantiate(mesh);
                    nm.name = mesh.name + "(ATO)";
                    r.meshMap[mesh] = nm;
                }
                var uvs = new List<Vector2>(mesh.vertexCount);
                mesh.GetUVs(grp.key.channel, uvs);
                foreach (var isl in grp.islands.Where(i => i.placed))
                {
                    float sx = 1f / Mathf.Max(1e-9f, isl.uvMax.x - isl.uvMin.x);
                    float sy = 1f / Mathf.Max(1e-9f, isl.uvMax.y - isl.uvMin.y);
                    foreach (var v in isl.vertices)
                    {
                        var uv = uvs[v] + isl.uvShift;
                        var local = new Vector2((uv.x - isl.uvMin.x) * sx, (uv.y - isl.uvMin.y) * sy);
                        var a = AtlasPacker.RotatedUV(local, isl.rotated);
                        uvs[v] = new Vector2(
                            (isl.atlasRect.xMin + a.x * isl.atlasRect.width) / grp.AtlasWidthOf(),
                            (isl.atlasRect.yMin + a.y * isl.atlasRect.height) / grp.AtlasHeightOf());
                    }
                }
                nm.SetUVs(grp.key.channel, uvs);
            }
        }

        // ------------------------------------------------------------------
        // Renderers / 渲染器
        // ------------------------------------------------------------------

        private static void ApplyToRenderers(BuildContext ctx, UsageGraph g, RewriteResult r)
        {
            foreach (var rend in g.scan.renderers)
            {
                var mats = rend.sharedMaterials;
                bool changed = false;
                for (int i = 0; i < mats.Length; i++)
                    if (mats[i] != null && r.materialMap.TryGetValue(mats[i], out var m2)) { mats[i] = m2; changed = true; }
                if (changed) rend.sharedMaterials = mats;

                var mesh = UsageGraphBuilder.MeshOf(rend);
                if (mesh != null && r.meshMap.TryGetValue(mesh, out var nm))
                {
                    if (rend is SkinnedMeshRenderer smr) smr.sharedMesh = nm;
                    else if (rend.GetComponent<MeshFilter>() is MeshFilter mf) mf.sharedMesh = nm;
                }
            }
        }

        // ------------------------------------------------------------------
        // Dedup & slot merge / 去重与槽合并
        // ------------------------------------------------------------------

        private static void DedupPass(BuildContext ctx, UsageGraph g, RewriteResult r, ATOSettings st)
        {
            using (ATOLog.Scope("DedupPass"))
            {
                // textures: identical pixels + params / 贴图：像素与参数一致
                if (st.textureDedup)
                {
                    var byHash = new Dictionary<(Hash128, int, int), List<Texture2D>>();
                    foreach (var t in r.textureMap.Values.Distinct().Where(t => t != null))
                        byHash[(ImportSettingsUtil.ContentHash(t), t.width, t.height)].Add(t);
                    foreach (var kv in byHash.Where(x => x.Value.Count > 1))
                    {
                        var keep = kv.Value[0];
                        foreach (var dup in kv.Value.Skip(1))
                            r.textureMap[dup] = keep; // transitively resolved by consumers / 消费方传递解析
                    }
                    ApplyTextureDedup(ctx, g, r);
                }

                // materials: identical params / 材质：参数一致
                if (st.materialDedup)
                {
                    var mats = ctx.AvatarRootObject.GetComponentsInChildren<Renderer>(true)
                        .SelectMany(x => x.sharedMaterials).Where(m => m != null).Distinct().ToList();
                    var byKey = new Dictionary<string, List<Material>>();
                    foreach (var m in mats)
                        byKey[Fingerprint(m)].Add(m);
                    foreach (var kv in byKey.Where(x => x.Value.Count > 1))
                    {
                        var keep = kv.Value[0];
                        foreach (var dup in kv.Value.Skip(1))
                            if (dup != keep) r.materialMap[dup] = keep;
                    }
                    ApplyMaterialDedup(ctx, r);
                    TryMergeSlots(ctx, g, r);
                }
            }
        }

        private static void ApplyTextureDedup(BuildContext ctx, UsageGraph g, RewriteResult r)
        {
            // replace on materials / 材质上替换
            foreach (var rend in ctx.AvatarRootObject.GetComponentsInChildren<Renderer>(true))
                foreach (var m in rend.sharedMaterials)
                    ReplaceOnMaterial(m, r.textureMap);
            foreach (var kv in r.materialMap.Values) ReplaceOnMaterial(kv, r.textureMap);
        }

        private static void ReplaceOnMaterial(Material m, Dictionary<Texture2D, Texture2D> map)
        {
            if (m == null || m.shader == null) return;
            int n = m.shader.GetPropertyCount();
            for (int i = 0; i < n; i++)
            {
                if (m.shader.GetPropertyType(i) != UnityEngine.Rendering.ShaderPropertyType.Texture) continue;
                string p = m.shader.GetPropertyName(i);
                if (m.GetTexture(p) is Texture2D t && map.TryGetValue(t, out var rep) && rep != t) m.SetTexture(p, rep);
            }
        }

        private static void ApplyMaterialDedup(BuildContext ctx, RewriteResult r)
        {
            foreach (var rend in ctx.AvatarRootObject.GetComponentsInChildren<Renderer>(true))
            {
                var mats = rend.sharedMaterials;
                bool changed = false;
                for (int i = 0; i < mats.Length; i++)
                    if (mats[i] != null && r.materialMap.TryGetValue(mats[i], out var m2) && m2 != mats[i]) { mats[i] = m2; changed = true; }
                if (changed) rend.sharedMaterials = mats;
            }
            // compose map (dup -> keep chains) / 映射链压缩
            foreach (var k in r.materialMap.Keys.ToList())
            {
                var v = r.materialMap[k]; int guard = 0;
                while (r.materialMap.TryGetValue(v, out var v2) && v2 != v && guard++ < 32) v = v2;
                r.materialMap[k] = v;
            }
        }

        /// <summary>Merge identical opaque slots of a mesh when no animation switches its slots individually. / 动画未单独切换材质槽时合并网格上相同的材质槽。</summary>
        private static void TryMergeSlots(BuildContext ctx, UsageGraph g, RewriteResult r)
        {
            foreach (var rend in g.scan.renderers)
            {
                string path = g.scan.paths.GetValueOrDefault(rend);
                if (path == null) continue;
                // any slot animation on this renderer? / 该渲染器是否存在材质槽动画？
                bool animated = g.scan.slotSwaps.Keys.Any(k => k.Item1 == path);
                if (animated) continue;
                var mesh = UsageGraphBuilder.MeshOf(rend);
                if (mesh == null || !r.meshMap.TryGetValue(mesh, out var nm)) nm = mesh;
                var mats = rend.sharedMaterials;
                if (mats.Length != mesh.subMeshCount) continue; // multi-slot beyond submeshes: skip / 超出子网格数的多槽：跳过
                var groups = new Dictionary<Material, List<int>>();
                for (int i = 0; i < mats.Length; i++)
                {
                    if (mats[i] == null) continue;
                    if (!IsOpaque(mats[i])) continue; // opaque only / 仅不透明
                    if (!groups.TryGetValue(mats[i], out var l)) groups[mats[i]] = l = new List<int>();
                    l.Add(i);
                }
                var mergeable = groups.Where(kv => kv.Value.Count > 1).ToList();
                if (mergeable.Count == 0) continue;

                // rebuild submeshes / 重建子网格
                var newSubs = new List<int[]>();
                var newMats = new List<Material>();
                var merged = new HashSet<int>();
                foreach (var kv in mergeable)
                {
                    var idx = new List<int>();
                    foreach (var s in kv.Value) { idx.AddRange(nm.GetTriangles(s)); merged.Add(s); }
                    newSubs.Add(idx.ToArray());
                    newMats.Add(kv.Key);
                }
                for (int s = 0; s < mesh.subMeshCount; s++)
                    if (!merged.Contains(s)) { newSubs.Add(nm.GetTriangles(s)); newMats.Add(mats[s]); }
                nm.subMeshCount = newSubs.Count;
                for (int s = 0; s < newSubs.Count; s++) nm.SetTriangles(newSubs[s], s, false);
                rend.sharedMaterials = newMats.ToArray();
                r.mergedSlots += merged.Count - mergeable.Count;
                ATOLog.Info($"merged slots on {path}: {merged.Count} -> {mergeable.Count}");
            }
        }

        private static bool IsOpaque(Material m)
        {
            int q = m.renderQueue;
            return q < 2450;
        }

        /// <summary>Param fingerprint of a material (textures by instance). / 材质参数指纹（贴图按实例）。</summary>
        private static string Fingerprint(Material m)
        {
            var sb = new System.Text.StringBuilder(256);
            sb.Append(m.shader != null ? m.shader.GetInstanceID() : 0).Append('|').Append(m.renderQueue).Append('|');
            sb.Append(string.Join(",", m.shaderKeywords.OrderBy(x => x)));
            var sh = m.shader;
            if (sh == null) return sb.ToString();
            int n = sh.GetPropertyCount();
            for (int i = 0; i < n; i++)
            {
                string p = sh.GetPropertyName(i);
                switch (sh.GetPropertyType(i))
                {
                    case UnityEngine.Rendering.ShaderPropertyType.Color: sb.Append(p).Append('=').Append(m.GetColor(p).ToString("F4")); break;
                    case UnityEngine.Rendering.ShaderPropertyType.Vector: sb.Append(p).Append('=').Append(m.GetVector(p).ToString("F4")); break;
                    case UnityEngine.Rendering.ShaderPropertyType.Float:
                    case UnityEngine.Rendering.ShaderPropertyType.Range: sb.Append(p).Append('=').Append(m.GetFloat(p).ToString("R")); break;
                    case UnityEngine.Rendering.ShaderPropertyType.Texture:
                        sb.Append(p).Append('=').Append(m.GetTexture(p) != null ? m.GetTexture(p).GetInstanceID() : 0)
                          .Append('/').Append(m.GetTextureScale(p).ToString("F4")).Append('/').Append(m.GetTextureOffset(p).ToString("F4"));
                        break;
                }
            }
            return sb.ToString();
        }
    }

    /// <summary>Small helpers on UvGroup for atlas dims. / UvGroup上的图集尺寸辅助。</summary>
    public static class UvGroupAtlasExt
    {
        public static int AtlasWidthOf(this UvGroup g) => g.islands.FirstOrDefault(i => i.placed) is { } i && i.atlasId >= 0 ? AtlasRegistry.Width(i.atlasId) : 1;
        public static int AtlasHeightOf(this UvGroup g) => g.islands.FirstOrDefault(i => i.placed) is { } i && i.atlasId >= 0 ? AtlasRegistry.Height(i.atlasId) : 1;
    }

    /// <summary>Global atlas dimension lookup filled by the pipeline. / 管线填充的全局图集尺寸表。</summary>
    public static class AtlasRegistry
    {
        private static readonly Dictionary<int, (int, int)> _dims = new Dictionary<int, (int, int)>();
        public static void Register(int id, int w, int h) => _dims[id] = (w, h);
        public static int Width(int id) => _dims.TryGetValue(id, out var d) ? d.Item1 : 1;
        public static int Height(int id) => _dims.TryGetValue(id, out var d) ? d.Item2 : 1;
        public static void Clear() => _dims.Clear();
    }
}
