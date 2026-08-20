// ATOReport.cs — NDMF 控制台报告 / NDMF console report.
// 说明：烘焙完成后在 NDMF 控制台显示报告（默认展示总体结果，具体细节折叠）：
// 总体：处理贴图/岛/图集数量、总耗时、相对原贴图的优化量（字节/百分比）、评估次数；
// 细节（折叠）：每阶段耗时、每张图集的贴图来源、岛数量、尺寸、利用率、格式、各贴图优化量。
// 实现基于 NDMF ErrorReport.ReportError(IError)（已读 NDMF 源码验证：会输出到 NDMF 错误窗口与 Debug 日志）。
// Note: shows a report in the NDMF console after the build (summary by default, details collapsed):
// totals (textures/islands/atlases, total time, byte/percent savings vs originals, evaluation count) and folded
// details (per-stage timings; per-atlas sources, island counts, sizes, utilization, format; per-texture savings).
// Implemented via NDMF ErrorReport.ReportError(IError) (verified in NDMF source: surfaces in the NDMF error window & debug log).

using System;
using System.Collections.Generic;
using System.Text;
using nadena.dev.ndmf;
using UnityEngine;
using UnityEngine.UIElements;

namespace Fosa.AvatarTextureOptimizer
{
    /// <summary>ATO 报告条目（IError 实现）。/ ATO report entry (IError implementation).</summary>
    internal sealed class ATOReportError : IError
    {
        public ErrorSeverity Severity { get; }
        private readonly string _title;
        private readonly string _details;

        public ATOReportError(ErrorSeverity severity, string title, string details)
        {
            Severity = severity;
            _title = title;
            _details = details;
        }

        public VisualElement CreateVisualElement(ErrorReport report)
        {
            var container = new VisualElement();
            var titleLabel = new Label(_title)
            {
                style = { whiteSpace = WhiteSpace.Normal, fontSize = 13 },
            };
            container.Add(titleLabel);
            if (!string.IsNullOrEmpty(_details))
            {
                var foldout = new Foldout { text = "Details / 详情", value = false };
                var detailLabel = new Label(_details)
                {
                    style = { whiteSpace = WhiteSpace.Normal, fontSize = 11 },
                };
                foldout.Add(detailLabel);
                container.Add(foldout);
            }
            return container;
        }

        public string ToMessage() => _title;

        public void AddReference(ObjectReference reference) { }
    }

    /// <summary>构建报告。/ Build report.</summary>
    internal sealed class ATOReport
    {
        private readonly List<(string title, string details)> _entries = new List<(string, string)>();

        /// <summary>构建总体报告并输出到 NDMF 控制台。/ Build the summary report and surface it to the NDMF console.</summary>
        public void Build(BuildContext context, ATOAvatarScanResult scan, List<ATOTypeGroup> groups,
            ATOTextureWriter writer, ATOQualityEvaluator evaluator, ATOBuildSession session)
        {
            var sb = new StringBuilder();
            var details = new StringBuilder();

            // ---- 总体 / summary ----
            int atlasCount = 0;
            long atlasPixels = 0;
            long originalBytes = 0;
            long outputBytes = 0;
            foreach (var group in groups)
            {
                foreach (var bin in group.bins)
                {
                    atlasCount++;
                    atlasPixels += (long)bin.width * bin.height;
                    foreach (var kv in bin.atlases)
                    {
                        if (kv.Value == null) continue;
                        outputBytes += (long)kv.Value.width * kv.Value.height;
                    }
                }
            }
            if (writer != null)
            {
                foreach (var o in writer.Outputs)
                {
                    if (o.output == null) continue;
                    if (o.originalBytes > 0) originalBytes += o.originalBytes;
                    outputBytes += o.outputBytes;
                }
            }

            long totalMs = 0;
            foreach (var (name, ms, detail) in ATOLog.Stages)
            {
                totalMs += (long)ms;
                details.AppendLine($"  {name}: {ms:F1} ms {(string.IsNullOrEmpty(detail) ? "" : "(" + detail + ")")}");
            }

            var processedTextures = scan != null ? scan.textures.Count : 0;
            var islands = session != null ? session.IslandCount : 0;
            var evals = evaluator != null ? evaluator.TotalEvaluations : 0;

            sb.AppendLine("Avatar Texture Optimizer — build report (ATO 构建报告)");
            sb.AppendLine($"Processed textures (处理贴图): {processedTextures}");
            sb.AppendLine($"UV islands (UV 岛): {islands}");
            sb.AppendLine($"Generated atlases (生成图集): {atlasCount}");
            sb.AppendLine($"Quality evaluations (质量评估次数): {evals}");
            sb.AppendLine($"Total time (总耗时): {totalMs / 1000.0:F2} s");
            if (originalBytes > 0)
            {
                var saved = originalBytes - outputBytes;
                var pct = (double)saved / originalBytes;
                sb.AppendLine($"Texture size (贴图体积): {ATOLog.FormatBytes(originalBytes)} → {ATOLog.FormatBytes(outputBytes)} ({(pct >= 0 ? "-" : "+")}{Math.Abs(pct) * 100:F1}%)");
            }

            // ---- 细节 / details ----
            details.AppendLine("== Atlases / 图集 ==");
            int idx = 0;
            foreach (var group in groups)
            {
                foreach (var bin in group.bins)
                {
                    var sources = new List<string>();
                    foreach (var item in bin.items)
                        sources.Add(item.texture.name);
                    long occBits = bin.occupancy != null ? bin.occupancy.CountBits() : 0;
                    long totalCells = bin.occupancy != null ? (long)bin.occupancy.cellsW * bin.occupancy.cellsH : 1;
                    var util = (double)occBits / totalCells;
                    var islandCount = 0;
                    foreach (var item in bin.items) islandCount += item.refs.Count;
                    details.AppendLine($"  [{idx}] {bin.width}x{bin.height} islands={islandCount} utilization={ATOLog.FormatPct(util)} sRGB={bin.isSRGB} alpha={bin.hasAlpha}");
                    details.AppendLine($"      sources: {string.Join(", ", sources)}");
                    foreach (var kv in bin.atlases)
                    {
                        if (kv.Value == null) continue;
                        details.AppendLine($"      role={kv.Key}: {kv.Value.name} ({kv.Value.width}x{kv.Value.height})");
                    }
                    idx++;
                }
            }
            if (writer != null)
            {
                details.AppendLine("== Whole-texture outputs / 整图路径输出 ==");
                foreach (var o in writer.Outputs)
                {
                    if (o.output == null || o.source == null) continue;
                    var saved = o.originalBytes - o.outputBytes;
                    details.AppendLine($"  {o.source.name} → {o.name} ({o.width}x{o.height}, {o.format}, {(saved >= 0 ? "-" : "+")}{ATOLog.FormatBytes(Math.Abs(saved))})");
                }
            }

            ErrorReport.ReportError(new ATOReportError(ErrorSeverity.Information, sb.ToString(), details.ToString()));
        }

        /// <summary>取消报告。/ Cancellation report.</summary>
        public void ReportCancelled(BuildContext context)
        {
            ErrorReport.ReportError(new ATOReportError(ErrorSeverity.Error,
                "ATO build cancelled by user. (ATO 构建被用户取消)", "Temp assets were kept on disk; CPU/GPU/memory resources were released. (磁盘临时资产保留，资源已释放)"));
        }

        /// <summary>失败报告。/ Failure report.</summary>
        public void ReportFailed(BuildContext context, Exception e)
        {
            ErrorReport.ReportError(new ATOReportError(ErrorSeverity.InternalError,
                "ATO build failed: " + e.Message, e.ToString()));
        }
    }
}
