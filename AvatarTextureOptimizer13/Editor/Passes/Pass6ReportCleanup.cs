// ATO — Avatar Texture Optimizer
// Pass 6 — report & cleanup: runs third-party post processors, removes the ATO component
// from the baked avatar, and emits the final report to the NDMF console (summary shown,
// details collapsed).
// Pass 6——报告与清理：运行第三方后处理器，从烘焙后的 Avatar 移除 ATO 组件，
// 并把最终报告输出到 NDMF 控制台（总体展示、细节折叠）。

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using nadena.dev.ndmf;
using net.fosa.ato;

namespace net.fosa.ato.editor
{
    /// <summary>
    /// Pass 6 — report & cleanup. Pass 6——报告与清理。
    /// </summary>
    public class Pass6ReportCleanup : ATOBasePass<Pass6ReportCleanup>
    {
        protected override void Process(ATOBuildContext bc, BuildContext context)
        {
            var result = bc.Result;
            if (result == null) return;

            // 1. Third-party post processors. 1. 第三方后处理器。
            RunPostProcessors(bc, result);

            // 2. Remove the component from the baked avatar. 2. 从烘焙后的 Avatar 移除组件。
            if (result.component != null)
            {
                UnityEngine.Object.DestroyImmediate(result.component);
            }

            // 3. Final report to the NDMF console. 3. 最终报告输出到 NDMF 控制台。
            ComputeByteStats(result);
            EmitReport(bc, result);

            bc.ClearCaches();
            ATOLog.Info("ATO processing finished.");
        }

        private static void RunPostProcessors(ATOBuildContext bc, ATOAnalysisResult result)
        {
            var types = TypeCache.GetTypesDerivedFrom<IATOPostProcessor>();
            var impls = new List<(int, IATOPostProcessor)>();
            foreach (var t in types)
            {
                if (t.IsAbstract || t.IsInterface) continue;
                try
                {
                    var instance = (IATOPostProcessor)Activator.CreateInstance(t);
                    int order = t.GetCustomAttribute<ATOExtensionOrderAttribute>()?.Order ?? 0;
                    impls.Add((order, instance));
                }
                catch (Exception e)
                {
                    ATOLog.Warn($"Failed to instantiate post processor {t.Name}: {e.Message}");
                }
            }
            impls.Sort((a, b) => a.Item1.CompareTo(b.Item1));

            var ctx = new ATOPostProcessContext
            {
                avatarRoot = result.component != null ? result.component.gameObject : null,
                settings = result.settings,
            };
            foreach (var atlas in result.atlases)
            {
                if (atlas.texture == null) continue;
                ctx.generatedTextures.Add(new ATOGeneratedTexture
                {
                    texture = atlas.texture,
                    name = atlas.name,
                    isAtlas = true,
                    sources = atlas.sources,
                    islandCount = atlas.packed.Count,
                    utilization = atlas.utilization,
                });
            }
            foreach (var kv in bc.Report.StageTimings) ctx.stageTimings[kv.Key] = kv.Value;

            foreach (var (_, impl) in impls)
            {
                try { impl.PostProcess(ctx); }
                catch (Exception e) { ATOLog.Warn($"[PostProcessor] {impl.DisplayName} threw: {e.Message}"); }
            }
        }

        /// <summary>Estimate texture memory before/after for the report. 估算报告用的优化前后贴图内存。</summary>
        private static void ComputeByteStats(ATOAnalysisResult result)
        {
            long before = 0, after = 0;
            var atlasTextures = new HashSet<Texture2D>();
            foreach (var atlas in result.atlases)
            {
                after += (long)atlas.size * atlas.size * 4;
                if (atlas.texture != null) atlasTextures.Add(atlas.texture);
            }
            var counted = new HashSet<Texture2D>();
            foreach (var tr in result.textures)
            {
                if (tr.whitelisted || tr.texture == null) continue;
                before += ATOTextureIO.EstimateBytes(tr.texture);
                Texture2D rep = null;
                foreach (var u in tr.usages)
                    if (u.replacement != null) { rep = u.replacement; break; }
                if (rep == null) after += ATOTextureIO.EstimateBytes(tr.texture);
                else if (!atlasTextures.Contains(rep) && counted.Add(rep)) after += ATOTextureIO.EstimateBytes(rep);
            }
            result.bytesBefore = before;
            result.bytesAfter = after;
        }

        private static void EmitReport(ATOBuildContext bc, ATOAnalysisResult result)
        {
            var report = bc.Report;
            report.TexturesProcessed = result.textures.Count;
            report.AtlasesGenerated = result.atlases.Count;
            report.IslandsProcessed = CountIslands(result);
            report.EstimatedBytesBefore = result.bytesBefore;
            report.EstimatedBytesAfter = result.bytesAfter;

            long saved = result.bytesBefore - result.bytesAfter;
            var summary = new List<string>
            {
                $"{ATOI18n.T(ATOI18nKeys.ReportTexturesProcessed)}: {report.TexturesProcessed}",
                $"{ATOI18n.T(ATOI18nKeys.ReportAtlasesGenerated)}: {report.AtlasesGenerated}",
                $"{ATOI18n.T(ATOI18nKeys.ReportIslands)}: {report.IslandsProcessed}",
                $"{ATOI18n.T(ATOI18nKeys.ReportSavedTotal)}: {FormatBytes(saved)}",
                $"{ATOI18n.T(ATOI18nKeys.ReportElapsed)}: {report.TotalMs:F0} ms",
            };

            var details = new List<string>();
            foreach (var kv in report.StageTimings)
                details.Add($"{kv.Key}: {kv.Value:F1} ms");
            foreach (var atlas in result.atlases)
            {
                details.Add($"  {atlas.name} [{atlas.kind}] {atlas.size}x{atlas.size} " +
                            $"util={atlas.utilization:P0} islands={atlas.packed.Count} " +
                            $"sources={string.Join(", ", atlas.sources.Select(s => s != null ? s.name : "?"))}");
            }
            details.AddRange(report.DetailLines);

            ErrorReport.ReportError(new ATOReportError(summary, details, report.WarningLines));

            // Also dump to the Unity console for easy filtering by [ATO]. 同时输出到 Unity 控制台方便按 [ATO] 过滤。
            ATOLog.Info("=== ATO Report ===");
            foreach (var s in summary) ATOLog.Info("  " + s);
            foreach (var d in details) ATOLog.Verbose("  " + d);
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes < 0) return $"-{FormatBytes(-bytes)}";
            if (bytes >= 1024 * 1024) return $"{bytes / (1024f * 1024f):F1} MB";
            if (bytes >= 1024) return $"{bytes / 1024f:F1} KB";
            return $"{bytes} B";
        }

        private static int CountIslands(ATOAnalysisResult result)
        {
            int count = 0;
            foreach (var g in result.uvGroups) count += g.islands.Count;
            return count;
        }
    }

    /// <summary>
    /// A report error shown in the NDMF console: summary always visible, details collapsed.
    /// 在 NDMF 控制台显示的报告：总体始终可见，细节折叠。
    /// </summary>
    public class ATOReportError : IError
    {
        private readonly List<string> _summary;
        private readonly List<string> _details;
        private readonly List<string> _warnings;

        public ATOReportError(List<string> summary, List<string> details, List<string> warnings)
        {
            _summary = summary;
            _details = details;
            _warnings = warnings;
        }

        public ErrorSeverity Severity => ErrorSeverity.Information;

        public VisualElement CreateVisualElement(ErrorReport report)
        {
            var root = new VisualElement();
            var header = new Label($"[ATO] {ATOI18n.T(ATOI18nKeys.ReportTitle)}");
            header.style.unityFontStyleAndWeight = FontStyle.Bold;
            root.Add(header);

            var summaryBox = new VisualElement();
            summaryBox.style.marginLeft = 8;
            foreach (var s in _summary)
                summaryBox.Add(new Label("• " + s));
            root.Add(summaryBox);

            if (_warnings.Count > 0)
            {
                var warnFoldout = new Foldout { text = $"{ATOI18n.T(ATOI18nKeys.ReportDetails)} (warnings {_warnings.Count})" };
                foreach (var w in _warnings) warnFoldout.Add(new Label("! " + w));
                root.Add(warnFoldout);
            }

            if (_details.Count > 0)
            {
                var detailsFoldout = new Foldout { text = ATOI18n.T(ATOI18nKeys.ReportDetails) };
                foreach (var d in _details) detailsFoldout.Add(new Label("  " + d));
                root.Add(detailsFoldout);
            }
            return root;
        }

        public string ToMessage()
        {
            return $"[ATO] {string.Join("; ", _summary)}";
        }

        public void AddReference(ObjectReference obj)
        {
            // Informational report; references not required. 信息性报告；无需引用。
        }
    }
}
