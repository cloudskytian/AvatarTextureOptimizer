// AvatarTextureOptimizer.cs - The single avatar component that drives the whole optimization. / 挂载在Avatar上驱动整个优化的唯一组件。
// RULES / 规则:
//  - Exactly ONE component per avatar (root inclusive). / 一个Avatar（含子级）只允许一个组件。
//  - Must sit on the object carrying VRCAvatarDescriptor. / 必须挂在持有 VRCAvatarDescriptor 的对象上。
//  - Non-conforming mounts abort the bake/build with an error. / 不合规挂载将报错并中止烘焙或构建。
//  - The plugin removes itself from the built avatar. / 插件会在构建产物上移除自身。
using System;
using System.Collections.Generic;
using UnityEngine;
#if ATO_VRCSDK3A
using VRC.SDKBase;
#endif

namespace Fosa.ATO.Runtime
{
    [AddComponentMenu("Avatar Texture Optimizer/ATO Avatar Texture Optimizer")]
    [DisallowMultipleComponent]
    [HelpURL("https://github.com/fosa/AvatarTextureOptimizer")]
#if ATO_VRCSDK3A
    public class AvatarTextureOptimizer : MonoBehaviour, IEditorOnly
#else
    public class AvatarTextureOptimizer : MonoBehaviour
#endif
    {
        // ------------------------------------------------------------------
        // Settings / 设置
        // ------------------------------------------------------------------

        [Tooltip("All optimization parameters. / 全部优化参数。")]
        public ATOSettings settings = new ATOSettings();

        [Tooltip("PC platform override. / PC 平台Override。")]
        public ATOPlatformOverride pcOverride = new ATOPlatformOverride();
        [Tooltip("Android platform override. / Android 平台Override。")]
        public ATOPlatformOverride androidOverride = new ATOPlatformOverride();
        [Tooltip("iOS platform override. / iOS 平台Override。")]
        public ATOPlatformOverride iosOverride = new ATOPlatformOverride();

        [Tooltip("Objects whose referenced textures skip ALL optimization. Any object type is allowed (mesh, material, texture, animation, ...). / 其引用的全部贴图跳过所有优化的对象。允许任意类型（网格、材质、贴图、动画等）。")]
        public List<UnityEngine.Object> whitelist = new List<UnityEngine.Object>();

        // ---- Advanced / 高级（默认折叠，UI中展示） ----
        [Tooltip("Verbose [ATO] logging for debugging. / 调试用详细 [ATO] 日志。")]
        public bool verboseLog = false;
        [Tooltip("Log per-step timings (always summarized in the report). / 记录每步耗时（报告始终汇总）。")]
        public bool logTimings = true;
        [Tooltip("Log texture import settings in detail. / 详细记录贴图导入设置。")]
        public bool logImportSettings = false;

        // ------------------------------------------------------------------
        // Resolved settings / 解析后的设置
        // ------------------------------------------------------------------

        /// <summary>Resolve effective settings for a target platform (override wins when enabled). / 解析目标平台的有效设置（勾选Override时优先生效）。</summary>
        public ATOSettings Resolve(ATOPlatform platform)
        {
            ATOPlatformOverride o = platform switch
            {
                ATOPlatform.Android => androidOverride,
                ATOPlatform.iOS => iosOverride,
                _ => pcOverride,
            };
            return (o != null && o.enabled) ? o.settings : settings;
        }

        /// <summary>Convenience: current effective settings by the platform passed by the pipeline. / 便捷方法：管线传入平台后的当前有效设置。</summary>
        public ATOSettings ResolveEditor() => settings;
    }
}
