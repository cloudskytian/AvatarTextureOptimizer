using Fosa.AvatarTextureOptimizer.Editor.Analysis;
using Fosa.AvatarTextureOptimizer.Editor.Atlas;
using Fosa.AvatarTextureOptimizer.Editor.Quality;
using NUnit.Framework;
using UnityEngine;

namespace Fosa.AvatarTextureOptimizer.Tests
{
    public sealed class TextureMipChainTests
    {
        [Test]
        public void PackedRgbaDataUsesAlphaPreservingClassSettings()
        {
            var settings = new ATOOptimizationSettings();
            settings.alpha.compression = ATOCompression.UncompressedRGBA32;
            settings.alpha.mipmapsAndStreaming = false;
            var selected = TextureFormatResolver.ClassSettings(ATOTextureKind.ColorRgbaData, settings);
            Assert.That(selected, Is.SameAs(settings.alpha));
            Assert.That(selected.compression, Is.EqualTo(ATOCompression.UncompressedRGBA32));
        }

        [Test]
        public void WholeTextureMipReductionUsesExactSharedLodOffset()
        {
            var source = new Texture2D(1024, 512, TextureFormat.RGBA32, true, true);
            try
            {
                Assert.That(WholeTextureOptimizer.SelectSourceMipReduction(source, new Vector2Int(500, 200)),
                    Is.EqualTo(1));
                Assert.That(WholeTextureOptimizer.SelectSourceMipReduction(source, new Vector2Int(500, 300)),
                    Is.EqualTo(0));
                Assert.That(WholeTextureOptimizer.SelectSourceMipReduction(source, new Vector2Int(120, 60)),
                    Is.EqualTo(3));
            }
            finally { Object.DestroyImmediate(source); }
        }

        [Test]
        public void WholeTextureMipReductionStopsBeforeInexactNpotLodOffset()
        {
            var source = new Texture2D(6, 10, TextureFormat.RGBA32, true, true);
            try
            {
                // 6x10 -> 3x5 is exactly 2x, but selecting 1x2 as a two-level base would imply
                // a 4x8 source rather than 6x10 when interpreted as an unchanged LOD offset.
                Assert.That(WholeTextureOptimizer.SelectSourceMipReduction(source, Vector2Int.one),
                    Is.EqualTo(1));
            }
            finally { Object.DestroyImmediate(source); }
        }

        [Test]
        public void AtlasMipOffsetRequiresSameExactPotReductionOnBothAxes()
        {
            var source = new Texture2D(1024, 1024, TextureFormat.RGBA32, true, true);
            var island = new UvIsland { UvBounds = new Rect(0f, 0f, 0.5f, 0.25f) };
            try
            {
                Assert.That(AtlasTextureGenerator.TryGetExactSourceMipOffset(source, island,
                    new Vector2Int(256, 128), out var offset), Is.True);
                Assert.That(offset, Is.EqualTo(1));
                Assert.That(AtlasTextureGenerator.TryGetExactSourceMipOffset(source, island,
                    new Vector2Int(256, 127), out _), Is.False);
            }
            finally { Object.DestroyImmediate(source); }
        }

        [Test]
        public void PullPushChainRetainsOddEdges()
        {
            var first = AtlasTextureGenerator.NextPullSize(new Vector2Int(5, 3));
            var second = AtlasTextureGenerator.NextPullSize(first);
            var third = AtlasTextureGenerator.NextPullSize(second);
            Assert.AreEqual(new Vector2Int(3, 2), first);
            Assert.AreEqual(new Vector2Int(2, 1), second);
            Assert.AreEqual(Vector2Int.one, third);
        }

        [Test]
        public void AlphaMipUsesPremultipliedColorWeights()
        {
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, true, true);
            try
            {
                texture.SetPixelData(new[]
                {
                    new Color32(255, 0, 0, 255),
                    new Color32(0, 255, 0, 0),
                    new Color32(0, 0, 255, 0),
                    new Color32(255, 255, 255, 0)
                }, 0);
                texture.Apply(false, false);

                TextureFormatResolver.BuildPremultipliedAlphaMipChain(texture, false);

                var mip = texture.GetPixelData<Color32>(1)[0];
                Assert.That(mip.r, Is.EqualTo(255));
                Assert.That(mip.g, Is.EqualTo(0));
                Assert.That(mip.b, Is.EqualTo(0));
                Assert.That(mip.a, Is.EqualTo(64).Within(1));
            }
            finally
            {
                Object.DestroyImmediate(texture);
            }
        }

        [Test]
        public void FullyTransparentMipKeepsExtrapolatedHiddenColor()
        {
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, true, true);
            try
            {
                texture.SetPixelData(new[]
                {
                    new Color32(255, 0, 0, 0), new Color32(255, 0, 0, 0),
                    new Color32(0, 0, 255, 0), new Color32(0, 0, 255, 0)
                }, 0);
                texture.Apply(false, false);

                TextureFormatResolver.BuildPremultipliedAlphaMipChain(texture, false);

                var mip = texture.GetPixelData<Color32>(1)[0];
                Assert.That(mip.r, Is.EqualTo(128).Within(1));
                Assert.That(mip.g, Is.EqualTo(0));
                Assert.That(mip.b, Is.EqualTo(128).Within(1));
                Assert.That(mip.a, Is.EqualTo(0));
            }
            finally
            {
                Object.DestroyImmediate(texture);
            }
        }

        [Test]
        public void SrgbMipAveragesInLinearLight()
        {
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, true, false);
            try
            {
                texture.SetPixelData(new[]
                {
                    new Color32(0, 0, 0, 0), new Color32(0, 0, 0, 0),
                    new Color32(255, 255, 255, 0), new Color32(255, 255, 255, 0)
                }, 0);
                texture.Apply(false, false);

                TextureFormatResolver.BuildPremultipliedAlphaMipChain(texture, true);

                var mip = texture.GetPixelData<Color32>(1)[0];
                Assert.That(mip.r, Is.EqualTo(188).Within(1));
                Assert.That(mip.g, Is.EqualTo(188).Within(1));
                Assert.That(mip.b, Is.EqualTo(188).Within(1));
                Assert.That(mip.a, Is.EqualTo(0));
            }
            finally
            {
                Object.DestroyImmediate(texture);
            }
        }

        [TestCase((int)ATONormalInputEncoding.EncodedRgb)]
        [TestCase((int)ATONormalInputEncoding.EncodedRgOrAg)]
        [TestCase((int)ATONormalInputEncoding.EncodedAg)]
        public void NormalEncodingRoundTripsEveryMip(int encodingValue)
        {
            var encoding = (ATONormalInputEncoding)encodingValue;
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, true, true);
            var expected = new Vector3(0.3f, -0.4f, Mathf.Sqrt(0.75f));
            var encoded = new Color(expected.x * 0.5f + 0.5f, expected.y * 0.5f + 0.5f,
                expected.z * 0.5f + 0.5f, 1f);
            try
            {
                texture.SetPixels(new[] { encoded, encoded, encoded, encoded }, 0);
                texture.Apply(true, false);

                TextureFormatResolver.EncodeNormalMipChain(texture, encoding);

                for (var mip = 0; mip < texture.mipmapCount; mip++)
                {
                    var stored = texture.GetPixels(mip)[0];
                    var actual = DecodeNormal(stored, encoding);
                    Assert.That(Vector3.Angle(expected, actual), Is.LessThan(0.25f), "mip " + mip);
                    if (encoding == ATONormalInputEncoding.EncodedAg)
                    {
                        Assert.That(stored.r, Is.EqualTo(1f).Within(1f / 255f));
                        Assert.That(stored.b, Is.EqualTo(1f).Within(1f / 255f));
                    }
                    else
                    {
                        Assert.That(stored.a, Is.EqualTo(1f).Within(1f / 255f));
                    }
                }
            }
            finally
            {
                Object.DestroyImmediate(texture);
            }
        }

        [Test]
        public void TrilinearMipChainsUseConservativeFractionalLodFallback()
        {
            Assert.That(TextureLodSafety.RequiresFractionalLodFallback(FilterMode.Trilinear, 2), Is.True);
            Assert.That(TextureLodSafety.RequiresFractionalLodFallback(FilterMode.Trilinear, 1), Is.False,
                "without a mip chain there is no fractional LOD blend");
            Assert.That(TextureLodSafety.RequiresFractionalLodFallback(FilterMode.Bilinear, 8), Is.False);
            Assert.That(TextureLodSafety.RequiresFractionalLodFallback(FilterMode.Point, 8), Is.False);
        }

        private static Vector3 DecodeNormal(Color stored, ATONormalInputEncoding encoding)
        {
            float x;
            switch (encoding)
            {
                case ATONormalInputEncoding.EncodedAg: x = stored.a; break;
                case ATONormalInputEncoding.EncodedRgOrAg: x = stored.r * stored.a; break;
                default: x = stored.r; break;
            }
            var normal = new Vector3(x * 2f - 1f, stored.g * 2f - 1f,
                encoding == ATONormalInputEncoding.EncodedRgb ? stored.b * 2f - 1f : 0f);
            if (encoding != ATONormalInputEncoding.EncodedRgb)
                normal.z = Mathf.Sqrt(1f - Mathf.Clamp01(normal.x * normal.x + normal.y * normal.y));
            return normal.normalized;
        }
    }
}
