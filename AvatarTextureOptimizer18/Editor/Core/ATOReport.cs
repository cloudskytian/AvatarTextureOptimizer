using System;
using System.Collections.Generic;
using System.Text;
using nadena.dev.ndmf;
using nadena.dev.ndmf.localization;
using UnityEngine;

namespace Fosa.AvatarTextureOptimizer.Editor
{
    // 烘焙报告：记录各阶段耗时与统计信息，构建完成后输出到 NDMF 控制台。
    // Build report: records per-stage timing and statistics; printed to the NDMF console after the build.
    internal sealed class ATOReport
    {
        // 单个阶段。A single stage.
        public sealed class Stage
        {
            public string name;
            public double ms;
            public readonly List<string> lines = new List<string>();

            public void AddLine(string line)
            {
                lines.Add(line);
            }
        }

        public readonly List<Stage> stages = new List<Stage>();
        public string avatarName = "";
        public double totalMs;

        // 汇总统计。Summary statistics.
        public int textureCount;
        public int whitelistedTextureCount;
        public int slotCount;
        public int materialCount;
        public long dedupBytesSaved;
        public int dedupMergedTextures;

        public Stage BeginStage(string name)
        {
            var s = new Stage { name = name };
            stages.Add(s);
            return s;
        }

        public void EndStage(Stage s, double ms)
        {
            s.ms = ms;
        }

        // 输出总体结果（默认）与详细内容（Verbose 时折叠性输出）。Prints the summary (always) and details (when verbose).
        public void PrintToConsole()
        {
            var sb = new StringBuilder();
            sb.AppendLine("========== Avatar Texture Optimizer 报告 / Report ==========");
            sb.AppendLine(string.Format("Avatar: {0}", avatarName));
            sb.AppendLine(string.Format("总耗时 / Total time: {0:F1} ms", totalMs));
            sb.AppendLine(string.Format("贴图 / Textures: {0}（白名单 / whitelisted: {1}）", textureCount, whitelistedTextureCount));
            sb.AppendLine(string.Format("材质槽 / Material slots: {0}，材质 / Materials: {1}", slotCount, materialCount));
            sb.AppendLine(string.Format("贴图去重 / Texture dedup: 合并 {0} 张，估算节省 / est. saved {1:F2} MB", dedupMergedTextures, dedupBytesSaved / 1048576.0));
            foreach (var s in stages)
            {
                sb.AppendLine(string.Format("  阶段 / Stage '{0}': {1:F1} ms", s.name, s.ms));
            }
            sb.Append("======================================================");
            ATOLog.Info(sb.ToString());

            // 详细内容：仅详细模式输出（默认开启；对应“具体细节折叠起来”的开发期行为）。
            // Details are printed in verbose mode only (default on; development-time behaviour matching "details folded").
            if (!ATOLog.Verbose) return;
            foreach (var s in stages)
            {
                if (s.lines.Count == 0) continue;
                ATOLog.Debug(string.Format("---- 阶段细节 / Stage details: {0} ----", s.name));
                foreach (var line in s.lines)
                {
                    ATOLog.Debug("  " + line);
                }
            }
        }

        // 通过 NDMF ErrorReport 上报一条错误/警告（显示在 NDMF 控制台）。
        // subst 为标题占位符 {0} {1} ... 的替换值；具体细节同时输出到 Unity Console。
        // Reports an error/warning through the NDMF ErrorReport (shown in the NDMF console).
        // subst fills the title placeholders {0} {1} ...; specifics also go to the Unity Console.
        public static void Report(ErrorReport report, ErrorSeverity severity, string titleKey, params string[] subst)
        {
            if (report == null) return;
            ErrorReport.ReportError(new ATOSimpleError(severity, titleKey, subst));
        }

        // 简单错误实现：通过 ATOLocalization 提供标题/详情的多语言。
        // Simple error implementation using ATOLocalization for multilingual titles/details.
        private sealed class ATOSimpleError : SimpleError
        {
            private static readonly Lazy<Localizer> LazyLocalizer = new Lazy<Localizer>(() =>
                new Localizer("en-us", () =>
                {
                    var list = new List<(string, Func<string, string>)>();
                    foreach (var lang in ATOLocalization.AvailableLanguages)
                    {
                        list.Add((lang, key => ATOLocalization.Raw(lang, key)));
                    }
                    return list;
                }));

            public override Localizer Localizer => LazyLocalizer.Value;
            public override string TitleKey { get; }
            public override ErrorSeverity Severity { get; }
            private readonly string[] _subst;
            public override string[] TitleSubst => _subst;

            public ATOSimpleError(ErrorSeverity severity, string titleKey, string[] subst)
            {
                Severity = severity;
                TitleKey = titleKey;
                _subst = subst == null ? Array.Empty<string>() : subst;
            }
        }
    }
}
