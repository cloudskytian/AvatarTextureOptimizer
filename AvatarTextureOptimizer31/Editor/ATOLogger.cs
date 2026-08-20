// ATOLogger.cs
// Structured logging system. All ATO logs are prefixed with [ATO].
// Logs include timing, island counts, atlas sizes, utilization, and optimization deltas.
// 结构化日志系统。所有 ATO 日志以 [ATO] 为前缀。
//
// Copyright (c) 2024 fosa. Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using UnityEngine;

namespace Fosa.AvatarTextureOptimizer
{
    /// <summary>
    /// Centralized logging for the entire ATO pipeline. Logs go to the Unity Console
    /// and are summarized for the NDMF error report.
    /// ATO 管线的集中日志系统。
    /// </summary>
    internal sealed class ATOLogger
    {
        private static ATOLogger _instance;
        internal static ATOLogger Instance => _instance ??= new ATOLogger();

        private bool _verbose;
        private readonly List<ATOLogEntry> _entries = new List<ATOLogEntry>();
        private readonly Dictionary<string, Stopwatch> _timers = new Dictionary<string, Stopwatch>();
        private readonly Dictionary<string, long> _phaseTimings = new Dictionary<string, long>();

        internal void Configure(bool verbose)
        {
            _verbose = verbose;
            _entries.Clear();
            _timers.Clear();
            _phaseTimings.Clear();
        }

        internal void Info(string message)
        {
            var entry = new ATOLogEntry("[ATO] " + message, LogType.Info);
            _entries.Add(entry);
            UnityEngine.Debug.Log(entry.Message);
        }

        internal void Verbose(string message)
        {
            if (!_verbose) return;
            var msg = "[ATO][VERBOSE] " + message;
            _entries.Add(new ATOLogEntry(msg, LogType.Log));
            UnityEngine.Debug.Log(msg);
        }

        internal void Warning(string message)
        {
            var entry = new ATOLogEntry("[ATO][WARNING] " + message, LogType.Warning);
            _entries.Add(entry);
            UnityEngine.Debug.LogWarning(entry.Message);
        }

        internal void Error(string message)
        {
            var entry = new ATOLogEntry("[ATO][ERROR] " + message, LogType.Error);
            _entries.Add(entry);
            UnityEngine.Debug.LogError(entry.Message);
        }

        /// <summary>Begin timing a named phase. / 开始计时命名阶段。</summary>
        internal void BeginTimer(string name)
        {
            if (!_timers.TryGetValue(name, out var sw))
            {
                sw = new Stopwatch();
                _timers[name] = sw;
            }
            sw.Restart();
        }

        /// <summary>End timing a named phase and log elapsed. / 结束计时并记录耗时。</summary>
        internal long EndTimer(string name)
        {
            if (_timers.TryGetValue(name, out var sw))
            {
                sw.Stop();
                _phaseTimings[name] = _phaseTimings.TryGetValue(name, out var prev) ? prev + sw.ElapsedMilliseconds : sw.ElapsedMilliseconds;
                Verbose($"Timing [{name}]: {sw.ElapsedMilliseconds}ms");
                return sw.ElapsedMilliseconds;
            }
            return 0;
        }

        internal List<ATOLogEntry> GetEntries() => _entries;
        internal Dictionary<string, long> GetPhaseTimings() => _phaseTimings;

        /// <summary>Generate a summary report string for the NDMF console. / 为 NDMF 控制台生成汇总报告字符串。</summary>
        internal string GenerateSummaryReport(ATOOptimizationReport report)
        {
            var sb = new StringBuilder();
            sb.AppendLine("═══════════════════════════════════════════════");
            sb.AppendLine("  Avatar Texture Optimizer — Optimization Report");
            sb.AppendLine("  Avatar Texture Optimizer — 优化报告");
            sb.AppendLine("═══════════════════════════════════════════════");
            sb.AppendLine();

            sb.AppendLine($"▶ Original textures: {report.OriginalTextureCount} ({FormatBytes(report.OriginalTextureBytes)})");
            sb.AppendLine($"  原始贴图数: {report.OriginalTextureCount}");
            sb.AppendLine($"▶ Optimized textures/atlases: {report.OptimizedTextureCount} ({FormatBytes(report.OptimizedTextureBytes)})");
            sb.AppendLine($"  优化后贴图/图集数: {report.OptimizedTextureCount}");
            var savings = report.OriginalTextureBytes > 0
                ? (1.0 - (double)report.OptimizedTextureBytes / report.OriginalTextureBytes) * 100
                : 0;
            sb.AppendLine($"▶ Memory savings: {savings:F1}% ({FormatBytes(report.OriginalTextureBytes - report.OptimizedTextureBytes)})");
            sb.AppendLine($"  节省: {savings:F1}%");
            sb.AppendLine($"▶ UV islands processed: {report.IslandsProcessed}");
            sb.AppendLine($"  处理岛数: {report.IslandsProcessed}");
            sb.AppendLine($"▶ Islands scaled (quality): {report.IslandsScaled}");
            sb.AppendLine($"  缩放岛数: {report.IslandsScaled}");
            sb.AppendLine($"▶ Atlases generated: {report.AtlasesGenerated}");
            sb.AppendLine($"  生成图集数: {report.AtlasesGenerated}");
            sb.AppendLine($"▶ Materials deduplicated: {report.MaterialsDeduplicated}");
            sb.AppendLine($"  去重材质数: {report.MaterialsDeduplicated}");
            sb.AppendLine($"▶ Textures deduplicated: {report.TexturesDeduplicated}");
            sb.AppendLine($"  去重贴图数: {report.TexturesDeduplicated}");

            sb.AppendLine();
            sb.AppendLine("── Per-Atlas Details / 各图集详情 ──────────────");
            foreach (var atlas in report.AtlasDetails)
            {
                var util = atlas.Utilization * 100f;
                sb.AppendLine($"  • {atlas.Name} ({atlas.Width}×{atlas.Height}) util={util:F1}%");
                sb.AppendLine($"    Sources: {atlas.SourceCount} textures, {atlas.IslandCount} islands");
            }

            sb.AppendLine();
            sb.AppendLine("── Phase Timings / 各阶段耗时 ─────────────────");
            foreach (var kvp in _phaseTimings)
            {
                sb.AppendLine($"  • {kvp.Key}: {kvp.Value}ms");
            }

            if (_entries.Count > 0)
            {
                var warnings = _entries.FindAll(e => e.Type == LogType.Warning);
                if (warnings.Count > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine($"── Warnings ({warnings.Count}) / 警告 ──────────");
                    foreach (var w in warnings)
                        sb.AppendLine($"  ⚠ {w.Message}");
                }
            }

            sb.AppendLine();
            sb.AppendLine("═══════════════════════════════════════════════");
            return sb.ToString();
        }

        internal static string FormatBytes(long bytes)
        {
            if (bytes < 1024) return $"{bytes} B";
            if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
            if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
            return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
        }
    }

    internal sealed class ATOLogEntry
    {
        internal string Message { get; }
        internal LogType Type { get; }

        internal ATOLogEntry(string message, LogType type)
        {
            Message = message;
            Type = type;
        }
    }

    /// <summary>Aggregated optimization report data. / 汇总优化报告数据。</summary>
    internal sealed class ATOOptimizationReport
    {
        internal int OriginalTextureCount;
        internal long OriginalTextureBytes;
        internal int OptimizedTextureCount;
        internal long OptimizedTextureBytes;
        internal int IslandsProcessed;
        internal int IslandsScaled;
        internal int AtlasesGenerated;
        internal int MaterialsDeduplicated;
        internal int TexturesDeduplicated;
        internal List<AtlasDetail> AtlasDetails = new List<AtlasDetail>();
    }

    internal sealed class AtlasDetail
    {
        internal string Name;
        internal int Width;
        internal int Height;
        internal float Utilization;
        internal int SourceCount;
        internal int IslandCount;
    }
}
