using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// Rebuilds meshes so each submesh can own an independent copy of its vertices.
    /// 重建网格，使每个 submesh 都能拥有独立顶点副本，以支持按材质槽分别重映射 UV。
    /// </summary>
    internal static class AtoMeshRebuilder
    {
        internal readonly struct UvRemapPlan
        {
            public readonly int SubMeshIndex;
            public readonly int UvChannel;
            public readonly Rect TargetRect;
            public readonly Vector2 SourceMin;
            public readonly Vector2 SourceSpan;
            public readonly Vector2 Translation;

            public UvRemapPlan(int subMeshIndex, int uvChannel, Rect targetRect, Vector2 sourceMin, Vector2 sourceSpan, Vector2 translation)
            {
                SubMeshIndex = subMeshIndex;
                UvChannel = uvChannel;
                TargetRect = targetRect;
                SourceMin = sourceMin;
                SourceSpan = sourceSpan;
                Translation = translation;
            }
        }

        public static Mesh RebuildWithIndependentSubmeshes(Mesh source, IReadOnlyCollection<UvRemapPlan> remapPlans)
        {
            if (source == null)
            {
                return null;
            }

            var vertices = source.vertices;
            var normals = source.normals;
            var tangents = source.tangents;
            var colors = source.colors;
            var colors32 = source.colors32;
            var boneWeights = source.boneWeights;
            var bindposes = source.bindposes;
            var uvChannels = LoadAllUvChannels(source);

            var newVertices = new List<Vector3>();
            var newNormals = normals != null && normals.Length == vertices.Length ? new List<Vector3>() : null;
            var newTangents = tangents != null && tangents.Length == vertices.Length ? new List<Vector4>() : null;
            var newColors = colors != null && colors.Length == vertices.Length ? new List<Color>() : null;
            var newColors32 = colors32 != null && colors32.Length == vertices.Length ? new List<Color32>() : null;
            var newBoneWeights = boneWeights != null && boneWeights.Length == vertices.Length ? new List<BoneWeight>() : null;
            var newUvChannels = Enumerable.Range(0, 8).Select(_ => new List<Vector2>()).ToArray();
            var newSubmeshTriangles = Enumerable.Range(0, source.subMeshCount).Select(_ => new List<int>()).ToArray();
            var mapping = new Dictionary<VertexUseKey, int>();
            var newToOriginal = new List<int>();
            var newToSubmesh = new List<int>();

            for (var subMesh = 0; subMesh < source.subMeshCount; subMesh++)
            {
                var triangles = source.GetTriangles(subMesh);
                for (var i = 0; i < triangles.Length; i++)
                {
                    var originalIndex = triangles[i];
                    var key = new VertexUseKey(subMesh, originalIndex);
                    if (!mapping.TryGetValue(key, out var newIndex))
                    {
                        newIndex = newVertices.Count;
                        mapping.Add(key, newIndex);
                        newToOriginal.Add(originalIndex);
                        newToSubmesh.Add(subMesh);

                        newVertices.Add(vertices[originalIndex]);
                        newNormals?.Add(normals[originalIndex]);
                        newTangents?.Add(tangents[originalIndex]);
                        newColors?.Add(colors[originalIndex]);
                        newColors32?.Add(colors32[originalIndex]);
                        newBoneWeights?.Add(boneWeights[originalIndex]);
                        for (var uvChannel = 0; uvChannel < 8; uvChannel++)
                        {
                            var sourceUvs = uvChannels[uvChannel];
                            newUvChannels[uvChannel].Add(originalIndex < sourceUvs.Count ? sourceUvs[originalIndex] : Vector2.zero);
                        }
                    }

                    newSubmeshTriangles[subMesh].Add(newIndex);
                }
            }

            foreach (var plan in remapPlans)
            {
                if (plan.SubMeshIndex < 0 || plan.SubMeshIndex >= source.subMeshCount || plan.UvChannel < 0 || plan.UvChannel >= 8)
                {
                    continue;
                }

                var spanX = Mathf.Max(plan.SourceSpan.x, 0.000001f);
                var spanY = Mathf.Max(plan.SourceSpan.y, 0.000001f);
                for (var newIndex = 0; newIndex < newVertices.Count; newIndex++)
                {
                    if (newToSubmesh[newIndex] != plan.SubMeshIndex)
                    {
                        continue;
                    }

                    var shifted = newUvChannels[plan.UvChannel][newIndex] + plan.Translation;
                    var nx = (shifted.x - plan.SourceMin.x) / spanX;
                    var ny = (shifted.y - plan.SourceMin.y) / spanY;
                    newUvChannels[plan.UvChannel][newIndex] = new Vector2(
                        plan.TargetRect.xMin + plan.TargetRect.width * nx,
                        plan.TargetRect.yMin + plan.TargetRect.height * ny);
                }
            }

            var rebuilt = new Mesh
            {
                name = source.name,
                indexFormat = newVertices.Count > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16,
                bindposes = bindposes,
                bounds = source.bounds,
            };
            rebuilt.SetVertices(newVertices);
            if (newNormals != null) rebuilt.SetNormals(newNormals);
            if (newTangents != null) rebuilt.SetTangents(newTangents);
            if (newColors != null) rebuilt.SetColors(newColors);
            if (newColors32 != null) rebuilt.SetColors(newColors32);
            if (newBoneWeights != null && newBoneWeights.Count == newVertices.Count) rebuilt.boneWeights = newBoneWeights.ToArray();
            for (var uvChannel = 0; uvChannel < 8; uvChannel++)
            {
                if (newUvChannels[uvChannel].Count == newVertices.Count)
                {
                    rebuilt.SetUVs(uvChannel, newUvChannels[uvChannel]);
                }
            }

            rebuilt.subMeshCount = source.subMeshCount;
            for (var subMesh = 0; subMesh < source.subMeshCount; subMesh++)
            {
                rebuilt.SetTriangles(newSubmeshTriangles[subMesh], subMesh, false);
            }

            CopyBlendShapes(source, rebuilt, newToOriginal);
            rebuilt.RecalculateBounds();
            return rebuilt;
        }

        private static List<Vector2>[] LoadAllUvChannels(Mesh source)
        {
            var result = new List<Vector2>[8];
            for (var i = 0; i < 8; i++)
            {
                result[i] = new List<Vector2>();
                source.GetUVs(i, result[i]);
            }
            return result;
        }

        private static void CopyBlendShapes(Mesh source, Mesh rebuilt, IReadOnlyList<int> newToOriginal)
        {
            if (source.blendShapeCount <= 0)
            {
                return;
            }

            var vertexCount = source.vertexCount;
            var deltaVertices = new Vector3[vertexCount];
            var deltaNormals = new Vector3[vertexCount];
            var deltaTangents = new Vector3[vertexCount];

            for (var shapeIndex = 0; shapeIndex < source.blendShapeCount; shapeIndex++)
            {
                var shapeName = source.GetBlendShapeName(shapeIndex);
                var frameCount = source.GetBlendShapeFrameCount(shapeIndex);
                for (var frameIndex = 0; frameIndex < frameCount; frameIndex++)
                {
                    Array.Clear(deltaVertices, 0, deltaVertices.Length);
                    Array.Clear(deltaNormals, 0, deltaNormals.Length);
                    Array.Clear(deltaTangents, 0, deltaTangents.Length);
                    var frameWeight = source.GetBlendShapeFrameWeight(shapeIndex, frameIndex);
                    source.GetBlendShapeFrameVertices(shapeIndex, frameIndex, deltaVertices, deltaNormals, deltaTangents);

                    var rebuiltVertices = new Vector3[newToOriginal.Count];
                    var rebuiltNormals = new Vector3[newToOriginal.Count];
                    var rebuiltTangents = new Vector3[newToOriginal.Count];
                    for (var i = 0; i < newToOriginal.Count; i++)
                    {
                        var originalIndex = newToOriginal[i];
                        rebuiltVertices[i] = deltaVertices[originalIndex];
                        rebuiltNormals[i] = deltaNormals[originalIndex];
                        rebuiltTangents[i] = deltaTangents[originalIndex];
                    }

                    rebuilt.AddBlendShapeFrame(shapeName, frameWeight, rebuiltVertices, rebuiltNormals, rebuiltTangents);
                }
            }
        }

        private readonly struct VertexUseKey : IEquatable<VertexUseKey>
        {
            private readonly int _subMesh;
            private readonly int _originalIndex;

            public VertexUseKey(int subMesh, int originalIndex)
            {
                _subMesh = subMesh;
                _originalIndex = originalIndex;
            }

            public bool Equals(VertexUseKey other)
            {
                return _subMesh == other._subMesh && _originalIndex == other._originalIndex;
            }

            public override bool Equals(object obj)
            {
                return obj is VertexUseKey other && Equals(other);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    return (_subMesh * 397) ^ _originalIndex;
                }
            }
        }
    }
}
