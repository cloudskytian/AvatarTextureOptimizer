using System;
using System.Collections.Generic;
using UnityEngine;

namespace net.fosa.ato
{
    /// <summary>
    /// EN: The single component the user drops on their avatar root. Exactly one instance is allowed
    ///     per avatar (root included), and it must live on a GameObject carrying a VRCAvatarDescriptor.
    ///     Both rules are validated at bake time and produce a hard error that aborts the build.
    /// ZH: 用户挂到 Avatar 根节点上的唯一组件。每个 Avatar（含根节点）只允许存在一个实例，
    ///     且必须挂在带有 VRCAvatarDescriptor 的 GameObject 上。两条规则都会在烘焙时校验，
    ///     不满足则报出致命错误并中止构建。
    /// </summary>
    [AddComponentMenu("Avatar Texture Optimizer/Avatar Texture Optimizer")]
    [DisallowMultipleComponent]
    [HelpURL("https://github.com/fosa/AvatarTextureOptimizer")]
    public sealed class AvatarTextureOptimizer : MonoBehaviour
#if ATO_VRCSDK3_AVATARS
        // EN: VRC.SDKBase.IEditorOnly makes the SDK strip the component on upload, which is exactly what
        //     we want as a belt-and-braces backup for NDMF removing it during the build.
        // ZH: VRC.SDKBase.IEditorOnly 会让 SDK 在上传时剥离该组件，
        //     这正是我们想要的双保险——NDMF 在构建中已经移除它，这里再兜一层。
        , VRC.SDKBase.IEditorOnly
#endif
    {
        // ---- Whitelist ------------------------------------------------------------------------------

        /// <summary>
        /// EN: Objects excluded from optimisation. The list is deliberately untyped: a Mesh, a Material,
        ///     a Texture, an AnimationClip, a GameObject or a Renderer are all accepted. Every texture
        ///     reachable from a whitelisted object skips every optimisation, including import-parameter
        ///     tweaks. Other textures sharing the same UV skip atlasing only, and still take part in
        ///     whole-texture scaling and import optimisation.
        /// ZH: 排除在优化之外的对象。该列表刻意不限制类型：网格、材质、贴图、动画、GameObject、
        ///     Renderer 均可。白名单对象可达的所有贴图会跳过全部优化（包括导入参数优化）。
        ///     与之共享同一 UV 的其他贴图仅跳过图集化，仍会参与整图缩放与导入参数优化。
        /// </summary>
        [SerializeField] public List<UnityEngine.Object> whitelist = new List<UnityEngine.Object>();

        // ---- Profiles -------------------------------------------------------------------------------

        /// <summary>EN: Parameters used when no platform override applies. ZH: 无平台覆盖时使用的参数。</summary>
        [SerializeField] public PlatformProfile common = new PlatformProfile();

        /// <summary>EN: PC override. ZH: PC 平台覆盖。</summary>
        [SerializeField] public PlatformProfile pcOverride = new PlatformProfile { platform = ATOPlatform.PC };

        /// <summary>EN: Android override. ZH: Android 平台覆盖。</summary>
        [SerializeField] public PlatformProfile androidOverride = new PlatformProfile { platform = ATOPlatform.Android };

        /// <summary>EN: iOS override. ZH: iOS 平台覆盖。</summary>
        [SerializeField] public PlatformProfile iosOverride = new PlatformProfile { platform = ATOPlatform.iOS };

        // ---- Diagnostics ----------------------------------------------------------------------------

        /// <summary>EN: Emit verbose per-step [ATO] logs to the Unity console. ZH: 向 Unity 控制台输出详细的分步 [ATO] 日志。</summary>
        [SerializeField] public bool verboseLogging = false;

        /// <summary>EN: Emit extremely detailed per-island logs. Very slow, debugging only.
        /// ZH: 输出极其详细的逐岛日志。非常慢，仅供调试。</summary>
        [SerializeField] public bool traceLogging = false;

        // ---- Localization ---------------------------------------------------------------------------

        /// <summary>EN: UI language mode. ZH: 界面语言模式。</summary>
        [SerializeField] public ATOLanguageMode languageMode = ATOLanguageMode.Auto;

        /// <summary>EN: Language code used when <see cref="languageMode"/> is Manual. ZH: languageMode 为 Manual 时使用的语言代码。</summary>
        [SerializeField] public string manualLanguage = "en";

        // ---- UI state (not part of the optimisation result) -----------------------------------------

        /// <summary>EN: Inspector foldout state for the advanced section. ZH: Inspector 高级选项折叠状态。</summary>
        [SerializeField] public bool uiAdvancedExpanded = false;

        /// <summary>EN: Inspector foldout state for the texture parameter section. ZH: Inspector 贴图参数折叠状态。</summary>
        [SerializeField] public bool uiTextureParamsExpanded = false;

        /// <summary>
        /// EN: Pick the profile that should drive the current build. When the matching platform override
        ///     is enabled it wins, otherwise the common profile is used.
        /// ZH: 选出驱动当前构建的配置。若对应平台覆盖已启用则优先使用，否则使用通用配置。
        /// </summary>
        public PlatformProfile ResolveProfile(ATOPlatform platform)
        {
            switch (platform)
            {
                case ATOPlatform.PC: if (pcOverride != null && pcOverride.enabled) return pcOverride; break;
                case ATOPlatform.Android: if (androidOverride != null && androidOverride.enabled) return androidOverride; break;
                case ATOPlatform.iOS: if (iosOverride != null && iosOverride.enabled) return iosOverride; break;
            }
            return common;
        }

        /// <summary>
        /// EN: Enumerate all four profiles, common first. Used by the inspector and by validation.
        /// ZH: 枚举全部四个配置，通用配置在前。供 Inspector 与校验使用。
        /// </summary>
        public IEnumerable<PlatformProfile> AllProfiles()
        {
            yield return common;
            yield return pcOverride;
            yield return androidOverride;
            yield return iosOverride;
        }
    }
}
