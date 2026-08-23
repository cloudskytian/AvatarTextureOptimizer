using Fosa.AvatarTextureOptimizer.Editor.Analysis;
using NUnit.Framework;
using UnityEngine;

namespace Fosa.AvatarTextureOptimizer.Tests
{
    public sealed class Geometry2DTests
    {
        [Test] public void OverlappingTrianglesAreDetected()
        {
            Assert.IsTrue(Geometry2D.TrianglesOverlap(Vector2.zero, Vector2.right, Vector2.up,
                new Vector2(0.2f, 0.2f), new Vector2(0.8f, 0.2f), new Vector2(0.2f, 0.8f)));
        }

        [Test] public void SeparatedTrianglesAreNotDetected()
        {
            Assert.IsFalse(Geometry2D.TrianglesOverlap(Vector2.zero, Vector2.right, Vector2.up,
                new Vector2(2f, 2f), new Vector2(3f, 2f), new Vector2(2f, 3f)));
        }
    }
}
