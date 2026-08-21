using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

// Build report: per-stage timings, atlas stats, island counts, savings.
// 构建报告：各阶段耗时、图集统计、岛数量、优化量。

namespace Net.Fosa.AvatarTextureOptimizer
{
    public sealed class ATOBuildReport
    {
        public sealed class StageTime
        {
            public string Name;
            public double Seconds;
        }

        public readonly List<StageTime> Stages = new List<StageTime>();
        private readonly Stopwatch _stageSw = new Stopwatch();

        public int TotalIslands;
        public int ScaledIslands;
        public int SkippedIslands;         // whitelist / fallback. 白名单/回退。
        public int AtlasCount;
        public long OriginalPixelBytes;
        public long FinalPixelBytes;
        public long OriginalIslandPixelArea;
        public long FinalIslandPixelArea;
        public double TotalAtlasUtilization;

        public void BeginStage(string name) { _stageSw.Restart(); Stages.Add(new StageTime { Name = name }); }
        public void EndStage() { if (Stages.Count > 0) Stages[^1].Seconds = _stageSw.Elapsed.TotalSeconds; }

        public double TotalSeconds
        {
            get { double t = 0; foreach (var s in Stages) t += s.Seconds; return t; }
        }

        /// <summary>
        /// Renders the console report block (default shows summary; details are folded in the console log).
        /// 生成控制台报告块（默认摘要；细节在日志中折叠展示）。
        /// </summary>
        public string RenderSummary()
        {
            var sb = new StringBuilder();
            sb.AppendLine("[ATO] ===== AvatarTextureOptimizer Build Report =====");
            foreach (var s in Stages)
                sb.AppendLine($"[ATO]   {s.Name,-28} {s.Seconds,8:F2}s");
            sb.AppendLine($"[ATO]   Total                             {TotalSeconds,8:F2}s");
            sb.AppendLine($"[ATO]   Islands: {TotalIslands} (scaled {ScaledIslands}, skipped {SkippedIslands})");
            sb.AppendLine($"[ATO]   Atlases: {AtlasCount}, avg utilization {TotalAtlasUtilization / Math.Max(1, AtlasCount) * 100f:F1}%");
            sb.AppendLine($"[ATO]   Island pixel area: {OriginalIslandPixelArea:N0} -> {FinalIslandPixelArea:N0} ({(1.0 - (double)FinalIslandPixelArea / Math.Max(1, OriginalIslandPixelArea)) * 100f:F1}% reduction)");
            sb.AppendLine("[ATO] ===== End of AvatarTextureOptimizer Build Report =====");
            return sb.ToString();
        }
    }
}
