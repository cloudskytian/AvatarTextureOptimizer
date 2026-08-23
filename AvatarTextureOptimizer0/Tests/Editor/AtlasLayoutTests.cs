using Fosa.AvatarTextureOptimizer.Editor.Analysis;
using Fosa.AvatarTextureOptimizer.Editor.Atlas;
using NUnit.Framework;
using UnityEngine;

namespace Fosa.AvatarTextureOptimizer.Tests
{
    public sealed class AtlasLayoutTests
    {
        [Test]
        public void PageSignatureAllowsDifferentPropertyNamesAcrossIndependentGroups()
        {
            var shader = Shader.Find("Hidden/InternalErrorShader");
            if (shader == null) Assert.Ignore("Unity internal error shader is unavailable.");
            var firstMaterial = new Material(shader);
            var secondMaterial = new Material(shader);
            var firstTexture = NewTexture(FilterMode.Bilinear);
            var secondTexture = NewTexture(FilterMode.Bilinear);
            try
            {
                var first = CreateSingleLayerGroup(firstMaterial, firstTexture, "_MainTex");
                var second = CreateSingleLayerGroup(secondMaterial, secondTexture, "_BaseMap");

                Assert.That(AtlasLayoutAnalyzer.TryCreate(first, out var firstLayout, out var firstFailure),
                    Is.True, firstFailure);
                Assert.That(AtlasLayoutAnalyzer.TryCreate(second, out var secondLayout, out var secondFailure),
                    Is.True, secondFailure);

                Assert.That(firstLayout.Signature, Is.EqualTo(secondLayout.Signature),
                    "a page layer is typed by texture semantics, not by the shader's property spelling");
                Assert.That(firstLayout.MaterialLayers[firstMaterial][0].PropertyName, Is.EqualTo("_MainTex"));
                Assert.That(secondLayout.MaterialLayers[secondMaterial][0].PropertyName, Is.EqualTo("_BaseMap"));
            }
            finally
            {
                Object.DestroyImmediate(firstMaterial); Object.DestroyImmediate(secondMaterial);
                Object.DestroyImmediate(firstTexture); Object.DestroyImmediate(secondTexture);
            }
        }

        [Test]
        public void AnimatedMaterialStatesWithDifferentPropertySchemasFallBack()
        {
            var shader = Shader.Find("Hidden/InternalErrorShader");
            if (shader == null) Assert.Ignore("Unity internal error shader is unavailable.");
            var firstMaterial = new Material(shader);
            var secondMaterial = new Material(shader);
            var texture = NewTexture(FilterMode.Bilinear);
            try
            {
                var slot = new MaterialSlotRecord();
                slot.Materials.Add(firstMaterial); slot.Materials.Add(secondMaterial);
                var group = new UvGroupRecord { Slot = slot };
                group.Bindings.Add(NewBinding(firstMaterial, texture, "_MainTex", true, false));
                group.Bindings.Add(NewBinding(secondMaterial, texture, "_BaseMap", true, false));

                Assert.That(AtlasLayoutAnalyzer.TryCreate(group, out _, out var failure), Is.False);
                Assert.That(failure, Does.Contain("incompatible texture property"));
            }
            finally
            {
                Object.DestroyImmediate(firstMaterial); Object.DestroyImmediate(secondMaterial);
                Object.DestroyImmediate(texture);
            }
        }

        [Test]
        public void NullInitialTextureWithAnimatedValuesKeepsAnEmptyInitialLayer()
        {
            var shader = Shader.Find("Hidden/InternalErrorShader");
            if (shader == null) Assert.Ignore("Unity internal error shader is unavailable.");
            var material = new Material(shader);
            var animated = NewTexture(FilterMode.Bilinear);
            try
            {
                var slot = new MaterialSlotRecord(); slot.Materials.Add(material);
                var group = new UvGroupRecord { Slot = slot };
                group.Bindings.Add(NewBinding(material, animated, "_MainTex", false, true));

                Assert.That(AtlasLayoutAnalyzer.TryCreate(group, out var layout, out var failure),
                    Is.True, failure);
                Assert.That(layout.MaterialLayers[material][0].Initial, Is.Null,
                    "a null initial material reference must not be replaced by an animated keyframe value");
                Assert.That(layout.MaterialLayers[material][0].AnimatedValues.Count, Is.EqualTo(1));
                Assert.That(layout.MaterialLayers[material][0].AnimatedValues[0], Is.SameAs(group.Bindings[0]));
            }
            finally
            {
                Object.DestroyImmediate(material); Object.DestroyImmediate(animated);
            }
        }

        [Test]
        public void AnimatedTextureCannotChangeEffectiveSamplingState()
        {
            var shader = Shader.Find("Hidden/InternalErrorShader");
            if (shader == null) Assert.Ignore("Unity internal error shader is unavailable.");
            var material = new Material(shader);
            var initial = NewTexture(FilterMode.Bilinear);
            var animated = NewTexture(FilterMode.Point);
            try
            {
                var slot = new MaterialSlotRecord(); slot.Materials.Add(material);
                var group = new UvGroupRecord { Slot = slot };
                group.Bindings.Add(NewBinding(material, initial, "_MainTex", true, false));
                group.Bindings.Add(NewBinding(material, animated, "_MainTex", false, true));

                Assert.That(AtlasLayoutAnalyzer.TryCreate(group, out _, out var failure), Is.False);
                Assert.That(failure, Does.Contain("effective sampling state"));
            }
            finally
            {
                Object.DestroyImmediate(material);
                Object.DestroyImmediate(initial); Object.DestroyImmediate(animated);
            }
        }

        private static UvGroupRecord CreateSingleLayerGroup(Material material, Texture2D texture, string property)
        {
            var slot = new MaterialSlotRecord(); slot.Materials.Add(material);
            var group = new UvGroupRecord { Slot = slot };
            group.Bindings.Add(NewBinding(material, texture, property, true, false));
            return group;
        }

        private static TextureBindingRecord NewBinding(Material material, Texture2D texture, string property,
            bool initial, bool animated) => new TextureBindingRecord
        {
            Material = material,
            Texture = texture,
            PropertyName = property,
            Kind = ATOTextureKind.ColorOpaque,
            IsInitialValue = initial,
            IsAnimatedValue = animated
        };

        private static Texture2D NewTexture(FilterMode filterMode) =>
            new Texture2D(4, 4, TextureFormat.RGBA32, false, true)
            {
                filterMode = filterMode,
                anisoLevel = 1,
                mipMapBias = 0f
            };
    }
}
