// SPDX-License-Identifier: MIT
// AvatarTextureOptimizer (ATO) - Avatar component.
// AvatarTextureOptimizer (ATO) - Avatar 组件。

using nadena.dev.ndmf;
using UnityEngine;

namespace Net.Fosa.AvatarTextureOptimizer
{
    /// <summary>
    /// EN: Attach a single instance of this component to an avatar root that carries a VRCAvatarDescriptor.
    ///     Exactly one instance is allowed per avatar (including children); the build aborts otherwise.
    ///     The component removes itself from the built avatar during the NDMF pass.
    /// ZH: 将本组件挂载到带有 VRCAvatarDescriptor 的 Avatar 根物体上。
    ///     一个 Avatar 及其子级只允许存在一个实例，否则会中止烘焙/构建并报错。
    ///     NDMF 处理过程中组件会将自身从成品上移除。
    /// </summary>
    [AddComponentMenu("FOSA/Avatar Texture Optimizer")]
    [DisallowMultipleComponent]
    [HelpURL("https://github.com/fosa/AvatarTextureOptimizer")]
    public sealed class AvatarTextureOptimizer : MonoBehaviour, INDMFEditorOnly
    {
        /// <summary>EN: All user-facing configuration. ZH: 全部用户配置。</summary>
        [SerializeField] public ATOSettings settings = new ATOSettings();

        /// <summary>
        /// EN: Bumped whenever the serialized layout changes during development. Version compatibility is
        ///     explicitly NOT maintained while the tool is pre-1.0.
        /// ZH: 开发期序列化结构变更时递增。1.0 之前不保证版本兼容性。
        /// </summary>
        [SerializeField, HideInInspector] public int settingsFormatVersion = 1;
    }
}
