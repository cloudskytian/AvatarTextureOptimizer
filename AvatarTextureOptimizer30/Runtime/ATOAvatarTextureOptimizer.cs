// ATOAvatarTextureOptimizer.cs — 主组件 / Main component.
// 用法：在一个存在 VRCAvatarDescriptor 的 GameObject 上挂载本组件（Avatar 及其子级上一共只允许一个）。
// Usage: add this component to a GameObject that has a VRCAvatarDescriptor (exactly one allowed per avatar hierarchy).

using System.Collections.Generic;
using UnityEngine;

namespace Fosa.AvatarTextureOptimizer
{
    /// <summary>
    /// Avatar 贴图优化组件：对整个 Avatar 执行 UV 岛质量缩放、剔除与图集重排。
    /// Avatar texture optimizer component: performs UV island quality scaling, trimming and atlas repacking on the whole avatar.
    /// </summary>
    [AddComponentMenu("ATO/Avatar Texture Optimizer", 0)]
    [DisallowMultipleComponent]
    [HelpURL("https://github.com/fosa/AvatarTextureOptimizer")]
    public sealed class ATOAvatarTextureOptimizer : MonoBehaviour
    {
        [Tooltip("全部优化配置（含高级选项）。/ All optimization settings (incl. advanced options).")]
        public ATOConfig config = new ATOConfig();

        [Tooltip("白名单资产：其中的对象及其引用的全部贴图跳过所有优化。/ Whitelist assets: objects inside and all textures they reference skip all optimizations.")]
        public List<ATOWhitelistAsset> whitelistAssets = new List<ATOWhitelistAsset>();

        [Tooltip("界面语言（空 = Auto 跟随 NDMF 当前语言，缺失翻译回退英文；语言列表来自包内 i18n json 配置文件，可扩展）。/ UI language (empty = Auto follows NDMF's language with English fallback; languages come from the in-package i18n json files and are extensible).")]
        public string i18nLanguage = "";
    }
}
