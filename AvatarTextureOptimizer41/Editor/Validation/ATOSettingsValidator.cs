using UnityEngine;

// Pre-bake validation: exactly one ATOSettings per avatar, anchored on the VRCAvatarDescriptor object.
// 烘焙前校验：每个 Avatar 恰好一个 ATOSettings，且挂在 VRCAvatarDescriptor 对象上。

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    public static class ATOSettingsValidator
    {
        /// <summary>
        /// Returns null when valid, otherwise a human-readable error (build must abort).
        /// 合法时返回 null，否则返回可读错误（构建必须中止）。
        /// </summary>
        public static string Validate(GameObject root, ATOSettings found)
        {
            if (found == null) return "AvatarTextureOptimizer: no ATOSettings component found on the avatar";
            var all = root.GetComponentsInChildren<ATOSettings>(true);
            if (all.Length > 1)
                return $"AvatarTextureOptimizer: {all.Length} components found; only ONE is allowed per avatar (remove the extras)";
            if (!found.HasValidAnchor)
                return $"AvatarTextureOptimizer: the component on '{found.gameObject.name}' is NOT on the object carrying the VRCAvatarDescriptor; move it (VRCAvatarDescriptor required)";
            return null;
        }
    }
}
