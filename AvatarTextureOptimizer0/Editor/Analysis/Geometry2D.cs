using UnityEngine;

namespace Fosa.AvatarTextureOptimizer.Editor.Analysis
{
    internal static class Geometry2D
    {
        private const float Epsilon = 1e-7f;

        public static bool TrianglesOverlap(Vector2 a0, Vector2 a1, Vector2 a2, Vector2 b0, Vector2 b1, Vector2 b2)
        {
            if (PointInTriangle(a0, b0, b1, b2) || PointInTriangle(b0, a0, a1, a2)) return true;
            return SegmentsIntersect(a0, a1, b0, b1) || SegmentsIntersect(a0, a1, b1, b2) ||
                   SegmentsIntersect(a0, a1, b2, b0) || SegmentsIntersect(a1, a2, b0, b1) ||
                   SegmentsIntersect(a1, a2, b1, b2) || SegmentsIntersect(a1, a2, b2, b0) ||
                   SegmentsIntersect(a2, a0, b0, b1) || SegmentsIntersect(a2, a0, b1, b2) ||
                   SegmentsIntersect(a2, a0, b2, b0);
        }

        public static bool PointInTriangle(Vector2 point, Vector2 a, Vector2 b, Vector2 c)
        {
            var d1 = Cross(point - b, a - b);
            var d2 = Cross(point - c, b - c);
            var d3 = Cross(point - a, c - a);
            var hasNegative = d1 < -Epsilon || d2 < -Epsilon || d3 < -Epsilon;
            var hasPositive = d1 > Epsilon || d2 > Epsilon || d3 > Epsilon;
            return !(hasNegative && hasPositive);
        }

        public static bool SegmentsIntersect(Vector2 a, Vector2 b, Vector2 c, Vector2 d)
        {
            var abC = Cross(b - a, c - a); var abD = Cross(b - a, d - a);
            var cdA = Cross(d - c, a - c); var cdB = Cross(d - c, b - c);
            return abC * abD <= Epsilon && cdA * cdB <= Epsilon &&
                   Mathf.Max(Mathf.Min(a.x, b.x), Mathf.Min(c.x, d.x)) <= Mathf.Min(Mathf.Max(a.x, b.x), Mathf.Max(c.x, d.x)) + Epsilon &&
                   Mathf.Max(Mathf.Min(a.y, b.y), Mathf.Min(c.y, d.y)) <= Mathf.Min(Mathf.Max(a.y, b.y), Mathf.Max(c.y, d.y)) + Epsilon;
        }

        public static float TriangleArea(Vector3 a, Vector3 b, Vector3 c) => Vector3.Cross(b - a, c - a).magnitude * 0.5f;
        private static float Cross(Vector2 a, Vector2 b) => a.x * b.y - a.y * b.x;
    }
}
