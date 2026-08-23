using System;
using System.Collections.Generic;
using Fosa.AvatarTextureOptimizer.Editor.Analysis;
using Fosa.AvatarTextureOptimizer.Editor.Quality;
using Fosa.AvatarTextureOptimizer.Editor.Pipeline;
using NUnit.Framework;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using Object = UnityEngine.Object;

namespace Fosa.AvatarTextureOptimizer.Tests
{
    public sealed class IslandSizeSolverTests
    {
        [Test]
        public void TargetQualityOneSolverDoesNotShrink()
        {
            var analysis = new AvatarAnalysis();
            var group = new UvGroupRecord { AtlasSafe = true };
            var island = new UvIsland { OriginalPixelBounds = new Vector2Int(913, 517) };
            group.Islands.Add(island); analysis.UvGroups.Add(group);
            var settings = new ATOOptimizationSettings { qualityPreset = ATOQualityPreset.Custom };
            settings.customQuality.targetQuality = 1f;
            new IslandSizeSolver().Solve(analysis, settings);
            Assert.AreEqual(new Vector2Int(913, 517), island.TargetPixelSize);
            Assert.AreEqual(Vector2.one, island.Scale);
        }

        [Test]
        public void BoneSkinnedRendererPreservesOriginalIslandResolution()
        {
            var analysis = new AvatarAnalysis();
            var renderer = new RendererRecord { PreserveOriginalIslandResolution = true };
            var group = new UvGroupRecord { AtlasSafe = true, Renderer = renderer };
            var island = new UvIsland { OriginalPixelBounds = new Vector2Int(1024, 512) };
            group.Islands.Add(island); analysis.UvGroups.Add(group);
            var settings = new ATOOptimizationSettings { qualityPreset = ATOQualityPreset.Custom };
            settings.customQuality.targetQuality = 0.5f;

            new IslandSizeSolver().Solve(analysis, settings);

            Assert.AreEqual(new Vector2Int(1024, 512), island.TargetPixelSize);
            Assert.AreEqual(Vector2.one, island.Scale);
        }

        [Test]
        public void BoneSkinnedOversizedCandidateFallsBackBeforeGpuEvaluation()
        {
            var analysis = new AvatarAnalysis();
            var renderer = new RendererRecord { PreserveOriginalIslandResolution = true };
            var group = new UvGroupRecord { AtlasSafe = true, Renderer = renderer };
            group.Islands.Add(new UvIsland { OriginalPixelBounds = new Vector2Int(2049, 2048) });
            analysis.UvGroups.Add(group);
            var settings = new ATOOptimizationSettings { qualityPreset = ATOQualityPreset.Custom };
            settings.customQuality.targetQuality = 0.5f;

            new IslandSizeSolver().Solve(analysis, settings);

            Assert.IsFalse(group.AtlasSafe);
            Assert.That(analysis.Fallbacks[0].Reason, Does.Contain("resident-memory"));
        }

        [Test]
        public void BoneSkinnedCandidateExactlyAtResidentLimitRemainsAllowed()
        {
            var analysis = new AvatarAnalysis();
            var renderer = new RendererRecord { PreserveOriginalIslandResolution = true };
            var group = new UvGroupRecord { AtlasSafe = true, Renderer = renderer };
            var size = new Vector2Int(2048, 2048);
            group.Islands.Add(new UvIsland { OriginalPixelBounds = size });
            analysis.UvGroups.Add(group);
            var settings = new ATOOptimizationSettings { qualityPreset = ATOQualityPreset.Custom };
            settings.customQuality.targetQuality = 0.5f;

            new IslandSizeSolver().Solve(analysis, settings);

            Assert.That((long)size.x * size.y, Is.EqualTo(IslandQualityEvaluator.MaximumResidentPixels));
            Assert.IsTrue(group.AtlasSafe);
            Assert.That(group.Islands[0].TargetPixelSize, Is.EqualTo(size));
            Assert.That(analysis.Fallbacks, Is.Empty);
        }

        [Test]
        public void StrictPipelineBypassCoversAtlasAndWholeTextureModes()
        {
            foreach (var generateAtlases in new[] { false, true })
            {
                var settings = new ATOOptimizationSettings
                {
                    generateAtlases = generateAtlases,
                    qualityPreset = ATOQualityPreset.Custom
                };
                settings.customQuality.targetQuality = 1f;
                Assert.IsTrue(ATOPipeline.RequiresStrictQualityBypass(settings));

                settings.customQuality.targetQuality = 0.999f;
                Assert.IsFalse(ATOPipeline.RequiresStrictQualityBypass(settings));
            }
        }

        [Test]
        public void GpuCapabilityGateRequiresComputeReadbackAndEveryWorkFormatUsage()
        {
            var required = new HashSet<string>
            {
                GraphicsFormat.R16G16B16A16_SFloat + ":" + FormatUsage.Sample,
                GraphicsFormat.R16G16B16A16_SFloat + ":" + FormatUsage.Render,
                GraphicsFormat.R16G16B16A16_SFloat + ":" + FormatUsage.LoadStore,
                GraphicsFormat.R8_UNorm + ":" + FormatUsage.Sample,
                GraphicsFormat.R8_UNorm + ":" + FormatUsage.Render,
                GraphicsFormat.R8_UNorm + ":" + FormatUsage.LoadStore
            };
            var queried = new HashSet<string>();
            Assert.That(ATOPipeline.SupportsRequiredGpuCapabilities(true, true, (format, usage) =>
            {
                queried.Add(format + ":" + usage);
                return true;
            }), Is.True);
            CollectionAssert.AreEquivalent(required, queried);

            Assert.That(ATOPipeline.SupportsRequiredGpuCapabilities(false, true, (_, __) => true), Is.False);
            Assert.That(ATOPipeline.SupportsRequiredGpuCapabilities(true, false, (_, __) => true), Is.False);
            Assert.That(ATOPipeline.SupportsRequiredGpuCapabilities(true, true, null), Is.False);
            foreach (var missing in required)
                Assert.That(ATOPipeline.SupportsRequiredGpuCapabilities(true, true,
                    (format, usage) => format + ":" + usage != missing), Is.False,
                    "missing GPU format usage must fail closed: " + missing);
            Assert.That(ATOPipeline.SupportsRequiredGpuCapabilities(true, true,
                (_, __) => throw new InvalidOperationException("injected query failure")), Is.False);
        }

        [Test]
        public void ExactMipCandidatesIncludeOffsetZeroAndFractionalFootprints()
        {
            var texture = new Texture2D(8, 8, TextureFormat.RGBA32, true);
            try
            {
                var group = new UvGroupRecord();
                group.Bindings.Add(new TextureBindingRecord { Texture = texture });
                var island = new UvIsland { UvBounds = new Rect(0f, 0f, 0.75f, 0.5f) };

                var candidates = IslandSizeSolver.FindExactMipCandidates(group, island,
                    Vector2Int.one, new Vector2Int(8, 8));

                CollectionAssert.AreEqual(new[] { new Vector2Int(3, 2), new Vector2Int(6, 4) }, candidates);
                Assert.That(candidates, Does.Contain(new Vector2Int(6, 4)),
                    "offset zero is a runtime LOD candidate and must not be skipped");
            }
            finally { Object.DestroyImmediate(texture); }
        }

        [Test]
        public void FractionalCropAlignsOutwardToSharedTexelsAndRetainsExactLodCandidate()
        {
            var eight = new Texture2D(8, 8, TextureFormat.RGBA32, true);
            var four = new Texture2D(4, 4, TextureFormat.RGBA32, true);
            try
            {
                var group = new UvGroupRecord();
                group.Bindings.Add(new TextureBindingRecord { Texture = eight });
                group.Bindings.Add(new TextureBindingRecord { Texture = four });
                var island = new UvIsland { UvBounds = new Rect(0.1f, 0.1f, 0.7f, 0.7f) };

                Assert.That(UvIslandExtractor.AlignBoundsToSharedMipTexels(group, island, out var failure), Is.True,
                    failure);
                Assert.That(island.UvBounds.xMin, Is.LessThanOrEqualTo(0.1f));
                Assert.That(island.UvBounds.xMax, Is.GreaterThanOrEqualTo(0.8f));
                Assert.That(IslandSizeSolver.FindExactMipCandidates(group, island,
                    Vector2Int.one, new Vector2Int(8, 8)), Does.Contain(new Vector2Int(4, 4)),
                    "the shared target maps the 8px source at offset 1 and the 4px source at offset 0");
            }
            finally
            {
                Object.DestroyImmediate(eight); Object.DestroyImmediate(four);
            }
        }

        [Test]
        public void ExactMipCandidatesRequireAnIntersectionAcrossBindings()
        {
            var eight = new Texture2D(8, 8, TextureFormat.RGBA32, true);
            var six = new Texture2D(6, 6, TextureFormat.RGBA32, true);
            try
            {
                var group = new UvGroupRecord();
                group.Bindings.Add(new TextureBindingRecord { Texture = eight });
                group.Bindings.Add(new TextureBindingRecord { Texture = six });
                var island = new UvIsland { UvBounds = new Rect(0f, 0f, 1f, 1f) };

                Assert.That(IslandSizeSolver.FindExactMipCandidates(group, island,
                    Vector2Int.one, new Vector2Int(8, 8)), Is.Empty);
            }
            finally
            {
                Object.DestroyImmediate(eight);
                Object.DestroyImmediate(six);
            }
        }

        [Test]
        public void NoMipBindingsKeepTheContinuousCandidatePath()
        {
            var texture = new Texture2D(8, 8, TextureFormat.RGBA32, false);
            try
            {
                var group = new UvGroupRecord();
                group.Bindings.Add(new TextureBindingRecord { Texture = texture });
                Assert.That(IslandSizeSolver.RequiresExactMipCandidates(group), Is.False);
                Assert.That(IslandSizeSolver.FindExactMipCandidates(group, new UvIsland(),
                    Vector2Int.one, new Vector2Int(8, 8)), Is.Empty);
            }
            finally { Object.DestroyImmediate(texture); }
        }

        [Test]
        public void TargetQualityOneWithMipBindingDoesNotDereferenceGpuEvaluator()
        {
            var texture = new Texture2D(8, 8, TextureFormat.RGBA32, true);
            try
            {
                var analysis = new AvatarAnalysis();
                var group = new UvGroupRecord { AtlasSafe = true };
                group.Bindings.Add(new TextureBindingRecord { Texture = texture });
                var island = new UvIsland
                {
                    UvBounds = new Rect(0f, 0f, 1f, 1f),
                    OriginalPixelBounds = new Vector2Int(8, 8)
                };
                group.Islands.Add(island); analysis.UvGroups.Add(group);
                var settings = new ATOOptimizationSettings { qualityPreset = ATOQualityPreset.Custom };
                settings.customQuality.targetQuality = 1f;

                new IslandSizeSolver().Solve(analysis, settings);

                Assert.That(group.AtlasSafe, Is.True);
                Assert.That(island.TargetPixelSize, Is.EqualTo(new Vector2Int(8, 8)));
            }
            finally { Object.DestroyImmediate(texture); }
        }

        [Test]
        public void NoSharedExactMipCandidateFallsBackTheWholeUvGroup()
        {
            var eight = new Texture2D(8, 8, TextureFormat.RGBA32, true);
            var six = new Texture2D(6, 6, TextureFormat.RGBA32, true);
            try
            {
                var analysis = new AvatarAnalysis();
                var group = new UvGroupRecord { AtlasSafe = true };
                group.Bindings.Add(new TextureBindingRecord { Texture = eight });
                group.Bindings.Add(new TextureBindingRecord { Texture = six });
                group.Islands.Add(new UvIsland
                {
                    UvBounds = new Rect(0f, 0f, 1f, 1f),
                    OriginalPixelBounds = new Vector2Int(8, 8)
                });
                analysis.UvGroups.Add(group);
                var settings = new ATOOptimizationSettings { qualityPreset = ATOQualityPreset.Custom };
                settings.customQuality.targetQuality = 0.9f;

                new IslandSizeSolver().Solve(analysis, settings);

                Assert.That(group.AtlasSafe, Is.False);
                Assert.That(analysis.Fallbacks, Has.Count.EqualTo(1));
                Assert.That(group.Islands[0].TargetPixelSize, Is.EqualTo(Vector2Int.zero));
            }
            finally
            {
                Object.DestroyImmediate(eight);
                Object.DestroyImmediate(six);
            }
        }

        [Test]
        public void PureColorIslandUsesOnePixelAfterReconstructionValidation()
        {
            if (!SystemInfo.supportsComputeShaders || !SystemInfo.supportsAsyncGPUReadback)
                Assert.Ignore("Compute shaders or asynchronous GPU readback are unavailable.");
            var texture = new Texture2D(4, 4, TextureFormat.RGBA32, false, true);
            var mesh = new Mesh
            {
                vertices = new[] { Vector3.zero, Vector3.right, Vector3.up }
            };
            var rendererObject = new GameObject("pure-color-solver-test");
            try
            {
                texture.SetPixels(new[]
                {
                    Color.red, Color.red, Color.red, Color.red,
                    Color.red, Color.red, Color.red, Color.red,
                    Color.red, Color.red, Color.red, Color.red,
                    Color.red, Color.red, Color.red, Color.red
                });
                texture.Apply(false, false);
                mesh.SetUVs(0, new List<Vector2> { Vector2.zero, Vector2.right, Vector2.up });
                mesh.SetTriangles(new[] { 0, 1, 2 }, 0);
                var renderer = rendererObject.AddComponent<MeshRenderer>();
                var rendererRecord = new RendererRecord { Renderer = renderer, Mesh = mesh };
                var group = new UvGroupRecord { AtlasSafe = true, Renderer = rendererRecord, UvChannel = 0 };
                group.Bindings.Add(new TextureBindingRecord
                {
                    Texture = texture, Kind = ATOTextureKind.ColorOpaque, AtlasSafe = true
                });
                var island = new UvIsland
                {
                    UvBounds = new Rect(0f, 0f, 1f, 1f),
                    OriginalPixelBounds = new Vector2Int(4, 4)
                };
                island.TriangleIndices.Add(0);
                group.Islands.Add(island);
                var analysis = new AvatarAnalysis(); analysis.UvGroups.Add(group);
                var settings = new ATOOptimizationSettings { qualityPreset = ATOQualityPreset.Custom };
                settings.customQuality.targetQuality = 0.9f;

                new IslandSizeSolver().Solve(analysis, settings);

                Assert.That(group.AtlasSafe, Is.True);
                Assert.That(island.PureColor, Is.True);
                Assert.That(island.TargetPixelSize, Is.EqualTo(Vector2Int.one));
                Assert.That(island.Scale, Is.EqualTo(new Vector2(0.25f, 0.25f)));
            }
            finally
            {
                Object.DestroyImmediate(rendererObject);
                Object.DestroyImmediate(mesh);
                Object.DestroyImmediate(texture);
            }
        }

        [Test]
        public void MaskedPureColorDoesNotBypassOffIslandOnePixelSample()
        {
            if (!SystemInfo.supportsComputeShaders || !SystemInfo.supportsAsyncGPUReadback)
                Assert.Ignore("Compute shaders or asynchronous GPU readback are unavailable.");
            var texture = new Texture2D(8, 8, TextureFormat.RGBA32, false, true)
            {
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp
            };
            var mesh = new Mesh { vertices = new Vector3[8] };
            var rendererObject = new GameObject("masked-pure-reconstruction-test");
            NativeArray<byte> sourceMask = default;
            try
            {
                // A connected L-shaped island: its UV-bound centre is deliberately outside the covered shape.
                // 连通的 L 形岛：其 UV bounds 中心明确位于形状外。
                mesh.SetUVs(0, new List<Vector2>
                {
                    new Vector2(0f, 0f), new Vector2(0.25f, 0f),
                    new Vector2(0f, 1f), new Vector2(0.25f, 1f),
                    new Vector2(0.25f, 0.75f), new Vector2(1f, 0.75f),
                    new Vector2(0.25f, 1f), new Vector2(1f, 1f)
                });
                mesh.SetTriangles(new[] { 0, 1, 2, 1, 3, 2, 4, 5, 6, 5, 7, 6 }, 0);
                var renderer = rendererObject.AddComponent<MeshRenderer>();
                var group = new UvGroupRecord
                {
                    AtlasSafe = true,
                    Renderer = new RendererRecord { Renderer = renderer, Mesh = mesh },
                    UvChannel = 0
                };
                var binding = new TextureBindingRecord
                {
                    Texture = texture, Kind = ATOTextureKind.ColorOpaque, AtlasSafe = true
                };
                group.Bindings.Add(binding);
                var island = new UvIsland
                {
                    UvBounds = new Rect(0f, 0f, 1f, 1f),
                    OriginalPixelBounds = new Vector2Int(8, 8)
                };
                island.TriangleIndices.AddRange(new[] { 0, 1, 2, 3 });
                group.Islands.Add(island);

                sourceMask = IslandMaskRasterizer.Rasterize(group, island, new Vector2Int(8, 8),
                    Allocator.TempJob);
                const int onePixelSampleInLocalBounds = 4 * 8 + 4;
                Assert.That(IslandMaskRasterizer.IsSet(sourceMask, onePixelSampleInLocalBounds), Is.False,
                    "the 1x1 point sample must lie outside the L-shaped island for this regression");
                var colors = new Color[8 * 8];
                for (var i = 0; i < colors.Length; i++)
                    colors[i] = IslandMaskRasterizer.IsSet(sourceMask, i) ? Color.red : Color.blue;
                texture.SetPixels(colors);
                texture.Apply(false, false);

                using (var evaluator = new IslandQualityEvaluator())
                {
                    var passes = evaluator.Passes(group, island, Vector2Int.one,
                        new ATOQualitySettings(), out var allPureColor);
                    Assert.That(allPureColor, Is.True,
                        "all source texels covered by the island mask are deliberately red");
                    Assert.That(passes, Is.False,
                        "masked source purity must not bypass the actual blue 1x1 reconstruction");
                }
            }
            finally
            {
                if (sourceMask.IsCreated) sourceMask.Dispose();
                Object.DestroyImmediate(rendererObject);
                Object.DestroyImmediate(mesh);
                Object.DestroyImmediate(texture);
            }
        }

        [Test]
        public void ContinuousSolverOnlyReturnsADirectlyRevalidatedNonMonotoneCandidate()
        {
            var upper = new Vector2Int(64, 64);
            var last = Vector2Int.zero;
            Func<Vector2Int, bool> accepted = candidate => candidate == upper ||
                candidate.x == 17 || candidate.y == 11;
            Func<Vector2Int, bool> observed = candidate =>
            {
                last = candidate;
                return accepted(candidate);
            };

            Assert.That(IslandSizeSolver.TrySolveContinuous(Vector2Int.one, upper, observed, out var result),
                Is.True);
            Assert.That(accepted(result), Is.True);
            Assert.That(last, Is.EqualTo(result),
                "the returned non-monotone candidate must be the exact final predicate invocation");
        }

        [Test]
        public void ContinuousSolverRejectsCandidateThatFailsFinalRevalidationAndUsesRevalidatedUpper()
        {
            var upper = new Vector2Int(64, 64);
            var counts = new Dictionary<Vector2Int, int>();
            Func<Vector2Int, bool> unstable = candidate =>
            {
                counts.TryGetValue(candidate, out var count);
                counts[candidate] = count + 1;
                if (candidate == upper) return true;
                return count == 0;
            };

            Assert.That(IslandSizeSolver.TrySolveContinuous(Vector2Int.one, upper, unstable, out var result),
                Is.True);
            Assert.That(result, Is.EqualTo(upper));
            Assert.That(counts[upper], Is.EqualTo(2),
                "upper must be directly revalidated rather than trusted from the initial check");
        }

        [Test]
        public void ContinuousSolverFailsWhenSelectedAndUpperBothFailFinalRevalidation()
        {
            var upper = new Vector2Int(64, 64);
            var counts = new Dictionary<Vector2Int, int>();
            Func<Vector2Int, bool> unstable = candidate =>
            {
                counts.TryGetValue(candidate, out var count);
                counts[candidate] = count + 1;
                return count == 0;
            };

            Assert.That(IslandSizeSolver.TrySolveContinuous(Vector2Int.one, upper, unstable, out var result),
                Is.False);
            Assert.That(result, Is.EqualTo(Vector2Int.zero));
            Assert.That(counts[upper], Is.EqualTo(2));
        }

        [Test]
        public void ContinuousSolverKeepsUniformThenAxisSearchForMonotonePredicate()
        {
            var lower = new Vector2Int(1, 1);
            var upper = new Vector2Int(64, 32);
            Func<Vector2Int, bool> passes = candidate => candidate.x >= 13 && candidate.y >= 9;

            Assert.That(IslandSizeSolver.TrySolveContinuous(lower, upper, passes, out var result), Is.True);
            Assert.That(result, Is.EqualTo(new Vector2Int(13, 9)));
            Assert.That(result.x, Is.GreaterThanOrEqualTo(lower.x));
            Assert.That(result.y, Is.GreaterThanOrEqualTo(lower.y));
            Assert.That(passes(result), Is.True);
        }

        [Test]
        public void NearLosslessPresetUsesStrictPipelineBypass()
        {
            var settings = new ATOOptimizationSettings { qualityPreset = ATOQualityPreset.NearLossless };
            settings.quality.ApplyPreset(settings.qualityPreset);
            Assert.IsTrue(ATOPipeline.RequiresStrictQualityBypass(settings));
        }
    }
}
