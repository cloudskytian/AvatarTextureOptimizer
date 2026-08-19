// SPDX-License-Identifier: MIT
// AvatarTextureOptimizer (ATO) - Shader property-table analysis.
// AvatarTextureOptimizer (ATO) - 着色器属性表分析。

using System;
using System.Collections.Generic;
using Net.Fosa.AvatarTextureOptimizer.Editor.Core;
using UnityEngine;
using UnityEngine.Rendering;

namespace Net.Fosa.AvatarTextureOptimizer.Editor.Analysis
{
    /// <summary>
    /// EN: Why a texture slot cannot be optimized. Anything other than <see cref="None"/> makes ATO treat the
    ///     texture exactly as if the user had whitelisted it, and (except for <see cref="None"/>) emits a warning.
    /// ZH: 贴图槽无法被优化的原因。除 <see cref="None"/> 外的任何值都会让 ATO 将该贴图按白名单处理，
    ///     并（除 <see cref="None"/> 外）报出警告。
    /// </summary>
    public enum SlotRejectReason
    {
        None = 0,
        /// <summary>EN: Tiling/offset is not identity. ZH: Tiling/Offset 不是单位变换。</summary>
        ScaleOffset,
        /// <summary>EN: Shader-side UV scroll / rotation. ZH: 着色器侧的 UV 滚动 / 旋转。</summary>
        ScrollRotate,
        /// <summary>EN: Slot is a decal / deformed usage. ZH: 该槽为贴花 / 形变用途。</summary>
        Decal,
        /// <summary>EN: Slot does not sample mesh UVs at all (matcap, screen space, gradation LUT...).
        ///     ZH: 该槽根本不采样网格 UV（MatCap、屏幕空间、渐变 LUT 等）。</summary>
        NonMeshUv,
        /// <summary>EN: UV channel is outside the mesh's available channels. ZH: UV 通道超出网格可用范围。</summary>
        UvChannelMissing,
        /// <summary>EN: Shader could not be understood. ZH: 无法解析该着色器。</summary>
        UnknownShader,
        /// <summary>EN: Animation drives the tiling/offset/uv-mode of this slot. ZH: 动画驱动了该槽的 Tiling/Offset/UV 模式。</summary>
        AnimatedTransform,
        /// <summary>EN: Not a Texture2D (cube/3D/array/RT). ZH: 不是 Texture2D（Cube/3D/Array/RT）。</summary>
        NotTexture2D,
    }

    /// <summary>
    /// EN: One analysed texture property of one material.
    /// ZH: 某个材质上的一个已分析贴图属性。
    /// </summary>
    public sealed class MaterialTextureSlot
    {
        public Material Material;
        public string PropertyName;
        public Texture2D Texture;

        /// <summary>EN: Mesh UV channel this slot samples (0..7). ZH: 该槽采样的网格 UV 通道（0..7）。</summary>
        public int UvChannel;

        /// <summary>EN: True if the shader declares [Normal] or the importer says NormalMap.
        ///     ZH: 着色器声明了 [Normal]，或导入器类型为 NormalMap 时为 true。</summary>
        public bool IsNormalMap;

        /// <summary>EN: True for the shader's main texture (via [MainTexture] or _MainTex).
        ///     ZH: 着色器主贴图（通过 [MainTexture] 标记或名为 _MainTex）时为 true。</summary>
        public bool IsMainTexture;

        /// <summary>EN: sRGB sampling (from the importer). ZH: 是否 sRGB 采样（来自导入器）。</summary>
        public bool SRGB;

        public SlotRejectReason Reject = SlotRejectReason.None;

        public bool Usable => Reject == SlotRejectReason.None && Texture != null;

        public override string ToString() =>
            $"{Material?.name}.{PropertyName} -> {Texture?.name} (uv{UvChannel}{(IsNormalMap ? ", normal" : "")}{(Reject != SlotRejectReason.None ? ", REJECT:" + Reject : "")})";
    }

    /// <summary>
    /// EN: Generic, future-proof shader analysis. We read the shader's own property table rather than
    ///     hard-coding shader names, and additionally understand the well-known lilToon naming conventions
    ///     (<c>_X_UVMode</c>, <c>_X_ScrollRotate</c>, <c>_XAngle</c>, <c>_XIsDecal</c>). Whatever we cannot
    ///     prove safe is rejected and treated as whitelisted, so unknown/future shaders degrade gracefully.
    /// ZH: 通用且面向未来的着色器分析。我们读取着色器自己的属性表，而不是硬编码着色器名称，
    ///     同时理解 lilToon 等使用的通用命名约定（<c>_X_UVMode</c>、<c>_X_ScrollRotate</c>、
    ///     <c>_XAngle</c>、<c>_XIsDecal</c>）。任何无法证明安全的槽都会被拒绝并按白名单处理，
    ///     因此未知/未来的着色器只会退化而不会出错。
    /// </summary>
    public static class ShaderAnalysis
    {
        /// <summary>
        /// EN: Property-name fragments that are known never to sample mesh UVs.
        /// ZH: 已知绝不采样网格 UV 的属性名片段。
        /// </summary>
        private static readonly string[] NonMeshUvFragments =
        {
            "matcap", "dither", "gradation", "gradient", "rimshade_lut", "_lut", "screen",
            "reflectioncube", "cubemap", "smoothnesstex_matcap", "outlinevectortex_uvmode",
        };

        private sealed class ShaderCacheEntry
        {
            public readonly List<int> TextureProperties = new List<int>();
            public readonly HashSet<string> AllProperties = new HashSet<string>(StringComparer.Ordinal);
            public readonly Dictionary<string, ShaderPropertyType> Types =
                new Dictionary<string, ShaderPropertyType>(StringComparer.Ordinal);
            public string MainTextureProperty;
        }

        private static readonly Dictionary<Shader, ShaderCacheEntry> _cache = new Dictionary<Shader, ShaderCacheEntry>();

        /// <summary>EN: Drop cached shader reflection data. ZH: 清空缓存的着色器反射数据。</summary>
        public static void ClearCache() => _cache.Clear();

        private static ShaderCacheEntry GetEntry(Shader shader)
        {
            if (_cache.TryGetValue(shader, out var e)) return e;

            e = new ShaderCacheEntry();
            int count = shader.GetPropertyCount();
            for (int i = 0; i < count; i++)
            {
                var name = shader.GetPropertyName(i);
                var type = shader.GetPropertyType(i);
                e.AllProperties.Add(name);
                e.Types[name] = type;

                if (type != ShaderPropertyType.Texture) continue;
                e.TextureProperties.Add(i);

                var flags = shader.GetPropertyFlags(i);
                if ((flags & ShaderPropertyFlags.MainTexture) != 0) e.MainTextureProperty = name;
            }

            if (e.MainTextureProperty == null && e.AllProperties.Contains("_MainTex"))
                e.MainTextureProperty = "_MainTex";

            _cache[shader] = e;
            return e;
        }

        /// <summary>
        /// EN: Analyse every texture property of a material.
        /// ZH: 分析一个材质的全部贴图属性。
        /// </summary>
        /// <param name="material">EN: material to analyse. ZH: 要分析的材质。</param>
        /// <param name="availableUvChannels">EN: number of UV channels the mesh actually provides.
        ///     ZH: 网格实际提供的 UV 通道数。</param>
        public static List<MaterialTextureSlot> Analyse(Material material, int availableUvChannels)
        {
            var result = new List<MaterialTextureSlot>();
            if (material == null || material.shader == null) return result;

            // EN: Third-party providers get first refusal, so unusual shaders can be taught explicitly.
            // ZH: 第三方提供者拥有优先权，使特殊着色器可以被显式教会。
            foreach (var provider in API.ATOExtensionRegistry.ShaderProviders)
            {
                try
                {
                    if (!provider.Supports(material.shader)) continue;
                    var custom = provider.Analyse(material, availableUvChannels);
                    if (custom == null) continue;
                    ATOLog.Trace($"shader '{material.shader.name}' analysed by {provider.GetType().Name}");
                    return custom;
                }
                catch (Exception ex)
                {
                    ATOLog.Warn($"shader provider {provider.GetType().Name} threw: {ex.Message}");
                }
            }

            ShaderCacheEntry entry;
            try
            {
                entry = GetEntry(material.shader);
            }
            catch (Exception ex)
            {
                ATOLog.Warn($"shader analysis failed for '{material.shader?.name}': {ex.Message}");
                return result;
            }

            var shader = material.shader;
            foreach (var index in entry.TextureProperties)
            {
                var propName = shader.GetPropertyName(index);
                var slot = new MaterialTextureSlot
                {
                    Material = material,
                    PropertyName = propName,
                    UvChannel = 0,
                    IsMainTexture = propName == entry.MainTextureProperty,
                };

                // ---- Dimension check / 维度检查 ----
                if (shader.GetPropertyTextureDimension(index) != TextureDimension.Tex2D)
                {
                    continue; // EN: not our business at all. ZH: 完全不在我们的处理范围内。
                }

                var tex = material.GetTexture(propName);
                if (tex == null) continue;

                slot.Texture = tex as Texture2D;
                if (slot.Texture == null)
                {
                    slot.Reject = SlotRejectReason.NotTexture2D;
                    result.Add(slot);
                    continue;
                }

                var flags = shader.GetPropertyFlags(index);
                slot.IsNormalMap = (flags & ShaderPropertyFlags.Normal) != 0
                                   || TextureIntrospection.IsImportedAsNormalMap(slot.Texture)
                                   || LooksLikeNormalName(propName);
                slot.SRGB = !slot.IsNormalMap && TextureIntrospection.IsSRGB(slot.Texture);

                // ---- Known non-mesh-UV slots / 已知非网格 UV 的槽 ----
                if (IsNonMeshUvProperty(propName))
                {
                    slot.Reject = SlotRejectReason.NonMeshUv;
                    result.Add(slot);
                    continue;
                }

                // ---- Tiling / offset / 平铺与偏移 ----
                if ((flags & ShaderPropertyFlags.NoScaleOffset) == 0)
                {
                    var scale = material.GetTextureScale(propName);
                    var offset = material.GetTextureOffset(propName);
                    if (!Approximately(scale, Vector2.one) || !Approximately(offset, Vector2.zero))
                    {
                        slot.Reject = SlotRejectReason.ScaleOffset;
                        result.Add(slot);
                        continue;
                    }
                }

                // ---- lilToon-style scroll / rotate / angle / decal ----
                if (HasNonZeroVector(material, entry, propName + "_ScrollRotate"))
                {
                    slot.Reject = SlotRejectReason.ScrollRotate;
                    result.Add(slot);
                    continue;
                }
                if (HasNonZeroFloat(material, entry, propName + "Angle") ||
                    HasNonZeroFloat(material, entry, propName + "_Angle"))
                {
                    slot.Reject = SlotRejectReason.ScrollRotate;
                    result.Add(slot);
                    continue;
                }
                if (HasNonZeroFloat(material, entry, propName + "IsDecal") ||
                    HasNonZeroFloat(material, entry, propName + "_IsDecal") ||
                    HasNonZeroFloat(material, entry, propName + "IsMSDF") ||
                    HasNonZeroFloat(material, entry, propName + "ShouldCopy") ||
                    HasNonZeroFloat(material, entry, propName + "ShouldFlipMirror") ||
                    HasNonZeroFloat(material, entry, propName + "ShouldFlipCopy"))
                {
                    slot.Reject = SlotRejectReason.Decal;
                    result.Add(slot);
                    continue;
                }

                // ---- UV channel selection / UV 通道选择 ----
                var uvMode = GetFloatOrNull(material, entry, propName + "_UVMode");
                if (uvMode.HasValue)
                {
                    int mode = Mathf.RoundToInt(uvMode.Value);
                    if (mode < 0 || mode > 3)
                    {
                        // EN: lilToon uses 4 == MatCap-like UV, which is not a mesh UV channel.
                        // ZH: lilToon 中 4 代表 MatCap 类 UV，不是网格 UV 通道。
                        slot.Reject = SlotRejectReason.NonMeshUv;
                        result.Add(slot);
                        continue;
                    }
                    slot.UvChannel = mode;
                }

                if (slot.UvChannel >= availableUvChannels)
                {
                    slot.Reject = SlotRejectReason.UvChannelMissing;
                    result.Add(slot);
                    continue;
                }

                result.Add(slot);
            }

            return result;
        }

        /// <summary>
        /// EN: The set of shader property names whose animation would invalidate our analysis for
        ///     <paramref name="propName"/>. Used by the animation scanner.
        /// ZH: 一旦被动画修改就会让我们对 <paramref name="propName"/> 的分析失效的属性名集合。
        ///     供动画扫描器使用。
        /// </summary>
        public static IEnumerable<string> TransformSensitiveProperties(string propName)
        {
            yield return propName + "_ST";
            yield return propName + "_ScrollRotate";
            yield return propName + "Angle";
            yield return propName + "_Angle";
            yield return propName + "_UVMode";
            yield return propName + "IsDecal";
            yield return propName + "_IsDecal";
            yield return propName + "IsMSDF";
            yield return propName + "ShouldCopy";
            yield return propName + "ShouldFlipMirror";
            yield return propName + "ShouldFlipCopy";
        }

        private static bool LooksLikeNormalName(string p)
        {
            var lower = p.ToLowerInvariant();
            return lower.Contains("bumpmap") || lower.Contains("normalmap") || lower.EndsWith("_nm")
                   || lower.Contains("normaltex") || lower.Contains("bump2nd");
        }

        private static bool IsNonMeshUvProperty(string p)
        {
            var lower = p.ToLowerInvariant();
            foreach (var frag in NonMeshUvFragments)
            {
                if (lower.Contains(frag)) return true;
            }
            return false;
        }

        private static bool Approximately(Vector2 a, Vector2 b) =>
            Mathf.Abs(a.x - b.x) < 1e-5f && Mathf.Abs(a.y - b.y) < 1e-5f;

        private static float? GetFloatOrNull(Material m, ShaderCacheEntry e, string name)
        {
            if (!e.Types.TryGetValue(name, out var t)) return null;
            if (t != ShaderPropertyType.Float && t != ShaderPropertyType.Range && t != ShaderPropertyType.Int)
                return null;
            return m.GetFloat(name);
        }

        private static bool HasNonZeroFloat(Material m, ShaderCacheEntry e, string name)
        {
            var v = GetFloatOrNull(m, e, name);
            return v.HasValue && Mathf.Abs(v.Value) > 1e-6f;
        }

        private static bool HasNonZeroVector(Material m, ShaderCacheEntry e, string name)
        {
            if (!e.Types.TryGetValue(name, out var t)) return false;
            if (t != ShaderPropertyType.Vector && t != ShaderPropertyType.Color) return false;
            var v = m.GetVector(name);
            return v.sqrMagnitude > 1e-10f;
        }

        /// <summary>EN: Does the shader declare this property at all? ZH: 着色器是否声明了该属性？</summary>
        public static bool HasProperty(Shader shader, string name)
        {
            if (shader == null) return false;
            return GetEntry(shader).AllProperties.Contains(name);
        }
    }
}
