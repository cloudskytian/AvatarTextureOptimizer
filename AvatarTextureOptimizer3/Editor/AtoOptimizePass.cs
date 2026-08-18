// English: Single NDMF pass that runs the full ATO pipeline and removes itself afterwards.
// 中文：单个 NDMF Pass，执行完整流水线并在成品上移除自身组件。
using System;
using System.Linq;
using nadena.dev.ndmf;
using net.fosa.ato;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace net.fosa.ato.editor
{
    public sealed class AtoOptimizePass : Pass<AtoOptimizePass>
    {
        public override string DisplayName => "Avatar Texture Optimizer";

        protected override void Execute(BuildContext context)
        {
            var root = context.AvatarRootObject;
            var comps = root.GetComponentsInChildren<AvatarTextureOptimizer>(true);
            if (comps == null || comps.Length == 0)
            {
                AtoLog.VerboseInfo("No AvatarTextureOptimizer on avatar, skip.");
                return;
            }

            if (comps.Length > 1)
            {
                ErrorReport.ReportError(AtoErrors.Localizer, ErrorSeverity.Error,
                    "err.multiple_components");
                AtoLog.Error("Multiple AvatarTextureOptimizer components. Abort.");
                throw new Exception("[ATO] Multiple components on one avatar.");
            }

            var comp = comps[0];
            AtoLog.Verbose = comp.verboseLogs;
            AtoI18n.SetMode(comp.language);

            if (!AtoVrcCompat.HasAvatarDescriptor(root))
            {
                ErrorReport.ReportError(AtoErrors.Localizer, ErrorSeverity.Error,
                    "err.no_descriptor");
                AtoLog.Error("VRCAvatarDescriptor missing. Abort.");
                throw new Exception("[ATO] Missing VRCAvatarDescriptor.");
            }

            // Requirement: the GameObject that hosts the component must itself have VRCAvatarDescriptor.
            if (!AtoVrcCompat.HasAvatarDescriptor(comp.gameObject))
            {
                ErrorReport.ReportError(AtoErrors.Localizer, ErrorSeverity.Error,
                    "err.descriptor_on_host");
                AtoLog.Error("AvatarTextureOptimizer host has no VRCAvatarDescriptor. Abort.");
                throw new Exception("[ATO] Component must sit on the VRCAvatarDescriptor object.");
            }

            var platform = AtoPlatformUtil.Detect(context);
            var settings = comp.Resolve(platform);
            AtoLog.Info($"Start bake platform={platform} atlas={settings.generateAtlas} preset={settings.qualityPreset}");

            var cancel = AtoCancel.Create();
            try
            {
                using (var progress = new AtoProgress("Avatar Texture Optimizer", cancel))
                {
                    var pipeline = new AtoPipeline(context, comp, settings, platform, progress, cancel);
                    pipeline.Run();
                }
            }
            catch (AtoCanceledException)
            {
                AtoLog.Warn("User canceled. Temporary assets kept. CPU/GPU/memory released.");
                throw;
            }
            finally
            {
                // Always strip component from the baked avatar.
                if (comp != null)
                {
                    Object.DestroyImmediate(comp);
                    AtoLog.Info("Removed AvatarTextureOptimizer from baked avatar.");
                }
                AtoGpuUtil.ReleaseScratch();
                GC.Collect();
            }
        }

        private static bool IsOnAvatarRootOrSelf(AvatarTextureOptimizer c, GameObject root) =>
            c.transform == root.transform || c.transform.IsChildOf(root.transform);
    }
}
