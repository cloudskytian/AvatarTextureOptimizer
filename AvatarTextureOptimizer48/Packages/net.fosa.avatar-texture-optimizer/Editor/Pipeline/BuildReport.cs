// Build report: collected at the end of a run and printed to the NDMF console (Unity console).
// / 构建报告：运行结束时汇总并输出到 NDMF 控制台（Unity 控制台）。
// Summary is printed by default; details are folded (only printed when verbose).
// / 默认打印总体结果；细节仅在 verbose 时展开。

using System;
using System.Collections.Generic;
using System.Text;

namespace net.fosa.avatar_texture_optimizer.editor.pipeline
{
    /// <summary>
    /// Timing + statistics for one pipeline stage. / 单个流水线阶段的耗时与统计。
    /// </summary>
    public sealed class StageStat
    {
        public string Name;
        public double Seconds;
        public readonly List<string> Notes = new List<string>();
    }

    /// <summary>
    /// Per-atlas statistics. / 单个图集的统计信息。
    /// </summary>
    public sealed class AtlasStat
    {
        public string Name;
        public int Width;
        public int Height;
        public int IslandCount;
        public double Utilization;            // 0~1 / 利用率
        public string SourceTextures;         // texture sources / 贴图来源
        public long OriginalTexelCount;       // source texels / 原贴图像素数
        public long AtlasTexelCount;          // atlas texels / 图集像素数
        public double SavingsRatio;           // 1 - atlas/source / 相对原贴图的节省比例
    }

    /// <summary>
    /// Final report model. / 最终报告模型。
    /// </summary>
    public sealed class BuildReport
    {
        public readonly List<StageStat> Stages = new List<StageStat>();
        public readonly List<AtlasStat> Atlases = new List<AtlasStat>();
        public int ProcessedIslands;
        public int ProcessedTextures;
        public int SkippedAsWhitelist;
        public readonly List<string> WarningMessages = new List<string>();
        public long SourceTotalTexels;
        public long OutputTotalTexels;
        public bool Cancelled;
        public string ErrorMessage;

        public void AddStage(string name, Action<StageStat> work)
        {
            var stat = new StageStat { Name = name };
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                work(stat);
            }
            finally
            {
                sw.Stop();
                stat.Seconds = sw.Elapsed.TotalSeconds;
                Stages.Add(stat);
            }
        }

        /// <summary>
        /// Render the report as text for the NDMF console. / 把报告渲染为文本输出到控制台。
        /// </summary>
        public string Render(bool verbose)
        {
            var sb = new StringBuilder();
            sb.AppendLine("================ [ATO] Avatar Texture Optimizer Report ================");
            if (Cancelled) sb.AppendLine("STATUS: CANCELLED (temporary assets kept) / 已取消（临时资产保留）");
            else if (ErrorMessage != null) sb.AppendLine("STATUS: FAILED - " + ErrorMessage);
            else sb.AppendLine("STATUS: SUCCESS / 成功");

            double total = 0;
            foreach (var s in Stages) total += s.Seconds;
            sb.AppendLine(string.Format("Total time: {0:F2}s / 总耗时 {0:F2}s", total));

            sb.AppendLine(string.Format(
                "Islands processed: {0} | Textures processed: {1} | Whitelist skipped: {2} | Warnings: {3}",
                ProcessedIslands, ProcessedTextures, SkippedAsWhitelist, WarningMessages.Count));

            double totalSavings = SourceTotalTexels > 0 ? (1.0 - (double)OutputTotalTexels / SourceTotalTexels) : 0;
            sb.AppendLine(string.Format(
                "Texels: {0:N0} -> {1:N0}  (savings {2:P1}) / 像素数：{0:N0} -> {1:N0}（节省 {2:P1}）",
                SourceTotalTexels, OutputTotalTexels, totalSavings));

            if (verbose)
            {
                sb.AppendLine("---- Stage timings / 各阶段耗时 ----");
                foreach (var s in Stages)
                {
                    sb.AppendLine(string.Format("  [{0,6:F2}s] {1}", s.Seconds, s.Name));
                    foreach (var n in s.Notes) sb.AppendLine("      - " + n);
                }

                if (Atlases.Count > 0)
                {
                    sb.AppendLine("---- Atlases / 图集 ----");
                    foreach (var a in Atlases)
                    {
                        sb.AppendLine(string.Format(
                            "  {0}  {1}x{2}  islands={3}  util={4:P1}  savings={5:P1}  from={6}",
                            a.Name, a.Width, a.Height, a.IslandCount, a.Utilization, a.SavingsRatio, a.SourceTextures));
                    }
                }
            }
            else
            {
                sb.AppendLine(string.Format(
                    "Atlases generated: {0} (total {1:N0} texels, savings {2:P1}) / 生成图集 {0} 张（共 {1:N0} 像素，节省 {2:P1}）",
                    Atlases.Count, OutputTotalTexels, totalSavings));
                sb.AppendLine("(enable verboseLogs on the component for detailed report / 组件上开启 verboseLogs 可查看详细报告)");
            }
            sb.AppendLine("========================================================================");
            return sb.ToString();
        }
    }
}
