using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using UnityEngine;

// Logging utilities. All console output is prefixed with [ATO].
// 日志工具。所有控制台输出统一以 [ATO] 前缀。

namespace Net.Fosa.AvatarTextureOptimizer
{
    /// <summary>
    /// Central logger. Every message is prefixed with "[ATO]".
    /// 统一日志器：所有消息带 [ATO] 前缀。
    /// </summary>
    public static class ATOLog
    {
        private static readonly Stopwatch Sw = Stopwatch.StartNew();
        private static readonly object Lock = new object();
        private static readonly List<string> Buffer = new List<string>();

        /// <summary>
        /// Master switch for verbose debug logs (advanced users can enable via editor prefs).
        /// 详细调试日志总开关（高级用户通过 EditorPrefs 开启）。
        /// </summary>
        public static bool Verbose
        {
            get => PlayerPrefs.GetInt("ATO.Verbose", 0) != 0;
            set => PlayerPrefs.SetInt("ATO.Verbose", value ? 1 : 0);
        }

        private static string Stamp(string body) => $"[ATO] [{Sw.Elapsed.TotalSeconds,7:F2}s] {body}";

        public static void Info(string message) { lock (Lock) { var s = Stamp(message); Buffer.Add(s); UnityEngine.Debug.Log(s); } }
        public static void Warn(string message) { lock (Lock) { var s = Stamp("[WARN] " + message); Buffer.Add(s); UnityEngine.Debug.LogWarning(s); } }
        public static void Error(string message) { lock (Lock) { var s = Stamp("[ERROR] " + message); Buffer.Add(s); UnityEngine.Debug.LogError(s); } }

        /// <summary>
        /// Detailed logs, hidden unless ATOLog.Verbose is on.
        /// 详细日志，仅当 Verbose 开启时输出。
        /// </summary>
        public static void VerboseLog(string message)
        {
            if (!Verbose) return;
            lock (Lock) { var s = Stamp("[VRB] " + message); Buffer.Add(s); UnityEngine.Debug.Log(s); }
        }

        /// <summary>
        /// Report rows collected during a bake, printed to the console at the end.
        /// 烘焙期间收集的报告行，结束时输出到控制台。
        /// </summary>
        public static void Report(string message)
        {
            lock (Lock)
            {
                var s = Stamp("[REPORT] " + message);
                Buffer.Add(s);
                UnityEngine.Debug.Log(s);
            }
        }

        /// <summary>
        /// Returns the buffered report text (used by the final report block).
        /// 返回缓冲的报告文本（供最终报告块使用）。
        /// </summary>
        public static string FlushReport()
        {
            lock (Lock)
            {
                var sb = new StringBuilder();
                foreach (var l in Buffer) sb.AppendLine(l);
                Buffer.Clear();
                return sb.ToString();
            }
        }
    }
}
