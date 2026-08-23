using System.Collections.Generic;
using System.Linq;
using Fosa.AvatarTextureOptimizer.Editor.Analysis;
using Fosa.AvatarTextureOptimizer.Editor.Atlas;
using Fosa.AvatarTextureOptimizer.Editor.Pipeline;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;

namespace Fosa.AvatarTextureOptimizer.Tests
{
    [Parallelizable(ParallelScope.None)]
    public sealed class UvAnalysisStageTests
    {
        [TearDown]
        public void TearDown() => ATOProgress.End();

        [Test]
        public void AtlasRejectsEveryUvGroupOnRendererWithMaterialPropertyBlockOnce()
        {
            var blockedObject = new GameObject("property-blocked-renderer");
            var unaffectedObject = new GameObject("unaffected-renderer");
            var unaffectedMesh = new Mesh
            {
                vertices = new[] { Vector3.zero, Vector3.right, Vector3.up },
                triangles = new[] { 0, 1, 2 }
            };
            unaffectedMesh.SetUVs(0, new List<Vector2> { Vector2.zero, Vector2.right, Vector2.up });
            var unaffectedTexture = new Texture2D(4, 4, TextureFormat.RGBA32, false, true)
            {
                wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear
            };
            try
            {
                var blockedRenderer = blockedObject.AddComponent<MeshRenderer>();
                var unaffectedRenderer = unaffectedObject.AddComponent<MeshRenderer>();
                var block = new MaterialPropertyBlock();
                block.SetFloat("_UnmodeledUvSelector", 1f);
                blockedRenderer.SetPropertyBlock(block);
                Assert.That(blockedRenderer.HasPropertyBlock(), Is.True,
                    "the regression requires Unity to attach the test property block");

                var blockedRecord = new RendererRecord { Renderer = blockedRenderer };
                var unaffectedRecord = new RendererRecord { Renderer = unaffectedRenderer, Mesh = unaffectedMesh };
                var first = new UvGroupRecord { Renderer = blockedRecord, AtlasSafe = true };
                var second = new UvGroupRecord
                {
                    Renderer = new RendererRecord { Renderer = blockedRenderer }, AtlasSafe = true
                };
                var unaffected = new UvGroupRecord
                {
                    Renderer = unaffectedRecord, Slot = new MaterialSlotRecord { Slot = 0 },
                    UvChannel = 0, AtlasSafe = true
                };
                unaffected.Bindings.Add(new TextureBindingRecord
                {
                    Renderer = unaffectedRecord, Slot = unaffected.Slot, Texture = unaffectedTexture,
                    OriginalTexture = unaffectedTexture, UvChannel = 0, AtlasSafe = true
                });
                first.Islands.Add(new UvIsland()); second.Islands.Add(new UvIsland());
                var analysis = new AvatarAnalysis();
                analysis.UvGroups.Add(first); analysis.UvGroups.Add(second); analysis.UvGroups.Add(unaffected);

                UvAnalysisStage.Execute(analysis, true);

                Assert.That(first.AtlasSafe, Is.False);
                Assert.That(second.AtlasSafe, Is.False);
                Assert.That(first.Islands, Is.Empty); Assert.That(second.Islands, Is.Empty);
                Assert.That(unaffected.AtlasSafe, Is.True);
                Assert.That(analysis.Fallbacks, Has.Count.EqualTo(1),
                    "one Renderer-level reason must not be duplicated for every slot/channel group");
                Assert.That(analysis.Fallbacks[0].Subject, Is.SameAs(blockedRenderer));
                Assert.That(analysis.Fallbacks[0].Reason, Does.Contain("MaterialPropertyBlock"));
            }
            finally
            {
                Object.DestroyImmediate(unaffectedTexture);
                Object.DestroyImmediate(unaffectedMesh);
                Object.DestroyImmediate(blockedObject);
                Object.DestroyImmediate(unaffectedObject);
            }
        }

        [Test]
        public void WholeModeDoesNotRejectRendererOnlyBecauseItHasPropertyBlock()
        {
            var gameObject = new GameObject("whole-property-block-renderer");
            try
            {
                var renderer = gameObject.AddComponent<MeshRenderer>();
                var block = new MaterialPropertyBlock(); block.SetFloat("_NonTextureValue", 2f);
                renderer.SetPropertyBlock(block);
                var group = new UvGroupRecord
                {
                    Renderer = new RendererRecord { Renderer = renderer }, AtlasSafe = true
                };
                var analysis = new AvatarAnalysis(); analysis.UvGroups.Add(group);

                UvAnalysisStage.RejectAtlasRenderersWithPropertyBlocks(analysis, false);

                Assert.That(group.AtlasSafe, Is.True);
                Assert.That(analysis.Fallbacks, Is.Empty);
            }
            finally
            {
                Object.DestroyImmediate(gameObject);
            }
        }

        [TestCase(TextureWrapMode.Repeat, false)]
        [TestCase(TextureWrapMode.Mirror, false)]
        [TestCase(TextureWrapMode.MirrorOnce, false)]
        [TestCase(TextureWrapMode.Repeat, true)]
        [TestCase(TextureWrapMode.Mirror, true)]
        [TestCase(TextureWrapMode.MirrorOnce, true)]
        public void AnyNonClampAnimatedCandidateRejectsWholeUvGroup(TextureWrapMode wrap, bool requireWritableUv)
        {
            var gameObject = new GameObject("uv-analysis-test");
            var current = NewTexture(TextureWrapMode.Clamp);
            var animated = NewTexture(wrap);
            try
            {
                var renderer = gameObject.AddComponent<MeshRenderer>();
                var rendererRecord = new RendererRecord { Renderer = renderer };
                var slot = new MaterialSlotRecord { Slot = 0 };
                var group = new UvGroupRecord
                {
                    AtlasSafe = true,
                    Renderer = rendererRecord,
                    Slot = slot,
                    UvChannel = 0
                };
                group.Bindings.Add(new TextureBindingRecord { Texture = current, AtlasSafe = true, IsInitialValue = true });
                group.Bindings.Add(new TextureBindingRecord { Texture = animated, AtlasSafe = true, IsAnimatedValue = true });
                var analysis = new AvatarAnalysis();
                analysis.UvGroups.Add(group);

                UvAnalysisStage.Execute(analysis, requireWritableUv);

                Assert.IsFalse(group.AtlasSafe);
                Assert.That(group.Islands, Is.Empty);
                Assert.That(analysis.Fallbacks, Has.Count.EqualTo(1));
            }
            finally
            {
                Object.DestroyImmediate(current);
                Object.DestroyImmediate(animated);
                Object.DestroyImmediate(gameObject);
            }
        }

        [Test]
        public void ClampUvOutsideZeroOneKeepsOriginalSourceDomainAndIsNormalizedByAtlasMapping()
        {
            var gameObject = new GameObject("clamp-out-of-range");
            var texture = NewTexture(TextureWrapMode.Clamp);
            var mesh = new Mesh { name = "clamp-out-of-range-mesh" };
            try
            {
                mesh.vertices = new[] { Vector3.zero, Vector3.right, Vector3.up };
                mesh.uv = new[]
                {
                    new Vector2(1.25f, -0.75f), new Vector2(1.75f, -0.75f),
                    new Vector2(1.25f, -0.25f)
                };
                mesh.triangles = new[] { 0, 1, 2 };
                var renderer = gameObject.AddComponent<MeshRenderer>();
                var rendererRecord = new RendererRecord { Renderer = renderer, Mesh = mesh };
                var slot = new MaterialSlotRecord { Slot = 0 };
                var group = new UvGroupRecord
                {
                    AtlasSafe = true, Renderer = rendererRecord, Slot = slot, UvChannel = 0
                };
                group.Bindings.Add(new TextureBindingRecord { Texture = texture, AtlasSafe = true });
                var analysis = new AvatarAnalysis(); analysis.UvGroups.Add(group);

                UvAnalysisStage.Execute(analysis, true);

                Assert.That(group.AtlasSafe, Is.True);
                Assert.That(group.Islands, Has.Count.EqualTo(1));
                Assert.That(group.Islands[0].IntegerNormalization, Is.EqualTo(Vector2.zero),
                    "Clamp coordinates must not be shifted to a different source texture region");
                Assert.That(group.Islands[0].UvBounds.xMin, Is.EqualTo(1.25f));
                Assert.That(group.Islands[0].UvBounds.yMin, Is.EqualTo(-0.75f));
                Assert.That(analysis.Fallbacks, Is.Empty);
            }
            finally
            {
                Object.DestroyImmediate(mesh);
                Object.DestroyImmediate(texture);
                Object.DestroyImmediate(gameObject);
            }
        }

        [TestCase(VertexAttributeFormat.Float16)]
        [TestCase(VertexAttributeFormat.UNorm8)]
        [TestCase(VertexAttributeFormat.SNorm8)]
        [TestCase(VertexAttributeFormat.UNorm16)]
        [TestCase(VertexAttributeFormat.SNorm16)]
        public void AtlasRewriteRejectsLowPrecisionTargetUvBeforeIslandExtraction(VertexAttributeFormat format)
        {
            AssertUvLayoutRejected(format, 4,
                "atlas remapping requires Float32 UVs; low-precision UV quantization is not covered by the final quality proof: " + format);
        }

        [Test]
        public void AtlasRewriteRejectsOneComponentTargetUvBeforeIslandExtraction()
        {
            AssertUvLayoutRejected(VertexAttributeFormat.Float32, 1,
                "required UV vertex attribute is missing or has fewer than two components");
        }

        [Test]
        public void RepeatNormalizationRejectsAnIslandTouchingTheNextTileSeam()
        {
            Assert.That(UvIslandExtractor.NormalizingShift(-0.8f, -0.2f, TextureWrapMode.Repeat),
                Is.EqualTo(1f));
            Assert.That(float.IsNaN(UvIslandExtractor.NormalizingShift(0.2f, 1f,
                TextureWrapMode.Repeat)), Is.True);
            Assert.That(float.IsNaN(UvIslandExtractor.NormalizingShift(0.2f, 0.8f,
                TextureWrapMode.Mirror)), Is.True);
            Assert.That(UvIslandExtractor.NormalizingShift(-4f, 9f, TextureWrapMode.Clamp), Is.Zero,
                "Clamp out-of-range domains are preserved instead of periodically shifted");
        }

        [Test]
        public void PointFilteredMipChainIsRejectedBeforeIslandExtraction()
        {
            AssertEarlyMipFallback(true, FilterMode.Point, true,
                "point-filtered source mip chains cannot yet be preserved safely");
        }

        [Test]
        public void MipPresenceMismatchIsRejectedBeforeIslandExtraction()
        {
            AssertEarlyMipFallback(false, FilterMode.Bilinear, true,
                "configured mip-map presence differs from the source and cannot preserve derivative-dependent sampling");
        }

        [Test]
        public void AaoEvacuationReturnsFailureInsteadOfAmbiguousChannelZero()
        {
            var unavailable = new HashSet<int> { 1 };
            Assert.That(AAOUvCompatibilityBridge.FindEvacuationChannel(unavailable, _ => true), Is.EqualTo(-1));
        }

        [Test]
        public void AaoEvacuationPreservesChannelZeroAsAValidSelection()
        {
            Assert.That(AAOUvCompatibilityBridge.FindEvacuationChannel(new HashSet<int>(), _ => false),
                Is.EqualTo(0));
        }

        [Test]
        public void AaoEvacuationSelectsFirstAbsentAndUnusedChannel()
        {
            var unavailable = new HashSet<int> { 0, 2 };
            Assert.That(AAOUvCompatibilityBridge.FindEvacuationChannel(unavailable, channel => channel == 1),
                Is.EqualTo(3));
        }

        private static void AssertUvLayoutRejected(VertexAttributeFormat format, int dimension,
            string expectedReason)
        {
            var gameObject = new GameObject("uv-layout-analysis-test");
            var texture = NewTexture(TextureWrapMode.Clamp);
            var mesh = new Mesh { name = "uv-layout-source" };
            try
            {
                mesh.SetVertexBufferParams(3,
                    new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 3, 0),
                    new VertexAttributeDescriptor(VertexAttribute.TexCoord0, format, dimension, 1));
                mesh.SetIndices(new[] { 0, 1, 2 }, MeshTopology.Triangles, 0, false);
                var renderer = gameObject.AddComponent<MeshRenderer>();
                var group = new UvGroupRecord
                {
                    AtlasSafe = true,
                    Renderer = new RendererRecord { Renderer = renderer, Mesh = mesh },
                    Slot = new MaterialSlotRecord { Slot = 0 },
                    UvChannel = 0
                };
                group.Bindings.Add(new TextureBindingRecord { Texture = texture, AtlasSafe = true });
                var analysis = new AvatarAnalysis();
                analysis.UvGroups.Add(group);

                UvAnalysisStage.Execute(analysis, true);

                Assert.That(group.AtlasSafe, Is.False);
                Assert.That(group.Islands, Is.Empty);
                Assert.That(analysis.Fallbacks, Has.Count.EqualTo(1));
                Assert.That(analysis.Fallbacks[0].Reason, Is.EqualTo(expectedReason));
            }
            finally
            {
                Object.DestroyImmediate(mesh);
                Object.DestroyImmediate(texture);
                Object.DestroyImmediate(gameObject);
            }
        }

        [Test]
        public void DegenerateUvBoundsFailClosedBeforeRasterization()
        {
            var gameObject = new GameObject("degenerate-uv-test");
            var texture = NewTexture(TextureWrapMode.Clamp);
            var mesh = new Mesh { vertices = new[] { Vector3.zero, Vector3.right, Vector3.up } };
            mesh.SetUVs(0, new List<Vector2>
            {
                new Vector2(0f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(1f, 0.5f)
            });
            mesh.SetTriangles(new[] { 0, 1, 2 }, 0);
            try
            {
                var renderer = gameObject.AddComponent<MeshRenderer>();
                var group = new UvGroupRecord
                {
                    Renderer = new RendererRecord { Renderer = renderer, Mesh = mesh },
                    Slot = new MaterialSlotRecord { Slot = 0 }, UvChannel = 0
                };
                group.Bindings.Add(new TextureBindingRecord { Texture = texture, AtlasSafe = true });

                Assert.IsFalse(new UvIslandExtractor().Extract(group, out var failure));
                Assert.That(failure, Is.EqualTo("UV island has a zero-width or zero-height bound"));
                Assert.That(group.Islands, Is.Empty);
            }
            finally
            {
                Object.DestroyImmediate(mesh); Object.DestroyImmediate(texture);
                Object.DestroyImmediate(gameObject);
            }
        }

        [Test]
        public void HugeFiniteClampFootprintFailsClosedWithoutIntegerOverflow()
        {
            var gameObject = new GameObject("huge-uv-test");
            var texture = NewTexture(TextureWrapMode.Clamp);
            var mesh = new Mesh { vertices = new[] { Vector3.zero, Vector3.right, Vector3.up } };
            mesh.SetUVs(0, new List<Vector2>
            {
                new Vector2(-float.MaxValue, -1f), new Vector2(float.MaxValue, -1f),
                new Vector2(-float.MaxValue, 1f)
            });
            mesh.SetTriangles(new[] { 0, 1, 2 }, 0);
            try
            {
                var renderer = gameObject.AddComponent<MeshRenderer>();
                var group = new UvGroupRecord
                {
                    Renderer = new RendererRecord { Renderer = renderer, Mesh = mesh },
                    Slot = new MaterialSlotRecord { Slot = 0 }, UvChannel = 0
                };
                group.Bindings.Add(new TextureBindingRecord { Texture = texture, AtlasSafe = true });

                Assert.IsFalse(new UvIslandExtractor().Extract(group, out var failure));
                Assert.That(failure, Is.EqualTo(
                    "UV sampling footprint is non-finite or exceeds supported integer dimensions"));
                Assert.That(group.Islands, Is.Empty);
            }
            finally
            {
                Object.DestroyImmediate(mesh); Object.DestroyImmediate(texture);
                Object.DestroyImmediate(gameObject);
            }
        }

        [Test]
        public void SurfaceMetricIncludesSimultaneousBlendShapesAndAnimatedAreaScale()
        {
            var gameObject = new GameObject("blend-shape-area-test");
            var texture = NewTexture(TextureWrapMode.Clamp);
            var mesh = new Mesh
            {
                vertices = new[] { Vector3.zero, Vector3.right, Vector3.up },
                triangles = new[] { 0, 1, 2 }
            };
            mesh.SetUVs(0, new List<Vector2> { Vector2.zero, Vector2.right, Vector2.up });
            var zeros = new Vector3[3];
            var wide = new Vector3[3]; wide[1] = Vector3.right;
            var tall = new Vector3[3]; tall[2] = Vector3.up;
            mesh.AddBlendShapeFrame("Wide", 100f, wide, zeros, zeros);
            mesh.AddBlendShapeFrame("Tall", 100f, tall, zeros, zeros);
            try
            {
                var renderer = gameObject.AddComponent<SkinnedMeshRenderer>();
                renderer.sharedMesh = mesh;
                var group = new UvGroupRecord
                {
                    Renderer = new RendererRecord
                        { Renderer = renderer, Mesh = mesh, MaximumAreaScale = 4f },
                    Slot = new MaterialSlotRecord { Slot = 0 }, UvChannel = 0
                };
                group.Bindings.Add(new TextureBindingRecord { Texture = texture, AtlasSafe = true });

                Assert.That(new UvIslandExtractor().Extract(group, out var failure), Is.True, failure);

                Assert.That(group.Islands, Has.Count.EqualTo(1));
                Assert.That(group.Islands[0].SurfaceAreaSquareMeters, Is.EqualTo(8f).Within(1e-5f),
                    "the conservative simultaneous-shape area 2 must include maximum animated area scale 4");
                Assert.That(group.Islands[0].OriginalPixelBounds, Is.EqualTo(new Vector2Int(2, 2)));
                Assert.That(group.Islands[0].TargetPixelSize, Is.EqualTo(new Vector2Int(2, 2)));
            }
            finally
            {
                Object.DestroyImmediate(mesh); Object.DestroyImmediate(texture);
                Object.DestroyImmediate(gameObject);
            }
        }

        [Test]
        public void SurfaceMetricIncludesIntermediateBlendShapeFrameEnvelope()
        {
            var gameObject = new GameObject("blend-shape-intermediate-area-test");
            var texture = NewTexture(TextureWrapMode.Clamp);
            var mesh = new Mesh
            {
                vertices = new[] { Vector3.zero, Vector3.right, Vector3.up },
                triangles = new[] { 0, 1, 2 }
            };
            mesh.SetUVs(0, new List<Vector2> { Vector2.zero, Vector2.right, Vector2.up });
            var zeros = new Vector3[3]; var expanded = new Vector3[3]; expanded[1] = Vector3.right * 9f;
            mesh.AddBlendShapeFrame("Pulse", 50f, expanded, zeros, zeros);
            mesh.AddBlendShapeFrame("Pulse", 100f, zeros, zeros, zeros);
            try
            {
                var renderer = gameObject.AddComponent<SkinnedMeshRenderer>(); renderer.sharedMesh = mesh;
                var group = new UvGroupRecord
                {
                    Renderer = new RendererRecord { Renderer = renderer, Mesh = mesh, MaximumAreaScale = 1f },
                    Slot = new MaterialSlotRecord { Slot = 0 }, UvChannel = 0
                };
                group.Bindings.Add(new TextureBindingRecord { Texture = texture, AtlasSafe = true });

                Assert.That(new UvIslandExtractor().Extract(group, out var failure), Is.True, failure);
                Assert.That(group.Islands[0].SurfaceAreaSquareMeters, Is.EqualTo(5f).Within(1e-5f),
                    "the 50% frame is larger than both 0% and 100% and must remain in the interval envelope");
            }
            finally
            {
                Object.DestroyImmediate(mesh); Object.DestroyImmediate(texture);
                Object.DestroyImmediate(gameObject);
            }
        }

        [Test]
        public void MultipleIslandsUseOnlyTheirOwnPrecomputedTriangleBounds()
        {
            var gameObject = new GameObject("blend-shape-multiple-island-area-test");
            var texture = NewTexture(TextureWrapMode.Clamp);
            var mesh = new Mesh
            {
                vertices = new[]
                {
                    Vector3.zero, Vector3.right, Vector3.up,
                    new Vector3(20f, 0f), new Vector3(21f, 0f), new Vector3(20f, 1f)
                },
                triangles = new[] { 0, 1, 2, 3, 4, 5 }
            };
            mesh.SetUVs(0, new List<Vector2>
            {
                new Vector2(.05f, .05f), new Vector2(.25f, .05f), new Vector2(.05f, .25f),
                new Vector2(.65f, .65f), new Vector2(.85f, .65f), new Vector2(.65f, .85f)
            });
            var frame50 = new Vector3[6]; frame50[1] = new Vector3(9f, 0f, 0f);
            var zeros = new Vector3[6];
            mesh.AddBlendShapeFrame("FirstIslandOnly", 50f, frame50, zeros, zeros);
            mesh.AddBlendShapeFrame("FirstIslandOnly", 100f, zeros, zeros, zeros);
            try
            {
                var renderer = gameObject.AddComponent<SkinnedMeshRenderer>(); renderer.sharedMesh = mesh;
                var group = new UvGroupRecord
                {
                    Renderer = new RendererRecord { Renderer = renderer, Mesh = mesh, MaximumAreaScale = 1f },
                    Slot = new MaterialSlotRecord { Slot = 0 }, UvChannel = 0
                };
                group.Bindings.Add(new TextureBindingRecord { Texture = texture, AtlasSafe = true });

                Assert.That(new UvIslandExtractor().Extract(group, out var failure), Is.True, failure);
                Assert.That(group.Islands.Count, Is.EqualTo(2));
                var areas = group.Islands.Select(value => value.SurfaceAreaSquareMeters)
                    .OrderBy(value => value).ToArray();
                Assert.That(areas[0], Is.EqualTo(.5f).Within(1e-5f),
                    "an unaffected island must not inherit another triangle's envelope");
                Assert.That(areas[1], Is.EqualTo(5f).Within(1e-5f),
                    "the affected island must receive its own precomputed triangle bound");
            }
            finally
            {
                Object.DestroyImmediate(mesh); Object.DestroyImmediate(texture);
                Object.DestroyImmediate(gameObject);
            }
        }

        [Test]
        public void ZeroWeightOnlyBlendShapeFrameFailsClosedInsteadOfGuessingExtrapolation()
        {
            var gameObject = new GameObject("blend-shape-zero-frame-test");
            var texture = NewTexture(TextureWrapMode.Clamp);
            var mesh = new Mesh
            {
                vertices = new[] { Vector3.zero, Vector3.right, Vector3.up },
                triangles = new[] { 0, 1, 2 }
            };
            mesh.SetUVs(0, new List<Vector2> { Vector2.zero, Vector2.right, Vector2.up });
            var zeros = new Vector3[3]; var delta = new Vector3[3]; delta[1] = Vector3.right;
            mesh.AddBlendShapeFrame("Ambiguous", 0f, delta, zeros, zeros);
            try
            {
                var renderer = gameObject.AddComponent<SkinnedMeshRenderer>(); renderer.sharedMesh = mesh;
                var group = new UvGroupRecord
                {
                    Renderer = new RendererRecord { Renderer = renderer, Mesh = mesh, MaximumAreaScale = 1f },
                    Slot = new MaterialSlotRecord { Slot = 0 }, UvChannel = 0
                };
                group.Bindings.Add(new TextureBindingRecord { Texture = texture, AtlasSafe = true });

                Assert.That(new UvIslandExtractor().Extract(group, out var failure), Is.False);
                Assert.That(failure, Is.EqualTo(
                    "blend-shape frames cannot establish a finite 0..100 surface-area bound"));
            }
            finally
            {
                Object.DestroyImmediate(mesh); Object.DestroyImmediate(texture);
                Object.DestroyImmediate(gameObject);
            }
        }

        [Test]
        public void BlendShapeAtHundredCoversExactInterpolationAndExtrapolationBranches()
        {
            var mesh = new Mesh { vertices = new[] { Vector3.zero, Vector3.right, Vector3.up } };
            var zeros = new Vector3[3];
            try
            {
                AddShapeFrame(mesh, "Exact", 100f, 1f, zeros);
                AddShapeFrame(mesh, "SingleBelow", 50f, 0.5f, zeros);
                AddShapeFrame(mesh, "TwoBelow", 50f, 0.5f, zeros);
                AddShapeFrame(mesh, "TwoBelow", 80f, 0.8f, zeros);
                AddShapeFrame(mesh, "SingleAbove", 120f, 1.2f, zeros);

                for (var shape = 0; shape < mesh.blendShapeCount; shape++)
                {
                    var delta = UvIslandExtractor.BlendShapeAt100(mesh, shape, mesh.vertexCount);
                    Assert.That(delta[1].x, Is.EqualTo(1f).Within(1e-5f),
                        "shape " + mesh.GetBlendShapeName(shape) + " did not reconstruct its 100% delta");
                    Assert.That(delta[0], Is.EqualTo(Vector3.zero));
                    Assert.That(delta[2], Is.EqualTo(Vector3.zero));
                }
            }
            finally { Object.DestroyImmediate(mesh); }
        }

        [Test]
        public void DenseOverlapsWithoutSharedUvVerticesMergeThroughSpatialGrid()
        {
            const int triangleCount = 300;
            var gameObject = new GameObject("overlap-spatial-grid-test");
            var texture = NewTexture(TextureWrapMode.Clamp);
            var vertices = new Vector3[triangleCount * 3];
            var uvs = new List<Vector2>(triangleCount * 3);
            var indices = new int[triangleCount * 3];
            for (var triangle = 0; triangle < triangleCount; triangle++)
            {
                var offset = triangle * 3; var shift = triangle * 0.0001f;
                vertices[offset] = Vector3.zero; vertices[offset + 1] = Vector3.right;
                vertices[offset + 2] = Vector3.up;
                uvs.Add(new Vector2(shift, 0f));
                uvs.Add(new Vector2(1f, shift));
                uvs.Add(new Vector2(shift, 1f));
                indices[offset] = offset; indices[offset + 1] = offset + 1; indices[offset + 2] = offset + 2;
            }
            var mesh = new Mesh { vertices = vertices };
            mesh.SetUVs(0, uvs); mesh.SetTriangles(indices, 0);
            try
            {
                var renderer = gameObject.AddComponent<MeshRenderer>();
                var group = new UvGroupRecord
                {
                    Renderer = new RendererRecord { Renderer = renderer, Mesh = mesh },
                    Slot = new MaterialSlotRecord { Slot = 0 }, UvChannel = 0
                };
                group.Bindings.Add(new TextureBindingRecord { Texture = texture, AtlasSafe = true });

                Assert.IsTrue(new UvIslandExtractor().Extract(group, out var failure), failure);
                Assert.That(group.Islands, Has.Count.EqualTo(1));
                Assert.That(group.Islands[0].TriangleIndices, Has.Count.EqualTo(triangleCount));
            }
            finally
            {
                Object.DestroyImmediate(mesh); Object.DestroyImmediate(texture);
                Object.DestroyImmediate(gameObject);
            }
        }

        [Test]
        public void SpatialReferenceBudgetOverflowResumesWithSweepAndPreservesComponents()
        {
            var vertices = RepeatedOverlappingTriangleVertices(300);

            var components = UvIslandExtractor.MergeOverlappingWithSpatialBudgetsForTesting(vertices,
                1, int.MaxValue, out var outcome, out var usedSweepFallback);

            Assert.That(outcome, Is.EqualTo(UvIslandExtractor.SpatialGridOutcome.ReferenceBudgetExceeded));
            Assert.That(usedSweepFallback, Is.True);
            Assert.That(components, Is.EqualTo(1));
        }

        [Test]
        public void SpatialCandidateBudgetOverflowResumesWithSweepAndPreservesComponents()
        {
            var vertices = RepeatedOverlappingTriangleVertices(300);

            var components = UvIslandExtractor.MergeOverlappingWithSpatialBudgetsForTesting(vertices,
                int.MaxValue, 0, out var outcome, out var usedSweepFallback);

            Assert.That(outcome, Is.EqualTo(UvIslandExtractor.SpatialGridOutcome.CandidateBudgetExceeded));
            Assert.That(usedSweepFallback, Is.True);
            Assert.That(components, Is.EqualTo(1));
        }

        [Test]
        public void DenseOverlapExtractionHasFineGrainedCancellation()
        {
            const int triangleCount = 600;
            var gameObject = new GameObject("overlap-cancellation-test");
            var texture = NewTexture(TextureWrapMode.Clamp);
            var mesh = new Mesh { vertices = new[] { Vector3.zero, Vector3.right, Vector3.up } };
            mesh.SetUVs(0, new List<Vector2> { Vector2.zero, Vector2.right, Vector2.up });
            var indices = new int[triangleCount * 3];
            for (var triangle = 0; triangle < triangleCount; triangle++)
            {
                indices[triangle * 3] = 0; indices[triangle * 3 + 1] = 1; indices[triangle * 3 + 2] = 2;
            }
            mesh.SetTriangles(indices, 0);
            try
            {
                var renderer = gameObject.AddComponent<MeshRenderer>();
                var group = new UvGroupRecord
                {
                    Renderer = new RendererRecord { Renderer = renderer, Mesh = mesh },
                    Slot = new MaterialSlotRecord { Slot = 0 }, UvChannel = 0
                };
                group.Bindings.Add(new TextureBindingRecord { Texture = texture, AtlasSafe = true });
                var checkpoints = 0;
                ATOProgress.Begin(() => ++checkpoints >= 3);

                Assert.Throws<System.OperationCanceledException>(() =>
                {
                    string failure;
                    new UvIslandExtractor().Extract(group, out failure);
                });
                Assert.That(checkpoints, Is.EqualTo(3));
            }
            finally
            {
                Object.DestroyImmediate(mesh); Object.DestroyImmediate(texture);
                Object.DestroyImmediate(gameObject);
            }
        }

        private static void AssertEarlyMipFallback(bool sourceMips, FilterMode filter, bool outputMips,
            string expectedReason, bool atlasMode = false)
        {
            var gameObject = new GameObject("uv-mip-analysis-test");
            var texture = new Texture2D(4, 4, TextureFormat.RGBA32, sourceMips, false)
            {
                filterMode = filter, wrapMode = TextureWrapMode.Clamp
            };
            try
            {
                var renderer = gameObject.AddComponent<MeshRenderer>();
                var group = new UvGroupRecord
                {
                    AtlasSafe = true,
                    Renderer = new RendererRecord { Renderer = renderer },
                    Slot = new MaterialSlotRecord { Slot = 0 },
                    UvChannel = 0
                };
                group.Bindings.Add(new TextureBindingRecord
                {
                    Texture = texture, Kind = ATOTextureKind.ColorOpaque, AtlasSafe = true
                });
                var analysis = new AvatarAnalysis(); analysis.UvGroups.Add(group);
                var settings = new ATOOptimizationSettings();
                settings.opaque.mipmapsAndStreaming = outputMips;

                UvAnalysisStage.Execute(analysis, atlasMode, settings);

                Assert.That(group.AtlasSafe, Is.False);
                Assert.That(analysis.Fallbacks, Has.Count.EqualTo(1));
                Assert.That(analysis.Fallbacks[0].Reason, Is.EqualTo(expectedReason));
            }
            finally
            {
                Object.DestroyImmediate(texture);
                Object.DestroyImmediate(gameObject);
            }
        }

        private static List<Vector2> RepeatedOverlappingTriangleVertices(int triangleCount)
        {
            var vertices = new List<Vector2>(triangleCount * 3);
            for (var triangle = 0; triangle < triangleCount; triangle++)
            {
                var shift = triangle * 0.0001f;
                vertices.Add(new Vector2(shift, 0f));
                vertices.Add(new Vector2(1f, shift));
                vertices.Add(new Vector2(shift, 1f));
            }
            return vertices;
        }

        private static void AddShapeFrame(Mesh mesh, string name, float weight, float vertexOneDelta,
            Vector3[] zeros)
        {
            var delta = new Vector3[mesh.vertexCount];
            delta[1] = Vector3.right * vertexOneDelta;
            mesh.AddBlendShapeFrame(name, weight, delta, zeros, zeros);
        }

        private static Texture2D NewTexture(TextureWrapMode wrap)
        {
            return new Texture2D(2, 2, TextureFormat.RGBA32, false, false)
            {
                wrapModeU = wrap,
                wrapModeV = TextureWrapMode.Clamp
            };
        }
    }
}
