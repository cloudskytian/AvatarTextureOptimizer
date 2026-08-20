// SPDX-License-Identifier: MIT
// EN: The single component the user drops on the avatar root.
// ZH: 用户挂在 Avatar 根节点上的唯一组件。

using UnityEngine;

namespace Net.Fosa.AvatarTextureOptimizer
{
    /// <summary>
    /// EN: Avatar Texture Optimizer entry component. Exactly one instance is allowed per avatar
    ///     (root or descendants) and it must sit on a GameObject carrying a VRCAvatarDescriptor.
    /// ZH: Avatar Texture Optimizer 的入口组件。每个 Avatar（含子级）只允许存在一个，
    ///     并且必须挂在带有 VRCAvatarDescriptor 的对象上。
    /// </summary>
    [AddComponentMenu("Avatar Texture Optimizer/Avatar Texture Optimizer")]
    [DisallowMultipleComponent]
    [HelpURL("https://github.com/fosa/AvatarTextureOptimizer")]
    public sealed class AvatarTextureOptimizer : MonoBehaviour
#if ATO_VRCSDK3_AVATARS
        , VRC.SDKBase.IEditorOnly
#endif
    {
        /// <summary>EN: Serialized configuration. ZH: 序列化配置。</summary>
        [SerializeField] public ATOSettings settings = new ATOSettings();

        /// <summary>
        /// EN: Foldout state of the advanced section (editor only, harmless at runtime).
        /// ZH: 高级选项折叠状态（仅编辑器用，运行时无副作用）。
        /// </summary>
        [SerializeField] public bool advancedFoldout;

        /// <summary>EN: Foldout state of the output section. ZH: 输出设置折叠状态。</summary>
        [SerializeField] public bool outputFoldout;

        /// <summary>EN: Foldout state of the whitelist section. ZH: 白名单折叠状态。</summary>
        [SerializeField] public bool whitelistFoldout;

        /// <summary>EN: Foldout state of the debug section. ZH: 调试折叠状态。</summary>
        [SerializeField] public bool debugFoldout;
    }
}
