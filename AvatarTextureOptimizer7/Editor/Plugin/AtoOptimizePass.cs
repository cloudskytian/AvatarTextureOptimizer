using System;
using System.Collections.Generic;
using System.Diagnostics;
using Fosa.AvatarTextureOptimizer;
using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// Main bake pass. After MA, before AAO. Cancelling releases CPU/GPU/memory but keeps disk temps.
    /// 主烘焙通道。MA 之后、AAO 之前。取消时释放 CPU/GPU/内存，保留硬盘临时资产。
    /// </summary>
    public sealed class AtoOptimizePass : Pass<AtoOptimizePass>
    {
        public override string DisplayName => "ATO Optimize Textures";

        protected override void Execute(BuildContext context)
        {
            var root = context.AvatarRootObject;
            if (root == null) return;
            var component = root.GetComponentInChildren<AvatarTextureOptimizer>(true);
            if (component == null) return;

            var session = new AtoSession
            {
                Context = context,
                Component = component,
                Language = component.language,
                Animators = context.Extension<AnimatorServicesContext>()
            };
            session.Log.Verbose = component.verboseLogging;
            session.GenerateAtlas = component.generateAtlas;
            session.Lossless = component.IsLosslessPreset;
            session.MinPadding = (int)component.minPadding;
            session.MinPxPerMeter = (int)component.minPixelDensity;
            session.MaxPxPerMeter = (int)component.maxPixelDensity;
            session.Platform = AtoPlatformUtil.Resolve(component.platform);
            session.PlatformSettings = component.ResolvePlatformSettings(session.Platform);
            session.Npot = component.experimentalNpot ||
                           (session.PlatformSettings != null && session.PlatformSettings.experimentalNpot);
            session.MaxAtlas = AtoPlatformUtil.MaxAtlasEdge(session.Platform);
            session.Quality = component.quality != null ? component.quality.Clone() : AvatarTextureOptimizer.GetBuiltinPreset(component.qualityPreset);
            if (session.Npot)
            {
                session.Log.Info("NPOT atlases enabled; PVRTC will be rejected.");
            }

            var sw = Stopwatch.StartNew();
            try
            {
                Run(session);
            }
            catch (AtoCancelledException)
            {
                session.Log.Warn("Cancelled by user. Disk temps kept, runtime resources released.");
                ErrorReport.ReportError(AtoLoc.NdmfLocalizer, ErrorSeverity.Information, "error.cancelled");
            }
            catch (Exception e)
            {
                session.Log.Error(e.ToString());
                ErrorReport.ReportError(AtoLoc.NdmfLocalizer, ErrorSeverity.Error, "error.fatal", e.Message);
                throw;
            }
            finally
            {
                sw.Stop();
                session.Report.Seconds = sw.Elapsed.TotalSeconds;
                EmitReport(session);
                DestroySelf(session);
                session.Dispose();
            }
        }

        static void Run(AtoSession session)
        {
            AtoGraph graph = null;
            try
            {
            session.Log.Info("Start avatar=" + session.Context.AvatarRootObject.name +
                             " platform=" + session.Platform +
                             " atlas=" + session.GenerateAtlas +
                             " preset=" + session.Component.qualityPreset +
                             " lossless=" + session.Lossless +
                             " AAO=" + AaoBridge.Available);

            session.SetProgress("progress.validate", 0.02f);
            WhitelistResolver.Collect(session);

            session.SetProgress("progress.anim", 0.08f);
            AnimationCollector anim;
            using (session.Log.Stage("animation"))
            {
                anim = AnimationCollector.Collect(session.Animators, session.Context.AvatarRootTransform, session.Log);
            }

            session.SetProgress("progress.collect", 0.12f);
            List<AtoRendererInfo> renderers;
            using (session.Log.Stage("renderers"))
            {
                // Bind animation first so enable-curves are visible. / 先绑动画才能看到启用曲线。
                var preview = session.Context.AvatarRootObject.GetComponentsInChildren<Renderer>(true);
                anim.BindRenderers(preview, session.Log);
                renderers = RendererCollector.Collect(session.Context.AvatarRootObject, anim, session.Log);
            }

            session.SetProgress("progress.analyze", 0.20f);
            using (session.Log.Stage("graph"))
            {
                graph = GraphBuilder.Build(session, renderers, anim);
            }

            ImporterUtil.CountSourcePixels(session, graph);

            session.SetProgress("progress.quality", 0.35f);
            using (session.Log.Stage("quality-scale"))
            {
                QualityEvaluator.ScaleGraph(session, graph);
            }

            session.SetProgress("progress.atlas", 0.60f);
            AtlasPlan plan;
            using (session.Log.Stage("atlas"))
            {
                plan = AtlasGenerator.Build(session, graph);
            }

            using (session.Log.Stage("whole-texture-fallback"))
            {
                WholeTextureScaler.ScaleNonAtlas(session, graph, plan);
            }

            session.SetProgress("progress.apply", 0.80f);
            using (session.Log.Stage("apply-mesh"))
            {
                MeshUvRewriter.Apply(session, graph, plan);
            }

            using (session.Log.Stage("apply-material"))
            {
                MaterialAssigner.Apply(session, graph, plan);
            }

            using (session.Log.Stage("apply-animation"))
            {
                AnimationPatcher.Apply(session);
            }

            session.SetProgress("progress.post", 0.90f);
            using (session.Log.Stage("post-dedup"))
            {
                PostDedup.Run(session, graph, anim);
                AnimationPatcher.Apply(session);
            }

            using (session.Log.Stage("compress"))
            {
                var atlases = new List<Texture2D>();
                foreach (var a in plan.Atlases)
                {
                    if (a.Texture == null) continue;
                    atlases.Add(a.Texture);
                    ImporterUtil.ApplyGenerated(session, new[] { a.Texture }, a.Kind, a.HasAlpha, true);
                }

                var scaled = new List<Texture2D>();
                foreach (var kv in session.TextureRemap)
                {
                    if (kv.Value is Texture2D t && t != null && !atlases.Contains(t))
                        scaled.Add(t);
                }

                if (scaled.Count > 0)
                    ImporterUtil.ApplyGenerated(session, scaled, AtoTextureKind.Albedo, true, false);

                session.Report.OutputTextures = atlases.Count + scaled.Count;
                if (session.Report.OutputTextures == 0)
                    session.Report.OutputTextures = session.Report.SourceTextures;
                if (session.Report.OutputPixels == 0)
                    session.Report.OutputPixels = session.Report.SourcePixels;
            }

            session.SetProgress("progress.post", 1f);
            session.Log.Info(string.Format(
                "Done. textures {0}->{1} atlases={2} islands={3} saved≈{4:0.0}% time={5:0.00}s",
                session.Report.SourceTextures, session.Report.OutputTextures, session.Report.AtlasCount,
                session.Report.IslandCount, session.Report.SavedPercent, session.Log.Elapsed.TotalSeconds));
            }
            finally
            {
                graph?.DisposeNative();
            }
        }

        static void EmitReport(AtoSession session)
        {
            if (session?.Report == null) return;
            var summary = AtoLoc.T(session.Language, "report.summary",
                session.Report.SourceTextures,
                session.Report.OutputTextures,
                session.Report.AtlasCount,
                session.Report.IslandCount,
                session.Report.SavedPercent,
                session.Report.Seconds);
            var details = session.Log.GetDetailDump();
            if (session.Report.AtlasLines.Count > 0)
            {
                details += "\n--- atlases ---\n" + string.Join("\n", session.Report.AtlasLines.ToArray());
            }

            ErrorReport.ReportError(new AtoReportError(AtoLoc.T(session.Language, "report.title"), summary, details));
        }

        static void DestroySelf(AtoSession session)
        {
            try
            {
                var root = session.Context.AvatarRootObject;
                foreach (var c in root.GetComponentsInChildren<AvatarTextureOptimizer>(true))
                {
                    if (c != null) Object.DestroyImmediate(c);
                }
            }
            catch (Exception e)
            {
                session.Log.Warn("Failed to remove ATO component: " + e.Message);
            }
        }
    }
}
