using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// Shared state for one avatar build, threaded through NDMF passes via
    /// <c>BuildContext.GetState&lt;ATOState&gt;()</c>. / 单次 Avatar 构建的共享状态，通过 NDMF pass 传递。
    /// </summary>
    public sealed class ATOState
    {
        public bool initialized = false;

        // resolved settings / 解析后的设置
        public ATOPlatformSettings settings;
        public ATOPlatform platform = ATOPlatform.PC;

        // whitelist / 白名单
        public readonly HashSet<UnityEngine.Object> whitelistedObjects = new HashSet<UnityEngine.Object>();
        public readonly HashSet<Texture2D> whitelistedTextures = new HashSet<Texture2D>();

        // collection results / 收集结果
        public readonly List<Renderer> renderers = new List<Renderer>();
        public readonly Dictionary<Texture2D, TextureEntry> textureEntries = new Dictionary<Texture2D, TextureEntry>();
        public readonly List<TextureEntry> textures = new List<TextureEntry>();

        // UV groups (one island + its textures; shared UV) / UV 组（一个岛 + 其贴图；共享 UV）
        public readonly List<UvGroup> uvGroups = new List<UvGroup>();

        // UV groups that failed atlas packing (fall back to direct scaling) / 装箱失败回退直接缩放的 UV 组
        public readonly List<UvGroup> fallbackGroups = new List<UvGroup>();

        // (renderer, slot) whose material is switched individually by animation (blocks slot merge)
        // 被动画单独切换材质的（渲染器,槽）——禁止合并该槽
        public readonly HashSet<(Renderer, int)> animatedMaterialSlots = new HashSet<(Renderer, int)>();

        // generated atlases / 生成的图集
        public readonly List<AtlasResult> atlases = new List<AtlasResult>();

        // report / 报告
        public readonly List<ATOReportEntry> report = new List<ATOReportEntry>();

        // cancellation token / 取消令牌
        public System.Threading.CancellationTokenSource cancellation;

        /// <summary>Raise an error to abort the build. / 触发错误以中止构建。</summary>
        public void Abort(string reason, UnityEngine.Object ctx = null)
        {
            ATOLogger.Error("aborted: " + reason, ctx);
            throw new InvalidOperationException("[ATO] " + reason);
        }

        public void AddReport(string stage, long elapsedMs, string summary, List<string> details)
        {
            report.Add(new ATOReportEntry { stage = stage, elapsedMs = elapsedMs, summary = summary, details = details });
        }

        /// <summary>Emit the final report to the console. / 输出最终报告到控制台。</summary>
        public void EmitReport()
        {
            var sb = new StringBuilder();
            sb.AppendLine("==================================================");
            sb.AppendLine("[ATO] Avatar Texture Optimizer — build report");
            sb.AppendLine("==================================================");
            long total = 0;
            foreach (var e in report)
            {
                total += e.elapsedMs;
                sb.AppendLine($"[ATO] [{e.stage}] {e.elapsedMs} ms — {e.summary}");
                if (e.details.Count > 0 && ATOLogger.Verbose)
                {
                    foreach (var d in e.details) sb.AppendLine("[ATO]     · " + d);
                }
            }
            sb.AppendLine($"[ATO] total elapsed: {total} ms");
            Debug.Log(sb.ToString());
        }
    }
}
