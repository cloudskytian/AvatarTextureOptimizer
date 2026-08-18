// Copyright (c) fosa. Licensed under the MIT License.
// Rewrites mesh UVs to the packed atlas layout. When one vertex is shared by islands that land
// in different atlas slots the vertex must be split, and every per-vertex attribute -- including
// blendshape deltas and skin weights -- must be duplicated with it or the avatar will deform
// incorrectly. This is the single most dangerous stage in the pipeline.
// 将网格 UV 重写到装箱后的图集布局。当某个顶点被落在不同图集位置的多个岛共享时必须拆分顶点，
// 且所有逐顶点属性（含形态键 delta 与蒙皮权重）都必须一并复制，否则模型会发生错误形变。
// 这是整条管线中最危险的阶段。

using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// Produces a new mesh whose UVs address the generated atlas.
    /// 生成一份 UV 指向新图集的网格。
    /// </summary>
    public sealed class MeshUVRewriter
    {
        private readonly ATOLogger _log;

        /// <summary>Creates a rewriter. / 创建重写器。</summary>
        public MeshUVRewriter(ATOLogger log)
        {
            _log = log;
        }

        /// <summary>
        /// Rewrites <paramref name="channel" /> of <paramref name="source" /> so every island
        /// maps onto its packed atlas rect.
        /// 重写 <paramref name="source" /> 的 <paramref name="channel" /> 通道，
        /// 使每个岛映射到其装箱后的图集矩形。
        /// </summary>
        /// <returns>A new mesh; the source is never modified. / 返回新网格；源网格永不被修改。</returns>
        public Mesh Rewrite(
            Mesh source,
            int channel,
            IReadOnlyList<UVIsland> islands,
            int atlasWidth,
            int atlasHeight)
        {
            if (source == null || islands == null || islands.Count == 0) return null;
            if (atlasWidth <= 0 || atlasHeight <= 0) return null;

            var originalUVs = new List<Vector2>();
            source.GetUVs(channel, originalUVs);
            if (originalUVs.Count == 0)
            {
                _log?.Warning($"Mesh {source.name} has no UV{channel}; skipping rewrite");
                return null;
            }

            var vertexCount = source.vertexCount;

            // Map every triangle to the island that owns it, so a vertex used by two islands is
            // detected rather than silently taking whichever transform was applied last.
            // 将每个三角形映射到拥有它的岛，
            // 使被两个岛共享的顶点能被检测出来，而不是静默地采用最后施加的那个变换。
            var triangleIsland = new Dictionary<int, int>();
            for (var islandIdx = 0; islandIdx < islands.Count; islandIdx++)
            {
                foreach (var tri in islands[islandIdx].Triangles)
                {
                    triangleIsland[tri] = islandIdx;
                }
            }

            // vertexIsland[v] holds the first island claiming v; conflicts trigger a split.
            // vertexIsland[v] 保存第一个占用 v 的岛；冲突时触发拆分。
            var vertexIsland = new int[vertexCount];
            for (var i = 0; i < vertexCount; i++) vertexIsland[i] = -1;

            // (originalVertex, island) -> new vertex index
            var splitMap = new Dictionary<long, int>();
            var newToOriginal = new List<int>(vertexCount);
            for (var i = 0; i < vertexCount; i++) newToOriginal.Add(i);

            var subMeshCount = source.subMeshCount;
            var newIndices = new List<int>[subMeshCount];
            var triangleCursor = 0;
            var splitCount = 0;

            for (var sub = 0; sub < subMeshCount; sub++)
            {
                var tris = source.GetTriangles(sub);
                var list = new List<int>(tris.Length);

                for (var t = 0; t < tris.Length; t += 3)
                {
                    var triIndex = triangleCursor++;
                    if (!triangleIsland.TryGetValue(triIndex, out var island))
                    {
                        // Triangle belongs to no packed island: keep it exactly as-is.
                        // 三角形不属于任何已装箱的岛：完全保持原样。
                        list.Add(tris[t]);
                        list.Add(tris[t + 1]);
                        list.Add(tris[t + 2]);
                        continue;
                    }

                    for (var k = 0; k < 3; k++)
                    {
                        var v = tris[t + k];
                        if (v < 0 || v >= vertexCount)
                        {
                            list.Add(v);
                            continue;
                        }

                        if (vertexIsland[v] == -1)
                        {
                            vertexIsland[v] = island;
                            list.Add(v);
                            continue;
                        }

                        if (vertexIsland[v] == island)
                        {
                            list.Add(v);
                            continue;
                        }

                        var key = ((long)v << 20) | (uint)island;
                        if (!splitMap.TryGetValue(key, out var newIndex))
                        {
                            newIndex = newToOriginal.Count;
                            newToOriginal.Add(v);
                            splitMap[key] = newIndex;
                            splitCount++;
                        }

                        list.Add(newIndex);
                    }
                }

                newIndices[sub] = list;
            }

            var newVertexCount = newToOriginal.Count;

            // Build the rewritten UV set. Islands transform independently.
            // 构建重写后的 UV 集合。各岛独立变换。
            var islandOfNewVertex = new int[newVertexCount];
            for (var i = 0; i < newVertexCount; i++) islandOfNewVertex[i] = -1;
            for (var i = 0; i < vertexCount; i++) islandOfNewVertex[i] = vertexIsland[i];
            foreach (var kv in splitMap)
            {
                islandOfNewVertex[kv.Value] = (int)(kv.Key & 0xFFFFF);
            }

            var newUVs = new Vector2[newVertexCount];
            for (var i = 0; i < newVertexCount; i++)
            {
                var original = newToOriginal[i];
                var uv = original < originalUVs.Count ? originalUVs[original] : Vector2.zero;
                var islandIdx = islandOfNewVertex[i];

                newUVs[i] = islandIdx >= 0
                    ? TransformUV(uv, islands[islandIdx], atlasWidth, atlasHeight)
                    : uv;
            }

            var mesh = CloneMeshWithSplits(source, newToOriginal, channel, newUVs, newIndices);

            if (splitCount > 0)
            {
                _log?.Detail(
                    $"{source.name}: split {splitCount} vertices across island boundaries " +
                    $"({vertexCount} -> {newVertexCount})");
            }

            return mesh;
        }

        /// <summary>
        /// Maps a UV from its source rect into the island's packed atlas rect, honouring the
        /// normalization offset and the 90 degree rotation chosen by the packer.
        /// 将 UV 从其源矩形映射到岛在图集中的装箱矩形，
        /// 并考虑归一化平移与装箱器选择的 90 度旋转。
        /// </summary>
        public static Vector2 TransformUV(
            Vector2 uv, UVIsland island, int atlasWidth, int atlasHeight)
        {
            // Undo the out-of-range normalization first, so all islands live in [0,1].
            // 先撤销越界归一化，使所有岛都位于 [0,1] 内。
            uv.x += island.NormalizationOffset.x;
            uv.y += island.NormalizationOffset.y;

            var bounds = island.UVBounds;
            if (bounds.width <= 0f || bounds.height <= 0f) return uv;

            // Normalised position inside the island's own bounding box.
            // 岛自身包围盒内的归一化位置。
            var local = new Vector2(
                (uv.x - bounds.xMin) / bounds.width,
                (uv.y - bounds.yMin) / bounds.height);

            var size = island.PackedSize;
            var pos = island.PackedPosition;

            float px, py;
            if (island.Rotated)
            {
                // The packer stored PackedSize pre-rotation; rotating swaps the axes.
                // 装箱器存储的 PackedSize 为旋转前尺寸；旋转会交换轴向。
                px = pos.x + (1f - local.y) * size.y;
                py = pos.y + local.x * size.x;
            }
            else
            {
                px = pos.x + local.x * size.x;
                py = pos.y + local.y * size.y;
            }

            return new Vector2(px / atlasWidth, py / atlasHeight);
        }

        /// <summary>
        /// Clones a mesh, duplicating every per-vertex attribute for split vertices.
        /// Missing any attribute here corrupts deformation, so all of them are handled.
        /// 克隆网格，并为拆分出的顶点复制所有逐顶点属性。
        /// 此处遗漏任何属性都会破坏形变，因此全部予以处理。
        /// </summary>
        private static Mesh CloneMeshWithSplits(
            Mesh source,
            List<int> newToOriginal,
            int rewrittenChannel,
            Vector2[] rewrittenUVs,
            List<int>[] newIndices)
        {
            var mesh = new Mesh
            {
                name = source.name + "_ATO",
                indexFormat = newToOriginal.Count > 65535 ? IndexFormat.UInt32 : source.indexFormat,
            };

            var n = newToOriginal.Count;

            mesh.SetVertices(Remap(source.vertices, newToOriginal, n));

            var normals = source.normals;
            if (normals != null && normals.Length > 0)
                mesh.SetNormals(Remap(normals, newToOriginal, n));

            var tangents = source.tangents;
            if (tangents != null && tangents.Length > 0)
                mesh.SetTangents(Remap(tangents, newToOriginal, n));

            var colors = source.colors;
            if (colors != null && colors.Length > 0)
                mesh.SetColors(Remap(colors, newToOriginal, n));

            // All eight UV channels: the rewritten one uses new values, the rest are copied so
            // secondary UVs (lightmaps, shader effects) keep working.
            // 全部 8 个 UV 通道：被重写的通道使用新值，其余原样复制，
            // 使次级 UV（光照贴图、着色器特效）继续正常工作。
            for (var ch = 0; ch < 8; ch++)
            {
                if (ch == rewrittenChannel)
                {
                    mesh.SetUVs(ch, new List<Vector2>(rewrittenUVs));
                    continue;
                }

                var uvs = new List<Vector4>();
                source.GetUVs(ch, uvs);
                if (uvs.Count == 0) continue;

                var remapped = new List<Vector4>(n);
                for (var i = 0; i < n; i++)
                {
                    var o = newToOriginal[i];
                    remapped.Add(o < uvs.Count ? uvs[o] : Vector4.zero);
                }

                mesh.SetUVs(ch, remapped);
            }

            // Skin weights. Without these a split vertex would collapse to the origin.
            // 蒙皮权重。缺失会导致拆分出的顶点塌陷到原点。
            var boneWeights = source.boneWeights;
            if (boneWeights != null && boneWeights.Length > 0)
            {
                mesh.boneWeights = Remap(boneWeights, newToOriginal, n);
            }

            var bindposes = source.bindposes;
            if (bindposes != null && bindposes.Length > 0) mesh.bindposes = bindposes;

            mesh.subMeshCount = newIndices.Length;
            for (var sub = 0; sub < newIndices.Length; sub++)
            {
                mesh.SetTriangles(newIndices[sub], sub);
            }

            CopyBlendShapes(source, mesh, newToOriginal);

            mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>
        /// Copies every blendshape frame, remapping deltas onto the split vertex set.
        /// 复制所有形态键帧，并将 delta 重映射到拆分后的顶点集合。
        /// </summary>
        private static void CopyBlendShapes(Mesh source, Mesh target, List<int> newToOriginal)
        {
            var shapeCount = source.blendShapeCount;
            if (shapeCount == 0) return;

            var originalCount = source.vertexCount;
            var n = newToOriginal.Count;

            var dv = new Vector3[originalCount];
            var dn = new Vector3[originalCount];
            var dt = new Vector3[originalCount];

            for (var shape = 0; shape < shapeCount; shape++)
            {
                var name = source.GetBlendShapeName(shape);
                var frames = source.GetBlendShapeFrameCount(shape);

                for (var frame = 0; frame < frames; frame++)
                {
                    var weight = source.GetBlendShapeFrameWeight(shape, frame);
                    source.GetBlendShapeFrameVertices(shape, frame, dv, dn, dt);

                    var ndv = new Vector3[n];
                    var ndn = new Vector3[n];
                    var ndt = new Vector3[n];

                    for (var i = 0; i < n; i++)
                    {
                        var o = newToOriginal[i];
                        if (o < 0 || o >= originalCount) continue;
                        ndv[i] = dv[o];
                        ndn[i] = dn[o];
                        ndt[i] = dt[o];
                    }

                    target.AddBlendShapeFrame(name, weight, ndv, ndn, ndt);
                }
            }
        }

        private static T[] Remap<T>(T[] source, List<int> newToOriginal, int count)
        {
            var result = new T[count];
            if (source == null || source.Length == 0) return result;

            for (var i = 0; i < count; i++)
            {
                var o = newToOriginal[i];
                result[i] = o >= 0 && o < source.Length ? source[o] : default;
            }

            return result;
        }

        private static List<Vector3> Remap(Vector3[] source, List<int> newToOriginal, int count)
        {
            var arr = Remap<Vector3>(source, newToOriginal, count);
            return new List<Vector3>(arr);
        }

        private static List<Vector4> Remap(Vector4[] source, List<int> newToOriginal, int count)
        {
            var arr = Remap<Vector4>(source, newToOriginal, count);
            return new List<Vector4>(arr);
        }

        private static List<Color> Remap(Color[] source, List<int> newToOriginal, int count)
        {
            var arr = Remap<Color>(source, newToOriginal, count);
            return new List<Color>(arr);
        }
    }
}
