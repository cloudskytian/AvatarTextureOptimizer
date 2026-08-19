using System;
using nadena.dev.ndmf;
using nadena.dev.ndmf.fluent;
using NetFosa.AvatarTextureOptimizer.Editor.Logging;
using NetFosa.AvatarTextureOptimizer.Editor.Utils;
using UnityEngine;

[assembly: ExportsPlugin(typeof(NetFosa.AvatarTextureOptimizer.Editor.ATOModule))]

namespace NetFosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// AvatarTextureOptimizer NDMF 插件。
    /// 执行时机：Optimizing 阶段，Modular Avatar 之后、Avatar Optimizer 之前
    /// （源码依据：nadena.dev.ndmf Sequence.AfterPlugin/BeforePlugin 约束 = 本序列整体在
    /// MA PluginEnd 之后、AAO PluginStart 之前；AAO OptimizerPlugin 的 Optimizing 序列见其源码）。
    /// </summary>
    public sealed class ATOModule : Plugin<ATOModule>
    {
        public override string QualifiedName => "net.fosa.avatar-texture-optimizer";
        public override string DisplayName => "AvatarTextureOptimizer";

        protected override void Configure()
        {
            InPhase(BuildPhase.Optimizing)
                .AfterPlugin("nadena.dev.modular-avatar")
                .BeforePlugin("com.anatawa12.avatar-optimizer")
                .Run(ATOPass.Instance);
        }
    }

    /// <summary>主 Pass：调用 BuildPipeline 执行全部优化。</summary>
    public sealed class ATOPass : Pass<ATOPass>
    {
        public override string DisplayName => "AvatarTextureOptimizer";

        protected override void Execute(BuildContext context)
        {
            var logger = new ATOLogger();
            try
            {
                BuildPipeline.Execute(context.AvatarRootObject, logger);
            }
            catch (ATOBuildCancelledException)
            {
                // 取消：已在管线内处理
            }
            catch (Exception e)
            {
                logger.Error($"AvatarTextureOptimizer pass failed: {e}");
                throw;
            }
        }
    }
}
