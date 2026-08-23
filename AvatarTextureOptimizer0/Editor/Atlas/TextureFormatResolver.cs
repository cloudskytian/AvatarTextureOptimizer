using System;
using Fosa.AvatarTextureOptimizer.Editor.Analysis;
using Fosa.AvatarTextureOptimizer.Editor.Pipeline;
using Fosa.AvatarTextureOptimizer.Editor.Quality;
using UnityEditor;
using UnityEditor.Build;
using UnityEngine;

namespace Fosa.AvatarTextureOptimizer.Editor.Atlas
{
    internal static class TextureFormatResolver
    {
        public static TextureFormat Resolve(TextureTypeKey key, ATOOptimizationSettings settings)
        {
            // Target quality 1 promises an exact-size, near-lossless path; do not introduce block-compression loss.
            if (settings.EffectiveQuality.IsLosslessBypass) return TextureFormat.RGBA32;
            var configured = ClassSettings(key.Kind, settings).compression;
            var mobile = EditorUserBuildSettings.activeBuildTarget == BuildTarget.Android ||
                         EditorUserBuildSettings.activeBuildTarget == BuildTarget.iOS;
            if (configured == ATOCompression.Auto)
            {
                if (mobile) configured = key.Kind == ATOTextureKind.ColorOpaque ? ATOCompression.ASTC6x6 : ATOCompression.ASTC4x4;
                else configured = key.Kind == ATOTextureKind.Normal ? ATOCompression.BC5 : ATOCompression.BC7;
            }
            if (mobile && (configured == ATOCompression.BC7 || configured == ATOCompression.BC5 ||
                           configured == ATOCompression.DXT1 || configured == ATOCompression.DXT5))
                configured = key.Kind == ATOTextureKind.ColorOpaque ? ATOCompression.ASTC6x6 : ATOCompression.ASTC4x4;
            if (!mobile && (configured == ATOCompression.ETC2RGB || configured == ATOCompression.ETC2RGBA8 ||
                            configured == ATOCompression.ASTC4x4 || configured == ATOCompression.ASTC6x6))
                configured = key.Kind == ATOTextureKind.Normal ? ATOCompression.BC5 : ATOCompression.BC7;
            var losesBlue = configured == ATOCompression.BC5;
            var losesAlpha = losesBlue || configured == ATOCompression.DXT1 || configured == ATOCompression.ETC2RGB ||
                             configured == ATOCompression.UncompressedRGB24;
            if (key.Kind == ATOTextureKind.ColorOpaque && losesBlue)
                configured = mobile ? ATOCompression.ASTC6x6 : ATOCompression.BC7;
            if ((key.Kind == ATOTextureKind.ColorAlpha || key.Kind == ATOTextureKind.ColorRgbaData) && losesAlpha)
                configured = configured == ATOCompression.UncompressedRGB24
                    ? ATOCompression.UncompressedRGBA32
                    : mobile ? ATOCompression.ETC2RGBA8 : ATOCompression.DXT5;
            if (key.Kind == ATOTextureKind.Grayscale && losesAlpha)
                configured = configured == ATOCompression.UncompressedRGB24
                    ? ATOCompression.UncompressedRGBA32
                    : mobile ? ATOCompression.ASTC4x4 : ATOCompression.BC7;
            // Mobile DXT5nm-style decoding reads X from alpha. Never select an alpha-less output there.
            if (key.Kind == ATOTextureKind.Normal && mobile && losesAlpha &&
                ActiveMobileNormalEncoding() == NormalMapEncoding.DXT5nm)
                configured = configured == ATOCompression.UncompressedRGB24
                    ? ATOCompression.UncompressedRGBA32
                    : configured == ATOCompression.ETC2RGB
                        ? ATOCompression.ETC2RGBA8
                        : ATOCompression.ASTC4x4;

            switch (configured)
            {
                case ATOCompression.UncompressedRGB24: return TextureFormat.RGB24;
                case ATOCompression.BC7: return TextureFormat.BC7;
                case ATOCompression.BC5: return TextureFormat.BC5;
                case ATOCompression.DXT1: return TextureFormat.DXT1;
                case ATOCompression.DXT5: return TextureFormat.DXT5;
                case ATOCompression.ETC2RGB: return TextureFormat.ETC2_RGB;
                case ATOCompression.ETC2RGBA8: return TextureFormat.ETC2_RGBA8;
                case ATOCompression.ASTC4x4: return TextureFormat.ASTC_4x4;
                case ATOCompression.ASTC6x6: return TextureFormat.ASTC_6x6;
                default: return TextureFormat.RGBA32;
            }
        }

        /// <summary>
        /// Builds every RGBA32 mip in linear premultiplied-alpha space, then stores straight RGB.
        /// This avoids Unity's straight-alpha automatic mip filtering and preserves an extrapolated
        /// hidden RGB color for fully transparent texels. / 在线性预乘 Alpha 空间生成全部 mip。
        /// </summary>
        public static void BuildPremultipliedAlphaMipChain(Texture2D texture, bool srgb)
        {
            if (texture == null || texture.format != TextureFormat.RGBA32 || texture.mipmapCount <= 1) return;
            for (var mip = 1; mip < texture.mipmapCount; mip++)
            {
                var source = texture.GetPixelData<Color32>(mip - 1);
                var destination = texture.GetPixelData<Color32>(mip);
                var sourceWidth = Mathf.Max(1, texture.width >> (mip - 1));
                var sourceHeight = Mathf.Max(1, texture.height >> (mip - 1));
                var destinationWidth = Mathf.Max(1, texture.width >> mip);
                var destinationHeight = Mathf.Max(1, texture.height >> mip);
                for (var y = 0; y < destinationHeight; y++)
                {
                    if ((y & 255) == 0) ATOProgress.Checkpoint("Building premultiplied alpha mip " + mip);
                    for (var x = 0; x < destinationWidth; x++)
                        destination[y * destinationWidth + x] = PremultipliedBox(source, sourceWidth, sourceHeight,
                            x, y, destinationWidth, destinationHeight, srgb);
                }
            }
            texture.Apply(false, false);
        }

        private static Color32 PremultipliedBox(Unity.Collections.NativeArray<Color32> source,
            int sourceWidth, int sourceHeight, int destinationX, int destinationY,
            int destinationWidth, int destinationHeight, bool srgb)
        {
            var x0 = (float)destinationX * sourceWidth / destinationWidth;
            var x1 = (float)(destinationX + 1) * sourceWidth / destinationWidth;
            var y0 = (float)destinationY * sourceHeight / destinationHeight;
            var y1 = (float)(destinationY + 1) * sourceHeight / destinationHeight;
            var firstX = Mathf.FloorToInt(x0); var lastX = Mathf.CeilToInt(x1);
            var firstY = Mathf.FloorToInt(y0); var lastY = Mathf.CeilToInt(y1);
            var premultiplied = Vector3.zero; var hidden = Vector3.zero;
            var alphaSum = 0f; var area = 0f;
            for (var sourceY = firstY; sourceY < lastY; sourceY++)
            for (var sourceX = firstX; sourceX < lastX; sourceX++)
            {
                var weightX = Mathf.Max(0f, Mathf.Min(x1, sourceX + 1f) - Mathf.Max(x0, sourceX));
                var weightY = Mathf.Max(0f, Mathf.Min(y1, sourceY + 1f) - Mathf.Max(y0, sourceY));
                var weight = weightX * weightY;
                if (weight <= 0f) continue;
                var pixel = source[Mathf.Clamp(sourceY, 0, sourceHeight - 1) * sourceWidth +
                                   Mathf.Clamp(sourceX, 0, sourceWidth - 1)];
                var rgb = new Vector3(Decode(pixel.r, srgb), Decode(pixel.g, srgb), Decode(pixel.b, srgb));
                var alpha = pixel.a / 255f;
                premultiplied += rgb * (alpha * weight);
                hidden += rgb * weight;
                alphaSum += alpha * weight; area += weight;
            }
            if (area <= 1e-12f) return new Color32(0, 0, 0, 0);
            var outputAlpha = Mathf.Clamp01(alphaSum / area);
            var outputRgb = alphaSum > 1e-12f ? premultiplied / alphaSum : hidden / area;
            return new Color32(Encode(outputRgb.x, srgb), Encode(outputRgb.y, srgb), Encode(outputRgb.z, srgb),
                (byte)Mathf.Clamp(Mathf.RoundToInt(outputAlpha * 255f), 0, 255));
        }

        private static float Decode(byte value, bool srgb)
        {
            var encoded = value / 255f;
            if (!srgb) return encoded;
            return encoded <= 0.04045f ? encoded / 12.92f : Mathf.Pow((encoded + 0.055f) / 1.055f, 2.4f);
        }

        private static byte Encode(float value, bool srgb)
        {
            value = Mathf.Clamp01(value);
            var encoded = !srgb ? value : value <= 0.0031308f
                ? value * 12.92f
                : 1.055f * Mathf.Pow(value, 1f / 2.4f) - 0.055f;
            return (byte)Mathf.Clamp(Mathf.RoundToInt(encoded * 255f), 0, 255);
        }

        public static ATONormalInputEncoding NormalStorageEncoding(TextureFormat outputFormat)
        {
            var target = EditorUserBuildSettings.activeBuildTarget;
            var group = BuildPipeline.GetBuildTargetGroup(target);
            if (group == BuildTargetGroup.Standalone)
            {
                // BC5 has no alpha payload and therefore requires RG. DXT5 preserves X more accurately in A.
                return outputFormat == TextureFormat.DXT5
                    ? ATONormalInputEncoding.EncodedAg
                    : ATONormalInputEncoding.EncodedRgOrAg;
            }
            if (target != BuildTarget.Android && target != BuildTarget.iOS)
                throw new NotSupportedException("ATO normal output is only verified for PC, Android, and iOS build targets.");
            var encoding = ActiveMobileNormalEncoding();
            switch (encoding)
            {
                case NormalMapEncoding.XYZ: return ATONormalInputEncoding.EncodedRgb;
                case NormalMapEncoding.DXT5nm: return ATONormalInputEncoding.EncodedAg;
                default: throw new NotSupportedException("ATO does not recognize the active target's normal-map encoding.");
            }
        }

        private static NormalMapEncoding ActiveMobileNormalEncoding()
        {
            var group = BuildPipeline.GetBuildTargetGroup(EditorUserBuildSettings.activeBuildTarget);
            return PlayerSettings.GetNormalMapEncoding(NamedBuildTarget.FromBuildTargetGroup(group));
        }

        /// <summary>
        /// Normalizes every mip and writes the channel layout consumed by Unity's platform normal decoder.
        /// PC uses RG-or-AG, mobile XYZ uses RGB, and mobile DXT5nm/ASTC uses AG.
        /// / 归一化每级 mip，并按目标平台写入 RGB、RG/AG 或 AG 通道布局。
        /// </summary>
        public static void EncodeNormalMipChain(Texture2D texture, ATONormalInputEncoding encoding)
        {
            if (texture == null) return;
            if (encoding != ATONormalInputEncoding.EncodedRgb &&
                encoding != ATONormalInputEncoding.EncodedRgOrAg &&
                encoding != ATONormalInputEncoding.EncodedAg)
                throw new ArgumentOutOfRangeException(nameof(encoding));
            for (var mip = 0; mip < texture.mipmapCount; mip++)
            {
                var pixels = texture.GetPixels(mip);
                for (var index = 0; index < pixels.Length; index++)
                {
                    if ((index & 65535) == 0) ATOProgress.Checkpoint("Encoding normal mip " + mip);
                    var color = pixels[index];
                    var normal = new Vector3(color.r * 2f - 1f, color.g * 2f - 1f, color.b * 2f - 1f);
                    normal = normal.sqrMagnitude > 1e-12f ? normal.normalized : Vector3.forward;
                    var x = normal.x * 0.5f + 0.5f;
                    var y = normal.y * 0.5f + 0.5f;
                    var z = normal.z * 0.5f + 0.5f;
                    // R=1 makes the AG layout valid both for UNITY_ASTC_NORMALMAP_ENCODING (direct AG)
                    // and the classic RG-or-AG decoder (A *= R). B is unused by both paths.
                    pixels[index] = encoding == ATONormalInputEncoding.EncodedAg
                        ? new Color(1f, y, 1f, x)
                        : new Color(x, y, z, 1f);
                }
                texture.SetPixels(pixels, mip);
            }
            texture.Apply(false, false);
        }

        public static ATOTextureClassSettings ClassSettings(ATOTextureKind kind, ATOOptimizationSettings settings)
        {
            if (kind == ATOTextureKind.Normal) return settings.normal;
            if (kind == ATOTextureKind.Grayscale) return settings.grayscale;
            if (kind == ATOTextureKind.ColorAlpha || kind == ATOTextureKind.ColorRgbaData) return settings.alpha;
            return settings.opaque;
        }
    }
}
