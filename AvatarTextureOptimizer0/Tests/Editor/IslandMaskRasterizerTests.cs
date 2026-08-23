using System.Collections.Generic;
using Fosa.AvatarTextureOptimizer.Editor.Analysis;
using Fosa.AvatarTextureOptimizer.Editor.Quality;
using Fosa.AvatarTextureOptimizer.Editor.Pipeline;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace Fosa.AvatarTextureOptimizer.Tests
{
    [Parallelizable(ParallelScope.None)]
    public sealed class IslandMaskRasterizerTests
    {
        [TearDown]
        public void TearDown() => ATOProgress.End();

        [Test]
        public void ThinTriangleMarksPixelsTouchedOnlyAtBoundary()
        {
            var mesh = new Mesh { vertices = new[] { Vector3.zero, Vector3.right, Vector3.up } };
            mesh.SetUVs(0, new List<Vector2> { Vector2.zero, Vector2.one, new Vector2(0.99f, 1f) });
            mesh.SetTriangles(new[] { 0, 1, 2 }, 0);
            var rendererObject = new GameObject("mask-test");
            try
            {
                var renderer = rendererObject.AddComponent<MeshRenderer>();
                var record = new RendererRecord { Renderer = renderer, Mesh = mesh };
                var slot = new MaterialSlotRecord { Slot = 0 };
                var group = new UvGroupRecord { Renderer = record, Slot = slot, UvChannel = 0 };
                var island = new UvIsland { UvBounds = new Rect(0f, 0f, 1f, 1f), IntegerNormalization = Vector2.zero };
                island.TriangleIndices.Add(0);
                var mask = IslandMaskRasterizer.Rasterize(group, island, new Vector2Int(4, 4), Allocator.TempJob);
                try
                {
                    Assert.IsTrue(IslandMaskRasterizer.IsSet(mask, 4),
                        "The diagonal touches pixel (0,1) at its corner and conservative coverage must retain it.");
                    Assert.Greater(Count(mask, 16), 0);
                }
                finally { mask.Dispose(); }
            }
            finally
            {
                Object.DestroyImmediate(rendererObject); Object.DestroyImmediate(mesh);
            }
        }

        [Test]
        public void BinnedComplexIslandMatchesSingleTriangleCoverageAndCanCancel()
        {
            const int triangleCount = 65;
            var mesh = new Mesh { vertices = new[] { Vector3.zero, Vector3.right, Vector3.up } };
            mesh.SetUVs(0, new List<Vector2> { Vector2.zero, Vector2.right, Vector2.up });
            var indices = new int[triangleCount * 3];
            for (var triangle = 0; triangle < triangleCount; triangle++)
            {
                indices[triangle * 3] = 0; indices[triangle * 3 + 1] = 1; indices[triangle * 3 + 2] = 2;
            }
            mesh.SetTriangles(indices, 0);
            var rendererObject = new GameObject("binned-mask-test");
            try
            {
                var renderer = rendererObject.AddComponent<MeshRenderer>();
                var group = new UvGroupRecord
                {
                    Renderer = new RendererRecord { Renderer = renderer, Mesh = mesh },
                    Slot = new MaterialSlotRecord { Slot = 0 }, UvChannel = 0
                };
                var simple = new UvIsland { UvBounds = new Rect(0f, 0f, 1f, 1f) };
                simple.TriangleIndices.Add(0);
                var complex = new UvIsland { UvBounds = simple.UvBounds };
                for (var triangle = 0; triangle < triangleCount; triangle++) complex.TriangleIndices.Add(triangle);

                var expected = IslandMaskRasterizer.Rasterize(group, simple, new Vector2Int(32, 32),
                    Allocator.TempJob);
                var actual = IslandMaskRasterizer.Rasterize(group, complex, new Vector2Int(32, 32),
                    Allocator.TempJob, out var usedBins);
                try
                {
                    Assert.IsTrue(usedBins, "65 triangles must cross the binned-path threshold.");
                    CollectionAssert.AreEqual(expected.ToArray(), actual.ToArray());
                }
                finally { expected.Dispose(); actual.Dispose(); }

                ATOProgress.Begin(() => true);
                Assert.Throws<System.OperationCanceledException>(() =>
                    IslandMaskRasterizer.Rasterize(group, complex, new Vector2Int(32, 32), Allocator.TempJob));
            }
            finally
            {
                Object.DestroyImmediate(rendererObject); Object.DestroyImmediate(mesh);
            }
        }

        [Test]
        public void TriangleBinReferenceBudgetFallsBackWithoutChangingCoverage()
        {
            const int triangleCount = 300;
            var mesh = new Mesh { vertices = new[] { Vector3.zero, Vector3.right, Vector3.up } };
            mesh.SetUVs(0, new List<Vector2> { Vector2.zero, Vector2.right, Vector2.up });
            var indices = new int[triangleCount * 3];
            for (var triangle = 0; triangle < triangleCount; triangle++)
            {
                indices[triangle * 3] = 0; indices[triangle * 3 + 1] = 1; indices[triangle * 3 + 2] = 2;
            }
            mesh.SetTriangles(indices, 0);
            var rendererObject = new GameObject("mask-budget-fallback-test");
            try
            {
                var renderer = rendererObject.AddComponent<MeshRenderer>();
                var group = new UvGroupRecord
                {
                    Renderer = new RendererRecord { Renderer = renderer, Mesh = mesh },
                    Slot = new MaterialSlotRecord { Slot = 0 }, UvChannel = 0
                };
                var simple = new UvIsland { UvBounds = new Rect(0f, 0f, 1f, 1f) };
                simple.TriangleIndices.Add(0);
                var complex = new UvIsland { UvBounds = simple.UvBounds };
                for (var triangle = 0; triangle < triangleCount; triangle++) complex.TriangleIndices.Add(triangle);

                var expected = IslandMaskRasterizer.Rasterize(group, simple, new Vector2Int(32, 32),
                    Allocator.TempJob);
                var actual = IslandMaskRasterizer.Rasterize(group, complex, new Vector2Int(32, 32),
                    Allocator.TempJob, out var usedBins);
                try
                {
                    Assert.IsFalse(usedBins, "Full-page references must exceed the bounded tile-index budget.");
                    CollectionAssert.AreEqual(expected.ToArray(), actual.ToArray());
                }
                finally { expected.Dispose(); actual.Dispose(); }
            }
            finally
            {
                Object.DestroyImmediate(rendererObject); Object.DestroyImmediate(mesh);
            }
        }

        [Test]
        public void DegenerateTriangleStillHasConservativeFiniteCoverage()
        {
            var mesh = new Mesh { vertices = new[] { Vector3.zero, Vector3.right, Vector3.up } };
            mesh.SetUVs(0, new List<Vector2>
            {
                new Vector2(-1000f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(1000f, 0.5f)
            });
            mesh.SetTriangles(new[] { 0, 1, 2 }, 0);
            var rendererObject = new GameObject("degenerate-mask-test");
            try
            {
                var renderer = rendererObject.AddComponent<MeshRenderer>();
                var group = new UvGroupRecord
                {
                    Renderer = new RendererRecord { Renderer = renderer, Mesh = mesh },
                    Slot = new MaterialSlotRecord { Slot = 0 }, UvChannel = 0
                };
                var island = new UvIsland { UvBounds = new Rect(0f, 0f, 1f, 1f) };
                island.TriangleIndices.Add(0);
                var mask = IslandMaskRasterizer.Rasterize(group, island, new Vector2Int(16, 16),
                    Allocator.TempJob);
                try { Assert.Greater(Count(mask, 16 * 16), 0); }
                finally { mask.Dispose(); }
            }
            finally
            {
                Object.DestroyImmediate(rendererObject); Object.DestroyImmediate(mesh);
            }
        }

        [Test]
        public void EmptyCoverageIsNotPureColor()
        {
            var colors = new NativeArray<float4>(4, Allocator.TempJob, NativeArrayOptions.ClearMemory);
            var mask = new NativeArray<byte>(1, Allocator.TempJob, NativeArrayOptions.ClearMemory);
            try { Assert.IsFalse(IslandQualityEvaluator.IsPure(colors, mask)); }
            finally { colors.Dispose(); mask.Dispose(); }
        }

        [Test]
        public void NonFiniteCoveredPixelCannotUsePureColorShortcut()
        {
            var colors = new NativeArray<float4>(4, Allocator.TempJob, NativeArrayOptions.ClearMemory);
            var mask = new NativeArray<byte>(1, Allocator.TempJob, NativeArrayOptions.ClearMemory);
            try
            {
                mask[0] = 0x0f;
                for (var i = 0; i < colors.Length; i++) colors[i] = new float4(0.25f, 0.5f, 0.75f, 1f);
                Assert.That(IslandQualityEvaluator.IsPure(colors, mask), Is.True);
                colors[2] = new float4(float.NaN, 0.5f, 0.75f, 1f);
                Assert.That(IslandQualityEvaluator.IsPure(colors, mask), Is.False);
                colors[2] = new float4(0.25f, float.PositiveInfinity, 0.75f, 1f);
                Assert.That(IslandQualityEvaluator.IsPure(colors, mask), Is.False);
            }
            finally { colors.Dispose(); mask.Dispose(); }
        }

        [Test]
        public void CandidateAreaAboveResidentLimitFallsBackBeforeGpuAllocation()
        {
            var texture = new Texture2D(1, 1, TextureFormat.RGBA32, false, true);
            var evaluator = new IslandQualityEvaluator();
            try
            {
                var binding = new TextureBindingRecord
                {
                    Texture = texture,
                    Kind = ATOTextureKind.ColorOpaque,
                    AtlasSafe = true
                };
                var group = new UvGroupRecord(); group.Bindings.Add(binding);
                var island = new UvIsland { UvBounds = new Rect(0f, 0f, 1f, 1f) };
                var tooLarge = new Vector2Int((int)IslandQualityEvaluator.MaximumResidentPixels + 1, 1);
                Assert.IsFalse(evaluator.Passes(group, island, tooLarge, new ATOQualitySettings(), out _));
                Assert.IsTrue(evaluator.ResourceLimitExceeded);
            }
            finally
            {
                evaluator.Dispose(); Object.DestroyImmediate(texture);
            }
        }

        private static int Count(NativeArray<byte> mask, int pixels)
        {
            var count = 0;
            for (var pixel = 0; pixel < pixels; pixel++) if (IslandMaskRasterizer.IsSet(mask, pixel)) count++;
            return count;
        }
    }
}
