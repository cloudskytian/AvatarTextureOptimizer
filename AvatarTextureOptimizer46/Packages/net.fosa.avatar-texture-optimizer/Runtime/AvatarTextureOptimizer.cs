// SPDX-License-Identifier: MIT
// EN: The user facing avatar component.
// ZH: 面向用户的 Avatar 组件。

using UnityEngine;
#if ATO_VRCSDK3_AVATARS
using VRC.SDKBase;
#endif

namespace Net.Fosa.AvatarTextureOptimizer
{
    /// <summary>
    /// EN: Add this component to the avatar root (the object holding the VRCAvatarDescriptor) to run the
    ///     texture optimizer during a non destructive build. Exactly one instance is allowed per avatar.
    /// ZH: 将该组件添加到 Avatar 根物体（挂有 VRCAvatarDescriptor 的对象）即可在非破坏性构建中执行贴图优化。
    ///     每个 Avatar 只允许存在一个实例。
    /// </summary>
    [AddComponentMenu("FOSA/Avatar Texture Optimizer")]
    [DisallowMultipleComponent]
    [HelpURL("https://github.com/fosa-net/AvatarTextureOptimizer")]
    public sealed class AvatarTextureOptimizer : MonoBehaviour
#if ATO_VRCSDK3_AVATARS
        , IEditorOnly
#endif
    {
        /// <summary>EN: All optimizer settings. ZH: 全部优化器设置。</summary>
        [SerializeField] public AtoSettings settings = new AtoSettings();

        /// <summary>
        /// EN: Schema version of the serialized settings. The tool is pre-release, so no migration is
        ///     performed; the field exists to make future migrations possible.
        /// ZH: 序列化设置的架构版本。工具处于开发阶段，暂不做迁移；保留该字段以便将来迁移。
        /// </summary>
        [SerializeField, HideInInspector] public int settingsVersion = 1;
    }
}
