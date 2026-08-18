using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using nadena.dev.ndmf;
using UnityEditor;
using UnityEngine;
using Net.Fosa.AvatarTextureOptimizer;
using Object = UnityEngine.Object;

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// Full bake session. / 完整烘焙会话。
    /// </summary>
    public sealed class AtoSession : IDisposable
    {
        public readonly BuildContext Context;
        public readonly AvatarTextureOptimizerComponent Component;
        public readonly AtoPlatformOverride Settings;
        public readonly AtoPlatform Platform;
        public readonly CancellationProbe Cancel;
        public readonly AtoReport Report = new AtoReport();
        public readonly AtoTextureCache Cache = new AtoTextureCache();

        public AtoSession(BuildContext ctx, AvatarTextureOptimizerComponent comp, AtoPlatformOverride settings, AtoPlatform platform)
        {
            Context = ctx;
            Component = comp;
            Settings = settings;
            Platform = platform;
            Cancel = new CancellationProbe();
        }

        public void Run()
        {
            var total = Stopwatch.StartNew();
            try
            {
                AtoApply.TextureRemap.Clear();
                AtoApply.MaterialRemap.Clear();
                Cancel.ThrowIfCancelled();
                Show(AtoI18n.T("progress.dedup"), 0.05f);
                var whitelist = AtoWhitelist.Collect(Context.AvatarRootObject, Component);
                var sw = AtoLog.Start("texture content dedup");
                AtoTextureDedup.Apply(Context.AvatarRootObject, whitelist, Cache);
                AtoLog.End(sw, "texture content dedup");

                Cancel.ThrowIfCancelled();
                Show(AtoI18n.T("progress.scan"), 0.15f);
                sw = AtoLog.Start("scan materials/animations");
                var graph = AtoAvatarScanner.Scan(Context.AvatarRootObject, Component, whitelist, Cache, Report);
                AtoLog.End(sw, "scan materials/animations");
                AtoExtensionPoints.RaiseAfterScan(graph);
                AtoLog.Info($"UV bindings={graph.Bindings.Count} eligible textures={graph.EligibleTextures.Count} whitelist textures={graph.WhitelistedTextures.Count}");

                Cancel.ThrowIfCancelled();
                Show(AtoI18n.T("progress.islands"), 0.35f);
                sw = AtoLog.Start("islands + quality scale");
                var islands = AtoIslandPipeline.Process(graph, Settings, Cache, Report, Cancel);
                AtoLog.End(sw, "islands + quality scale");
                Report.IslandsProcessed = islands.Count;
                AtoExtensionPoints.RaiseAfterIslands(islands);
                AtoLog.Info($"islands processed={islands.Count}");

                List<AtoAtlasResult> atlases = new List<AtoAtlasResult>();
                if (Settings.generateAtlas)
                {
                    Cancel.ThrowIfCancelled();
                    Show(AtoI18n.T("progress.atlas"), 0.65f);
                    sw = AtoLog.Start("atlas pack");
                    atlases = AtoAtlasPipeline.Pack(graph, islands, Settings, Platform, Cache, Report, Cancel);
                    AtoLog.End(sw, "atlas pack");
                    Report.AtlasesGenerated = atlases.Count;
                }
                else
                {
                    sw = AtoLog.Start("whole-texture scale (no atlas)");
                    AtoWholeTextureScaler.Apply(graph, islands, Settings, Cache, Report);
                    AtoLog.End(sw, "whole-texture scale (no atlas)");
                }

                Cancel.ThrowIfCancelled();
                Show(AtoI18n.T("progress.apply"), 0.85f);
                sw = AtoLog.Start("apply meshes/refs");
                AtoApply.Apply(Context, graph, islands, atlases, Settings, Cache, Report, Platform);
                AtoLog.End(sw, "apply meshes/refs");

                if (Settings.deduplicateTextures || Settings.deduplicateMaterials)
                {
                    sw = AtoLog.Start("post dedup");
                    AtoPostDedup.Apply(Context.AvatarRootObject, Settings, Report);
                    AtoLog.End(sw, "post dedup");
                }

                total.Stop();
                Report.TotalMs = total.ElapsedMilliseconds;
                Show(AtoI18n.T("progress.done"), 1f);
                Report.Emit();
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                Cache.ReleaseGpu();
            }
        }

        void Show(string phase, float p)
        {
            if (EditorUtility.DisplayCancelableProgressBar("Avatar Texture Optimizer", phase, p))
                Cancel.Cancel();
            AtoLog.Info($"phase: {phase} ({p:P0})");
        }

        public void Dispose()
        {
            Cache.Dispose();
            EditorUtility.ClearProgressBar();
        }
    }

    public sealed class CancellationProbe
    {
        public bool IsCancelled { get; private set; }
        public void Cancel() => IsCancelled = true;
        public void ThrowIfCancelled()
        {
            if (IsCancelled) throw new OperationCanceledException();
        }
    }

    public sealed class AtoReport
    {
        public long TotalMs;
        public int IslandsProcessed;
        public int AtlasesGenerated;
        public long OriginalTexels;
        public long ResultTexels;
        public readonly List<string> Details = new List<string>();
        public readonly List<string> Warnings = new List<string>();

        public void Detail(string s)
        {
            Details.Add(s);
            AtoLog.VerboseInfo(s);
        }

        public void Warn(string key, string arg)
        {
            var msg = string.Format(AtoI18n.T(key), arg);
            Warnings.Add(msg);
            AtoLog.Warn(msg);
            try
            {
                ErrorReport.ReportError(AtoError.Localizer, ErrorSeverity.NonFatal, key, arg);
            }
            catch { /* localizer optional */ }
        }

        public void Emit()
        {
            var saved = OriginalTexels > 0
                ? (1.0 - (double)ResultTexels / OriginalTexels) * 100.0
                : 0;
            AtoLog.Info($"==== {AtoI18n.T("report.title")} ====");
            AtoLog.Info(string.Format(AtoI18n.T("report.atlases"), AtlasesGenerated));
            AtoLog.Info(string.Format(AtoI18n.T("report.islands"), IslandsProcessed));
            AtoLog.Info(string.Format(AtoI18n.T("report.saved"), saved.ToString("0.0")));
            AtoLog.Info($"total {TotalMs} ms  originalTexels={OriginalTexels} resultTexels={ResultTexels}");
            foreach (var w in Warnings) AtoLog.Info($"warning: {w}");
            AtoLog.Info($"---- details ({Details.Count}) ----");
            foreach (var d in Details) AtoLog.VerboseInfo(d);

            try
            {
                ErrorReport.ReportError(AtoError.Localizer, ErrorSeverity.Information, "report.title");
                ErrorReport.ReportError(AtoError.Localizer, ErrorSeverity.Information, "report.atlases", AtlasesGenerated.ToString());
                ErrorReport.ReportError(AtoError.Localizer, ErrorSeverity.Information, "report.islands", IslandsProcessed.ToString());
                ErrorReport.ReportError(AtoError.Localizer, ErrorSeverity.Information, "report.saved", saved.ToString("0.0"));
            }
            catch { /* ok */ }
        }
    }
}
