using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using NetFosa.AvatarTextureOptimizer;

namespace NetFosa.AvatarTextureOptimizer.Editor.Logging
{
    /// <summary>
    /// 构建期日志。所有输出以 [ATO] 开头；verbose 受设置控制。
    /// Build-time logger. Every line is prefixed with [ATO].
    /// </summary>
    public sealed class ATOLogger
    {
        public bool Verbose { get; set; }

        public void Info(string message) => Debug.Log($"[ATO] {message}");
        public void Warn(string message) => Debug.LogWarning($"[ATO] {message}");
        public void Error(string message) => Debug.LogError($"[ATO] {message}");

        public void VerboseLog(string message)
        {
            if (Verbose) Debug.Log($"[ATO][Verbose] {message}");
        }

        public void Timed(string step, TimeSpan elapsed) => Info($"{step} took {elapsed.TotalMilliseconds:F0} ms");
    }

    /// <summary>
    /// 构建报告：记录每步耗时、图集来源、处理岛数、图集大小/利用率、相对原贴图优化量。
    /// 默认输出总体结果，细节折叠（以 [ATO][Detail] 输出便于过滤）。
    /// </summary>
    public sealed class BuildReport
    {
        public struct AtlasEntry
        {
            public string name;
            public int width;
            public int height;
            public int islandCount;
            public float utilization; // 0..1
            public List<string> sources; // 图集贴图来源
            public ATOTextureCategory category;
        }

        public struct ScaledTextureEntry
        {
            public string name;
            public int fromW, fromH, toW, toH;
            public bool atlasFailed;
        }

        public DateTime StartedAt = DateTime.Now;
        public readonly StopwatchBox Stopwatch = new StopwatchBox();
        public readonly List<AtlasEntry> Atlases = new List<AtlasEntry>();
        public readonly List<ScaledTextureEntry> ScaledTextures = new List<ScaledTextureEntry>();
        public readonly List<string> Warnings = new List<string>();
        public readonly List<string> InfoLines = new List<string>();

        public long TexelsIn;
        public long TexelsOut;
        public int IslandsProcessed;
        public int TexturesIn;
        public int TexturesOut;
        public int WhitelistedTextures;

        public readonly List<(string step, TimeSpan elapsed)> StepTimings = new List<(string, TimeSpan)>();

        public void AddWarning(string message) => Warnings.Add(message);

        public string FormatSummary(string localizer)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"[ATO] ==== {localizer} ====");
            var total = (DateTime.Now - StartedAt).TotalMilliseconds;
            sb.AppendLine($"[ATO] Total time: {total:F0} ms");
            sb.AppendLine($"[ATO] Atlases: {Atlases.Count} | Islands: {IslandsProcessed} | Textures in: {TexturesIn} -> out: {TexturesOut} | Whitelisted: {WhitelistedTextures}");
            if (TexelsIn > 0)
            {
                double ratio = TexelsOut / (double)TexelsIn;
                sb.AppendLine($"[ATO] Texels in: {TexelsIn:N0} -> out: {TexelsOut:N0} (reduction {1 - ratio:P1})");
            }
            if (Warnings.Count > 0)
            {
                sb.AppendLine($"[ATO] Warnings: {Warnings.Count}");
                for (int i = 0; i < Warnings.Count && i < 20; i++) sb.AppendLine($"[ATO][Warning] {Warnings[i]}");
                if (Warnings.Count > 20) sb.AppendLine($"[ATO][Warning] ... and {Warnings.Count - 20} more");
            }
            return sb.ToString();
        }

        public string FormatDetails()
        {
            var sb = new StringBuilder();
            sb.AppendLine("[ATO][Detail] ==== Detailed report ====");
            foreach (var s in StepTimings) sb.AppendLine($"[ATO][Detail] step {s.step}: {s.elapsed.TotalMilliseconds:F0} ms");
            foreach (var a in Atlases)
            {
                var sources = string.Join(", ", a.sources.ToArray());
                sb.AppendLine($"[ATO][Detail] atlas {a.name} {a.width}x{a.height} {a.islandCount} islands utilization {a.utilization:P1} category={a.category} sources: {sources}");
            }
            foreach (var t in ScaledTextures)
            {
                if (t.atlasFailed)
                    sb.AppendLine($"[ATO][Detail] no-atlas fallback (whole texture scaled): {t.name} {t.fromW}x{t.fromH} -> {t.toW}x{t.toH}");
                else
                    sb.AppendLine($"[ATO][Detail] scaled texture: {t.name} {t.fromW}x{t.fromH} -> {t.toW}x{t.toH}");
            }
            foreach (var s in InfoLines) sb.AppendLine($"[ATO][Detail] {s}");
            return sb.ToString();
        }
    }

    /// <summary>轻量秒表（引用类型，避免 struct 复制问题）。</summary>
    public sealed class StopwatchBox
    {
        private System.Diagnostics.Stopwatch _sw = new System.Diagnostics.Stopwatch();
        public void Start() { _sw.Restart(); }
        public void Stop() { _sw.Stop(); }
        public TimeSpan Elapsed => _sw.Elapsed;
    }
}
