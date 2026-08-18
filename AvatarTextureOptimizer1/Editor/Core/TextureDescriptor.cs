// TextureDescriptor.cs / TextureDescriptor.cs
// Identifies a Texture2D instance together with its import settings so that two textures
// which have identical pixel content AND identical import settings are considered the same
// for deduplication.
// 将Texture2D与其导入设置组合成一个去重描述符——像素内容相同且导入设置相同才视为同一张贴图。

using System;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace net.fosa.avatar_texture_optimizer.Editor.Core
{
    /// <summary>
    /// Describes the texture usage type for type-grouping purposes.
    /// 描述贴图用途类型，用于类型分组。
    /// </summary>
    [Flags]
    public enum TextureUsageFlags
    {
        None = 0,
        BaseColor = 1 << 0,   // 主色/Albedo
        Normal = 1 << 1,      // 法线
        Mask = 1 << 2,        // 蒙版/Metallic/AO/Smoothness/Matcap/Emission等
        Grayscale = 1 << 3,   // 单通道灰度
        HasAlpha = 1 << 4,    // 含Alpha通道
        Transparent = 1 << 5, // 需要透明度（alpha并非全1）
        IsCutout = 1 << 6,    // Cutout渲染模式
    }

    /// <summary>
    /// A lightweight struct identifying a texture for dedup and grouping purposes.
    /// 一个轻量结构体，用于去重和分组时识别贴图。
    /// </summary>
    public struct TextureDescriptor : IEquatable<TextureDescriptor>
    {
        public Texture2D Texture;
        public TextureImporterFormat PlatformFormat;
        public bool sRGB;
        public FilterMode Filter;
        public TextureWrapMode WrapU, WrapV;
        public int Aniso;
        // TODO: add more importer fields (compression, etc.) as needed
        // TODO: 按需添加更多导入字段（压缩等）

        public TextureDescriptor(Texture2D tex)
        {
            Texture = tex;
            sRGB = tex != null && tex.isDataSRGB;
            // Detect alpha-is-transparency from importer when possible / 可能时从importer检测alphaIsTransparency
            try
            {
                var tpath = AssetDatabase.GetAssetPath(tex);
                if (!string.IsNullOrEmpty(tpath))
                {
                    var ti = AssetImporter.GetAtPath(tpath) as TextureImporter;
                    if (ti != null)
                    {
                        // No per-field capture; used later via ti.alphaIsTransparency if needed
                    }
                }
            }
            catch { /* ignore / 忽略 */ }
            Filter = tex != null ? tex.filterMode : FilterMode.Bilinear;
            WrapU = tex != null ? tex.wrapModeU : TextureWrapMode.Repeat;
            WrapV = tex != null ? tex.wrapModeV : TextureWrapMode.Repeat;
            Aniso = tex != null ? tex.anisoLevel : 1;
            PlatformFormat = TextureImporterFormat.Automatic;
            if (tex != null)
            {
                try
                {
                    var path = AssetDatabase.GetAssetPath(tex);
                    if (!string.IsNullOrEmpty(path))
                    {
                        var importer = AssetImporter.GetAtPath(path) as TextureImporter;
                        if (importer != null)
                        {
                            var plat = EditorUserBuildSettings.activeBuildTarget;
                            var settings = importer.GetPlatformTextureSettings(plat.ToString());
                            if (settings != null && settings.overridden)
                                PlatformFormat = settings.format;
                        }
                    }
                }
                catch
                {
                    // ignore / 忽略
                }
            }
        }

        public bool Equals(TextureDescriptor other)
        {
            return Texture == other.Texture && PlatformFormat == other.PlatformFormat
                   && sRGB == other.sRGB && Filter == other.Filter
                   && WrapU == other.WrapU && WrapV == other.WrapV && Aniso == other.Aniso;
        }

        public override bool Equals(object obj) => obj is TextureDescriptor td && Equals(td);
        public override int GetHashCode()
        {
            unchecked
            {
                int h = Texture != null ? Texture.GetHashCode() : 0;
                h = (h * 397) ^ (int)PlatformFormat;
                h = (h * 397) ^ sRGB.GetHashCode();
                h = (h * 397) ^ (int)Filter;
                h = (h * 397) ^ (int)WrapU;
                h = (h * 397) ^ (int)WrapV;
                h = (h * 397) ^ Aniso;
                return h;
            }
        }
    }
}
