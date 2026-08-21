using UnityEngine;
using VRC.SDK3.Avatars.Components;

// The ATO component attached to an avatar.
// 挂载在 Avatar 上的 ATO 组件。

namespace Net.Fosa.AvatarTextureOptimizer
{
    /// <summary>
    /// One AvatarTextureOptimizer component per avatar. It must be attached to the object that
    /// carries the VRCAvatarDescriptor; at most one component is allowed in the whole avatar hierarchy.
    /// The component drives an NDMF build that analyzes and optimizes the avatar's textures.
    ///
    /// 每个 Avatar 仅允许一个 AvatarTextureOptimizer 组件，且必须挂在持有 VRCAvatarDescriptor 的对象上。
    /// 该组件驱动 NDMF 构建，对 Avatar 的贴图进行分析与优化。
    /// </summary>
    [AddComponentMenu("AvatarTextureOptimizer")]
    [HelpURL("https://github.com/fosa/AvatarTextureOptimizer")]
    [DisallowMultipleComponent]
    [RequireComponent(typeof(VRCAvatarDescriptor))]
    [DefaultExecutionOrder(-1000)]
    public sealed class ATOSettings : MonoBehaviour
    {
        [Tooltip("优化参数（全平台基准）。Optimization parameters (all-platform baseline).")]
        [SerializeField] internal ATOSettingsData data = new ATOSettingsData();

        public ATOSettingsData Data => data;

        /// <summary>
        /// True if the component sits on the VRCAvatarDescriptor anchor object.
        /// 组件是否挂在 VRCAvatarDescriptor 锚点对象上。
        /// </summary>
        public bool HasValidAnchor => GetComponent<VRCAvatarDescriptor>() != null;
    }
}
