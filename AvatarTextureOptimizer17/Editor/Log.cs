// ============================================================================
// AvatarTextureOptimizer (net.fosa.avatar-texture-optimizer)
// Log.cs — [ATO] 前缀日志与阶段计时 / [ATO]-prefixed logging and stage timing
//
// 需求: 日志以[ATO]开头；包含每一步耗时、图集的贴图来源、处理岛的数量、图集大小、
//       利用率、相对原贴图的优化量；日志可预留开关（verboseLogging）供高级用户调试。
// 共识: 静态类直接可用，避免在管线里到处传 logger；每个 Stage 计时并自动输出。
// ============================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace net.fosa.avatar_texture_optimizer.editor
{
    /// <summary>
    /// ATO 日志中心 / Central [ATO] logger.
    /// </summary>
    public static class Log
    {
        /// <summary>详细日志开关（由组件配置注入） / Verbose switch (injected from component config)</summary>
        public static bool Verbose { get; set; }

        /// <summary>当前阶段名（用于统一上下文） / Current stage name</summary>
        public static string Stage { get; private set; } = "";

        private static readonly List<StageTimer> _timers = new List<StageTimer>();

        private sealed class StageTimer
        {
            public string name;
            public readonly Stopwatch sw = new Stopwatch();
        }

        /// <summary>开始一个阶段（结束用 EndStage 或 Stage 切换） / Begin a stage</summary>
        public static void BeginStage(string name)
        {
            if (_timers.Count > 0 && _timers[_timers.Count - 1].sw.IsRunning)
            {
                _timers[_timers.Count - 1].sw.Stop();
                Info($"stage '{Stage}' done in {_timers[_timers.Count - 1].sw.ElapsedMilliseconds} ms");
            }
            Stage = name;
            _timers.Add(new StageTimer { name = name });
            _timers[_timers.Count - 1].sw.Start();
            Info($"== stage: {name} ==");
        }

        /// <summary>结束所有进行中的阶段计时 / Stop all pending stage timers</summary>
        public static void EndStage(string name)
        {
            if (_timers.Count > 0 && _timers[_timers.Count - 1].sw.IsRunning)
            {
                _timers[_timers.Count - 1].sw.Stop();
                Info($"stage '{_timers[_timers.Count - 1].name}' done in {_timers[_timers.Count - 1].sw.ElapsedMilliseconds} ms");
            }
            Stage = "";
        }

        /// <summary>普通信息 / Info (always shown)</summary>
        public static void Info(string msg)
        {
            Debug.Log("[ATO] " + msg);
        }

        /// <summary>详细信息（仅 verbose 时输出） / Verbose info (only when verbose enabled)</summary>
        public static void VerboseLog(string msg)
        {
            if (Verbose) Debug.Log("[ATO][v] " + msg);
        }

        /// <summary>警告 / Warning</summary>
        public static void Warning(string msg)
        {
            Debug.LogWarning("[ATO] " + msg);
        }

        /// <summary>错误 / Error</summary>
        public static void Error(string msg)
        {
            Debug.LogError("[ATO] " + msg);
        }

        /// <summary>
        /// 字节大小人性化 / Human readable byte size.
        /// </summary>
        public static string HumanSize(long bytes)
        {
            double b = bytes;
            string[] units = { "B", "KB", "MB", "GB" };
            int i = 0;
            while (b >= 1024 && i < units.Length - 1) { b /= 1024.0; i++; }
            return $"{b:0.##} {units[i]}";
        }
    }
}
