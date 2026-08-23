using Fosa.AvatarTextureOptimizer.Editor.Analysis;
using NUnit.Framework;
using UnityEngine;

namespace Fosa.AvatarTextureOptimizer.Tests
{
    public sealed class TextureDeduplicationTests
    {
        [Test]
        public void NullWhitelistRootsResolveToEmptySetAtBuildBoundary()
        {
            Assert.That(WhitelistResolver.Resolve(null), Is.Empty,
                "a null public whitelist must mean no exclusions rather than aborting the build");
        }

        [Test]
        public void DisabledDeduplicationPreservesDistinctTextureIdentityAndWhitelistScope()
        {
            var first = NewTexture(); var second = NewTexture();
            try
            {
                var analysis = BuildAnalysis(first, second);
                analysis.WhitelistedTextures.Add(first);
                analysis.Renderers[0].Slots[0].Bindings[0].Whitelisted = true;
                analysis.Renderers[0].Slots[0].Bindings[0].AtlasSafe = false;

                AvatarAnalyzer.BuildDeduplicationMap(analysis, false);
                AvatarAnalyzer.PromoteWhitelistAcrossDuplicates(analysis);

                Assert.That(analysis.CanonicalTextures[first], Is.SameAs(first));
                Assert.That(analysis.CanonicalTextures[second], Is.SameAs(second));
                Assert.That(analysis.Renderers[0].Slots[0].Bindings[1].AtlasSafe, Is.True,
                    "disabling texture deduplication must not expand one texture's whitelist to a distinct object");
            }
            finally
            {
                Object.DestroyImmediate(first); Object.DestroyImmediate(second);
            }
        }

        [Test]
        public void EnabledDeduplicationCanonicalizesExactDuplicatesAndPromotesWhitelist()
        {
            var first = NewTexture(); var second = NewTexture();
            try
            {
                var analysis = BuildAnalysis(first, second);
                analysis.WhitelistedTextures.Add(first);
                analysis.Renderers[0].Slots[0].Bindings[0].Whitelisted = true;
                analysis.Renderers[0].Slots[0].Bindings[0].AtlasSafe = false;

                AvatarAnalyzer.BuildDeduplicationMap(analysis, true);
                AvatarAnalyzer.PromoteWhitelistAcrossDuplicates(analysis);

                Assert.That(analysis.CanonicalTextures[second], Is.SameAs(first));
                Assert.That(analysis.Renderers[0].Slots[0].Bindings[1].Texture, Is.SameAs(first));
                Assert.That(analysis.Renderers[0].Slots[0].Bindings[1].AtlasSafe, Is.False);
                Assert.That(analysis.Renderers[0].Slots[0].Bindings[1].Whitelisted, Is.True);
            }
            finally
            {
                Object.DestroyImmediate(first); Object.DestroyImmediate(second);
            }
        }

        private static AvatarAnalysis BuildAnalysis(Texture2D first, Texture2D second)
        {
            var analysis = new AvatarAnalysis();
            var renderer = new RendererRecord();
            var slot = new MaterialSlotRecord { Slot = 0 };
            slot.Bindings.Add(new TextureBindingRecord { Texture = first, OriginalTexture = first, AtlasSafe = true });
            slot.Bindings.Add(new TextureBindingRecord { Texture = second, OriginalTexture = second, AtlasSafe = true });
            renderer.Slots.Add(slot); analysis.Renderers.Add(renderer);
            return analysis;
        }

        private static Texture2D NewTexture()
        {
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false, true);
            texture.SetPixels(new[] { Color.red, Color.green, Color.blue, Color.white });
            texture.Apply(false, false);
            return texture;
        }
    }
}
