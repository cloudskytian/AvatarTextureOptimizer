using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using Fosa.AvatarTextureOptimizer.Editor.Analysis;
using Fosa.AvatarTextureOptimizer.Editor.Atlas;
using NUnit.Framework;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace Fosa.AvatarTextureOptimizer.Tests
{
    public sealed class MeshAtlasRemapperTests
    {
        private static readonly int[] SplitSourceVertices = { 0, 1, 2, 0, 2, 3 };

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct HalfUv
        {
            public ushort X;
            public ushort Y;

            public HalfUv(ushort x, ushort y)
            {
                X = x;
                Y = y;
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PositionUv1
        {
            public Vector3 Position;
            public float U;

            public PositionUv1(Vector3 position, float u)
            {
                Position = position;
                U = u;
            }
        }

        [Test]
        public void BaseVertexIndicesBecomeAbsoluteAndUnmodifiedTopologyAndEmptySubmeshSurvive()
        {
            var gameObject = new GameObject("mesh-remap-base-vertex");
            var source = NewBaseVertexMesh();
            Mesh generated = null;
            try
            {
                var renderer = gameObject.AddComponent<MeshRenderer>();
                var setup = BuildSingleGroupPlan(renderer, source, 1);

                generated = new MeshAtlasRemapper(null).Build(setup.Analysis, setup.Plan)[renderer];

                CollectionAssert.AreEqual(new[] { 0 }, generated.GetIndices(0, true));
                Assert.That(generated.GetTopology(0), Is.EqualTo(MeshTopology.Points));
                CollectionAssert.AreEqual(new[] { 1, 2, 3 }, generated.GetIndices(1, true),
                    "the source submesh baseVertex must be applied before vertices are re-keyed");
                Assert.That(generated.GetBaseVertex(1), Is.Zero,
                    "the rebuilt absolute indices use a normalized zero baseVertex");
                Assert.That(generated.GetIndices(2, true), Is.Empty);
                Assert.That(generated.GetTopology(2), Is.EqualTo(MeshTopology.Lines));
                Assert.That(generated.vertexCount, Is.EqualTo(4),
                    "unreferenced source vertices are intentionally omitted");
                Assert.That(generated.vertices[0], Is.EqualTo(source.vertices[0]));
                Assert.That(generated.vertices[1], Is.EqualTo(source.vertices[3]));
                Assert.That(generated.vertices[2], Is.EqualTo(source.vertices[4]));
                Assert.That(generated.vertices[3], Is.EqualTo(source.vertices[5]));
            }
            finally
            {
                Destroy(generated, source, gameObject);
            }
        }

        [Test]
        public void CompletelyEmptyMeshAndTriangleSubmeshRemainValid()
        {
            var gameObject = new GameObject("mesh-remap-empty");
            var source = new Mesh { name = "empty-source", subMeshCount = 1 };
            source.SetIndices(Array.Empty<int>(), MeshTopology.Triangles, 0, false);
            Mesh generated = null;
            try
            {
                var renderer = gameObject.AddComponent<MeshRenderer>();
                var setup = BuildSingleGroupPlan(renderer, source, 0);

                generated = new MeshAtlasRemapper(null).Build(setup.Analysis, setup.Plan)[renderer];

                Assert.That(generated.vertexCount, Is.Zero);
                Assert.That(generated.subMeshCount, Is.EqualTo(1));
                Assert.That(generated.GetTopology(0), Is.EqualTo(MeshTopology.Triangles));
                Assert.That(generated.GetIndices(0, true), Is.Empty);
            }
            finally
            {
                Destroy(generated, source, gameObject);
            }
        }

        [Test]
        public void DifferentIslandPlacementsSplitSharedVerticesAndPreserveBlendShapesAndSkinWeights()
        {
            var gameObject = new GameObject("mesh-remap-rich-split");
            var source = NewRichSplitMesh();
            Mesh generated = null;
            try
            {
                var renderer = gameObject.AddComponent<SkinnedMeshRenderer>();
                renderer.sharedMesh = source;
                var setup = BuildTwoIslandPlan(renderer, source);

                generated = new MeshAtlasRemapper(null).Build(setup.Analysis, setup.Plan)[renderer];

                Assert.That(generated.vertexCount, Is.EqualTo(6),
                    "the two shared source vertices need independent atlas coordinates");
                CollectionAssert.AreEqual(new[] { 0, 1, 2, 3, 4, 5 }, generated.GetIndices(0, true));
                AssertMappedVertices(source.vertices, generated.vertices);
                AssertMappedUvTailsAndUntouchedUvChannel(source, generated);
                AssertMappedColors(source, generated);
                AssertMappedBlendShape(source, generated);
                AssertMappedSkinWeights(source, generated);
                CollectionAssert.AreEqual(source.bindposes, generated.bindposes);
            }
            finally
            {
                Destroy(generated, source, gameObject);
            }
        }

        [Test]
        public void Float16SecondaryUvAndNormalizedColorStreamsRemainByteCompatibleAcrossSplit()
        {
            var gameObject = new GameObject("mesh-remap-half-stream");
            var source = NewFloat16SecondaryUvMesh();
            Mesh generated = null;
            try
            {
                var renderer = gameObject.AddComponent<MeshRenderer>();
                var setup = BuildTwoIslandPlan(renderer, source);

                generated = new MeshAtlasRemapper(null).Build(setup.Analysis, setup.Plan)[renderer];

                Assert.That(generated.GetVertexAttributeFormat(VertexAttribute.TexCoord0),
                    Is.EqualTo(VertexAttributeFormat.Float32));
                Assert.That(generated.GetVertexAttributeDimension(VertexAttribute.TexCoord0), Is.EqualTo(4));
                Assert.That(generated.GetVertexAttributeFormat(VertexAttribute.TexCoord1),
                    Is.EqualTo(VertexAttributeFormat.Float16));
                Assert.That(generated.GetVertexAttributeDimension(VertexAttribute.TexCoord1), Is.EqualTo(2));
                Assert.That(generated.GetVertexAttributeFormat(VertexAttribute.Color),
                    Is.EqualTo(VertexAttributeFormat.UNorm8));

                var sourceUv0 = new List<Vector4>();
                var generatedUv0 = new List<Vector4>();
                var sourceUv1 = new List<Vector2>();
                var generatedUv1 = new List<Vector2>();
                source.GetUVs(0, sourceUv0);
                generated.GetUVs(0, generatedUv0);
                source.GetUVs(1, sourceUv1);
                generated.GetUVs(1, generatedUv1);
                for (var i = 0; i < SplitSourceVertices.Length; i++)
                {
                    var sourceVertex = SplitSourceVertices[i];
                    Assert.That(generatedUv0[i].z, Is.EqualTo(sourceUv0[sourceVertex].z));
                    Assert.That(generatedUv0[i].w, Is.EqualTo(sourceUv0[sourceVertex].w));
                    Assert.That(generatedUv1[i], Is.EqualTo(sourceUv1[sourceVertex]));
                    Assert.That(generated.colors32[i], Is.EqualTo(source.colors32[sourceVertex]));
                }
            }
            finally
            {
                Destroy(generated, source, gameObject);
            }
        }

        [Test]
        public void AaoEvacuationCopiesOriginalUvThroughActualVertexSplits()
        {
            var gameObject = new GameObject("mesh-remap-aao-uv");
            var source = NewSplitMesh();
            Mesh generated = null;
            try
            {
                var renderer = gameObject.AddComponent<SkinnedMeshRenderer>();
                renderer.sharedMesh = source;
                var setup = BuildTwoIslandPlan(renderer, source);
                var flags = BindingFlags.Static | BindingFlags.NonPublic;
                var bridge = new AAOUvCompatibilityBridge(
                    typeof(MeshAtlasRemapperTests).GetMethod(nameof(IsUvUsedByAao), flags),
                    typeof(MeshAtlasRemapperTests).GetMethod(nameof(RegisterAaoEvacuation), flags));
                bridge.Analyze(setup.Analysis);

                generated = new MeshAtlasRemapper(bridge).Build(setup.Analysis, setup.Plan)[renderer];

                var original = new List<Vector4>();
                var evacuated = new List<Vector4>();
                source.GetUVs(0, original);
                generated.GetUVs(1, evacuated);
                Assert.That(evacuated, Has.Count.EqualTo(generated.vertexCount));
                for (var i = 0; i < SplitSourceVertices.Length; i++)
                    Assert.That(evacuated[i], Is.EqualTo(original[SplitSourceVertices[i]]),
                        "AAO's saved channel must contain pre-atlas UVs in rebuilt-vertex order");
            }
            finally
            {
                Destroy(generated, source, gameObject);
            }
        }

        [Test]
        public void FailureAfterWritableMeshDataAllocationDisposesDataAndDestroysPartialOutput()
        {
            var gameObject = new GameObject("mesh-remap-exception-cleanup");
            var source = NewOneComponentUvMesh();
            try
            {
                var renderer = gameObject.AddComponent<MeshRenderer>();
                var setup = BuildSingleGroupPlan(renderer, source, 0);
                var before = AllMeshInstanceIds();

                Assert.Throws<InvalidOperationException>(() =>
                    new MeshAtlasRemapper(null).Build(setup.Analysis, setup.Plan));

                CollectionAssert.AreEqual(before, AllMeshInstanceIds(),
                    "a failed writable MeshData apply must not leave the partial output Mesh alive");
            }
            finally
            {
                Destroy(source, gameObject);
            }
        }

        [Test]
        public void RemappingATexturedNonTriangleSubmeshFailsClosed()
        {
            var gameObject = new GameObject("mesh-remap-topology-fallback");
            var source = NewPointMesh();
            try
            {
                var renderer = gameObject.AddComponent<MeshRenderer>();
                var setup = BuildSingleGroupPlan(renderer, source, 0);
                Assert.Throws<InvalidOperationException>(() =>
                    new MeshAtlasRemapper(null).Build(setup.Analysis, setup.Plan));
            }
            finally
            {
                Destroy(source, gameObject);
            }
        }

        private static (AvatarAnalysis Analysis, AtlasPlan Plan) BuildSingleGroupPlan(
            Renderer renderer, Mesh mesh, int slotIndex)
        {
            var analysis = NewAnalysis(renderer, mesh, slotIndex, out var group);
            var island = NewIsland(0, 0);
            group.Islands.Add(island);

            var page = new AtlasPage { Id = 0, Size = new Vector2Int(64, 64) };
            page.Groups.Add(group);
            page.Placements.Add(new AtlasPlacement
            {
                Group = group, Island = island, ContentRect = new RectInt(8, 8, 32, 32)
            });
            var plan = new AtlasPlan();
            plan.Pages.Add(page);
            return (analysis, plan);
        }

        private static (AvatarAnalysis Analysis, AtlasPlan Plan) BuildTwoIslandPlan(Renderer renderer, Mesh mesh)
        {
            var analysis = NewAnalysis(renderer, mesh, 0, out var group);
            var first = NewIsland(0, 0);
            var second = NewIsland(1, 1);
            group.Islands.Add(first);
            group.Islands.Add(second);

            var page = new AtlasPage { Id = 0, Size = new Vector2Int(64, 64) };
            page.Groups.Add(group);
            page.Placements.Add(new AtlasPlacement
            {
                Group = group, Island = first, ContentRect = new RectInt(4, 4, 24, 24)
            });
            page.Placements.Add(new AtlasPlacement
            {
                Group = group, Island = second, ContentRect = new RectInt(36, 36, 24, 24)
            });
            var plan = new AtlasPlan();
            plan.Pages.Add(page);
            return (analysis, plan);
        }

        private static AvatarAnalysis NewAnalysis(Renderer renderer, Mesh mesh, int slotIndex,
            out UvGroupRecord group)
        {
            var analysis = new AvatarAnalysis();
            var record = new RendererRecord { Renderer = renderer, Mesh = mesh };
            var slot = new MaterialSlotRecord { Slot = slotIndex };
            record.Slots.Add(slot);
            analysis.Renderers.Add(record);
            group = new UvGroupRecord
            {
                Id = 0, Renderer = record, Slot = slot, UvChannel = 0, AtlasSafe = true
            };
            analysis.UvGroups.Add(group);
            return analysis;
        }

        private static UvIsland NewIsland(int islandId, int triangleIndex)
        {
            var island = new UvIsland
            {
                Id = islandId, UvGroupId = 0, UvBounds = new Rect(0f, 0f, 1f, 1f),
                IntegerNormalization = Vector2.zero
            };
            island.TriangleIndices.Add(triangleIndex);
            return island;
        }

        private static Mesh NewBaseVertexMesh()
        {
            var mesh = new Mesh { name = "base-vertex-source", indexFormat = IndexFormat.UInt16 };
            mesh.vertices = new[]
            {
                new Vector3(0f, 0f, 0f), new Vector3(1f, 0f, 0f), new Vector3(0f, 1f, 0f),
                new Vector3(2f, 0f, 0f), new Vector3(3f, 0f, 0f), new Vector3(2f, 1f, 0f)
            };
            mesh.uv = new[]
            {
                new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0f, 1f),
                new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0f, 1f)
            };
            mesh.subMeshCount = 3;
            mesh.SetIndices(new[] { 0 }, MeshTopology.Points, 0, false, 0);
            mesh.SetIndices(new[] { 0, 1, 2 }, MeshTopology.Triangles, 1, false, 3);
            mesh.SetIndices(Array.Empty<int>(), MeshTopology.Lines, 2, false, 0);
            return mesh;
        }

        private static Mesh NewSplitMesh()
        {
            var mesh = new Mesh { name = "split-source" };
            mesh.vertices = new[] { Vector3.zero, Vector3.right, Vector3.one, Vector3.up };
            mesh.uv = new[]
            {
                new Vector2(0.1f, 0.2f), new Vector2(0.8f, 0.2f),
                new Vector2(0.8f, 0.9f), new Vector2(0.1f, 0.9f)
            };
            mesh.triangles = new[] { 0, 1, 2, 0, 2, 3 };
            return mesh;
        }

        private static Mesh NewRichSplitMesh()
        {
            var mesh = NewSplitMesh();
            mesh.name = "rich-split-source";
            mesh.SetUVs(0, new List<Vector4>
            {
                new Vector4(0.1f, 0.2f, 10f, 20f), new Vector4(0.8f, 0.2f, 11f, 21f),
                new Vector4(0.8f, 0.9f, 12f, 22f), new Vector4(0.1f, 0.9f, 13f, 23f)
            });
            mesh.SetUVs(1, new List<Vector3>
            {
                new Vector3(0.2f, 0.3f, 30f), new Vector3(0.4f, 0.5f, 31f),
                new Vector3(0.6f, 0.7f, 32f), new Vector3(0.8f, 0.9f, 33f)
            });
            mesh.colors32 = new[]
            {
                new Color32(1, 2, 3, 4), new Color32(5, 6, 7, 8),
                new Color32(9, 10, 11, 12), new Color32(13, 14, 15, 16)
            };
            mesh.bindposes = new[]
            {
                Matrix4x4.identity, Matrix4x4.Translate(Vector3.right),
                Matrix4x4.Translate(Vector3.up), Matrix4x4.Translate(Vector3.forward)
            };

            var counts = new NativeArray<byte>(new byte[] { 1, 2, 1, 1 }, Allocator.Temp);
            var weights = new NativeArray<BoneWeight1>(new[]
            {
                Weight(0, 1f), Weight(1, 0.25f), Weight(2, 0.75f), Weight(2, 1f), Weight(3, 1f)
            }, Allocator.Temp);
            try
            {
                mesh.SetBoneWeights(counts, weights);
            }
            finally
            {
                counts.Dispose();
                weights.Dispose();
            }

            var deltaVertices = new Vector3[4];
            var deltaNormals = new Vector3[4];
            var deltaTangents = new Vector3[4];
            for (var i = 0; i < 4; i++)
            {
                deltaVertices[i] = new Vector3(i + 1f, i + 2f, i + 3f);
                deltaNormals[i] = new Vector3(i + 4f, i + 5f, i + 6f);
                deltaTangents[i] = new Vector3(i + 7f, i + 8f, i + 9f);
            }
            mesh.AddBlendShapeFrame("Smile", 100f, deltaVertices, deltaNormals, deltaTangents);
            return mesh;
        }

        private static Mesh NewFloat16SecondaryUvMesh()
        {
            var mesh = new Mesh { name = "half-secondary-uv-source" };
            mesh.SetVertexBufferParams(4,
                new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 3, 0),
                new VertexAttributeDescriptor(VertexAttribute.Color, VertexAttributeFormat.UNorm8, 4, 1),
                new VertexAttributeDescriptor(VertexAttribute.TexCoord0, VertexAttributeFormat.Float32, 4, 2),
                new VertexAttributeDescriptor(VertexAttribute.TexCoord1, VertexAttributeFormat.Float16, 2, 3));
            var flags = MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices;
            mesh.SetVertexBufferData(new[]
            {
                Vector3.zero, Vector3.right, Vector3.one, Vector3.up
            }, 0, 0, 4, 0, flags);
            mesh.SetVertexBufferData(new[]
            {
                new Color32(17, 18, 19, 20), new Color32(21, 22, 23, 24),
                new Color32(25, 26, 27, 28), new Color32(29, 30, 31, 32)
            }, 0, 0, 4, 1, flags);
            mesh.SetVertexBufferData(new[]
            {
                new Vector4(0.1f, 0.2f, 10f, 20f), new Vector4(0.8f, 0.2f, 11f, 21f),
                new Vector4(0.8f, 0.9f, 12f, 22f), new Vector4(0.1f, 0.9f, 13f, 23f)
            }, 0, 0, 4, 2, flags);
            mesh.SetVertexBufferData(new[]
            {
                new HalfUv(0x0000, 0x3400), new HalfUv(0x3800, 0x3a00),
                new HalfUv(0x3c00, 0x3800), new HalfUv(0x3400, 0x3c00)
            }, 0, 0, 4, 3, flags);
            mesh.SetIndices(new[] { 0, 1, 2, 0, 2, 3 }, MeshTopology.Triangles, 0, false);
            return mesh;
        }

        private static Mesh NewOneComponentUvMesh()
        {
            var mesh = new Mesh { name = "invalid-one-component-uv-source" };
            mesh.SetVertexBufferParams(3,
                new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 3),
                new VertexAttributeDescriptor(VertexAttribute.TexCoord0, VertexAttributeFormat.Float32, 1));
            mesh.SetVertexBufferData(new[]
            {
                new PositionUv1(Vector3.zero, 0f), new PositionUv1(Vector3.right, 0.5f),
                new PositionUv1(Vector3.up, 1f)
            }, 0, 0, 3, 0, MeshUpdateFlags.DontRecalculateBounds);
            mesh.SetIndices(new[] { 0, 1, 2 }, MeshTopology.Triangles, 0, false);
            return mesh;
        }

        private static Mesh NewPointMesh()
        {
            var mesh = new Mesh { name = "point-source" };
            mesh.vertices = new[] { Vector3.zero };
            mesh.uv = new[] { Vector2.zero };
            mesh.SetIndices(new[] { 0 }, MeshTopology.Points, 0);
            return mesh;
        }

        private static BoneWeight1 Weight(int boneIndex, float weight)
        {
            return new BoneWeight1 { boneIndex = boneIndex, weight = weight };
        }

        private static void AssertMappedVertices(IReadOnlyList<Vector3> source, IReadOnlyList<Vector3> generated)
        {
            for (var i = 0; i < SplitSourceVertices.Length; i++)
                Assert.That(generated[i], Is.EqualTo(source[SplitSourceVertices[i]]));
        }

        private static void AssertMappedUvTailsAndUntouchedUvChannel(Mesh source, Mesh generated)
        {
            Assert.That(generated.GetVertexAttributeDimension(VertexAttribute.TexCoord0), Is.EqualTo(4));
            Assert.That(generated.GetVertexAttributeDimension(VertexAttribute.TexCoord1), Is.EqualTo(3));
            var sourceUv0 = new List<Vector4>();
            var generatedUv0 = new List<Vector4>();
            var sourceUv1 = new List<Vector4>();
            var generatedUv1 = new List<Vector4>();
            source.GetUVs(0, sourceUv0);
            generated.GetUVs(0, generatedUv0);
            source.GetUVs(1, sourceUv1);
            generated.GetUVs(1, generatedUv1);
            for (var i = 0; i < SplitSourceVertices.Length; i++)
            {
                var sourceVertex = SplitSourceVertices[i];
                Assert.That(generatedUv0[i].z, Is.EqualTo(sourceUv0[sourceVertex].z));
                Assert.That(generatedUv0[i].w, Is.EqualTo(sourceUv0[sourceVertex].w));
                Assert.That(generatedUv1[i], Is.EqualTo(sourceUv1[sourceVertex]));
            }
        }

        private static void AssertMappedColors(Mesh source, Mesh generated)
        {
            Assert.That(generated.GetVertexAttributeFormat(VertexAttribute.Color),
                Is.EqualTo(VertexAttributeFormat.UNorm8));
            for (var i = 0; i < SplitSourceVertices.Length; i++)
                Assert.That(generated.colors32[i], Is.EqualTo(source.colors32[SplitSourceVertices[i]]));
        }

        private static void AssertMappedBlendShape(Mesh source, Mesh generated)
        {
            Assert.That(generated.blendShapeCount, Is.EqualTo(1));
            Assert.That(generated.GetBlendShapeName(0), Is.EqualTo("Smile"));
            Assert.That(generated.GetBlendShapeFrameCount(0), Is.EqualTo(1));
            Assert.That(generated.GetBlendShapeFrameWeight(0, 0), Is.EqualTo(100f));
            var sourceVertices = new Vector3[source.vertexCount];
            var sourceNormals = new Vector3[source.vertexCount];
            var sourceTangents = new Vector3[source.vertexCount];
            var generatedVertices = new Vector3[generated.vertexCount];
            var generatedNormals = new Vector3[generated.vertexCount];
            var generatedTangents = new Vector3[generated.vertexCount];
            source.GetBlendShapeFrameVertices(0, 0, sourceVertices, sourceNormals, sourceTangents);
            generated.GetBlendShapeFrameVertices(0, 0, generatedVertices, generatedNormals, generatedTangents);
            for (var i = 0; i < SplitSourceVertices.Length; i++)
            {
                var sourceVertex = SplitSourceVertices[i];
                Assert.That(generatedVertices[i], Is.EqualTo(sourceVertices[sourceVertex]));
                Assert.That(generatedNormals[i], Is.EqualTo(sourceNormals[sourceVertex]));
                Assert.That(generatedTangents[i], Is.EqualTo(sourceTangents[sourceVertex]));
            }
        }

        private static void AssertMappedSkinWeights(Mesh source, Mesh generated)
        {
            var sourceCounts = source.GetBonesPerVertex();
            var sourceWeights = source.GetAllBoneWeights();
            var generatedCounts = generated.GetBonesPerVertex();
            var generatedWeights = generated.GetAllBoneWeights();
            try
            {
                var sourceOffsets = new int[source.vertexCount + 1];
                for (var i = 0; i < source.vertexCount; i++)
                    sourceOffsets[i + 1] = sourceOffsets[i] + sourceCounts[i];
                var generatedWeight = 0;
                for (var i = 0; i < SplitSourceVertices.Length; i++)
                {
                    var sourceVertex = SplitSourceVertices[i];
                    Assert.That(generatedCounts[i], Is.EqualTo(sourceCounts[sourceVertex]));
                    for (var j = 0; j < sourceCounts[sourceVertex]; j++, generatedWeight++)
                    {
                        var expected = sourceWeights[sourceOffsets[sourceVertex] + j];
                        var actual = generatedWeights[generatedWeight];
                        Assert.That(actual.boneIndex, Is.EqualTo(expected.boneIndex));
                        Assert.That(actual.weight, Is.EqualTo(expected.weight));
                    }
                }
                Assert.That(generatedWeight, Is.EqualTo(generatedWeights.Length));
            }
            finally
            {
                sourceCounts.Dispose();
                sourceWeights.Dispose();
                generatedCounts.Dispose();
                generatedWeights.Dispose();
            }
        }

        private static int[] AllMeshInstanceIds()
        {
            return Resources.FindObjectsOfTypeAll<Mesh>().Select(mesh => mesh.GetInstanceID()).OrderBy(id => id).ToArray();
        }

        private static void Destroy(params UnityEngine.Object[] objects)
        {
            foreach (var value in objects)
                if (value != null) UnityEngine.Object.DestroyImmediate(value);
        }

        private static bool IsUvUsedByAao(SkinnedMeshRenderer renderer, int channel) => channel == 0;
        private static void RegisterAaoEvacuation(SkinnedMeshRenderer renderer, int original, int saved) { }
    }
}
