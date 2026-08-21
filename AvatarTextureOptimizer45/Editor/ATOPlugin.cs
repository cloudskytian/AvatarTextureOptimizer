using System;
using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;
using UnityEngine;

[assembly: ExportsPlugin(typeof(net.fosa.ato.ATOPlugin))]

namespace net.fosa.ato
{
    /// <summary>
    /// ATO NDMF 插件 / The ATO NDMF plugin.
    ///
    /// 执行顺序: Modular Avatar 之后, Avatar Optimizer 之前 (Optimizing 阶段) / Runs after Modular Avatar and
    /// before Avatar Optimizer (Optimizing phase). 需要 AnimatorServicesContext 以非破坏方式改写动画引用.
    /// Requires AnimatorServicesContext to rewrite animation references non-destructively.
    /// </summary>
    internal sealed class ATOPlugin : Plugin<ATOPlugin>
    {
        public ATOPlugin() { }

        public override string DisplayName => "Avatar Texture Optimizer";
        public override string QualifiedName => "net.fosa.avatar-texture-optimizer";

        protected override void Configure()
        {
            InPhase(BuildPhase.Optimizing)
                .AfterPlugin("nadena.dev.modular-avatar")
                .BeforePlugin("com.anatawa12.avatar-optimizer")
                .WithRequiredExtensions(
                    new[] { typeof(AnimatorServicesContext) },
                    sequence => { sequence.Run(ATOPass.Instance); }
                );
        }

        protected override void OnUnhandledException(Exception e)
        {
            if (e is OperationCanceledException)
            {
                ATOLog.Info("用户取消了烘焙, 已释放CPU/GPU/内存资源, 硬盘上的临时资产保留 / build cancelled by user; CPU/GPU/memory resources released, temporary assets on disk are kept");
                return;
            }

            ATOLog.Error($"ATO 处理发生异常 / ATO processing failed: {e}");
        }
    }
}
