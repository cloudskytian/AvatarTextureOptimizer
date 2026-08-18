// Copyright (c) fosa. Licensed under the MIT License.
// The single avatar-level component that drives the whole optimization.
// 驱动整个优化流程的唯一 Avatar 级组件。

using UnityEngine;

namespace Net.Fosa.AvatarTextureOptimizer
{
    /// <summary>
    /// Attach one of these to an avatar root that carries a VRCAvatarDescriptor to enable
    /// texture optimization. Exactly one component is permitted per avatar hierarchy; the build
    /// is aborted with an error otherwise.
    /// 将本组件挂载到带有 VRCAvatarDescriptor 的 Avatar 根物体上即可启用贴图优化。
    /// 每个 Avatar 层级中只允许存在一个，否则构建会报错中止。
    /// </summary>
    [AddComponentMenu("Avatar Texture Optimizer/Avatar Texture Optimizer")]
    [DisallowMultipleComponent]
    [HelpURL("https://github.com/fosa/AvatarTextureOptimizer")]
    public sealed class AvatarTextureOptimizer : MonoBehaviour
#if ATO_VRCSDK3_AVATARS
        , VRC.SDKBase.IEditorOnly
#endif
    {
        /// <summary>
        /// All user-facing configuration. The tool is still in development, so this schema may
        /// change without migration support.
        /// 全部用户配置。工具尚在开发阶段，该结构可能变更且不提供迁移支持。
        /// </summary>
        [SerializeField]
        private ATOSettings settings = new ATOSettings();

        /// <summary>Accessor for the settings block. / 设置块访问器。</summary>
        public ATOSettings Settings
        {
            get => settings ??= new ATOSettings();
            set => settings = value;
        }
    }
}
