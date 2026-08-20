using System.Text;
using nadena.dev.ndmf;

namespace net.fosa.avatar_texture_optimizer.editor
{
    /// <summary>
    /// Builds and shows the final ATO report in the NDMF console. / 构建最终报告并显示到 NDMF 控制台。
    /// The overall summary is always visible; per-atlas details are collapsed in a foldout. /
    /// 总体结果始终展示，具体细节折叠在折叠栏中。
    /// </summary>
    internal static class AtoReport
    {
        /// <summary>
        /// Write the final report: NDMF console entry + [ATO] console log. / 输出最终报告：NDMF 控制台条目 + [ATO] 控制台日志。
        /// </summary>
        public static void Write(AtoBuildState state)
        {
            state.TotalStopwatch.Stop();

            var sb = new StringBuilder();
            sb.AppendLine(state.Tr("report.summary",
                state.TextureCount, state.UvGroupCount, state.IslandCount, state.AtlasCount,
                state.BytesBefore, state.BytesAfter,
                state.BytesBefore > 0 ? (int)(100 - 100.0 * state.BytesAfter / state.BytesBefore) : 0,
                state.TotalStopwatch.ElapsedMilliseconds));
            if (state.WarningCount > 0 || state.ErrorCount > 0)
            {
                sb.AppendLine(state.Tr("report.warningCount", state.WarningCount, state.ErrorCount));
            }

            var details = new StringBuilder();
            details.AppendLine(state.Tr("report.details"));
            foreach (var record in state.AtlasRecords)
            {
                details.AppendLine("  " + state.Tr("report.atlasLine",
                    record.Category, record.Name, record.SourceTextureCount, record.IslandCount,
                    record.Width, record.Height,
                    (int)(record.Utilization * 100), (int)record.SavedPercent));
            }
            foreach (var record in state.TextureRecords)
            {
                details.AppendLine("  " + state.Tr("report.textureLine",
                    record.Name, record.BytesBefore, record.BytesAfter, (int)record.SavedPercent, record.Reason));
            }
            if (state.Notes.Count > 0)
            {
                foreach (var note in state.Notes)
                {
                    details.AppendLine("  - " + note);
                }
            }

            // Console log (Unity log). / 控制台日志。
            AtoLog.Summary(sb.ToString().TrimEnd());
            if (state.Settings.logLevel >= AtoLogLevel.Normal && state.AtlasRecords.Count > 0)
            {
                AtoLog.Info(details.ToString().TrimEnd());
            }

            // NDMF console entry (summary always visible, details folded). / NDMF 控制台条目（摘要常显，细节折叠）。
            var error = new AtoConsoleEntry(sb.ToString().TrimEnd(), details.ToString().TrimEnd(),
                ErrorSeverity.Information);
            if (state.Component != null)
            {
                error.AddReference(ObjectRegistry.GetReference(state.Component));
            }
            ErrorReport.ReportError(error);
        }
    }
}
