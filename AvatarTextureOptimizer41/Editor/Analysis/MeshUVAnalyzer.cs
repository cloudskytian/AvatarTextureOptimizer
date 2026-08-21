using System;
using System.Collections.Generic;
using UnityEngine;
using Net.Fosa.AvatarTextureOptimizer.Pure;

// Mesh UV analysis: extracts UV islands per (mesh, submesh, channel), computes pixel-space bounds
// against the source texture and world-space size (worst case over blend shapes 0/100 and scale anims).
// 网格 UV 分析：按（网格、子网格、通道）提取 UV 岛，计算相对源贴图的像素包围盒与世界尺寸
// （形态键 0/100 与缩放动画的最差情况）。

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    public static class MeshUVAnalyzer
    {
        /// <summary>
        /// Extracts islands for one UV channel of one submesh, wrapped into Unity UVIsland objects.
        /// Islands that cross wrap seams are returned in `wrapIslands` (they must be whitelisted).
        /// 提取某个子网格某 UV 通道的岛并包装为 Unity 对象；跨缝岛放入 wrapIslands（须白名单化）。
        /// </summary>
        public static List<UVIsland> ExtractIslands(
            Mesh mesh, int submesh, int channel, Texture2D sourceTex,
            AnimationAnalysis anim, Renderer renderer, GameObject root,
            List<UVIsland> wrapIslands)
        {
            var result = new List<UVIsland>();
            if (mesh == null || sourceTex == null) return result;

            Vector2[] uvs = GetUVs(mesh, channel);
            if (uvs == null || uvs.Length == 0) return result;

            int[] tris = mesh.GetTriangles(submesh);
            if (tris == null || tris.Length == 0) return result;

            var uvF = new float[uvs.Length * 2];
            for (int i = 0; i < uvs.Length; i++) { uvF[i * 2] = uvs[i].x; uvF[i * 2 + 1] = uvs[i].y; }

            var raw = IslandCore.Extract(uvF, tris, mesh.vertexCount);
            raw = IslandCore.MergeOverlapping(raw);

            foreach (var iso in raw)
            {
                var island = new UVIsland
                {
                    Channel = channel,
                    SourceTexture = sourceTex,
                    TriangleIndices = iso.Triangles,
                    TriangleArrayIndices = iso.Triangles.ToArray(),
                    UVs = uvF,
                    BoundsMin = new Vector2(iso.MinU, iso.MinV),
                    BoundsMax = new Vector2(iso.MaxU, iso.MaxV),
                    WasTranslated = !iso.CrossesWrap,
                    CrossesWrap = iso.CrossesWrap,
                };
                if (iso.CrossesWrap)
                {
                    wrapIslands.Add(island);
                    continue;
                }
                // Pixel bbox at source texture resolution. 源贴图分辨率下的像素包围盒。
                island.OrigPixelSize = new Vector2Int(
                    Mathf.Max(1, Mathf.RoundToInt((iso.MaxU - iso.MinU) * sourceTex.width)),
                    Mathf.Max(1, Mathf.RoundToInt((iso.MaxV - iso.MinV) * sourceTex.height)));
                island.WorldSizeMeters = ComputeWorldSize(mesh, iso, renderer, anim, root);
                result.Add(island);
            }
            return result;
        }

        public static Vector2[] GetUVs(Mesh mesh, int channel)
        {
            switch (channel)
            {
                case 0: return mesh.uv;
                case 1: return mesh.uv2;
                case 2: return mesh.uv3;
                case 3: return mesh.uv4;
                case 4: return mesh.uv5;
                case 5: return mesh.uv6;
                case 6: return mesh.uv7;
                case 7: return mesh.uv8;
                default: return null;
            }
        }

        /// <summary>
        /// World-space AABB size of an island, worst case: mesh vertex positions transformed by the renderer,
        /// scale animation worst case applied, and animated blend shapes inflated by their max displacement
        /// (0 and 100 weights per spec). 岛的世界空间 AABB 尺寸（最差情况）：网格顶点经渲染器变换，
        /// 应用缩放动画最差情况，并用动画形态键最大位移膨胀（按规格取 0 与 100 权）。
        /// </summary>
        public static Vector2 ComputeWorldSize(Mesh mesh, Island iso, Renderer renderer, AnimationAnalysis anim, GameObject root)
        {
            Vector3[] verts = mesh.vertices;
            Vector3 min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            Vector3 max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            bool any = false;
            foreach (int t in iso.Triangles)
            {
                int i0 = t * 3, i1 = t * 3 + 1, i2 = t * 3 + 2;
                if (i2 + 1 >= verts.Length) continue;
                for (int k = 0; k < 3; k++)
                {
                    int vi = t * 3 + k;
                    var p = verts[vi];
                    min = Vector3.Min(min, p); max = Vector3.Max(max, p);
                    any = true;
                }
            }
            if (!any) return new Vector2(1e-3f, 1e-3f);

            Vector3 localSize = max - min;

            // World scale with animation worst case. 世界缩放（含动画最差情况）。
            Vector3 scale = Vector3.one;
            if (renderer != null && renderer.transform != null)
            {
                scale = renderer.transform.lossyScale;
                if (anim != null)
                {
                    Vector3 worst = anim.WorstLocalScale(renderer.transform, root);
                    Vector3 local = renderer.transform.localScale;
                    scale.x *= local.x != 0f ? Mathf.Max(1f, Mathf.Abs(worst.x / local.x)) : 1f;
                    scale.y *= local.y != 0f ? Mathf.Max(1f, Mathf.Abs(worst.y / local.y)) : 1f;
                    scale.z *= local.z != 0f ? Mathf.Max(1f, Mathf.Abs(worst.z / local.z)) : 1f;
                }
            }
            Vector3 worldSize = new Vector3(localSize.x * Mathf.Abs(scale.x), localSize.y * Mathf.Abs(scale.y), localSize.z * Mathf.Abs(scale.z));

            // Blend-shape worst-case inflation. 形态键最差情况膨胀。
            if (renderer is SkinnedMeshRenderer smr && mesh.blendShapeCount > 0)
            {
                var animatedShapes = anim?.AnimatedBlendShapes(smr, root) ?? new HashSet<string>();
                float inflation = 0f;
                for (int s = 0; s < mesh.blendShapeCount; s++)
                {
                    string name = mesh.GetBlendShapeName(s);
                    bool animated = animatedShapes.Contains(name);
                    float weight = animated ? 100f : smr.GetBlendShapeWeight(s);
                    if (weight <= 0f && !animated) continue;
                    var deltas = new Vector3[mesh.vertexCount];
                    mesh.GetBlendShapeFrameVertices(s, 0, deltas, null, null);
                    float maxDelta = 0f;
                    foreach (int t in iso.Triangles)
                        for (int k = 0; k < 3; k++)
                        {
                            int vi = t * 3 + k;
                            if (vi >= deltas.Length) continue;
                            maxDelta = Mathf.Max(maxDelta, deltas[vi].magnitude);
                        }
                    // Worst case = full 100 weight for animated shapes; static weight for others.
                    // 动画形态键按 100 权最差；静态按当前权。
                    float w = animated ? 1f : Mathf.Clamp01(weight / 100f);
                    inflation = Mathf.Max(inflation, maxDelta * w);
                }
                // Inflation in world units on the largest local axis. 沿最大局部轴按世界单位膨胀。
                float maxLocalAxis = Mathf.Max(localSize.x, Mathf.Max(localSize.y, localSize.z));
                if (maxLocalAxis > 1e-6f)
                {
                    float factor = (maxLocalAxis + inflation * 2f) / maxLocalAxis;
                    worldSize.x *= factor; worldSize.y *= factor;
                }
            }

            return new Vector2(Mathf.Max(1e-4f, worldSize.x), Mathf.Max(1e-4f, worldSize.y));
        }
    }
}
