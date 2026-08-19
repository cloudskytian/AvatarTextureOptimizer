using System.Collections.Generic;
using UnityEngine;

namespace AvatarTextureOptimizer
{
    /// <summary>
    /// Whitelist component. All textures referenced by the listed objects (any type: mesh, material,
    /// texture, animation, ...) skip every optimization step (including atlas generation and later
    /// parameter optimizations). Textures sharing the same UV are also excluded from atlasing.
    /// 白名单组件。列出的对象（任意类型：网格、材质、贴图、动画等）引用的全部贴图跳过所有优化
    /// （包括图集化与后续参数优化）；同 UV 的其他贴图也跳过图集化。
    /// </summary>
    [AddComponentMenu("Avatar Texture Optimizer/Texture Whitelist")]
    [DisallowMultipleComponent]
    public sealed class TextureWhitelist : MonoBehaviour
    {
        [Tooltip("Objects whose referenced textures are whitelisted. / 其引用贴图进入白名单的对象。")]
        public List<Object> objects = new List<Object>();

        [Tooltip("Also whitelist textures referenced by children of this GameObject. / 同时白名单化本物体子级引用的贴图。")]
        public bool includeChildren = false;
    }
}
