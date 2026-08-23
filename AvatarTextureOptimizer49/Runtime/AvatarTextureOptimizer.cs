using System.Collections.Generic;
using UnityEngine;
using VRC.SDKBase;

namespace Fosa.AvatarTextureOptimizer
{
    /// <summary>
    /// Avatar Texture Optimizer component. Exactly one per avatar, on the object holding the
    /// VRCAvatarDescriptor; anything else aborts the build with an error.
    /// / ATO 组件：一个 Avatar（含子级）只允许挂载一个，且必须挂在带 VRCAvatarDescriptor 的物体上；
    /// 不合规挂载会在烘焙/构建时报错并中止。
    /// </summary>
    [AddComponentMenu("Avatar Texture Optimizer/ATO Avatar Texture Optimizer")]
    [DisallowMultipleComponent]
    [HelpURL("https://github.com/fosa/AvatarTextureOptimizer")]
    public class AvatarTextureOptimizer : MonoBehaviour, IEditorOnly
    {
        // ------------------------------------------------------------------ whitelist
        /// <summary>
        /// Whitelist. Any object type is allowed (mesh, material, texture, animation, GameObject, ...).
        /// Every texture referenced (directly or recursively) by a whitelisted object skips ALL
        /// optimization. Textures sharing the UV with a whitelisted texture skip atlasing but still
        /// receive whole-texture scaling and import-parameter optimizations.
        /// / 白名单：不限制对象类型。白名单对象引用到的全部贴图跳过所有优化；
        /// 与其同 UV 的其他贴图跳过图集化，但保留整图缩放与导入参数等其他优化。
        /// </summary>
        public List<Object> whitelist = new List<Object>();

        // ------------------------------------------------------------------ settings
        /// <summary>Common settings (used for any platform without an enabled override). / 通用参数（未勾选平台覆盖时生效）。</summary>
        public AtoSettings settings = new AtoSettings();

        public AtoPlatformOverride pcOverride = new AtoPlatformOverride();
        public AtoPlatformOverride androidOverride = new AtoPlatformOverride();
        public AtoPlatformOverride iosOverride = new AtoPlatformOverride();

        /// <summary>UI language preference (editor-only, persisted by the inspector). / 界面语言选择（仅编辑器，Inspector 持久化）。</summary>
        [HideInInspector] public string languageOverride = "auto";
    }
}
