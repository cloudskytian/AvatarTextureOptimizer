using UnityEditor;

namespace net.fosa.avatar_texture_optimizer.editor
{
    /// <summary>
    /// Platform helpers: map ATO's platform enum to Unity build target names, and resolve the
    /// current build platform (used as the default for platform overrides). /
    /// 平台工具：ATO 平台枚举 ↔ Unity 构建目标名映射；解析当前构建平台（平台 override 的默认值）。
    /// </summary>
    internal static class AtoPlatformUtil
    {
        /// <summary>The name of the current build platform (texture importer naming). / 当前构建平台的贴图导入器名称。</summary>
        public static string CurrentPlatformName()
        {
            switch (EditorUserBuildSettings.activeBuildTarget)
            {
                case BuildTarget.Android: return "Android";
                case BuildTarget.iOS: return "iPhone";
                case BuildTarget.StandaloneWindows:
                case BuildTarget.StandaloneWindows64:
                case BuildTarget.StandaloneLinux64:
                case BuildTarget.StandaloneOSX:
                case BuildTarget.StandaloneWindowsPlayer:
                    return "Standalone";
                default: return "Standalone";
            }
        }

        /// <summary>Map ATO platform enum to importer platform name. / ATO 平台枚举 → 导入器平台名。</summary>
        public static string ImporterPlatform(AtoTargetPlatform platform) => platform switch
        {
            AtoTargetPlatform.Android => "Android",
            AtoTargetPlatform.IOS => "iPhone",
            _ => "Standalone",
        };

        /// <summary>Map ATO platform enum to Unity BuildTarget (for format capability checks). / ATO 平台枚举 → Unity BuildTarget。</summary>
        public static BuildTarget BuildTargetFor(AtoTargetPlatform platform) => platform switch
        {
            AtoTargetPlatform.Android => BuildTarget.Android,
            AtoTargetPlatform.IOS => BuildTarget.iOS,
            _ => BuildTarget.StandaloneWindows64,
        };

        /// <summary>Resolve the ATO platform for the current build target. / 解析当前构建目标对应的 ATO 平台。</summary>
        public static AtoTargetPlatform CurrentPlatform()
        {
            switch (EditorUserBuildSettings.activeBuildTarget)
            {
                case BuildTarget.Android: return AtoTargetPlatform.Android;
                case BuildTarget.iOS: return AtoTargetPlatform.IOS;
                default: return AtoTargetPlatform.PC;
            }
        }

        /// <summary>Whether the platform override is enabled in the settings. / 设置中该平台 override 是否启用。</summary>
        public static bool IsOverrideEnabled(AtoSettings settings, AtoTargetPlatform platform) => platform switch
        {
            AtoTargetPlatform.Android => settings.platforms.android.enabled,
            AtoTargetPlatform.IOS => settings.platforms.ios.enabled,
            _ => settings.platforms.pc.enabled,
        };

        /// <summary>Get the platform override. / 获取平台 override。</summary>
        public static AtoPlatformOverride GetOverride(AtoSettings settings, AtoTargetPlatform platform) => platform switch
        {
            AtoTargetPlatform.Android => settings.platforms.android,
            AtoTargetPlatform.IOS => settings.platforms.ios,
            _ => settings.platforms.pc,
        };
    }
}
