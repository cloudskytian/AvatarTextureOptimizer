// ATOPlugin.cs — NDMF 插件注册与执行顺序 / NDMF plugin registration and execution ordering.
// 说明：在 Optimizing 阶段、Modular Avatar 之后、Avatar Optimizer 之前执行（符合需求：
// "处理应发生在 ma 执行后，AAO 执行前"）。AAO 未安装时 BeforePlugin 对不存在的插件名
// 惰性创建空阶段占位（已读 NDMF 源码 SolverContext.GetPluginPhases 验证），不会报错。
// Note: runs in the Optimizing phase, after Modular Avatar and before Avatar Optimizer (per requirement:
// "processing happens after MA, before AAO"). When AAO is absent, BeforePlugin lazily creates empty
// phase placeholders for unknown plugin names (verified in NDMF source SolverContext.GetPluginPhases), so it never errors.

using System;
using nadena.dev.ndmf;
using UnityEngine;
using Debug = UnityEngine.Debug;

[assembly: ExportsPlugin(typeof(Fosa.AvatarTextureOptimizer.ATOPlugin))]

namespace Fosa.AvatarTextureOptimizer
{
    /// <summary>NDMF 插件定义。/ NDMF plugin definition.</summary>
    public sealed class ATOPlugin : Plugin<ATOPlugin>
    {
        /// <summary>插件限定名。/ Plugin qualified name.</summary>
        public override string QualifiedName => "net.fosa.avatar-texture-optimizer";

        /// <summary>显示名。/ Display name.</summary>
        public override string DisplayName => "ATO: Avatar Texture Optimizer";

        /// <summary>配置插件执行顺序与执行体。/ Configure execution order and body.</summary>
        protected override void Configure()
        {
            // MA 之后、AAO 之前（Optimizing 阶段）/ after MA, before AAO (Optimizing phase)
            InPhase(BuildPhase.Optimizing)
                .AfterPlugin("nadena.dev.modular-avatar")
                .BeforePlugin("com.anatawa12.avatar-optimizer")
                .Run("net.fosa.avatar-texture-optimizer:process", ctx => new ATOBuildSession(ctx).Run());
        }

        /// <summary>未捕获异常处理（打日志便于定位）。/ Unhandled exception handling (log for diagnostics).</summary>
        protected override void OnUnhandledException(Exception e)
        {
            try
            {
                ATOLog.Error("Unhandled exception in ATO build: " + e);
            }
            catch (Exception)
            {
                Debug.LogException(e);
            }
            Debug.LogException(e);
        }
    }
}
