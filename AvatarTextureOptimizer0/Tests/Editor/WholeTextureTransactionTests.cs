using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Fosa.AvatarTextureOptimizer.Editor.Analysis;
using Fosa.AvatarTextureOptimizer.Editor.Atlas;
using Fosa.AvatarTextureOptimizer.Editor.Pipeline;
using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Fosa.AvatarTextureOptimizer.Tests
{
    public sealed class WholeTextureTransactionTests
    {
        [Test]
        public void PipelineWholeCleanupHonorsOwnershipTransferGate()
        {
            var retained = new Texture2D(1, 1) { name = "retained-whole" };
            var reclaimed = new Texture2D(1, 1) { name = "reclaimed-whole" };
            var preexistingCanonical = new Texture2D(1, 1) { name = "preexisting-canonical" };
            try
            {
                var retainedResult = new WholeTextureOptimizer.Result();
                retainedResult.Replacements.Add(new TextureBindingRecord(), retained);
                retainedResult.GeneratedTextures.Add(retained);
                ATOPipeline.DestroyTransientWholeIfOwned(false, retainedResult);
                Assert.That(retained == null, Is.False,
                    "successful ownership transfer or incomplete rollback must retain generated textures");

                var reclaimedResult = new WholeTextureOptimizer.Result();
                reclaimedResult.Replacements.Add(new TextureBindingRecord(), reclaimed);
                reclaimedResult.Replacements.Add(new TextureBindingRecord(), reclaimed);
                reclaimedResult.Replacements.Add(new TextureBindingRecord(), preexistingCanonical);
                reclaimedResult.GeneratedTextures.Add(reclaimed);
                ATOPipeline.DestroyTransientWholeIfOwned(true, reclaimedResult);
                Assert.That(reclaimed == null, Is.True,
                    "complete rollback must reclaim distinct transient whole-texture outputs");
                Assert.That(preexistingCanonical == null, Is.False,
                    "identity-only canonical replacements are pre-existing inputs, not optimizer-owned outputs");
                Assert.DoesNotThrow(() => ATOPipeline.DestroyTransientWholeIfOwned(true, null));
            }
            finally
            {
                if (retained != null) Object.DestroyImmediate(retained);
                if (reclaimed != null) Object.DestroyImmediate(reclaimed);
                if (preexistingCanonical != null) Object.DestroyImmediate(preexistingCanonical);
            }
        }

        [Test]
        public void WholeOutputBudgetRejectsBeforeCompleteSurfaceAllocation()
        {
            Assert.That(WholeTextureOptimizer.FitsOutputBudget(new Vector2Int(4096, 4096), 8192), Is.True,
                "the exact conservative pixel boundary remains supported");
            Assert.That(WholeTextureOptimizer.FitsOutputBudget(new Vector2Int(4097, 4096), 8192), Is.False,
                "one pixel column over the complete-output budget must fallback before allocation");
            Assert.That(WholeTextureOptimizer.FitsOutputBudget(new Vector2Int(4096, 2048), 2048), Is.False,
                "each output axis must also fit the graphics device limit");
            Assert.That(WholeTextureOptimizer.FitsOutputBudget(Vector2Int.zero, 8192), Is.False);
            Assert.That(WholeTextureOptimizer.FitsOutputBudget(Vector2Int.one, 0), Is.False);
        }

        [Test]
        public void IdentitySizedWholeOptimizationStillRedirectsExactDuplicateToCanonicalTexture()
        {
            var canonical = new Texture2D(2, 2, TextureFormat.RGBA32, false, true);
            var duplicate = new Texture2D(2, 2, TextureFormat.RGBA32, false, true);
            try
            {
                var duplicateBinding = new TextureBindingRecord
                    { Texture = canonical, OriginalTexture = duplicate };
                var alreadyCanonical = new TextureBindingRecord
                    { Texture = canonical, OriginalTexture = canonical };
                var missingOriginal = new TextureBindingRecord
                    { Texture = canonical, OriginalTexture = null };
                var result = new WholeTextureOptimizer.Result();

                WholeTextureOptimizer.AddIdentityCanonicalReplacements(result,
                    new[] { duplicateBinding, alreadyCanonical, missingOriginal }, canonical, true);

                Assert.That(result.Replacements, Has.Count.EqualTo(1));
                Assert.That(result.Replacements[duplicateBinding], Is.SameAs(canonical));
                Assert.That(result.Replacements.ContainsKey(alreadyCanonical), Is.False,
                    "identity optimization must not create a meaningless self replacement");
            }
            finally
            {
                Object.DestroyImmediate(canonical);
                Object.DestroyImmediate(duplicate);
            }
        }

        [Test]
        public void IdentitySizedWholeOptimizationKeepsOriginalIdentityWhenDeduplicationIsDisabled()
        {
            var canonical = new Texture2D(2, 2, TextureFormat.RGBA32, false, true);
            var duplicate = new Texture2D(2, 2, TextureFormat.RGBA32, false, true);
            try
            {
                var binding = new TextureBindingRecord
                    { Texture = canonical, OriginalTexture = duplicate };
                var result = new WholeTextureOptimizer.Result();

                WholeTextureOptimizer.AddIdentityCanonicalReplacements(result,
                    new[] { binding }, canonical, false);

                Assert.That(result.Replacements, Is.Empty,
                    "the user-facing deduplication switch must also gate identity-only canonical redirects");
            }
            finally
            {
                Object.DestroyImmediate(canonical);
                Object.DestroyImmediate(duplicate);
            }
        }

        [Test]
        public void EmptyReplacementPlanReturnsNoTransactionAndDoesNotMutateRenderer()
        {
            var fixture = new Fixture();
            try
            {
                var transaction = fixture.Rewriter.Apply(fixture.Analysis,
                    new Dictionary<TextureBindingRecord, Texture2D>());

                Assert.That(transaction, Is.Null);
                Assert.That(fixture.Renderer.sharedMaterials[0], Is.SameAs(fixture.SourceMaterial));
                Assert.That(fixture.Saver.Saved, Is.Empty);
            }
            finally { fixture.Dispose(); }
        }

        [Test]
        public void SuccessfulApplyCompleteAndDisposeKeepWholeTextureReplacement()
        {
            var fixture = new Fixture();
            Material generatedMaterial = null;
            try
            {
                var transaction = fixture.Rewriter.Apply(fixture.Analysis, fixture.Replacements);
                generatedMaterial = fixture.Renderer.sharedMaterials[0];
                try
                {
                    Assert.That(generatedMaterial, Is.Not.SameAs(fixture.SourceMaterial));
                    Assert.That(generatedMaterial.GetTexture("_MainTex"), Is.SameAs(fixture.GeneratedTexture));
                    Assert.That(fixture.Saver.Saved, Does.Contain(fixture.GeneratedTexture));
                    Assert.That(fixture.Saver.Saved, Does.Contain(generatedMaterial));
                    Assert.DoesNotThrow(transaction.Complete);
                    Assert.DoesNotThrow(transaction.Dispose);
                    Assert.That(fixture.Renderer.sharedMaterials[0], Is.SameAs(generatedMaterial));
                    Assert.That(fixture.GeneratedTexture == null, Is.False);
                }
                finally { transaction.Dispose(); }
            }
            finally
            {
                fixture.Dispose();
                if (generatedMaterial != null) Object.DestroyImmediate(generatedMaterial);
            }
        }

        [Test]
        public void IdentityCanonicalReplacementIsNotDestroyedByCommitRollback()
        {
            var fixture = new Fixture();
            try
            {
                var transaction = fixture.Rewriter.Apply(fixture.Analysis, fixture.Replacements,
                    Array.Empty<Texture2D>());
                try
                {
                    Assert.That(transaction.Rollback(), Is.True);
                    Assert.That(fixture.GeneratedTexture == null, Is.False,
                        "a pre-existing identity canonical is not an optimizer-owned generated texture");
                }
                finally { transaction.Dispose(); }
            }
            finally { fixture.Dispose(); }
        }

        [Test]
        public void SuccessfulApplyCanRollbackAfterLaterWholePipelineFailure()
        {
            var fixture = new Fixture();
            try
            {
                var transaction = fixture.Rewriter.Apply(fixture.Analysis, fixture.Replacements);
                try
                {
                    Assert.That(fixture.Renderer.sharedMaterials[0], Is.Not.SameAs(fixture.SourceMaterial));
                    var rollbackRestored = false;
                    Assert.Throws<InvalidOperationException>(() =>
                    {
                        try { throw new InvalidOperationException("simulated final report failure"); }
                        finally { rollbackRestored = transaction.Rollback(); }
                    });
                    Assert.That(rollbackRestored, Is.True);
                    Assert.That(fixture.Renderer.sharedMaterials[0], Is.SameAs(fixture.SourceMaterial));
                    Assert.That(fixture.GeneratedTexture == null, Is.True,
                        "a fully restored transaction may destroy its transient replacement texture");
                }
                finally { transaction.Dispose(); }
            }
            finally { fixture.Dispose(); }
        }

        [Test]
        public void IncompleteWholeApplyRollbackSignalsTextureRetention()
        {
            var saver = new DestroyRendererSaver();
            var fixture = new Fixture(saver);
            saver.Target = fixture.Renderer;
            try
            {
                LogAssert.Expect(LogType.Error,
                    new Regex("\\[ATO\\] Transaction rollback failed for current whole-texture renderer:"));

                var exception = Assert.Throws<ATORollbackIncompleteException>(() =>
                    fixture.Rewriter.Apply(fixture.Analysis, fixture.Replacements));

                Assert.That(exception.InnerException, Is.Not.Null);
                Assert.That(fixture.GeneratedTexture == null, Is.False,
                    "an incomplete whole-texture rollback must retain replacement textures still reachable from generated materials");
                Assert.That(WholeTextureOptimizer.CanDestroyGeneratedTexturesAfterBuildFailure(exception), Is.False);
                Assert.That(WholeTextureOptimizer.CanDestroyGeneratedTexturesAfterBuildFailure(
                    new InvalidOperationException("ordinary")), Is.True);
            }
            finally
            {
                fixture.Dispose();
                foreach (var material in saver.Saved.OfType<Material>().Where(value => value != null).Distinct())
                    Object.DestroyImmediate(material);
            }
        }

        [Test]
        public void WholeAssetSaveFailureNeverExposesClonedMaterial()
        {
            var fixture = new Fixture(new ThrowingSaver());
            try
            {
                Assert.Throws<InvalidOperationException>(() =>
                    fixture.Rewriter.Apply(fixture.Analysis, fixture.Replacements));

                Assert.That(fixture.Renderer.sharedMaterials[0], Is.SameAs(fixture.SourceMaterial));
            }
            finally { fixture.Dispose(); }
        }

        private sealed class Fixture : IDisposable
        {
            public readonly GameObject GameObject;
            public readonly MeshRenderer Renderer;
            public readonly Material SourceMaterial;
            public readonly Texture2D SourceTexture;
            public readonly Texture2D GeneratedTexture;
            public readonly TextureBindingRecord Binding;
            public readonly AvatarAnalysis Analysis;
            public readonly RecordingSaver Saver;
            public readonly WholeTextureRewriter Rewriter;
            public readonly Dictionary<TextureBindingRecord, Texture2D> Replacements;

            public Fixture(IAssetSaver saver = null)
            {
                var shader = Shader.Find("Unlit/Texture") ?? Shader.Find("Hidden/InternalErrorShader");
                if (shader == null) Assert.Ignore("No built-in texture shader is available for this Editor test.");
                GameObject = new GameObject("whole-transaction-test");
                Renderer = GameObject.AddComponent<MeshRenderer>();
                SourceTexture = new Texture2D(2, 2, TextureFormat.RGBA32, false, true)
                    { name = "whole-source" };
                GeneratedTexture = new Texture2D(1, 1, TextureFormat.RGBA32, false, true)
                    { name = "ATO_whole-generated" };
                SourceMaterial = new Material(shader) { name = "whole-source-material" };
                if (!SourceMaterial.HasProperty("_MainTex"))
                {
                    Dispose();
                    Assert.Ignore("The available built-in shader does not expose _MainTex.");
                }
                SourceMaterial.SetTexture("_MainTex", SourceTexture);
                Renderer.sharedMaterials = new[] { SourceMaterial };

                var rendererRecord = new RendererRecord
                    { Renderer = Renderer, Path = string.Empty };
                var slot = new MaterialSlotRecord { Slot = 0 };
                slot.Materials.Add(SourceMaterial);
                Binding = new TextureBindingRecord
                {
                    Renderer = rendererRecord,
                    Slot = slot,
                    Material = SourceMaterial,
                    PropertyName = "_MainTex",
                    Texture = SourceTexture,
                    OriginalTexture = SourceTexture,
                    IsInitialValue = true,
                    AtlasSafe = true
                };
                slot.Bindings.Add(Binding);
                rendererRecord.Slots.Add(slot);
                Analysis = new AvatarAnalysis();
                Analysis.Renderers.Add(rendererRecord);
                Replacements = new Dictionary<TextureBindingRecord, Texture2D>
                    { [Binding] = GeneratedTexture };
                Saver = saver as RecordingSaver ?? new RecordingSaver();
                Rewriter = new WholeTextureRewriter(saver ?? Saver,
                    new AnimationIndex(Array.Empty<VirtualNode>()), false);
            }

            public void Dispose()
            {
                if (GameObject != null) Object.DestroyImmediate(GameObject);
                if (SourceMaterial != null) Object.DestroyImmediate(SourceMaterial);
                if (SourceTexture != null) Object.DestroyImmediate(SourceTexture);
                if (GeneratedTexture != null) Object.DestroyImmediate(GeneratedTexture);
            }
        }

        private sealed class RecordingSaver : IAssetSaver
        {
            private readonly List<Object> _saved = new List<Object>();
            public IReadOnlyList<Object> Saved => _saved;
            public Object CurrentContainer => null;
            public void SaveAsset(Object asset) { if (asset != null) _saved.Add(asset); }
            public bool IsTemporaryAsset(Object asset) => asset == null || !EditorUtility.IsPersistent(asset);
            public IEnumerable<Object> GetPersistedAssets() => _saved;
            public void Dispose() { }
        }

        private sealed class DestroyRendererSaver : IAssetSaver
        {
            private readonly List<Object> _saved = new List<Object>();
            public Renderer Target;
            public IReadOnlyList<Object> Saved => _saved;
            public Object CurrentContainer => null;

            public void SaveAsset(Object asset)
            {
                if (asset != null) _saved.Add(asset);
                if (Target == null) return;
                var targetObject = Target.gameObject;
                Target = null;
                Object.DestroyImmediate(targetObject);
            }

            public bool IsTemporaryAsset(Object asset) => true;
            public IEnumerable<Object> GetPersistedAssets() => _saved;
            public void Dispose() { }
        }

        private sealed class ThrowingSaver : IAssetSaver
        {
            public Object CurrentContainer => null;
            public void SaveAsset(Object asset) => throw new InvalidOperationException("simulated whole asset save failure");
            public bool IsTemporaryAsset(Object asset) => true;
            public IEnumerable<Object> GetPersistedAssets() => Enumerable.Empty<Object>();
            public void Dispose() { }
        }
    }
}
