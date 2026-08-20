using UnityEngine;
#if ATO_VRCSDK_INSTALLED
using VRC.SDKBase;
#endif

namespace Fosa.Ato.Runtime
{
    /// <summary>
    /// Attach to the avatar root (which must have a VRCAvatarDescriptor). There must be at most
    /// one ATO component on an avatar and its descendants. The NDMF pass enforces this and
    /// aborts the build on violation.
    /// 挂在 Avatar 根节点（必须带 VRCAvatarDescriptor）。一个 Avatar 及其子级只允许挂载一个本组件，
    /// NDMF Pass 会强制校验，违规时报错中止构建。
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("Fosa/Avatar Texture Optimizer")]
    public class AvatarTextureOptimizer : MonoBehaviour
    {
        public AtoSettings Settings = new();

        [Tooltip("Objects (meshes, materials, textures, animations, ...) to skip. " +
               "Any texture referenced from a whitelisted object is skipped entirely (including param optimization); " +
               "other textures sharing the same UV skip atlasization but still get whole-texture scale + import optimizations.\n" +
               "白名单对象（网格/材质/贴图/动画等）。白名单内对象引用的全部贴图都跳过所有优化；" +
               "同 UV 的其他贴图跳过图集化，但仍参与整图缩放与导入参数优化。")]
        public UnityEngine.Object[] Whitelist = new UnityEngine.Object[0];

        /// <summary>True if the component is on a valid VRC avatar root. / 是否位于合法的 VRC Avatar 根节点上。</summary>
        public bool IsValidRoot
        {
            get
            {
#if ATO_VRCSDK_INSTALLED
                return GetComponent<VRCAvatarDescriptor>() != null;
#else
                return true; // best-effort when SDK not referenced in this assembly
#endif
            }
        }
    }
}
