using UnityEngine;

namespace Fosa.AvatarTextureOptimizer.Editor.Apply
{
    // 组件清理：NDMF 烘焙完成后从成品 Avatar 上移除本工具自身的组件（ATOAvatar 与 ATOWhitelist）。
    // Component cleanup: removes this tool's own components (ATOAvatar & ATOWhitelist) from the built avatar.
    internal static class ComponentCleanup
    {
        public static void Clean(ATOContext ctx)
        {
            foreach (var c in ctx.avatarRoot.GetComponentsInChildren<ATOAvatar>(true))
            {
                Object.DestroyImmediate(c);
            }
            foreach (var w in ctx.avatarRoot.GetComponentsInChildren<ATOWhitelist>(true))
            {
                Object.DestroyImmediate(w);
            }
            ATOLog.Debug("已移除自身组件 / own components removed");
        }
    }
}
