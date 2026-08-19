// ATO — Avatar Texture Optimizer
// Candidate atlas size pool: powers-of-two by default, or NPOT (64px steps) when enabled.
// 候选图集尺寸池：默认 2 的 n 次幂，勾选 NPOT 时按 64px 步进。

using System.Collections.Generic;
using net.fosa.ato;

namespace net.fosa.ato.editor
{
    /// <summary>
    /// Candidate atlas sizes. 候选图集尺寸。
    /// </summary>
    public static class AtlasPool
    {
        public const int MinEdge = 64;

        /// <summary>Maximum atlas edge for a platform (mobile caps at 4096). 各平台最大边长（移动端上限 4096）。</summary>
        public static int MaxEdgeFor(ATOPlatform platform)
        {
            return platform == ATOPlatform.PC ? 8192 : 4096;
        }

        /// <summary>
        /// Candidate sizes from 64 up to maxEdge. 从 64 到 maxEdge 的候选尺寸。
        /// </summary>
        public static List<int> Candidates(int maxEdge, bool npot)
        {
            var list = new List<int>();
            if (npot)
            {
                for (int e = MinEdge; e <= maxEdge; e += 64) list.Add(e);
            }
            else
            {
                for (int e = MinEdge; e <= maxEdge; e *= 2) list.Add(e);
                if (list.Count == 0 || list[list.Count - 1] != maxEdge) list.Add(maxEdge);
            }
            return list;
        }

        /// <summary>
        /// Effective island padding: max(user minimum, ceil(maxEdge/128)) clamped to >= 4px.
        /// 有效岛间距：max(用户最小值, ceil(maxEdge/128))，下钳到 4px。
        /// </summary>
        public static int EffectivePadding(int maxEdge, int userMinPadding)
        {
            int auto = MathfMax(4, CeilDiv(maxEdge, 128));
            return MathfMax(userMinPadding, auto);
        }

        private static int MathfMax(int a, int b) => a > b ? a : b;
        private static int CeilDiv(int a, int b) => (a + b - 1) / b;
    }
}
