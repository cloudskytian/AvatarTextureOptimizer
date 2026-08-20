// Avatar Texture Optimizer (ATO)
// Bilingual comments: English + Simplified Chinese. 双语注释：英文 + 简体中文。

using AvatarTextureOptimizer;
using AvatarTextureOptimizer.Editor.Core;
using nadena.dev.ndmf;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

namespace AvatarTextureOptimizer.Editor.Passes
{
    /// <summary>
    /// Pass 1 — validate the ATO component placement.
    /// Requirements:
    ///  - Exactly ONE ATOAvatarTextureOptimizer on the avatar and its children.
    ///  - The object it is attached to MUST have a VRCAvatarDescriptor.
    /// Any violation aborts the bake with an error.
    ///
    /// Pass 1 —— 校验 ATO 组件挂载。
    /// 要求：
    ///  - Avatar 及其子级上一共只允许一个 ATOAvatarTextureOptimizer。
    ///  - 挂载对象必须带有 VRCAvatarDescriptor。
    /// 任一违规即报错中止。
    /// </summary>
    public sealed class ATOValidateComponentPass : Pass<ATOValidateComponentPass>
    {
        public override string DisplayName => "ATO: Validate component / 校验组件";

        protected override void Execute(BuildContext context)
        {
            var root = context.AvatarRootObject;
            var comps = root.GetComponentsInChildren<ATOAvatarTextureOptimizer>(true);

            if (comps.Length == 0)
            {
                ATOLog.Verbose("No ATO component found; skipping. / 未发现 ATO 组件，跳过。");
                return;
            }

            if (comps.Length > 1)
            {
                ATOError.Report(
                    "Multiple ATOAvatarTextureOptimizer components found on one avatar. Only one is allowed. " +
                    "/ 一个 Avatar 上发现多个 ATO 组件，只允许一个。");
                return;
            }

            var comp = comps[0];

            if (comp.GetComponent<VRCAvatarDescriptor>() == null)
            {
                ATOError.Report(
                    "ATOAvatarTextureOptimizer is attached to an object without VRCAvatarDescriptor. " +
                    "Attach it to the avatar root. / ATO 组件挂在了没有 VRCAvatarDescriptor 的对象上，请挂到 Avatar 根节点。",
                    ErrorSeverity.Error, comp);
                return;
            }

            var state = context.GetState<ATOBuildState>();
            state.Component = comp;
            ATOLog.Level = comp.logLevel;

            ATOLog.Info($"ATO bake started for avatar: {root.name} / 开始烘焙 Avatar：{root.name}");
        }
    }
}
