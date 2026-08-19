// ATO — Avatar Texture Optimizer
// Blend-shape area factor: for animated blend shapes, compute the world-space triangle
// area at weight 0 and at the blend shape's full frame (weight 100), and take the maximum.
// Combinations / negative weights / weights above 100 are deliberately ignored to avoid
// combinatorial explosion (spec #6).
// 形态键面积因子：对动画中的形态键，计算权重 0 与形态键满帧（权重 100）下的世界空间三角形
// 面积并取最大值。刻意不考虑排列组合、负数、超过 100 的情况（避免组合爆炸，规范 #6）。

using System.Collections.Generic;
using UnityEngine;

namespace net.fosa.ato.editor
{
    /// <summary>
    /// Blend-shape area analysis. 形态键面积分析。
    /// </summary>
    public static class BlendShapeAnalyzer
    {
        /// <summary>
        /// Compute the conservative area scale factor for the given animated blend-shape
        /// indices: max over shapes of max(area(weight 0), area(full frame)) / area(weight 0).
        /// 计算给定动画形态键下标的保守面积缩放因子：对各形态键取 max(0 权重面积, 满帧面积)/0 权重面积，再取最大。
        /// </summary>
        public static float ComputeFactor(SkinnedMeshRenderer smr, IEnumerable<int> shapeIndices)
        {
            if (smr == null || smr.sharedMesh == null) return 1f;
            var mesh = smr.sharedMesh;
            var verts = mesh.vertices;
            int[] tris = mesh.triangles;
            if (verts.Length == 0 || tris.Length == 0) return 1f;

            float baseArea = TotalArea(verts, tris);
            if (baseArea <= 1e-9f) return 1f;

            float maxRatio = 1f;
            var work = new Vector3[verts.Length];
            var delta = new Vector3[verts.Length];
            foreach (var shapeIndex in shapeIndices)
            {
                if (shapeIndex < 0 || shapeIndex >= mesh.blendShapeCount) continue;
                int frameCount = mesh.GetBlendShapeFrameCount(shapeIndex);
                if (frameCount <= 0) continue;

                // Use the frame with the largest weight (weight 100, typically frame 0).
                // 取权重最大的帧（通常为权重 100 的第 0 帧）。
                int bestFrame = 0;
                float bestWeight = -1f;
                for (int f = 0; f < frameCount; f++)
                {
                    float w = mesh.GetBlendShapeFrameWeight(shapeIndex, f);
                    if (w > bestWeight) { bestWeight = w; bestFrame = f; }
                }

                mesh.GetBlendShapeFrameVertices(shapeIndex, bestFrame, delta, null, null);
                for (int i = 0; i < verts.Length; i++) work[i] = verts[i] + delta[i];
                float fullArea = TotalArea(work, tris);
                float ratio = Mathf.Max(baseArea, fullArea) / baseArea;
                if (ratio > maxRatio) maxRatio = ratio;
            }
            return maxRatio;
        }

        private static float TotalArea(Vector3[] verts, int[] tris)
        {
            float area = 0f;
            int triCount = tris.Length / 3;
            for (int t = 0; t < triCount; t++)
            {
                var a = verts[tris[t * 3]];
                var b = verts[tris[t * 3 + 1]];
                var c = verts[tris[t * 3 + 2]];
                area += 0.5f * Vector3.Cross(b - a, c - a).magnitude;
            }
            return area;
        }
    }
}
