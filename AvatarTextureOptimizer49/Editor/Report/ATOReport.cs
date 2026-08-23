using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using nadena.dev.ndmf;
using UnityEngine;

namespace Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// Collects build statistics and prints the final report to the NDMF console: overall result
    /// by default, details (per-stage timings, atlas sources, utilization, savings) folded into
    /// the expandable details section. / 收集构建统计并向 NDMF 控制台输出报告：默认展示总体结果，
    /// 明细（分阶段耗时、图集来源、利用率、优化量）折叠在详情区。
    /// </summary>
    internal class ATOReport
    {
        internal int RendererCount;
        internal int UvGroupCount;
        internal int AtlasEligibleGroups;
        internal int IslandCount;
        internal int InstanceCount;
        internal int TextureCount;
        internal int WhitelistCount;
        internal int DedupCount;
        internal long OriginalPixels;
        internal long OptimizedPixels;
        internal float OriginalMegabytes;
        internal float OptimizedMegabytes;
        internal readonly List<string> Warnings = new List<string>();
        internal readonly List<string> AtlasLines = new List<string>();

        internal string Summary()
        {
            float pxRatio = OriginalPixels > 0 ? (float)OptimizedPixels / OriginalPixels : 1f;
            float mbRatio = OriginalMegabytes > 0.01f ? OptimizedMegabytes / OriginalMegabytes : 1f;
            return
                $"{RendererCount} renderers, {TextureCount} textures, {UvGroupCount} UV groups " +
                $"({AtlasEligibleGroups} atlased, {IslandCount} islands, {InstanceCount} instances), " +
                $"{AtlasLines.Count} atlases; " +
                $"pixels {OriginalPixels / 1_000_000.0:F1}M→{OptimizedPixels / 1_000_000.0:F1}M ({pxRatio:P0}), " +
                $"est. VRAM {OriginalMegabytes:F1}MB→{OptimizedMegabytes:F1}MB ({mbRatio:P0}), " +
                $"warnings {Warnings.Count}, dedup {DedupCount}";
        }

        internal string Details()
        {
            var sb = new StringBuilder();
            sb.AppendLine("== Stage timings / 阶段耗时 ==");
            foreach (var (stage, ms) in ATOLog.StageTimings)
                sb.AppendLine($"  {stage}: {ms:F0} ms");
            sb.AppendLine("== Atlases / 图集 ==");
            foreach (var line in AtlasLines) sb.AppendLine("  " + line);
            sb.AppendLine("== Warnings / 警告 ==");
            foreach (var w in Warnings) sb.AppendLine("  " + w);
            sb.AppendLine("== Memory note / 内存 ==");
            sb.AppendLine("  temporary GPU buffers and pixel caches are released after each stage / 每阶段后释放临时GPU缓冲与像素缓存");
            return sb.ToString();
        }

        /// <summary>Emit to the NDMF console (Information severity). / 输出到NDMF控制台。</summary>
        internal void Emit(Localizer localizer)
        {
            ErrorReport.ReportError(localizer, ErrorSeverity.Information, "ato:report",
                Summary(), Details());
        }
    }
}
