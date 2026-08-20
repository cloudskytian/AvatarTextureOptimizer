using System.Linq;
using UnityEngine;

namespace net.fosa.avatar_texture_optimizer.editor
{
    /// <summary>
    /// Material equality: same shader, same keywords, same render queue, same values for every
    /// shader property (colors/vectors/floats/ints/textures). Only content and parameters are
    /// compared — object identity is irrelevant for deduplication. /
    /// 材质相等性：相同 shader、相同关键字、相同渲染队列、每个着色器属性值相同（颜色/向量/浮点/整数/贴图）。
    /// 只比较内容与参数 —— 对象身份与去重无关。
    /// </summary>
    internal static class MaterialComparer
    {
        public static bool Equals(Material a, Material b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a == null || b == null) return false;
            if (a.shader != b.shader) return false;
            if (a.renderQueue != b.renderQueue) return false;

            // Keywords. / 关键字。
            var keywordsA = a.enabledKeywords.Select(k => k.name).OrderBy(n => n).ToArray();
            var keywordsB = b.enabledKeywords.Select(k => k.name).OrderBy(n => n).ToArray();
            if (!keywordsA.SequenceEqual(keywordsB)) return false;

            var shader = a.shader;
            for (var i = 0; i < shader.GetPropertyCount(); i++)
            {
                var name = shader.GetPropertyName(i);
                var type = shader.GetPropertyType(i);
                switch (type)
                {
                    case ShaderPropertyType.Color:
                        if (a.GetColor(name) != b.GetColor(name)) return false;
                        break;
                    case ShaderPropertyType.Vector:
                        if (a.GetVector(name) != b.GetVector(name)) return false;
                        break;
                    case ShaderPropertyType.Float:
                    case ShaderPropertyType.Range:
                        if (!Mathf.Approximately(a.GetFloat(name), b.GetFloat(name))) return false;
                        break;
                    case ShaderPropertyType.Int:
                        if (a.GetInt(name) != b.GetInt(name)) return false;
                        break;
                    case ShaderPropertyType.Texture:
                        if (a.GetTexture(name) != b.GetTexture(name)) return false;
                        break;
                }
            }
            return true;
        }

        /// <summary>
        /// Whether the material renders opaque (no cutout, no transparency). / 材质是否为不透明渲染（无 cutout、无透明）。
        /// </summary>
        public static bool IsOpaque(Material material)
        {
            var renderType = material.GetTag("RenderType", false, "");
            if (renderType == "Transparent" || renderType == "TransparentCutout") return false;
            return material.renderQueue < 3000;
        }
    }
}
