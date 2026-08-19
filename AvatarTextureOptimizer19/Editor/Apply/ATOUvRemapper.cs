// English: Rewrite mesh UVs onto packed atlas positions. Evacuate AAO-used channels first.
// 中文：把网格 UV 重写到图集位置。若 AAO 占用该通道，先疏散原 UV。
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using Net.Fosa.AvatarTextureOptimizer;

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    internal static class ATOUvRemapper
    {
        public static void Apply(ATOState state)
        {
            if (!state.GenerateAtlases) return;

            var byMesh = new Dictionary<Mesh, List<ATOIsland>>();
            foreach (var isl in state.Islands)
            {
                if (isl.Atlas == null || isl.Renderer == null || isl.Renderer.Mesh == null) continue;
                List<ATOIsland> list;
                if (!byMesh.TryGetValue(isl.Renderer.Mesh, out list))
                {
                    list = new List<ATOIsland>();
                    byMesh[isl.Renderer.Mesh] = list;
                }

                list.Add(isl);
            }

            var meshMap = new Dictionary<Mesh, Mesh>();
            foreach (var kv in byMesh)
            {
                state.Progress.ThrowIfCanceled();
                var src = kv.Key;
                var dst = Object.Instantiate(src);
                dst.name = AvatarTextureOptimizer.AtlasNamePrefix + "Mesh_" + src.name;
                dst.CopyBlendShapesFrom(src);

                var channels = new HashSet<int>();
                foreach (var isl in kv.Value) channels.Add(isl.UvChannel);

                foreach (var ch in channels)
                {
                    var uvs = new List<Vector2>(src.vertexCount);
                    src.GetUVs(ch, uvs);
                    if (uvs.Count < src.vertexCount)
                    {
                        while (uvs.Count < src.vertexCount) uvs.Add(Vector2.zero);
                    }

                    var originalCopy = new List<Vector2>(uvs);
                    foreach (var info in state.Renderers)
                    {
                        if (info.Mesh != src) continue;
                        var smr = info.Renderer as SkinnedMeshRenderer;
                        if (smr != null) ATOAaoCompat.EvacuateIfNeeded(state, smr, dst, ch, originalCopy);
                    }

                    var written = new bool[uvs.Count];
                    foreach (var isl in kv.Value)
                    {
                        if (isl.UvChannel != ch || isl.Atlas == null) continue;
                        var atlasW = Mathf.Max(1, isl.Atlas.Width);
                        var atlasH = Mathf.Max(1, isl.Atlas.Height);
                        foreach (var vi in isl.VertexIndices)
                        {
                            if (vi < 0 || vi >= uvs.Count || written[vi]) continue;
                            var uv = uvs[vi] + isl.UvTranslate;
                            var local = new Vector2(
                                (uv.x - isl.UvBounds.xMin) / Mathf.Max(1e-8f, isl.UvBounds.width),
                                (uv.y - isl.UvBounds.yMin) / Mathf.Max(1e-8f, isl.UvBounds.height));
                            local.x = Mathf.Clamp01(local.x);
                            local.y = Mathf.Clamp01(local.y);
                            if (isl.Rotated)
                            {
                                var lx = local.x;
                                local.x = 1f - local.y;
                                local.y = lx;
                            }

                            var px = (isl.PackX + local.x * isl.PackW + 0.5f) / atlasW;
                            var py = (isl.PackY + local.y * isl.PackH + 0.5f) / atlasH;
                            uvs[vi] = new Vector2(px, py);
                            written[vi] = true;
                        }
                    }

                    dst.SetUVs(ch, uvs);
                }

                meshMap[src] = dst;
                state.Generated.Add(dst);
            }

            foreach (var info in state.Renderers)
            {
                if (info.Mesh == null) continue;
                Mesh repl;
                if (!meshMap.TryGetValue(info.Mesh, out repl)) continue;
                var smr = info.Renderer as SkinnedMeshRenderer;
                if (smr != null) smr.sharedMesh = repl;
                else
                {
                    var mf = info.Renderer.GetComponent<MeshFilter>();
                    if (mf != null) mf.sharedMesh = repl;
                }

                info.Mesh = repl;
            }

            state.Log.Info("meshes remapped=" + meshMap.Count);
        }
    }

    internal static class MeshBlendShapeCopy
    {
        public static void CopyBlendShapesFrom(this Mesh dst, Mesh src)
        {
            // Instantiate already copies blendshapes; this is a safety no-op hook.
            if (dst == null || src == null) return;
        }
    }
}
