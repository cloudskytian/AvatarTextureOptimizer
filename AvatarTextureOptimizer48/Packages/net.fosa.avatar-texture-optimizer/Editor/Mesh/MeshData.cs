// Mesh scanning: loads UV channels, triangles, transforms, blendshape deltas and world areas.
// / 网格扫描：读取 UV 通道、三角形、变换、形态键位移与世界面积。

using System.Collections.Generic;
using UnityEngine;

namespace net.fosa.avatar_texture_optimizer.editor.analysis
{
    /// <summary>
    /// Raw mesh data needed for island extraction. / 岛提取所需的网格原始数据。
    /// </summary>
    public sealed class MeshData
    {
        public Mesh Mesh;
        public int VertexCount;
        public int[] Triangles;                  // flattened triangle indices / 平铺三角形索引
        public Vector2[] Uv;                     // the channel we are processing / 正在处理的通道
        public int UvChannel;
        public Vector3[] Vertices;               // local vertices / 局部顶点
        public bool HasBlendShapes;
        public Vector3[][] BlendShapeDeltas;     // per blendshape: per-vertex delta / 每个形态键的逐顶点位移
        public float MaxVertexDelta;             // for fast rejection / 用于快速剔除
        public Matrix4x4 LocalToWorld;

        /// <summary>Read mesh data for one UV channel. / 读取某 UV 通道的网格数据。</summary>
        public static MeshData Load(Mesh mesh, int uvChannel, Transform transform)
        {
            var md = new MeshData
            {
                Mesh = mesh,
                VertexCount = mesh.vertexCount,
                Triangles = mesh.triangles,
                UvChannel = uvChannel,
                Vertices = mesh.vertices,
                LocalToWorld = transform.localToWorldMatrix,
            };

            md.Uv = uvChannel switch
            {
                0 => mesh.uv,
                1 => mesh.uv2,
                2 => mesh.uv3,
                3 => mesh.uv4,
                4 => mesh.uv5,
                5 => mesh.uv6,
                6 => mesh.uv7,
                7 => mesh.uv8,
                _ => null,
            };

            if (md.Uv == null || md.Uv.Length == 0) return null;

            md.HasBlendShapes = mesh.blendShapeCount > 0;
            if (md.HasBlendShapes)
            {
                var deltas = new Vector3[mesh.blendShapeCount][];
                float maxDelta = 0f;
                for (int b = 0; b < mesh.blendShapeCount; b++)
                {
                    var frameCount = mesh.GetBlendShapeFrameCount(b);
                    if (frameCount == 0) { deltas[b] = null; continue; }
                    // Use the last frame (weight 100 extreme). / 取最后一帧（权重 100 的极值）。
                    var frame = frameCount - 1;
                    mesh.GetBlendShapeFrameVertices(b, frame, deltas[b] = new Vector3[mesh.vertexCount], null, null);
                    for (int v = 0; v < deltas[b].Length; v++)
                    {
                        var d = deltas[b][v].magnitude;
                        if (d > maxDelta) maxDelta = d;
                    }
                }
                md.BlendShapeDeltas = deltas;
                md.MaxVertexDelta = maxDelta;
            }

            return md;
        }
    }
}
