// MeshProcessor.cs / MeshProcessor.cs
// Rewrites UVs on working meshes to point at atlas positions. Applies meshes to renderers.
// Also handles tangent rotation when UV islands are rotated 90° (normal-map-safe).
// 在工作网格上重写UV指向图集位置。将网格应用到渲染器。处理UV岛旋转90°时的切线旋转（法线贴图安全）。

using System.Collections.Generic;
using nadena.dev.ndmf;
using net.fosa.avatar_texture_optimizer.Editor.Atlas;
using net.fosa.avatar_texture_optimizer.Editor.Core;
using net.fosa.avatar_texture_optimizer.Editor.Util;
using UnityEngine;

namespace net.fosa.avatar_texture_optimizer.Editor.Processing
{
    public static class MeshProcessor
    {
        public static void Remesh(AvatarAnalysisResult analysis, bool generateAtlas)
        {
            if (!generateAtlas) return; // non-atlas mode: keep UVs intact / 非图集模式：保持UV不变

            foreach (var rendererEntry in analysis.Renderers)
            {
                var mesh = rendererEntry.WorkingMesh;
                if (mesh == null) continue;

                // Load all 8 UV channels / 加载所有8个UV通道
                Vector2[][] uvs = new Vector2[8][];
                var tmpList = new List<Vector2>();
                for (int ch = 0; ch < 8; ch++)
                {
                    tmpList.Clear();
                    mesh.GetUVs(ch, tmpList);
                    uvs[ch] = new Vector2[Mathf.Max(mesh.vertexCount, tmpList.Count)];
                    for (int i = 0; i < tmpList.Count; i++) uvs[ch][i] = tmpList[i];
                    for (int i = tmpList.Count; i < mesh.vertexCount; i++) uvs[ch][i] = Vector2.zero;
                }

                // Load normals + tangents for rotation when islands are rotated 90° on normal maps
                // 加载法线+切线，用于法线贴图岛旋转90°时旋转
                Vector3[] normals = mesh.normals;
                Vector4[] tangents = mesh.tangents;
                bool hasNormals = normals != null && normals.Length == mesh.vertexCount;
                bool hasTangents = tangents != null && tangents.Length == mesh.vertexCount;
                // Per-vertex flag: whether tangent was already rotated (avoid double-rotation)
                // 逐顶点标记：切线是否已旋转（避免重复旋转）
                bool[] tangentRotated = hasTangents ? new bool[mesh.vertexCount] : null;

                // Group islands for this renderer by UV channel
                // 按UV通道分组此渲染器的岛
                var islandsByChannel = new Dictionary<int, List<UVIsland>>();
                foreach (var island in analysis.Islands)
                {
                    if (island.RendererEntry != rendererEntry) continue;
                    if (island.AssignedAtlas == null || island.IsWhitelisted) continue;
                    int ch = island.UVChannel;
                    if (ch < 0 || ch >= 8) continue;
                    if (!islandsByChannel.TryGetValue(ch, out var list))
                    {
                        list = new List<UVIsland>();
                        islandsByChannel[ch] = list;
                    }
                    list.Add(island);
                }

                foreach (var kv in islandsByChannel)
                {
                    int ch = kv.Key;
                    bool channelHasNormal = false;
                    foreach (var isl in kv.Value) if (isl.NeedsNormalRotation) { channelHasNormal = true; break; }

                    foreach (var island in kv.Value)
                    {
                        Rect srcUvRect = island.BoundsUV;
                        var atl = island.AssignedAtlas;
                        float invW = 1f / Mathf.Max(1, atl.Width);
                        float invH = 1f / Mathf.Max(1, atl.Height);
                        Rect dstUv = new Rect(
                            island.AtlasRect.x * invW,
                            island.AtlasRect.y * invH,
                            island.AtlasRect.width * invW,
                            island.AtlasRect.height * invH);

                        bool rotated = island.Rotated;
                        // Rotate tangents only for normal-map islands (and once per vertex)
                        // 仅对法线贴图岛旋转切线（每顶点一次）
                        bool rotateTangent = rotated && island.NeedsNormalRotation && hasTangents;

                        for (int t = 0; t + 2 < island.Triangles.Count; t += 3)
                        {
                            for (int k = 0; k < 3; k++)
                            {
                                int vidx = island.Triangles[t + k];
                                if (vidx < 0 || vidx >= mesh.vertexCount) continue;
                                Vector2 uv = uvs[ch][vidx];
                                float u = Mathf.InverseLerp(srcUvRect.xMin, srcUvRect.xMax, uv.x);
                                float v = Mathf.InverseLerp(srcUvRect.yMin, srcUvRect.yMax, uv.y);
                                u = Mathf.Clamp01(u); v = Mathf.Clamp01(v);

                                if (rotated)
                                {
                                    // 90° clockwise UV rotation in source -> (u,v) -> (1-v, u)
                                    // 源UV顺时针旋转90° -> (u,v) -> (1-v, u)
                                    float tu = u; u = 1f - v; v = tu;
                                }

                                float nu = Mathf.Lerp(dstUv.xMin, dstUv.xMax, u);
                                float nv = Mathf.Lerp(dstUv.yMin, dstUv.yMax, v);
                                uvs[ch][vidx] = new Vector2(nu, nv);

                                // Tangent rotation: rotate tangent -90° about the normal to keep
                                // the tangent basis consistent with the 90° UV rotation.
                                // 切线旋转：绕法线将tangent旋转-90°，使切线基与90°UV旋转保持一致。
                                if (rotateTangent && !tangentRotated[vidx])
                                {
                                    Vector4 t4 = tangents[vidx];
                                    Vector3 T = new Vector3(t4.x, t4.y, t4.z);
                                    Vector3 N = hasNormals ? normals[vidx] : new Vector3(0, 0, 1);
                                    // Bitangent B = cross(N, T) * w. Rotating UVs 90° clockwise is
                                    // equivalent to swapping the basis: T' = B * -w, B' = T * w.
                                    // bitangent B = cross(N,T)*w。将UV顺时针旋转90°等价于交换基：T' = B * -w, B' = T * w。
                                    Vector3 B = Vector3.Cross(N, T).normalized * t4.w;
                                    Vector3 Tnew = -B; // T' = rotate T by -90° about N
                                    tangents[vidx] = new Vector4(Tnew.x, Tnew.y, Tnew.z, t4.w);
                                    tangentRotated[vidx] = true;
                                }
                            }
                        }
                    }
                }

                // Write back all UV channels / 写回所有UV通道
                for (int ch = 0; ch < 8; ch++)
                {
                    tmpList.Clear();
                    for (int i = 0; i < mesh.vertexCount; i++) tmpList.Add(uvs[ch][i]);
                    mesh.SetUVs(ch, tmpList);
                }
                if (hasTangents) mesh.tangents = tangents;
                mesh.RecalculateBounds();
                try { mesh.RecalculateUVDistributionMetrics(); } catch { /* ignore */ }
            }
        }

        public static void ApplyToRenderers(AvatarAnalysisResult analysis, BuildContext context)
        {
            foreach (var re in analysis.Renderers)
            {
                if (re.WorkingMesh == null) continue;
                if (re.Skinned != null)
                {
                    re.Skinned.sharedMesh = re.WorkingMesh;
                    context.SetEnableUVDistributionRecalculation(re.WorkingMesh, true);
                }
                else if (re.Renderer is MeshFilter mf)
                {
                    mf.sharedMesh = re.WorkingMesh;
                    context.SetEnableUVDistributionRecalculation(re.WorkingMesh, true);
                }
                context.AssetSaver.SaveAsset(re.WorkingMesh);
            }
        }
    }
}
