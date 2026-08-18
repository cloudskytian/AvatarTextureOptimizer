// Avatar Texture Optimizer (ATO)
// Writes the final report to the NDMF console AND the NDMF report window: a summary is
// logged, warnings are emitted as non-fatal errors, and a collapsible Information entry
// shows per-atlas statistics (sources, island counts, size, utilization).
// 把最终报告写入 NDMF 控制台与 NDMF 报告窗口：汇总记入日志，告警以非致命错误呈现，
// 一条可折叠的 Information 条目展示逐图集统计（来源、岛数、尺寸、利用率）。

using System.Text;
using nadena.dev.ndmf;
using UnityEngine;

namespace NetFosa.ATO
{
    /// <summary>
    /// Stage 9a: report writing. / 阶段 9a：报告写入。
    /// </summary>
    public static class ATOBuildReportWriter
    {
        public static void Write(ATOBuildContext build)
        {
            var r = build.report;

            // Warnings as non-fatal NDMF errors. / 告警以非致命 NDMF 错误呈现。
            foreach (var w in r.warnings)
            {
                var err = new ATOInlineError(ErrorSeverity.NonFatal, "warn.generic");
                ErrorReport.ReportError(err);
                ATOLogger.Warn(w);
            }

            // Compute derived stats. / 计算派生统计。
            int atlasedIslands = 0;
            foreach (var a in build.atlases) atlasedIslands += a.islandCount;
            r.islandCountSkipped = r.islandCount - atlasedIslands;

            // Console summary. / 控制台汇总。
            var summary = new StringBuilder();
            summary.AppendLine("======== ATO (Avatar Texture Optimizer) Report ========");
            summary.AppendLine($"Renderers: {r.rendererCount} | Material slots: {r.materialSlotCount}");
            summary.AppendLine($"Textures: {r.textureCountBeforeDedup} -> {r.textureCountAfterDedup} after dedup");
            summary.AppendLine($"UV islands: {r.islandCount} (atlased: {atlasedIslands}, skipped: {r.islandCountSkipped})");
            summary.AppendLine($"Atlases: {build.atlases.Count}");
            summary.AppendLine($"Whitelisted/skipped textures: {r.whitelistedTextureCount}");
            summary.AppendLine($"Warnings: {r.warnings.Count}");
            summary.AppendLine($"Total time: {r.totalTimeMs / 1000.0:F2}s");
            ATOLogger.Info(summary.ToString());

            foreach (var a in build.atlases)
                ATOLogger.Debug($"  Atlas '{a.name}': {a.width}x{a.height}, sources={a.sources.Count}, islands={a.islandCount}, utilization={a.utilization:P1}");

            // NDMF report window entry: Information severity, collapsible details. / 报告窗口条目：Information 级、可折叠详情。
            var detail = BuildDetail(build, summary.ToString());
            ErrorReport.ReportError(new ATOReportError(detail));
        }

        /// <summary>
        /// Information-level report entry whose collapsible detail holds the full report.
        /// 详情区承载完整报告的 Information 级报告条目。
        /// </summary>
        private sealed class ATOReportError : SimpleError
        {
            private readonly string[] _details;
            public ATOReportError(string details) { _details = new[] { details }; }
            public override nadena.dev.ndmf.localization.Localizer Localizer => ATOI18n.NdmfLocalizer;
            public override string TitleKey => "report.title";
            public override ErrorSeverity Severity => ErrorSeverity.Information;
            public override string[] DetailsSubst => _details;
        }

        private static string BuildDetail(ATOBuildContext build, string summary)
        {
            var sb = new StringBuilder();
            sb.AppendLine(summary);
            sb.AppendLine();
            sb.AppendLine("--- Atlases / 图集 ---");
            foreach (var a in build.atlases)
            {
                sb.AppendLine($"  {a.name}  {a.width}x{a.height}  sources={a.sources.Count}  islands={a.islandCount}  utilization={a.utilization:P1}  alpha={a.hasAlpha}");
                foreach (var s in a.sources)
                    sb.AppendLine($"      <- {s.texture.name} ({s.width}x{s.height})");
            }
            int shown = 0;
            foreach (var iq in build.report.islandQuality)
            {
                if (shown++ >= 100) { sb.AppendLine($"  ... and {build.report.islandQuality.Count - 100} more islands"); break; }
                sb.AppendLine($"  Island {iq.islandId}: {iq.limitingMetric}={iq.worstMetric:F4}, texels {iq.originalTexels} -> {iq.scaledTexels}");
            }
            return sb.ToString();
        }
    }
}
