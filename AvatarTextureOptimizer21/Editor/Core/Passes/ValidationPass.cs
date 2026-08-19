// Validation Pass - Validates component placement and avatar configuration
// 验证Pass - 验证组件位置和Avatar配置

using System.Diagnostics;
using nadena.dev.ndmf;
using net.fosa.avatar_texture_optimizer.Runtime;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace net.fosa.avatar_texture_optimizer.Editor.Core.Passes
{
    /// <summary>
    /// Validates the ATO component and avatar setup before processing.
    /// 在处理之前验证ATO组件和Avatar设置。
    /// </summary>
    public class ValidationPass : Pass<ValidationPass>
    {
        public override string DisplayName => "ATO: Validation / 验证";

        protected override void Execute(BuildContext context)
        {
            var sw = Stopwatch.StartNew();
            var atoCtx = context.GetState<ATOBuildContext>();

            ATOLog.Info("Starting validation...");
            ATOLog.Info("开始验证...");

            var root = context.AvatarRootObject;

            // Find ATO component
            // 查找ATO组件
            var component = root.GetComponentInChildren<AvatarTextureOptimizerComponent>(true);
            if (component == null)
            {
                ATOLog.Error("AvatarTextureOptimizerComponent not found on avatar. Skipping optimization.");
                ATOLog.Error("未在Avatar上找到AvatarTextureOptimizerComponent。跳过优化。");
                atoCtx.IsValid = false;
                return;
            }

            // Check: component must be on the avatar root (same object as VRCAvatarDescriptor)
            // 检查：组件必须在Avatar根对象上（与VRCAvatarDescriptor同一对象）
            if (component.gameObject != root)
            {
                ATOLog.Error($"AvatarTextureOptimizerComponent must be on the avatar root object '{root.name}', " +
                             $"but found on '{component.gameObject.name}'. Aborting.");
                ATOLog.Error($"AvatarTextureOptimizerComponent必须在Avatar根对象'{root.name}'上，" +
                             $"但发现其在'{component.gameObject.name}'上。中止处理。");
                atoCtx.IsValid = false;
                return;
            }

            // Check: only one ATO component allowed
            // 检查：只允许一个ATO组件
            var allComponents = root.GetComponentsInChildren<AvatarTextureOptimizerComponent>(true);
            if (allComponents.Length > 1)
            {
                ATOLog.Error($"Multiple AvatarTextureOptimizerComponent found ({allComponents.Length}). " +
                             "Only one is allowed per avatar. Aborting.");
                ATOLog.Error($"发现多个AvatarTextureOptimizerComponent（{allComponents.Length}个）。" +
                             "每个Avatar只允许一个。中止处理。");
                atoCtx.IsValid = false;
                return;
            }

            // Check: VRCAvatarDescriptor must exist
            // 检查：VRCAvatarDescriptor必须存在
#if NDMF_VRCSDK3_AVATARS
            var vrcDescriptor = root.GetComponent<VRC.SDK3.Avatars.Components.VRCAvatarDescriptor>();
            if (vrcDescriptor == null)
            {
                ATOLog.Error("VRCAvatarDescriptor not found on avatar root. ATO requires a VRChat avatar.");
                ATOLog.Error("未在Avatar根对象上找到VRCAvatarDescriptor。ATO需要VRChat Avatar。");
                atoCtx.IsValid = false;
                return;
            }
#endif

            // Resolve platform
            // 解析平台
            atoCtx.Component = component;
            atoCtx.EffectivePlatform = ResolvePlatform(component.targetPlatform);

            ATOLog.Info($"Validation passed. Platform: {atoCtx.EffectivePlatform}");
            ATOLog.Info($"验证通过。平台：{atoCtx.EffectivePlatform}");

            sw.Stop();
            atoCtx.StageTimings["Validation"] = sw.Elapsed.TotalMilliseconds;
        }

        private TargetPlatform ResolvePlatform(TargetPlatform requested)
        {
            if (requested != TargetPlatform.Auto) return requested;

#if UNITY_EDITOR
            var target = UnityEditor.EditorUserBuildSettings.activeBuildTarget;
            switch (target)
            {
                case UnityEditor.BuildTarget.Android:
                    return TargetPlatform.Android;
                case UnityEditor.BuildTarget.iOS:
                    return TargetPlatform.iOS;
                default:
                    return TargetPlatform.PC;
            }
#else
            return TargetPlatform.PC;
#endif
        }
    }
}
