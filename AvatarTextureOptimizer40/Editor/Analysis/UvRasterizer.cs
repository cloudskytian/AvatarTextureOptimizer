using Unity.Burst;
using UnityEngine;

namespace Fosa.Ato.Editor.Analysis
{
    /// <summary>
    /// UV analysis helpers: safe out-of-[0,1] normalization and world-area computation. The Burst
    /// 4px rasterizer for triangle masks is used by the atlas packer; this static class holds the
    /// UV-space safety checks and world-area math shared across stages.
    /// UV 分析工具：安全的越界归一判断与世界面积计算。装箱器使用 Burst 4px 光栅化；本类持有各阶段
    /// 共享的 UV 安全检查与面积计算。
    /// </summary>
    internal static class UvRasterizer
    {
        public const int Granularity = 4;

        /// <summary>
        /// Returns true if the UVs can be normalized into [0,1] WITHOUT crossing a wrap seam (whole
        /// range fits within a single 1-wide span). If they already span a seam / rely on Repeat,
        /// returns false (caller whitelists + warns).
        /// 判断 UV 是否可整体归一到 [0,1] 且不跨 wrap 缝。若跨缝/依赖 Repeat 则返回 false。
        /// </summary>
        public static bool CanNormalize(Vector2[] uvs, int[] tris, out Vector2 shift)
        {
            shift = Vector2.zero;
            if (uvs == null || uvs.Length == 0) return false;
            float minX = float.MaxValue, maxX = float.MinValue;
            float minY = float.MaxValue, maxY = float.MinValue;
            foreach (var v in uvs)
            {
                if (v.x < minX) minX = v.x; if (v.x > maxX) maxX = v.x;
                if (v.y < minY) minY = v.y; if (v.y > maxY) maxY = v.y;
            }
            // If already in [0,1] / 已在范围
            if (minX >= 0 && maxX <= 1 && minY >= 0 && maxY <= 1) return true;
            float w = maxX - minX, h = maxY - minY;
            if (w > 1f || h > 1f) return false; // spans across more than one tile => seam / 跨多个平铺
            // Whole-island translate possible; compute integer floor shift.
            // 可整体平移归一：计算整数平移
            shift = new Vector2(-Mathf.Floor(minX), -Mathf.Floor(minY));
            return true;
        }

        /// <summary>Compute world-space triangle area summed over a submesh, max over blendshapes & scale. / 计算世界面积（形态键与缩放取最大值）。</summary>
        public static float MaxWorldArea(Mesh mesh, int subMesh, Transform root, float maxAnimScale)
        {
            float baseArea = SubMeshWorldArea(mesh, subMesh, root, null);
            float max = baseArea;
            if (mesh.blendShapeCount > 0)
            {
                // Evaluate at weight 100 only (no permutations per spec) / 仅取权重 100（不做排列组合）
                var baseVerts = mesh.vertices;
                for (int i = 0; i < mesh.blendShapeCount; i++)
                {
                    int lastFrame = mesh.GetBlendShapeFrameCount(i) - 1;
                    mesh.GetBlendShapeFrameVertices(i, lastFrame, out var dv, out _, out _);
                    // Approximate area delta using delta vertices / 用 delta 顶点近似面积
                    var verts = (Vector3[])baseVerts.Clone();
                    for (int k = 0; k < verts.Length && k < dv.Length; k++) verts[k] += (Vector3)dv[k];
                    max = Mathf.Max(max, SubMeshWorldArea(mesh, subMesh, root, verts));
                }
            }
            return max * maxAnimScale * maxAnimScale;
        }

        private static float SubMeshWorldArea(Mesh mesh, int subMesh, Transform root, Vector3[] vertsOverride)
        {
            var verts = vertsOverride ?? mesh.vertices;
            var tris = mesh.GetTriangles(subMesh);
            var scale = root != null ? root.lossyScale : Vector3.one;
            float area = 0f;
            for (int i = 0; i + 2 < tris.Length; i += 3)
            {
                Vector3 a = Vector3.Scale(verts[tris[i]], scale);
                Vector3 b = Vector3.Scale(verts[tris[i + 1]], scale);
                Vector3 c = Vector3.Scale(verts[tris[i + 2]], scale);
                area += Vector3.Cross(b - a, c - a).magnitude * 0.5f;
            }
            return area;
        }
    }
}
