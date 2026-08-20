// SPDX-License-Identifier: MIT
// EN: Generic shader / material analysis. Works on the shader property table and standard keywords so it
//     keeps working with future lilToon versions; anything it cannot prove safe is treated as whitelisted.
// ZH: 通用的着色器/材质分析。基于着色器属性表与标准关键字工作，因此对未来的 lilToon 版本依然有效；
//     任何无法证明安全的情况都按白名单处理。

using System;
using System.Collections.Generic;
using Net.Fosa.AvatarTextureOptimizer.Editor.API;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// EN: Result of analysing one texture property of one material.
    /// ZH: 分析某材质某贴图属性得到的结果。
    /// </summary>
    public sealed class ATOPropertyAnalysis
    {
        public string PropertyName;
        public Texture2D Texture;
        public ATOTextureRole Role;
        public int UVChannel;
        public bool[] UsedChannels = { true, true, true, true };

        /// <summary>EN: false = must be treated as whitelisted. ZH: false = 必须按白名单处理。</summary>
        public bool Safe = true;

        public string UnsafeReason;
    }

    /// <summary>
    /// EN: Result of analysing a whole material.
    /// ZH: 分析整个材质得到的结果。
    /// </summary>
    public sealed class ATOMaterialAnalysis
    {
        public Material Material;
        public ATOAlphaMode AlphaMode = ATOAlphaMode.Opaque;
        public float Cutoff = 0.5f;
        public readonly List<ATOPropertyAnalysis> Properties = new List<ATOPropertyAnalysis>();

        /// <summary>EN: Shader could not be understood at all. ZH: 完全无法理解该着色器。</summary>
        public bool ShaderUnknown;
    }

    /// <summary>
    /// EN: The analyser. Stateless apart from a small per-shader cache.
    /// ZH: 分析器。除了少量按着色器缓存的数据外无状态。
    /// </summary>
    public sealed class ATOShaderAnalyzer
    {
        private readonly ATOLog _log;
        private readonly Dictionary<Shader, ShaderProfile> _cache = new Dictionary<Shader, ShaderProfile>();

        public ATOShaderAnalyzer(ATOLog log)
        {
            _log = log;
        }

        // ------------------------------------------------------------------ shader profile

        private sealed class ShaderProfile
        {
            public readonly List<TexProp> TextureProperties = new List<TexProp>();
            public readonly HashSet<string> AllProperties = new HashSet<string>(StringComparer.Ordinal);
            public bool IsLilToon;
            public bool HasCutoff;
        }

        private struct TexProp
        {
            public string Name;
            public bool NoScaleOffset;
            public bool IsNormalFlag;
            public bool IsMainTexture;
            public TextureDimension Dimension;
        }

        private ShaderProfile GetProfile(Shader shader)
        {
            if (_cache.TryGetValue(shader, out var p)) return p;

            p = new ShaderProfile();
            var count = shader.GetPropertyCount();
            for (var i = 0; i < count; i++)
            {
                var name = shader.GetPropertyName(i);
                p.AllProperties.Add(name);

                if (name == "_Cutoff" || name == "_AlphaCutoff" || name == "_Cutout") p.HasCutoff = true;

                if (shader.GetPropertyType(i) != ShaderPropertyType.Texture) continue;

                var flags = shader.GetPropertyFlags(i);
                p.TextureProperties.Add(new TexProp
                {
                    Name = name,
                    NoScaleOffset = (flags & ShaderPropertyFlags.NoScaleOffset) != 0,
                    IsNormalFlag = (flags & ShaderPropertyFlags.Normal) != 0,
                    IsMainTexture = (flags & ShaderPropertyFlags.MainTexture) != 0,
                    Dimension = shader.GetPropertyTextureDimension(i),
                });
            }

            var shaderName = shader.name ?? "";
            p.IsLilToon = shaderName.IndexOf("lilToon", StringComparison.OrdinalIgnoreCase) >= 0 ||
                          shaderName.StartsWith("Hidden/lil", StringComparison.OrdinalIgnoreCase) ||
                          p.AllProperties.Contains("_MainTexHSVG");

            _cache[shader] = p;
            _log.Trace("shader",
                $"profile '{shaderName}': {p.TextureProperties.Count} texture properties, lilToon={p.IsLilToon}");
            return p;
        }

        // ------------------------------------------------------------------ public API

        /// <summary>
        /// EN: Analyses one material: alpha mode, cutoff and every Texture2D property.
        /// ZH: 分析一个材质：alpha 模式、cutoff 以及每一个 Texture2D 属性。
        /// </summary>
        public ATOMaterialAnalysis Analyze(Material material)
        {
            var result = new ATOMaterialAnalysis { Material = material };
            if (material == null || material.shader == null)
            {
                result.ShaderUnknown = true;
                return result;
            }

            var profile = GetProfile(material.shader);
            var adapter = FindAdapter(material.shader);
            result.AlphaMode = DetectAlphaMode(material, profile);
            result.Cutoff = DetectCutoff(material, profile);

            foreach (var tp in profile.TextureProperties)
            {
                if (tp.Dimension != TextureDimension.Tex2D) continue;

                var tex = material.GetTexture(tp.Name) as Texture2D;
                if (tex == null) continue;

                var role = DetectRole(material, profile, tp, tex);
                if (adapter != null && adapter.TryGetRole(material, tp.Name, out var adapterRole)) role = adapterRole;

                var uvSafe = true;
                string uvReason = null;
                int uvChannel;

                if (adapter != null)
                {
                    uvChannel = adapter.GetUVChannel(material, tp.Name);
                    if (uvChannel < 0 || uvChannel > 7)
                    {
                        uvSafe = false;
                        uvReason = $"shader adapter reports '{tp.Name}' is not a plain UV lookup";
                        uvChannel = 0;
                    }
                }
                else
                {
                    uvChannel = DetectUVChannel(material, profile, tp, out uvSafe, out uvReason);
                }

                var pa = new ATOPropertyAnalysis
                {
                    PropertyName = tp.Name,
                    Texture = tex,
                    Role = role,
                    UVChannel = uvChannel,
                    UsedChannels = DetectUsedChannels(profile, tp),
                };

                if (!uvSafe)
                {
                    pa.Safe = false;
                    pa.UnsafeReason = uvReason;
                }
                else if (adapter != null)
                {
                    if (!adapter.IsTransformFree(material, tp.Name))
                    {
                        pa.Safe = false;
                        pa.UnsafeReason = $"shader adapter reports a UV transform on '{tp.Name}'";
                    }
                }
                else if (!IsTransformFree(material, profile, tp, out var reason))
                {
                    pa.Safe = false;
                    pa.UnsafeReason = reason;
                }

                result.Properties.Add(pa);
            }

            return result;
        }

        /// <summary>
        /// EN: Returns every float/vector property name that, when animated, invalidates our assumptions
        ///     for the given material. Used by the animation analyser.
        /// ZH: 返回一旦被动画修改就会打破我们假设的所有 float/vector 属性名，供动画分析器使用。
        /// </summary>
        public IEnumerable<string> GetTransformSensitiveProperties(Material material)
        {
            if (material == null || material.shader == null) yield break;
            var profile = GetProfile(material.shader);

            foreach (var tp in profile.TextureProperties)
            {
                if (!tp.NoScaleOffset)
                {
                    yield return tp.Name + "_ST.x";
                    yield return tp.Name + "_ST.y";
                    yield return tp.Name + "_ST.z";
                    yield return tp.Name + "_ST.w";
                }

                foreach (var suffix in UVSuffixes)
                {
                    var candidate = tp.Name + suffix;
                    if (profile.AllProperties.Contains(candidate))
                    {
                        yield return candidate;
                        yield return candidate + ".x";
                        yield return candidate + ".y";
                        yield return candidate + ".z";
                        yield return candidate + ".w";
                    }
                }
            }

            foreach (var extra in GlobalSensitiveProperties)
            {
                if (profile.AllProperties.Contains(extra)) yield return extra;
            }
        }

        /// <summary>EN: Property suffixes that describe a UV transform. ZH: 描述 UV 变换的属性后缀。</summary>
        private static readonly string[] UVSuffixes =
        {
            "_ScrollRotate", "_UVMode", "_Angle", "_DecalAnimation", "_DecalSubParam", "_IsDecal",
        };

        /// <summary>EN: Material-wide properties that change how UVs are sampled. ZH: 影响 UV 采样方式的全局属性。</summary>
        private static readonly string[] GlobalSensitiveProperties =
        {
            "_UVSec", "_ShiftBackfaceUV", "_UDIMDiscardMode", "_UDIMDiscardUV",
            "_Cutoff", "_AlphaCutoff", "_Cutout", "_Mode", "_AlphaMaskMode", "_Invisible",
        };

        /// <summary>
        /// EN: Returns the first third party adapter that claims the shader, if any.
        /// ZH: 返回第一个认领该着色器的第三方适配器（若存在）。
        /// </summary>
        private static IATOShaderAdapter FindAdapter(Shader shader)
        {
            foreach (var adapter in ATOExtensions.ShaderAdapters)
            {
                try
                {
                    if (adapter.CanHandle(shader)) return adapter;
                }
                catch (Exception e)
                {
                    Debug.LogError($"{ATOLog.Prefix}[api] shader adapter threw: {e}");
                }
            }

            return null;
        }

        // ------------------------------------------------------------------ helpers

        private static ATOAlphaMode DetectAlphaMode(Material m, ShaderProfile profile)
        {
            // EN: 1) Unity standard style "_Mode" float. ZH: 1) Unity 标准着色器风格的 "_Mode"。
            if (profile.AllProperties.Contains("_Mode"))
            {
                var mode = m.GetFloat("_Mode");
                // 0 Opaque, 1 Cutout, 2 Fade, 3 Transparent
                if (Mathf.Approximately(mode, 1f)) return ATOAlphaMode.Cutout;
                if (mode >= 2f) return ATOAlphaMode.Blend;
                return ATOAlphaMode.Opaque;
            }

            // EN: 2) RenderType tag, the de-facto standard keyword. ZH: 2) RenderType 标签，事实上的标准关键字。
            var renderType = m.GetTag("RenderType", false, "");
            if (renderType.Equals("TransparentCutout", StringComparison.OrdinalIgnoreCase)) return ATOAlphaMode.Cutout;
            if (renderType.Equals("Transparent", StringComparison.OrdinalIgnoreCase)) return ATOAlphaMode.Blend;

            // EN: 3) lilToon encodes the mode in the shader name. ZH: 3) lilToon 把模式写在着色器名字里。
            var shaderName = m.shader != null ? m.shader.name : "";
            if (shaderName.IndexOf("cutout", StringComparison.OrdinalIgnoreCase) >= 0) return ATOAlphaMode.Cutout;
            if (shaderName.IndexOf("trans", StringComparison.OrdinalIgnoreCase) >= 0 ||
                shaderName.IndexOf("fur", StringComparison.OrdinalIgnoreCase) >= 0 ||
                shaderName.IndexOf("gem", StringComparison.OrdinalIgnoreCase) >= 0 ||
                shaderName.IndexOf("refraction", StringComparison.OrdinalIgnoreCase) >= 0) return ATOAlphaMode.Blend;

            // EN: 4) Render queue as the last resort. ZH: 4) 最后用渲染队列兜底。
            if (m.renderQueue >= (int)RenderQueue.Transparent) return ATOAlphaMode.Blend;
            if (m.renderQueue >= (int)RenderQueue.AlphaTest) return ATOAlphaMode.Cutout;

            return ATOAlphaMode.Opaque;
        }

        private static float DetectCutoff(Material m, ShaderProfile profile)
        {
            if (profile.AllProperties.Contains("_Cutoff")) return m.GetFloat("_Cutoff");
            if (profile.AllProperties.Contains("_AlphaCutoff")) return m.GetFloat("_AlphaCutoff");
            if (profile.AllProperties.Contains("_Cutout")) return m.GetFloat("_Cutout");
            return 0.5f;
        }

        private static ATOTextureRole DetectRole(Material m, ShaderProfile profile, TexProp tp, Texture2D tex)
        {
            // EN: The [Normal] attribute is authoritative. ZH: [Normal] 特性是权威依据。
            if (tp.IsNormalFlag) return ATOTextureRole.Normal;

            var lower = tp.Name.ToLowerInvariant();
            if (lower.Contains("normal") || lower.Contains("bump") || lower.EndsWith("nrm"))
                return ATOTextureRole.Normal;

            // EN: Importer says "normal map" -> normal, regardless of the property name.
            // ZH: 导入器标记为法线贴图时无条件按法线处理。
            var path = AssetDatabase.GetAssetPath(tex);
            if (!string.IsNullOrEmpty(path) &&
                AssetImporter.GetAtPath(path) is TextureImporter ti && ti.textureType == TextureImporterType.NormalMap)
                return ATOTextureRole.Normal;

            var isMask = lower.Contains("mask") || lower.Contains("smoothness") || lower.Contains("metallic") ||
                         lower.Contains("occlusion") || lower.Contains("roughness") || lower.Contains("height") ||
                         lower.Contains("parallax") || lower.Contains("dissolve") || lower.Contains("noise") ||
                         lower.Contains("width") || lower.Contains("strength") || lower.Contains("blur") ||
                         lower.Contains("border") || lower.Contains("alphamask");

            if (isMask) return ATOTextureRole.Grayscale;

            // EN: Colour texture: whether alpha matters is decided later per material alpha mode.
            // ZH: 颜色贴图：alpha 是否重要稍后由材质的 alpha 模式决定。
            return ATOTextureRole.ColorOpaque;
        }

        private static bool[] DetectUsedChannels(ShaderProfile profile, TexProp tp)
        {
            var lower = tp.Name.ToLowerInvariant();

            // EN: lilToon masks are sampled per channel through a companion "*_RGBA"/blend property, but we
            //     cannot know which channel without shader code analysis, so we stay conservative and keep
            //     every non-empty channel (the writer downgrades to R8 only when the content allows it).
            // ZH: lilToon 的蒙版通过配套的 "*_RGBA"/blend 属性逐通道采样，但不做着色器代码分析就无法确定通道，
            //     所以保守起见保留所有非空通道（写出阶段会根据实际内容再决定能否降到 R8）。
            if (lower.Contains("alphamask")) return new[] { true, false, false, false };

            return new[] { true, true, true, true };
        }

        private static int DetectUVChannel(Material m, ShaderProfile profile, TexProp tp, out bool safe,
            out string reason)
        {
            safe = true;
            reason = null;

            // EN: lilToon style "<prop>_UVMode": 0..3 = UV0..UV3, >=4 = MatCap/Rim (not UV sampled).
            // ZH: lilToon 风格的 "<prop>_UVMode"：0..3 表示 UV0..UV3，>=4 表示 MatCap/Rim（不按 UV 采样）。
            var uvModeProp = tp.Name + "_UVMode";
            if (profile.AllProperties.Contains(uvModeProp))
            {
                var v = Mathf.RoundToInt(m.GetFloat(uvModeProp));
                if (v < 0 || v > 3)
                {
                    safe = false;
                    reason = $"{uvModeProp}={v} is not a plain UV lookup";
                    return 0;
                }

                return v;
            }

            // EN: Unity standard "_UVSec" applies to the secondary map set. ZH: Unity 标准的 "_UVSec" 控制第二套 UV。
            if (profile.AllProperties.Contains("_UVSec") &&
                (tp.Name == "_DetailAlbedoMap" || tp.Name == "_DetailNormalMap" || tp.Name == "_DetailMask"))
            {
                return Mathf.Clamp(Mathf.RoundToInt(m.GetFloat("_UVSec")), 0, 1);
            }

            return 0;
        }

        private static bool IsTransformFree(Material m, ShaderProfile profile, TexProp tp, out string reason)
        {
            reason = null;

            if (!tp.NoScaleOffset)
            {
                var scale = m.GetTextureScale(tp.Name);
                var offset = m.GetTextureOffset(tp.Name);
                if (!Approximately(scale, Vector2.one) || !Approximately(offset, Vector2.zero))
                {
                    reason = $"{tp.Name}_ST = ({scale.x}, {scale.y}, {offset.x}, {offset.y})";
                    return false;
                }
            }

            var scrollProp = tp.Name + "_ScrollRotate";
            if (profile.AllProperties.Contains(scrollProp))
            {
                var v = m.GetVector(scrollProp);
                if (v.sqrMagnitude > 1e-12f)
                {
                    reason = $"{scrollProp} = {v}";
                    return false;
                }
            }

            var angleProp = tp.Name + "Angle";
            if (profile.AllProperties.Contains(angleProp) && Mathf.Abs(m.GetFloat(angleProp)) > 1e-6f)
            {
                reason = $"{angleProp} = {m.GetFloat(angleProp)}";
                return false;
            }

            foreach (var decalProp in new[] { tp.Name + "IsDecal", tp.Name + "_IsDecal" })
            {
                if (profile.AllProperties.Contains(decalProp) && m.GetFloat(decalProp) > 0.5f)
                {
                    reason = $"{decalProp} is enabled";
                    return false;
                }
            }

            // EN: lilToon can shift the backface UV by one tile which breaks any repacking.
            // ZH: lilToon 可以把背面 UV 平移一格，这会破坏任何重排。
            if (profile.AllProperties.Contains("_ShiftBackfaceUV") && m.GetFloat("_ShiftBackfaceUV") > 0.5f)
            {
                reason = "_ShiftBackfaceUV is enabled";
                return false;
            }

            // EN: UDIM discard relies on UV tiles outside [0,1]. ZH: UDIM 丢弃依赖 [0,1] 之外的 UV 分块。
            if (profile.AllProperties.Contains("_UDIMDiscardCompile") &&
                m.GetFloat("_UDIMDiscardCompile") > 0.5f)
            {
                reason = "_UDIMDiscardCompile is enabled";
                return false;
            }

            return true;
        }

        private static bool Approximately(Vector2 a, Vector2 b) =>
            Mathf.Abs(a.x - b.x) < 1e-5f && Mathf.Abs(a.y - b.y) < 1e-5f;
    }
}
