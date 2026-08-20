// ATOWhitelistAsset.cs — 白名单资产（可放任意类型的资产对象）/ Whitelist asset (can hold any asset type).
// 说明：白名单不限制对象类型。创建本资产后把需要跳过优化的对象（网格/材质/贴图/动画/GameObject 等）
// 拖入列表，并挂到 ATOAvatarTextureOptimizer.whitelistAssets 上。
// Note: whitelist entries are type-agnostic. Create this asset, drag any objects (meshes/materials/
// textures/animations/GameObjects...) into the list, and reference it from ATOAvatarTextureOptimizer.whitelistAssets.

using System.Collections.Generic;
using UnityEngine;

namespace Fosa.AvatarTextureOptimizer
{
    /// <summary>
    /// 白名单资产：列表中的对象及其引用的全部贴图跳过所有优化。
    /// Whitelist asset: listed objects and all textures they reference skip all optimizations.
    /// </summary>
    [CreateAssetMenu(fileName = "ATOWhitelist", menuName = "ATO/Whitelist Asset", order = 200)]
    public sealed class ATOWhitelistAsset : ScriptableObject
    {
        [Tooltip("白名单对象列表（不限类型：网格/材质/贴图/动画/GameObject 等）。/ Whitelisted objects (any type: meshes/materials/textures/animations/GameObjects...).")]
        public List<Object> targets = new List<Object>();
    }
}
