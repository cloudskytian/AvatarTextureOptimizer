// ATOBitmaskTests.cs — 位掩码与装箱基础单元测试 / Bitmask & packing primitive unit tests.

using System.Collections.Generic;
using NUnit.Framework;
using Unity.Collections;

namespace Fosa.AvatarTextureOptimizer
{
    [TestFixture]
    public class ATOBitmaskTests
    {
        [Test]
        public void FillRect_CountBits()
        {
            using var mask = new ATOBitmask(32, 32, Allocator.TempJob);
            ATOBitmaskOps.FillRect(mask, 2, 3, 5, 9); // 4x7 cells / 4×7 格
            Assert.AreEqual(4L * 7L, mask.CountBits());
        }

        [Test]
        public void Rotate90_FourTimesIsIdentity()
        {
            using var mask = new ATOBitmask(16, 8, Allocator.TempJob);
            ATOBitmaskOps.FillRect(mask, 1, 1, 4, 3);
            var r1 = ATOBitmaskOps.Rotate90(mask);
            var r2 = ATOBitmaskOps.Rotate90(r1);
            var r3 = ATOBitmaskOps.Rotate90(r2);
            var r4 = ATOBitmaskOps.Rotate90(r3);
            Assert.AreEqual(mask.cellsW, r4.cellsW);
            Assert.AreEqual(mask.cellsH, r4.cellsH);
            for (int i = 0; i < mask.bits.Length; i++)
                Assert.AreEqual(mask.bits[i], r4.bits[i]);
            r1.Dispose();
            r2.Dispose();
            r3.Dispose();
            r4.Dispose();
        }

        [Test]
        public void FitsAt_And_Stamp()
        {
            using var occ = new ATOBitmask(64, 64, Allocator.TempJob);
            using var item = new ATOBitmask(8, 8, Allocator.TempJob);
            ATOBitmaskOps.FillRect(item, 0, 0, 3, 5);

            Assert.IsTrue(ATOBitmaskOps.FitsAt(occ.bits, occ.stride, occ.cellsW, occ.cellsH, item.bits, item.stride, item.cellsW, item.cellsH, 10, 10));
            ATOBitmaskOps.Stamp(occ.bits, occ.stride, item.bits, item.stride, item.cellsW, item.cellsH, 10, 10);
            Assert.IsFalse(ATOBitmaskOps.FitsAt(occ.bits, occ.stride, occ.cellsW, occ.cellsH, item.bits, item.stride, item.cellsW, item.cellsH, 11, 10));
            Assert.IsFalse(ATOBitmaskOps.FitsAt(occ.bits, occ.stride, occ.cellsW, occ.cellsH, item.bits, item.stride, item.cellsW, item.cellsH, 10, 12));
            Assert.IsFalse(ATOBitmaskOps.FitsAt(occ.bits, occ.stride, occ.cellsW, occ.cellsH, item.bits, item.stride, item.cellsW, item.cellsH, 60, 60));
        }

        [Test]
        public void TryPlaceBlf_BottomLeftPreference()
        {
            using var occ = new ATOBitmask(64, 64, Allocator.TempJob);
            using var item = new ATOBitmask(4, 4, Allocator.TempJob);
            ATOBitmaskOps.FillRect(item, 0, 0, 1, 1);
            Assert.IsTrue(ATOBitmaskOps.TryPlaceBlf(occ.bits, occ.stride, occ.cellsW, occ.cellsH, item.bits, item.stride, item.cellsW, item.cellsH, out var x, out var y));
            Assert.AreEqual(0, x);
            Assert.AreEqual(0, y);
        }

        [Test]
        public void CandidatePool_Ordering()
        {
            var packer = new ATOAtlasPacker(1024, false, 4);
            // padding 公式：max(用户挡位, ceil(maxSide/128)) / padding formula
            Assert.AreEqual(4, packer.PaddingFor(256, 256));       // ceil(256/128)=2 → max(4,2)=4
            Assert.AreEqual(8, packer.PaddingFor(1024, 1024));     // ceil(1024/128)=8
            Assert.AreEqual(16, packer.PaddingFor(2048, 64));      // max side 2048 → 16
            packer.Dispose();
        }
    }
}
