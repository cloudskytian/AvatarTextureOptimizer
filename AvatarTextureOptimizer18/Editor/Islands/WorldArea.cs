using System.Collections.Generic;
using UnityEngine;

namespace Fosa.AvatarTextureOptimizer.Editor.Islands
{
    // 世界面积计算：含动画缩放（逐轴最大）与形态键（仅取 0 与 100 两个状态的最大值，无排列组合）。
    // 同一网格被多个渲染器复用时取最坏（最大）面积；形态键因子按渲染器缓存。
    // World-area computation: includes animated scale (per-axis max) and blend shapes (max of the 0 and 100 states only).
    // Shared meshes take the worst (largest) area across renderers; blend-shape factors are cached per renderer.
    internal static class WorldArea
    {
        private static readonly Dictionary<SkinnedMeshRenderer, float> BlendShapeCache = new Dictionary<SkinnedMeshRenderer, float>();

        // 每轮烘焙开始时清空缓存。Clears the cache at the start of each build.
        public static void ResetCache()
        {
            BlendShapeCache.Clear();
        }

        // 每渲染器的有效世界缩放（lossyScale 与动画缩放逐轴取绝对值最大）。
        // Effective world scale per renderer (per-axis max of lossyScale and animated local scale).
        public static Vector3 EffectiveScale(Renderer renderer, ATOContext ctx, Transform avatarRoot)
        {
            Vector3 scale = Vector3.one;
            Transform t = renderer.transform;
            while (t != null)
            {
                Vector3 s = t.localScale;
                Vector3 anim;
                if (ctx.animations.maxLocalScale.TryGetValue(t, out anim))
                {
                    s = new Vector3(
                        Mathf.Max(Mathf.Abs(s.x), Mathf.Abs(anim.x)),
                        Mathf.Max(Mathf.Abs(s.y), Mathf.Abs(anim.y)),
                        Mathf.Max(Mathf.Abs(s.z), Mathf.Abs(anim.z)));
                }
                scale = new Vector3(scale.x * Mathf.Abs(s.x), scale.y * Mathf.Abs(s.y), scale.z * Mathf.Abs(s.z));
                if (t == avatarRoot) break;
                t = t.parent;
            }
            return scale;
        }

        // 计算岛的世界面积；同时返回用于各向异性分析的逐轴世界尺寸估计。
        // Computes the island world area; also returns per-axis world extent estimates for anisotropic analysis.
        public static void ComputeIslandArea(ATOContext ctx, IslandEntity e, out float area, out Vector2 worldExtents)
        {
            area = 0f;
            worldExtents = Vector2.zero;
            var mesh = e.mesh;
            var verts = mesh.vertices;

            // 局部空间三角形面积（每个渲染器只差缩放因子，缓存局部面积）。Local triangle area (per renderer only the scale factor differs).
            float localArea = 0f;
            for (int i = 0; i < e.triangles.Count; i += 3)
            {
                int a = e.triangles[i], b = e.triangles[i + 1], c = e.triangles[i + 2];
                if (a < 0 || b < 0 || c < 0) continue;
                if (a >= verts.Length || b >= verts.Length || c >= verts.Length) continue;
                localArea += TriangleArea(verts[a], verts[b], verts[c]);
            }

            // 遍历使用该网格的全部渲染器，取最坏情况。Iterate every renderer using this mesh; take the worst case.
            float best = 0f;
            float bestLinear = 1f;
            foreach (var r in ctx.renderers)
            {
                if (MeshOf(r) != mesh) continue;
                var sr = r as SkinnedMeshRenderer;
                float bsFactor = sr != null ? GetBlendShapeFactor(ctx, sr) : 1f;
                Vector3 scale = EffectiveScale(r, ctx, ctx.avatarRoot.transform);
                // 各向异性折合：det^(2/3)（均匀缩放的精确值；各向异性时取几何平均近似，各向异性细化阶段兜底）。
                // Anisotropic folding: det^(2/3) (exact for uniform scale; geometric mean for anisotropic, refined later).
                float det = scale.x * scale.y * scale.z;
                float areaFactor = Mathf.Pow(Mathf.Max(det, 1e-12f), 2f / 3f);
                float candidate = localArea * areaFactor * bsFactor;
                if (candidate > best)
                {
                    best = candidate;
                    bestLinear = Mathf.Sqrt(Mathf.Max(areaFactor, 1e-6f));
                }
            }
            area = Mathf.Max(best, 1e-6f);

            // 逐轴世界尺寸估计：使用包围盒的对角世界距离在 UV 两轴的近似投影。
            // Per-axis world extent estimate: approximate via world distances projected on the UV axes.
            Vector2 uvSpan = e.uvMax - e.uvMin;
            worldExtents = new Vector2(Mathf.Max(uvSpan.x, 1e-6f) * bestLinear, Mathf.Max(uvSpan.y, 1e-6f) * bestLinear);
        }

        private static Mesh MeshOf(Renderer r)
        {
            var sr = r as SkinnedMeshRenderer;
            if (sr != null) return sr.sharedMesh;
            var mr = r as MeshRenderer;
            if (mr == null) return null;
            var mf = mr.GetComponent<MeshFilter>();
            return mf != null ? mf.sharedMesh : null;
        }

        // 形态键 0/100 面积因子（带缓存）：新顶点 = 基础 + Σ delta * (w/100)（仅使用 0 与 100 状态）。
        // Blend-shape area factor (cached): new verts = base + Σ delta * (w/100) (only the 0 and 100 states).
        private static float GetBlendShapeFactor(ATOContext ctx, SkinnedMeshRenderer sr)
        {
            float cached;
            if (BlendShapeCache.TryGetValue(sr, out cached)) return cached;

            var mesh = sr.sharedMesh;
            Dictionary<string, float> weights;
            float factor = 1f;
            if (mesh != null && ctx.animations.blendShapeWeights.TryGetValue(sr, out weights) && weights.Count > 0)
            {
                factor = BlendShapeAreaFactor(mesh, weights);
            }
            BlendShapeCache[sr] = factor;
            return factor;
        }

        private static float BlendShapeAreaFactor(Mesh mesh, Dictionary<string, float> weights)
        {
            var baseVerts = mesh.vertices;
            var deltas = new Dictionary<string, Vector3[]>();
            var deltaWeights = new Dictionary<string, float>();
            int count = mesh.blendShapeCount;
            for (int i = 0; i < count; i++)
            {
                string name = mesh.GetBlendShapeName(i);
                float w;
                if (!weights.TryGetValue(name, out w)) continue;
                w = Mathf.Clamp(w, 0f, 100f);
                if (w <= 0f) continue;
                int frames = mesh.GetBlendShapeFrameCount(i);
                if (frames < 2) continue;
                var frameVerts = new Vector3[baseVerts.Length];
                mesh.GetBlendShapeFrameVertices(i, 1, frameVerts, null, null);
                var delta = new Vector3[baseVerts.Length];
                for (int v = 0; v < baseVerts.Length; v++) delta[v] = frameVerts[v] - baseVerts[v];
                deltas[name] = delta;
                deltaWeights[name] = w / 100f;
            }
            if (deltas.Count == 0) return 1f;

            var newVerts = new Vector3[baseVerts.Length];
            for (int v = 0; v < baseVerts.Length; v++)
            {
                var p = baseVerts[v];
                foreach (var kv in deltas)
                {
                    p += kv.Value[v] * deltaWeights[kv.Key];
                }
                newVerts[v] = p;
            }

            int[] tris = mesh.triangles;
            float baseArea = 0f, newArea = 0f;
            for (int i = 0; i + 2 < tris.Length; i += 3)
            {
                int a = tris[i], b = tris[i + 1], c = tris[i + 2];
                if (a < 0 || b < 0 || c < 0 || a >= baseVerts.Length || b >= baseVerts.Length || c >= baseVerts.Length) continue;
                baseArea += TriangleArea(baseVerts[a], baseVerts[b], baseVerts[c]);
                newArea += TriangleArea(newVerts[a], newVerts[b], newVerts[c]);
            }
            if (baseArea <= 1e-8f) return 1f;
            return newArea / baseArea;
        }

        private static float TriangleArea(Vector3 a, Vector3 b, Vector3 c)
        {
            return Vector3.Cross(b - a, c - a).magnitude * 0.5f;
        }
    }
}
