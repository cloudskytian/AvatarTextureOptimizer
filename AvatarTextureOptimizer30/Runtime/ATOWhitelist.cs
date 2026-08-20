// ATOWhitelist.cs — 白名单组件（挂在对象上）/ Whitelist component (placed on scene objects).
// 说明：白名单不限制对象类型。挂在任意 GameObject 上时，该对象及其整个子级内引用的
// 全部贴图（网格、材质、贴图、动画等引用的贴图）都跳过所有优化（含后续参数优化）。
// Note: the whitelist is type-agnostic. When placed on any GameObject, every texture referenced
// by anything inside that object and its subtree (meshes, materials, textures, animations, etc.)
// skips ALL optimizations (including later parameter optimizations).

using UnityEngine;

namespace Fosa.AvatarTextureOptimizer
{
    /// <summary>
    /// 白名单标记：挂载对象及其子树内引用的全部贴图跳过所有优化。
    /// Whitelist marker: textures referenced by this object and its subtree skip all optimizations.
    /// </summary>
    [AddComponentMenu("ATO/Whitelist (Object)", 100)]
    [DisallowMultipleComponent]
    public sealed class ATOWhitelist : MonoBehaviour
    {
        [Tooltip("勾选后同时白名单整个子树；取消则仅白名单该对象自身。/ When checked, whitelists the whole subtree; otherwise only this object itself.")]
        public bool includeChildren = true;
    }
}
