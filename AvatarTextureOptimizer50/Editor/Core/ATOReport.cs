// -----------------------------------------------------------------------------
// ATOReport.cs — build report data + NDMF console presentation.
// ATOReport.cs — 构建报告数据与 NDMF 控制台展示。
// -----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using nadena.dev.ndmf;
using UnityEngine;

namespace net.fosa.ato.editor
{
    /// <summary>Collects statistics during the run and renders the final report.
    /// 运行期间收集统计，并渲染最终报告。</summary>
    internal sealed class ATOReport
    {
        public long originalPixels;
        public long optimizedPixels;
        public int textureCount;
        public int atlasCount;
        public int islandCount;
        public int scaledIslandCount;
        public int pureColorIslandCount;
        public int losslessIslandCount;
        public int whitelistedTextureCount;
        public int dedupedTextureCount;
        public int mergedMaterialCount;
        public int mergedSlotCount;
        public int fallbackWholeScaleCount;
        public readonly List<string> warnings = new List<string>();
        public readonly List<AtlasResult> atlases = new List<AtlasResult>();

        public void AddWarning(string w)
        {
            warnings.Add(w);
            ATOLog.Warn(w);
        }

        public string SummaryTitle
        {
            get
            {
                double saved = originalPixels > 0
                    ? 100.0 * (1.0 - (double)optimizedPixels / originalPixels)
                    : 0;
                return ATOLocalization.F("Report:Title",
                    textureCount, atlasCount, islandCount, $"{saved:F1}");
            }
        }

        /// <summary>Compact multi-line details for the NDMF console entry.
        /// 供 NDMF 控制台使用的紧凑多行明细。</summary>
        public string BuildDetails(IReadOnlyList<(string stage, double ms)> timings)
        {
            var sb = new StringBuilder();

            // Summary / 总览
            sb.AppendLine(ATOLocalization.F("Report:Pixels", originalPixels, optimizedPixels));
            sb.AppendLine(ATOLocalization.F("Report:Islands", islandCount, scaledIslandCount,
                pureColorIslandCount, losslessIslandCount));
            sb.AppendLine(ATOLocalization.F("Report:Counts", whitelistedTextureCount,
                dedupedTextureCount, mergedMaterialCount, mergedSlotCount, fallbackWholeScaleCount));

            // Atlas table / 图集表
            sb.AppendLine(ATOLocalization.L("Report:AtlasTable"));
            foreach (var a in atlases)
            {
                var srcNames = string.Join(",",
                    a.islands.Select(i => i.group.owner.renderer.name).Distinct().Take(6));
                sb.AppendLine(ATOLocalization.F("Report:AtlasRow",
                    a.id, a.width, a.height,
                    $"{(a.baseLayer?.usedRatio ?? 0) * 100f:F1}",
                    a.islands.Count, srcNames));
                foreach (var layer in a.layers)
                    sb.AppendLine("    " + ATOLocalization.F("Report:LayerRow",
                        layer.kind.ToString(), layer.sourceTex != null ? layer.sourceTex.source.name : "-",
                        layer.width, layer.height, $"{layer.scaleVsBase:F2}"));
            }

            // Stage timings / 阶段耗时
            if (timings != null && timings.Count > 0)
            {
                sb.AppendLine(ATOLocalization.L("Report:Timings"));
                foreach (var (stage, ms) in timings)
                    sb.AppendLine($"    {stage}: {ms:F1} ms");
            }

            // Warnings / 警告
            if (warnings.Count > 0)
            {
                sb.AppendLine(ATOLocalization.F("Report:Warnings", warnings.Count));
                foreach (var w in warnings.Take(30)) sb.AppendLine("    " + w);
                if (warnings.Count > 30)
                    sb.AppendLine($"    ... (+{warnings.Count - 30})");
            }

            return sb.ToString();
        }

        /// <summary>Emit the report into the NDMF error console (Information severity).
        /// 将报告输出到 NDMF 控制台（Information 级别）。</summary>
        public void PublishToNdmfConsole(IReadOnlyList<(string stage, double ms)> timings,
            GameObject avatarRoot)
        {
            var error = new ATOReportError(this, timings);
            if (avatarRoot != null)
                error._references.Add(ObjectRegistry.GetReference(avatarRoot));
            ErrorReport.ReportError(error);
        }

        /// <summary>Full plain-text dump for the Unity console / 输出到 Unity 控制台的完整文本。</summary>
        public string BuildFullLog(IReadOnlyList<(string stage, double ms)> timings)
        {
            var sb = new StringBuilder();
            sb.AppendLine(SummaryTitle);
            sb.Append(BuildDetails(timings));
            return sb.ToString();
        }
    }

    /// <summary>SimpleError subclass showing summary in the title, details in the description.
    /// SimpleError 子类：标题显示总览，描述显示明细。</summary>
    internal sealed class ATOReportError : SimpleError
    {
        private readonly ATOReport _report;
        private readonly IReadOnlyList<(string stage, double ms)> _timings;

        public ATOReportError(ATOReport report, IReadOnlyList<(string stage, double ms)> timings)
        {
            _report = report;
            _timings = timings;
        }

        public override Localizer Localizer => ATOLocalization.NdmfLocalizer;
        public override string TitleKey => "Report:Title";
        public override ErrorSeverity Severity => ErrorSeverity.Information;

        public override string[] TitleSubst => new[]
        {
            _report.textureCount.ToString(), _report.atlasCount.ToString(),
            _report.islandCount.ToString(),
            (_report.originalPixels > 0
                ? (100.0 * (1.0 - (double)_report.optimizedPixels / _report.originalPixels)).ToString("F1")
                : "0")
        };

        public override string DetailsKey => "Report:Details";
        public override string[] DetailsSubst => new[] { _report.BuildDetails(_timings) };
    }
}
