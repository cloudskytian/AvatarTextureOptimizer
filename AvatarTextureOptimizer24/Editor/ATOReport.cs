// ============================================================================
// ATOReport.cs — 构建报告数据模型 / Build report data model
// (EN) Collects results during the build and prints a summary to the console.
// (ZH) 构建期间收集结果，并在控制台打印摘要（默认总体，细节折叠为日志）。
// ============================================================================

using System.Collections.Generic;
using System.Text;

namespace Fosa.AvatarTextureOptimizer
{
    public class ATOReport
    {
        public int texturesProcessed;
        public int texturesSkipped;
        public int atlasesGenerated;
        public long approxBytesBefore;
        public long approxBytesAfter;
        public long GetSavedBytes() => approxBytesBefore - approxBytesAfter;

        public readonly List<AtlasEntry> atlases = new List<AtlasEntry>();
        public readonly List<string> warnings = new List<string>();
        public readonly List<StepTiming> steps = new List<StepTiming>();

        public class AtlasEntry
        {
            public string name;
            public int width, height;
            public int islandCount;
            public float utilization; // 0..1
            public List<string> sourceTextures = new List<string>();
            public long bytesBefore, bytesAfter;
        }

        public class StepTiming
        {
            public string name;
            public double ms;
        }

        public void AddStep(string name, double ms) => steps.Add(new StepTiming { name = name, ms = ms });

        public void AddWarning(string w) { if (!warnings.Contains(w)) warnings.Add(w); }

        /// <summary>(EN) Print the report summary to the console. (ZH) 在控制台打印报告摘要。</summary>
        public void PrintSummary(string language)
        {
            var sb = new StringBuilder();
            sb.AppendLine("========== " + ATOLocalization.T(language, "ato.report.summary") + " ==========");
            sb.AppendLine($"{ATOLocalization.T(language, "ato.report.texturesProcessed")}: {texturesProcessed} ({ATOLocalization.T(language, "ato.warn.whitelist")}: {texturesSkipped})");
            sb.AppendLine($"{ATOLocalization.T(language, "ato.report.atlasesGenerated")}: {atlasesGenerated}");
            sb.AppendLine($"{ATOLocalization.T(language, "ato.report.sizeBefore")}: {FormatBytes(approxBytesBefore)}");
            sb.AppendLine($"{ATOLocalization.T(language, "ato.report.sizeAfter")}: {FormatBytes(approxBytesAfter)}");
            sb.AppendLine($"{ATOLocalization.T(language, "ato.report.saved")}: {FormatBytes(GetSavedBytes())}");
            foreach (var s in steps)
                sb.AppendLine($"  - {s.name}: {s.ms:F1} ms");

            if (atlases.Count > 0)
            {
                sb.AppendLine("---- " + ATOLocalization.T(language, "ato.section.atlas") + " ----");
                foreach (var a in atlases)
                {
                    sb.AppendLine(
                        $"  {a.name} ({a.width}x{a.height}) {ATOLocalization.T(language, "ato.report.islands")}={a.islandCount} " +
                        $"{ATOLocalization.T(language, "ato.report.utilization")}={a.utilization:P1} " +
                        $"{FormatBytes(a.bytesBefore)} -> {FormatBytes(a.bytesAfter)}");
                    if (a.sourceTextures.Count > 0)
                        sb.AppendLine("      <- " + string.Join(", ", a.sourceTextures));
                }
            }

            if (warnings.Count > 0)
            {
                sb.AppendLine("---- Warnings ----");
                foreach (var w in warnings) sb.AppendLine("  " + w);
            }

            ATOLog.Info(sb.ToString());
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("F1") + " KB";
            return (bytes / (1024.0 * 1024.0)).ToString("F2") + " MB";
        }
    }
}
