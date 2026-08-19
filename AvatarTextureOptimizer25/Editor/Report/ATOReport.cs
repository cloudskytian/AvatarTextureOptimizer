// Avatar Texture Optimizer / 头像贴图优化器
// Build report model + NDMF error-report integration.
// 构建报告模型与 NDMF 报错系统集成。
//
// The top-level summary is always reported as an Information entry; per-detail
// lines live in the details section (shown folded in the NDMF console). Errors
// abort the build (Error severity), problems fall to NonFatal, whitelist
// notices are Information.
// 总览始终以 Information 级别上报，细节行放入 details 区（在 NDMF 控制台中折叠）。
// Error 级中止构建，问题级为 NonFatal，白名单提示为 Information。

using System;
using System.Collections.Generic;
using System.Text;
using nadena.dev.ndmf;
using UnityEngine;

namespace FOSA.AvatarTextureOptimizer.Editor
{
    /// <summary>NDMF error entry for ATO messages. / ATO 消息的 NDMF 报错条目。</summary>
    public sealed class ATOSimpleError : SimpleError
    {
        private readonly string _key;
        private readonly string[] _titleSubst;
        private readonly string[] _detailsSubst;
        private readonly ErrorSeverity _severity;
        private readonly LocalizerHolder _holder;

        // NDMF requires Localizer from nadena assembly; ATO uses its own JSON i18n
        // adapted through ATOLoc.AsNdmfLocalizer().
        // NDMF 需要 nadena 程序集的 Localizer；ATO 用自己的 JSON i18n 经适配器提供。
        private sealed class LocalizerHolder
        {
            public nadena.dev.ndmf.localization.Localizer Value;
        }

        public ATOSimpleError(string key, ErrorSeverity severity, string[] titleSubst = null, string[] detailsSubst = null,
            List<ObjectReference> refs = null)
        {
            _key = key;
            _severity = severity;
            _titleSubst = titleSubst ?? Array.Empty<string>();
            _detailsSubst = detailsSubst ?? Array.Empty<string>();
            _holder = new LocalizerHolder();
            if (refs != null) _references.AddRange(refs);
        }

        public override nadena.dev.ndmf.localization.Localizer Localizer =>
            _holder.Value ?? (_holder.Value = ATOLoc.AsNdmfLocalizer());

        public override string TitleKey => "ato:" + _key;
        public override string[] TitleSubst => _titleSubst;
        public override string[] DetailsSubst => _detailsSubst;
        public override ErrorSeverity Severity => _severity;
    }

    /// <summary>
    /// Aggregates everything worth reporting about a build.
    /// 汇总一次构建中值得报告的全部信息。
    /// </summary>
    public sealed class ATOBuildReport
    {
        public sealed class AtlasInfo
        {
            public string name;
            public int width, height;
            public int islandCount;
            public int textureCount;
            public float utilization; // 0..1
            public string typeGroupKey;
            public long sourceBytes;
            public long resultBytes;
            public string reason;        // optional note (e.g. fallback cause) / 可选备注（如回退原因）
        }

        public sealed class TextureInfo
        {
            public string name;
            public int fromWidth, fromHeight;
            public int toWidth, toHeight;
            public long sourceBytes;
            public long resultBytes;
            public string reason;
        }

        public readonly List<string> warnings = new List<string>();
        public readonly List<string> whitelistNotes = new List<string>();
        public readonly List<AtlasInfo> atlases = new List<AtlasInfo>();
        public readonly List<TextureInfo> standaloneTextures = new List<TextureInfo>();
        /// <summary>Per-UV-group final disposition lines (traceability). / 逐 UV 组的最终处置行（可追溯性）。</summary>
        public readonly List<string> groupDispositions = new List<string>();
        public readonly List<(string stage, long ms)> stageTimings = new List<(string, long)>();

        public int renderersScanned;
        public int materialsScanned;
        public int texturesScanned;
        public int texturesDeduplicatedInto;
        public int materialsDeduplicatedInto;
        public int islandsTotal;
        public int islandsAtlased;
        public int uvGroupsTotal;
        public int uvGroupsSkippedAtlas;
        public long originalTextureBytes;
        public long optimizedTextureBytes;

        /// <summary>Overall saving ratio 0..1 (of original bytes). / 总体优化量（占原始字节比例）。</summary>
        public float SavingsRatio => originalTextureBytes > 0
            ? Mathf.Clamp01(1f - (float)optimizedTextureBytes / originalTextureBytes)
            : 0f;

        /// <summary>Render the details text (folded in the NDMF console). / 渲染细节文本（NDMF 控制台中折叠显示）。</summary>
        public string BuildDetailsText()
        {
            var sb = new StringBuilder(4096);
            sb.AppendLine(ATOLoc.T("ato:report.line.counts",
                renderersScanned, materialsScanned, texturesScanned, islandsTotal, uvGroupsTotal));
            sb.AppendLine(ATOLoc.T("ato:report.line.dedup", texturesDeduplicatedInto, materialsDeduplicatedInto));
            sb.AppendLine(ATOLoc.T("ato:report.line.islands", islandsAtlased, islandsTotal, uvGroupsSkippedAtlas));
            foreach (var t in stageTimings)
                sb.AppendLine(ATOLoc.T("ato:report.line.stage", t.stage, t.ms));
            foreach (var a in atlases)
            {
                sb.AppendLine(ATOLoc.T("ato:report.line.atlas",
                    a.name, a.width, a.height, a.typeGroupKey, a.islandCount, a.textureCount,
                    (a.utilization * 100f).ToString("F1"),
                    FormatSize(a.sourceBytes), FormatSize(a.resultBytes)));
                if (!string.IsNullOrEmpty(a.reason)) sb.AppendLine("    " + a.reason);
            }
            foreach (var t in standaloneTextures)
            {
                sb.AppendLine(ATOLoc.T("ato:report.line.standalone",
                    t.name, t.fromWidth, t.fromHeight, t.toWidth, t.toHeight,
                    FormatSize(t.sourceBytes), FormatSize(t.resultBytes), t.reason ?? ""));
            }
            foreach (var d in groupDispositions) sb.AppendLine("[group] " + d);
            foreach (var w in whitelistNotes) sb.AppendLine("[whitelist] " + w);
            foreach (var w in warnings) sb.AppendLine("[warning] " + w);
            return sb.ToString();
        }

        /// <summary>Render the compact summary title. / 渲染紧凑的总览标题。</summary>
        public string BuildSummaryText()
        {
            return ATOLoc.T("ato:report.summary",
                atlases.Count, texturesScanned,
                FormatSize(originalTextureBytes), FormatSize(optimizedTextureBytes),
                (SavingsRatio * 100f).ToString("F1"));
        }

        /// <summary>Submit the report (summary + details + warnings) to the NDMF console. / 将报告提交到 NDMF 控制台。</summary>
        public void Submit()
        {
            var summary = BuildSummaryText();
            var details = BuildDetailsText();
            ErrorReport.ReportError(new ATOSimpleError(
                "report.title",
                ErrorSeverity.Information,
                titleSubst: new[] { summary },
                detailsSubst: new[] { details }));
            foreach (var w in warnings)
            {
                ErrorReport.ReportError(new ATOSimpleError("report.warning", ErrorSeverity.NonFatal,
                    titleSubst: new[] { w }));
            }
        }

        public static string FormatSize(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("F1") + " KiB";
            if (bytes < 1024L * 1024 * 1024) return (bytes / (1024.0 * 1024)).ToString("F2") + " MiB";
            return (bytes / (1024.0 * 1024 * 1024)).ToString("F2") + " GiB";
        }
    }
}
