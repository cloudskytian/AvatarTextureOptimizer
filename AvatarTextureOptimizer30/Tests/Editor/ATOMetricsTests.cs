// ATOMetricsTests.cs — 质量度量单元测试（CIEDE2000 标准样本等）/ Quality metric unit tests (CIEDE2000 reference pairs etc.).
// CIEDE2000 参考值取自 Sharma, Wu & Dalal (2005) 的标准测试数据集。
// CIEDE2000 reference values from the standard test dataset of Sharma, Wu & Dalal (2005).

using System.Collections.Generic;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;

namespace Fosa.AvatarTextureOptimizer
{
    [TestFixture]
    public class ATOMetricsTests
    {
        // (L1,a1,b1, L2,a2,b2, ΔE00) — Sharma 2005 数据集样本 / samples from the Sharma 2005 dataset
        private static readonly (float, float, float, float, float, float, double)[] SharmaPairs =
        {
            (50.0000f, 2.6772f, -79.7751f, 50.0000f, 0.0000f, -82.7485f, 2.0425),
            (50.0000f, 3.1571f, -77.2803f, 50.0000f, 0.0000f, -82.7485f, 2.8615),
            (50.0000f, 2.8361f, -74.0200f, 50.0000f, 0.0000f, -82.7485f, 3.4412),
            (50.0000f, -1.3802f, -84.2814f, 50.0000f, 0.0000f, -82.7485f, 1.0000),
            (50.0000f, -1.1848f, -84.8006f, 50.0000f, 0.0000f, -82.7485f, 1.0000),
            (50.0000f, -0.9009f, -85.5211f, 50.0000f, 0.0000f, -82.7485f, 1.0000),
            (50.0000f, 0.0000f, 0.0000f, 50.0000f, -1.0000f, 2.0000f, 2.3669),
            (50.0000f, -1.0000f, 2.0000f, 50.0000f, 0.0000f, 0.0000f, 2.3669),
            (50.0000f, 2.4900f, -0.0010f, 50.0000f, -2.4900f, 0.0009f, 7.1792),
            (50.0000f, 2.4900f, -0.0010f, 50.0000f, -2.4900f, 0.0010f, 7.1792),
            (50.0000f, 2.4900f, -0.0010f, 50.0000f, -2.4900f, 0.0011f, 7.2195),
            (60.2574f, -34.0099f, 36.2677f, 60.4626f, -34.1751f, 39.4387f, 1.2644),
            (63.0109f, -31.0961f, -5.8663f, 62.8187f, -29.7946f, -4.0864f, 1.2630),
        };

        [Test]
        public void Ciede2000_SharmaReferencePairs()
        {
            foreach (var (l1, a1, b1, l2, a2, b2, expected) in SharmaPairs)
            {
                var de = ATOMetrics.Ciede2000(new float3(l1, a1, b1), new float3(l2, a2, b2));
                Assert.AreEqual(expected, de, 0.0005, $"ΔE00 mismatch for Lab1=({l1},{a1},{b1}) Lab2=({l2},{a2},{b2})");
            }
        }

        [Test]
        public void LinearToLab_WhiteIsL100()
        {
            var lab = ATOMetrics.LinearToLab(new float3(1f, 1f, 1f));
            Assert.AreEqual(100.0f, lab.x, 0.01f);
            Assert.AreEqual(0.0f, lab.y, 0.05f);
            Assert.AreEqual(0.0f, lab.z, 0.05f);
        }

        [Test]
        public void SrgbLinearRoundtrip()
        {
            var values = new[] { 0f, 0.0031308f, 0.25f, 0.5f, 0.75f, 1f };
            foreach (var v in values)
            {
                var back = ATOMetrics.LinearToSrgb(ATOMetrics.SrgbToLinear(v));
                Assert.AreEqual(v, back, 1e-4f);
            }
        }

        [Test]
        public void Percentile95()
        {
            var arr = new NativeArray<float>(100, Allocator.Temp);
            for (int i = 0; i < 100; i++) arr[i] = i;
            var p95 = ATOMetrics.Percentile95(arr);
            arr.Dispose();
            Assert.AreEqual(94f, p95, 0.001f);
        }

        [Test]
        public void IsSolid()
        {
            var solid = new NativeArray<float4>(16, Allocator.Temp);
            for (int i = 0; i < solid.Length; i++) solid[i] = new float4(0.5f, 0.25f, 0.125f, 1f);
            Assert.IsTrue(ATOMetrics.IsSolid(solid));

            solid[7] = new float4(0.9f, 0.25f, 0.125f, 1f);
            Assert.IsFalse(ATOMetrics.IsSolid(solid));
            solid.Dispose();
        }

        [Test]
        public void Resize_HalfPreservesSolidColor()
        {
            var src = new NativeArray<float4>(8 * 8, Allocator.Temp);
            for (int i = 0; i < src.Length; i++) src[i] = new float4(1f, 0f, 0f, 1f);
            var half = ATOMetrics.Resize(src, 8, 8, 4, 4, Allocator.Temp);
            for (int i = 0; i < half.Length; i++)
                Assert.AreEqual(1f, half[i].x, 1e-6f);
            half.Dispose();
            src.Dispose();
        }
    }
}
