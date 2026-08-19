// ATO Runtime API - Public interfaces accessible from runtime scripts
// ATO运行时API - 从运行时脚本可访问的公共接口

using UnityEngine;

namespace net.fosa.avatar_texture_optimizer.Runtime.API
{
    /// <summary>
    /// Public API for querying ATO optimization status at runtime.
    /// 用于在运行时查询ATO优化状态的公共API。
    /// </summary>
    public static class ATOApi
    {
        /// <summary>
        /// Check if ATO has processed a specific avatar.
        /// 检查ATO是否已处理特定Avatar。
        /// </summary>
        public static bool IsAvatarOptimized(GameObject avatarRoot)
        {
            // ATO removes its component after build, so check for ATO-generated assets
            if (avatarRoot == null) return false;

            var renderers = avatarRoot.GetComponentsInChildren<Renderer>(true);
            foreach (var r in renderers)
            {
                if (r.sharedMaterials != null)
                {
                    foreach (var mat in r.sharedMaterials)
                    {
                        if (mat == null) continue;
                        var shader = mat.shader;
                        if (shader == null) continue;

                        int propCount = shader.GetPropertyCount();
                        for (int i = 0; i < propCount; i++)
                        {
                            if (shader.GetPropertyType(i) == UnityEngine.Rendering.ShaderPropertyType.Texture)
                            {
                                var tex = mat.GetTexture(shader.GetPropertyName(i));
                                if (tex != null && tex.name.StartsWith("ATO_"))
                                    return true;
                            }
                        }
                    }
                }
            }

            return false;
        }
    }
}
