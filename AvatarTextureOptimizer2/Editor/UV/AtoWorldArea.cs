using UnityEngine;

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// World-space area: max of blendshape 0/100 and max animated scale.
    /// 世界空间面积：形态键 0/100 最大值 + 动画最大缩放。
    /// </summary>
    public static class AtoWorldArea
    {
        public static float IslandArea(Renderer r, Mesh mesh, AtoIsland island, GameObject root)
        {
            var verts = mesh.vertices;
            var tris = mesh.GetTriangles(island.Submesh);
            float area = Area(verts, tris, island);

            if (r is SkinnedMeshRenderer && mesh.blendShapeCount > 0)
            {
                var delta = new Vector3[mesh.vertexCount];
                for (int s = 0; s < mesh.blendShapeCount; s++)
                {
                    int frames = mesh.GetBlendShapeFrameCount(s);
                    if (frames == 0) continue;
                    // Use last frame as 100, first as 0 (already base).
                    float w = mesh.GetBlendShapeFrameWeight(s, frames - 1);
                    mesh.GetBlendShapeFrameVertices(s, frames - 1, delta, null, null);
                    var v100 = (Vector3[])verts.Clone();
                    float k = w == 0 ? 1f : 1f;
                    for (int i = 0; i < v100.Length; i++) v100[i] += delta[i] * k;
                    area = Mathf.Max(area, Area(v100, tris, island));
                }
            }

            var maxScale = MaxAnimatedLossyScale(r.transform, root);
            return area * maxScale * maxScale;
        }

        static float Area(Vector3[] verts, int[] tris, AtoIsland island)
        {
            float a = 0;
            var set = new System.Collections.Generic.HashSet<int>(island.Triangles);
            foreach (var t in set)
            {
                int i0 = tris[t * 3], i1 = tris[t * 3 + 1], i2 = tris[t * 3 + 2];
                a += Vector3.Cross(verts[i1] - verts[i0], verts[i2] - verts[i0]).magnitude * 0.5f;
            }
            return a;
        }

        static float MaxAnimatedLossyScale(Transform t, GameObject root)
        {
            float s = MaxAbs(t.lossyScale);
            foreach (var a in root.GetComponentsInChildren<Animator>(true))
            {
                if (a.runtimeAnimatorController == null) continue;
                foreach (var clip in a.runtimeAnimatorController.animationClips)
                {
                    foreach (var b in UnityEditor.AnimationUtility.GetCurveBindings(clip))
                    {
                        if (!b.propertyName.StartsWith("m_LocalScale")) continue;
                        var curve = UnityEditor.AnimationUtility.GetEditorCurve(clip, b);
                        if (curve == null) continue;
                        foreach (var k in curve.keys)
                            s = Mathf.Max(s, Mathf.Abs(k.value));
                    }
                }
            }
            return Mathf.Max(s, 1e-4f);
        }

        static float MaxAbs(Vector3 v) => Mathf.Max(Mathf.Abs(v.x), Mathf.Max(Mathf.Abs(v.y), Mathf.Abs(v.z)));
    }
}
