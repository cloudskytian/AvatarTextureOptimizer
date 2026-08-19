// English: Build report pushed to the NDMF console (summary) plus detailed [ATO] logs.
// 中文：写入 NDMF 控制台的总览报告，细节走 [ATO] 日志。
using System.Collections.Generic;
using System.Text;
using nadena.dev.ndmf;
using UnityEngine;

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    internal sealed class ATOReport
    {
        public int RenderersScanned;
        public int MaterialsScanned;
        public int TexturesSeen;
        public int TexturesDeduped;
        public int TexturesWhitelisted;
        public int IslandsExtracted;
        public int IslandsScaled;
        public int AtlasesBuilt;
        public long SourcePixels;
        public long ResultPixels;
        public readonly List<string> AtlasLines = new List<string>();
        public readonly List<string> Warnings = new List<string>();
        public bool Canceled;
        public double TotalMs;

        public string Headline()
        {
            if (Canceled) return ATOLoc.T("progress.cancelled");
            var saved = SourcePixels <= 0
                ? 0
                : (1.0 - (double)ResultPixels / SourcePixels) * 100.0;
            return string.Format(
                "atlases={0} islands={1} textures={2}->{3} pixels {4:N0}->{5:N0} ({6:F1}% fewer) {7:F0}ms",
                AtlasesBuilt,
                IslandsExtracted,
                TexturesSeen,
                TexturesSeen - TexturesDeduped,
                SourcePixels,
                ResultPixels,
                saved,
                TotalMs);
        }

        public string Details()
        {
            var sb = new StringBuilder();
            sb.AppendLine("renderers=" + RenderersScanned + " materials=" + MaterialsScanned);
            sb.AppendLine("whitelisted textures=" + TexturesWhitelisted + " dedup merges=" + TexturesDeduped);
            sb.AppendLine("islands scaled=" + IslandsScaled);
            foreach (var line in AtlasLines) sb.AppendLine(line);
            if (Warnings.Count > 0)
            {
                sb.AppendLine("warnings:");
                foreach (var w in Warnings) sb.AppendLine("  - " + w);
            }

            return sb.ToString();
        }

        public void PushToNdmf()
        {
            ErrorReport.ReportError(
                ATOLoc.L,
                ErrorSeverity.Information,
                "info.report",
                Headline(),
                Details());
        }

        public void AddAtlas(string name, int w, int h, float utilization, string sources, int islandCount)
        {
            var line = string.Format(
                "atlas {0} {1}x{2} util={3:P1} islands={4} sources=[{5}]",
                name, w, h, utilization, islandCount, sources);
            AtlasLines.Add(line);
        }
    }
}
