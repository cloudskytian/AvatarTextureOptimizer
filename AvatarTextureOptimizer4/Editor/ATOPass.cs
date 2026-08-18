// Avatar Texture Optimizer (ATO)
// NDMF pass entry: validation + orchestration.
// NDMF pass 入口：组件校验 + 编排。

using System;
using UnityEditor;
using UnityEngine;
using nadena.dev.ndmf;

namespace NetFosa.ATO
{
    /// <summary>
    /// The single NDMF pass executed for ATO. / ATO 唯一执行的 NDMF pass。
    /// </summary>
    public static class ATOPass
    {
        public static void Execute(BuildContext ctx)
        {
            // 1. Find & validate the component. / 查找并校验组件。
            var comps = ctx.AvatarRootObject.GetComponentsInChildren<ATOAvatarOptimizer>(true);
            if (comps == null || comps.Length == 0)
            {
                // Tool not enabled on this avatar. / 该 Avatar 未启用本工具。
                return;
            }

            if (comps.Length > 1)
            {
                var err = new ATOInlineError(ErrorSeverity.Error, "error.multiple.components");
                foreach (var c in comps) err.AddReference(ObjectRegistry.GetReference(c.gameObject));
                ErrorReport.ReportError(err);
                throw new InvalidOperationException(
                    "[ATO] More than one ATOAvatarOptimizer found on the avatar; exactly one is allowed. / Avatar 上存在多个 ATOAvatarOptimizer，只允许一个。");
            }

            var comp = comps[0];

#if ATO_VRCSDK3_AVATARS
            if (comp.GetComponent<VRC.SDK3.Avatars.Components.VRCAvatarDescriptor>() == null)
            {
                var err = new ATOInlineError(ErrorSeverity.Error, "error.missing.vrcdescriptor");
                err.AddReference(ObjectRegistry.GetReference(comp.gameObject));
                ErrorReport.ReportError(err);
                throw new InvalidOperationException(
                    "[ATO] ATOAvatarOptimizer must be on the object with a VRCAvatarDescriptor. / ATOAvatarOptimizer 必须挂载在带 VRCAvatarDescriptor 的对象上。");
            }
#endif

            // 2. Setup logging from advanced settings. / 依据高级设置配置日志。
            ATOLogger.Configure(comp.advanced.debugLogging, comp.advanced.verboseLogging);
            ATOI18n.Initialize();
            ATOI18n.SetLanguage(comp.advanced.languageMode);

            // 3. Resolve the active build platform. / 解析当前构建平台。
            var platform = ATOUtil.GetActivePlatform();

            // 4. Build & run the pipeline. / 构建并运行管线。
            var build = new ATOBuildContext(ctx, comp, platform);
            build.EnsureWhitelistResolved();

            try
            {
                ATOPipeline.Run(build);
            }
            catch (OperationCanceledException)
            {
                ATOLogger.Info("Build cancelled by user; temporary assets kept on disk. / 用户取消构建，临时资产保留在硬盘。");
            }
            finally
            {
                build.progress?.Dispose();
                // Release GPU temporaries and readable-copy caches (no leaks). / 释放 GPU 临时资源与可读副本缓存（无泄漏）。
                ATOTextureSampler.ClearCache();
                ATOGpu.ReleaseAll();
            }
        }
    }
}
