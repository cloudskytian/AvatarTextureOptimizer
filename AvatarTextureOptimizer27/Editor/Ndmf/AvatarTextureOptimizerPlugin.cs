#if NDMF || true
using System;
using nadena.dev.ndmf;
using Net.Fosa.AvatarTextureOptimizer;
using UnityEngine;

[assembly: ExportsPlugin(typeof(Net.Fosa.AvatarTextureOptimizer.Editor.AvatarTextureOptimizerPlugin))]

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// NDMF plugin: after Modular Avatar, before AAO.
    /// NDMF 插件：MA 之后、AAO 之前。
    /// </summary>
    public sealed class AvatarTextureOptimizerPlugin : Plugin<AvatarTextureOptimizerPlugin>
    {
        public override string QualifiedName => "net.fosa.avatar-texture-optimizer";
        public override string DisplayName => "Avatar Texture Optimizer";

        protected override void Configure()
        {
            InPhase(BuildPhase.Optimizing)
                .AfterPlugin("nadena.dev.modular-avatar")
                .BeforePlugin("com.anatawa12.avatar-optimizer")
                .Run(AvatarTextureOptimizerPass.Instance);
        }
    }

    public sealed class AvatarTextureOptimizerPass : Pass<AvatarTextureOptimizerPass>
    {
        public override string DisplayName => "ATO Optimize Textures";

        protected override void Execute(BuildContext context)
        {
            var root = context.AvatarRootObject;
            var comps = root.GetComponentsInChildren<Net.Fosa.AvatarTextureOptimizer.AvatarTextureOptimizer>(true);
            if (comps.Length == 0) return;
            if (comps.Length > 1)
            {
                AtoLog.Error("Multiple AvatarTextureOptimizer components on one avatar. Bake aborted.");
                throw new InvalidOperationException("[ATO] Multiple components on avatar");
            }

            var comp = comps[0];
            var desc = comp.GetComponent<VRC.SDK3.Avatars.Components.VRCAvatarDescriptor>();
            if (desc == null)
            {
                AtoLog.Error("AvatarTextureOptimizer must be on the object with VRCAvatarDescriptor.");
                throw new InvalidOperationException("[ATO] Missing VRCAvatarDescriptor");
            }

            AtoLog.Verbose = comp.verboseLogs;
            using (AtoLog.Time("NDMF pass total"))
            {
                var pipeline = new BakePipeline();
                var report = pipeline.Execute(context, comp);
                NdmfReportSink.Publish(context, report);
            }

            UnityEngine.Object.DestroyImmediate(comp);
        }
    }
}
#endif
