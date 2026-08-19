using nadena.dev.ndmf;
using UnityEditor;
using FOSA.AvatarTextureOptimizer;

namespace FOSA.AvatarTextureOptimizer.Editor
{
    internal static class ATOPlatformUtil
    {
        /// <summary>
        /// Default platform follows the current Unity build target, matching Unity's own override UI.
        /// 默认平台跟随当前 Unity 构建目标，对齐 Unity 自己的 platform override。
        /// </summary>
        public static ATOPlatform Detect(BuildContext context)
        {
            switch (EditorUserBuildSettings.activeBuildTarget)
            {
                case BuildTarget.Android: return ATOPlatform.Android;
                case BuildTarget.iOS: return ATOPlatform.iOS;
                default: return ATOPlatform.PC;
            }
        }

        public static string UnityPlatformName(ATOPlatform p)
        {
            switch (p)
            {
                case ATOPlatform.Android: return "Android";
                case ATOPlatform.iOS: return "iPhone";
                default: return "Standalone";
            }
        }
    }
}
