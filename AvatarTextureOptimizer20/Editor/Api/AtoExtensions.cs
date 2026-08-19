// Public extension API for advanced users & third-party developers.
// 面向高级用户与第三方开发者的公开扩展接口。
using System;
using System.Collections.Generic;

namespace net.fosa.ato.editor
{
    /// <summary>A custom pipeline stage. / 自定义流水线阶段。</summary>
    public interface IAtoCustomStage
    {
        /// <summary>Sort order: builtin stages run at 100,200,...,900. / 排序值，内置阶段为100..900。</summary>
        int Order { get; }
        void Run(AtoContext context);
    }

    /// <summary>
    /// Extension registry. Third parties can:
    ///  - register custom pipeline stages (run between builtin stages by Order)
    ///  - register shader semantics providers (see ShaderSemantics.Register)
    ///  - hook before/after bake events.
    /// 扩展注册表：自定义阶段、着色器语义 Provider、烘焙前后回调。
    /// </summary>
    public static class AtoExtensions
    {
        internal static readonly List<IAtoCustomStage> Stages = new List<IAtoCustomStage>();

        /// <summary>Fires before the pipeline runs. / 流水线开始前触发。</summary>
        public static event Action<AtoContext> OnBeforeProcess;
        /// <summary>Fires after the pipeline (before report). / 流水线结束后（报告前）触发。</summary>
        public static event Action<AtoContext> OnAfterProcess;

        public static void RegisterStage(IAtoCustomStage stage)
        {
            Stages.Add(stage);
            Stages.Sort((a, b) => a.Order.CompareTo(b.Order));
        }

        public static void RegisterShaderSemantics(IAtoShaderSemanticsProvider provider) =>
            ShaderSemantics.Register(provider);

        internal static void FireBefore(AtoContext ctx) => OnBeforeProcess?.Invoke(ctx);
        internal static void FireAfter(AtoContext ctx) => OnAfterProcess?.Invoke(ctx);

        internal static void RunCustomStages(AtoContext ctx, int afterOrder, int beforeOrder)
        {
            foreach (var s in Stages)
                if (s.Order > afterOrder && s.Order <= beforeOrder)
                {
                    try { s.Run(ctx); }
                    catch (AtoCancelledException) { throw; }
                    catch (Exception e) { AtoLog.Error($"custom stage {s.GetType().Name} failed: {e}"); }
                }
        }
    }
}
