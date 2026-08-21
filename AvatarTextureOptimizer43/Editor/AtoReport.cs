using System;
using System.Collections.Generic;
using System.Text;
using nadena.dev.ndmf;
using UnityEngine;
using Fosa.ATO;

namespace Fosa.ATO.Editor
{
    /// <summary>
    /// Bake report. Summary is shown on the NDMF console; details are folded via description.
    /// 烘焙报告。摘要显示在 NDMF 控制台，细节放在 description 中折叠展示。
    /// </summary>
    public sealed class AtoReport
    {
        public long TotalMs;
        public int Renderers;
        public int MaterialsSeen;
        public int TexturesSeen;
        public int TexturesDeduped;
        public int Islands;
        public int Atlases;
        public int UvGroups;
        public int TypeGroups;
        public int Whitelisted;
        public int SkippedIneligible;
        public long BytesBefore;
        public long BytesAfter;
        public readonly List<string> AtlasLines = new List<string>();
        public readonly List<string> Warnings = new List<string>();
        public readonly List<string> Details = new List<string>();

        public void AddAtlas(string name, int w, int h, int islandCount, float util, string sources)
        {
            AtlasLines.Add($"{name} {w}x{h} islands={islandCount} util={util:P1} src=[{sources}]");
        }

        public string Summary()
        {
            var saved = BytesBefore > 0 ? 1.0 - (double)BytesAfter / BytesBefore : 0;
            return AtoLoc.T("ato.report.summary",
                TotalMs, Atlases, Islands, TexturesDeduped,
                AtoLog.Bytes(BytesBefore), AtoLog.Bytes(BytesAfter), saved * 100.0);
        }

        public string DetailText()
        {
            var sb = new StringBuilder();
            sb.AppendLine(Summary());
            sb.AppendLine($"renderers={Renderers} materials={MaterialsSeen} textures={TexturesSeen} uvGroups={UvGroups} typeGroups={TypeGroups}");
            sb.AppendLine($"whitelist={Whitelisted} ineligible={SkippedIneligible}");
            foreach (var a in AtlasLines) sb.AppendLine(a);
            foreach (var d in Details) sb.AppendLine(d);
            foreach (var w in Warnings) sb.AppendLine("WARN " + w);
            return sb.ToString();
        }

        public void EmitToNdmf()
        {
            ErrorReport.ReportError(AtoLoc.NdmfLocalizer, ErrorSeverity.Information, "ato.report.done",
                Summary(), DetailText());
            foreach (var w in Warnings)
                ErrorReport.ReportError(AtoLoc.NdmfLocalizer, ErrorSeverity.NonFatal, "ato.warn.generic", w);
        }
    }
}
