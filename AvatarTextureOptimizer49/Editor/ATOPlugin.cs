using System;
using System.Linq;
using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;
using nadena.dev.ndmf.fluent;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using Object = UnityEngine.Object;

[assembly: ExportsPlugin(typeof(Fosa.AvatarTextureOptimizer.Editor.ATOPlugin))]

namespace Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// NDMF plugin entry. Runs in the Optimizing phase, after Modular Avatar and before AAO.
    /// / NDMF 插件入口：Optimizing 阶段、MA 之后、AAO 之前执行。
    /// </summary>
    [RunsOnPlatforms(WellKnownPlatforms.VRChatAvatar30)]
    public sealed class ATOPlugin : Plugin<ATOPlugin>
    {
        public override string QualifiedName => "net.fosa.avatar-texture-optimizer";
        public override string DisplayName => "Avatar Texture Optimizer";
        public override Color? ThemeColor => new Color(0.30f, 0.72f, 0.62f);

        protected override void Configure()
        {
            InPhase(BuildPhase.Optimizing)
                .WithRequiredExtension(typeof(AnimatorServicesContext), seq =>
                {
                    seq.Run(ATOProcessPass.Instance)
                        .AfterPlugin("nadena.dev.modular-avatar")
                        .BeforePlugin("com.anatawa12.avatar-optimizer");
                });
        }
    }

    /// <summary>
    /// The single ATO pass: validate → process → remove components → report.
    /// / 唯一的处理 Pass：校验 → 处理 → 移除组件 → 报告。
    /// </summary>
    public sealed class ATOProcessPass : Pass<ATOProcessPass>
    {
        public override string DisplayName => "Avatar Texture Optimizer";

        protected override void Execute(BuildContext context)
        {
            ATOLog.ResetTimings();
            ATOLog.Info("pass start");

            // ---- validation: exactly one component, on the VRCAvatarDescriptor object / 校验挂载 ----
            var components = context.AvatarRootObject
                .GetComponentsInChildren<AvatarTextureOptimizer>(true);
            if (components.Length == 0)
            {
                ATOLog.Verbose("no component, nothing to do");
                return;
            }

            var descriptor = context.AvatarRootObject.GetComponent<VRCAvatarDescriptor>();
            foreach (var c in components)
            {
                if (c.gameObject != context.AvatarRootObject || descriptor == null)
                {
                    ErrorReport.ReportError(ATOL10n.NdmfLocalizer, ErrorSeverity.Error,
                        "ato:error:component-placement", c.gameObject.name);
                }
            }

            if (components.Length > 1)
            {
                ErrorReport.ReportError(ATOL10n.NdmfLocalizer, ErrorSeverity.Error,
                    "ato:error:multiple-components", components.Length.ToString());
            }

            if (context.ErrorReport.Errors.Any(e => e.TheError.Severity >= ErrorSeverity.Error))
            {
                // Fatal validation failure: abort the build as requested. / 校验失败：按要求中止构建。
                throw new AtoAbortException("ATO component placement is invalid / ATO 组件挂载不合规");
            }

            var component = components[0];
            var platform = DetectPlatform();
            var settings = AtoSettings.Resolve(component.settings, component.pcOverride,
                component.androidOverride, component.iosOverride, platform);

            ATOLog.VerboseEnabled = settings.verboseLog;
            ATOProgress.Begin();
            try
            {
                ATOProcessor.Run(context, component, settings, platform);

                // Success: remove ourselves from the baked avatar. / 成功：从成品移除自身组件。
                foreach (var c in components) Object.DestroyImmediate(c);
            }
            catch (AtoCancelledException)
            {
                // User cancelled: keep temp assets on disk (already saved), release is done in
                // finally blocks. Re-throw aborts the build. / 用户取消：保留临时资产；finally 已释放资源，重抛以中止构建。
                ATOLog.Info("cancelled by user; build aborted / 已被用户取消，构建中止");
                throw;
            }
            finally
            {
                ATOProgress.End();
                ATOLog.VerboseEnabled = false;
            }
        }

        internal static AtoPlatform DetectPlatform()
        {
            switch (EditorUserBuildSettings.activeBuildTarget)
            {
                case BuildTarget.Android: return AtoPlatform.Android;
                case BuildTarget.iOS: return AtoPlatform.iOS;
                default: return AtoPlatform.PC;
            }
        }
    }
}
