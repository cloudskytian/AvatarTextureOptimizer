using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Fosa.Ato.Editor.i18n;
using Fosa.Ato.Runtime;
using nadena.dev.ndmf;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Fosa.Ato.Editor.Pipeline
{
    /// <summary>
    /// Orchestrates all optimization stages. Holds shared analysis state (collected textures,
    /// islands, groups, results) so stages can communicate. Every stage logs [ATO] timings and
    /// checks cancellation.
    /// 编排所有阶段，持有共享分析状态；每阶段打印耗时并检查取消。
    /// </summary>
    internal sealed class AtoPipeline
    {
        private BuildContext _ctx;
        private AvatarTextureOptimizer _component;
        private AtoSettings _settings;
        private AtoProgress _progress;
        private readonly Stopwatch _total = new();

        // Shared state
        internal readonly Dictionary<MaterialSlotRef, List<TextureUsage>> SlotTextures = new();
        internal readonly Dictionary<Texture2D, TextureUsage> Usages = new();
        internal readonly List<Island> Islands = new();
        internal readonly List<UvGroup> UvGroups = new();
        internal readonly List<TypeGroup> TypeGroups = new();
        internal readonly List<AtlasResult> Atlases = new();
        internal readonly HashSet<UnityEngine.Object> Whitelist = new();
        internal readonly AtoReport Report = new();

        private interface IStage { string Name { get; } float Weight { get; } void Run(AtoPipeline p); }

        public void Run(BuildContext ctx)
        {
            _ctx = ctx;
            _component = ctx.AvatarRootObject.GetComponentInChildren<AvatarTextureOptimizer>(true);
            if (_component == null || _component.Settings == null) return; // not configured / 未配置
            if (!_component.Settings.Enabled) { AtoLog.Info("Disabled on this avatar; skipping. / 本 Avatar 已禁用，跳过。"); return; }

            _settings = _component.Settings;
            AtoLog.Verbose = _settings.VerboseLogging;

            // ---- Validation: single component + descriptor ----
            // 校验：组件唯一 + 必须有 VRCAvatarDescriptor
            var all = ctx.AvatarRootObject.GetComponentsInChildren<AvatarTextureOptimizer>(true);
            if (all.Length > 1)
            {
                AtoLog.Error(Localizer.T("err.duplicateComponent"));
                ReportFatal(Localizer.T("err.duplicateComponent"));
                return;
            }
            if (!_component.IsValidRoot)
            {
                AtoLog.Error(Localizer.T("err.noDescriptor"));
                ReportFatal(Localizer.T("err.noDescriptor"));
                return;
            }

            using (_progress = new AtoProgress())
            {
                _total.Start();
                try
                {
                    var stages = new IStage[]
                    {
                        new Stages.Stage01Collect(),
                        new Stages.Stage02MaterialMapping(),
                        new Stages.Stage03Animation(),
                        new Stages.Stage04Eligibility(),
                        new Stages.Stage05Dedup(),
                        new Stages.Stage06Islands(),
                        new Stages.Stage07Quality(),
                        new Stages.Stage08Packing(),
                        new Stages.Stage09Compose(),
                        new Stages.Stage10MeshWrite(),
                        new Stages.Stage11Rewrite(),
                        new Stages.Stage12Finalize(),
                    };

                    float totalWeight = stages.Sum(s => s.Weight);
                    float acc = 0f;
                    foreach (var s in stages)
                    {
                        _progress.ThrowIfCancelled();
                        _progress.Stage(s.Name, acc / totalWeight);
                        AtoLog.Timed(s.Name, () => s.Run(this));
                        acc += s.Weight;
                    }

                    _total.Stop();
                    Report.ElapsedMs = _total.ElapsedMilliseconds;
                    EmitReport();
                }
                catch (OperationCanceledException)
                {
                    _total.Stop();
                    AtoLog.Warn(Localizer.T("err.canceled"));
                    // Keep temp assets on disk; release CPU/GPU/memory via Dispose paths in stages.
                    // 保留硬盘临时资产，释放 CPU/GPU/内存
                    CleanupRuntimeResources();
                }
                catch (Exception e)
                {
                    _total.Stop();
                    AtoLog.Error(e, "Pipeline failed; avatar left unchanged where possible. / 流程失败，尽量保持 Avatar 不变。");
                    CleanupRuntimeResources();
                    throw;
                }
                finally
                {
                    // Remove self from the baked clone per spec / 烘焙后从成品移除自身
                    if (_component != null)
                    {
                        if (_ctx.IsTemporaryAsset(_component) || _component.gameObject.scene.IsValid())
                            UnityEngine.Object.DestroyImmediate(_component);
                    }
                }
            }
        }

        // ---- Accessors for stages / 阶段访问器 ----
        public T GetState<T>() where T : class, new() => _ctx.GetState<T>();
        public BuildContext Ctx => _ctx;
        public AvatarTextureOptimizer Component => _component;
        public AtoSettings Settings => _settings;
        public AtoProgress Progress => _progress;
        public AtoPlatform CurrentPlatform => DetectPlatform();

        private AtoPlatform DetectPlatform()
        {
            switch (EditorUserBuildSettings.activeBuildTarget)
            {
                case BuildTarget.Android: return AtoPlatform.Android;
                case BuildTarget.iOS: return AtoPlatform.iOS;
                default: return AtoPlatform.PC;
            }
        }

        private void ReportFatal(string msg)
        {
            // Surface in NDMF error report. ErrorReport.AddError is internal, so we use the public
            // static ReportError helper which also logs and attaches reference context.
            // 输出到 NDMF 错误报告（AddError 为 internal，使用 public static ReportError）
            try { nadena.dev.ndmf.ErrorReport.ReportError(new AtoError(msg, ErrorSeverity.Error)); } catch { }
        }

        private void EmitReport()
        {
            long src = Atlases.Sum(a => a.SourceBytes), dst = Atlases.Sum(a => a.OutputBytes);
            double pct = src > 0 ? 100.0 * (src - dst) / src : 0;
            string saved = EditorUtility.FormatBytes(Math.Max(0, src - dst));
            AtoLog.Info(Localizer.T("report.summary",
                Report.RendererCount, Report.TextureCount, Report.IslandCount, Atlases.Count, Report.ElapsedMs));
            AtoLog.Info(Localizer.T("report.bytesSaved", saved, pct));
            if (Report.SkippedCount > 0)
                AtoLog.Warn(Localizer.T("report.skipped", Report.SkippedCount));

            if (Settings.VerboseLogging)
            {
                foreach (var a in Atlases)
                {
                    AtoLog.VIf(true,
                        $"  • {a.Name} {a.Width}x{a.Height} util={a.Utilization:P1} " +
                        $"islands={a.Placements.Count} {(a.FallbackStandalone ? "[standalone]" : "")} " +
                        $"{EditorUtility.FormatBytes(a.SourceBytes)} -> {EditorUtility.FormatBytes(a.OutputBytes)}");
                }
            }

            // Push a final informational entry into the NDMF error report console / 写入 NDMF 控制台
            try
            {
                var summary = Localizer.T("report.summary",
                    Report.RendererCount, Report.TextureCount, Report.IslandCount, Atlases.Count, Report.ElapsedMs);
                nadena.dev.ndmf.ErrorReport.ReportError(new AtoError(summary, ErrorSeverity.Information));
            }
            catch { }
        }

        private void CleanupRuntimeResources()
        {
            // Stages that allocated GPU buffers/RenderTextures register cleanup here.
            // 阶段分配的 GPU 缓冲/RenderTexture 在此统一释放。
            foreach (var a in ActionCleanups)
            {
                try { a(); } catch { }
            }
            ActionCleanups.Clear();
        }

        private readonly List<Action> ActionCleanups = new();
        public void RegisterCleanup(Action a) => ActionCleanups.Add(a);
    }

    internal sealed class AtoReport
    {
        public int RendererCount, TextureCount, IslandCount, SkippedCount;
        public long ElapsedMs;
    }

    /// <summary>Simple NDMF error/report entry. / 简单的 NDMF 报告条目。</summary>
    internal sealed class AtoError : nadena.dev.ndmf.IError
    {
        private readonly string _msg;
        private readonly System.Collections.Generic.List<nadena.dev.ndmf.ObjectReference> _refs = new();
        public AtoError(string msg, ErrorSeverity sev) { _msg = msg; Severity = sev; }
        public ErrorSeverity Severity { get; }
        public string ToMessage() => $"{AtoLog.Tag} {_msg}";
        public void AddReference(nadena.dev.ndmf.ObjectReference obj) => _refs.Add(obj);
        public UnityEngine.UIElements.VisualElement CreateVisualElement(nadena.dev.ndmf.ErrorReport report) => null;
    }
}
