// SPDX-License-Identifier: MIT
// AvatarTextureOptimizer (ATO) - Build report shown in the NDMF console.
// AvatarTextureOptimizer (ATO) - 在 NDMF 控制台展示的构建报告。

using System.Collections.Generic;
using System.Text;
using nadena.dev.ndmf;
using nadena.dev.ndmf.localization;
using Net.Fosa.AvatarTextureOptimizer.Editor.Atlas;
using Net.Fosa.AvatarTextureOptimizer.Editor.Core;
using Net.Fosa.AvatarTextureOptimizer.Editor.Localization;
using UnityEngine.UIElements;

namespace Net.Fosa.AvatarTextureOptimizer.Editor.Plugin
{
    /// <summary>
    /// EN: Aggregates the numbers the user cares about and renders them as a single NDMF console entry:
    ///     a one-line summary that is always visible plus a foldout with the full detail.
    /// ZH: 汇总用户关心的数字，并渲染为 NDMF 控制台中的一条记录：
    ///     始终可见的一行总览，加上一个折叠起来的完整细节区。
    /// </summary>
    public sealed class ATOBuildReport
    {
        public ATOPlatform Platform;
        public ATOPlatformSettings Options;

        public int TextureCount;
        public int ExcludedCount;
        public int IslandCount;
        public int AtlasCount;

        public long OriginalBytes;
        public long OptimisedBytes;

        public readonly List<string> AtlasLines = new List<string>();
        public readonly List<string> Notes = new List<string>();

        public float SavedRatio => OriginalBytes <= 0 ? 0f : 1f - (float)OptimisedBytes / OriginalBytes;

        public string Summary()
        {
            return ATOL.Tr("ATO:report:summary",
                AtlasCount, TextureCount, ExcludedCount,
                (OriginalBytes / 1024f / 1024f).ToString("F1"),
                (OptimisedBytes / 1024f / 1024f).ToString("F1"),
                (SavedRatio * 100f).ToString("F1"),
                (ATOLog.TotalMilliseconds / 1000.0).ToString("F2"));
        }

        public string Details()
        {
            var sb = new StringBuilder();
            sb.Append(ATOL.Tr("ATO:report:platform")).Append(": ").Append(Platform).Append('\n');
            sb.Append(ATOL.Tr("ATO:report:tier")).Append(": ").Append(Options?.qualityTier).Append('\n');
            sb.Append(ATOL.Tr("ATO:report:islands")).Append(": ").Append(IslandCount).Append('\n');
            sb.Append('\n').Append(ATOL.Tr("ATO:report:atlases")).Append(":\n");
            foreach (var line in AtlasLines) sb.Append("  ").Append(line).Append('\n');

            if (Notes.Count > 0)
            {
                sb.Append('\n').Append(ATOL.Tr("ATO:report:notes")).Append(":\n");
                foreach (var n in Notes) sb.Append("  ").Append(n).Append('\n');
            }

            sb.Append('\n').Append(ATOL.Tr("ATO:report:timings")).Append(":\n").Append(ATOLog.FormatTimings());
            return sb.ToString();
        }

        /// <summary>EN: Push the report into the NDMF console. ZH: 把报告推送到 NDMF 控制台。</summary>
        public void Emit()
        {
            ATOLog.Info(Summary());
            ATOLog.Debug_("\n" + Details());
            ErrorReport.ReportError(new ATOReportError(this));
        }
    }

    internal sealed class ATOReportError : SimpleError
    {
        private readonly ATOBuildReport _report;

        public ATOReportError(ATOBuildReport report) => _report = report;

        public override Localizer Localizer => ATOL.Localizer;
        public override ErrorSeverity Severity => ErrorSeverity.Information;
        public override string TitleKey => "ATO:report:title";

        public override string ToMessage() => _report.Summary();

        public override VisualElement CreateVisualElement(ErrorReport report)
        {
            var root = new VisualElement();
            root.style.flexDirection = FlexDirection.Column;

            var title = new Label(ATOL.Tr("ATO:report:title"));
            title.style.unityFontStyleAndWeight = UnityEngine.FontStyle.Bold;
            root.Add(title);

            var summary = new Label(_report.Summary()) { style = { whiteSpace = WhiteSpace.Normal } };
            root.Add(summary);

            // EN: Details are collapsed by default, as requested.
            // ZH: 按要求，细节默认折叠。
            var foldout = new Foldout { text = ATOL.Tr("ATO:report:details"), value = false };
            var details = new Label(_report.Details())
            {
                style = { whiteSpace = WhiteSpace.Normal }
            };
            foldout.Add(details);
            root.Add(foldout);

            return root;
        }
    }
}
