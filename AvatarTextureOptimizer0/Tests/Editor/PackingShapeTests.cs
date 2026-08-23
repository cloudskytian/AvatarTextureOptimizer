using Fosa.AvatarTextureOptimizer.Editor.Analysis;
using Fosa.AvatarTextureOptimizer.Editor.Atlas;
using NUnit.Framework;
using UnityEngine;

namespace Fosa.AvatarTextureOptimizer.Tests
{
    public sealed class PackingShapeTests
    {
        [Test]
        public void RotatedShapePreservesBitsAndSwapsDimensions()
        {
            var shape = new PackingShape { Width = 8, Height = 4, Bits = new byte[8] };
            Set(shape, 2, 1); Set(shape, 7, 3);
            var rotated = shape.Rotated();
            Assert.AreEqual(4, rotated.Width); Assert.AreEqual(8, rotated.Height);
            Assert.IsTrue(rotated.IsSet(2, 2));
            Assert.IsTrue(rotated.IsSet(0, 7));
        }

        [Test]
        public void RotatedContentOffsetIncludesAlignmentSlack()
        {
            var used = new PackingShape { Width = 16, Height = 16, Bits = new byte[64] };
            Assert.AreEqual(6, ShapeAtlasPacker.ContentOffsetX(used, new UnityEngine.Vector2Int(5, 6), 4, true));
            Assert.AreEqual(4, ShapeAtlasPacker.ContentOffsetX(used, new UnityEngine.Vector2Int(5, 6), 4, false));
        }

        [Test]
        public void AtlasAxisBudgetUsesTheStricterConfiguredOrDeviceLimit()
        {
            Assert.That(ShapeAtlasPacker.EffectiveMaximumAtlasSize(8192, 4096), Is.EqualTo(4096));
            Assert.That(ShapeAtlasPacker.EffectiveMaximumAtlasSize(2048, 8192), Is.EqualTo(2048));
            Assert.That(ShapeAtlasPacker.EffectiveMaximumAtlasSize(0, 8192), Is.Zero);
            Assert.That(ShapeAtlasPacker.EffectiveMaximumAtlasSize(8192, 0), Is.Zero);
        }

        [Test]
        public void RotatedMeshUvMatchesClockwisePackedContentCoordinates()
        {
            var island = new UvIsland { UvBounds = new Rect(0.2f, 0.1f, 0.6f, 0.8f) };
            var placement = new AtlasPlacement
            {
                Island = island,
                Rotated = true,
                ContentRect = new RectInt(10, 20, 30, 40)
            };
            var page = new AtlasPage { Id = 7, Size = new Vector2Int(100, 80) };
            page.Placements.Add(placement);
            var plan = new AtlasPlan(); plan.Pages.Add(page);

            var lowerLeft = MeshAtlasRemapper.TransformUv(new Vector4(0.2f, 0.1f, 4f, 5f), placement, plan);
            var upperRight = MeshAtlasRemapper.TransformUv(new Vector4(0.8f, 0.9f, 6f, 7f), placement, plan);

            Assert.That(lowerLeft.x, Is.EqualTo(0.4f).Within(1e-6f));
            Assert.That(lowerLeft.y, Is.EqualTo(0.25f).Within(1e-6f));
            Assert.That(upperRight.x, Is.EqualTo(0.1f).Within(1e-6f));
            Assert.That(upperRight.y, Is.EqualTo(0.75f).Within(1e-6f));
            Assert.That(lowerLeft.z, Is.EqualTo(4f));
            Assert.That(lowerLeft.w, Is.EqualTo(5f));
        }

        private static void Set(PackingShape shape, int x, int y)
        {
            var index = y * shape.Width + x;
            shape.Bits[index >> 2] |= (byte)(1 << (index & 3));
        }
    }
}
