using NUnit.Framework;
using UnityEngine;
using Fosa.AvatarTextureOptimizer;
using Fosa.AvatarTextureOptimizer.Editor;

namespace Fosa.AvatarTextureOptimizer.Tests
{
    /// <summary>
    /// Deterministic editor tests for pure settings and packer invariants. / 对纯配置与装箱不变量的确定性编辑器测试。
    /// </summary>
    public sealed class ATOAlgorithmTests
    {
        [Test]
        public void CustomQualityStartsNearLossless()
        {
            ATOQualityParameters parameters = ATOQualityParameters.NearLossless();
            Assert.That(parameters.targetQuality, Is.EqualTo(1f));
            Assert.That(parameters.msSsimQuality, Is.EqualTo(1f));
            Assert.That(parameters.normalQuality, Is.EqualTo(1f));
        }

        [Test]
        public void ImportFingerprintIgnoresAssetPath()
        {
            TextureImportFingerprint first = new TextureImportFingerprint(128, 64, TextureWrapMode.Clamp,
                FilterMode.Bilinear, true, true, true, UnityEditor.TextureImporterCompression.Compressed, 128, "A.png");
            TextureImportFingerprint second = new TextureImportFingerprint(128, 64, TextureWrapMode.Clamp,
                FilterMode.Bilinear, true, true, true, UnityEditor.TextureImporterCompression.Compressed, 128, "B.png");
            Assert.That(first, Is.EqualTo(second));
        }

        [Test]
        public void PackerPlacesTwoNonOverlappingIslands()
        {
            IslandRecord first = MakeIsland(32, 32, 0f);
            IslandRecord second = MakeIsland(32, 32, 0.5f);
            AtlasPackingResult result = AtlasPacker.TryPack(new[] { first, second }, 256, 64, false, 4, 4,
                new ATOLogger(false));
            Assert.That(result, Is.Not.Null);
            Assert.That(result.Placements.Count, Is.EqualTo(2));
            Assert.That(result.Width, Is.GreaterThanOrEqualTo(64));
        }

        private static IslandRecord MakeIsland(int width, int height, float offset)
        {
            IslandRecord island = new IslandRecord
            {
                UVBounds = new Rect(offset, 0f, 0.25f, 0.25f),
                OutputWidth = width,
                OutputHeight = height
            };
            island.Triangles.Add(new IslandTriangle(0, 1, 2, new Vector2(offset, 0f),
                new Vector2(offset + 0.25f, 0f), new Vector2(offset, 0.25f), 0.03125f));
            return island;
        }
    }
}
