using System;
using System.Collections.Generic;
using UnityEngine;

namespace Fosa.AvatarTextureOptimizer.Editor.Analysis
{
    /// <summary>EN: Computes triangle area maxima at blend-shape weights 0 and 100 only. ZH: 仅计算形态键权重 0 与 100 时的三角形最大面积。</summary>
    internal static class MorphAreaAnalyzer
    {
        public static Dictionary<(int a, int b, int c), float> Build(Mesh mesh, IEnumerable<int> triangleIndices)
        {
            var positions = mesh.vertices;
            var result = new Dictionary<(int, int, int), float>();
            var indices = new List<int>(triangleIndices);
            for (var i = 0; i + 2 < indices.Count; i += 3)
            {
                var key = (indices[i], indices[i + 1], indices[i + 2]);
                result[key] = Area(positions[key.Item1], positions[key.Item2], positions[key.Item3]);
            }
            if (mesh.blendShapeCount == 0) return result;

            var delta = new Vector3[mesh.vertexCount];
            var low = new Vector3[mesh.vertexCount];
            var high = new Vector3[mesh.vertexCount];
            var normals = new Vector3[mesh.vertexCount];
            var tangents = new Vector3[mesh.vertexCount];
            var morphed = new Vector3[mesh.vertexCount];
            for (var shape = 0; shape < mesh.blendShapeCount; shape++)
            {
                EvaluateAt100(mesh, shape, delta, low, high, normals, tangents);
                for (var v = 0; v < positions.Length; v++) morphed[v] = positions[v] + delta[v];
                for (var i = 0; i + 2 < indices.Count; i += 3)
                {
                    var key = (indices[i], indices[i + 1], indices[i + 2]);
                    result[key] = Mathf.Max(result[key], Area(morphed[key.Item1], morphed[key.Item2], morphed[key.Item3]));
                }
            }
            return result;
        }

        private static void EvaluateAt100(Mesh mesh, int shape, Vector3[] output, Vector3[] low, Vector3[] high,
            Vector3[] normals, Vector3[] tangents)
        {
            Array.Clear(output, 0, output.Length);
            var count = mesh.GetBlendShapeFrameCount(shape);
            if (count == 0) return;
            var lower = -1; var upper = -1;
            for (var frame = 0; frame < count; frame++)
            {
                var weight = mesh.GetBlendShapeFrameWeight(shape, frame);
                if (weight <= 100f && (lower < 0 || weight > mesh.GetBlendShapeFrameWeight(shape, lower))) lower = frame;
                if (weight >= 100f && (upper < 0 || weight < mesh.GetBlendShapeFrameWeight(shape, upper))) upper = frame;
            }
            if (lower < 0) lower = 0;
            if (upper < 0) upper = count - 1;
            var lowWeight = mesh.GetBlendShapeFrameWeight(shape, lower);
            var highWeight = mesh.GetBlendShapeFrameWeight(shape, upper);
            mesh.GetBlendShapeFrameVertices(shape, lower, low, normals, tangents);
            if (lower == upper || Mathf.Approximately(lowWeight, highWeight))
            {
                var multiplier = Mathf.Approximately(lowWeight, 0f) ? 0f : 100f / lowWeight;
                for (var i = 0; i < output.Length; i++) output[i] = low[i] * multiplier;
                return;
            }
            mesh.GetBlendShapeFrameVertices(shape, upper, high, normals, tangents);
            var t = Mathf.InverseLerp(lowWeight, highWeight, 100f);
            for (var i = 0; i < output.Length; i++) output[i] = Vector3.LerpUnclamped(low[i], high[i], t);
        }

        private static float Area(Vector3 a, Vector3 b, Vector3 c) => Vector3.Cross(b - a, c - a).magnitude * 0.5f;
    }
}
