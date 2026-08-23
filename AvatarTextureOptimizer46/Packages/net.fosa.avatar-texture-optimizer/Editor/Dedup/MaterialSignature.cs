// SPDX-License-Identifier: MIT
// EN: A stable, content based signature for a material.
// ZH: 材质的稳定的、基于内容的签名。

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;

namespace Net.Fosa.AvatarTextureOptimizer.Editor.Dedup
{
    /// <summary>
    /// EN: Computes a signature that is equal for two materials exactly when they would render
    ///     identically. Everything that can affect rendering is included: the shader, every declared
    ///     property, the keyword set, the render queue and the global illumination flags.
    /// ZH: 计算一个签名，两个材质当且仅当渲染结果完全相同时签名才相等。
    ///     一切会影响渲染的内容都被纳入：着色器、每一个声明的属性、关键字集合、
    ///     渲染队列以及全局光照标志。
    /// </summary>
    public static class MaterialSignature
    {
        /// <summary>
        /// EN: Builds the signature. Texture references are resolved through
        ///     <paramref name="textureCanonical"/> first, so two materials that only differ by pointing at
        ///     two byte-identical atlases are still recognised as duplicates.
        /// ZH: 构建签名。贴图引用会先经 <paramref name="textureCanonical"/> 解析，
        ///     因此两个仅仅指向两张逐字节相同图集的材质仍会被识别为重复。
        /// </summary>
        public static string Compute(Material material, Func<Texture, Texture> textureCanonical = null)
        {
            if (material == null) return "<null>";
            var shader = material.shader;
            var sb = new StringBuilder(512);
            sb.Append("shader=").Append(shader != null ? shader.name : "<null>").Append('\n');
            sb.Append("queue=").Append(material.renderQueue.ToString(CultureInfo.InvariantCulture)).Append('\n');
            sb.Append("gi=").Append(((int)material.globalIlluminationFlags).ToString(CultureInfo.InvariantCulture)).Append('\n');
            sb.Append("doubleSidedGI=").Append(material.doubleSidedGI ? '1' : '0').Append('\n');
            sb.Append("enableInstancing=").Append(material.enableInstancing ? '1' : '0').Append('\n');

            // EN: Keywords are unordered; sort them so the signature is stable.
            // ZH: 关键字无序；排序以保证签名稳定。
            var keywords = material.shaderKeywords ?? Array.Empty<string>();
            sb.Append("keywords=").Append(string.Join(",", keywords.OrderBy(k => k, StringComparer.Ordinal))).Append('\n');

            // EN: Shader tags that ATO or other tools may rely on.
            // ZH: ATO 或其他工具可能依赖的着色器标签。
            foreach (var tag in new[] { "RenderType", "Queue", "VRCFallback", "IgnoreProjector" })
                sb.Append("tag:").Append(tag).Append('=').Append(material.GetTag(tag, false, "")).Append('\n');

            if (shader == null) return sb.ToString();

            int count = shader.GetPropertyCount();
            var lines = new List<string>(count);
            for (int i = 0; i < count; i++)
            {
                var name = shader.GetPropertyName(i);
                switch (shader.GetPropertyType(i))
                {
                    case ShaderPropertyType.Color:
                        lines.Add($"{name}=C{Fmt(material.GetColor(name))}");
                        break;
                    case ShaderPropertyType.Vector:
                        lines.Add($"{name}=V{Fmt(material.GetVector(name))}");
                        break;
                    case ShaderPropertyType.Float:
                    case ShaderPropertyType.Range:
                        lines.Add($"{name}=F{material.GetFloat(name).ToString("R", CultureInfo.InvariantCulture)}");
                        break;
                    case ShaderPropertyType.Int:
                        lines.Add($"{name}=I{material.GetInt(name).ToString(CultureInfo.InvariantCulture)}");
                        break;
                    case ShaderPropertyType.Texture:
                        var tex = material.GetTexture(name);
                        if (textureCanonical != null) tex = textureCanonical(tex);
                        var id = tex == null ? "null" : tex.GetInstanceID().ToString(CultureInfo.InvariantCulture);
                        var scale = material.GetTextureScale(name);
                        var offset = material.GetTextureOffset(name);
                        lines.Add($"{name}=T{id}|{Fmt(scale)}|{Fmt(offset)}");
                        break;
                }
            }

            // EN: Property order is shader defined and therefore already stable, but sorting costs
            //     nothing and protects against shader variant reordering.
            // ZH: 属性顺序由着色器定义、本已稳定，但排序几乎没有代价，
            //     且能防止着色器变体重排带来的影响。
            lines.Sort(StringComparer.Ordinal);
            foreach (var l in lines) sb.Append(l).Append('\n');

            return sb.ToString();
        }

        private static string Fmt(Color c)
            => $"{c.r:R},{c.g:R},{c.b:R},{c.a:R}";

        private static string Fmt(Vector4 v)
            => $"{v.x:R},{v.y:R},{v.z:R},{v.w:R}";

        private static string Fmt(Vector2 v)
            => $"{v.x:R},{v.y:R}";
    }
}
