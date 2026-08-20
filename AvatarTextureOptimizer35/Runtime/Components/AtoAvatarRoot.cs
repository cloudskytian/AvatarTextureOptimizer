using UnityEngine;

namespace net.fosa.avatar_texture_optimizer
{
    /// <summary>
    /// Add this component to the avatar root to optimize the whole avatar. / 在 Avatar 根部挂载此组件即可优化整个 Avatar。
    ///
    /// Rules: / 规则：
    /// - At most ONE AtoAvatarRoot may exist on an avatar and its children. / 一个 Avatar 及其子级上只允许一个。
    /// - The GameObject it is attached to MUST have a VRCAvatarDescriptor. / 挂载对象上必须存在 VRCAvatarDescriptor。
    /// - Violations cause the bake/build to abort with an error. / 违规挂载会在烘焙/构建时报错中止。
    /// </summary>
    [AddComponentMenu("ATO/Avatar Texture Optimizer (Root)")]
    [DisallowMultipleComponent]
    public sealed class AtoAvatarRoot : MonoBehaviour
    {
        /// <summary>All settings. / 全部设置。</summary>
        public AtoSettings settings = new AtoSettings();
    }
}
