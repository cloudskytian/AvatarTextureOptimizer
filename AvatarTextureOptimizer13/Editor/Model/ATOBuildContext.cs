// ATO — Avatar Texture Optimizer
// Per-build context carried between passes, plus progress display and cancellation.
// Pass 之间传递的单次构建上下文，外加进度显示与取消。

using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using net.fosa.ato;

namespace net.fosa.ato.editor
{
    /// <summary>
    /// Per-build state, obtained via <c>ctx.GetState&lt;ATOBuildContext&gt;()</c>.
    /// 单次构建状态，通过 <c>ctx.GetState&lt;ATOBuildContext&gt;()</c> 获取。
    /// </summary>
    public class ATOBuildContext
    {
        public ATOAnalysisResult Result;
        public ATOReport Report = new ATOReport();
        public AvatarTextureOptimizer Component;
        public ATOEffectiveSettings Settings;
        /// <summary>Resolved build platform (drives atlas max size). 解析出的构建平台（决定图集最大边长）。</summary>
        public net.fosa.ato.ATOPlatform Platform;
        /// <summary>Folder next to the NDMF asset container where generated textures are saved. 生成贴图保存目录（NDMF 资产容器旁）。</summary>
        public string AssetFolder = "Assets/ATO_Generated";

        /// <summary>Set when the user cancelled the build via the ATO menu item. 用户通过 ATO 菜单项取消构建时置位。</summary>
        public static bool Cancelled;

        // ---- Caches (memory-conscious; see CLAUDE.md requirement #27). 缓存（注意内存，#27）。----
        /// <summary>Decoded pixel cache: Texture2D → raw RGBA32 pixels (4 bytes/px, memory-conscious). 解码像素缓存（4 字节/像素，注意内存）。</summary>
        public Dictionary<Texture2D, Color32[]> DecodedPixels = new Dictionary<Texture2D, Color32[]>();

        /// <summary>Raster cache: (island, atlas size) → bitmask. 光栅化缓存：(岛, 图集尺寸) → 位掩码。</summary>
        public Dictionary<(ATOIsland, int), BitMask> RasterCache = new Dictionary<(ATOIsland, int), BitMask>();

        public void ClearCaches()
        {
            DecodedPixels.Clear();
            RasterCache.Clear();
            GC.Collect();
        }

        // ---- Progress & cancellation. 进度与取消。----
        private string _stageKey;
        private int _total;
        private int _done;

        public void BeginStage(string i18nStageKey, int totalUnits)
        {
            _stageKey = i18nStageKey;
            _total = Mathf.Max(1, totalUnits);
            _done = 0;
            UpdateBar();
        }

        public void ReportProgress(int doneUnits)
        {
            _done = doneUnits;
            UpdateBar();
        }

        public void EndStage()
        {
            EditorUtility.ClearProgressBar();
        }

        private void UpdateBar()
        {
            if (Cancelled) return;
            float frac = Mathf.Clamp01((float)_done / _total);
            EditorUtility.DisplayProgressBar(
                "ATO — Avatar Texture Optimizer",
                $"{ATOI18n.T(_stageKey)}  ({_done}/{_total})",
                frac);
        }

        /// <summary>Throw if the user requested cancellation. 若用户请求取消则抛出。</summary>
        public void ThrowIfCancelled()
        {
            if (Cancelled) throw new ATOBuildCancelledException();
        }
    }

    /// <summary>
    /// Thrown to unwind a cancelled build; resources are released in the pass finally blocks
    /// and temporary assets on disk are left in place.
    /// 取消构建时抛出以展开调用栈；资源在各 Pass 的 finally 中释放，磁盘临时资产保留。
    /// </summary>
    public class ATOBuildCancelledException : Exception
    {
        public ATOBuildCancelledException() : base("ATO build cancelled by user.") { }
    }

    /// <summary>
    /// Editor menu helpers for development + cancellation. 开发与取消用的编辑器菜单。
    /// </summary>
    public static class ATOMenu
    {
        [MenuItem("ATO/Cancel current build")]
        public static void CancelBuild()
        {
            ATOBuildContext.Cancelled = true;
            ATOLog.Warn(ATOI18n.T(ATOI18nKeys.ProgressCancelled));
        }

        [MenuItem("ATO/Enable verbose logging", true)]
        public static bool ValidateLogToggle() => true;

        [MenuItem("ATO/Enable verbose logging")]
        public static void ToggleVerbose()
        {
            ATOLog.Verbose = !ATOLog.Verbose;
            ATOLog.Info($"Verbose logging {(ATOLog.Verbose ? "enabled" : "disabled")}.");
        }
    }
}
