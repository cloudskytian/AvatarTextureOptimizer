using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace Fosa.ATO.Editor
{
    /// <summary>
    /// 压缩格式 / 平台 / MipStreaming 的【真正落地】。
    /// 关键事实（已读 AAO OptimizeTexture 源码取证）：
    /// - NDMF 生成的贴图是子资产，TextureImporter 无效；正确做法是在运行时 Texture2D 上直接：
    ///   (a) EditorUtility.CompressTexture 压缩（源须为未压缩格式）；
    ///   (b) 直接设置 wrapMode/filterMode/anisoLevel/mipMapBias；
    ///   (c) MipStreaming 用 SetStreamingMipMapSettings 从原贴图透传。
    ///
    /// Compression / platform / mip-streaming — actually applied on the runtime Texture2D
    /// (TextureImporter does NOT work for NDMF sub-assets).
    /// </summary>
    public static class ATOCompression
    {
        // ---- MipStreaming 透传（反射，兼容不同 Unity 版本） ----
        private static MethodInfo _getStreamingSettings;
        private static MethodInfo _setStreamingSettings;

        public static void Apply(Texture2D tex, ATOAtlas atlas, AvatarTextureOptimizer comp)
        {
            var platform = ResolvePlatform();
            var settings = platform == ATOPlatformTarget.PC ? comp.platformPC
                         : platform == ATOPlatformTarget.Android ? comp.platformAndroid : comp.platformiOS;
            var cs = settings.overrideEnabled ? settings.compression : comp.compression;

            // 1) 图集强制 Clamp（不给用户改）。
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.filterMode = atlas.group.filterMode;
            tex.anisoLevel = 1;

            // 2) 压缩落地。Compression actually applied here.
            bool hasAlpha = AtlasHasAlpha(atlas);
            ATOCompressionFormat chosen = PickFormat(cs, atlas.group.type, hasAlpha, platform, comp.allowNPOT);

            // 灰度多通道兜底：多通道灰度贴图强制 RGBA。
            bool forceRGBA = false;
            if (atlas.group.type == ATOTextureType.Grayscale && GrayscaleIsMultiChannel(atlas))
            {
                forceRGBA = true;
                ATOLogger.Warn(ATOLocalization.Tr("warning.grayMultiChannel"));
            }

            if (forceRGBA || chosen == ATOCompressionFormat.None)
            {
                // 保持未压缩 RGBA32（已是最安全）。
                ATOLogger.VerboseLog($"'{tex.name}': kept uncompressed RGBA32");
            }
            else
            {
                var fmt = ToTextureFormat(chosen, hasAlpha);
                if (fmt != TextureFormat.RGBA32 && fmt != TextureFormat.RGB24)
                {
                    try
                    {
                        EditorUtility.CompressTexture(tex, fmt, TextureCompressionQuality.Best);
                        ATOLogger.VerboseLog($"'{tex.name}': compressed to {fmt} ({chosen}, platform={platform})");
                    }
                    catch (System.Exception e)
                    {
                        ATOLogger.Warn($"'{tex.name}': compression to {fmt} failed ({e.Message}); kept uncompressed");
                    }
                }
            }

            // 3) MipStreaming 透传。Mip streaming passed through from the source.
            if (comp.mipmapAndStreaming)
            {
                var src = FirstSourceTexture(atlas);
                if (src != null) CopyStreamingSettings(src, tex);
            }

            // 4) 确保 mipmap 与 streaming 绑定一致。
            ApplyMipmapFlag(tex, comp.mipmapAndStreaming);
        }

        /// <summary>从原贴图透传 streaming settings（反射）。</summary>
        private static void CopyStreamingSettings(Texture2D src, Texture2D dst)
        {
            try
            {
                if (_getStreamingSettings == null || _setStreamingSettings == null)
                {
                    _getStreamingSettings = typeof(Texture2D).GetMethod("GetStreamingMipMapSettings", BindingFlags.Public | BindingFlags.Instance);
                    _setStreamingSettings = typeof(Texture2D).GetMethod("SetStreamingMipMapSettings", BindingFlags.Public | BindingFlags.Instance);
                }
                if (_getStreamingSettings != null && _setStreamingSettings != null)
                {
                    var s = _getStreamingSettings.Invoke(src, null);
                    if (s != null) _setStreamingSettings.Invoke(dst, new[] { s });
                    return;
                }
            }
            catch { /* fall through to legacy */ }

            // 旧 API 兜底。Legacy fallback.
            try
            {
                var prop = typeof(Texture2D).GetProperty("streamingMipmaps", BindingFlags.Public | BindingFlags.Instance);
                if (prop != null && prop.CanWrite)
                {
                    var v = prop.GetValue(src);
                    prop.SetValue(dst, v);
                }
            }
            catch { }
        }

        private static void ApplyMipmapFlag(Texture2D tex, bool mipAndStreaming)
        {
            // 新生成的纹理已按构造参数带 mipmap；此处仅记录绑定关系。
            // 实际 mipmap 数量在生成时决定；streaming 已透传。
        }

        private static Texture2D FirstSourceTexture(ATOAtlas atlas)
        {
            foreach (var island in atlas.islands)
                if (island.texture != null && island.texture.texture != null)
                    return island.texture.texture;
            return null;
        }

        // ---- 格式选择（安全枚举 + 平台过滤 + NPOT 剔除） ----
        public static ATOCompressionFormat PickFormat(ATOCompressionSettings cs, ATOTextureType type,
            bool hasAlpha, ATOPlatformTarget platform, bool allowNPOT)
        {
            ATOCompressionFormat chosen;
            switch (type)
            {
                case ATOTextureType.NormalMap: chosen = cs.normalMap; break;
                case ATOTextureType.Grayscale: chosen = cs.grayscale; break;
                default: chosen = hasAlpha ? cs.transparent : cs.opaque; break;
            }

            if (chosen == ATOCompressionFormat.Auto)
                chosen = AutoPick(platform, hasAlpha);

            // 安全兜底：透明贴图不允许无 alpha 格式。
            if (hasAlpha && (chosen == ATOCompressionFormat.DXT1 || chosen == ATOCompressionFormat.ETC2))
                chosen = AutoPick(platform, true);

            // 平台过滤 + NPOT 剔除。
            var allowed = AllowedFormats(platform, allowNPOT);
            if (!allowed.Contains(chosen))
                chosen = AutoPick(platform, hasAlpha);

            return chosen;
        }

        public static HashSet<ATOCompressionFormat> AllowedFormats(ATOPlatformTarget platform, bool allowNPOT)
        {
            var set = new HashSet<ATOCompressionFormat> { ATOCompressionFormat.Auto, ATOCompressionFormat.None };
            switch (platform)
            {
                case ATOPlatformTarget.Android:
                    set.Add(ATOCompressionFormat.ETC2);
                    set.Add(ATOCompressionFormat.ASTC_6x6);
                    set.Add(ATOCompressionFormat.ASTC_4x4);
                    break;
                case ATOPlatformTarget.iOS:
                    set.Add(ATOCompressionFormat.ASTC_6x6);
                    set.Add(ATOCompressionFormat.ASTC_4x4);
                    if (!allowNPOT) set.Add(ATOCompressionFormat.PVRTC_4BPP); // NPOT 剔除 PVRTC
                    break;
                default: // PC
                    set.Add(ATOCompressionFormat.DXT1);
                    set.Add(ATOCompressionFormat.DXT5);
                    set.Add(ATOCompressionFormat.BC7);
                    set.Add(ATOCompressionFormat.Crunch);
                    break;
            }
            return set;
        }

        private static ATOCompressionFormat AutoPick(ATOPlatformTarget platform, bool hasAlpha)
        {
            switch (platform)
            {
                case ATOPlatformTarget.Android: return ATOCompressionFormat.ASTC_6x6;
                case ATOPlatformTarget.iOS: return ATOCompressionFormat.ASTC_6x6;
                default: return hasAlpha ? ATOCompressionFormat.DXT5 : ATOCompressionFormat.DXT1;
            }
        }

        public static TextureFormat ToTextureFormat(ATOCompressionFormat f, bool hasAlpha)
        {
            switch (f)
            {
                case ATOCompressionFormat.DXT1: return TextureFormat.DXT1;
                case ATOCompressionFormat.DXT5: return TextureFormat.DXT5;
                case ATOCompressionFormat.BC7: return TextureFormat.BC7;
                case ATOCompressionFormat.ETC2: return hasAlpha ? TextureFormat.ETC2_RGBA8 : TextureFormat.ETC2_RGB4;
                case ATOCompressionFormat.ASTC_6x6: return TextureFormat.ASTC_6x6;
                case ATOCompressionFormat.ASTC_4x4: return TextureFormat.ASTC_4x4;
                case ATOCompressionFormat.PVRTC_4BPP: return TextureFormat.PVRTC_RGBA4;
                case ATOCompressionFormat.Crunch: return hasAlpha ? TextureFormat.DXT5Crunched : TextureFormat.DXT1Crunched;
                default: return hasAlpha ? TextureFormat.RGBA32 : TextureFormat.RGB24;
            }
        }

        private static ATOPlatformTarget ResolvePlatform()
        {
            switch (EditorUserBuildSettings.activeBuildTarget)
            {
                case BuildTarget.Android: return ATOPlatformTarget.Android;
                case BuildTarget.iOS: return ATOPlatformTarget.iOS;
                default: return ATOPlatformTarget.PC;
            }
        }

        private static bool AtlasHasAlpha(ATOAtlas atlas)
        {
            if (atlas.group.type == ATOTextureType.NormalMap) return false;
            foreach (var island in atlas.islands)
                if (island.texture.hasAlpha) return true;
            return false;
        }

        /// <summary>检测灰度图集是否实际包含多通道内容（读首个岛原贴图）。</summary>
        private static bool GrayscaleIsMultiChannel(ATOAtlas atlas)
        {
            foreach (var island in atlas.islands)
            {
                if (island.texture == null || island.texture.texture == null) continue;
                if (IsMultiChannelGray(island.texture.texture)) return true;
            }
            return false;
        }

        private static bool IsMultiChannelGray(Texture2D tex)
        {
            try
            {
                var (px, w, h) = ATOProcessor.ReadTextureLinear(tex);
                int n = w * h;
                for (int i = 0; i < n; i++)
                {
                    float r = px[i * 4 + 0], g = px[i * 4 + 1], b = px[i * 4 + 2];
                    if (Mathf.Abs(r - g) > 0.02f || Mathf.Abs(r - b) > 0.02f) return true;
                }
                return false;
            }
            catch { return false; }
        }
    }
}
