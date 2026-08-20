using nadena.dev.ndmf;
using UnityEngine;

namespace Fosa.ATO.Editor
{
    /// <summary>阶段 0：收集贴图与材质（去重、白名单解析、贴图去重）。</summary>
    public class ATOCollectPass : Pass<ATOCollectPass>
    {
        protected override void Execute(BuildContext context)
        {
            var data = context.GetState<ATOBuildData>();
            if (!ATOValidation.TryInit(context, data)) return;
            new ATOCollector(context, data).Run();
        }
    }

    /// <summary>阶段 1：分析动画与 UV 映射（UV 组、岛提取、形态键/缩放面积、UV 归一）。</summary>
    public class ATOAnalyzePass : Pass<ATOAnalyzePass>
    {
        protected override void Execute(BuildContext context)
        {
            var data = context.GetState<ATOBuildData>();
            if (data.component == null) return;
            new ATOAnalyzer(context, data).Run();
        }
    }

    /// <summary>阶段 2：按目标质量缩放 UV 岛（质量算法 + 二分搜索）。</summary>
    public class ATOProcessPass : Pass<ATOProcessPass>
    {
        protected override void Execute(BuildContext context)
        {
            var data = context.GetState<ATOBuildData>();
            if (data.component == null) return;
            new ATOProcessor(context, data).Run();
        }
    }

    /// <summary>阶段 3：装箱为图集（光栅化 + 候选图集池）。</summary>
    public class ATOPackPass : Pass<ATOPackPass>
    {
        protected override void Execute(BuildContext context)
        {
            var data = context.GetState<ATOBuildData>();
            if (data.component == null) return;
            new ATOPacker(context, data).Run();
        }
    }

    /// <summary>阶段 4：写回网格/贴图/材质 + AAO 兼容 + 去重 + 报告 + 移除组件。</summary>
    public class ATOApplyPass : Pass<ATOApplyPass>
    {
        protected override void Execute(BuildContext context)
        {
            var data = context.GetState<ATOBuildData>();
            if (data.component == null) return;
            new ATOApplier(context, data).Run();
        }
    }

    /// <summary>校验：单实例 + VRCAvatarDescriptor。返回 false 表示应跳过（无组件）。</summary>
    public static class ATOValidation
    {
        /// <summary>尝试初始化。无组件返回 false；违规则抛异常中止烘焙。</summary>
        public static bool TryInit(BuildContext context, ATOBuildData data)
        {
            var root = context.AvatarRootObject;
            if (root == null) return false;

            var components = root.GetComponentsInChildren<AvatarTextureOptimizer>(true);
            if (components.Length == 0) return false;

            if (components.Length > 1)
            {
                ATOLogger.Error(ATOLocalization.Tr("whitelist.multiple"));
                throw new System.InvalidOperationException("[ATO] Multiple AvatarTextureOptimizer components found");
            }

            var comp = components[0];

            if (!HasVRCAvatarDescriptor(comp.gameObject))
            {
                ATOLogger.Error(ATOLocalization.Tr("whitelist.noDescriptor"));
                throw new System.InvalidOperationException("[ATO] VRCAvatarDescriptor required");
            }

            data.component = comp;
            ATOLogger.Verbose = comp.verboseLogging;
            ATOLogger.ResetReport();
            ATOLogger.Info($"Starting Avatar Texture Optimizer on '{root.name}' (generateAtlas={comp.generateAtlas}, quality={comp.qualityPreset})");

            // 白名单集合化。Materialize whitelist into a set.
            data.whitelistSet.Clear();
            if (comp.whitelist != null)
                foreach (var o in comp.whitelist)
                    if (o != null) data.whitelistSet.Add(o);

            return true;
        }

        /// <summary>通过反射检测 VRCAvatarDescriptor（避免强依赖 SDK 程序集名）。</summary>
        public static bool HasVRCAvatarDescriptor(GameObject go)
        {
            if (go.GetComponent("VRCAvatarDescriptor") != null) return true;
            foreach (var c in go.GetComponents<Component>())
            {
                if (c != null && c.GetType().Name == "VRCAvatarDescriptor") return true;
            }
            return false;
        }
    }
}
