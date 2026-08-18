// Avatar Texture Optimizer (ATO)
// Pixel-density (texels per meter) computation and clamping.
// 像素密度（每米纹素）计算与钳制。
//
// Accounts for: mesh world scale, animation-driven local scale, and blend-shape
// displacement (each shape key evaluated at weight 0 and 100, taking the maximum).
// 考虑：网格世界缩放、动画驱动的局部缩放、以及形态键位移（每个形态键取 0 与 100 时的最大值）。

using UnityEngine;

namespace NetFosa.ATO
{
    /// <summary>
    /// Pixel density helpers. / 像素密度辅助。
    /// </summary>
    public static class ATOPixelDensity
    {
        /// <summary>
        /// Compute the island's world area in square meters (with scale + blend-shape inflation).
        /// 计算岛的世界面积（平方米，含缩放与形态键膨胀）。
        /// </summary>
        public static float WorldAreaMeters(ATOBuildContext build, ATORendererRef rr, ATOIsland isl)
        {
            var mesh = rr.sourceMesh;
            var verts = mesh.vertices;
            var normals = mesh.normals;
            if (normals.Length != verts.Length) normals = null;

            float worldArea = 0f;
            for (int t = 0; t < isl.triangles.Length / 3; t++)
            {
                int i0 = isl.localVertices[isl.triangles[t * 3]];
                int i1 = isl.localVertices[isl.triangles[t * 3 + 1]];
                int i2 = isl.localVertices[isl.triangles[t * 3 + 2]];
                var p0 = verts[i0]; var p1 = verts[i1]; var p2 = verts[i2];
                worldArea += Vector3.Cross(p1 - p0, p2 - p0).magnitude * 0.5f;
            }

            // Transform scale (local scale of the renderer's transform). / 渲染器变换的局部缩放。
            var s = rr.renderer.transform.lossyScale;
            worldArea *= Mathf.Max(Mathf.Abs(s.x * s.y), Mathf.Abs(s.x * s.z), Mathf.Abs(s.y * s.z));

            // Animation-driven scale (max area). / 动画驱动缩放（最大面积）。
            if (build.anim.maxAreaScale.TryGetValue(rr.path, out var animScale))
                worldArea *= animScale;

            // Blend-shape inflation. / 形态键膨胀。
            worldArea *= BlendShapeInflation(mesh);

            return worldArea;
        }

        /// <summary>
        /// Blend-shape inflation factor: for each shape key, take the max displacement at
        /// weight 0 vs 100 relative to the mesh's characteristic size. Conservative bound.
        /// 形态键膨胀系数：每个形态键取 0 与 100 时相对网格特征尺寸的最大位移，做保守上界。
        /// </summary>
        public static float BlendShapeInflation(Mesh mesh)
        {
            int count = mesh.blendShapeCount;
            if (count == 0) return 1f;
            var bounds = mesh.bounds;
            float charSize = Mathf.Max(bounds.size.magnitude, 0.0001f);
            float maxDelta = 0f;
            var deltas = new Vector3[mesh.vertexCount];
            var positions = mesh.vertices;
            for (int i = 0; i < count; i++)
            {
                int frameCount = mesh.GetBlendShapeFrameCount(i);
                if (frameCount == 0) continue;
                // Frame 0 = weight 0; last frame = weight 100. / 第 0 帧=权重 0；最后一帧=权重 100。
                mesh.GetBlendShapeFrameVertices(i, 0, deltas, null, null);
                float d0 = MaxMagnitude(deltas);
                mesh.GetBlendShapeFrameVertices(i, frameCount - 1, deltas, null, null);
                float d100 = MaxMagnitude(deltas);
                maxDelta = Mathf.Max(maxDelta, Mathf.Max(d0, d100));
            }
            if (maxDelta <= 0f) return 1f;
            float linear = 1f + maxDelta / charSize;
            return linear * linear; // area inflation / 面积膨胀
        }

        private static float MaxMagnitude(Vector3[] vs)
        {
            float m = 0f;
            for (int i = 0; i < vs.Length; i++)
            {
                float l = vs[i].sqrMagnitude;
                if (l > m) m = l;
            }
            return Mathf.Sqrt(m);
        }

        /// <summary>
        /// Texels per meter for the island against a given texture width.
        /// 相对给定贴图宽度的每米纹素数。
        /// </summary>
        public static float TexelsPerMeter(float textureWidth, float areaUv, float worldAreaMeters)
        {
            if (worldAreaMeters <= 0f || areaUv <= 0f) return float.PositiveInfinity;
            float scale = Mathf.Sqrt(areaUv / worldAreaMeters); // uv-units per meter / 每米的 UV 单位
            return textureWidth * scale;
        }
    }
}
