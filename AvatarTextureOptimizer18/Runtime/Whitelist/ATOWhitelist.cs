using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Fosa.AvatarTextureOptimizer
{
    // 白名单组件：列表中的对象（不限类型：网格、材质、贴图、动画、物体等）引用的全部贴图跳过所有优化。
    // Whitelist component: every texture referenced by listed objects (any type: meshes, materials, textures,
    // animations, GameObjects, ...) skips all optimization, including import-parameter optimization.
    // 挂载在 Avatar 层级任意位置均可；允许存在多个实例。
    // It can be placed anywhere under the avatar; multiple instances are allowed.
    [AddComponentMenu("VRChat SDK/ATO Whitelist")]
    [HelpURL("https://github.com/fosa/AvatarTextureOptimizer")]
    public sealed class ATOWhitelist : MonoBehaviour
    {
        // 白名单对象列表（不限制类型）。Whitelisted objects (any type).
        public List<Object> objects = new List<Object>();

        public bool Contains(Object obj)
        {
            if (obj == null) return false;
            if (objects == null) return false;
            foreach (var o in objects)
            {
                if (o == obj) return true;
            }
            return false;
        }

        // 收集 Avatar 层级下所有白名单组件。Collects every whitelist component under the avatar.
        public static ATOWhitelist[] Collect(GameObject avatarRoot)
        {
            if (avatarRoot == null) return new ATOWhitelist[0];
            return avatarRoot.GetComponentsInChildren<ATOWhitelist>(true);
        }

        // 某对象是否被任意白名单组件直接列出。Whether an object is directly listed in any whitelist component.
        public static bool IsDirectlyListed(GameObject avatarRoot, Object obj)
        {
            foreach (var w in Collect(avatarRoot))
            {
                if (w.Contains(obj)) return true;
            }
            return false;
        }
    }
}
