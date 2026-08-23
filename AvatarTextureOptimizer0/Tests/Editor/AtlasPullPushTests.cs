using Fosa.AvatarTextureOptimizer.Editor.Atlas;
using Fosa.AvatarTextureOptimizer.Editor.Quality;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace Fosa.AvatarTextureOptimizer.Tests
{
    public sealed class AtlasPullPushTests
    {
        [Test]
        public void OddRightTopEdgeSurvivesActualComputePullPush()
        {
            if (!SystemInfo.supportsComputeShaders) Assert.Ignore("The active Editor graphics device has no compute support");

            var size = new Vector2Int(5, 3);
            var color = CreateRenderTexture(size, GraphicsFormat.R16G16B16A16_SFloat, "ATO_Test_Color");
            var validity = CreateRenderTexture(size, GraphicsFormat.R8_UNorm, "ATO_Test_Validity");
            RenderTexture result = null;
            var colorUpload = new Texture2D(size.x, size.y, TextureFormat.RGBAFloat, false, true);
            var validityUpload = new Texture2D(size.x, size.y, TextureFormat.R8, false, true);
            var readback = new Texture2D(size.x, size.y, TextureFormat.RGBAFloat, false, true);
            try
            {
                var colors = new Color[size.x * size.y];
                colors[(size.y - 1) * size.x + size.x - 1] = new Color(0.75f, 0.25f, 0.5f, 1f);
                colorUpload.SetPixels(colors);
                colorUpload.Apply(false, false);

                var validities = new byte[size.x * size.y];
                validities[validities.Length - 1] = 255;
                validityUpload.SetPixelData(validities, 0);
                validityUpload.Apply(false, false);
                Graphics.Blit(colorUpload, color);
                Graphics.Blit(validityUpload, validity);

                using (var generator = new AtlasTextureGenerator(null, null))
                    result = generator.PullPushForTests(color, validity, size, ATOTextureKind.ColorOpaque);

                var previous = RenderTexture.active;
                try
                {
                    RenderTexture.active = result;
                    readback.ReadPixels(new Rect(0, 0, size.x, size.y), 0, 0, false);
                    readback.Apply(false, false);
                }
                finally
                {
                    RenderTexture.active = previous;
                }

                foreach (var pixel in readback.GetPixels())
                {
                    Assert.That(pixel.r, Is.EqualTo(0.75f).Within(0.002f));
                    Assert.That(pixel.g, Is.EqualTo(0.25f).Within(0.002f));
                    Assert.That(pixel.b, Is.EqualTo(0.5f).Within(0.002f));
                    Assert.That(pixel.a, Is.EqualTo(1f).Within(0.002f));
                }
            }
            finally
            {
                if (result != null) { result.Release(); Object.DestroyImmediate(result); }
                color.Release(); validity.Release();
                Object.DestroyImmediate(color); Object.DestroyImmediate(validity);
                Object.DestroyImmediate(colorUpload); Object.DestroyImmediate(validityUpload);
                Object.DestroyImmediate(readback);
            }
        }

        [Test]
        public void RotatedNonSquareCopyRotatesAsymmetricPixelsAndMaskTogether()
        {
            if (!SystemInfo.supportsComputeShaders)
                Assert.Ignore("The active Editor graphics device has no compute support");

            var source = new Texture2D(2, 3, TextureFormat.RGBAHalf, false, true);
            var mask = new Texture2D(2, 3, TextureFormat.R8, false, true);
            var readback = new Texture2D(3, 2, TextureFormat.RGBAHalf, false, true);
            RenderTexture result = null;
            try
            {
                source.SetPixels(new[]
                {
                    new Color(1f, 0f, 0f, 1f), new Color(2f, 0f, 0f, 1f),
                    new Color(3f, 0f, 0f, 1f), new Color(4f, 0f, 0f, 1f),
                    new Color(5f, 0f, 0f, 1f), new Color(6f, 0f, 0f, 1f)
                });
                source.Apply(false, false);
                // Select source (0,0), (1,1), and (0,2). A clockwise rotation must put them at
                // destination (2,0), (1,1), and (0,0), while every other texel remains invalid/clear.
                mask.SetPixelData(new byte[] { 255, 0, 0, 255, 255, 0 }, 0);
                mask.Apply(false, false);

                using (var generator = new AtlasTextureGenerator(null, null))
                    result = generator.CopyMaskedForTests(source, mask, new Vector2Int(2, 3), true);

                var previous = RenderTexture.active;
                try
                {
                    RenderTexture.active = result;
                    readback.ReadPixels(new Rect(0, 0, 3, 2), 0, 0, false);
                    readback.Apply(false, false);
                }
                finally { RenderTexture.active = previous; }

                var pixels = readback.GetPixels();
                Assert.That(pixels[0].r, Is.EqualTo(5f).Within(0.01f));
                Assert.That(pixels[1].a, Is.EqualTo(0f).Within(0.01f));
                Assert.That(pixels[2].r, Is.EqualTo(1f).Within(0.01f));
                Assert.That(pixels[3].a, Is.EqualTo(0f).Within(0.01f));
                Assert.That(pixels[4].r, Is.EqualTo(4f).Within(0.01f));
                Assert.That(pixels[5].a, Is.EqualTo(0f).Within(0.01f));
            }
            finally
            {
                GpuLinearResampler.Release(result);
                Object.DestroyImmediate(source);
                Object.DestroyImmediate(mask);
                Object.DestroyImmediate(readback);
            }
        }

        [Test]
        public void OnePixelPullPushReturnsIndependentOwnership()
        {
            if (!SystemInfo.supportsComputeShaders) Assert.Ignore("The active Editor graphics device has no compute support");
            var size = Vector2Int.one;
            var color = CreateRenderTexture(size, GraphicsFormat.R16G16B16A16_SFloat, "ATO_Test_One_Color");
            var validity = CreateRenderTexture(size, GraphicsFormat.R8_UNorm, "ATO_Test_One_Validity");
            RenderTexture result = null;
            try
            {
                using (var generator = new AtlasTextureGenerator(null, null))
                    result = generator.PullPushForTests(color, validity, size, ATOTextureKind.ColorOpaque);
                Assert.That(result, Is.Not.SameAs(color));
                Assert.That(result.IsCreated(), Is.True);
                GpuLinearResampler.Release(result);
                result = null;
                Assert.That(color.IsCreated(), Is.True,
                    "releasing the returned texture must not release the caller-owned base color");
            }
            finally
            {
                GpuLinearResampler.Release(result);
                GpuLinearResampler.Release(color);
                GpuLinearResampler.Release(validity);
            }
        }

        private static RenderTexture CreateRenderTexture(Vector2Int size, GraphicsFormat format, string name)
        {
            var texture = new RenderTexture(size.x, size.y, 0)
            {
                graphicsFormat = format,
                enableRandomWrite = true,
                useMipMap = false,
                autoGenerateMips = false,
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
                name = name
            };
            if (!texture.Create())
            {
                Object.DestroyImmediate(texture);
                Assert.Fail("Could not allocate compute test texture " + name);
            }
            return texture;
        }
    }
}
