using System.Text;
using nadena.dev.ndmf;
using UnityEngine;

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    public static class NdmfReportSink
    {
        public static void Publish(BuildContext ctx, BakeReport report)
        {
            var sb = new StringBuilder();
            sb.AppendLine("[ATO] ===== Bake report =====");
            sb.AppendLine(report.Summary);
            foreach (var w in report.Warnings)
                sb.AppendLine("[WARN] " + w);
            AtoLog.Info(report.Summary);
            foreach (var d in report.Details)
                AtoLog.VerboseInfo(d);
            Debug.Log(sb.ToString());
        }
    }
}
