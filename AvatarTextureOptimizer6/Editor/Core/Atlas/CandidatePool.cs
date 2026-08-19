using System;
using System.Collections.Generic;
using System.Linq;
using NetFosa.AvatarTextureOptimizer.Editor.Logging;

namespace NetFosa.AvatarTextureOptimizer.Editor.Atlas
{
    /// <summary>
    /// 候选图集池：
    /// - 默认（POT）：以 2 的 n 次幂为边长，最小 64，最大 8192（移动端 4096），生成多个候选图集（允许非正方形）。
    /// - 实验性 NPOT：以 64 为边长步进，上限同上；NPOT 下调用方需剔除 PVRTC 等不支持格式。
    /// 候选按面积升序、长边/短边升序（最接近正方形优先）排序。
    /// </summary>
    public sealed class CandidatePool
    {
        public struct Candidate
        {
            public int width;
            public int height;
            public long area;
            public float aspect; // 长边/短边

            public int MaxSide => Math.Max(width, height);
        }

        public readonly List<Candidate> Entries = new List<Candidate>();
        public readonly int MaxSide;

        public CandidatePool(bool npot, bool mobile, int minSide = 64, int maxSide = -1)
        {
            int upper = maxSide > 0 ? maxSide : (mobile ? 4096 : 8192);
            MaxSide = upper;
            var sizes = new HashSet<int>();

            if (npot)
            {
                for (int s = minSide; s <= upper; s += 64) sizes.Add(s);
            }
            else
            {
                for (int s = minSide; s <= upper; s *= 2) sizes.Add(s);
            }

            // 生成候选（正方形 + 常见 2:1 非正方形）
            foreach (var s in sizes)
            {
                Add(s, s);
                int half = s / 2;
                if (half >= minSide && sizes.Contains(half)) Add(s, half); // 2:1
            }

            Entries.Sort((a, b) =>
            {
                int c = a.area.CompareTo(b.area);
                if (c != 0) return c;
                return a.aspect.CompareTo(b.aspect);
            });
        }

        private void Add(int w, int h)
        {
            if (w < 64 || h < 64) return;
            Entries.Add(new Candidate
            {
                width = w,
                height = h,
                area = (long)w * h,
                aspect = Math.Max(w, h) / (float)Math.Min(w, h),
            });
        }

        /// <summary>返回面积不小于 minArea 的候选（已按面积/长宽比排序）。</summary>
        public IEnumerable<Candidate> CandidatesWithAreaAtLeast(long minArea)
        {
            return Entries.Where(e => e.area >= minArea);
        }

        /// <summary>图集 padding：max(最小 padding, ceil(图集最大边/128))。</summary>
        public static int ComputePadding(int atlasMaxSide, int minPadding)
        {
            int autoPad = (atlasMaxSide + 127) / 128;
            return Math.Max(minPadding, Math.Max(4, autoPad));
        }
    }
}
