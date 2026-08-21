// TextureOptimizerPipeline.cs - The orchestrator: scan -> graph -> quality -> pack -> render -> rewrite -> report.
// 管线编排：扫描 -> 建图 -> 质量 -> 装箱 -> 渲染 -> 改写 -> 报告。
// Memory & lifetime: GPU pools are disposed in finally; cancel keeps on-disk temp assets but frees
// CPU/GPU/memory immediately. / 显存与生命周期：GPU池在finally释放；取消时保留硬盘临时资产并立即释放资源。
using System;
using System.Collections.Generic;
using System.Linq;
using Fosa.ATO.Editor.Analysis;
using Fosa.ATO.Editor.Atlas;
using Fosa.ATO.Editor.Compat;
using Fosa.ATO.Editor.Core;
using Fosa.ATO.Editor.Quality;
using Fosa.ATO.Runtime;
using nadena.dev.ndmf;
using UnityEditor;
using UnityEngine;
using Fosa.ATO.Editor.Pipeline;

namespace Fosa.ATO.Editor
{
    /// <summary>Build report data. / 构建报告数据。</summary>
    public sealed class ATOReport
    {
        public int textureCountBefore, textureCountAfter;
        public int atlasCount, islandCount;
        public long bytesBefore, bytesAfter;
        public readonly List<string> atlasLines = new List<string>();
        public readonly List<string> detailLines = new List<string>();
        public double totalMs;
        public int skippedWhitelist, fallbackWhole;
    }

    public static class TextureOptimizerPipeline
    {
        public const string PluginQualifiedName = "net.fosa.avatar-texture-optimizer";

        /// <summary>Whole pipeline for one avatar. / 单个Avatar的完整管线。</summary>
        public static void Run(BuildContext ctx)
        {
            var comps = ctx.AvatarRootObject.GetComponentsInChildren<AvatarTextureOptimizer>(true);
            if (comps.Length == 0) return;
            // validation: exactly one, on the descriptor object / 校验：唯一且挂在描述符对象上
            if (comps.Length > 1)
            {
                ErrorReport.ReportError(Localization.ATOI18n.BuildNdmfLocalizer(), ErrorSeverity.Error,
                    "ato.err.multi_component", comps.Length);
                throw new InvalidOperationException("[ATO] multiple components / 存在多个组件");
            }
            var comp = comps[0];
#if ATO_VRCSDK3A
            if (comp.GetComponent<VRC.SDK3.Avatars.Components.VRCAvatarDescriptor>() == null)
            {
                ErrorReport.ReportError(Localization.ATOI18n.BuildNdmfLocalizer(), ErrorSeverity.Error, "ato.err.no_descriptor");
                throw new InvalidOperationException("[ATO] component not on VRCAvatarDescriptor object / 组件不在描述符对象上");
            }
#endif

            var platform = CurrentPlatform();
            var st = comp.Resolve(platform);
            ATOLog.Verbose = comp.verboseLog;
            ATOLog.Timings = comp.logTimings;
            ATOLog.ImportSettings = comp.logImportSettings;
            ATOLog.Reset();
            AtlasRegistry.Clear();
            var report = new ATOReport();

            GPUContext gpu = null;
            var progress = new ATOProgress("Avatar Texture Optimizer");
            try
            {
                var root = ctx.AvatarRootObject;
                report.bytesBefore = SumBytes(root);

                // ---- 1. scan / 扫描 ----
                progress.Report(0.02f, "Scanning avatar");
                var scan = AvatarScanner.Scan(ctx);

                gpu = new GPUContext();
                using var ops = new GPUTexOps(gpu);

                // ---- 2. usage graph / 使用图 ----
                progress.Report(0.08f, "Building usage graph");
                var graph = UsageGraphBuilder.Build(ctx, scan, comp, progress);
                ATOExtensions.Fire(ATOStage.GraphBuilt, graph);
                report.textureCountBefore = graph.textures.Count;
                report.skippedWhitelist = graph.textures.Count(t => t.whitelisted);

                // ---- 3. quality / 质量 ----
                progress.Report(0.25f, "Quality optimization");
                if (st.generateAtlas)
                {
                    QualityEvaluator.ProcessAll(graph, st, ops, progress);
                    ATOExtensions.Fire(ATOStage.QualityDone, graph);
                }
                else
                {
                    QualityEvaluator.ProcessWholeTextures(graph, st, ops, progress, _ => true);
                    ATOExtensions.Fire(ATOStage.QualityDone, graph);
                }

                PackResult pack = null;
                List<AtlasImage> images = new List<AtlasImage>();
                List<Texture2D> rescaled = new List<Texture2D>();

                if (st.generateAtlas)
                {
                    // ---- 4. packing / 装箱 ----
                    progress.Report(0.5f, "Packing atlases");
                    pack = AtlasPacker.Pack(graph, st, platform == ATOPlatform.Android || platform == ATOPlatform.iOS, progress);
                    ATOExtensions.Fire(ATOStage.Packed, pack);
                    foreach (var a in pack.atlases) AtlasRegistry.Register(a.id, a.width, a.height);

                    // groups that left atlas-ization -> whole-scale / 放弃图集化的组走整图缩放
                    var fallbackGroups = new HashSet<UvGroup>(pack.fallbackGroups);
                    foreach (var grp in graph.groups.Where(x => x.skipAtlas && !fallbackGroups.Contains(x))) fallbackGroups.Add(grp);
                    // whole-scale only entries with NO atlas coverage / 仅对完全没有图集覆盖的贴图做整图缩放
                    QualityEvaluator.ProcessWholeTextures(graph, st, ops, progress,
                        e => graph.Coverage(e).Any(c => c.skipAtlas || fallbackGroups.Contains(c) || !c.islands.Any(i => i.placed))
                            && !c_isAtlased(graph, e));

                    // ---- 5. render / 渲染 ----
                    progress.Report(0.7f, "Rendering atlases");
                    images = AtlasRenderer.Render(graph, pack, gpu, ops, st, platform, progress);
                    ATOExtensions.Fire(ATOStage.Rendered, images);
                    report.atlasCount = images.Count;
                    report.islandCount = pack.atlases.Sum(a => a.islands.Count);
                    foreach (var a in pack.atlases)
                        report.atlasLines.Add($"ATO #{a.id}: {a.width}x{a.height} islands={a.islands.Count} util={a.Utilization:P0} sources={string.Join(",", a.islands.Select(i => i.group.key.ToString()).Distinct().Take(6))}");
                }
                else
                {
                    // no atlas mode: everything whole-scale / 无图集模式：全部整图缩放
                    var outputs = new List<Texture2D>();
                    foreach (var e in graph.textures.Where(t => !t.whitelisted))
                    {
                        if (e.wholeScale >= 0.999f) continue;
                        outputs.Add(TextureWriter.FinalizeRescaled(ops, e, st, platform));
                    }
                    rescaled = outputs;
                }

                // ---- 6. rewrite / 改写 ----
                progress.Report(0.85f, "Rewriting avatar");
                var rr = MeshMaterialRewriter.Run(ctx, graph, pack, images, rescaled, st, progress);
                ATOExtensions.Fire(ATOStage.Rewritten, rr);

                // ---- 7. save / 保存 ----
                progress.Report(0.95f, "Saving assets");
                SaveAssets(ctx, images, rescaled, rr);

                report.bytesAfter = SumBytes(root);
                report.totalMs = ATOLog.TimingsSnapshot.Sum(t => t.ms);
                report.textureCountAfter = root.GetComponentsInChildren<Renderer>(true)
                    .SelectMany(r2 => r2.sharedMaterials).Where(m => m != null)
                    .SelectMany(m => m.shader != null ? EnumerateTextures(m) : Enumerable.Empty<Texture2D>()).Distinct().Count();
                foreach (var w in graph.warnings) ErrorReport.ReportError(Localization.ATOI18n.BuildNdmfLocalizer(), ErrorSeverity.Information, w.key, w.args);
                foreach (var w in TextureWriter.Warnings) ErrorReport.ReportError(Localization.ATOI18n.BuildNdmfLocalizer(), ErrorSeverity.Information, w.key, w.args);
                foreach (var t in ATOLog.TimingsSnapshot) report.detailLines.Add($"{t.step}: {t.ms:F1} ms");
                ATOExtensions.Fire(ATOStage.Finished, report);
                ReportGenerator.Output(report);
            }
            finally
            {
                progress?.Dispose();
                gpu?.Dispose();
                TextureWriter.Warnings.Clear();
                EditorUtility.ClearProgressBar();
            }

            // remove ourselves from the product / 从产物上移除自身
            foreach (var c in ctx.AvatarRootObject.GetComponentsInChildren<AvatarTextureOptimizer>(true))
                UnityEngine.Object.DestroyImmediate(c);
        }

        private static bool c_isAtlased(UsageGraph g, TexEntry e)
        {
            foreach (var c in g.Coverage(e)) if (!c.skipAtlas && c.islands.Any(i => i.placed)) return true;
            return false;
        }

        internal static ATOPlatform CurrentPlatform()
        {
            switch (EditorUserBuildSettings.activeBuildTarget)
            {
                case BuildTarget.Android: return ATOPlatform.Android;
                case BuildTarget.iOS: return ATOPlatform.iOS;
                default: return ATOPlatform.PC;
            }
        }

        private static IEnumerable<Texture2D> EnumerateTextures(Material m)
        {
            var sh = m.shader;
            int n = sh.GetPropertyCount();
            for (int i = 0; i < n; i++)
            {
                if (sh.GetPropertyType(i) == UnityEngine.Rendering.ShaderPropertyType.Texture)
                    if (m.GetTexture(sh.GetPropertyName(i)) is Texture2D t) yield return t;
            }
        }

        private static long SumBytes(GameObject root)
        {
            long sum = 0;
            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
                foreach (var m in r.sharedMaterials)
                {
                    if (m == null) continue;
                    foreach (var t in EnumerateTextures(m))
                        sum += EstimateBytes(t);
                }
            return sum;
        }

        internal static long EstimateBytes(Texture2D t)
        {
            try
            {
                int bpp = UnityEngine.Experimental.Rendering.GraphicsFormatUtility.GetBitsPerPixel(t.graphicsFormat);
                return (long)(t.width * t.height * bpp / 8) * (t.mipmapCount > 1 ? 4 / 3 : 1);
            }
            catch { return (long)t.width * t.height * 4; }
        }

        private static void SaveAssets(BuildContext ctx, List<AtlasImage> images, List<Texture2D> rescaled, RewriteResult rr)
        {
            using (ATOLog.Scope("SaveAssets"))
            {
                var list = new List<UnityEngine.Object>();
                foreach (var i in images) if (i.output != null) list.Add(i.output);
                list.AddRange(rescaled);
                foreach (var kv in rr.meshMap) list.Add(kv.Value);
                foreach (var kv in rr.materialMap) if (kv.Value != kv.Key) list.Add(kv.Value);
                ctx.AssetSaver.SaveAssets(list);
            }
        }
    }

    /// <summary>Report output to the ndmf console. / 输出报告到ndmf控制台。</summary>
    public static class ReportGenerator
    {
        public static void Output(ATOReport r)
        {
            ErrorReport.ReportError(Localization.ATOI18n.BuildNdmfLocalizer(), ErrorSeverity.Information, "ato.report.summary",
                r.textureCountBefore, r.textureCountAfter, r.atlasCount, r.islandCount,
                ATOLog.FormatBytes(r.bytesBefore), ATOLog.FormatBytes(r.bytesAfter),
                100f * (1f - (float)r.bytesAfter / Mathf.Max(1f, r.bytesBefore)));
            // details folded into console log / 细节折叠进控制台日志
            LogBlock.Dump($"report / 报告  total={r.totalMs:F0}ms",
                r.atlasLines.Concat(r.detailLines).Concat(new[] { $"whitelist skipped / 白名单跳过: {r.skippedWhitelist}" }));
        }
    }
}
