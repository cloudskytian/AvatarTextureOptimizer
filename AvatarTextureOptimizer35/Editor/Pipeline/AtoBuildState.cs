using System;
using System.Collections.Generic;
using nadena.dev.ndmf;
using UnityEditor;
using UnityEngine;

namespace net.fosa.avatar_texture_optimizer.editor
{
    /// <summary>
    /// Per-build mutable state, stored in the BuildContext state dictionary. / 每次构建的可变状态，存于 BuildContext 状态字典。
    /// </summary>
    internal sealed class AtoBuildState
    {
        /// <summary>Avatar settings (from the AtoAvatarRoot component). / Avatar 设置（来自 AtoAvatarRoot 组件）。</summary>
        public AtoSettings Settings { get; set; }

        /// <summary>The AtoAvatarRoot component on the avatar. / Avatar 上的 AtoAvatarRoot 组件。</summary>
        public AtoAvatarRoot Component { get; set; }

        /// <summary>Resolved language code for this build's messages. / 本次构建消息使用的语言代码。</summary>
        public string LanguageCode { get; set; } = "en";

        /// <summary>Overall stopwatch. / 总计时器。</summary>
        public System.Diagnostics.Stopwatch TotalStopwatch { get; } = System.Diagnostics.Stopwatch.StartNew();

        // ---- cancellation ----
        private bool _cancelled;

        /// <summary>Whether the user cancelled the build. / 用户是否已取消构建。</summary>
        public bool IsCancelled => _cancelled;

        public void Cancel() => _cancelled = true;

        /// <summary>Throw if cancelled. Call between and inside stages. / 已取消则抛异常。在阶段之间与阶段内部调用。</summary>
        public void ThrowIfCancelled()
        {
            if (_cancelled) throw new OperationCanceledException("ATO build cancelled by user");
        }

        // ---- progress ----
        private string _progressTitle = "";

        public void BeginProgress(string stageName)
        {
            _progressTitle = stageName;
            EditorUtility.DisplayProgressBar(stageName, "", 0f);
        }

        public void SetProgress(string step, float fraction)
        {
            ThrowIfCancelled();
            // Detect the cancel button on the progress bar. / 检测进度条的取消按钮。
            if (EditorUtility.DisplayCancelableProgressBar(_progressTitle, step, fraction))
            {
                Cancel();
                ThrowIfCancelled();
            }
        }

        public void EndProgress()
        {
            EditorUtility.ClearProgressBar();
        }

        // ---- statistics for the final report ----
        public int TextureCount;
        public int UvGroupCount;
        public int IslandCount;
        public int AtlasCount;
        public long BytesBefore;
        public long BytesAfter;
        public int WarningCount;
        public int ErrorCount;

        /// <summary>Per-atlas report records. / 每图集的报告记录。</summary>
        public readonly List<AtoAtlasReportRecord> AtlasRecords = new List<AtoAtlasReportRecord>();

        /// <summary>Per-texture report records (fallback textures etc.). / 每贴图的报告记录（fallback 贴图等）。</summary>
        public readonly List<AtoTextureReportRecord> TextureRecords = new List<AtoTextureReportRecord>();

        /// <summary>All warnings/notes collected during the build. / 构建期间收集的全部警告/提示。</summary>
        public readonly List<string> Notes = new List<string>();

        public void Note(string message) => Notes.Add(message);

        public string Tr(string key, params object[] args) => AtoLoc.Tr(LanguageCode, key, args);
    }

    /// <summary>
    /// One atlas's report record. / 一张图集的报告记录。
    /// </summary>
    internal sealed class AtoAtlasReportRecord
    {
        /// <summary>Atlas texture name. / 图集贴图名。</summary>
        public string Name;
        /// <summary>Category key (e.g. Main/Normal/Mask). / 分类键（如 Main/Normal/Mask）。</summary>
        public string Category;
        public int Width;
        public int Height;
        public int IslandCount;
        public int SourceTextureCount;
        /// <summary>Area utilization 0..1. / 面积利用率 0..1。</summary>
        public float Utilization;
        /// <summary>Bytes saved vs. the source textures, in percent (0..100). / 相对来源贴图节省的体积百分比。</summary>
        public float SavedPercent;
    }

    /// <summary>
    /// One processed texture's report record. / 一张被处理贴图的报告记录。
    /// </summary>
    internal sealed class AtoTextureReportRecord
    {
        public string Name;
        public long BytesBefore;
        public long BytesAfter;
        public float SavedPercent;
        public string Reason;
    }
}
