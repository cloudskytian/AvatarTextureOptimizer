// Stage8_Report — NDMF console report + console summary + self removal / 报告 + 移除自身
// Default shows overall results; details are emitted as verbose [ATO] logs (collapsible in console).<br>
// 默认展示总体结果；细节走 verbose 日志（控制台可折叠）。烘焙完成后移除组件自身（需求）。
using System.Linq;
using System.Text;
using nadena.dev.ndmf;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Fosa.ATO.Editor
{
    internal static class Stage8_Report
    {
        internal static void Run(BuildContext ctx, ATOPipeContext pipe, AvatarTextureOptimizer comp, long elapsedMs)
        {
            long origBytes = pipe.textures.Sum(t => t.ApproxBytes);
            long atlasBytes = pipe.atlases.SelectMany(a => a.planes.Values).Sum(p => (long)(p.texture != null ? p.texture.width * p.texture.height * 4L : 0));
            long savedEst = origBytes - atlasBytes;
            int islandCount = pipe.islands.Count;

            // summary (always) / 总览（始终输出）
            var sb = new StringBuilder();
            sb.AppendLine($"[ATO] ===== {comp.name} =====");
            sb.AppendLine(ATOL10n.T("ato.report.line1", pipe.textures.Count, islandCount, pipe.atlases.Count));
            sb.AppendLine(ATOL10n.T("ato.report.line2", Fmt(origBytes), Fmt(atlasBytes), Fmt(savedEst), elapsedMs));
            sb.AppendLine(ATOL10n.T("ato.report.line3", pipe.materialReplacements.Count, pipe.meshReplacements.Count, pipe.warnings.Count));
            Debug.Log(sb.ToString());

            // details (verbose) / 细节
            if (ATOLog.Verbose)
            {
                var d = new StringBuilder();
                foreach (var (stage, ms) in ATOLog.StageTimes) d.AppendLine($"  · {stage}: {ms} ms");
                for (int i = 0; i < pipe.atlases.Count; i++)
                {
                    var a = pipe.atlases[i];
                    var sources = a.entries.Select(e => e.tex.source.name).Distinct().Take(6).ToList();
                    d.AppendLine($"  · atlas#{i} {a.width}x{a.height} key={a.key} islands={a.islands.Count} util={a.Utilization:P1} sources=[{string.Join(",", sources)}]");
                }
                foreach (var w in pipe.warnings) d.AppendLine("  ! " + w);
                Debug.Log("[ATO] details:\n" + d);
            }

            // NDMF console: overall info + per-warning entries / NDMF 控制台
            ErrorReport.ReportError(ATOL10n.L, ErrorSeverity.Information, "ato.report.summary",
                pipe.textures.Count, islandCount, pipe.atlases.Count, Fmt(savedEst));

            foreach (var w in pipe.warnings.Take(20))
                ErrorReport.ReportError(ATOL10n.L, ErrorSeverity.NonFatal, "ato.report.warning_item", w);

            // remove self from the baked avatar (spec) / 移除自身（需求）
            if (comp != null)
            {
                ATOLog.V("removing AvatarTextureOptimizer component from baked avatar");
                Object.DestroyImmediate(comp);
            }
        }

        private static string Fmt(long bytes)
        {
            if (bytes >= 1L << 30) return $"{bytes / (float)(1L << 30):F2} GB";
            if (bytes >= 1L << 20) return $"{bytes / (float)(1L << 20):F2} MB";
            if (bytes >= 1L << 10) return $"{bytes / (float)(1L << 10):F1} KB";
            return $"{bytes} B";
        }
    }
}
