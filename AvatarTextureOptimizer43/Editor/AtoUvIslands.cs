using System;
using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using Fosa.ATO;

namespace Fosa.ATO.Editor
{
    /// <summary>
    /// One connected UV island in a single mesh UV channel.
    /// 单个网格 UV 通道上的连通 UV 岛。
    /// </summary>
    public sealed class AtoIsland
    {
        public Mesh Mesh;
        public int UvChannel;
        public int Submesh;
        public List<int> Triangles = new List<int>(); // index buffer positions (3 per tri)
        public Vector2 Min = new Vector2(float.PositiveInfinity, float.PositiveInfinity);
        public Vector2 Max = new Vector2(float.NegativeInfinity, float.NegativeInfinity);
        public float UvArea;
        public float WorldArea;
        public Vector2 Translate; // applied to bring to [0,1]
        public bool Wrapped;
        public bool OverflowUnrecoverable;
        public int TexW, TexH;
        public RectInt PixelBounds;
        public bool SolidColor;
        public float ScaleU = 1f, ScaleV = 1f;
        public int PackedX, PackedY, PackedW, PackedH;
        public bool Rotated90;
        public NativeArray<ulong> Raster; // optional, disposed by packer cache
        public bool RasterOwned;
        public int RasterW, RasterH;
        // Cached 4px shapes so candidate search does not re-raster. 候选搜索时复用光栅结果。
        public ulong[] CachedShape, CachedShapeRot;
        public int CachedIw, CachedIh, CachedRw, CachedRh, CachedPadG = -1;

        public int PixelShortSide => Math.Max(1, Math.Min(PixelBounds.width, PixelBounds.height));

        public void Encapsulate(Vector2 uv)
        {
            Min = Vector2.Min(Min, uv);
            Max = Vector2.Max(Max, uv);
        }

        public void FinishBounds(int texW, int texH)
        {
            TexW = Math.Max(1, texW);
            TexH = Math.Max(1, texH);
            float w = Mathf.Max(1e-8f, Max.x - Min.x);
            float h = Mathf.Max(1e-8f, Max.y - Min.y);
            int x = Mathf.FloorToInt(Min.x * texW);
            int y = Mathf.FloorToInt(Min.y * texH);
            int x2 = Mathf.CeilToInt(Max.x * texW);
            int y2 = Mathf.CeilToInt(Max.y * texH);
            PixelBounds = new RectInt(x, y, Math.Max(1, x2 - x), Math.Max(1, y2 - y));
        }
    }

    public static class AtoUvIslands
    {
        const float Seam = 0.5f;

        public static List<AtoIsland> Extract(Mesh mesh, int uvChannel, int texW, int texH, out string fail)
        {
            fail = null;
            var result = new List<AtoIsland>();
            if (mesh == null) { fail = "null mesh"; return result; }
            var uvs = new List<Vector2>();
            mesh.GetUVs(uvChannel, uvs);
            if (uvs == null || uvs.Count == 0)
            {
                fail = "no UV" + uvChannel;
                return result;
            }
            var verts = mesh.vertices;
            if (verts == null || verts.Length != uvs.Count)
            {
                // UV count can differ? Usually equals vertex count.
            }

            int subCount = Math.Max(1, mesh.subMeshCount);
            for (int sm = 0; sm < subCount; sm++)
            {
                int[] tris;
                try { tris = mesh.GetTriangles(sm); }
                catch { continue; }
                if (tris == null || tris.Length < 3) continue;

                int triCount = tris.Length / 3;
                var parent = new int[triCount];
                for (int i = 0; i < triCount; i++) parent[i] = i;

                int Find(int a) { while (parent[a] != a) { parent[a] = parent[parent[a]]; a = parent[a]; } return a; }
                void Union(int a, int b) { a = Find(a); b = Find(b); if (a != b) parent[b] = a; }

                var edge = new Dictionary<long, int>();
                bool wrapped = false;
                for (int t = 0; t < triCount; t++)
                {
                    int i0 = tris[t * 3], i1 = tris[t * 3 + 1], i2 = tris[t * 3 + 2];
                    if (i0 >= uvs.Count || i1 >= uvs.Count || i2 >= uvs.Count) continue;
                    var uv0 = uvs[i0]; var uv1 = uvs[i1]; var uv2 = uvs[i2];
                    if (CrossesSeam(uv0, uv1) || CrossesSeam(uv1, uv2) || CrossesSeam(uv2, uv0))
                        wrapped = true;
                    AddEdge(edge, i0, i1, t, Union);
                    AddEdge(edge, i1, i2, t, Union);
                    AddEdge(edge, i2, i0, t, Union);
                }

                var groups = new Dictionary<int, AtoIsland>();
                for (int t = 0; t < triCount; t++)
                {
                    int r = Find(t);
                    if (!groups.TryGetValue(r, out var isl))
                    {
                        isl = new AtoIsland { Mesh = mesh, UvChannel = uvChannel, Submesh = sm, Wrapped = wrapped };
                        groups[r] = isl;
                    }
                    isl.Triangles.Add(tris[t * 3]);
                    isl.Triangles.Add(tris[t * 3 + 1]);
                    isl.Triangles.Add(tris[t * 3 + 2]);
                    var a = uvs[tris[t * 3]];
                    var b = uvs[tris[t * 3 + 1]];
                    var c = uvs[tris[t * 3 + 2]];
                    isl.Encapsulate(a); isl.Encapsulate(b); isl.Encapsulate(c);
                    isl.UvArea += Area2D(a, b, c);
                    if (verts != null && tris[t * 3] < verts.Length)
                        isl.WorldArea += Area3D(verts[tris[t * 3]], verts[tris[t * 3 + 1]], verts[tris[t * 3 + 2]]);
                }

                foreach (var isl in groups.Values)
                {
                    NormalizeOverflow(isl);
                    isl.FinishBounds(texW, texH);
                    result.Add(isl);
                }
            }

            MergeOverlapping(result);
            return result;
        }

        static void AddEdge(Dictionary<long, int> edge, int a, int b, int tri, Action<int, int> union)
        {
            if (a > b) (a, b) = (b, a);
            long k = ((long)a << 32) ^ (uint)b;
            if (edge.TryGetValue(k, out var other)) union(other, tri);
            else edge[k] = tri;
        }

        static bool CrossesSeam(Vector2 a, Vector2 b)
        {
            return Mathf.Abs(a.x - b.x) > Seam || Mathf.Abs(a.y - b.y) > Seam;
        }

        /// <summary>
        /// Translate island into [0,1] if it does not cross wrap seams and bbox fits.
        /// 若不跨 wrap 缝且包围盒可平移进 [0,1]，则整体平移归一。
        /// </summary>
        public static void NormalizeOverflow(AtoIsland isl)
        {
            if (isl.Wrapped)
            {
                isl.OverflowUnrecoverable = true;
                return;
            }
            float w = isl.Max.x - isl.Min.x;
            float h = isl.Max.y - isl.Min.y;
            if (w > 1.0001f || h > 1.0001f)
            {
                isl.OverflowUnrecoverable = true;
                return;
            }
            float dx = 0, dy = 0;
            if (isl.Min.x < 0f || isl.Max.x > 1f) dx = -Mathf.Floor(isl.Min.x);
            if (isl.Min.y < 0f || isl.Max.y > 1f) dy = -Mathf.Floor(isl.Min.y);
            // After floor-translate, must sit in [0,1].
            if (isl.Min.x + dx < -1e-5f || isl.Max.x + dx > 1.0001f
                || isl.Min.y + dy < -1e-5f || isl.Max.y + dy > 1.0001f)
            {
                isl.OverflowUnrecoverable = true;
                return;
            }
            isl.Translate = new Vector2(dx, dy);
            isl.Min += isl.Translate;
            isl.Max += isl.Translate;
        }

        static void MergeOverlapping(List<AtoIsland> list)
        {
            bool changed = true;
            while (changed)
            {
                changed = false;
                for (int i = 0; i < list.Count; i++)
                for (int j = i + 1; j < list.Count; j++)
                {
                    var a = list[i]; var b = list[j];
                    if (a.Mesh != b.Mesh || a.UvChannel != b.UvChannel) continue;
                    if (!Overlap(a.Min, a.Max, b.Min, b.Max)) continue;
                    a.Triangles.AddRange(b.Triangles);
                    a.Min = Vector2.Min(a.Min, b.Min);
                    a.Max = Vector2.Max(a.Max, b.Max);
                    a.UvArea += b.UvArea;
                    a.WorldArea += b.WorldArea;
                    a.Wrapped |= b.Wrapped;
                    a.OverflowUnrecoverable |= b.OverflowUnrecoverable;
                    a.FinishBounds(a.TexW, a.TexH);
                    list.RemoveAt(j);
                    changed = true;
                    break;
                }
            }
        }

        static bool Overlap(Vector2 amin, Vector2 amax, Vector2 bmin, Vector2 bmax)
        {
            return amin.x < bmax.x && amax.x > bmin.x && amin.y < bmax.y && amax.y > bmin.y;
        }

        static float Area2D(Vector2 a, Vector2 b, Vector2 c)
            => Mathf.Abs((b.x - a.x) * (c.y - a.y) - (c.x - a.x) * (b.y - a.y)) * 0.5f;

        static float Area3D(Vector3 a, Vector3 b, Vector3 c)
            => Vector3.Cross(b - a, c - a).magnitude * 0.5f;

        /// <summary>
        /// Blendshape 0 vs 100 (each shape independently) world-area max.
        /// 每个形态键仅取 0 与 100 的面积最大值。
        /// </summary>
        public static float MaxBlendshapeWorldArea(SkinnedMeshRenderer smr, AtoIsland isl)
        {
            if (smr == null || smr.sharedMesh == null) return isl.WorldArea;
            var mesh = smr.sharedMesh;
            int bs = mesh.blendShapeCount;
            if (bs == 0) return isl.WorldArea * ScaleMul(smr.transform);
            var baked = new Mesh();
            float max = isl.WorldArea;
            try
            {
                var saved = new float[smr.sharedMesh.blendShapeCount];
                for (int i = 0; i < saved.Length; i++) saved[i] = smr.GetBlendShapeWeight(i);

                void Measure()
                {
                    smr.BakeMesh(baked, true);
                    var v = baked.vertices;
                    float area = 0;
                    var tris = isl.Triangles;
                    for (int i = 0; i + 2 < tris.Count; i += 3)
                    {
                        int a = tris[i], b = tris[i + 1], c = tris[i + 2];
                        if (a < v.Length && b < v.Length && c < v.Length)
                            area += Area3D(v[a], v[b], v[c]);
                    }
                    if (area > max) max = area;
                }

                for (int i = 0; i < saved.Length; i++) smr.SetBlendShapeWeight(i, 0);
                Measure();
                for (int s = 0; s < bs; s++)
                {
                    smr.SetBlendShapeWeight(s, 100f);
                    Measure();
                    smr.SetBlendShapeWeight(s, 0);
                }
                for (int i = 0; i < saved.Length; i++) smr.SetBlendShapeWeight(i, saved[i]);
            }
            catch (Exception e)
            {
                AtoLog.Detail("Blendshape area failed: " + e.Message);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(baked);
            }
            return max;
        }

        static float ScaleMul(Transform t)
        {
            var s = t.lossyScale;
            float m = Mathf.Max(Mathf.Abs(s.x), Mathf.Abs(s.y), Mathf.Abs(s.z));
            return Mathf.Max(m * m, 1e-12f); // area ~ scale^2
        }

        /// <summary>
        /// Texel density in px/m from island pixel size vs world size.
        /// 由岛像素尺寸与世界尺寸得到 px/m。
        /// </summary>
        public static float DensityPxPerMeter(AtoIsland isl, float worldArea, int pixelW, int pixelH)
        {
            float world = Mathf.Sqrt(Mathf.Max(worldArea, 1e-12f));
            float pix = Mathf.Sqrt(Mathf.Max(1, pixelW * pixelH) * Mathf.Max(isl.UvArea, 1e-12f)
                                   / Mathf.Max((isl.Max.x - isl.Min.x) * (isl.Max.y - isl.Min.y), 1e-12f));
            // Simpler: short side pixels / equivalent world short side.
            float uvShort = Mathf.Min(isl.Max.x - isl.Min.x, isl.Max.y - isl.Min.y);
            float pxShort = uvShort * Mathf.Min(pixelW, pixelH);
            return world > 1e-8f ? pxShort / world : 0f;
        }
    }

    /// <summary>
    /// 4px-granularity island rasterizer (Burst). Used by the BLF packer.
    /// 4px 粒度光栅化（Burst），供 BLF 装箱使用。
    /// </summary>
    [BurstCompile]
    public struct AtoRasterJob : IJob
    {
        [ReadOnly] public NativeArray<float2> Uv; // 3 per tri, already in island local 0..w/h pixels
        public int TriCount;
        public int W, H; // bitmask size
        public NativeArray<ulong> Bits; // row-major, 64 px per ulong along X

        public void Execute()
        {
            int stride = (W + 63) / 64;
            for (int t = 0; t < TriCount; t++)
            {
                var a = Uv[t * 3];
                var b = Uv[t * 3 + 1];
                var c = Uv[t * 3 + 2];
                int minx = (int)math.floor(math.min(a.x, math.min(b.x, c.x)));
                int maxx = (int)math.ceil(math.max(a.x, math.max(b.x, c.x)));
                int miny = (int)math.floor(math.min(a.y, math.min(b.y, c.y)));
                int maxy = (int)math.ceil(math.max(a.y, math.max(b.y, c.y)));
                minx = math.clamp(minx, 0, W - 1);
                maxx = math.clamp(maxx, 0, W - 1);
                miny = math.clamp(miny, 0, H - 1);
                maxy = math.clamp(maxy, 0, H - 1);
                for (int y = miny; y <= maxy; y++)
                for (int x = minx; x <= maxx; x++)
                {
                    if (!PointInTri(x + 0.5f, y + 0.5f, a, b, c)) continue;
                    int word = y * stride + (x >> 6);
                    ulong bit = 1ul << (x & 63);
                    Bits[word] |= bit;
                }
            }
        }

        static bool PointInTri(float px, float py, float2 a, float2 b, float2 c)
        {
            float s = a.y * c.x - a.x * c.y + (c.y - a.y) * px + (a.x - c.x) * py;
            float t = a.x * b.y - a.y * b.x + (a.y - b.y) * px + (b.x - a.x) * py;
            if ((s < 0) != (t < 0) && s != 0 && t != 0) return false;
            float A = -b.y * c.x + a.y * (c.x - b.x) + a.x * (b.y - c.y) + b.x * c.y;
            return A < 0 ? (s <= 0 && s + t >= A) : (s >= 0 && s + t <= A);
        }
    }
}
