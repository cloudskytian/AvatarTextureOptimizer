// AvatarTextureOptimizer
// File: Editor/Analysis/DensityAnalyzer.cs
//
// Corrects island pixel densities for deformation and animation:
//   - blend shapes: each shape key is evaluated at weight 0 and weight 100;
//     the LARGEST world area wins (per spec: take the max of the two values,
//     no combinations, no negatives, no over-100)
//   - animation: the maximum animated local scale is applied (area at max
//     scale, per spec)
// The result is a conservative per-mesh area factor; island densities are
// divided by it so islands are sized for the LARGEST area.
//
// 修正岛像素密度以考虑形变与动画：
//   - 形态键：每个形态键在权重 0 与权重 100 分别评估；取【最大】世界面积
//     （按规格：仅取 0 和 100 二者的最大值，不考虑组合、负数、超过 100）
//   - 动画：应用最大动画局部缩放（按最大缩放时的面积，按规格）
// 结果是每网格的保守面积因子；岛密度除以它，使岛按【最大面积】确定尺寸。

using System.Collections.Generic;
using net.fosa.avatar_texture_optimizer.editor.model;
using UnityEngine;

namespace net.fosa.avatar_texture_optimizer.editor.analysis
{
    public static class DensityAnalyzer
    {
        /// <summary>
        /// Apply density corrections to every island of the state.
        /// 对状态中的每个岛应用密度修正。
        /// </summary>
        public static void Correct(ATOBuildState state, AnimationFacts facts)
        {
            // Per-renderer area factor cache. / 每渲染器面积因子缓存。
            var factorCache = new Dictionary<Renderer, float>();

            foreach (var group in state.UVGroups)
            {
                if (group.Whitelisted || group.Islands.Count == 0) continue;
                var renderer = group.Space.Renderer;
                if (renderer == null) continue;

                if (!factorCache.TryGetValue(renderer, out var factor))
                {
                    factor = ComputeAreaFactor(renderer, facts, state);
                    factorCache[renderer] = factor;
                }
                if (factor <= 1.0001f) continue;

                foreach (var island in group.Islands)
                {
                    if (island.PixelDensityPPM > 0f)
                        island.PixelDensityPPM /= factor; // larger area -> lower density / 面积更大 -> 密度更低
                }
            }
        }

        private static float ComputeAreaFactor(Renderer renderer, AnimationFacts facts, ATOBuildState state)
        {
            float factor = 1f;

            // 1. Animated local scale (max of animated vs current).
            //    动画局部缩放（动画最大值与当前值之比）。
            if (renderer != null)
            {
                string path = PathOf(renderer, state);
                if (path != null && facts.MaxAnimatedScale.TryGetValue(path, out var maxScale))
                {
                    var cur = renderer.transform.localScale;
                    float fx = SafeRatio(maxScale.x, cur.x);
                    float fy = SafeRatio(maxScale.y, cur.y);
                    float fz = SafeRatio(maxScale.z, cur.z);
                    float maxAxis = Mathf.Max(fx, Mathf.Max(fy, fz));
                    if (maxAxis > 1f) factor *= maxAxis * maxAxis; // area scales by s^2 / 面积按 s² 缩放
                }
            }

            // 2. Blend shapes at weight 0 and 100 (largest AABB area factor).
            //    形态键在权重 0 与 100（最大 AABB 面积因子）。
            if (renderer is SkinnedMeshRenderer smr && smr.sharedMesh != null)
            {
                var mesh = smr.sharedMesh;
                int shapeCount = mesh.blendShapeCount;
                if (shapeCount > 0)
                {
                    var baseVerts = mesh.vertices;
                    var baseBounds = mesh.bounds;
                    float baseArea = AreaXY(baseBounds);
                    if (baseArea > 1e-9f)
                    {
                        float maxFactor = 1f;
                        for (int s = 0; s < shapeCount; s++)
                        {
                            int frames = mesh.GetBlendShapeFrameCount(s);
                            if (frames == 0) continue;
                            var dv = new Vector3[baseVerts.Length];
                            var dn = new Vector3[baseVerts.Length];
                            var dt = new Vector3[baseVerts.Length];
                            mesh.GetBlendShapeFrameVertices(s, frames - 1, dv, dn, dt);

                            // weight 100 (frames hold full deltas). / 权重 100
                            // （帧保存完整增量）。
                            var deformed = new Vector3[baseVerts.Length];
                            for (int i = 0; i < baseVerts.Length; i++)
                                deformed[i] = baseVerts[i] + dv[i];

                            var min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
                            var max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
                            foreach (var p in deformed)
                            {
                                min = Vector3.Min(min, p);
                                max = Vector3.Max(max, p);
                            }
                            float area = (max.x - min.x) * (max.y - min.y);
                            float f = area / baseArea;
                            if (f > maxFactor) maxFactor = f;
                        }
                        if (maxFactor > 1f) factor *= maxFactor;
                    }
                }
            }

            return factor;
        }

        private static float AreaXY(Bounds b) => (b.size.x) * (b.size.y);

        private static float SafeRatio(float a, float b)
        {
            if (Mathf.Abs(b) < 1e-6f) return 1f;
            return a / Mathf.Abs(b);
        }

        private static string PathOf(Renderer renderer, ATOBuildState state)
        {
            var root = state.Component != null ? state.Component.transform : null;
            if (root == null) return null;
            var t = renderer.transform;
            var parts = new List<string>();
            while (t != null && t != root)
            {
                parts.Insert(0, t.name);
                t = t.parent;
            }
            return string.Join("/", parts);
        }
    }
}
