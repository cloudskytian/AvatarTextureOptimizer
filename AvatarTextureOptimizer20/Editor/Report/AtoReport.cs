// Build report shown in the NDMF console. / 输出到 NDMF 控制台的构建报告。
using System.Linq;
using System.Text;
using nadena.dev.ndmf;
using nadena.dev.ndmf.localization;

namespace net.fosa.ato.editor
{
    /// <summary>Information-severity report: summary first, details folded in description.
    /// 信息级报告：总体结果在标题，细节折叠在描述里。</summary>
    public class AtoReportError : SimpleError
    {
        private readonly string[] _subst;
        public override Localizer Localizer => AtoL10n.Localizer;
        public override ErrorSeverity Severity => ErrorSeverity.Information;
        public override string TitleKey => "report.title";
        public override string[] TitleSubst => _subst;
        public override string[] DetailsSubst => _subst;

        public AtoReportError(params string[] subst) { _subst = subst; }
    }

    public static class AtoReport
    {
        public static void Emit(AtoContext ctx)
        {
            var s = ctx.Stats;
            double ratio = s.OriginalPixels > 0 ? 1.0 - (double)s.FinalPixels / s.OriginalPixels : 0;

            var detail = new StringBuilder();
            foreach (var (label, ms) in s.StageTimes)
                detail.AppendLine($"{label}: {ms} ms");
            foreach (var a in s.Atlases)
                detail.AppendLine($"{a.Name}: {a.Width}x{a.Height} {a.Role} " +
                    $"util={a.Utilization:P1} sources=[{string.Join(", ", a.Sources.Select(t => t.Tex ? t.Tex.name : "?"))}]");

            ErrorReport.ReportError(new AtoReportError(
                s.Atlases.Count.ToString(),
                s.TexturesAtlased.ToString(),
                s.TexturesScaled.ToString(),
                s.TexturesWhitelisted.ToString(),
                s.TexturesDeduped.ToString(),
                s.IslandCount.ToString(),
                (ratio * 100).ToString("F1"),
                detail.ToString()));

            AtoLog.Info($"===== ATO report ===== atlases={s.Atlases.Count} atlased={s.TexturesAtlased} " +
                        $"scaled={s.TexturesScaled} whitelisted={s.TexturesWhitelisted} deduped={s.TexturesDeduped} " +
                        $"islands={s.IslandCount} pixel-reduction={ratio:P1}");
            AtoLog.Info(detail.ToString());
        }
    }
}
