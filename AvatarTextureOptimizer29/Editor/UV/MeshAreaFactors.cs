// Per-triangle world area factors: blendshapes at max(0, 100) per key (no combinations,
// per spec) and nothing else here; animated scale is applied per-renderer in IslandExtractor.
// 逐三角形世界面积因子：形态键逐键取 max(0,100)（按需求不组合）；动画缩放在 IslandExtractor
// 内按渲染器施加。

using UnityEngine;

namespace net.fosa.ato.editor
{
    internal static class MeshAreaFactors
    {
        /// <summary>
        /// factor[t] >= 1: max over (base, each blendshape at 100) triangle area, per key.
        /// factor[t] >= 1：逐键取 base 与该键=100 时三角形面积的最大值。
        /// </summary>
        internal static float[] BlendshapeFactors(Mesh mesh, Vector3[] vertices, int[] tris,
            int triCount, float[] baseWorldArea, Matrix4x4 l2w)
        {
            var factor = new float[triCount];
            for (int t = 0; t < triCount; t++) factor[t] = 1f;

            int shapeCount = mesh.blendShapeCount;
            if (shapeCount == 0) return factor;

            var deltas = new Vector3[vertices.Length];
            var shaped = new Vector3[3];
            for (int si = 0; si < shapeCount; si++)
            {
                int frame = mesh.GetBlendShapeFrameCount(si) - 1; // last frame = 100 / 末帧=100
                mesh.GetBlendShapeFrameVertices(si, frame, deltas, null, null);

                for (int t = 0; t < triCount; t++)
                {
                    int i0 = tris[t * 3], i1 = tris[t * 3 + 1], i2 = tris[t * 3 + 2];
                    shaped[0] = l2w.MultiplyPoint3x4(vertices[i0] + deltas[i0]);
                    shaped[1] = l2w.MultiplyPoint3x4(vertices[i1] + deltas[i1]);
                    shaped[2] = l2w.MultiplyPoint3x4(vertices[i2] + deltas[i2]);
                    float area = TriArea(shaped[0], shaped[1], shaped[2]);
                    if (area > factor[t] * baseWorldArea[t] && baseWorldArea[t] > 1e-12f)
                        factor[t] = area / baseWorldArea[t];
                }
            }

            return factor;
        }

        internal static float TriArea(Vector3 a, Vector3 b, Vector3 c) =>
            Vector3.Cross(b - a, c - a).magnitude * 0.5f;
    }
}
