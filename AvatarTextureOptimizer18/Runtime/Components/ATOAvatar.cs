using UnityEngine;

namespace Fosa.AvatarTextureOptimizer
{
    // AvatarTextureOptimizer 主组件：挂载在存在 VRCAvatarDescriptor 的对象上，优化整个 Avatar。
    // Main component: put it on the same GameObject as the VRCAvatarDescriptor to optimize the whole avatar.
    // 规则（在烘焙时强校验）：
    // - 一个 Avatar 及其子级上只允许挂载一个本组件。
    // - 挂载对象上必须存在 VRCAvatarDescriptor，否则报错中止烘焙/构建。
    // Rules (enforced at build time):
    // - At most one instance of this component may exist on an avatar and its children.
    // - The hosting GameObject must have a VRCAvatarDescriptor, otherwise the build is aborted.
    [DisallowMultipleComponent]
    [AddComponentMenu("VRChat SDK/Avatar Texture Optimizer (ATO)")]
    [HelpURL("https://github.com/fosa/AvatarTextureOptimizer")]
    public sealed class ATOAvatar : MonoBehaviour
    {
        // 全部优化设置。All optimization settings.
        public ATOSettings settings = new ATOSettings();
    }
}
