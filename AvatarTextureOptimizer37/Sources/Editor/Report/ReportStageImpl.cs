// ============================================================================
// ATO - report stage (stage 8)
// ATO - 报告阶段（阶段8）
//
// NDMF console: summary by default; per-atlas detail lines only in verbose
// mode. The full log buffer is also written to a temp file for advanced
// users.
// NDMF 控制台：默认摘要；每图集细节行仅 verbose 模式。完整日志缓冲另写入临
// 时文件供高级用户查阅。
// ============================================================================

#region

using System.Collections.Generic;
using System.IO;
using nadena.dev.ndmf;
using net.fosa.AvatarTextureOptimizer.Editor.Analysis;
using net.fosa.AvatarTextureOptimizer.Editor.Core;
using UnityEngine;

#endregion

namespace net.fosa.AvatarTextureOptimizer.Editor.Report
{
    public static class ReportStageImpl
    {
        public static void Execute(ATOContext ctx, BuildContext context, bool verbose)
        {
            var an = ctx.Analysis;
            var log = ctx.Log;
            if (an == null) return;

            long originalBytes = 0, optimizedBytes = 0;
            var countedOriginal = new HashSet<int>();

            // original bytes  原始字节
            foreach (var (tid, tref) in an.Textures)
            {
                if (tref.Whitelisted) continue;
                originalBytes += (long) tref.Width * tref.Height * 4;
            }

            // optimized bytes  优化后字节
            if (an.PackedResult != null)
            {
                foreach (var page in an.PackedResult.Pages)
                {
                    if (page.Texture == null) continue;
                    optimizedBytes += (long) page.W * page.H * 4;
                }
            }
            foreach (var (tid, scaled) in an.ScaledTextures)
            {
                if (scaled == null) continue;
                optimizedBytes += (long) scaled.width * scaled.height * 4;
            }
            // textures that kept original size (whole-scale = 1 or whitelist-free unchanged)
            // 保持原尺寸的贴图
            foreach (var (tid, tref) in an.Textures)
            {
                if (tref.Whitelisted) continue;
                bool atlased = false;
                foreach (var island in an.Islands)
                {
                    if (island.SampledTextureIds.Contains(tid) && island.AtlasPage >= 0)
                    {
                        atlased = true;
                        break;
                    }
                }
                if (atlased) continue;
                if (an.ScaledTextures.ContainsKey(tid)) continue;
                optimizedBytes += (long) tref.Width * tref.Height * 4;
            }

            int islands = an.IslandCount;
            int pages = an.PackedResult != null ? an.PackedResult.Pages.Count : 0;
            int reduction = originalBytes == 0 ? 0 : (int) System.Math.Round((1f - (float) optimizedBytes / originalBytes) * 100f);

            // summary  摘要
            Debug.Log(string.Format(
                "[ATO] ===== ATO report {0} ===== islands: {1} | atlas pages: {2} | " +
                "textures {3:N0} -> {4:N0} bytes ({5}% reduction) | whitelisted: {6} | " +
                "material merges: {7} | total {8:F0} ms\n" +
                "===== ATO 报告 ===== 岛: {1} | 图集页: {2} | 贴图 {3:N0} -> {4:N0} 字节（减少 {5}%） | " +
                "白名单: {6} | 材质合并: {7} | 总耗时 {8:F0} ms",
                context.AvatarRootObject.name, islands, pages, originalBytes, optimizedBytes,
                reduction, an.WhitelistedTextureCount, an.MaterialDedupMap.Count,
                log.NowMs));

            // detail (verbose only)  细节（仅 verbose）
            if (verbose)
            {
                if (an.PackedResult != null)
                {
                    foreach (var page in an.PackedResult.Pages)
                    {
                        var tg = an.TypeGroups[page.TypeGroupId];
                        var sources = new HashSet<string>();
                        foreach (var tid in tg.TextureIds)
                        {
                            sources.Add(an.Textures[tid].Texture.name);
                        }
                        foreach (var dict in tg.SpecialTextures.Values)
                        {
                            foreach (var sid in dict.Values)
                            {
                                sources.Add(an.Textures[sid].Texture.name);
                            }
                        }
                        Debug.Log(string.Format(
                            "[ATO]   page {0}: {1}x{2} util={3:F1}% islands={4} sources=[{5}] mirror={6}",
                            page.Texture?.name ?? "?", page.W, page.H, page.Utilization * 100f,
                            page.IslandCount, string.Join(", ", sources),
                            page.IsMirrorRole >= 0 ? "role" + page.IsMirrorRole : "main"));
                    }
                }

                // full log dump  完整日志转储
                var dir = Path.Combine(System.Environment.GetTempPath(), "ATO_Reports");
                try
                {
                    Directory.CreateDirectory(dir);
                    var file = Path.Combine(dir, $"ATO_Report_{context.AvatarRootObject.name}_{System.DateTime.Now:yyyyMMdd_HHmmss}.txt");
                    File.WriteAllText(file, log.RenderAll());
                    Debug.Log("[ATO] full report: " + file);
                }
                catch (System.Exception e)
                {
                    Debug.LogWarning("[ATO] could not write report file: " + e.Message);
                }
            }
        }
    }
}
