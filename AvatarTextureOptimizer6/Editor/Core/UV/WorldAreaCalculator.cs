using NetFosa.AvatarTextureOptimizer.Editor.Analysis;
using UnityEngine;

namespace NetFosa.AvatarTextureOptimizer.Editor.UV
{
    /// <summary>
    /// 世界面积计算：UV 岛在模型上的真实面积（m²）。
    /// 考虑形态键（每个形态键仅取 0 与 100 两态取大，不组合）与动画缩放（按最大缩放）。
    /// 仅计算面积，旋转不影响面积，因此只用 lossyScale。
    /// </summary>
    public static class WorldAreaCalculator
    {
        public static float ComputeIslandAreaM2(UvIsland island, AnimationAnalysis animation)
        {
            var group = island.group;
            var mesh = group.mesh;
            var renderer = group.renderer;

            var vertices = mesh.vertices;
            var slotTris = mesh.GetTriangles(group.slotIndex);
            var bounds = island.uvBounds;

            var scaleFactor = MaxLossyScale(renderer);
            if (animation != null && animation.TryGetAreaScaleFactor(renderer, out float animFactor))
                scaleFactor *= Mathf.Max(1f, animFactor);

            float maxArea = 0f;

            // 静态姿势
            float restArea = SumTriangleArea(vertices, slotTris, island.triangleIndices);
            maxArea = restArea;

            // 形态键（SkinnedMeshRenderer）
            if (renderer is SkinnedMeshRenderer skinned && skinned.sharedMesh != null)
            {
                var sm = skinned.sharedMesh;
                int shapeCount = sm.blendShapeCount;
                for (int s = 0; s < shapeCount; s++)
                {
                    int frameCount = sm.GetBlendShapeFrameCount(s);
                    if (frameCount <= 0) continue;
                    // weight 100 = 最后一帧
                    int frame = frameCount - 1;
                    var deltas = new Vector3[vertices.Length];
                    var normals = new Vector3[vertices.Length];
                    var tangents = new Vector3[vertices.Length];
                    sm.GetBlendShapeFrameVertices(s, frame, deltas, normals, tangents);
                    var posed = new Vector3[vertices.Length];
                    for (int i = 0; i < vertices.Length; i++) posed[i] = vertices[i] + deltas[i];
                    float area = SumTriangleArea(posed, slotTris, island.triangleIndices);
                    if (area > maxArea) maxArea = area;
                }
            }

            return maxArea * scaleFactor * scaleFactor;
        }

        private static float SumTriangleArea(Vector3[] verts, int[] slotTris, System.Collections.Generic.List<int> triIndices)
        {
            double sum = 0;
            for (int k = 0; k < triIndices.Count; k += 3)
            {
                int v0 = slotTris[triIndices[k]];
                int v1 = slotTris[triIndices[k + 1]];
                int v2 = slotTris[triIndices[k + 2]];
                sum += TriangleArea(verts[v0], verts[v1], verts[v2]);
            }
            return (float)sum;
        }

        private static double TriangleArea(Vector3 a, Vector3 b, Vector3 c)
        {
            var ab = b - a;
            var ac = c - a;
            var cr = Vector3.Cross(ab, ac);
            return 0.5 * cr.magnitude;
        }

        private static float MaxLossyScale(Renderer renderer)
        {
            var ls = renderer.transform.lossyScale;
            return Mathf.Max(Mathf.Abs(ls.x), Mathf.Max(Mathf.Abs(ls.y), Mathf.Abs(ls.z)));
        }
    }
}
