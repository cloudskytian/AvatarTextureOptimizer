using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using nadena.dev.ndmf;
using UnityEngine;

namespace net.fosa.ato.editor
{
    /// <summary>EN: Accumulates the numbers shown in the final NDMF console report. ZH: 累积最终 NDMF 控制台报告所需的数字。</summary>
    public sealed class ATOReport
    {
        /// <summary>EN: Atlases produced. ZH: 产出的图集。</summary>
        public readonly List<BakedAtlas> Atlases = new List<BakedAtlas>();
        /// <summary>EN: Total islands packed. ZH: 装箱的岛总数。</summary>
        public int PackedIslands;
        /// <summary>EN: Source texture bytes before optimisation. ZH: 优化前的源贴图字节数。</summary>
        public long BytesBefore;
        /// <summary>EN: Output bytes after optimisation. ZH: 优化后的输出字节数。</summary>
        public long BytesAfter;
        /// <summary>EN: Textures skipped, with the reason. ZH: 被跳过的贴图及原因。</summary>
        public readonly List<string> Skipped = new List<string>();
        /// <summary>EN: Duplicate textures removed on the input side. ZH: 输入端被移除的重复贴图数。</summary>
        public int InputDuplicatesRemoved;
        /// <summary>EN: Duplicate textures removed on the output side. ZH: 输出端被移除的重复贴图数。</summary>
        public int OutputTextureDuplicatesRemoved;
        /// <summary>EN: Duplicate materials removed. ZH: 被移除的重复材质数。</summary>
        public int MaterialDuplicatesRemoved;

        /// <summary>EN: Human readable one-line summary. ZH: 人类可读的单行总览。</summary>
        public string Summary()
        {
            float saved = BytesBefore > 0 ? (1f - (float)BytesAfter / BytesBefore) * 100f : 0f;
            return ATOLocalizer.Tr("ato.report.summary",
                Atlases.Count, PackedIslands, Human(BytesBefore), Human(BytesAfter), saved.ToString("F1"));
        }

        /// <summary>EN: Full detail block, collapsed by default in the console. ZH: 完整明细块，控制台默认折叠。</summary>
        public string Details(ATOLog log)
        {
            var sb = new StringBuilder();

            sb.AppendLine("== " + ATOLocalizer.Tr("ato.report.atlases") + " ==");
            foreach (var a in Atlases)
            {
                sb.AppendLine($"  {a.Texture.name}  {a.Texture.width}x{a.Texture.height}  {a.Texture.format}  " +
                              $"mips={a.Texture.mipmapCount}  islands={a.IslandCount}  " +
                              $"utilisation={a.Utilisation * 100f:F1}%  size={Human(TextureOutput.EstimateBytes(a.Texture))}");
                foreach (var s in a.Sources)
                    sb.AppendLine($"      <- {s.Source.name} ({s.Width}x{s.Height}, {s.Class})");
            }

            if (Skipped.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("== " + ATOLocalizer.Tr("ato.report.skipped") + " ==");
                foreach (var s in Skipped) sb.AppendLine("  " + s);
            }

            sb.AppendLine();
            sb.AppendLine($"  input duplicates removed:  {InputDuplicatesRemoved}");
            sb.AppendLine($"  output texture duplicates: {OutputTextureDuplicatesRemoved}");
            sb.AppendLine($"  material duplicates:       {MaterialDuplicatesRemoved}");

            sb.AppendLine();
            sb.AppendLine("== " + ATOLocalizer.Tr("ato.report.timings") + " ==");
            sb.Append(log.FormatTimings());

            if (log.Warnings.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("== Warnings ==");
                foreach (var w in log.Warnings) sb.AppendLine("  " + w);
            }

            return sb.ToString();
        }

        /// <summary>EN: Push the report into the NDMF console. ZH: 把报告推送到 NDMF 控制台。</summary>
        public void Publish(ATOLog log)
        {
            var summary = Summary();
            var details = Details(log);

            // EN: The summary is always visible; details go into the expandable body of the same entry.
            // ZH: 总览始终可见；细节放进同一条目的可展开正文中。
            ErrorReport.ReportError(new ATOWarning(summary + "\n\n" + details));
            Debug.Log($"{ATOConstants.LogPrefix} {summary}\n{details}");
        }

        /// <summary>EN: Format a byte count. ZH: 格式化字节数。</summary>
        public static string Human(long bytes)
        {
            if (bytes >= 1024L * 1024 * 1024) return $"{bytes / 1024.0 / 1024 / 1024:F2} GB";
            if (bytes >= 1024L * 1024) return $"{bytes / 1024.0 / 1024:F1} MB";
            if (bytes >= 1024) return $"{bytes / 1024.0:F0} KB";
            return $"{bytes} B";
        }
    }
}
