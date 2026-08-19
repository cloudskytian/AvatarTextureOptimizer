// ============================================================================
// AvatarTextureOptimizer (net.fosa.avatar-texture-optimizer)
// Report.cs — 构建报告 / Build report
//
// 需求: 烘焙完成后在 ndmf 控制台上显示报告；默认展示总体结果，具体细节折叠起来；
//       日志包含耗时、图集的贴图来源、岛数量、图集大小、利用率、优化量。
// 实现: [ATO] 前缀分块输出；细节以折叠占位说明（控制台无折叠机制，按行前缀区分）。
// ============================================================================
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;

namespace net.fosa.avatar_texture_optimizer.editor
{
    /// <summary>
    /// 构建报告 / Build report.
    /// </summary>
    public sealed class ATOReport
    {
        public string avatarName;
        public long durationMs;
        public int slotCount, whitelistedSlotCount;
        public int inputTextures, dedupedTextures, optimizedTextures, whitelistedTextures;
        public int islandDetected, islandScaled, islandPacked;
        public int atlasCount;
        public long sourceBytes, targetBytes;
        public int meshRewrites;
        public string meshChannels = "";
        public int clipPatches;
        public int materialsDeduped;
        public int texturesDeduped;
        public bool cancelled;

        private readonly List<string> _atlasLines = new List<string>();

        public void AddAtlasLine(string line) => _atlasLines.Add(line);

        /// <summary>
        /// 输出报告到控制台 / Write the report to the console.
        /// </summary>
        public void Write()
        {
            var sb = new StringBuilder();
            sb.AppendLine("==================== " + I18n.T("report.title") + " ====================");
            sb.AppendLine("  " + I18n.T("report.avatar", avatarName));
            sb.AppendLine("  " + I18n.T("report.duration", durationMs));
            sb.AppendLine("  " + I18n.T("report.materials", slotCount, whitelistedSlotCount));
            sb.AppendLine("  " + I18n.T("report.textures", inputTextures, dedupedTextures, optimizedTextures, whitelistedTextures));
            sb.AppendLine("  " + I18n.T("report.islands", islandDetected, islandScaled, islandPacked));
            sb.AppendLine("  " + I18n.T("report.atlases", atlasCount));
            if (sourceBytes > 0)
            {
                sb.AppendLine("  " + I18n.T("report.savings",
                    Log.HumanSize(sourceBytes), Log.HumanSize(targetBytes),
                    (int)(100 - (targetBytes * 100.0 / sourceBytes))));
            }
            if (meshRewrites > 0)
            {
                sb.AppendLine("  " + I18n.T("report.meshRewrites", meshRewrites, meshChannels));
            }
            if (clipPatches > 0)
            {
                sb.AppendLine("  " + I18n.T("report.clipPatches", clipPatches));
            }
            if (materialsDeduped > 0) sb.AppendLine("  " + I18n.T("report.dedupMaterials", materialsDeduped));
            if (texturesDeduped > 0) sb.AppendLine("  " + I18n.T("report.dedupTextures", texturesDeduped));
            if (cancelled) sb.AppendLine("  " + I18n.T("report.cancelled"));

            // 细节（折叠概念：单独一行标题 + 缩进内容）/
            // details (folded concept: one header line + indented content)
            if (_atlasLines.Count > 0)
            {
                sb.AppendLine("  ▸ " + I18n.T("report.details") + " (" + _atlasLines.Count + "):");
                foreach (var line in _atlasLines)
                {
                    sb.AppendLine("      " + line);
                }
            }
            sb.AppendLine("===========================================================");

            Log.Info(sb.ToString());
        }
    }

    /// <summary>
    /// 图集行格式 / atlas report line format helper.
    /// </summary>
    public static class ReportFormat
    {
        /// <summary>图集行: ATO_{index} {role}: WxH {category}, 利用率 X%, 来源: n, 岛: n, 优化 X%</summary>
        public static string AtlasLine(AtlasResult atlas, int index)
        {
            long sourcePx = atlas.sourcePixels > 0 ? atlas.sourcePixels : 1;
            double opt = 100.0 - (atlas.targetPixels * 100.0 / sourcePx);
            return string.Format(
                "ATO_{1} {0}: {2}x{3} {4}, utilization {5:P1}, sources: {6}, islands: {7}, optimized {8:F1}%",
                atlas.family.role, index, atlas.width, atlas.height,
                atlas.family.category,
                atlas.utilization,
                atlas.sources.Count,
                atlas.islands.Count,
                opt);
        }
    }
}
