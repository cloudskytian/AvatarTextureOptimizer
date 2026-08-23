// SPDX-License-Identifier: MIT
// EN: Cheap content probing: alpha usage, solid colour detection, per channel variance.
// ZH: 低成本的内容探测：alpha 使用情况、纯色检测、逐通道方差。

using System;
using System.Collections.Generic;
using Net.Fosa.AvatarTextureOptimizer.Editor.Core;
using Net.Fosa.AvatarTextureOptimizer.Editor.Model;
using UnityEditor;
using UnityEngine;

namespace Net.Fosa.AvatarTextureOptimizer.Editor.Textures
{
    /// <summary>
    /// EN: Facts about a texture's pixel content, computed once per texture on a downscaled copy so the
    ///     cost stays negligible even for 8K assets.
    /// ZH: 关于贴图像素内容的事实，每张贴图在缩小副本上计算一次，
    ///     即使 8K 资产开销也可忽略。
    /// </summary>
    public sealed class TextureContentFacts
    {
        /// <summary>EN: True when any texel has alpha below 1. ZH: 存在 alpha 小于 1 的像素时为 true。</summary>
        public bool HasAlpha;
        /// <summary>EN: True when the whole texture is one colour. ZH: 整张贴图为单一颜色时为 true。</summary>
        public bool IsSolid;
        /// <summary>EN: The colour when <see cref="IsSolid"/>. ZH: <see cref="IsSolid"/> 时的颜色。</summary>
        public Color SolidColor;
        /// <summary>EN: RGBA bit mask of channels that are not constant. ZH: 非恒定通道的 RGBA 位掩码。</summary>
        public int VaryingChannelMask;
        /// <summary>EN: True when R, G and B are identical everywhere. ZH: R、G、B 处处相同时为 true。</summary>
        public bool IsMonochrome;
    }

    /// <summary>
    /// EN: Probes texture content and import settings without changing anything on disk.
    /// ZH: 在不改动硬盘任何内容的前提下探测贴图内容与导入设置。
    /// </summary>
    public static class TextureProbe
    {
        private const string Stage = "Probe";
        private const int ProbeSize = 256;

        /// <summary>
        /// EN: Computes content facts by downsampling to at most 256x256 and inspecting the result.
        ///     Solid colour detection uses the full resolution min/max of a two level reduction, which is
        ///     exact for constant images and conservative otherwise.
        /// ZH: 通过降采样到最大 256x256 并检查结果来计算内容事实。
        ///     纯色检测使用两级归约的全分辨率最小/最大值，对常量图像是精确的，其他情况偏保守。
        /// </summary>
        public static TextureContentFacts Probe(Texture2D texture)
        {
            var facts = new TextureContentFacts();
            int w = Mathf.Min(ProbeSize, Mathf.Max(1, texture.width));
            int h = Mathf.Min(ProbeSize, Mathf.Max(1, texture.height));

            RenderTexture full = null, small = null;
            LinearImage image = null;
            try
            {
                full = GpuTextureUtil.ToLinearRT(texture);
                small = GpuTextureUtil.Downsample(full, new RectInt(0, 0, full.width, full.height), new Vector2Int(w, h), false);
                image = GpuTextureUtil.Readback(small, new RectInt(0, 0, w, h));

                var first = image.Pixels[0];
                float minR = 1e9f, maxR = -1e9f, minG = 1e9f, maxG = -1e9f, minB = 1e9f, maxB = -1e9f, minA = 1e9f, maxA = -1e9f;
                bool mono = true;
                for (int i = 0; i < image.Pixels.Length; i++)
                {
                    var c = image.Pixels[i];
                    minR = Mathf.Min(minR, c.r); maxR = Mathf.Max(maxR, c.r);
                    minG = Mathf.Min(minG, c.g); maxG = Mathf.Max(maxG, c.g);
                    minB = Mathf.Min(minB, c.b); maxB = Mathf.Max(maxB, c.b);
                    minA = Mathf.Min(minA, c.a); maxA = Mathf.Max(maxA, c.a);
                    if (mono && (Mathf.Abs(c.r - c.g) > 1e-4f || Mathf.Abs(c.g - c.b) > 1e-4f)) mono = false;
                }

                const float eps = 1.5e-3f; // EN: about 0.4/255 / ZH: 约 0.4/255
                facts.HasAlpha = minA < 1f - eps;
                facts.IsMonochrome = mono;
                facts.VaryingChannelMask =
                    (maxR - minR > eps ? 1 : 0) |
                    (maxG - minG > eps ? 2 : 0) |
                    (maxB - minB > eps ? 4 : 0) |
                    (maxA - minA > eps ? 8 : 0);
                facts.IsSolid = facts.VaryingChannelMask == 0;
                facts.SolidColor = first;
            }
            catch (Exception e)
            {
                AtoLog.Warning(Stage, $"probe failed for '{texture.name}': {e.Message}");
            }
            finally
            {
                image?.Dispose();
                GpuTextureUtil.Release(small);
                GpuTextureUtil.Release(full);
            }
            return facts;
        }

        /// <summary>
        /// EN: Reads the import settings that must be preserved on the generated atlas.
        /// ZH: 读取生成图集时必须保留的导入设置。
        /// </summary>
        public static void ReadImportSettings(TextureEntry entry)
        {
            var tex = entry.Texture;
            entry.FilterMode = tex.filterMode;
            entry.AnisoLevel = tex.anisoLevel;
            entry.WrapMode = tex.wrapMode;
            entry.HasMipmaps = tex.mipmapCount > 1;
            entry.SRgb = true;

            var path = AssetDatabase.GetAssetPath(tex);
            if (!string.IsNullOrEmpty(path) && AssetImporter.GetAtPath(path) is TextureImporter ti)
            {
                entry.SRgb = ti.sRGBTexture && ti.textureType != TextureImporterType.NormalMap;
                entry.HasMipmaps = ti.mipmapEnabled;
            }
            else
            {
                entry.SRgb = entry.Kind == AtoTextureKind.ColorOpaque || entry.Kind == AtoTextureKind.ColorAlpha;
            }
        }

        /// <summary>
        /// EN: A stable signature of the import settings. Two textures with different signatures are
        ///     never considered duplicates, exactly as the specification requires.
        /// ZH: 导入设置的稳定签名。签名不同的两张贴图永远不会被判定为重复，
        ///     这正是规格的要求。
        /// </summary>
        public static string ImportSignature(Texture2D tex)
        {
            var path = AssetDatabase.GetAssetPath(tex);
            var ti = string.IsNullOrEmpty(path) ? null : AssetImporter.GetAtPath(path) as TextureImporter;
            if (ti == null)
                return $"rt|{tex.width}x{tex.height}|{tex.format}|{tex.filterMode}|{tex.wrapMode}|{tex.anisoLevel}|{tex.mipmapCount > 1}";

            return string.Join("|",
                ti.textureType, ti.sRGBTexture, ti.alphaSource, ti.alphaIsTransparency,
                ti.mipmapEnabled, ti.streamingMipmaps, ti.filterMode, ti.wrapMode, ti.anisoLevel,
                ti.npotScale, ti.isReadable, tex.width, tex.height);
        }
    }
}
