// SPDX-License-Identifier: MIT
// EN: Builds the summary shown in the NDMF console after a build.
// ZH: 构建结束后在 NDMF 控制台中显示的摘要。

using System.Collections.Generic;
using System.Linq;
using System.Text;
using nadena.dev.ndmf;
using Net.Fosa.AvatarTextureOptimizer.Editor.Core;
using Net.Fosa.AvatarTextureOptimizer.Editor.Model;

namespace Net.Fosa.AvatarTextureOptimizer.Editor.Plugin
{
    /// <summary>
    /// EN: Accumulates numbers during the build and publishes one information entry with the headline
    ///     figures plus a collapsible detail block, as requested.
    /// ZH: 在构建过程中累积各项数字，并按要求发布一条包含总体数据与可折叠细节块的信息条目。
    /// </summary>
    public sealed class AtoBuildReport
    {
        /// <summary>EN: Total distinct textures found. ZH: 找到的不同贴图总数。</summary>
        public int TotalTextures;
        /// <summary>EN: Textures eligible for optimization. ZH: 符合优化条件的贴图数。</summary>
        public int OptimizableTextures;
        /// <summary>EN: Number of UV groups. ZH: UV 组数量。</summary>
        public int Groups;
        /// <summary>EN: Generated atlases. ZH: 生成的图集。</summary>
        public readonly List<AtlasResult> Atlases = new List<AtlasResult>();
        /// <summary>EN: Duplicate textures removed after optimization. ZH: 优化后被移除的重复贴图数。</summary>
        public int TexturesDeduplicated;
        /// <summary>EN: Duplicate materials removed after optimization. ZH: 优化后被移除的重复材质数。</summary>
        public int MaterialsDeduplicated;
        /// <summary>EN: Material slots merged away. ZH: 被合并掉的材质槽数。</summary>
        public int SlotsMerged;

        private long _originalTexels;
        private long _optimizedTexels;
        private int _islands;
        private readonly List<string> _skipped = new List<string>();

        /// <summary>EN: Collects the final numbers. ZH: 收集最终数字。</summary>
        public void Finish(AtoCollection collection, AtlasStageResult atlas)
        {
            foreach (var entry in collection.Textures.Values)
            {
                _originalTexels += (long)entry.Width * entry.Height;
                if (!entry.IsOptimizable)
                    _skipped.Add($"{entry.Texture.name}: {entry.SkipReason} ({entry.SkipDetail})");
            }
            foreach (var g in collection.Groups) _islands += g.Islands?.Count ?? 0;
            foreach (var a in atlas.Atlases) _optimizedTexels += (long)a.Size.x * a.Size.y;
        }

        /// <summary>EN: Writes the report to the NDMF console. ZH: 将报告写入 NDMF 控制台。</summary>
        public void Publish()
        {
            float saved = _originalTexels > 0
                ? 100f * (1f - _optimizedTexels / (float)_originalTexels)
                : 0f;

            var headline =
                $"{Atlases.Count} atlases, {_islands} islands, " +
                $"{OptimizableTextures}/{TotalTextures} textures optimized, " +
                $"{saved:F1}% fewer texels, " +
                $"-{TexturesDeduplicated} dup textures, -{MaterialsDeduplicated} dup materials, " +
                $"-{SlotsMerged} material slots, {AtoLog.ElapsedMs / 1000.0:F1} s";

            AtoLog.Info("Report", headline);

            var details = new StringBuilder();
            details.AppendLine("=== Atlases ===");
            foreach (var a in Atlases.OrderBy(a => a.Index))
            {
                details.AppendLine(
                    $"  #{a.Index} {a.Size.x}x{a.Size.y} utilization {a.Utilization:P1} " +
                    $"sources: {string.Join(", ", a.Sources.Select(s => s.Texture.name))}");
            }
            if (_skipped.Count > 0)
            {
                details.AppendLine("=== Skipped textures ===");
                foreach (var s in _skipped) details.AppendLine("  " + s);
            }
            details.AppendLine("=== Timeline ===");
            details.Append(AtoLog.Dump(AtoLogLevel.Info));

            AtoReporting.Info("Report", "ATO:info:summary", null, headline, details.ToString());
        }
    }
}
