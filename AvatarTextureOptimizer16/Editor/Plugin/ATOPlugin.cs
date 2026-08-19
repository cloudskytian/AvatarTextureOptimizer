using nadena.dev.ndmf;

[assembly: ExportsPlugin(typeof(AvatarTextureOptimizer.Editor.ATOPlugin))]

namespace AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// NDMF plugin: runs after Modular Avatar and before Avatar Optimizer (AAO), in the
    /// Optimizing phase. / NDMF 插件：在 Optimizing 阶段、Modular Avatar 之后、AAO 之前运行。
    /// </summary>
    public sealed class ATOPlugin : Plugin<ATOPlugin>
    {
        public override string QualifiedName => "net.fosa.avatar-texture-optimizer";
        public override string DisplayName => "Avatar Texture Optimizer";

        protected override void Configure()
        {
            InPhase(BuildPhase.Optimizing)
                .AfterPlugin("nadena.dev.modular-avatar")
                .BeforePlugin("com.anatawa12.avatar-optimizer")
                .Run("Avatar Texture Optimizer", ctx => ATOProcessor.Run(ctx));
        }

        protected override void OnUnhandledException(System.Exception e)
        {
            // NDMF already routes this; keep a [ATO] log for clarity. / NDMF 已处理；补充 [ATO] 日志便于定位。
            UnityEngine.Debug.LogError("[ATO] unhandled exception: " + e);
        }
    }
}
