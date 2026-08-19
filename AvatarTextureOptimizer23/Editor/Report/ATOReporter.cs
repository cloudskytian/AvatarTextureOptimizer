using nadena.dev.ndmf;
using UnityEngine;
using UnityEngine.UIElements;

namespace FOSA.AvatarTextureOptimizer.Editor
{
    internal static class ATOReporter
    {
        public static void Publish(ATOContext ctx)
        {
            ctx.Report.TextureIn = CountUniqueSources(ctx);
            ctx.Report.TextureOut = ctx.TextureRemap.Count;
            ctx.Report.Warnings.AddRange(ctx.Warnings);

            var saved = ctx.Report.BytesIn > 0
                ? 1.0 - ctx.Report.BytesOut / (double)System.Math.Max(1, ctx.Report.BytesIn)
                : 0;
            ctx.Log.Info(
                $"Summary renderers={ctx.Report.RendererCount} islands={ctx.Report.IslandCount} " +
                $"atlases={ctx.Report.AtlasCount} skipAtlas={ctx.Report.SkippedAtlas} " +
                $"whitelist={ctx.Report.WhitelistCount} saved≈{saved:P1} time={ctx.Report.TotalMs:F0}ms");

            ErrorReport.ReportError(new ATOReportError(ctx));
        }

        private static int CountUniqueSources(ATOContext ctx)
        {
            var set = new System.Collections.Generic.HashSet<int>();
            foreach (var use in ctx.Uses)
                if (use.Slot.texture != null) set.Add(use.Slot.texture.GetInstanceID());
            return set.Count;
        }
    }

    /// <summary>
    /// Foldable NDMF console card: summary visible, details collapsed.
    /// 可折叠的 NDMF 控制台卡片：默认只看总览，细节收起。
    /// </summary>
    internal sealed class ATOReportError : IError
    {
        private readonly ATOContext _ctx;
        public ErrorSeverity Severity => ErrorSeverity.Information;

        public ATOReportError(ATOContext ctx) { _ctx = ctx; }

        public void AddReference(ObjectReference obj) { }

        public string ToMessage()
        {
            return _ctx.Log.SummaryText + "\n" + _ctx.Log.DetailText;
        }

        public VisualElement CreateVisualElement(ErrorReport report)
        {
            var root = new VisualElement();
            root.style.paddingLeft = 8;
            root.style.paddingRight = 8;
            root.style.paddingTop = 6;
            root.style.paddingBottom = 6;

            var title = new Label(ATOLoc.T("ato.report.title"));
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            title.style.fontSize = 13;
            root.Add(title);

            var r = _ctx.Report;
            var saved = r.BytesIn > 0 ? 1.0 - r.BytesOut / (double)System.Math.Max(1, r.BytesIn) : 0;
            root.Add(new Label(ATOLoc.T("ato.report.summary",
                r.RendererCount, r.IslandCount, r.AtlasCount, r.SkippedAtlas, r.WhitelistCount, saved, r.TotalMs)));

            var fold = new Foldout { text = ATOLoc.T("ato.report.details"), value = false };
            var detail = new Label(_ctx.Log.DetailText);
            detail.style.whiteSpace = WhiteSpace.Normal;
            detail.style.unityFont = new StyleFont(Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"));
            fold.Add(detail);

            if (r.AtlasLines.Count > 0)
            {
                var atlases = new Foldout { text = ATOLoc.T("ato.report.atlases"), value = false };
                foreach (var line in r.AtlasLines)
                    atlases.Add(new Label(line));
                fold.Add(atlases);
            }
            root.Add(fold);
            return root;
        }
    }
}
