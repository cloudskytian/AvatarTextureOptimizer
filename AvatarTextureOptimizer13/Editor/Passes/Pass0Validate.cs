// ATO — Avatar Texture Optimizer
// Pass 0: validate component placement and initialize the build context.
// Pass 0：校验组件挂载并初始化构建上下文。
//
// Rules (CLAUDE.md #26): exactly one component per avatar subtree; the hosting object
// must carry a VRCAvatarDescriptor; violations abort the build.
// 规则（CLAUDE.md #26）：每个 Avatar 子树只允许一个组件；挂载对象必须带 VRCAvatarDescriptor；违规即中止。

using System;
using nadena.dev.ndmf;
using UnityEditor;
using UnityEngine;
using net.fosa.ato;
#if ATO_VRCSDK3
using VRC.SDK3.Avatars.Components;
#endif

namespace net.fosa.ato.editor
{
    /// <summary>
    /// Pass 0 — validation & initialization. Pass 0——校验与初始化。
    /// </summary>
    public class Pass0Validate : ATOBasePass<Pass0Validate>
    {
        protected override void Process(ATOBuildContext bc, BuildContext context)
        {
            ATOBuildContext.Cancelled = false;

            var components = context.AvatarRootObject.GetComponentsInChildren<AvatarTextureOptimizer>(true);
            if (components == null || components.Length == 0)
            {
                // No component on this avatar → nothing to do. 该 Avatar 无组件 → 无需处理。
                ATOLog.Verbose("No AvatarTextureOptimizer component found; skipping avatar.");
                return;
            }

            if (components.Length > 1)
            {
                throw new InvalidOperationException(
                    ATOI18n.T(ATOI18nKeys.ErrorMultipleComponents, nameof(AvatarTextureOptimizer)));
            }

            var comp = components[0];
#if ATO_VRCSDK3
            var descriptor = comp.GetComponent<VRCAvatarDescriptor>();
            if (descriptor == null)
            {
                throw new InvalidOperationException(
                    ATOI18n.T(ATOI18nKeys.ErrorNoDescriptor, nameof(AvatarTextureOptimizer)));
            }
#endif

            if (!comp.enable)
            {
                ATOLog.Info("[ATO] Component is disabled; skipping this avatar.");
                return;
            }

            // Initialize localization + logging from the component settings.
            // 按组件设置初始化本地化与日志。
            ATOLog.Verbose = comp.verboseLogging;
            ATOI18n.SetLanguage(comp.language);

            var platform = ResolveBuildPlatform();
            var settings = comp.EffectiveSettingsFor(platform);

            bc.Component = comp;
            bc.Settings = settings;
            bc.Platform = platform;
            bc.AssetFolder = ResolveAssetFolder(context);
            bc.Result = new ATOAnalysisResult
            {
                component = comp,
                settings = settings,
            };

            ATOLog.Info($"Starting ATO for '{context.AvatarRootObject.name}' " +
                        $"(platform={platform}, preset={settings.qualityPreset}, " +
                        $"atlas={(settings.generateAtlas ? "on" : "off")}, padding={settings.islandPadding}px, " +
                        $"density={settings.minPixelDensity}/{settings.maxPixelDensity} px/m).");
        }

        /// <summary>Resolve the folder for generated assets, next to the NDMF asset container. 解析生成资产目录（NDMF 资产容器旁）。</summary>
        internal static string ResolveAssetFolder(nadena.dev.ndmf.BuildContext context)
        {
            try
            {
                var path = UnityEditor.AssetDatabase.GetAssetPath(context.AssetContainer);
                if (!string.IsNullOrEmpty(path))
                {
                    var dir = System.IO.Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(dir)) return dir;
                }
            }
            catch (System.Exception) { /* fall through 回退 */ }
            return "Assets/ATO_Generated";
        }

        /// <summary>
        /// Map the current build target to an ATO platform. 将当前构建目标映射为 ATO 平台。
        /// </summary>
        internal static ATOPlatform ResolveBuildPlatform()
        {
            switch (EditorUserBuildSettings.activeBuildTarget)
            {
                case BuildTarget.Android: return ATOPlatform.Android;
                case BuildTarget.iOS: return ATOPlatform.iOS;
                default: return ATOPlatform.PC;
            }
        }
    }
}
