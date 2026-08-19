// Stage 8: mesh UV rewrite, material/animation reference updates, AAO UV compat.
// 阶段8：网格UV重写、材质与动画引用更新、AAO UV兼容。
using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf.animator;
using UnityEngine;

namespace net.fosa.ato.editor
{
    public static class RewriteStage
    {
        public static void Run(AtoContext ctx)
        {
            using (AtoLog.Time("RewriteStage", (l, ms) => ctx.Stats.StageTimes.Add((l, ms))))
            {
                AtoProgress.BeginStage(AtoL10n.Tr("stage.rewrite"));
                RewriteMeshes(ctx);
                RewriteMaterials(ctx);
            }
        }

        // ---- mesh UV rewrite / 网格UV重写 ----
        private static void RewriteMeshes(AtoContext ctx)
        {
            // meshes with atlased islands / 有已装箱岛的网格
            var byMesh = new Dictionary<Mesh, List<MappingKey>>();
            foreach (var kv in ctx.Islands)
            {
                if (!kv.Value.Any(i => i.PlacedAtlas >= 0)) continue;
                if (!byMesh.TryGetValue(kv.Key.Mesh, out var list)) byMesh[kv.Key.Mesh] = list = new List<MappingKey>();
                list.Add(kv.Key);
            }

            var meshClones = new Dictionary<Mesh, Mesh>();
            foreach (var pair in byMesh)
            {
                var mesh = pair.Key;
                var clone = UnityEngine.Object.Instantiate(mesh);
                clone.name = mesh.name + "_ATO";
                nadena.dev.ndmf.ObjectRegistry.RegisterReplacedObject(mesh, clone);

                foreach (var key in pair.Value)
                {
                    var islands = ctx.Islands[key];
                    var data = IslandStage.UvCache[key];
                    var uv = new List<Vector2>();
                    clone.GetUVs(key.Channel, uv);
                    var newUv = uv.ToArray();
                    var atlasSizeOf = new Dictionary<int, Vector2Int>();
                    foreach (var u in ctx.PackUnits)
                        foreach (var t in u.Textures)
                            if (t.AtlasIndex >= 0) atlasSizeOf[t.AtlasIndex] = u.AtlasSize;

                    // vertex -> island assignment via triangles / 顶点归属岛
                    foreach (var isl in islands)
                    {
                        if (isl.PlacedAtlas < 0) continue;
                        if (!atlasSizeOf.TryGetValue(isl.PlacedAtlas, out var atlasSize) ||
                            atlasSize.x <= 0) continue;
                        foreach (var t0 in isl.Triangles)
                            for (int k = 0; k < 3; k++)
                            {
                                int vi = data.Indices[t0 + k];
                                var p = uv[vi] + isl.Shift;
                                // uv -> src px (in bbox-crop local space) / UV→源像素局部坐标
                                var srcPxX = (p.x - isl.BBoxMin.x) / Mathf.Max(1e-9f, isl.BBoxMax.x - isl.BBoxMin.x) * isl.SrcPixelSize.x;
                                var srcPxY = (p.y - isl.BBoxMin.y) / Mathf.Max(1e-9f, isl.BBoxMax.y - isl.BBoxMin.y) * isl.SrcPixelSize.y;
                                // scale into raster space / 缩放到光栅空间
                                var scaled = new Vector2(
                                    srcPxX * isl.RasterSize.x / Mathf.Max(1, isl.SrcPixelSize.x),
                                    srcPxY * isl.RasterSize.y / Mathf.Max(1, isl.SrcPixelSize.y));
                                var atlasPx = BakeStage.IslandToAtlasPx(isl, scaled);
                                newUv[vi] = new Vector2(atlasPx.x / atlasSize.x, atlasPx.y / atlasSize.y);
                            }
                    }
                    clone.SetUVs(key.Channel, newUv.ToList());
                }
                meshClones[mesh] = clone;
                ctx.Stats.MeshesRewritten++;
            }

            // swap on renderers + AAO evacuation / 替换渲染器网格并做AAO疏散
            foreach (var ri in ctx.Renderers)
            {
                if (!meshClones.TryGetValue(ri.Mesh, out var clone)) continue;
                var rewrittenChannels = byMesh[ri.Mesh].Select(k => k.Channel).Distinct().ToList();

                if (ri.Renderer is SkinnedMeshRenderer smr)
                {
                    AaoCompat.EvacuateUvChannels(smr, ri.Mesh, clone, rewrittenChannels);
                    smr.sharedMesh = clone;
                }
                else if (ri.Renderer.TryGetComponent<MeshFilter>(out var mf))
                {
                    mf.sharedMesh = clone;
                }
                ctx.Ndmf.AssetSaver.SaveAsset(clone);
            }
        }

        // ---- material & animation rewrite / 材质与动画重写 ----
        private static void RewriteMaterials(AtoContext ctx)
        {
            var outputs = ctx.Textures.Values.Where(t => t.Output != null)
                .ToDictionary(t => t.Tex, t => (Texture)t.Output);
            if (outputs.Count == 0) return;

            foreach (var t in ctx.Textures.Values.Where(t => t.Output != null))
                ctx.Ndmf.AssetSaver.SaveAsset(t.Output);

            var matClones = new Dictionary<Material, Material>();
            Material Retarget(Material m)
            {
                if (m == null) return null;
                if (matClones.TryGetValue(m, out var c)) return c;
                bool needs = m.GetTexturePropertyNames()
                    .Any(p => m.GetTexture(p) is Texture2D t && outputs.ContainsKey(t));
                if (!needs) { matClones[m] = m; return m; }
                var clone = UnityEngine.Object.Instantiate(m);
                clone.name = m.name;
                nadena.dev.ndmf.ObjectRegistry.RegisterReplacedObject(m, clone);
                // ONLY texture references are modified, never any other shader parameter.
                // 只改贴图引用，绝不动其他着色器参数。
                foreach (var p in clone.GetTexturePropertyNames())
                    if (clone.GetTexture(p) is Texture2D t && outputs.TryGetValue(t, out var o))
                        clone.SetTexture(p, o);
                ctx.Ndmf.AssetSaver.SaveAsset(clone);
                matClones[m] = clone;
                ctx.Stats.MaterialsCloned++;
                return clone;
            }

            foreach (var ri in ctx.Renderers)
            {
                var mats = ri.Renderer.sharedMaterials;
                for (int s = 0; s < mats.Length; s++) mats[s] = Retarget(mats[s]);
                ri.Renderer.sharedMaterials = mats;
            }

            // animations: swap materials AND direct texture references / 动画中的材质与贴图引用
            var asc = ctx.Ndmf.Extension<AnimatorServicesContext>();
            asc.AnimationIndex.RewriteObjectCurves(o =>
            {
                if (o is Material m)
                {
                    // materials appearing only in animations also need retargeting / 仅动画中出现的材质同样处理
                    return Retarget(m);
                }
                if (o is Texture2D t && outputs.TryGetValue(t, out var ot)) return ot;
                return o;
            });
        }
    }
}
