// -----------------------------------------------------------------------------
// ATOMeshRebuild.cs — clone meshes & rewrite UVs to atlas coordinates.
// ATOMeshRebuild.cs —— 克隆网格并重写 UV 至图集坐标。
//
// Every renderer with atlased groups gets its own mesh clone (never mutate shared
// assets). AAO UV channel evacuation: if AAO claims the channel, the ORIGINAL UVs
// are copied to a free channel and registered via UVUsageCompabilityAPI.
// 每个含图集化组的渲染器克隆自己的网格（绝不修改共享资产）。AAO UV 搬移：若 AAO
// 声明占用该通道，则将原UV复制到空闲通道并经 UVUsageCompabilityAPI 登记。
// -----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using UnityEngine;

namespace net.fosa.ato.editor
{
    internal static class ATOMeshRebuild
    {
        /// <summary>Rewrite UVs of all atlased groups; assign cloned meshes.
        /// 重写所有图集化组的UV；赋值克隆网格。</summary>
        public static void Run(ATOBuildState st)
        {
            // group meshes needing changes / 需要修改的网格
            var byRenderer = new Dictionary<RendererInfo, List<UvGroupInfo>>();
            foreach (var g in st.uvGroups)
            {
                if (!g.eligibleForAtlas || !st.settings.generateAtlas) continue;
                if (!g.atlasified) continue;
                if (!byRenderer.TryGetValue(g.owner, out var list)) byRenderer[g.owner] = list = new List<UvGroupInfo>();
                list.Add(g);
            }

            foreach (var (r, groups) in byRenderer)
            {
                if (!st.meshClones.TryGetValue(r, out var clone) || clone == null)
                {
                    clone = UnityEngine.Object.Instantiate(r.mesh);
                    clone.name = r.mesh.name + "(ATO)";
                    st.meshClones[r] = clone;
                    st.assetSaver.SaveAsset(clone);
                }

                // AAO evacuation BEFORE rewriting (original UVs must be saved away)
                // 改写前执行 AAO 搬移（先保存原始 UV）
                ATOAaoBridge.EvacuateRenderer(r, clone, st);

                foreach (var g in groups)
                    RewriteChannel(clone, g, st);

                if (r.renderer is SkinnedMeshRenderer smr) smr.sharedMesh = clone;
                else
                {
                    var mf = r.renderer.GetComponent<MeshFilter>();
                    if (mf != null) mf.sharedMesh = clone;
                }
            }

            ATOLog.Info($"Mesh rebuild: {byRenderer.Count} renderers, {st.meshClones.Count} cloned meshes");
        }

        /// <summary>Rewrite one channel of the cloned mesh for one group.
        /// 为一个组重写克隆网格的一个通道。</summary>
        private static void RewriteChannel(Mesh mesh, UvGroupInfo g, ATOBuildState st)
        {
            var uvs = GetUV(mesh, g.channel);
            bool any = false;

            foreach (var isl in g.islands)
            {
                if (isl.atlasId < 0 || isl.cellRect.width <= 0) continue;

                // atlas rect (normalized) / 图集矩形（归一化）
                var atlas = FindAtlas(st, isl);
                if (atlas == null) continue;

                foreach (var target in AllIslandCopies(isl))
                {
                    float aw = atlas.width, ah = atlas.height;
                    var cr = target.cellRect;
                    var rectPx = new Rect(cr.x * IslandRaster.Cell, cr.y * IslandRaster.Cell,
                        cr.width * IslandRaster.Cell, cr.height * IslandRaster.Cell);

                    foreach (var vi in target.vertexIndices)
                    {
                        var uv = uvs[vi];
                        // raw → normalized island space / 原始→归一化岛空间
                        var local = new Vector2(
                            (uv.x + target.uvOffset.x - target.uvBounds.xMin) /
                            Mathf.Max(1e-6f, target.uvBounds.width),
                            (uv.y + target.uvOffset.y - target.uvBounds.yMin) /
                            Mathf.Max(1e-6f, target.uvBounds.height));

                        // normalized → atlas rect (with rotation) / 归一化→图集矩形（含旋转）
                        Vector2 atlasUv;
                        if (target.rotated)
                        {
                            // transposed: island (x,y) → atlas (y, 1-x) within the rect
                            // 转置：岛内 (x,y) → 矩形内 (y, 1-x)
                            atlasUv = new Vector2(
                                rectPx.x + local.y * rectPx.width,
                                rectPx.y + (1f - local.x) * rectPx.height);
                        }
                        else
                        {
                            atlasUv = new Vector2(
                                rectPx.x + local.x * rectPx.width,
                                rectPx.y + local.y * rectPx.height);
                        }

                        uvs[vi] = new Vector2(atlasUv.x / aw, atlasUv.y / ah);
                        any = true;
                    }
                }
            }

            if (any)
            {
                // AAO evacuation handled BEFORE overwrite (see plugin stage order).
                // AAO 搬移已在改写前处理（见插件阶段顺序）。
                mesh.SetUVs(g.channel, new List<Vector2>(uvs));
            }
        }

        internal static AtlasResult FindAtlas(ATOBuildState st, IslandInfo isl)
        {
            foreach (var a in st.atlases)
                if (a.id == isl.atlasId)
                    return a;
            return null;
        }

        private static IEnumerable<IslandInfo> AllIslandCopies(IslandInfo isl)
        {
            yield return isl;
            foreach (var dup in isl.mergedDuplicates) yield return dup;
        }

        private static Vector2[] GetUV(Mesh mesh, int channel)
        {
            var l = new List<Vector2>();
            try
            {
                mesh.GetUVs(channel, l);
                return l.ToArray();
            }
            catch (Exception)
            {
                var l3 = new List<Vector3>();
                mesh.GetUVs(channel, l3);
                var arr = new Vector2[l3.Count];
                for (int i = 0; i < l3.Count; i++) arr[i] = l3[i];
                return arr;
            }
        }
    }
}
