using System;
using System.Diagnostics;
using nadena.dev.ndmf;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using Fosa.AvatarTextureOptimizer.Editor.Analysis;

namespace Fosa.AvatarTextureOptimizer.Editor.Pipeline
{
    // 烘焙主流程：验证 → 扫描材质槽 → 扫描动画 → 过滤槽位 → 收集贴图 → 白名单解析
    // → 岛提取 → UV/类型组 → 质量缩放 → 装箱 → 图集 → 应用 → 报告。
    // Main build pipeline: validate → scan slots → scan animations → filter → collect textures → whitelists
    // → islands → UV/type groups → quality scaling → packing → atlases → apply → report.
    internal sealed class ATOBuildProcess
    {
        public void Run(BuildContext ndmfCtx)
        {
            var root = ndmfCtx.AvatarRootObject;
            if (root == null)
            {
                ATOLog.Warn("AvatarRootObject 为空 / is null; skipping. 跳过本次处理。");
                return;
            }

            // 未挂载本组件 → 工具未启用，静默跳过（与其他 NDMF 工具行为一致）。
            // No ATOAvatar component → the tool is not enabled on this avatar; skip silently.
            var comps = root.GetComponentsInChildren<ATOAvatar>(true);
            if (comps == null || comps.Length == 0) return;

            // 规则校验：一个 Avatar 及其子级上只允许挂载一个组件。
            // Rule: at most one component on an avatar and its children.
            if (comps.Length > 1)
            {
                string msg = string.Format(ATOLocalization.Tr("error.multipleComponents"), comps.Length, root.name);
                ATOLog.Error(msg);
                ATOReport.Report(ndmfCtx.ErrorReport, ErrorSeverity.Error, "error.multipleComponents", comps.Length.ToString(), root.name);
                throw new ATOAbortException(msg);
            }

            var comp = comps[0];

            // 规则校验：挂载对象上必须存在 VRCAvatarDescriptor，否则报错中止烘焙或构建。
            // Rule: the hosting GameObject must have a VRCAvatarDescriptor, otherwise abort.
            var descriptor = comp.GetComponent<VRCAvatarDescriptor>();
            if (descriptor == null)
            {
                string msg = string.Format(ATOLocalization.Tr("error.componentNotOnDescriptor"), comp.gameObject.name, root.name);
                ATOLog.Error(msg);
                ATOReport.Report(ndmfCtx.ErrorReport, ErrorSeverity.Error, "error.componentNotOnDescriptor", comp.gameObject.name, root.name);
                throw new ATOAbortException(msg);
            }

            var ctx = new ATOContext(ndmfCtx, root, descriptor, comp);
            ATOLog.Verbose = ctx.settings.verboseLog;
            ATOLocalization.ApplySetting(ctx.settings.language);
            ATOCancellation.Reset();

            var sw = Stopwatch.StartNew();
            try
            {
                ATOLog.Info(string.Format("开始处理 Avatar / Processing avatar: {0} (v{1})", root.name, ATOConstants.Version));
                ATOLog.Info(string.Format("平台 / Platform: {0}，质量挡位 / Quality preset: {1}，生成图集 / Generate atlas: {2}",
                    ctx.platform, ctx.settings.qualityPreset, ctx.settings.generateAtlas));

                // 各阶段执行并更新进度条（支持取消）。Run stages with progress updates (cancellable).
                string title = "Avatar Texture Optimizer: " + root.name;
                ctx.Progress(title, ATOLocalization.Tr("stage.validate"), 0.02f);
                PipelineStages.Validate(ctx);
                ctx.Progress(title, ATOLocalization.Tr("stage.scanSlots"), 0.10f);
                PipelineStages.ScanMaterialSlots(ctx);
                ctx.Progress(title, ATOLocalization.Tr("stage.scanAnimations"), 0.25f);
                PipelineStages.ScanAnimations(ctx);
                ctx.Progress(title, ATOLocalization.Tr("stage.filterSlots"), 0.30f);
                PipelineStages.FilterSlots(ctx);
                ctx.Progress(title, ATOLocalization.Tr("stage.collectTextures"), 0.35f);
                PipelineStages.CollectTextures(ctx);
                ctx.Progress(title, ATOLocalization.Tr("stage.whitelist"), 0.40f);
                PipelineStages.ResolveWhitelists(ctx);
                ctx.Progress(title, ATOLocalization.Tr("stage.islands"), 0.45f);
                PipelineStages.ExtractIslands(ctx);
                ctx.Progress(title, ATOLocalization.Tr("stage.uvgroups"), 0.50f);
                PipelineStages.BuildUvGroups(ctx);
                ctx.Progress(title, ATOLocalization.Tr("stage.quality"), 0.60f);
                PipelineStages.ScaleIslands(ctx);
                ctx.Progress(title, ATOLocalization.Tr("stage.packing"), 0.70f);
                PipelineStages.PackAtlases(ctx);
                ctx.Progress(title, ATOLocalization.Tr("stage.atlases"), 0.80f);
                PipelineStages.BuildAtlases(ctx);
                ctx.Progress(title, ATOLocalization.Tr("stage.apply"), 0.95f);
                PipelineStages.ApplyChanges(ctx);

                sw.Stop();
                ctx.report.totalMs = sw.Elapsed.TotalMilliseconds;
                ctx.report.PrintToConsole();
                ATOLog.Info("全部阶段完成 / All stages done.");
            }
            catch (ATOCancelledException)
            {
                // 取消：终止烘焙；硬盘上的临时资产保留；CPU/GPU/内存资源随栈展开释放。
                // Cancelled: abort the build; temp assets on disk are kept; resources are released via stack unwinding.
                ATOLog.Warn(ATOLocalization.Tr("log.cancelled"));
                throw new OperationCanceledException("Avatar Texture Optimizer: build cancelled by user.");
            }
            catch (ATOAbortException)
            {
                ATOLog.Error("烘焙已中止 / Build aborted.");
                throw;
            }
            catch (Exception e)
            {
                ATOLog.Error("未处理异常 / Unhandled exception: " + e);
                throw;
            }
            finally
            {
                ATOCancellation.End();
            }
        }
    }
}
