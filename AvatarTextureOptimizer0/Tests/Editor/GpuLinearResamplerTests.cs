using Fosa.AvatarTextureOptimizer.Editor.Quality;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace Fosa.AvatarTextureOptimizer.Tests
{
    public sealed class GpuLinearResamplerTests
    {
        [Test]
        public void PackedRgbaDataDoesNotPremultiplyRgbByAlpha()
        {
            if (!SystemInfo.supportsComputeShaders || !SystemInfo.supportsAsyncGPUReadback)
                Assert.Ignore("Compute shaders or asynchronous GPU readback are unavailable.");
            var source = new Texture2D(2, 1, TextureFormat.RGBA32, false, true);
            RenderTexture result = null;
            NativeArray<float4> pixels = default;
            try
            {
                source.SetPixels(new[] { new Color(1f, 0f, 0f, 0f), new Color(0f, 0f, 1f, 1f) });
                source.Apply(false, false);
                using (var resampler = new GpuLinearResampler())
                {
                    result = resampler.Resample(source, new Rect(0f, 0f, 1f, 1f), Vector2Int.one,
                        false, false, true, ATOTextureKind.ColorRgbaData);
                    pixels = resampler.Readback(result, Allocator.Temp);
                }
                Assert.That(pixels[0].x, Is.EqualTo(0.5f).Within(0.01f));
                Assert.That(pixels[0].z, Is.EqualTo(0.5f).Within(0.01f));
                Assert.That(pixels[0].w, Is.EqualTo(0.5f).Within(0.01f));
            }
            finally
            {
                if (pixels.IsCreated) pixels.Dispose();
                GpuLinearResampler.Release(result);
                Object.DestroyImmediate(source);
            }
        }

        [Test]
        public void ExplicitSourceMipReadsPersistedLevelInsteadOfMipZero()
        {
            if (!SystemInfo.supportsComputeShaders || !SystemInfo.supportsAsyncGPUReadback)
                Assert.Ignore("Compute shaders or asynchronous GPU readback are unavailable.");
            var source = new Texture2D(4, 4, TextureFormat.RGBA32, true, true);
            RenderTexture result = null;
            NativeArray<float4> pixels = default;
            try
            {
                source.SetPixels(new Color[16]);
                source.SetPixels(new[] { Color.green, Color.green, Color.green, Color.green }, 1);
                source.SetPixel(0, 0, Color.blue, 2);
                source.Apply(false, false);
                using (var resampler = new GpuLinearResampler())
                {
                    result = resampler.Resample(source, new Rect(0f, 0f, 1f, 1f),
                        new Vector2Int(2, 2), true, false, false, ATOTextureKind.ColorOpaque,
                        ATONormalInputEncoding.Imported, false, 1);
                    pixels = resampler.Readback(result, Allocator.Temp);
                }
                foreach (var pixel in pixels)
                {
                    Assert.That(pixel.x, Is.LessThan(0.01f));
                    Assert.That(pixel.y, Is.EqualTo(1f).Within(0.01f));
                    Assert.That(pixel.z, Is.LessThan(0.01f));
                }
            }
            finally
            {
                if (pixels.IsCreated) pixels.Dispose();
                GpuLinearResampler.Release(result);
                Object.DestroyImmediate(source);
            }
        }

        [Test]
        public void RotatedAtlasRegionReconstructsSourceOrientation()
        {
            if (!SystemInfo.supportsComputeShaders || !SystemInfo.supportsAsyncGPUReadback)
                Assert.Ignore("Compute shaders or asynchronous GPU readback are unavailable.");
            var sourcePixels = new[]
            {
                new Color(0.1f, 0f, 0f, 1f), new Color(0.2f, 0f, 0f, 1f),
                new Color(0.3f, 0f, 0f, 1f), new Color(0.4f, 0f, 0f, 1f),
                new Color(0.5f, 0f, 0f, 1f), new Color(0.6f, 0f, 0f, 1f)
            };
            // CopyMasked maps source (u,v) into the packed rectangle as (1-v,u).
            var packed = new Texture2D(3, 2, TextureFormat.RGBA32, false, true);
            RenderTexture result = null;
            NativeArray<float4> pixels = default;
            try
            {
                packed.SetPixels(new[]
                {
                    sourcePixels[4], sourcePixels[2], sourcePixels[0],
                    sourcePixels[5], sourcePixels[3], sourcePixels[1]
                });
                packed.Apply(false, false);
                using (var resampler = new GpuLinearResampler())
                {
                    result = resampler.Resample(packed, new Rect(0f, 0f, 1f, 1f),
                        new Vector2Int(2, 3), true, false, false, ATOTextureKind.ColorOpaque,
                        ATONormalInputEncoding.Imported, true);
                    pixels = resampler.Readback(result, Allocator.Temp);
                }
                for (var index = 0; index < sourcePixels.Length; index++)
                    Assert.That(pixels[index].x, Is.EqualTo(sourcePixels[index].r).Within(0.01f));
            }
            finally
            {
                if (pixels.IsCreated) pixels.Dispose();
                GpuLinearResampler.Release(result);
                Object.DestroyImmediate(packed);
            }
        }

        [Test]
        public void ExplicitRgba32ReadbackPreservesPixelOrderAndEncodesSrgbOnce()
        {
            if (!SystemInfo.supportsComputeShaders || !SystemInfo.supportsAsyncGPUReadback)
                Assert.Ignore("Compute shaders or asynchronous GPU readback are unavailable.");
            var source = new Texture2D(2, 2, TextureFormat.RGBAHalf, false, true);
            var linear = new Texture2D(2, 2, TextureFormat.RGBA32, false, true);
            var srgb = new Texture2D(2, 2, TextureFormat.RGBA32, false, false);
            RenderTexture result = null;
            try
            {
                source.SetPixels(new[]
                {
                    new Color(0f, 0f, 0f, 0f), new Color(0.25f, 0f, 0f, 0.25f),
                    new Color(0.5f, 0f, 0f, 0.5f), new Color(1f, 0f, 0f, 1f)
                });
                source.Apply(false, false);
                using (var resampler = new GpuLinearResampler())
                    result = resampler.Resample(source, new Rect(0f, 0f, 1f, 1f), new Vector2Int(2, 2),
                        true, false, false, ATOTextureKind.ColorRgbaData);

                GpuLinearResampler.CopyToRgba32(result, linear, 0, false);
                GpuLinearResampler.CopyToRgba32(result, srgb, 0, true);
                var linearBytes = linear.GetPixelData<Color32>(0);
                var srgbBytes = srgb.GetPixelData<Color32>(0);
                CollectionAssert.AreEqual(new byte[] { 0, 64, 128, 255 },
                    new[] { linearBytes[0].r, linearBytes[1].r, linearBytes[2].r, linearBytes[3].r });
                CollectionAssert.AreEqual(new byte[] { 0, 137, 188, 255 },
                    new[] { srgbBytes[0].r, srgbBytes[1].r, srgbBytes[2].r, srgbBytes[3].r });
                CollectionAssert.AreEqual(new byte[] { 0, 64, 128, 255 },
                    new[] { srgbBytes[0].a, srgbBytes[1].a, srgbBytes[2].a, srgbBytes[3].a });
            }
            finally
            {
                GpuLinearResampler.Release(result);
                Object.DestroyImmediate(source); Object.DestroyImmediate(linear); Object.DestroyImmediate(srgb);
            }
        }

        [TestCase((int)ATONormalInputEncoding.EncodedRgb)]
        [TestCase((int)ATONormalInputEncoding.EncodedRgOrAg)]
        [TestCase((int)ATONormalInputEncoding.EncodedAg)]
        public void GeneratedNormalLayoutsDecodeToEncodedRgb(int encodingValue)
        {
            var encoding = (ATONormalInputEncoding)encodingValue;
            if (!SystemInfo.supportsComputeShaders || !SystemInfo.supportsAsyncGPUReadback)
                Assert.Ignore("Compute shaders or asynchronous GPU readback are unavailable.");
            var expected = new Vector3(0.3f, -0.4f, Mathf.Sqrt(0.75f));
            var x = expected.x * 0.5f + 0.5f;
            var y = expected.y * 0.5f + 0.5f;
            var z = expected.z * 0.5f + 0.5f;
            var stored = encoding == ATONormalInputEncoding.EncodedAg
                ? new Color(1f, y, 1f, x)
                : new Color(x, y, z, 1f);
            var source = new Texture2D(1, 1, TextureFormat.RGBA32, false, true);
            RenderTexture result = null;
            NativeArray<float4> pixels = default;
            try
            {
                source.SetPixel(0, 0, stored);
                source.Apply(false, false);
                using (var resampler = new GpuLinearResampler())
                {
                    result = resampler.Resample(source, new Rect(0f, 0f, 1f, 1f), Vector2Int.one,
                        true, false, false, ATOTextureKind.Normal, encoding);
                    pixels = resampler.Readback(result, Allocator.Temp);
                }

                var actual = new Vector3(pixels[0].x * 2f - 1f, pixels[0].y * 2f - 1f,
                    pixels[0].z * 2f - 1f).normalized;
                Assert.That(Vector3.Angle(expected, actual), Is.LessThan(0.25f));
            }
            finally
            {
                if (pixels.IsCreated) pixels.Dispose();
                GpuLinearResampler.Release(result);
                Object.DestroyImmediate(source);
            }
        }
    }
}
