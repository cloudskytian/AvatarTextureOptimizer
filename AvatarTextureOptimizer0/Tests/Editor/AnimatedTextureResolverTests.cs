using System.Collections.Generic;
using Fosa.AvatarTextureOptimizer.Editor.Analysis;
using Fosa.AvatarTextureOptimizer.Editor.Atlas;
using NUnit.Framework;
using UnityEngine;

namespace Fosa.AvatarTextureOptimizer.Tests
{
    public sealed class AnimatedTextureResolverTests
    {
        [Test]
        public void NullAnimationKeyframeRemainsUnmapped()
        {
            var slot = new MaterialSlotRecord();
            var result = AnimatedTextureResolver.Resolve(slot, "_MainTex", null,
                new Dictionary<TextureBindingRecord, Texture2D>(), out var replacement);
            Assert.That(result, Is.EqualTo(AnimatedTextureResolution.Unmapped));
            Assert.That(replacement, Is.Null);
        }

        [Test]
        public void OriginalIdentitySurvivesCanonicalPixelDeduplication()
        {
            var canonical = NewTexture("canonical");
            var originalDuplicate = NewTexture("original-duplicate");
            var generated = NewTexture("generated");
            try
            {
                var slot = new MaterialSlotRecord();
                var binding = new TextureBindingRecord
                {
                    PropertyName = "_MainTex", Texture = canonical, OriginalTexture = originalDuplicate,
                    IsAnimatedValue = true
                };
                slot.Bindings.Add(binding);
                var replacements = new Dictionary<TextureBindingRecord, Texture2D> { [binding] = generated };

                var result = AnimatedTextureResolver.Resolve(slot, "_MainTex", originalDuplicate,
                    replacements, out var replacement);
                Assert.That(result, Is.EqualTo(AnimatedTextureResolution.Resolved));
                Assert.That(replacement, Is.SameAs(generated));

                Assert.That(AnimatedTextureResolver.Resolve(slot, "_MainTex", canonical,
                    replacements, out _), Is.EqualTo(AnimatedTextureResolution.Unmapped),
                    "the curve must be matched by its original object identity, not the canonical pixel source");
            }
            finally
            {
                Object.DestroyImmediate(canonical);
                Object.DestroyImmediate(originalDuplicate);
                Object.DestroyImmediate(generated);
            }
        }

        [Test]
        public void PartialMaterialStateMappingIsAmbiguous()
        {
            var source = NewTexture("source");
            var generated = NewTexture("generated");
            try
            {
                var slot = new MaterialSlotRecord();
                var first = Binding(source); var second = Binding(source);
                slot.Bindings.Add(first); slot.Bindings.Add(second);
                var replacements = new Dictionary<TextureBindingRecord, Texture2D> { [first] = generated };

                Assert.That(AnimatedTextureResolver.Resolve(slot, "_MainTex", source,
                    replacements, out var replacement), Is.EqualTo(AnimatedTextureResolution.Ambiguous));
                Assert.That(replacement, Is.Null);
            }
            finally
            {
                Object.DestroyImmediate(source);
                Object.DestroyImmediate(generated);
            }
        }

        [Test]
        public void AtlasPreflightRejectsOneCurveIdentityAcrossDifferentUvLayouts()
        {
            var source = NewTexture("source");
            try
            {
                var analysis = new AvatarAnalysis();
                var renderer = new RendererRecord();
                var slot = new MaterialSlotRecord();
                renderer.Slots.Add(slot); analysis.Renderers.Add(renderer);
                var first = Binding(source); var second = Binding(source);
                first.Renderer = second.Renderer = renderer; first.Slot = second.Slot = slot;
                first.UvChannel = 0; second.UvChannel = 1;
                slot.Bindings.Add(first); slot.Bindings.Add(second);
                var firstGroup = new UvGroupRecord { Id = 1, Renderer = renderer, Slot = slot, UvChannel = 0 };
                var secondGroup = new UvGroupRecord { Id = 2, Renderer = renderer, Slot = slot, UvChannel = 1 };
                firstGroup.Bindings.Add(first); secondGroup.Bindings.Add(second);
                firstGroup.Islands.Add(new UvIsland()); secondGroup.Islands.Add(new UvIsland());
                analysis.UvGroups.Add(firstGroup); analysis.UvGroups.Add(secondGroup);

                UvAnalysisStage.EnforceAnimatedTextureIdentityClosure(analysis);

                Assert.That(firstGroup.AtlasSafe, Is.False);
                Assert.That(secondGroup.AtlasSafe, Is.False);
                Assert.That(firstGroup.Islands, Is.Empty);
                Assert.That(secondGroup.Islands, Is.Empty);
                Assert.That(analysis.Fallbacks, Has.Count.EqualTo(2));
            }
            finally { Object.DestroyImmediate(source); }
        }

        [Test]
        public void AtlasPreflightKeepsOneCurveIdentityInsideOneUvLayout()
        {
            var source = NewTexture("source");
            try
            {
                var analysis = new AvatarAnalysis();
                var renderer = new RendererRecord();
                var slot = new MaterialSlotRecord();
                renderer.Slots.Add(slot); analysis.Renderers.Add(renderer);
                var first = Binding(source); var second = Binding(source);
                first.Renderer = second.Renderer = renderer; first.Slot = second.Slot = slot;
                slot.Bindings.Add(first); slot.Bindings.Add(second);
                var group = new UvGroupRecord { Id = 1, Renderer = renderer, Slot = slot, UvChannel = 0 };
                group.Bindings.Add(first); group.Bindings.Add(second); group.Islands.Add(new UvIsland());
                analysis.UvGroups.Add(group);

                UvAnalysisStage.EnforceAnimatedTextureIdentityClosure(analysis);

                Assert.That(group.AtlasSafe, Is.True);
                Assert.That(group.Islands, Has.Count.EqualTo(1));
                Assert.That(analysis.Fallbacks, Is.Empty);
            }
            finally { Object.DestroyImmediate(source); }
        }

        [Test]
        public void MultipleOutputsForOneCurveKeyframeAreAmbiguous()
        {
            var source = NewTexture("source");
            var firstOutput = NewTexture("first-output");
            var secondOutput = NewTexture("second-output");
            try
            {
                var slot = new MaterialSlotRecord();
                var first = Binding(source); var second = Binding(source);
                slot.Bindings.Add(first); slot.Bindings.Add(second);
                var replacements = new Dictionary<TextureBindingRecord, Texture2D>
                {
                    [first] = firstOutput,
                    [second] = secondOutput
                };

                Assert.That(AnimatedTextureResolver.Resolve(slot, "_MainTex", source,
                    replacements, out var replacement), Is.EqualTo(AnimatedTextureResolution.Ambiguous));
                Assert.That(replacement, Is.Null);
            }
            finally
            {
                Object.DestroyImmediate(source);
                Object.DestroyImmediate(firstOutput);
                Object.DestroyImmediate(secondOutput);
            }
        }

        private static TextureBindingRecord Binding(Texture2D source) => new TextureBindingRecord
        {
            PropertyName = "_MainTex", Texture = source, OriginalTexture = source, IsAnimatedValue = true
        };

        private static Texture2D NewTexture(string name) =>
            new Texture2D(1, 1, TextureFormat.RGBA32, false, true) { name = name };
    }
}
