// SPDX-License-Identifier: MIT
// EN: Turns working buffers into final Texture2D assets: colour space encoding, normal map swizzle,
//     mipmaps, mip streaming, compression format selection with safety fallbacks.
// ZH: 把工作缓冲转成最终的 Texture2D 资产：色彩空间编码、法线 swizzle、Mipmap、
//     MipStreaming，以及带安全回退的压缩格式选择。

using System;
using Unity.Collections;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// EN: Everything needed to write one output texture.
    /// ZH: 写出一张输出贴图所需的全部信息。
    /// </summary>
    public struct ATOWriteRequest
    {
        public string Name;
        public ATOTextureRole Role;
        public bool SRGB;
        public FilterMode Filter;
        public int AnisoLevel;
        public bool HasAlpha;
        public bool[] UsedChannels;
        public ATOPlatformProfile Profile;
        public ATOPlatform Platform;
    }

    /// <summary>
    /// EN: Writes final textures.
    /// ZH: 输出最终贴图。
    /// </summary>
    public sealed class ATOTextureWriter
    {
        private readonly ATOLog _log;
        private readonly ATOReporter _reporter;

        /// <summary>EN: content hash of every texture we produced. ZH: 我们生成的每张贴图的内容哈希。</summary>
        private readonly System.Collections.Generic.Dictionary<Texture2D, string> _hashes =
            new System.Collections.Generic.Dictionary<Texture2D, string>();

        /// <summary>
        /// EN: Content + parameter hash of a texture produced by this writer, used for deduplication.
        /// ZH: 本写出器生成的贴图的内容+参数哈希，用于去重。
        /// </summary>
        public bool TryGetHash(Texture2D texture, out string hash) => _hashes.TryGetValue(texture, out hash);

        public ATOTextureWriter(ATOLog log, ATOReporter reporter)
        {
            _log = log;
            _reporter = reporter;
        }

        /// <summary>
        /// EN: Encodes linear half data into a compressed Texture2D following the request.
        /// ZH: 按请求把线性 half 数据编码成压缩后的 Texture2D。
        /// </summary>
        public Texture2D Write(NativeArray<half4> linearPixels, int width, int height, ATOWriteRequest request)
        {
            var mipmaps = MipmapEnabled(request);
            var isNormalDXT5nm = false;
            var format = ResolveFormat(request, width, height, ref isNormalDXT5nm);

            var texture = new Texture2D(width, height, TextureFormat.RGBA32, mipmaps, !request.SRGB)
            {
                name = request.Name,
                filterMode = request.Filter,
                wrapMode = TextureWrapMode.Clamp, // EN: atlases are always clamped. ZH: 图集恒为 Clamp。
                anisoLevel = Mathf.Clamp(request.AnisoLevel, 0, 16),
            };

            // EN: With mipmaps the raw buffer holds every level; only level 0 is written here and
            //     Apply(true) regenerates the rest.
            // ZH: 启用 Mipmap 时原始缓冲包含所有层级；这里只写第 0 层，Apply(true) 会重新生成其余层级。
            var raw = texture.GetRawTextureData<Color32>();
            var mip0 = Mathf.Min(raw.Length, width * height);
            for (var i = 0; i < mip0; i++)
            {
                var c = (float4)linearPixels[i];
                raw[i] = Encode(c, request, isNormalDXT5nm);
            }

            texture.Apply(mipmaps, false);

            _hashes[texture] = ComputeHash(raw, mip0, width, height, format, request);

            if (format != TextureFormat.RGBA32)
            {
                try
                {
                    EditorUtility.CompressTexture(texture, format, (int)ResolveQuality(request));
                }
                catch (Exception e)
                {
                    _log.Warning("write",
                        $"'{request.Name}': compression to {format} failed ({e.Message}), keeping RGBA32");
                }
            }

            texture.Apply(false, true); // EN: drop the CPU copy (Read/Write off). ZH: 释放 CPU 副本（关闭 Read/Write）。

            ApplyStreamingMipmaps(texture, mipmaps);

            _log.Info("write",
                $"'{request.Name}' {width}x{height} {format} mip={mipmaps} srgb={request.SRGB} " +
                $"alpha={request.HasAlpha} role={request.Role}");
            return texture;
        }

        private static string ComputeHash(NativeArray<Color32> data, int count, int width, int height,
            TextureFormat format, ATOWriteRequest request)
        {
            ulong hash = 1469598103934665603UL;
            for (var i = 0; i < count; i++)
            {
                var c = data[i];
                hash ^= (ulong)(c.r | (c.g << 8) | (c.b << 16) | (c.a << 24));
                hash *= 1099511628211UL;
            }

            return $"{hash:x16}|{width}x{height}|{format}|{request.SRGB}|{request.Filter}|{request.Role}|" +
                   $"{request.AnisoLevel}";
        }

        /// <summary>
        /// EN: Bit exact copy of a texture including its compressed format; used by the lossless tier so
        ///     nothing is ever resampled or re-encoded.
        /// ZH: 连压缩格式一起的逐位拷贝；近无损挡位使用，保证既不重采样也不重新编码。
        /// </summary>
        public Texture2D CloneVerbatim(Texture2D source, string name, bool mipmaps)
        {
            var copy = new Texture2D(source.width, source.height, source.format, source.mipmapCount > 1,
                !GraphicsFormatUtility.IsSRGBFormat(source.graphicsFormat))
            {
                name = name,
                filterMode = source.filterMode,
                wrapMode = source.wrapMode,
                anisoLevel = source.anisoLevel,
            };

            try
            {
                Graphics.CopyTexture(source, copy);
            }
            catch (Exception e)
            {
                _log.Warning("write", $"verbatim copy of '{source.name}' failed: {e.Message}");
                UnityEngine.Object.DestroyImmediate(copy);
                return null;
            }

            ApplyStreamingMipmaps(copy, mipmaps && copy.mipmapCount > 1);
            _log.Info("write", $"'{name}' copied verbatim ({source.width}x{source.height} {source.format})");
            return copy;
        }

        private Color32 Encode(float4 linear, ATOWriteRequest request, bool normalDXT5nm)
        {
            if (request.Role == ATOTextureRole.Normal)
            {
                var n = math.normalizesafe(linear.xyz, new float3(0, 0, 1));
                var x = (byte)Mathf.Clamp(Mathf.RoundToInt((n.x * 0.5f + 0.5f) * 255f), 0, 255);
                var y = (byte)Mathf.Clamp(Mathf.RoundToInt((n.y * 0.5f + 0.5f) * 255f), 0, 255);
                var z = (byte)Mathf.Clamp(Mathf.RoundToInt((n.z * 0.5f + 0.5f) * 255f), 0, 255);

                // EN: DXT5nm keeps X in alpha and Y in green; every other format stores plain XYZ.
                // ZH: DXT5nm 把 X 存在 alpha、Y 存在绿色通道；其他格式直接存 XYZ。
                return normalDXT5nm ? new Color32(255, y, 255, x) : new Color32(x, y, z, 255);
            }

            var r = request.SRGB ? LinearToSrgb(linear.x) : linear.x;
            var g = request.SRGB ? LinearToSrgb(linear.y) : linear.y;
            var b = request.SRGB ? LinearToSrgb(linear.z) : linear.z;

            return new Color32(
                (byte)Mathf.Clamp(Mathf.RoundToInt(r * 255f), 0, 255),
                (byte)Mathf.Clamp(Mathf.RoundToInt(g * 255f), 0, 255),
                (byte)Mathf.Clamp(Mathf.RoundToInt(b * 255f), 0, 255),
                (byte)Mathf.Clamp(Mathf.RoundToInt(linear.w * 255f), 0, 255));
        }

        private static float LinearToSrgb(float c)
        {
            c = Mathf.Clamp01(c);
            return c <= 0.0031308f ? c * 12.92f : 1.055f * Mathf.Pow(c, 1f / 2.4f) - 0.055f;
        }

        private bool MipmapEnabled(ATOWriteRequest request)
        {
            var p = request.Profile;
            switch (request.Role)
            {
                case ATOTextureRole.Normal: return p.mipmapNormal;
                case ATOTextureRole.Grayscale: return p.mipmapGrayscale;
                default: return p.mipmapColor;
            }
        }

        private static TextureCompressionQuality ResolveQuality(ATOWriteRequest request)
        {
            var q = request.Profile.compressionQuality;
            if (q >= 90) return TextureCompressionQuality.Best;
            if (q >= 40) return TextureCompressionQuality.Normal;
            return TextureCompressionQuality.Fast;
        }

        /// <summary>
        /// EN: Chooses a safe compression format. Unsafe user choices are silently upgraded and reported.
        /// ZH: 选择安全的压缩格式。不安全的用户选择会被自动升级并给出报告。
        /// </summary>
        private TextureFormat ResolveFormat(ATOWriteRequest request, int width, int height, ref bool normalDXT5nm)
        {
            var p = request.Profile;
            var mobile = request.Platform != ATOPlatform.PC;
            var dxtCompatible = width % 4 == 0 && height % 4 == 0;

            switch (request.Role)
            {
                case ATOTextureRole.Normal:
                {
                    var choice = p.formatNormal;
                    if (choice == ATOFormatNormal.Automatic)
                        choice = mobile ? ATOFormatNormal.ASTC_5x5 : ATOFormatNormal.BC5;

                    switch (choice)
                    {
                        case ATOFormatNormal.DXT5nm when dxtCompatible && !mobile:
                            normalDXT5nm = true;
                            return TextureFormat.DXT5;
                        case ATOFormatNormal.BC5 when dxtCompatible && !mobile:
                            return TextureFormat.BC5;
                        case ATOFormatNormal.BC7 when dxtCompatible && !mobile:
                            return TextureFormat.BC7;
                        case ATOFormatNormal.ASTC_4x4: return TextureFormat.ASTC_4x4;
                        case ATOFormatNormal.ASTC_5x5: return TextureFormat.ASTC_5x5;
                        case ATOFormatNormal.ASTC_6x6: return TextureFormat.ASTC_6x6;
                        case ATOFormatNormal.Uncompressed_RGBA32: return TextureFormat.RGBA32;
                        default:
                            if (!dxtCompatible)
                                _reporter.Warn("ato:warn:npotFormat", null, request.Name, choice.ToString());
                            return mobile ? TextureFormat.ASTC_5x5 : TextureFormat.RGBA32;
                    }
                }

                case ATOTextureRole.Grayscale:
                {
                    var multiChannel = CountUsedChannels(request.UsedChannels) > 1;
                    var choice = p.formatGrayscale;
                    if (choice == ATOFormatGrayscale.Automatic)
                        choice = multiChannel
                            ? (mobile ? ATOFormatGrayscale.ASTC_6x6 : ATOFormatGrayscale.BC7)
                            : (mobile ? ATOFormatGrayscale.ASTC_6x6 : ATOFormatGrayscale.BC4);

                    // EN: A single channel format would destroy multi channel masks -> upgrade.
                    // ZH: 单通道格式会破坏多通道蒙版 -> 自动升级。
                    if (multiChannel && (choice == ATOFormatGrayscale.BC4 || choice == ATOFormatGrayscale.Uncompressed_R8))
                    {
                        _reporter.Warn("ato:warn:multiChannelGrayscale", null, request.Name);
                        choice = mobile ? ATOFormatGrayscale.ASTC_6x6 : ATOFormatGrayscale.BC7;
                    }

                    // EN: Alpha carrying masks must not use an alpha-less format. ZH: 带 alpha 的蒙版不能用无 alpha 的格式。
                    if (request.HasAlpha && choice == ATOFormatGrayscale.DXT1)
                    {
                        _reporter.Warn("ato:warn:alphaFormat", null, request.Name);
                        choice = ATOFormatGrayscale.DXT5;
                    }

                    switch (choice)
                    {
                        case ATOFormatGrayscale.BC4 when dxtCompatible && !mobile: return TextureFormat.BC4;
                        case ATOFormatGrayscale.BC7 when dxtCompatible && !mobile: return TextureFormat.BC7;
                        case ATOFormatGrayscale.DXT1 when dxtCompatible && !mobile: return TextureFormat.DXT1;
                        case ATOFormatGrayscale.DXT5 when dxtCompatible && !mobile: return TextureFormat.DXT5;
                        case ATOFormatGrayscale.ASTC_4x4: return TextureFormat.ASTC_4x4;
                        case ATOFormatGrayscale.ASTC_6x6: return TextureFormat.ASTC_6x6;
                        case ATOFormatGrayscale.Uncompressed_R8: return TextureFormat.R8;
                        default: return TextureFormat.RGBA32;
                    }
                }

                default:
                {
                    if (request.HasAlpha)
                    {
                        var choice = p.formatColorAlpha;
                        if (choice == ATOFormatColorAlpha.Automatic)
                            choice = mobile ? ATOFormatColorAlpha.ASTC_5x5 : ATOFormatColorAlpha.BC7;

                        switch (choice)
                        {
                            case ATOFormatColorAlpha.DXT5 when dxtCompatible && !mobile: return TextureFormat.DXT5;
                            case ATOFormatColorAlpha.BC7 when dxtCompatible && !mobile: return TextureFormat.BC7;
                            case ATOFormatColorAlpha.ASTC_4x4: return TextureFormat.ASTC_4x4;
                            case ATOFormatColorAlpha.ASTC_5x5: return TextureFormat.ASTC_5x5;
                            case ATOFormatColorAlpha.ASTC_6x6: return TextureFormat.ASTC_6x6;
                            case ATOFormatColorAlpha.ASTC_8x8: return TextureFormat.ASTC_8x8;
                            default: return TextureFormat.RGBA32;
                        }
                    }
                    else
                    {
                        var choice = p.formatColorOpaque;
                        if (choice == ATOFormatColorOpaque.Automatic)
                            choice = mobile ? ATOFormatColorOpaque.ASTC_6x6 : ATOFormatColorOpaque.DXT1;

                        switch (choice)
                        {
                            case ATOFormatColorOpaque.DXT1 when dxtCompatible && !mobile: return TextureFormat.DXT1;
                            case ATOFormatColorOpaque.BC7 when dxtCompatible && !mobile: return TextureFormat.BC7;
                            case ATOFormatColorOpaque.ASTC_4x4: return TextureFormat.ASTC_4x4;
                            case ATOFormatColorOpaque.ASTC_5x5: return TextureFormat.ASTC_5x5;
                            case ATOFormatColorOpaque.ASTC_6x6: return TextureFormat.ASTC_6x6;
                            case ATOFormatColorOpaque.ASTC_8x8: return TextureFormat.ASTC_8x8;
                            case ATOFormatColorOpaque.Uncompressed_RGB24: return TextureFormat.RGB24;
                            default: return TextureFormat.RGBA32;
                        }
                    }
                }
            }
        }

        private static int CountUsedChannels(bool[] channels)
        {
            if (channels == null) return 4;
            var n = 0;
            foreach (var c in channels)
                if (c)
                    n++;
            return Mathf.Max(1, n);
        }

        /// <summary>
        /// EN: VRChat requires mip streaming whenever mipmaps exist; the two are always set together.
        /// ZH: VRChat 要求只要有 Mipmap 就必须开启 MipStreaming；二者始终一起设置。
        /// </summary>
        private void ApplyStreamingMipmaps(Texture2D texture, bool mipmaps)
        {
            try
            {
                using var so = new SerializedObject(texture);
                var streaming = so.FindProperty("m_StreamingMipmaps");
                var priority = so.FindProperty("m_StreamingMipmapsPriority");
                if (streaming == null)
                {
                    _log.Warning("write", "m_StreamingMipmaps not found on this Unity version");
                    return;
                }

                streaming.boolValue = mipmaps;
                if (priority != null) priority.intValue = 0;
                so.ApplyModifiedPropertiesWithoutUndo();
            }
            catch (Exception e)
            {
                _log.Warning("write", $"could not set mip streaming: {e.Message}");
            }
        }

        /// <summary>
        /// EN: Returns the platform the current build targets.
        /// ZH: 返回当前构建目标平台。
        /// </summary>
        public static ATOPlatform CurrentPlatform()
        {
            switch (EditorUserBuildSettings.activeBuildTarget)
            {
                case BuildTarget.Android: return ATOPlatform.Android;
                case BuildTarget.iOS: return ATOPlatform.iOS;
                default: return ATOPlatform.PC;
            }
        }
    }
}
