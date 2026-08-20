// EditMode tests: packing bitmask, candidate pool, CIEDE2000 reference values (Sharma
// 2005), SSIM identity, resample round-trip, mini-json, union-find.
// EditMode 测试：装箱位掩码、候选池、CIEDE2000 参考值（Sharma 2005）、SSIM 恒等、
// 重采样往返、迷你JSON、并查集。

using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace net.fosa.ato.tests
{
    public class BitMaskTests
    {
        [Test]
        public void SetGet_Dilate_Transpose()
        {
            var m = new BitMask(10, 10);
            m.Set(3, 4);
            Assert.IsTrue(m.Get(3, 4));
            Assert.IsFalse(m.Get(4, 3));

            var d = m.Dilated(1);
            Assert.IsTrue(d.Get(2, 3) && d.Get(4, 5) && d.Get(3, 3) && d.Get(3, 5));
            Assert.IsFalse(d.Get(1, 1));

            var t = m.Transposed();
            Assert.IsTrue(t.Get(4, 3));
            Assert.AreEqual(1, t.PopCount());
        }

        [Test]
        public void Overlap_ShiftedWords_AreCorrect()
        {
            // atlas 256 cells wide (4 words per row), island mask 64 cells wide
            var used = new BitMask(256, 8);
            used.Set(70, 3);
            var mask = new BitMask(64, 4);
            mask.Set(0, 0); // would land at (70,3) with px=70,py=3

            Assert.IsTrue(BitmaskPacker.Overlap(used.Rows, BitMask.WordsPerRow(256), 256,
                mask.Rows, BitMask.WordsPerRow(64), 64, 4, 70, 3));
            Assert.IsFalse(BitmaskPacker.Overlap(used.Rows, BitMask.WordsPerRow(256), 256,
                mask.Rows, BitMask.WordsPerRow(64), 64, 4, 5, 3));
            // across a word boundary / 跨字边界
            Assert.IsTrue(BitmaskPacker.Overlap(used.Rows, BitMask.WordsPerRow(256), 256,
                mask.Rows, BitMask.WordsPerRow(64), 64, 4, 70 - 0 + 63, 3 - 0));
        }

        [Test]
        public void Padding_And_TypeGroup()
        {
            Assert.AreEqual(4, BitmaskPacker.PaddingFor(128, 4));
            Assert.AreEqual(4, BitmaskPacker.PaddingFor(512, 4));  // ceil(512/128)=4
            Assert.AreEqual(16, BitmaskPacker.PaddingFor(2048, 4)); // ceil(2048/128)=16
            Assert.AreEqual(64, BitmaskPacker.PaddingFor(8192, 4));
            Assert.AreEqual(32, BitmaskPacker.PaddingFor(2048, 32)); // user minimum wins / 用户最小值生效
        }
    }

    public class CandidatePoolTests
    {
        [Test]
        public void Pot_Ordering_AreaThenSquareness()
        {
            var list = CandidatePool.Candidates(false, 64 * 64, AtoPlatform.PC).ToList();
            Assert.AreEqual(new Vector2Int(64, 64), list[0]);
            // before any 256-wide candidate, all 128 combinations come first / 面积优先
            Assert.IsTrue(list.TakeWhile(c => c.x * c.y <= 128 * 128).Count >= 2);
            // square beats long strip of same area / 同面积下正方形优先
            int i256 = list.FindIndex(c => c.x == 256 && c.y == 256);
            int i256strip = list.FindIndex(c => c.x == 1024 && c.y == 64);
            Assert.Less(i256, i256strip);

            var mobile = CandidatePool.Candidates(false, 64 * 64, AtoPlatform.Android).ToList();
            Assert.IsFalse(mobile.Any(c => c.x > 4096 || c.y > 4096)); // mobile cap / 移动端上限
        }

        [Test]
        public void Npot_StepsBy64()
        {
            var list = CandidatePool.Candidates(true, 4096 * 4096, AtoPlatform.Android).ToList();
            Assert.IsTrue(list.Any(c => c.x == 4160 / 64 * 64 - 64 + 64)); // contains 64-step sizes
            Assert.IsFalse(list.Any(c => c.x % 64 != 0));
        }
    }

    public class MetricTests
    {
        private static Color32[] Solid(int w, int h, Color32 c)
        {
            var a = new Color32[w * h];
            for (int i = 0; i < a.Length; i++) a[i] = c;
            return a;
        }

        [Test]
        public void Ciede2000_MatchesSharmaReferencePairs()
        {
            // Reference pairs from Sharma et al. 2005 (Table 1)
            Assert.AreEqual(2.0425f, DeltaEJob.Ciede2000(
                new float3(50.0000f, 2.6772f, -79.7751f), new float3(50.0000f, 0.0000f, -82.7485f)), 0.001f);
            Assert.AreEqual(2.8615f, DeltaEJob.Ciede2000(
                new float3(50.0000f, 3.1571f, -77.2803f), new float3(50.0000f, 0.0000f, -82.7485f)), 0.001f);
            Assert.AreEqual(3.4412f, DeltaEJob.Ciede2000(
                new float3(50.0000f, 2.8361f, -74.0200f), new float3(50.0000f, 0.0000f, -82.7485f)), 0.001f);
        }

        [Test]
        public void Ssim_IdenticalImages_ScoreOne()
        {
            int w = 64, h = 64;
            var luma = new float[w * h];
            for (int i = 0; i < luma.Length; i++) luma[i] = Random.value;
            var mask = new float[w * h];
            for (int i = 0; i < mask.Length; i++) mask[i] = 1f;

            using var res = new NativeArray<float>(1, Allocator.TempJob);
            var job = new SsimJob
            {
                refLuma = new NativeArray<float>(luma, Allocator.TempJob),
                testLuma = new NativeArray<float>(luma, Allocator.TempJob),
                mask = new NativeArray<float>(mask, Allocator.TempJob),
                width = w, height = h, singleScale = false, result = res,
            };
            job.Schedule().Complete();
            Assert.GreaterOrEqual(res[0], 0.999f);
        }

        [Test]
        public void DownsampleUpsample_IsIdentityAtScaleOne()
        {
            int w = 32, h = 32;
            var src = new Color32[w * h];
            for (int i = 0; i < src.Length; i++)
                src[i] = new Color32((byte)(i * 7 % 256), (byte)(i * 13 % 256), (byte)(i * 29 % 256), 255);

            using var s = new NativeArray<Color32>(src, Allocator.TempJob);
            using var d = new NativeArray<Color32>(w * h, Allocator.TempJob);
            using var size = new NativeArray<int2>(1, Allocator.TempJob);
            size[0] = new int2(w, h);
            new DownsampleJob
            {
                src = s, srcW = w, srcH = h, region = new int4(0, 0, w, h),
                premultiply = false, srgb = false, dst = d, dstSize = size,
            }.Schedule().Complete();
            for (int i = 0; i < src.Length; i++)
            {
                Assert.AreEqual(src[i].r, d[i].r);
                Assert.AreEqual(src[i].g, d[i].g);
                Assert.AreEqual(src[i].b, d[i].b);
            }
        }

        [Test]
        public void GrayRmse_ReportsWorstUsedChannel()
        {
            int n = 16;
            var a = new Color32[n];
            var b = new Color32[n];
            for (int i = 0; i < n; i++)
            {
                a[i] = new Color32(100, 100, 100, 255);
                b[i] = new Color32(100, 104, 100, 255); // only G differs by 4/255
            }

            var mask = new float[n];
            for (int i = 0; i < n; i++) mask[i] = 1f;
            using var res = new NativeArray<float>(1, Allocator.TempJob);
            new GrayRmseJob
            {
                refPx = new NativeArray<Color32>(a, Allocator.TempJob),
                testPx = new NativeArray<Color32>(b, Allocator.TempJob),
                mask = new NativeArray<float>(mask, Allocator.TempJob),
                usedChannels = new bool4(true, true, false, false),
                result = res,
            }.Schedule().Complete();
            Assert.AreEqual(4f / 255f, res[0], 1e-4f);
        }
    }

    public class UtilTests
    {
        [Test]
        public void UnionFind_Merges()
        {
            var uf = new UnionFind(5);
            uf.Union(0, 1);
            uf.Union(1, 2);
            Assert.AreEqual(uf.Find(0), uf.Find(2));
            Assert.AreNotEqual(uf.Find(0), uf.Find(3));
            Assert.AreEqual(3, uf.ComponentCount);
        }

        [Test]
        public void MiniJson_ParsesNested()
        {
            var o = (Dictionary<string, object>)MiniJson.Parse(
                "{\"a\":1,\"b\":[true,null,\"x\"],\"c\":{\"d\":-2.5},\"e\":\"\\u4e2d\\u6587\"}");
            Assert.AreEqual(1L, o["a"]);
            Assert.AreEqual("x", ((List<object>)o["b"])[2]);
            Assert.AreEqual(-2.5, ((Dictionary<string, object>)o["c"])["d"]);
            Assert.AreEqual("中文", o["e"]);
        }

        [Test]
        public void MapUv_MapsIntoNormalizedRect()
        {
            var isl = new UvIsland();
            isl.uvBounds = Rect.MinMaxRect(0.25f, 0.25f, 0.75f, 0.75f);
            var r = new Rect(0.5f, 0.5f, 0.25f, 0.25f);
            var p = MeshRewriter.MapUv(new Vector2(0.25f, 0.25f), isl, r, false, Vector2.zero);
            Assert.AreEqual(0.5f, p.x, 1e-5f);
            Assert.AreEqual(0.5f, p.y, 1e-5f);
            var q = MeshRewriter.MapUv(new Vector2(0.75f, 0.75f), isl, r, false, Vector2.zero);
            Assert.AreEqual(0.75f, q.x, 1e-5f);
            // rotated: axes swapped / 旋转：轴交换 (u=1,v=0 -> (0,1) in rect)
            var rot = MeshRewriter.MapUv(new Vector2(0.75f, 0.25f), isl, r, true, Vector2.zero);
            Assert.AreEqual(0.5f, rot.x, 1e-5f);
            Assert.AreEqual(0.75f, rot.y, 1e-5f);
        }
    }
}
