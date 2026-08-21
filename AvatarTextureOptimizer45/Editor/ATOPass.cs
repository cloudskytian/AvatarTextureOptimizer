using System;
using System.Linq;
using nadena.dev.ndmf;
using UnityEditor;
using UnityEngine;
using UnityEngine.Profiling;
using VRC.SDK3.Avatars.Components;

namespace net.fosa.ato
{
    /// <summary>
    /// ATO 主 Pass / The main ATO pass.
    ///
    /// 阶段: 组件校验 -> 动画分析 -> 收集 -> UV岛 -> 质量缩放 -> 图集装箱 -> 应用 -> 报告.
    /// Stages: validation -> animation analysis -> collect -> islands -> quality scaling -> packing -> apply -> report.
    ///
    /// 显示当前阶段与进度并支持取消; 取消时终止构建、释放CPU/GPU/内存资源、保留硬盘临时资产.
    /// Shows the current stage with progress and supports cancellation; cancelling aborts the build,
    /// releases CPU/GPU/memory resources and keeps temporary assets on disk.
    ///
    /// 注意: 暂不支持 NDMF 预览 / Note: NDMF preview is not supported yet.
    /// </summary>
    internal sealed class ATOPass : Pass<ATOPass>
    {
        public ATOPass() { }

        public override string DisplayName => "Avatar Texture Optimizer";

        private int _progressId;

        protected override void Execute(BuildContext context)
        {
            var avatar = context.AvatarRootObject;

            // ---------------------------------------------------------------
            // 组件校验 / component validation
            // ---------------------------------------------------------------
            var comps = avatar.GetComponentsInChildren<AvatarTextureOptimizer>(true);
            if (comps.Length == 0)
            {
                ATOLog.InfoVerbose("Avatar 上未挂载 ATO 组件, 跳过 / no ATO component on the avatar; skipping");
                return;
            }

            if (comps.Length > 1)
            {
                // 整个Avatar只允许挂载一个 / only one instance is allowed
                ATOReport.Error("err.tooManyComponents", "err.tooManyComponents:description", null,
                    comps.Select(c => c.name).ToArray());
                ATOLog.Error($"Avatar 上挂载了 {comps.Length} 个 ATO 组件, 只允许一个; 中止处理 / found {comps.Length} ATO components; only one is allowed. Aborting.");
                return;
            }

            var comp = comps[0];
            if (comp.GetComponent<VRCAvatarDescriptor>() == null)
            {
                ATOReport.Error("err.noDescriptor", "err.noDescriptor:description", "err.noDescriptor:hint",
                    comp.gameObject.name);
                ATOLog.Error($"ATO 组件挂载对象上必须存在 VRCAvatarDescriptor ({comp.gameObject.name}); 中止处理 / the host object must have a VRCAvatarDescriptor. Aborting.");
                return;
            }

            // ---------------------------------------------------------------
            // 配置解析 / configuration
            // ---------------------------------------------------------------
            var platform = EditorUserBuildSettings.activeBuildTarget;
            var cfg = ATOConfig.Resolve(comp, platform);
            ATOLog.Verbose = cfg.debugLogging;
            ATOI18n.Resolve(comp.language);

            ATOLog.Info($"开始处理 Avatar: {avatar.name} (平台 {platform}, 质量挡位 {cfg.qualityPreset}, 图集 {(cfg.enableAtlas ? "开" : "关")})");

            var state = context.GetState(() => new ATOBuildState());
            state.config = cfg;
            state.component = comp;
            state.hasAAO = ATOAAOCompat.Available;

            // ---------------------------------------------------------------
            // 进度与取消 / progress & cancellation
            // ---------------------------------------------------------------
            _progressId = Progress.Start("ATO Avatar Texture Optimizer", avatar.name,
                Progress.Options.Sticky | Progress.Options.Indefinite);
            string[] stages = { "Animation", "Collect", "UV Islands", "Quality Scale", "Atlas Pack", "Apply" };
            int stageIdx = 0;

            void NextStage(string name)
            {
                CheckCancelled();
                Progress.Report(_progressId, stageIdx / (float)stages.Length, name);
                Progress.SetDescription(_progressId, name);
                stageIdx++;
            }

            bool succeeded = false;
            try
            {
                // 扩展预处理 / extension pre-processing
                foreach (var p in ATOExtensionRegistry.GetPreProcessors())
                {
                    try { p.OnPreProcess(state.pipelineContext); }
                    catch (Exception e) { ATOLog.Warn($"扩展预处理失败 / pre-processor failed: {e.Message}"); }
                }

                NextStage("ATO: Animation");
                state.anim = ATOAnimationAnalysis.Analyze(avatar, comp.GetComponent<VRCAvatarDescriptor>());
                ATOLog.InfoVerbose($"收集到 {state.anim.clips.Count} 个 AnimationClip / collected {state.anim.clips.Count} animation clips");

                NextStage("ATO: Collect");
                ATOCollect.Run(state, avatar);
                ATOLog.Info($"收集到 {state.meshes.Count} 个网格, {state.textures.Count} 张贴图 / collected {state.meshes.Count} meshes, {state.textures.Count} textures");

                NextStage("ATO: UV Islands");
                ATOIslands.Run(state, avatar);

                NextStage("ATO: Quality Scale");
                ATOScaler.Run(state);

                NextStage("ATO: Atlas Pack");
                if (cfg.enableAtlas)
                {
                    ATOPacker.Run(state, context);
                }

                NextStage("ATO: Apply");
                ATOApply.Run(state, context, avatar);

                // 扩展后处理 / extension post-processing
                state.pipelineContext.AvatarRoot = avatar;
                state.pipelineContext.Settings = comp;
                state.pipelineContext.Textures = state.textures;
                state.pipelineContext.Atlases = state.textures
                    .Where(t => t.group != null)
                    .SelectMany(t => t.group.atlases)
                    .Distinct()
                    .ToList();
                foreach (var p in ATOExtensionRegistry.GetPostProcessors())
                {
                    try { p.OnPostProcess(state.pipelineContext); }
                    catch (Exception e) { ATOLog.Warn($"扩展后处理失败 / post-processor failed: {e.Message}"); }
                }

                // 移除成品上的自身组件 / remove the ATO component from the final avatar
                if (state.component != null)
                {
                    UnityEngine.Object.DestroyImmediate(state.component);
                    state.component = null;
                }

                // 最终报告 / final report
                ATOReportFinal.Run(state, context, avatar);
                Progress.Report(_progressId, 1f, "ATO: Done");
                succeeded = true;
            }
            catch (OperationCanceledException)
            {
                // 取消: 释放资源并终止构建 / cancelled: release resources and abort the build
                ATOTextureIO.ReleaseAll(state);
                throw;
            }
            finally
            {
                ATOTextureIO.ReleaseAll(state);
                Progress.Finish(_progressId, succeeded ? Progress.Status.Succeeded : Progress.Status.Failed);
                ATOLog.Verbose = false;
            }
        }

        private void CheckCancelled()
        {
            var item = Progress.GetProgress(_progressId);
            if (item.cancelled)
            {
                throw new OperationCanceledException("ATO build cancelled by user");
            }
        }
    }

    /// <summary>
    /// 最终报告 / Final build report (NDMF console + [ATO] log summary).
    /// 默认展示总体结果, 具体细节折叠(以折叠日志形式输出) / shows the overall result by default,
    /// details are logged in a collapsed form.
    /// </summary>
    internal static class ATOReportFinal
    {
        public static void Run(ATOBuildState state, BuildContext context, GameObject avatar)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            long srcPx = 0;
            foreach (var t in state.textures)
            {
                if (t.dedupOf != null) continue;
                srcPx += (long)t.width * t.height;
            }

            // 输出像素来自唯一的输出贴图列表(图集与独立贴图各计一次) / output pixels come from the unique output list
            long outPx = 0;
            foreach (var o in state.outputTextures)
            {
                if (o.result != null) outPx += (long)o.result.width * o.result.height;
            }

            // 图集统计 / atlas stats
            var atlasLines = new System.Text.StringBuilder();
            var seenAtlas = new System.Collections.Generic.HashSet<ATOAtlas>();
            foreach (var t in state.textures)
            {
                if (t.group == null) continue;
                foreach (var atlas in t.group.atlases)
                {
                    if (!seenAtlas.Add(atlas)) continue;
                    var sources = string.Join(", ", atlas.placements
                        .Select(p => p.island.perTexture.Keys.FirstOrDefault(tt => tt.group == atlas.group)?.source.name)
                        .Where(n => n != null)
                        .Distinct()
                        .Take(8));
                    atlasLines.AppendLine($"[ATO]   图集 {atlas.name}: {atlas.width}x{atlas.height}, 岛数 {atlas.placements.Count}, 利用率 {atlas.utilization:P1}, 来源: {sources}");
                }
            }

            double savings = srcPx > 0 ? (1.0 - outPx / (double)Mathf.Max(1, srcPx)) * 100.0 : 0.0;
            sw.Stop();

            string summary = ATOI18n.T("report.summary", state.islandCount, state.atlasCount,
                srcPx / 1000000.0, outPx / 1000000.0, savings, sw.ElapsedMilliseconds,
                state.skippedFull, state.skippedAtlasOnly);

            ATOLog.Info($"================ ATO 报告 Report ================");
            ATOLog.Info(summary);
            if (ATOLog.Verbose)
            {
                ATOLog.Info(atlasLines.ToString());
            }

            ATOReport.Info("report.title", "report.title:description", null, avatar.name);
            ATOReport.Info("report.summary", null, null, state.islandCount, state.atlasCount,
                srcPx / 1000000.0, outPx / 1000000.0, savings, sw.ElapsedMilliseconds,
                state.skippedFull, state.skippedAtlasOnly);
        }
    }
}
