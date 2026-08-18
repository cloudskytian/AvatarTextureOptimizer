// Avatar Texture Optimizer (ATO)
// Removes the ATO component from the finished avatar after baking.
// 烘焙完成后从成品上移除 ATO 组件。

using UnityEngine;

namespace NetFosa.ATO
{
    /// <summary>
    /// Stage 9b: self-removal from the processed avatar. / 阶段 9b：从处理后的 Avatar 上移除自身。
    /// </summary>
    public static class ATOSelfRemoval
    {
        public static void Remove(ATOBuildContext build)
        {
            if (build.component == null) return;
            Object.DestroyImmediate(build.component);
            ATOLogger.Debug("Removed ATOAvatarOptimizer component from the processed avatar. / 已从成品上移除 ATOAvatarOptimizer 组件。");
        }
    }
}
