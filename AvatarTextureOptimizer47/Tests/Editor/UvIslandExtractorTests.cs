using System.Collections.Generic;
using Fosa.AvatarTextureOptimizer.Editor.Analysis;
using NUnit.Framework;
using UnityEngine;

namespace Fosa.AvatarTextureOptimizer.Tests
{
    internal sealed class UvIslandExtractorTests
    {
        [Test]
        public void IntegerTileIsNormalized()
        {
            var mesh = Triangle(new[] { new Vector2(1.1f, 1.1f), new Vector2(1.8f, 1.1f), new Vector2(1.1f, 1.8f) });
            try
            {
                var islands = UvIslandExtractor.Extract(mesh, 0, 0, 0, out var translation, out var failure);
                Assert.That(failure, Is.Null);
                Assert.That(translation, Is.EqualTo(new Vector2(-1f, -1f)));
                Assert.That(islands, Has.Count.EqualTo(1));
                Assert.That(islands[0].UvBounds.xMin, Is.EqualTo(0.1f).Within(1e-5f));
            }
            finally { Object.DestroyImmediate(mesh); }
        }

        [Test]
        public void WrapSeamIsRejected()
        {
            var mesh = Triangle(new[] { new Vector2(0.9f, 0.2f), new Vector2(1.1f, 0.2f), new Vector2(0.9f, 0.8f) });
            try
            {
                var islands = UvIslandExtractor.Extract(mesh, 0, 0, 0, out _, out var failure);
                Assert.That(islands, Is.Empty);
                Assert.That(failure, Does.Contain("wrap seam"));
            }
            finally { Object.DestroyImmediate(mesh); }
        }

        [Test]
        public void GeometricallyOverlappingDisconnectedTrianglesMerge()
        {
            var mesh = new Mesh();
            mesh.vertices = new[]
            {
                Vector3.zero, Vector3.right, Vector3.up,
                Vector3.one, Vector3.right * 2f, Vector3.up * 2f,
            };
            mesh.SetUVs(0, new List<Vector2>
            {
                new Vector2(.1f,.1f), new Vector2(.8f,.1f), new Vector2(.1f,.8f),
                new Vector2(.2f,.2f), new Vector2(.7f,.2f), new Vector2(.2f,.7f),
            });
            mesh.SetTriangles(new[] { 0, 1, 2, 3, 4, 5 }, 0);
            try
            {
                var islands = UvIslandExtractor.Extract(mesh, 0, 0, 0, out _, out var failure);
                Assert.That(failure, Is.Null);
                Assert.That(islands, Has.Count.EqualTo(1));
                Assert.That(islands[0].Triangles, Has.Count.EqualTo(2));
            }
            finally { Object.DestroyImmediate(mesh); }
        }

        private static Mesh Triangle(IReadOnlyList<Vector2> uv)
        {
            var mesh = new Mesh { vertices = new[] { Vector3.zero, Vector3.right, Vector3.up } };
            mesh.SetUVs(0, new List<Vector2>(uv));
            mesh.SetTriangles(new[] { 0, 1, 2 }, 0);
            return mesh;
        }
    }
}
