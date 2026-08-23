// SPDX-License-Identifier: MIT
// EN: Helpers shared by all shader analyzers.
// ZH: 所有着色器分析器共用的辅助方法。

using System;
using System.Collections.Generic;
using Net.Fosa.AvatarTextureOptimizer.Api;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Net.Fosa.AvatarTextureOptimizer.Editor.Analysis
{
    /// <summary>
    /// EN: Utility routines for inspecting shaders and materials in a version tolerant way.
    /// ZH: 以版本容忍的方式检查着色器与材质的工具方法。
    /// </summary>
    public static class ShaderAnalysisUtil
    {
        /// <summary>
        /// EN: Returns every texture property name declared by the shader.
        /// ZH: 返回着色器声明的全部贴图属性名。
        /// </summary>
        public static List<string> GetTextureProperties(Shader shader)
        {
            var list = new List<string>();
            if (shader == null) return list;
            int count = shader.GetPropertyCount();
            for (int i = 0; i < count; i++)
            {
                if (shader.GetPropertyType(i) != ShaderPropertyType.Texture) continue;
                if (shader.GetPropertyTextureDimension(i) != TextureDimension.Tex2D) continue;
                list.Add(shader.GetPropertyName(i));
            }
            return list;
        }

        /// <summary>
        /// EN: Returns the shader property flags of a named property, or None when not found.
        /// ZH: 返回具名属性的着色器属性标志，未找到时返回 None。
        /// </summary>
        public static ShaderPropertyFlags GetFlags(Shader shader, string propertyName)
        {
            if (shader == null) return ShaderPropertyFlags.None;
            int idx = shader.FindPropertyIndex(propertyName);
            return idx < 0 ? ShaderPropertyFlags.None : shader.GetPropertyFlags(idx);
        }

        /// <summary>
        /// EN: True when the material leaves tiling/offset at identity for this property, or when the
        ///     shader declares [NoScaleOffset] so the value cannot influence sampling at all.
        /// ZH: 当材质将该属性的 tiling/offset 保持为单位值，或着色器声明了 [NoScaleOffset]
        ///     使该值完全无法影响采样时返回 true。
        /// </summary>
        public static bool HasIdentityScaleOffset(Material material, string propertyName)
        {
            var flags = GetFlags(material.shader, propertyName);
            if ((flags & ShaderPropertyFlags.NoScaleOffset) != 0) return true;

            var scale = material.GetTextureScale(propertyName);
            var offset = material.GetTextureOffset(propertyName);
            return Approximately(scale.x, 1f) && Approximately(scale.y, 1f)
                   && Approximately(offset.x, 0f) && Approximately(offset.y, 0f);
        }

        /// <summary>EN: Exact-enough float comparison for UI authored values. ZH: 针对界面编辑值的足够精确的浮点比较。</summary>
        public static bool Approximately(float a, float b) => Mathf.Abs(a - b) < 1e-6f;

        /// <summary>
        /// EN: Reads a float property, returning <paramref name="fallback"/> when it does not exist.
        /// ZH: 读取一个 float 属性，不存在时返回 <paramref name="fallback"/>。
        /// </summary>
        public static float GetFloat(Material m, string name, float fallback)
            => m != null && m.HasProperty(name) ? m.GetFloat(name) : fallback;

        /// <summary>
        /// EN: Reads a vector property, returning <paramref name="fallback"/> when it does not exist.
        /// ZH: 读取一个 vector 属性，不存在时返回 <paramref name="fallback"/>。
        /// </summary>
        public static Vector4 GetVector(Material m, string name, Vector4 fallback)
            => m != null && m.HasProperty(name) ? m.GetVector(name) : fallback;

        /// <summary>
        /// EN: Derives the alpha handling of a material from the standard <c>RenderType</c> tag, falling
        ///     back to the render queue. Both are conventions every well behaved shader follows.
        /// ZH: 依据标准的 <c>RenderType</c> 标签推导材质的 alpha 处理方式，并回退到渲染队列。
        ///     这两者都是行为良好的着色器普遍遵循的约定。
        /// </summary>
        public static void ResolveAlphaMode(Material m, out AtoAlphaMode mode, out float cutoff)
        {
            cutoff = GetFloat(m, "_Cutoff", 0.5f);
            var renderType = m.GetTag("RenderType", true, "");

            if (renderType.Equals("TransparentCutout", StringComparison.OrdinalIgnoreCase))
            {
                mode = AtoAlphaMode.Cutout;
                return;
            }
            if (renderType.Equals("Transparent", StringComparison.OrdinalIgnoreCase) ||
                renderType.Equals("Fade", StringComparison.OrdinalIgnoreCase))
            {
                mode = AtoAlphaMode.Blend;
                return;
            }
            if (renderType.Equals("Opaque", StringComparison.OrdinalIgnoreCase))
            {
                mode = AtoAlphaMode.Opaque;
                return;
            }

            // EN: No usable tag; use the render queue, which VRChat shaders always set correctly.
            // ZH: 没有可用标签；改用渲染队列，VRChat 着色器总是正确设置该值。
            int q = m.renderQueue;
            if (q >= 2450 && q < 2500) mode = AtoAlphaMode.Cutout;
            else if (q >= 2500) mode = AtoAlphaMode.Blend;
            else mode = AtoAlphaMode.Opaque;
        }

        /// <summary>
        /// EN: Classifies a texture property as normal / grayscale / colour. Uses, in order:
        ///     the <c>[Normal]</c> shader flag, the texture importer type, then the property name.
        ///     The final opaque/alpha split is decided later from actual pixel content.
        /// ZH: 将贴图属性分类为法线/灰度/颜色。依次使用：<c>[Normal]</c> 着色器标志、
        ///     贴图导入器类型、属性名。最终的不透明/带 alpha 划分稍后根据实际像素内容决定。
        /// </summary>
        public static AtoTextureKind ClassifyKind(Shader shader, string propertyName, Texture texture)
        {
            var flags = GetFlags(shader, propertyName);
            if ((flags & ShaderPropertyFlags.Normal) != 0) return AtoTextureKind.Normal;

            var path = AssetDatabase.GetAssetPath(texture);
            if (!string.IsNullOrEmpty(path) && AssetImporter.GetAtPath(path) is TextureImporter ti)
            {
                if (ti.textureType == TextureImporterType.NormalMap) return AtoTextureKind.Normal;
                if (ti.textureType == TextureImporterType.SingleChannel) return AtoTextureKind.Grayscale;
                if (!ti.sRGBTexture) return AtoTextureKind.Grayscale;
            }

            var n = propertyName;
            if (Contains(n, "Bump") || Contains(n, "Normal")) return AtoTextureKind.Normal;
            if (Contains(n, "Mask") || Contains(n, "Smoothness") || Contains(n, "Metallic")
                || Contains(n, "Occlusion") || Contains(n, "Roughness") || Contains(n, "Height")
                || Contains(n, "Detail") && Contains(n, "Mask"))
                return AtoTextureKind.Grayscale;

            return AtoTextureKind.ColorOpaque;
        }

        private static bool Contains(string haystack, string needle)
            => haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
    }
}
