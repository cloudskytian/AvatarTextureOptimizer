// AvatarTextureOptimizer
// File: Editor/Logging/BuildReport.cs
//
// Structured build report. Collected during the bake and printed to the NDMF
// console when finished: overall results by default, details collapsible.
//
// 结构化烘焙报告。烘焙期间收集数据，完成后输出到 NDMF 控制台：默认展示
// 总体结果，细节折叠起来。

using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace net.fosa.avatar_texture_optimizer.editor.logging
{
    /// <summary>One generated atlas entry. / 一张生成的图集条目。</summary>
    public sealed class AtlasReportEntry
    {
        public string Name;                    // 图集名称 / atlas name
        public int Width, Height;              // 尺寸 / size
        public int SourceCount;                // 贴图来源数 / number of source textures
        public float Utilization;              // 0~1 利用率 / utilization
        public long OriginalBytes;             // 原贴图总字节 / original total bytes
        public long AtlasBytes;                // 图集字节 / atlas bytes
        public List<string> Sources = new List<string>(); // 贴图来源 / sources
        public int IslandCount;                // 处理的岛数量 / islands processed
    }

    /// <summary>
    /// Collects and renders the end-of-build report.
    /// 收集并渲染构建结束时的报告。
    /// </summary>
    public sealed class BuildReport
    {
        private readonly List<AtlasReportEntry> _atlases = new List<AtlasReportEntry>();
        private readonly List<string> _warnings = new List<string>();
        private long _originalTotalBytes;
        private long _resultTotalBytes;
        private int _totalIslands;
        private readonly Dictionary<string, long> _phaseTimings = new Dictionary<string, long>();
        private readonly System.Diagnostics.Stopwatch _sw = new System.Diagnostics.Stopwatch();
        private string _lastPhase;

        public void BuildStarted()
        {
            _sw.Restart();
            _lastPhase = null;
        }

        /// <summary>Record a phase boundary; durations are accumulated per phase. / 记录阶段边界；耗时按阶段累计。</summary>
        public void BeginPhase(string phase)
        {
            if (_lastPhase != null)
            {
                _phaseTimings.TryGetValue(_lastPhase, out var prev);
                _phaseTimings[_lastPhase] = prev + _sw.ElapsedMilliseconds;
            }
            _lastPhase = phase;
            _sw.Restart();
        }

        public void AddAtlas(AtlasReportEntry entry)
        {
            _atlases.Add(entry);
            _totalIslands += entry.IslandCount;
        }

        public void AddWarnings(IEnumerable<string> warnings)
        {
            foreach (var w in warnings) _warnings.Add(w);
        }

        public void AddWarning(string warning) => _warnings.Add(warning);

        public void AddBytes(long original, long result)
        {
            _originalTotalBytes += original;
            _resultTotalBytes += result;
        }

        /// <summary>
        /// Print the report to the Unity console. Overall results by default;
        /// per-atlas details printed after (they render as a block the user can
        /// collapse in the console).
        /// 将报告输出到 Unity 控制台。默认输出总体结果；图集细节随后输出
        /// （在控制台中可折叠成块）。
        /// </summary>
        public void Print()
        {
            if (_lastPhase != null)
            {
                _phaseTimings.TryGetValue(_lastPhase, out var prev);
                _phaseTimings[_lastPhase] = prev + _sw.ElapsedMilliseconds;
                _lastPhase = null;
            }

            var sb = new StringBuilder();
            sb.AppendLine();
            sb.AppendLine("================================================================");
            sb.AppendLine("[ATO] Avatar Texture Optimizer - Build Report / 烘焙报告");
            sb.AppendLine("================================================================");

            // Overall results / 总体结果
            sb.AppendLine($"[ATO] Atlases generated  : {_atlases.Count}");
            sb.AppendLine($"[ATO] Islands processed  : {_totalIslands}");
            if (_originalTotalBytes > 0)
            {
                double saved = _originalTotalBytes - _resultTotalBytes;
                double ratio = saved / (double)_originalTotalBytes;
                sb.AppendLine($"[ATO] Texture memory      : {_originalTotalBytes / 1048576.0:F2} MB -> {_resultTotalBytes / 1048576.0:F2} MB (saved {saved / 1048576.0:F2} MB, {ratio:P1})");
            }
            sb.AppendLine("[ATO] Phase timings / 阶段耗时:");
            foreach (var kv in _phaseTimings)
                sb.AppendLine($"[ATO]   {kv.Key}: {kv.Value} ms");

            if (_warnings.Count > 0)
            {
                sb.AppendLine($"[ATO] Warnings ({_warnings.Count}):");
                foreach (var w in _warnings) sb.AppendLine($"[ATO]   - {w}");
            }

            sb.AppendLine("--- Atlas details / 图集细节 ---");
            foreach (var a in _atlases)
            {
                sb.AppendLine($"[ATO] {a.Name}: {a.Width}x{a.Height}, sources={a.SourceCount}, utilization={a.Utilization:P1}, islands={a.IslandCount}, {a.OriginalBytes / 1024.0:F1}KB -> {a.AtlasBytes / 1024.0:F1}KB");
                if (ATOLog.Verbose)
                {
                    sb.AppendLine("[ATO]   Sources / 来源:");
                    foreach (var s in a.Sources)
                        sb.AppendLine($"[ATO]     - {s}");
                }
            }

            UnityEngine.Debug.Log(sb.ToString());
        }
    }
}
