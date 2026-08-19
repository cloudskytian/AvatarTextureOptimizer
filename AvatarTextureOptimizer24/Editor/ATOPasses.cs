// ============================================================================
// ATOPasses.cs — NDMF Pass 定义 / NDMF Pass definitions
// (EN) ValidatePass validates component placement; OptimizePass runs the
//      full optimization pipeline.
// (ZH) ValidatePass 校验组件挂载；OptimizePass 运行完整优化管线。
// ============================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf;
using nadena.dev.ndmf.localization;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

namespace Fosa.AvatarTextureOptimizer
{
    /// <summary>(EN) Validates the AvatarTextureOptimizer component placement. (ZH) 校验组件挂载是否合规。</summary>
    public class ValidatePass : Pass<ValidatePass>
    {
        public override string DisplayName => "ATO: Validate";

        protected override void Execute(BuildContext context)
        {
            var root = context.AvatarRootObject;
            var components = root.GetComponentsInChildren<AvatarTextureOptimizer>(true);

            if (components.Length == 0) return; // 未挂载，跳过 / nothing to do

            var localizer = ATONdmfBridge.Localizer;

            if (components.Length > 1)
            {
                foreach (var c in components)
                    ErrorReport.ReportError(localizer, ErrorSeverity.Error, "ato.error.multipleComponents", c);
                return;
            }

            var comp = components[0];
            var descriptor = comp.GetComponentInParent<VRCAvatarDescriptor>(true);
            if (descriptor == null)
            {
                ErrorReport.ReportError(localizer, ErrorSeverity.Error, "ato.warn.noDescriptor", comp);
            }
        }
    }

    /// <summary>(EN) Runs the full ATO optimization pipeline. (ZH) 运行完整 ATO 优化管线。</summary>
    public class OptimizePass : Pass<OptimizePass>
    {
        public override string DisplayName => "ATO: Optimize";

        protected override void Execute(BuildContext context)
        {
            var components = context.AvatarRootObject.GetComponentsInChildren<AvatarTextureOptimizer>(true);
            if (components.Length != 1) return; // 校验未通过时不处理 / skip if validation failed

            var comp = components[0];
            if (!comp.enable) return;

            var state = new ATOBuildContext
            {
                Ndmf = context,
                Component = comp,
                AvatarRoot = context.AvatarRootObject,
            };
            state.ResolveForPlatform(ATOBuildContext.DetectPlatform());

            ATOLog.Info(ATOLocalization.T(state.Language, "ato.log.start"));

            var pipeline = new ATOPipeline(state);
            try
            {
                pipeline.Run();
                state.Report.PrintSummary(state.Language);
                ATOLog.Info(ATOLocalization.T(state.Language, "ato.log.done"));
            }
            catch (OperationCanceledException)
            {
                ATOLog.Info("[ATO] Build cancelled by user");
            }
            catch (Exception e)
            {
                ErrorReport.ReportException(e);
                ATOLog.Error("ATO pipeline failed: " + e);
            }
        }
    }

    /// <summary>(EN) Bridges ATO i18n to NDMF's Localizer for error reporting. (ZH) 将 ATO i18n 桥接到 NDMF 的 Localizer。</summary>
    public static class ATONdmfBridge
    {
        private static Localizer _localizer;

        public static Localizer Localizer
        {
            get
            {
                if (_localizer == null)
                {
                    _localizer = new Localizer("en", () =>
                    {
                        var list = new List<(string, Func<string, string>)>();
                        foreach (var lang in ATOLocalization.AvailableLanguages)
                        {
                            list.Add((lang, key => ATOLocalization.GetExact(lang, key)));
                        }
                        return list;
                    });
                }
                return _localizer;
            }
        }
    }
}
