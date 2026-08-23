using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Fosa.AvatarTextureOptimizer.Editor.Analysis;
using Fosa.AvatarTextureOptimizer.Editor.API;
using Fosa.AvatarTextureOptimizer.Editor.Atlas;
using Fosa.AvatarTextureOptimizer.Editor.Pipeline;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace Fosa.AvatarTextureOptimizer.Tests
{
    public sealed class ExtensionPipelineTests
    {
        private readonly List<IATOExtension> _registered = new List<IATOExtension>();

        [TearDown]
        public void TearDown()
        {
            foreach (var extension in _registered) ATOExtensionRegistry.Unregister(extension);
            _registered.Clear();
        }

        [Test]
        public void EqualPriorityExtensionsRetainRegistrationOrder()
        {
            var first = Register(new TestExtension(10));
            var second = Register(new TestExtension(10));
            var third = Register(new TestExtension(10));

            var ours = ATOExtensionRegistry.Snapshot().Where(value => _registered.Contains(value)).ToArray();

            CollectionAssert.AreEqual(new[] { first, second, third }, ours);
        }

        [Test]
        public void BeforeAnalysisIsOrderedThenSanitizedBeforeQualityBypassDecision()
        {
            var calls = new List<int>();
            var late = new TestExtension(20, beforeAnalysis: context =>
            {
                calls.Add(20);
                context.Settings.qualityPreset = ATOQualityPreset.Custom;
                context.Settings.customQuality.targetQuality = 1f;
            });
            var early = new TestExtension(-20, beforeAnalysis: context =>
            {
                calls.Add(-20);
                context.Settings.maximumAtlasSize = int.MaxValue;
                context.Settings.minimumPadding = (ATOMinimumPadding)3;
                context.Settings.quality.maxDeltaE2000 = float.NaN;
            });
            Register(late); Register(early);
            var settings = new ATOOptimizationSettings();

            ATOPipeline.RunBeforeAnalysisExtensions(null, null, settings,
                ATOExtensionRegistry.Snapshot().Where(value => _registered.Contains(value)), new List<string>());

            CollectionAssert.AreEqual(new[] { -20, 20 }, calls);
            Assert.That(settings.maximumAtlasSize, Is.EqualTo(8192));
            Assert.That(settings.minimumPadding, Is.EqualTo(ATOMinimumPadding.Pixels4));
            Assert.That(settings.quality.maxDeltaE2000, Is.GreaterThanOrEqualTo(0f));
            Assert.That(ATOPipeline.RequiresStrictQualityBypass(settings), Is.True,
                "quality 1 selected by an extension must still take the pre-analysis no-resampling bypass");
        }

        [Test]
        public void ExtensionInjectedInvalidEnumsAreRepairedAtPipelineBoundary()
        {
            var extension = new TestExtension(0, beforeAnalysis: context =>
            {
                context.Settings.qualityPreset = (ATOQualityPreset)999;
                context.Settings.minimumPadding = (ATOMinimumPadding)(-1);
                context.Settings.minimumPixelDensity = (ATOPixelDensity)123;
                context.Settings.maximumPixelDensity = (ATOPixelDensity)456;
                context.Settings.opaque.compression = (ATOCompression)999;
                context.Settings.alpha.compression = (ATOCompression)999;
                context.Settings.normal.compression = (ATOCompression)999;
                context.Settings.grayscale.compression = (ATOCompression)999;
            });
            var settings = new ATOOptimizationSettings();

            ATOPipeline.RunBeforeAnalysisExtensions(null, null, settings, new[] { extension },
                new List<string>());

            Assert.That(settings.qualityPreset, Is.EqualTo(ATOQualityPreset.Balanced));
            Assert.That(settings.minimumPadding, Is.EqualTo(ATOMinimumPadding.Pixels4));
            Assert.That(settings.minimumPixelDensity, Is.EqualTo(ATOPixelDensity.Density2048));
            Assert.That(settings.maximumPixelDensity, Is.EqualTo(ATOPixelDensity.Density4096));
            Assert.That(settings.opaque.compression, Is.EqualTo(ATOCompression.Auto));
            Assert.That(settings.alpha.compression, Is.EqualTo(ATOCompression.Auto));
            Assert.That(settings.normal.compression, Is.EqualTo(ATOCompression.BC5));
            Assert.That(settings.grayscale.compression, Is.EqualTo(ATOCompression.Auto));
        }

        [Test]
        public void FormatContractAcceptsOnlyStandaloneAndroidAndIosTargets()
        {
            Assert.That(ATOPipeline.IsSupportedBuildTarget(BuildTarget.StandaloneWindows64), Is.True);
            Assert.That(ATOPipeline.IsSupportedBuildTarget(BuildTarget.Android), Is.True);
            Assert.That(ATOPipeline.IsSupportedBuildTarget(BuildTarget.iOS), Is.True);
            Assert.That(ATOPipeline.IsSupportedBuildTarget(BuildTarget.WebGL), Is.False,
                "unverified runtime compression and normal-decoder targets must preserve every original asset");
        }

        [Test]
        public void AreaSavingCountsUnchangedTexturesForWholeAndAtlasAndPreservesGrowth()
        {
            var replacedSource = new Texture2D(4, 4);
            var unchangedSource = new Texture2D(4, 4);
            var generated = new Texture2D(2, 2);
            try
            {
                var first = new TextureBindingRecord
                    { Texture = replacedSource, OriginalTexture = replacedSource };
                var second = new TextureBindingRecord
                    { Texture = unchangedSource, OriginalTexture = unchangedSource };
                var slot = new MaterialSlotRecord();
                slot.Bindings.Add(first); slot.Bindings.Add(second);
                var renderer = new RendererRecord(); renderer.Slots.Add(slot);
                var analysis = new AvatarAnalysis(); analysis.Renderers.Add(renderer);

                var whole = new WholeTextureOptimizer.Result();
                whole.Replacements[first] = generated;
                Assert.That(ATOPipeline.EstimateWholeTextureAreaSaving(analysis, whole),
                    Is.EqualTo(37.5).Within(1e-9),
                    "the unchanged 4x4 texture must remain in the estimated output area");

                var group = new UvGroupRecord(); group.Bindings.Add(first);
                var page = new AtlasPage(); page.Groups.Add(group);
                var plan = new AtlasPlan(); plan.Pages.Add(page);
                var atlas = new AtlasBuildResult(); atlas.BaseLayers[new PageLayerKey(0, 0)] = generated;
                Assert.That(ATOPipeline.EstimateAtlasTextureAreaSaving(analysis, plan, atlas),
                    Is.EqualTo(37.5).Within(1e-9),
                    "bindings outside accepted atlas pages must keep their source area in the estimate");

                Assert.That(ATOPipeline.EstimateTextureAreaSaving(new[] { replacedSource },
                        new[] { replacedSource, generated }), Is.EqualTo(-25.0).Within(1e-9),
                    "a generated texture added beside a retained source is growth and must not be clamped to zero");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(generated);
                UnityEngine.Object.DestroyImmediate(unchangedSource);
                UnityEngine.Object.DestroyImmediate(replacedSource);
            }
        }

        [Test]
        public void SuccessfulTerminalRemovesMarkerBeforeCompletingCommit()
        {
            var root = new GameObject("pipeline-terminal-test");
            var marker = root.AddComponent<AvatarTextureOptimizer>();
            var transaction = new CompletionProbe(() => Assert.That(marker == null, Is.True,
                "the build-only marker must be removed before the transaction becomes non-rollbackable"));
            try
            {
                ATOPipeline.CompleteSuccessfulRun(marker, transaction);

                Assert.That(marker == null, Is.True);
                Assert.That(transaction.CompleteCalls, Is.EqualTo(1));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
                transaction.Dispose();
            }
        }

        [Test]
        public void RollbackGateAllowsCleanupForNullOrCompleteRollbackOnly()
        {
            Assert.That(ATOPipeline.RollbackDeferredCommit(null, null), Is.True);

            var restored = new CompletionProbe(null, () => true);
            Assert.That(ATOPipeline.RollbackDeferredCommit(null, restored), Is.True);
            Assert.That(restored.RollbackCalls, Is.EqualTo(1));

            var incomplete = new CompletionProbe(null, () => false);
            Assert.That(ATOPipeline.RollbackDeferredCommit(null, incomplete), Is.False);
            Assert.That(incomplete.RollbackCalls, Is.EqualTo(1));
        }

        [Test]
        public void RollbackGateFailsClosedWhenTransactionThrows()
        {
            var throwing = new CompletionProbe(null, () => throw new InvalidOperationException("rollback-probe"));
            LogAssert.Expect(LogType.Error, new Regex("\\[ATO\\] Avatar rewrite rollback failed:.*rollback-probe",
                RegexOptions.Singleline));

            Assert.That(ATOPipeline.RollbackDeferredCommit(null, throwing), Is.False);
            Assert.That(throwing.RollbackCalls, Is.EqualTo(1));
        }

        [Test]
        public void IncompleteApplyRollbackExceptionAlwaysDisablesExternalCleanup()
        {
            var ordinary = new InvalidOperationException("ordinary");
            var incomplete = new ATORollbackIncompleteException("incomplete", ordinary);

            Assert.That(ATOPipeline.CanCleanupAfterRollback(ordinary, true), Is.True);
            Assert.That(ATOPipeline.CanCleanupAfterRollback(ordinary, false), Is.False);
            Assert.That(ATOPipeline.CanCleanupAfterRollback(incomplete, true), Is.False);
        }

        [Test]
        public void SuccessfulOwnershipTransferKeepsNonPersistentAtlasAndMesh()
        {
            var texture = new Texture2D(1, 1);
            texture.name = "null-saver-atlas";
            var mesh = new Mesh { name = "null-saver-mesh" };
            var owner = new GameObject("null-saver-owner");
            var renderer = owner.AddComponent<MeshRenderer>();
            var atlases = new AtlasBuildResult();
            atlases.BaseLayers.Add(new PageLayerKey(0, 0), texture);
            atlases.OwnedTextures.Add(texture);
            var meshes = new Dictionary<Renderer, Mesh> { [renderer] = mesh };
            try
            {
                Assert.That(EditorUtility.IsPersistent(texture), Is.False);
                Assert.That(EditorUtility.IsPersistent(mesh), Is.False);

                ATOPipeline.DestroyTransientAtlasesIfOwned(false, atlases);
                ATOPipeline.DestroyTransientMeshesIfOwned(false, meshes);

                Assert.That(texture == null, Is.False,
                    "successful NullAssetSaver-style ownership transfer must retain the referenced atlas");
                Assert.That(mesh == null, Is.False,
                    "successful NullAssetSaver-style ownership transfer must retain the referenced mesh");
            }
            finally
            {
                if (texture != null) UnityEngine.Object.DestroyImmediate(texture);
                if (mesh != null) UnityEngine.Object.DestroyImmediate(mesh);
                UnityEngine.Object.DestroyImmediate(owner);
            }
        }

        [Test]
        public void TextureClassificationVetoCannotBeClearedByALaterExtension()
        {
            var rejecting = new TestExtension(0, classify: context =>
            {
                context.RejectAsUnsafe = true;
                context.RejectionReason = "first veto";
            });
            var clearing = new TestExtension(1, classify: context =>
            {
                context.RejectAsUnsafe = false;
                context.RejectionReason = null;
            });
            var classification = new ATOTextureClassificationContext();

            var reason = AvatarAnalyzer.ApplyExtensionClassifiers(classification,
                new IATOExtension[] { rejecting, clearing }, out var rejected);

            Assert.That(rejected, Is.True);
            Assert.That(classification.RejectAsUnsafe, Is.True);
            Assert.That(reason, Is.EqualTo("first veto"));
        }

        [Test]
        public void UnsupportedCompositeClassificationCannotBeClearedByALaterExtension()
        {
            var rejectingSemantic = new TestExtension(0, classify: context =>
                context.SurfaceAlphaUsage = ATOSurfaceAlphaUsage.UnsupportedComposite);
            var clearing = new TestExtension(1, classify: context =>
                context.SurfaceAlphaUsage = ATOSurfaceAlphaUsage.TextureAlpha);
            var classification = new ATOTextureClassificationContext
            {
                SurfaceAlphaUsage = ATOSurfaceAlphaUsage.TextureAlpha
            };

            AvatarAnalyzer.ApplyExtensionClassifiers(classification,
                new IATOExtension[] { rejectingSemantic, clearing }, out var explicitlyRejected);

            Assert.That(explicitlyRejected, Is.False,
                "the alpha semantic is finalized by the separate material-alpha fail-closed gate");
            Assert.That(classification.SurfaceAlphaUsage, Is.EqualTo(ATOSurfaceAlphaUsage.UnsupportedComposite));
            Assert.That(AvatarAnalyzer.RequiresSurfaceAlphaFallback(true,
                ATOSurfaceAlphaUsage.TextureAlpha, classification.SurfaceAlphaUsage), Is.True);
        }

        [Test]
        public void BeforeCommitReceivesADeeplyDetachedSettingsSnapshot()
        {
            ATOOptimizationSettings observed = null;
            var extension = new TestExtension(0, beforeCommit: context =>
            {
                observed = context.Settings;
                context.Settings.maximumAtlasSize = 256;
                context.Settings.opaque.compression = ATOCompression.DXT1;
                context.Settings.customQuality.targetQuality = 0f;
                context.Warnings.Add("observed");
            });
            var actual = new ATOOptimizationSettings
            {
                maximumAtlasSize = 4096,
                qualityPreset = ATOQualityPreset.Custom
            };
            actual.opaque.compression = ATOCompression.BC7;
            actual.customQuality.targetQuality = 0.95f;
            var warnings = new List<string>();

            ATOPipeline.RunBeforeCommitExtensions(null, null, actual, new[] { extension }, warnings);

            Assert.That(observed, Is.Not.SameAs(actual));
            Assert.That(observed.opaque, Is.Not.SameAs(actual.opaque));
            Assert.That(observed.customQuality, Is.Not.SameAs(actual.customQuality));
            Assert.That(actual.maximumAtlasSize, Is.EqualTo(4096));
            Assert.That(actual.opaque.compression, Is.EqualTo(ATOCompression.BC7));
            Assert.That(actual.customQuality.targetQuality, Is.EqualTo(0.95f).Within(1e-6f));
            CollectionAssert.AreEqual(new[] { "observed" }, warnings);
        }

        private T Register<T>(T extension) where T : IATOExtension
        {
            ATOExtensionRegistry.Register(extension);
            _registered.Add(extension);
            return extension;
        }

        private sealed class CompletionProbe : IATOCommitTransaction
        {
            private readonly Action _onComplete;
            private readonly Func<bool> _onRollback;
            public int CompleteCalls { get; private set; }
            public int RollbackCalls { get; private set; }

            public CompletionProbe(Action onComplete, Func<bool> onRollback = null)
            {
                _onComplete = onComplete;
                _onRollback = onRollback;
            }

            public void Complete()
            {
                CompleteCalls++;
                _onComplete?.Invoke();
            }

            public bool Rollback()
            {
                RollbackCalls++;
                return _onRollback == null || _onRollback();
            }

            public void Dispose()
            {
            }
        }

        private sealed class TestExtension : IATOExtension
        {
            private readonly Action<ATOExtensionContext> _beforeAnalysis;
            private readonly Action<ATOTextureClassificationContext> _classify;
            private readonly Action<ATOExtensionContext> _beforeCommit;
            public int Priority { get; }

            public TestExtension(int priority, Action<ATOExtensionContext> beforeAnalysis = null,
                Action<ATOTextureClassificationContext> classify = null,
                Action<ATOExtensionContext> beforeCommit = null)
            {
                Priority = priority;
                _beforeAnalysis = beforeAnalysis;
                _classify = classify;
                _beforeCommit = beforeCommit;
            }

            public void BeforeAnalysis(ATOExtensionContext context) => _beforeAnalysis?.Invoke(context);
            public void ClassifyTexture(ATOTextureClassificationContext context) => _classify?.Invoke(context);
            public void BeforeCommit(ATOExtensionContext context) => _beforeCommit?.Invoke(context);
        }
    }
}
