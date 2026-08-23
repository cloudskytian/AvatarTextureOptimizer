using System;
using System.Collections.Generic;
using Fosa.AvatarTextureOptimizer.Editor.Analysis;
using Fosa.AvatarTextureOptimizer.Editor.Atlas;
using nadena.dev.ndmf;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Fosa.AvatarTextureOptimizer.Tests
{
    public sealed class AtlasPageFallbackTests
    {
        [Test]
        public void SameTextureDoesNotReuseAQualityProofFromAnotherBinding()
        {
            var texture = new Texture2D(1, 1);
            try
            {
                var first = new TextureBindingRecord { Texture = texture, Cutoff = 0.25f };
                var second = new TextureBindingRecord { Texture = texture, Cutoff = 0.75f };
                Assert.That(AtlasTextureGenerator.CanReuseBaseVariant(first, first), Is.True);
                Assert.That(AtlasTextureGenerator.CanReuseBaseVariant(second, first), Is.False,
                    "cutoff, alpha, and channel semantics belong to the binding, not merely the Texture2D");
                Assert.That(AtlasTextureGenerator.CanReuseBaseVariant(null, null), Is.True);
            }
            finally { Object.DestroyImmediate(texture); }
        }

        [Test]
        public void NullInitialAtlasLayersDoNotRequestUnreachableBlankOutputs()
        {
            var shader = Shader.Find("Unlit/Texture") ?? Shader.Find("Hidden/InternalErrorShader");
            if (shader == null) Assert.Ignore("No built-in shader is available for this Editor test.");
            var gameObject = new GameObject("atlas-null-output-test");
            var renderer = gameObject.AddComponent<MeshRenderer>();
            var currentMaterial = new Material(shader);
            var animatedMaterial = new Material(shader);
            var texture = new Texture2D(1, 1);
            try
            {
                renderer.sharedMaterials = new[] { currentMaterial };
                var slot = new MaterialSlotRecord { Slot = 0 };
                var group = new UvGroupRecord
                {
                    Renderer = new RendererRecord { Renderer = renderer },
                    Slot = slot
                };
                var currentLayer = new AtlasLayerBinding { Initial = null };
                var animatedBinding = new TextureBindingRecord { Texture = texture };
                var layout = new AtlasGroupLayout();
                layout.LayerKeys.Add(default);
                layout.MaterialLayers[currentMaterial] = new List<AtlasLayerBinding> { currentLayer };
                layout.MaterialLayers[animatedMaterial] = new List<AtlasLayerBinding>
                    { new AtlasLayerBinding { Initial = animatedBinding } };
                var plan = new AtlasPlan();
                plan.GroupLayouts[group] = layout;
                var page = new AtlasPage();
                page.Groups.Add(group);

                Assert.That(AtlasTextureGenerator.HasBaseLayerContent(plan, page, 0), Is.False,
                    "all-null current states must keep a null base output instead of generating a blank atlas");
                Assert.That(AtlasTextureGenerator.HasTextureContent(null), Is.False,
                    "a null material initial state must not request a variant");
                Assert.That(AtlasTextureGenerator.HasTextureContent(animatedBinding), Is.True,
                    "a non-null animated material state must still receive its independently quality-gated variant");

                currentLayer.Initial = new TextureBindingRecord { Texture = texture };
                Assert.That(AtlasTextureGenerator.HasBaseLayerContent(plan, page, 0), Is.True,
                    "one real current texture is sufficient to request the shared base layer");
            }
            finally
            {
                Object.DestroyImmediate(texture);
                Object.DestroyImmediate(currentMaterial);
                Object.DestroyImmediate(animatedMaterial);
                Object.DestroyImmediate(gameObject);
            }
        }

        [Test]
        public void LaterPageRollbackDoesNotClaimPreexistingDeduplicatedTexture()
        {
            var earlierPageTexture = new Texture2D(1, 1);
            var laterPageTexture = new Texture2D(1, 1);
            try
            {
                var preexisting = new HashSet<Texture2D> { earlierPageTexture };
                var laterPage = new AtlasBuildResult();
                // The failed page can legitimately reference the earlier page's texture in its output dictionaries.
                laterPage.BaseLayers.Add(new PageLayerKey(1, 0), earlierPageTexture);
                AtlasTextureGenerator.TrackPageTexture(earlierPageTexture, preexisting, laterPage.OwnedTextures);
                AtlasTextureGenerator.TrackPageTexture(laterPageTexture, preexisting, laterPage.OwnedTextures);

                Assert.That(laterPage.OwnedTextures.Contains(earlierPageTexture), Is.False,
                    "a failed later page must not own a shared transient object still referenced by an earlier page");
                Assert.That(laterPage.OwnedTextures.Contains(laterPageTexture), Is.True);
                laterPage.DestroyOwnedTransient();
                Assert.That(earlierPageTexture == null, Is.False,
                    "page rollback must use ownership, not every texture referenced by that page's output maps");
                Assert.That(laterPageTexture == null, Is.True);
            }
            finally
            {
                if (earlierPageTexture != null) Object.DestroyImmediate(earlierPageTexture);
                if (laterPageTexture != null) Object.DestroyImmediate(laterPageTexture);
            }
        }

        [Test]
        public void SemanticReuseNeverDestroysTextureOwnedByEarlierPage()
        {
            var semantic = new Texture2D(1, 1);
            var earlierPageTexture = new Texture2D(1, 1);
            var currentPageTexture = new Texture2D(1, 1);
            try
            {
                var preexisting = new HashSet<Texture2D> { earlierPageTexture };
                Assert.That(AtlasTextureGenerator.CanDestroySupersededSemanticTexture(
                    currentPageTexture, semantic, preexisting, true), Is.True,
                    "a superseded transient created by this exact BuildLayer call may be reclaimed");
                Assert.That(AtlasTextureGenerator.CanDestroySupersededSemanticTexture(
                    earlierPageTexture, semantic, preexisting, false), Is.False,
                    "a later page must not destroy a transient canonical object owned by an earlier page");
                Assert.That(AtlasTextureGenerator.CanDestroySupersededSemanticTexture(
                    currentPageTexture, semantic, preexisting, false), Is.False,
                    "a same-page dedup hit can be referenced elsewhere and is not this call's candidate");
                Assert.That(AtlasTextureGenerator.CanDestroySupersededSemanticTexture(
                    semantic, semantic, preexisting, true), Is.False,
                    "the selected semantic object must never destroy itself");
            }
            finally
            {
                Object.DestroyImmediate(semantic);
                Object.DestroyImmediate(earlierPageTexture);
                Object.DestroyImmediate(currentPageTexture);
            }
        }

        [Test]
        public void FinalizationFailureDestroysCandidateWithoutPublishingOwnershipOrCache()
        {
            var candidate = new Texture2D(1, 1);
            var dedup = new Dictionary<string, Texture2D>();
            var preexisting = new HashSet<Texture2D>();
            var generated = new HashSet<Texture2D>();
            try
            {
                Assert.Throws<InvalidOperationException>(() =>
                    AtlasTextureGenerator.FinalizeAndPublishTexture(candidate, "identity", dedup,
                        preexisting, generated, _ => throw new InvalidOperationException("injected finalizer failure"),
                        out _));

                Assert.That(candidate == null, Is.True);
                Assert.That(dedup, Is.Empty, "an unfinalized/destroyed Unity Object must never enter dedup");
                Assert.That(generated, Is.Empty, "failed finalization must not transfer page ownership");
            }
            finally { if (candidate != null) Object.DestroyImmediate(candidate); }
        }

        [Test]
        public void FinalizedCandidateIsTrackedBeforeItIsPublishedToDedup()
        {
            var candidate = new Texture2D(1, 1);
            var dedup = new Dictionary<string, Texture2D>();
            var preexisting = new HashSet<Texture2D>();
            var generated = new HashSet<Texture2D>();
            try
            {
                var output = AtlasTextureGenerator.FinalizeAndPublishTexture(candidate, "identity", dedup,
                    preexisting, generated, value =>
                    {
                        Assert.That(value, Is.SameAs(candidate));
                        Assert.That(generated, Is.Empty);
                        Assert.That(dedup, Is.Empty);
                    }, out var createdByCall);

                Assert.That(createdByCall, Is.True);
                Assert.That(output, Is.SameAs(candidate));
                Assert.That(generated, Does.Contain(candidate));
                Assert.That(dedup["identity"], Is.SameAs(candidate));
            }
            finally
            {
                generated.Remove(candidate); dedup.Clear();
                if (candidate != null) Object.DestroyImmediate(candidate);
            }
        }

        [Test]
        public void DedupHitReturnsCanonicalWithoutFinalizingOrOwningRedundantCandidate()
        {
            var canonical = new Texture2D(1, 1);
            var candidate = new Texture2D(1, 1);
            var dedup = new Dictionary<string, Texture2D> { { "identity", canonical } };
            var preexisting = new HashSet<Texture2D>();
            var generated = new HashSet<Texture2D> { canonical };
            var finalizerCalled = false;
            try
            {
                var output = AtlasTextureGenerator.FinalizeAndPublishTexture(candidate, "identity", dedup,
                    preexisting, generated, _ => finalizerCalled = true, out var createdByCall);

                Assert.That(createdByCall, Is.False);
                Assert.That(finalizerCalled, Is.False, "a redundant candidate need not be finalized");
                Assert.That(candidate == null, Is.True);
                Assert.That(output, Is.SameAs(canonical));
                Assert.That(generated.SetEquals(new[] { canonical }), Is.True,
                    "same-page canonical ownership must remain with its original acquisition");
                Assert.That(dedup["identity"], Is.SameAs(canonical));
            }
            finally
            {
                if (candidate != null) Object.DestroyImmediate(candidate);
                if (canonical != null) Object.DestroyImmediate(canonical);
            }
        }

        [Test]
        public void IncompletePageIsRejectedBeforeAnyAssetIsPersisted()
        {
            if (!SystemInfo.supportsComputeShaders)
                Assert.Ignore("The active Editor graphics device has no compute support");

            var settings = new ATOOptimizationSettings();
            settings.opaque.mipmapsAndStreaming = true;
            var analysis = new AvatarAnalysis();
            var group = new UvGroupRecord { Id = 1 };
            analysis.UvGroups.Add(group);
            var page = new AtlasPage { Id = 3, Size = Vector2Int.one };
            page.Groups.Add(group);
            var plan = new AtlasPlan();
            plan.Pages.Add(page);
            var layout = new AtlasGroupLayout();
            layout.LayerKeys.Add(new TextureTypeKey(ATOTextureKind.ColorOpaque, false,
                FilterMode.Bilinear, 1, 0f));
            plan.GroupLayouts.Add(group, layout);
            var saver = new RecordingAssetSaver();

            using (var generator = new AtlasTextureGenerator(settings, saver))
            {
                var result = generator.Generate(analysis, plan);
                Assert.That(result.BaseLayers, Is.Empty);
            }

            Assert.That(plan.Pages, Is.Empty, "the rejected page must not reach mesh or material commit");
            Assert.That(group.AtlasSafe, Is.False);
            Assert.That(analysis.Fallbacks, Has.Count.EqualTo(1));
            Assert.That(saver.Saved, Is.Empty,
                "IAssetSaver has no rollback API, so a page must pass all gates before its first SaveAsset call");
        }

        private sealed class RecordingAssetSaver : IAssetSaver
        {
            public readonly List<Object> Saved = new List<Object>();
            public Object CurrentContainer => null;
            public void SaveAsset(Object asset) { if (asset != null) Saved.Add(asset); }
            public bool IsTemporaryAsset(Object asset) => asset == null || !UnityEditor.EditorUtility.IsPersistent(asset);
            public IEnumerable<Object> GetPersistedAssets() => Saved;
            public void Dispose() { }
        }
    }
}
