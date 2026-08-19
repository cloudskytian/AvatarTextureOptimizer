// Avatar Texture Optimizer / 头像贴图优化器
// Public extension surface for advanced users and third-party tools.
// 公开扩展面：供高级用户与第三方工具使用。
//
// [EXPERIMENTAL] This API is pre-1.0 and may evolve; every hook is documented
// and failure-isolated (a throwing handler is logged and skipped, never aborts
// the build / 抛出异常的处理会被记录并跳过，绝不中止构建).

using System;
using System.Collections.Generic;
using UnityEngine;

namespace FOSA.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// Third-party shader analyzer. Registered analyzers run BEFORE ATO's
    /// built-in lilToon/Standard rules, in registration order; the first one
    /// returning true wins. Return false to defer to the next analyzer.
    /// 第三方着色器分析器。已注册的分析器先于 ATO 内置 lilToon/Standard 规则按注册
    /// 顺序执行，首个返回 true 的生效；返回 false 交给下一个分析器。
    /// </summary>
    public interface IATOShaderAnalyzer
    {
        /// <summary>
        /// Analyze a material. Set <paramref name="analysis"/> (a fully populated
        /// <see cref="ATOMaterialAnalysis"/>) and return true to claim the material.
        /// 分析材质。填充 <paramref name="analysis"/>（完整的 <see cref="ATOMaterialAnalysis"/>）
        /// 并返回 true 表示认领该材质。
        /// </summary>
        bool TryAnalyze(Material material, out ATOMaterialAnalysis analysis);
    }

    /// <summary>
    /// Context handed to <see cref="ATOExtensionApi.CustomPacker"/>.
    /// 传给 <see cref="ATOExtensionApi.CustomPacker"/> 的上下文。
    /// </summary>
    public sealed class ATOCustomPlanContext
    {
        /// <summary>The full usage model (read-only intent). / 完整使用模型（请只读）。</summary>
        public ATOUsageModel Model;
        /// <summary>Per-group per-island quality ratios. / 每组每岛质量比例。</summary>
        public Dictionary<ATOUVGroup, Dictionary<ATOIsland, Vector2>> QualityRatios;
        /// <summary>Effective build platform. / 生效的构建平台。</summary>
        public ATOPlatform Platform;
        /// <summary>The settings component. / 设置组件。</summary>
        public AvatarTextureOptimizer Settings;
        /// <summary>ATO's own eligibility predicate for atlas participation. / ATO 自用的图集参与判定。</summary>
        public Func<ATOUVGroup, bool> IsGroupAtlasEligible;
    }

    /// <summary>
    /// [EXPERIMENTAL] ATO extension API. All hooks are optional and additive;
    /// the pipeline works unchanged when nothing is registered.
    /// [实验性] ATO 扩展 API。所有钩子皆可选用且只增不改；未注册任何钩子时管线
    /// 行为与默认完全一致。
    /// </summary>
    public static class ATOExtensionApi
    {
        // ---------------------------------------------------------------
        // Shader analyzers / 着色器分析器
        // ---------------------------------------------------------------

        private static readonly List<IATOShaderAnalyzer> _analyzers = new List<IATOShaderAnalyzer>();

        /// <summary>Registrations (registration order). / 注册表（按注册顺序）。</summary>
        public static IReadOnlyList<IATOShaderAnalyzer> ShaderAnalyzers => _analyzers;

        /// <summary>Register a shader analyzer. / 注册着色器分析器。</summary>
        public static void RegisterShaderAnalyzer(IATOShaderAnalyzer analyzer)
        {
            if (analyzer == null || _analyzers.Contains(analyzer)) return;
            _analyzers.Add(analyzer);
        }

        /// <summary>Unregister a previously registered analyzer. / 注销已注册的分析器。</summary>
        public static bool UnregisterShaderAnalyzer(IATOShaderAnalyzer analyzer)
        {
            return analyzer != null && _analyzers.Remove(analyzer);
        }

        /// <summary>
        /// Try custom analyzers (isolated). Returns true when one claimed the material.
        /// 尝试自定义分析器（异常隔离）。有分析器认领时返回 true。
        /// </summary>
        internal static bool TryCustomAnalyze(Material mat, out ATOMaterialAnalysis analysis)
        {
            analysis = null;
            for (int i = 0; i < _analyzers.Count; i++)
            {
                var a = _analyzers[i];
                if (a == null) continue;
                try
                {
                    if (a.TryAnalyze(mat, out var result) && result != null)
                    {
                        if (result.material == null) result.material = mat;
                        analysis = result;
                        return true;
                    }
                }
                catch (Exception e)
                {
                    ATOLog.Warn($"custom shader analyzer {a.GetType().Name} threw: {e.Message}");
                }
            }
            return false;
        }

        // ---------------------------------------------------------------
        // Pipeline events / 管线事件
        // ---------------------------------------------------------------

        /// <summary>
        /// Fired after the usage model was built, BEFORE island building —
        /// handlers may still extend whitelists/exclusions.
        /// 使用模型构建完成后、岛构建之前触发——处理者仍可追加白名单/排除项。
        /// </summary>
        public static event Action<ATOUsageModel> ModelBuilt;
        internal static void NotifyModelBuilt(ATOUsageModel model) => SafeRaise(ModelBuilt, model, nameof(ModelBuilt));

        /// <summary>
        /// Fired right before atlas planning — last chance to atlas-block groups.
        /// 图集规划前一刻触发——给组图集阻塞标记的最后机会。
        /// </summary>
        public static event Action<ATOUsageModel> BeforeAtlasPlan;
        internal static void NotifyBeforeAtlasPlan(ATOUsageModel model) => SafeRaise(BeforeAtlasPlan, model, nameof(BeforeAtlasPlan));

        /// <summary>
        /// Fired right before the report is submitted — handlers may append notes.
        /// 报告提交前一刻触发——处理者可追加备注。
        /// </summary>
        public static event Action<ATOBuildReport> BeforeReport;
        internal static void NotifyBeforeReport(ATOBuildReport report) => SafeRaise(BeforeReport, report, nameof(BeforeReport));

        private static void SafeRaise<T>(Action<T> handlers, T arg, string name)
        {
            if (handlers == null) return;
            foreach (Action<T> h in handlers.GetInvocationList())
            {
                try { h(arg); }
                catch (Exception e)
                {
                    ATOLog.Warn($"extension handler {name}/{h.Method.DeclaringType?.Name} threw: {e.Message}");
                }
            }
        }

        // ---------------------------------------------------------------
        // Custom packer / 自定义装箱器
        // ---------------------------------------------------------------

        /// <summary>
        /// Replaces ATO's atlas planner; return a plan list, or null to defer to
        /// the built-in BLF packer. Plans must follow the same invariants (unit
        /// atomicity per UV group, padding respected).
        /// 替换 ATO 自带装箱器；返回规划列表，或返回 null 交还内置 BLF 装箱。
        /// 规划须满足相同不变量（UV 组单元原子性、padding 生效）。
        /// </summary>
        public delegate List<ATOAtlasPlan> CustomPackerDelegate(
            ATOCustomPlanContext context, out List<ATOUVGroup> fallbackGroups);

        /// <summary>Optional custom packer; null = built-in. / 可选自定义装箱器；null=内置。</summary>
        public static CustomPackerDelegate CustomPacker { get; set; }
    }
}
