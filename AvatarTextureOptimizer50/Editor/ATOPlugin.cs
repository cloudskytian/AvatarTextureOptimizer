// -----------------------------------------------------------------------------
// ATOPlugin.cs — NDMF plugin definition & the single orchestrating pass.
// ATOPlugin.cs —— NDMF 插件定义与唯一的编排 Pass。
//
// Ordering: Modular Avatar (after) → ATO → Avatar Optimizer (before), all inside
// BuildPhase.Optimizing, with NDMF AnimatorServices so animation edits reconcile.
// 顺序：Modular Avatar（之后）→ ATO → Avatar Optimizer（之前），全部位于
// BuildPhase.Optimizing，并启用 NDMF AnimatorServices 以自动写回动画修改。
// -----------------------------------------------------------------------------

using System;
using System.Linq;
using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;
using net.fosa.ato.editor;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

[assembly: ExportsPlugin(typeof(ATOPlugin))]

namespace net.fosa.ato.editor
{
    public class ATOPlugin : Plugin<ATOPlugin>
    {
        public override string QualifiedName => "net.fosa.avatar-texture-optimizer";
        public override string DisplayName => "Avatar Texture Optimizer";

        protected override void Configure()
        {
            InPhase(BuildPhase.Optimizing)
                .AfterPlugin("nadena.dev.modular-avatar")
                .WithRequiredExtension(typeof(AnimatorServicesContext), seq =>
                {
                    seq.Run(ATOOptimizePass.Instance)
                        .BeforePlugin("com.anatawa12.avatar-optimizer");
                });
        }
    }

    /// <summary>The whole pipeline in one pass (stage timing + cancellation + cleanup).
    /// 整条管线在单个 Pass 内（阶段计时 + 取消 + 清理）。</summary>
    public class ATOOptimizePass : Pass<ATOOptimizePass>
    {
        protected override void Execute(BuildContext context)
        {
            var component = context.AvatarRootObject
                .GetComponentInChildren<net.fosa.ato.AvatarTextureOptimizer>(true);

            // ---- validation / 校验 ----
            var all = context.AvatarRootObject
                .GetComponentsInChildren<net.fosa.ato.AvatarTextureOptimizer>(true);
            if (all.Length == 0) return; // no component → nothing to do / 无组件则不处理

            bool valid = all.Length == 1 && all[0].gameObject == context.AvatarRootObject;
#if ATO_VRCSDK_AVATARS
            valid = valid && context.AvatarRootObject.GetComponent<VRC.SDK3.Avatars.Components.VRCAvatarDescriptor>() != null;
#endif
            if (!valid)
            {
                ErrorReport.ReportError(ATOLocalization.NdmfLocalizer, ErrorSeverity.Error,
                    "Errors:InvalidMount", ObjectRegistry.GetReference(
                        all.Length > 0 ? all[0].gameObject : context.AvatarRootObject));
                throw new Exception(
                    "[ATO] Invalid ATO component placement — must be exactly one, on the avatar root " +
                    "with VRCAvatarDescriptor / ATO 组件必须恰好一个且挂在含 VRCAvatarDescriptor 的根上");
            }

            var st = context.GetState<ATOBuildState>();
            var progress = new ATOProgress();
            st.progress = progress;
            st.gpu = new ATOGpuPool();
            st.assetSaver = context.AssetSaver;
            st.settings = ResolveSettings(component);

            ATOLog.Level = component.logLevel;
            ATOLocalization.LanguageOverride = string.IsNullOrEmpty(component.language)
                ? "auto"
                : component.language;

            try
            {
                RunPipeline(context, st, component);
            }
            catch (ATOCancelledException)
            {
                st.report.PublishToNdmfConsole(progress.StageTimings, context.AvatarRootObject);
                ATOLog.Warn("bake cancelled by user — build aborted, resources released " +
                            "/ 用户取消——构建终止，资源已释放");
                throw;
            }
            finally
            {
                st.gpu?.Dispose();
                progress.Dispose();
                ATOQuality.ReleaseBuffers(st);
            }
        }

        private static void RunPipeline(BuildContext context, ATOBuildState st,
            net.fosa.ato.AvatarTextureOptimizer component)
        {
            ATOLog.Info($"=== ATO build start: {context.AvatarRootObject.name} " +
                        $"(platform {st.settings.platform}, atlas={st.settings.generateAtlas}) ===");

            // 1. Collect / 采集
            st.progress.BeginStage("Collect", 0f, 0.1f);
            ATOCollector.Run(context, st);

            // 2. Dedup / 去重
            st.progress.BeginStage("Dedup", 0.1f, 0.05f);
            ATOTexDedup.Run(st);

            // 3. Plan (islands → quality → pack) / 规划（岛→质量→装箱）
            st.progress.BeginStage("Plan", 0.15f, 0.45f);
            ATOPlanner.Plan(st);

            // 4. Atlas build / 图集合成
            if (st.atlases.Count > 0)
            {
                st.progress.BeginStage("AtlasBuild", 0.6f, 0.15f);
                ATOAtlasBuilder.BuildAll(st);
            }

            // 5. Mesh rebuild (AAO evacuation happens inside, after clone & before rewrite)
            // 网格重建（AAO 搬移在克隆后、改写前执行）
            st.progress.BeginStage("MeshRebuild", 0.75f, 0.1f);
            ATOMeshRebuild.Run(st);

            // 6. Material rebind / 材质重绑
            st.progress.BeginStage("MaterialRebind", 0.85f, 0.05f);
            ATOMaterialRebuild.Run(context, st);

            // 7. Slot merge & final dedup / 槽合并与最终去重
            st.progress.BeginStage("SlotMerge", 0.9f, 0.05f);
            ATOSlotMerge.Run(context, st);

            // 8. Finalize textures (Read/Write OFF per spec) & report
            //    最终化贴图（规格要求 Read/Write 关闭）与报告
            st.progress.BeginStage("Report", 0.95f, 0.05f);
            FinalizeTextures(st);
            ComputeStats(st);
            if (component.logReportToConsole)
                ATOLog.Info(st.report.BuildFullLog(st.progress.StageTimings));
            st.report.PublishToNdmfConsole(st.progress.StageTimings, context.AvatarRootObject);

            // 9. Remove self from the built avatar / 从成品移除自身
            if (component != null)
                Object.DestroyImmediate(component);

            ATOLog.Info($"=== ATO build done in {st.progress.TotalMs:F0} ms ===");
        }

        /// <summary>Force Read/Write disabled on every generated texture (spec). Run AFTER
        /// the dedup stage which needs pixel readback.
        /// 按规格对所有生成贴图强制关闭 Read/Write（在需要读回的去重阶段之后执行）。</summary>
        private static void FinalizeTextures(ATOBuildState st)
        {
            var set = new System.Collections.Generic.HashSet<Texture2D>();
            foreach (var a in st.atlases)
            {
                if (a.baseLayer != null && a.baseLayer.texture != null) set.Add(a.baseLayer.texture);
                foreach (var l in a.layers)
                    if (l.texture != null) set.Add(l.texture);
            }

            foreach (var t in st.textureToOptimized.Values)
                if (t != null) set.Add(t);
            foreach (var t in st.textures)
                if (t.wholeScaled != null) set.Add(t.wholeScaled);

            foreach (var tex in set)
            {
                try { tex.Apply(false, true); }
                catch (Exception e) { ATOLog.Debug($"makeNoLongerReadable failed on {tex.name}: {e.Message}"); }
            }
        }

        private static ATOSettings ResolveSettings(net.fosa.ato.AvatarTextureOptimizer c)
        {
            var platform = ATOPlatform.Detect();
            var ov = c.GetOverride(platform);
            var s = new ATOSettings
            {
                component = c,
                platform = platform,
                quality = c.quality,
                minDensity = c.minPixelDensity,
                maxDensity = c.maxPixelDensity,
                generateAtlas = c.generateAtlas,
                minPadding = c.minPadding,
                npotAtlases = c.npotAtlases,
                maxAtlasSize = c.EffectiveMaxAtlasSize(platform),
                formats = ov.enabled ? ov.formats : new net.fosa.ato.ATOFormatSet(),
                mips = c.mips,
                dedupMaterials = c.dedupMaterials,
                dedupTextures = c.dedupTextures,
            };
            return s;
        }

        private static void ComputeStats(ATOBuildState st)
        {
            st.report.atlases.AddRange(st.atlases);
            foreach (var t in st.textures)
            {
                if (t.whitelisted) continue;
                st.report.originalPixels += (long)t.Width * t.Height;
                if (t.wholeScaled != null)
                    st.report.optimizedPixels += (long)t.wholeScaled.width * t.wholeScaled.height;
            }

            // atlas pixels counted once per atlas layer / 图集按层计
            foreach (var a in st.atlases)
            {
                long px = (long)a.width * a.height;
                st.report.optimizedPixels += px;
                foreach (var l in a.layers)
                    st.report.optimizedPixels += (long)l.width * l.height;
            }

            // atlasified textures' original footprint counted, output lives in atlases
            // 图集化贴图的原始像素计入原值，产出在图集中
        }
    }
}
