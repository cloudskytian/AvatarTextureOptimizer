// Copyright (c) fosa. Licensed under the MIT License.
// Shader property/keyword analysis. Decides which texture properties are safe to optimize.
// Any property that could transform, animate or repurpose UVs forces a whitelist fallback.
// 着色器属性/关键字分析，判定哪些贴图属性可以安全优化。
// 任何可能对 UV 做变换、动画或特殊用途的属性都会强制回退到白名单。

using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// The verdict for one texture property on one material.
    /// 某个材质上某个贴图属性的判定结果。
    /// </summary>
    public sealed class TexturePropertyInfo
    {
        /// <summary>Shader property name. / 着色器属性名。</summary>
        public string Name;

        /// <summary>Inferred semantic category. / 推断出的语义分类。</summary>
        public TextureCategory Category = TextureCategory.OpaqueColor;

        /// <summary>Sampled in linear space rather than sRGB. / 以线性空间而非 sRGB 采样。</summary>
        public bool IsLinear;

        /// <summary>Safe to optimize. When false see <see cref="UnsafeReason" />. / 是否可安全优化，为 false 时见 UnsafeReason。</summary>
        public bool IsSafe = true;

        /// <summary>Why the property was rejected. / 属性被拒绝的原因。</summary>
        public string UnsafeReason;

        /// <summary>Which channels the shader actually consumes. / 着色器实际消费的通道。</summary>
        public ChannelMask UsedChannels = ChannelMask.All;

        /// <summary>UV channel this property samples with, defaulting to 0. / 该属性采样使用的 UV 通道，默认 0。</summary>
        public int UVChannel;
    }

    /// <summary>
    /// Analyses shaders generically, with verified special-casing for lilToon. Unknown shaders
    /// are handled conservatively: anything that cannot be proven safe is whitelisted and
    /// reported, so future shader versions degrade gracefully rather than corrupting materials.
    /// 通用着色器分析，并针对 lilToon 做了经验证的特殊处理。未知着色器采取保守策略：
    /// 无法证明安全的一律列入白名单并报告，使未来的着色器版本优雅降级而非破坏材质。
    /// </summary>
    public sealed class ShaderAnalyzer
    {
        /// <summary>
        /// Property-name suffixes that imply a UV transform. Sourced by scanning the real
        /// lilToon 2.3.4 shader sources; these are the standard Unity/lilToon conventions.
        /// 暗示存在 UV 变换的属性名后缀。通过扫描真实的 lilToon 2.3.4 着色器源码得出，
        /// 属于 Unity/lilToon 的标准约定。
        /// </summary>
        private static readonly string[] UnsafeSuffixes =
        {
            "_ST",            // tiling/offset / 平铺与偏移
            "_ScrollRotate",  // animated scroll and rotation / 动画滚动与旋转
            "_UVMode",        // selects a different UV source at runtime / 运行时切换 UV 来源
            "_Angle",         // rotation / 旋转
        };

        /// <summary>
        /// Substrings that mark a property as a decal or otherwise deforming use.
        /// 标记属性为贴花或其他形变用途的子串。
        /// </summary>
        private static readonly string[] UnsafeSubstrings =
        {
            "IsDecal",
            "DecalAnimation",
            "DecalSubParam",
            "UDIMDiscard",
        };

        /// <summary>
        /// Property-name fragments that reliably indicate a tangent-space normal map.
        /// 可靠指示切线空间法线贴图的属性名片段。
        /// </summary>
        private static readonly string[] NormalHints =
        {
            "_BumpMap", "_NormalMap", "_Bump2ndMap", "_DetailNormalMap", "_NormalMapTex",
        };

        /// <summary>
        /// Property-name fragments that indicate single/multi channel mask data.
        /// 指示单/多通道蒙版数据的属性名片段。
        /// </summary>
        private static readonly string[] MaskHints =
        {
            "Mask", "_MetallicGlossMap", "_SpecGlossMap", "_OcclusionMap", "_SmoothnessTex",
            "_Roughness", "_Metallic", "_AlphaMask", "_GlossMap",
        };

        private readonly Dictionary<Shader, Dictionary<string, TexturePropertyInfo>> _cache =
            new Dictionary<Shader, Dictionary<string, TexturePropertyInfo>>();

        private readonly ATOLogger _log;

        /// <summary>Creates an analyzer bound to a logger. / 创建绑定到日志器的分析器。</summary>
        public ShaderAnalyzer(ATOLogger log)
        {
            _log = log;
        }

        /// <summary>
        /// Analyses every texture property of a shader, caching per shader.
        /// 分析着色器的所有贴图属性，按着色器缓存结果。
        /// </summary>
        public Dictionary<string, TexturePropertyInfo> Analyze(Shader shader)
        {
            if (shader == null) return new Dictionary<string, TexturePropertyInfo>();
            if (_cache.TryGetValue(shader, out var cached)) return cached;

            var result = new Dictionary<string, TexturePropertyInfo>(StringComparer.Ordinal);
            var allNames = new HashSet<string>(StringComparer.Ordinal);

            var count = shader.GetPropertyCount();
            for (var i = 0; i < count; i++)
            {
                allNames.Add(shader.GetPropertyName(i));
            }

            for (var i = 0; i < count; i++)
            {
                if (shader.GetPropertyType(i) != ShaderPropertyType.Texture) continue;

                var name = shader.GetPropertyName(i);
                var info = new TexturePropertyInfo { Name = name };
                var flags = shader.GetPropertyFlags(i);

                // A NoScaleOffset property provably has no _ST, so it cannot be tiled or offset.
                // Without that flag we must check whether a companion _ST property exists.
                // 带 NoScaleOffset 的属性可证明没有 _ST，因此不会被平铺或偏移。
                // 没有该标志时必须检查是否存在配套的 _ST 属性。
                var hasNoScaleOffset = (flags & ShaderPropertyFlags.NoScaleOffset) != 0;
                if (!hasNoScaleOffset && allNames.Contains(name + "_ST"))
                {
                    info.IsSafe = true; // presence alone is fine; the *value* is checked per material
                                        // 仅存在该属性没问题，具体数值在每个材质上单独检查
                }

                // Reject properties whose very existence implies a UV transform we cannot follow.
                // 拒绝那些一旦存在就意味着我们无法跟踪的 UV 变换的属性。
                foreach (var suffix in UnsafeSuffixes)
                {
                    if (allNames.Contains(name + suffix) && suffix != "_ST")
                    {
                        info.IsSafe = false;
                        info.UnsafeReason = $"companion property '{name}{suffix}' implies a UV transform";
                        break;
                    }
                }

                if (info.IsSafe)
                {
                    foreach (var frag in UnsafeSubstrings)
                    {
                        if (name.IndexOf(frag, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            info.IsSafe = false;
                            info.UnsafeReason = $"property name contains '{frag}'";
                            break;
                        }
                    }
                }

                ClassifyProperty(shader, i, name, flags, info);
                result[name] = info;
            }

            _cache[shader] = result;
            _log?.Detail($"Shader '{shader.name}': analysed {result.Count} texture properties");
            return result;
        }

        /// <summary>
        /// Infers category, colour space and used channels from flags and naming conventions.
        /// 依据标志与命名约定推断分类、色彩空间与使用通道。
        /// </summary>
        private static void ClassifyProperty(
            Shader shader, int index, string name, ShaderPropertyFlags flags, TexturePropertyInfo info)
        {
            // The Normal flag is authoritative when present.
            // Normal 标志存在时具有权威性。
            if ((flags & ShaderPropertyFlags.Normal) != 0)
            {
                info.Category = TextureCategory.NormalMap;
                info.IsLinear = true;
                info.UsedChannels = ChannelMask.RGB;
                return;
            }

            foreach (var hint in NormalHints)
            {
                if (name.IndexOf(hint, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    info.Category = TextureCategory.NormalMap;
                    info.IsLinear = true;
                    info.UsedChannels = ChannelMask.RGB;
                    return;
                }
            }

            foreach (var hint in MaskHints)
            {
                if (name.IndexOf(hint, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    info.Category = TextureCategory.Grayscale;
                    info.IsLinear = true;
                    info.UsedChannels = ChannelMask.All;
                    return;
                }
            }

            // Everything else is treated as colour. Whether alpha matters is decided later from
            // the actual decoded pixels plus the referencing material's render mode.
            // 其余一律视为颜色贴图。alpha 是否重要留待之后依据实际解码像素与引用材质的渲染模式决定。
            info.Category = TextureCategory.OpaqueColor;
            info.IsLinear = false;
            info.UsedChannels = ChannelMask.All;
        }

        /// <summary>
        /// Verifies that a specific material does not transform the UVs feeding a property.
        /// This is the per-material half of the safety check: the shader may declare _ST, but the
        /// material must leave it at the identity transform.
        /// 验证某个具体材质没有对该属性所用的 UV 做变换。
        /// 这是安全检查的“逐材质”一半：着色器可以声明 _ST，但材质必须保持恒等变换。
        /// </summary>
        public bool IsMaterialUsageSafe(Material material, string propertyName, out string reason)
        {
            reason = null;
            if (material == null)
            {
                reason = "material is null";
                return false;
            }

            var stName = propertyName + "_ST";
            if (material.HasProperty(stName))
            {
                var scale = material.GetTextureScale(propertyName);
                var offset = material.GetTextureOffset(propertyName);
                if (!Approximately(scale, Vector2.one) || !Approximately(offset, Vector2.zero))
                {
                    reason = $"{stName} is not identity (scale={scale}, offset={offset})";
                    return false;
                }
            }

            // lilToon exposes UV scrolling/rotation as a vector; a non-zero value animates UVs.
            // lilToon 以向量形式暴露 UV 滚动/旋转，非零值意味着 UV 会被动画化。
            var scrollName = propertyName + "_ScrollRotate";
            if (material.HasProperty(scrollName))
            {
                var v = material.GetVector(scrollName);
                if (Mathf.Abs(v.x) > 1e-6f || Mathf.Abs(v.y) > 1e-6f ||
                    Mathf.Abs(v.z) > 1e-6f || Mathf.Abs(v.w) > 1e-6f)
                {
                    reason = $"{scrollName} is non-zero ({v})";
                    return false;
                }
            }

            // A non-zero UVMode selects a different UV set or a generated coordinate.
            // 非零的 UVMode 会选择不同的 UV 集合或程序生成的坐标。
            var uvModeName = propertyName + "_UVMode";
            if (material.HasProperty(uvModeName))
            {
                var mode = material.GetFloat(uvModeName);
                if (mode > 3.5f)
                {
                    // Modes above 3 are non-mesh UVs (e.g. MatCap space) in lilToon.
                    // lilToon 中大于 3 的模式属于非网格 UV（例如 MatCap 空间）。
                    reason = $"{uvModeName}={mode} selects a non-mesh UV source";
                    return false;
                }
            }

            // lilToon's decal mode rewrites UVs per-pixel: it mirrors (ShouldCopy /
            // ShouldFlipCopy / ShouldFlipMirror), hides a side by forcing u = -1
            // (IsLeftOnly / IsRightOnly) and rotates by Angle. It also fades the texture
            // out beyond the 0-1 range via lilIsIn0to1. None of that survives being packed
            // into an atlas, where 0-1 no longer means "this texture".
            // lilToon 的贴花模式会逐像素改写 UV：镜像、通过强制 u = -1 隐藏一侧、按 Angle 旋转，
            // 并通过 lilIsIn0to1 在 0-1 范围外淡出。图集化后 0-1 不再代表「这张贴图」，这些都会失效。
            if (IsFlagSet(material, propertyName + "IsDecal") ||
                IsFlagSet(material, propertyName + "IsLeftOnly") ||
                IsFlagSet(material, propertyName + "IsRightOnly") ||
                IsFlagSet(material, propertyName + "ShouldCopy") ||
                IsFlagSet(material, propertyName + "ShouldFlipMirror") ||
                IsFlagSet(material, propertyName + "ShouldFlipCopy"))
            {
                reason = $"{propertyName} uses lilToon decal UV manipulation";
                return false;
            }

            var angleName = propertyName + "Angle";
            if (material.HasProperty(angleName) &&
                Mathf.Abs(material.GetFloat(angleName)) > 1e-6f)
            {
                reason = $"{angleName} rotates the UVs ({material.GetFloat(angleName)})";
                return false;
            }

            // MSDF textures encode signed distances, not colour. Rescaling and dilating
            // them destroys the field, so never touch them.
            // MSDF 贴图编码的是有符号距离而非颜色。缩放和外扩会破坏距离场，因此绝不处理。
            if (IsFlagSet(material, propertyName + "IsMSDF"))
            {
                reason = $"{propertyName} is an MSDF texture";
                return false;
            }

            // Decal atlas animation walks a sprite grid over time. The defaults
            // (DecalAnimation=(1,1,1,30), DecalSubParam=(1,1,0,1)) collapse to identity,
            // so only flag a real grid.
            // 贴花图集动画会随时间遍历精灵网格。默认值会退化为恒等变换，故仅在存在真实网格时报告。
            var animName = propertyName + "DecalAnimation";
            var subName = propertyName + "DecalSubParam";
            if (material.HasProperty(animName) || material.HasProperty(subName))
            {
                var anim = material.HasProperty(animName)
                    ? material.GetVector(animName)
                    : new Vector4(1f, 1f, 1f, 30f);
                var sub = material.HasProperty(subName)
                    ? material.GetVector(subName)
                    : new Vector4(1f, 1f, 0f, 1f);

                var gridded = Mathf.Abs(anim.x - 1f) > 1e-6f || Mathf.Abs(anim.y - 1f) > 1e-6f;
                var scaled = Mathf.Abs(sub.x - 1f) > 1e-6f || Mathf.Abs(sub.y - 1f) > 1e-6f ||
                             Mathf.Abs(sub.z) > 1e-6f;
                if (gridded || scaled)
                {
                    reason = $"{propertyName} uses decal atlas animation " +
                             $"(animation={anim}, subParam={sub})";
                    return false;
                }
            }

            // UDIM discard keys off which UV tile a pixel lands in. We renormalise
            // out-of-range UVs into 0-1, which would silently change what gets discarded.
            // UDIM 剔除依赖像素落在哪个 UV 分块。我们会把越界 UV 归一化到 0-1，
            // 这会悄无声息地改变被剔除的内容。
            if (IsFlagSet(material, "_UDIMDiscardCompile") &&
                material.HasProperty("_UDIMDiscardMode"))
            {
                reason = "material uses UDIM discard, which depends on UV tile positions";
                return false;
            }

            return true;
        }

        /// <summary>
        /// Reads a lilToon toggle, treating "missing" as "off".
        /// 读取 lilToon 开关，「不存在」视为「关闭」。
        /// </summary>
        private static bool IsFlagSet(Material material, string propertyName)
        {
            return material.HasProperty(propertyName) && material.GetFloat(propertyName) > 0.5f;
        }

        /// <summary>
        /// Resolves the UV channel a property samples from, honouring lilToon's _UVMode.
        /// 解析属性所采样的 UV 通道，正确处理 lilToon 的 _UVMode。
        /// </summary>
        public int ResolveUVChannel(Material material, string propertyName)
        {
            if (material == null) return 0;
            var uvModeName = propertyName + "_UVMode";
            if (material.HasProperty(uvModeName))
            {
                var mode = Mathf.RoundToInt(material.GetFloat(uvModeName));
                if (mode >= 0 && mode <= 3) return mode;
            }

            return 0;
        }

        private static bool Approximately(Vector2 a, Vector2 b) =>
            Mathf.Abs(a.x - b.x) < 1e-5f && Mathf.Abs(a.y - b.y) < 1e-5f;
    }
}
