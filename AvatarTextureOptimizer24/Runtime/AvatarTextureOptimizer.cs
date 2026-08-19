// ============================================================================
// AvatarTextureOptimizer.cs — 挂在 Avatar 上的主组件 / Main component on Avatar
// (EN) Attach one instance to an object that has a VRCAvatarDescriptor.
//      Only ONE instance is allowed across the avatar and its children.
// (ZH) 挂载到拥有 VRCAvatarDescriptor 的对象上。一个 Avatar 及其子级只允许一个。
// ============================================================================

using System.Collections.Generic;
using UnityEngine;

namespace Fosa.AvatarTextureOptimizer
{
    [AddComponentMenu("Fosa/Avatar Texture Optimizer")]
    [DisallowMultipleComponent]
    public class AvatarTextureOptimizer : MonoBehaviour
    {
        // 是否启用整个工具 / enable the whole tool
        [Tooltip("(EN) Enable texture optimization for this avatar. (ZH) 为该 Avatar 启用贴图优化。")]
        public bool enable = true;

        [Header("Quality (质量)")]
        public ATOQualitySettings quality = new ATOQualitySettings();

        [Header("Atlas (图集)")]
        public ATOAtlasSettings atlas = new ATOAtlasSettings();

        [Header("Compression (压缩)")]
        public ATOCompressionSettings compression = new ATOCompressionSettings();

        [Header("Deduplication (去重)")]
        public ATODedupSettings dedup = new ATODedupSettings();

        [Header("Platform override (平台覆盖)")]
        [Tooltip("(EN) Per-platform overrides. (ZH) 各平台覆盖设置。")]
        public List<ATOPlatformOverride> platformOverrides = new List<ATOPlatformOverride>();

        [Header("Whitelist (白名单)")]
        [Tooltip("(EN) Objects in this list skip all optimization (any referenced texture is skipped). (ZH) 白名单内对象跳过所有优化（其引用的贴图全部跳过）。")]
        public List<Object> whitelist = new List<Object>();

        [Header("Localization (本地化)")]
        [Tooltip("(EN) UI language. (ZH) 界面语言。")]
        public ATOLanguage language = ATOLanguage.Auto;

        // 高级选项折叠组 / advanced options foldout (inspector 用)
        [HideInInspector] public bool foldQuality = false;
        [HideInInspector] public bool foldCompression = false;
        [HideInInspector] public bool foldPlatform = false;
        [HideInInspector] public bool foldAdvanced = false;
    }
}
