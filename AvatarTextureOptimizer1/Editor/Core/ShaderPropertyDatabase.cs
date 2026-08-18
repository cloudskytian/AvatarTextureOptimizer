// ShaderPropertyDatabase.cs / ShaderPropertyDatabase.cs
// Database of texture-property conventions used by common shaders (Unity Standard, lilToon, UTS, etc.).
// The goal is to identify which material properties are textures, which UV channel they use,
// whether they are normal maps, and whether they have UV tiling/offset (ST) animation.
// 常见着色器（Unity Standard、lilToon、UTS等）使用的贴图属性约定数据库。
// 目的是识别哪些材质属性是贴图、使用哪个UV通道、是否为法线贴图、是否有UV平移/缩放（ST）动画。

using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace net.fosa.avatar_texture_optimizer.Editor.Core
{
    /// <summary>
    /// The type of a texture property from the point of view of ATO.
    /// 从ATO角度看一个贴图属性的类型。
    /// </summary>
    public enum TexturePropertyKind
    {
        BaseColor,    // Albedo / main color
        Normal,       // Normal map
        Mask,         // Mask / metallic / smoothness / AO / emission / matcap / rim / etc.
        Grayscale,    // Single-channel map (e.g. roughness only)
        Ignored,      // Screen-space / matcap / noise / other non-UV-mapped
    }

    /// <summary>
    /// How the material treats alpha / transparency.
    /// 材质如何处理alpha/透明。
    /// </summary>
    public enum AlphaMode
    {
        Opaque,
        Cutout,
        Blend,
    }

    /// <summary>
    /// Describes a known texture property on a shader.
    /// 描述着色器上一个已知贴图属性。
    /// </summary>
    public class TexturePropertyDescriptor
    {
        public string PropertyName;       // e.g. "_MainTex"
        public TexturePropertyKind Kind;
        public int DefaultUVChannel = 0;  // UV0, UV1, ...
        public bool HasSTProperty = true; // e.g. "_MainTex_ST" exists?
        public string STPropertyName;     // override for ST suffix, default: PropertyName + "_ST"
        public bool IsNormalMap;
        public string UVModePropertyName; // e.g. "_MainTex_UVMode" (liltoon allows UV1 etc via this)
        public string EnableKeyword;      // shader keyword or toggle float that enables this texture; null => always enabled
        public string EnablePropertyName; // float property toggle (e.g. "_UseBumpMap"); null => always
        public float EnableIfValue = 0.5f; // if the toggle property is >= this, the texture is active
    }

    /// <summary>
    /// Describes a known shader family.
    /// 描述一个已知着色器家族。
    /// </summary>
    public class ShaderDescriptor
    {
        public string NameMatch;                 // substring of shader name to match / 要匹配的着色器名子串
        public List<TexturePropertyDescriptor> Textures = new();
        public string RenderTypeProperty;        // e.g. "_Mode" (Standard), "_TransparentMode" (liltoon)
        public string RenderTypeKeywordPrefix;   // e.g. "_ALPHABLEND_ON"
        public string CutoffPropertyName = "_Cutoff";
        public int OpaqueValue = 0;
        public int CutoutValue = 1;
        public int TransparentValue = 2;
        public bool IsCaseSensitive = false;
    }

    /// <summary>
    /// Static database of known shader texture properties. Plugins/advanced users can register additional descriptors.
    /// 已知着色器贴图属性静态数据库。高级用户/第三方插件可注册更多描述符。
    /// </summary>
    public static class ShaderPropertyDatabase
    {
        private static readonly List<ShaderDescriptor> _descriptors = new();
        private static readonly Dictionary<Shader, ShaderDescriptor> _cache = new();

        static ShaderPropertyDatabase()
        {
            RegisterUnityStandard();
            RegisterLilToon();
            RegisterUnlit();
            RegisterUTS();
            RegisterVRChatFallback();
        }

        /// <summary>
        /// Register a new shader descriptor (extension API).
        /// 注册新的着色器描述符（扩展API）。
        /// </summary>
        public static void Register(ShaderDescriptor desc) { _descriptors.Add(desc); _cache.Clear(); }

        private static ShaderDescriptor _genericFallback;

        public static ShaderDescriptor GetGenericFallback()
        {
            if (_genericFallback != null) return _genericFallback;
            _genericFallback = new ShaderDescriptor
            {
                NameMatch = "*",
                Textures = new List<TexturePropertyDescriptor>
                {
                    new TexturePropertyDescriptor
                    {
                        PropertyName = "_MainTex",
                        Kind = TexturePropertyKind.BaseColor,
                        DefaultUVChannel = 0,
                        STPropertyName = "_MainTex_ST",
                    }
                },
                RenderTypeProperty = null,
                CutoffPropertyName = "_Cutoff"
            };
            return _genericFallback;
        }

        /// <summary>
        /// Returns a ShaderDescriptor for the given shader. Falls back to a generic
        /// "_MainTex on UV0" descriptor for unknown shaders; returns null if the shader
        /// is totally unknown AND the user explicitly wants to skip unknowns.
        /// 返回给定着色器的ShaderDescriptor。未知着色器回退到通用"_MainTex在UV0"描述符。
        /// </summary>
        public static ShaderDescriptor GetDescriptor(Shader shader)
        {
            if (shader == null) return GetGenericFallback();
            if (_cache.TryGetValue(shader, out var cached)) return cached;

            foreach (var d in _descriptors)
            {
                bool match;
                if (d.NameMatch == "*") match = true;
                else if (d.IsCaseSensitive) match = shader.name.Contains(d.NameMatch);
                else match = shader.name.IndexOf(d.NameMatch, StringComparison.OrdinalIgnoreCase) >= 0;

                if (match)
                {
                    // Augment with any extra _ST properties we detect
                    // 用检测到的额外_ST属性扩充
                    var augmented = AugmentWithDetectedProperties(d, shader);
                    _cache[shader] = augmented;
                    return augmented;
                }
            }
            var fallback = AugmentWithDetectedProperties(GetGenericFallback(), shader);
            _cache[shader] = fallback;
            return fallback;
        }

        private static ShaderDescriptor AugmentWithDetectedProperties(ShaderDescriptor desc, Shader shader)
        {
            // Create a clone with additional detected texture properties that are not already listed
            // 克隆并补充未列出但检测到的额外贴图属性
            var clone = new ShaderDescriptor
            {
                NameMatch = desc.NameMatch,
                RenderTypeProperty = desc.RenderTypeProperty,
                RenderTypeKeywordPrefix = desc.RenderTypeKeywordPrefix,
                CutoffPropertyName = desc.CutoffPropertyName,
                OpaqueValue = desc.OpaqueValue,
                CutoutValue = desc.CutoutValue,
                TransparentValue = desc.TransparentValue,
                IsCaseSensitive = desc.IsCaseSensitive,
            };
            clone.Textures.AddRange(desc.Textures);

            var knownNames = new HashSet<string>();
            foreach (var t in clone.Textures) knownNames.Add(t.PropertyName);

            int count = ShaderUtil.GetPropertyCount(shader);
            for (int i = 0; i < count; i++)
            {
                if (ShaderUtil.GetPropertyType(shader, i) == ShaderUtil.ShaderPropertyType.TexEnv)
                {
                    var name = ShaderUtil.GetPropertyName(shader, i);
                    if (!knownNames.Contains(name))
                    {
                        // Best-effort guess: treat unknown textures as Mask (safer than ignoring)
                        // 尽力猜测：未知贴图当作Mask（比忽略更安全）
                        bool isNormal = name.IndexOf("normal", StringComparison.OrdinalIgnoreCase) >= 0
                                        || name.IndexOf("bump", StringComparison.OrdinalIgnoreCase) >= 0;
                        clone.Textures.Add(new TexturePropertyDescriptor
                        {
                            PropertyName = name,
                            Kind = isNormal ? TexturePropertyKind.Normal : TexturePropertyKind.Mask,
                            DefaultUVChannel = 0,
                            IsNormalMap = isNormal,
                            STPropertyName = name + "_ST",
                            HasSTProperty = true,
                        });
                    }
                }
            }
            return clone;
        }

        // ---- Known shader registrations / 已知着色器注册 ----

        private static void RegisterUnityStandard()
        {
            _descriptors.Add(new ShaderDescriptor
            {
                NameMatch = "Standard",
                RenderTypeProperty = "_Mode",
                CutoffPropertyName = "_Cutoff",
                OpaqueValue = 0, CutoutValue = 1, TransparentValue = 2,
                Textures = new List<TexturePropertyDescriptor>
                {
                    new TexturePropertyDescriptor { PropertyName = "_MainTex", Kind = TexturePropertyKind.BaseColor, STPropertyName = "_MainTex_ST" },
                    new TexturePropertyDescriptor { PropertyName = "_BumpMap", Kind = TexturePropertyKind.Normal, IsNormalMap = true, STPropertyName = "_BumpMap_ST" },
                    new TexturePropertyDescriptor { PropertyName = "_MetallicGlossMap", Kind = TexturePropertyKind.Mask, STPropertyName = "_MetallicGlossMap_ST" },
                    new TexturePropertyDescriptor { PropertyName = "_OcclusionMap", Kind = TexturePropertyKind.Mask, STPropertyName = "_OcclusionMap_ST" },
                    new TexturePropertyDescriptor { PropertyName = "_EmissionMap", Kind = TexturePropertyKind.Mask, STPropertyName = "_EmissionMap_ST" },
                    new TexturePropertyDescriptor { PropertyName = "_ParallaxMap", Kind = TexturePropertyKind.Mask, STPropertyName = "_ParallaxMap_ST" },
                    new TexturePropertyDescriptor { PropertyName = "_DetailMask", Kind = TexturePropertyKind.Ignored },
                    new TexturePropertyDescriptor { PropertyName = "_DetailAlbedoMap", Kind = TexturePropertyKind.Ignored },
                    new TexturePropertyDescriptor { PropertyName = "_DetailNormalMap", Kind = TexturePropertyKind.Ignored },
                }
            });
        }

        private static void RegisterUnlit()
        {
            _descriptors.Add(new ShaderDescriptor
            {
                NameMatch = "Unlit",
                Textures = new List<TexturePropertyDescriptor>
                {
                    new TexturePropertyDescriptor { PropertyName = "_MainTex", Kind = TexturePropertyKind.BaseColor, STPropertyName = "_MainTex_ST" },
                }
            });
        }

        private static void RegisterLilToon()
        {
            // Register multiple variants (liltoon has many shader names)
            void AddLil(string match)
            {
                _descriptors.Add(new ShaderDescriptor
                {
                    NameMatch = match,
                    RenderTypeProperty = "_TransparentMode",
                    CutoffPropertyName = "_Cutoff",
                    OpaqueValue = 0, CutoutValue = 1, TransparentValue = 2,
                    Textures = new List<TexturePropertyDescriptor>
                    {
                        new TexturePropertyDescriptor { PropertyName = "_MainTex", Kind = TexturePropertyKind.BaseColor, STPropertyName = "_MainTex_ST" },
                        new TexturePropertyDescriptor { PropertyName = "_Main2ndTex", Kind = TexturePropertyKind.BaseColor, STPropertyName = "_Main2ndTex_ST", UVModePropertyName = "_Main2ndTex_UVMode" },
                        new TexturePropertyDescriptor { PropertyName = "_Main3rdTex", Kind = TexturePropertyKind.BaseColor, STPropertyName = "_Main3rdTex_ST", UVModePropertyName = "_Main3rdTex_UVMode" },
                        new TexturePropertyDescriptor { PropertyName = "_AlphaMask", Kind = TexturePropertyKind.Mask, STPropertyName = "_AlphaMask_ST" },
                        new TexturePropertyDescriptor { PropertyName = "_BumpMap", Kind = TexturePropertyKind.Normal, IsNormalMap = true, STPropertyName = "_BumpMap_ST", EnablePropertyName = "_UseBumpMap", EnableIfValue = 0.5f },
                        new TexturePropertyDescriptor { PropertyName = "_Bump2ndMap", Kind = TexturePropertyKind.Normal, IsNormalMap = true, STPropertyName = "_Bump2ndMap_ST", UVModePropertyName = "_Bump2ndMap_UVMode", EnablePropertyName = "_UseBump2ndMap", EnableIfValue = 0.5f },
                        new TexturePropertyDescriptor { PropertyName = "_EmissionMap", Kind = TexturePropertyKind.Mask, STPropertyName = "_EmissionMap_ST", UVModePropertyName = "_EmissionMap_UVMode" },
                        new TexturePropertyDescriptor { PropertyName = "_Emission2ndMap", Kind = TexturePropertyKind.Mask, STPropertyName = "_Emission2ndMap_ST", UVModePropertyName = "_Emission2ndMap_UVMode" },
                        new TexturePropertyDescriptor { PropertyName = "_ShadowStrengthMask", Kind = TexturePropertyKind.Mask },
                        new TexturePropertyDescriptor { PropertyName = "_ShadowBorderMask", Kind = TexturePropertyKind.Mask },
                        new TexturePropertyDescriptor { PropertyName = "_ShadowBlurMask", Kind = TexturePropertyKind.Mask },
                        new TexturePropertyDescriptor { PropertyName = "_ShadowColorTex", Kind = TexturePropertyKind.Ignored },
                        new TexturePropertyDescriptor { PropertyName = "_Shadow2ndColorTex", Kind = TexturePropertyKind.Ignored },
                        new TexturePropertyDescriptor { PropertyName = "_Shadow3rdColorTex", Kind = TexturePropertyKind.Ignored },
                        new TexturePropertyDescriptor { PropertyName = "_RimShadeMask", Kind = TexturePropertyKind.Mask },
                        new TexturePropertyDescriptor { PropertyName = "_MatCapTex", Kind = TexturePropertyKind.Ignored, EnablePropertyName = "_UseMatCap", EnableIfValue = 0.5f },
                        new TexturePropertyDescriptor { PropertyName = "_MatCap2ndTex", Kind = TexturePropertyKind.Ignored, EnablePropertyName = "_UseMatCap2nd", EnableIfValue = 0.5f },
                        new TexturePropertyDescriptor { PropertyName = "_RimColorTex", Kind = TexturePropertyKind.Mask },
                        new TexturePropertyDescriptor { PropertyName = "_GlitterColorTex", Kind = TexturePropertyKind.Ignored, EnablePropertyName = "_UseGlitter", EnableIfValue = 0.5f },
                        new TexturePropertyDescriptor { PropertyName = "_OutlineWidthMask", Kind = TexturePropertyKind.Mask },
                        new TexturePropertyDescriptor { PropertyName = "_AIShadeMap", Kind = TexturePropertyKind.Mask, EnablePropertyName = "_UseAIShade", EnableIfValue = 0.5f },
                        new TexturePropertyDescriptor { PropertyName = "_AnisotropyTangentMap", Kind = TexturePropertyKind.Mask, EnablePropertyName = "_UseAnisotropy", EnableIfValue = 0.5f },
                        new TexturePropertyDescriptor { PropertyName = "_AnisotropyScaleMask", Kind = TexturePropertyKind.Mask, EnablePropertyName = "_UseAnisotropy", EnableIfValue = 0.5f },
                        new TexturePropertyDescriptor { PropertyName = "_ReflectionColorTex", Kind = TexturePropertyKind.Ignored, EnablePropertyName = "_UseReflection", EnableIfValue = 0.5f },
                        new TexturePropertyDescriptor { PropertyName = "_MatCapBumpMap", Kind = TexturePropertyKind.Ignored, EnablePropertyName = "_MatCapCustomNormal", EnableIfValue = 0.5f },
                        new TexturePropertyDescriptor { PropertyName = "_MatCap2ndBumpMap", Kind = TexturePropertyKind.Ignored, EnablePropertyName = "_MatCap2ndCustomNormal", EnableIfValue = 0.5f },
                        new TexturePropertyDescriptor { PropertyName = "_StencilRef", Kind = TexturePropertyKind.Ignored },
                        new TexturePropertyDescriptor { PropertyName = "_DissolveMask", Kind = TexturePropertyKind.Mask },
                        // Screen-space / matcap / fake-reflection / special UVs - not mesh UV mapped → ignored
                        // 屏幕空间/matcap/假反射/特殊UV - 不映射到mesh UV → 忽略
                        new TexturePropertyDescriptor { PropertyName = "_GlitterShapeTex", Kind = TexturePropertyKind.Ignored, EnablePropertyName = "_UseGlitter", EnableIfValue = 0.5f },
                        new TexturePropertyDescriptor { PropertyName = "_AudioLinkMask", Kind = TexturePropertyKind.Ignored, EnablePropertyName = "_UseAudioLink", EnableIfValue = 0.5f },
                        new TexturePropertyDescriptor { PropertyName = "_AudioLinkMap", Kind = TexturePropertyKind.Ignored, EnablePropertyName = "_UseAudioLink", EnableIfValue = 0.5f },
                        new TexturePropertyDescriptor { PropertyName = "_FurNoiseTex", Kind = TexturePropertyKind.Ignored, EnablePropertyName = "_UseFur", EnableIfValue = 0.5f },
                        new TexturePropertyDescriptor { PropertyName = "_FurMaskTex", Kind = TexturePropertyKind.Ignored, EnablePropertyName = "_UseFur", EnableIfValue = 0.5f },
                        new TexturePropertyDescriptor { PropertyName = "_GemReflectionTex", Kind = TexturePropertyKind.Ignored, EnablePropertyName = "_UseGem", EnableIfValue = 0.5f },
                        new TexturePropertyDescriptor { PropertyName = "_TessNoiseTex", Kind = TexturePropertyKind.Ignored, EnablePropertyName = "_UseTess", EnableIfValue = 0.5f },
                        new TexturePropertyDescriptor { PropertyName = "_FakeShadowMaskTex", Kind = TexturePropertyKind.Mask },
                        new TexturePropertyDescriptor { PropertyName = "_DistanceFadeAlphaMask", Kind = TexturePropertyKind.Mask },
                        new TexturePropertyDescriptor { PropertyName = "_BackFaceColorTex", Kind = TexturePropertyKind.Ignored, EnablePropertyName = "_UseBackface", EnableIfValue = 0.5f },
                    }
                });
            }
            AddLil("lilToon");
            AddLil("_lil");
            AddLil("Hidden/liltoon");
        }

        private static void RegisterUTS()
        {
            _descriptors.Add(new ShaderDescriptor
            {
                NameMatch = "UnityChanToonShader",
                RenderTypeProperty = "_BaseColor_Step",
                CutoffPropertyName = "_ClippingCanceler",
                Textures = new List<TexturePropertyDescriptor>
                {
                    new TexturePropertyDescriptor { PropertyName = "_MainTex", Kind = TexturePropertyKind.BaseColor },
                    new TexturePropertyDescriptor { PropertyName = "_BumpMap", Kind = TexturePropertyKind.Normal, IsNormalMap = true },
                    new TexturePropertyDescriptor { PropertyName = "_ShadeTexture", Kind = TexturePropertyKind.BaseColor },
                    new TexturePropertyDescriptor { PropertyName = "_EmissionMap", Kind = TexturePropertyKind.Mask },
                    new TexturePropertyDescriptor { PropertyName = "_OcclusionMap", Kind = TexturePropertyKind.Mask },
                    new TexturePropertyDescriptor { PropertyName = "_SphereAddMask", Kind = TexturePropertyKind.Ignored }, // MatCap
                    new TexturePropertyDescriptor { PropertyName = "_MatCap", Kind = TexturePropertyKind.Ignored },
                    new TexturePropertyDescriptor { PropertyName = "_OutlineWidthTexture", Kind = TexturePropertyKind.Mask },
                }
            });
        }

        private static void RegisterVRChatFallback()
        {
            // VRChat/Mobile/... shaders are typically simple unlit/Standard variants; generic fallback covers them.
        }

        // -- Helpers for reading material properties / 读取材质属性工具 --

        /// <summary>
        /// Determine alpha mode of a material.
        /// 确定材质的alpha模式。
        /// </summary>
        public static AlphaMode GetAlphaMode(Material mat, ShaderDescriptor desc)
        {
            if (mat == null) return AlphaMode.Opaque;
            if (mat.IsKeywordEnabled("_ALPHABLEND_ON") || mat.IsKeywordEnabled("_TRANSPARENT") || mat.IsKeywordEnabled("_ALPHAPREMULTIPLY_ON"))
                return AlphaMode.Blend;
            if (mat.IsKeywordEnabled("_ALPHATEST_ON") || mat.IsKeywordEnabled("_ALPHACLIP_ON"))
                return AlphaMode.Cutout;

            if (!string.IsNullOrEmpty(desc.RenderTypeProperty) && mat.HasProperty(desc.RenderTypeProperty))
            {
                float mode = mat.GetFloat(desc.RenderTypeProperty);
                if (Mathf.Abs(mode - desc.TransparentValue) < 0.01f) return AlphaMode.Blend;
                if (Mathf.Abs(mode - desc.CutoutValue) < 0.01f) return AlphaMode.Cutout;
            }

            string tag = mat.GetTag("RenderType", false, "");
            if (tag.IndexOf("Transparent", StringComparison.OrdinalIgnoreCase) >= 0) return AlphaMode.Blend;
            if (tag.IndexOf("TransparentCutout", StringComparison.OrdinalIgnoreCase) >= 0) return AlphaMode.Cutout;

            return AlphaMode.Opaque;
        }

        /// <summary>
        /// Returns the cutoff value (0..1) for a Cutout material; default 0.5.
        /// 返回Cutout材质的cutoff阈值（0..1）；默认0.5。
        /// </summary>
        public static float GetCutoff(Material mat, ShaderDescriptor desc)
        {
            if (mat != null && !string.IsNullOrEmpty(desc.CutoffPropertyName) && mat.HasProperty(desc.CutoffPropertyName))
                return mat.GetFloat(desc.CutoffPropertyName);
            return 0.5f;
        }

        /// <summary>
        /// Returns whether the texture property is currently active on the material (respecting enable keywords/properties).
        /// 返回该贴图属性当前是否在材质上被启用（考虑启用关键字/属性）。
        /// </summary>
        public static bool IsPropertyActive(Material mat, TexturePropertyDescriptor prop)
        {
            if (mat == null || prop == null) return false;
            if (!mat.HasProperty(prop.PropertyName)) return false;
            if (mat.GetTexture(prop.PropertyName) == null) return false;
            if (!string.IsNullOrEmpty(prop.EnableKeyword) && !mat.IsKeywordEnabled(prop.EnableKeyword)) return false;
            if (!string.IsNullOrEmpty(prop.EnablePropertyName) && mat.HasProperty(prop.EnablePropertyName))
            {
                if (mat.GetFloat(prop.EnablePropertyName) < prop.EnableIfValue) return false;
            }
            return true;
        }

        /// <summary>
        /// Returns the UV channel used by a property on a material (respecting UVMode properties for liltoon-style 0/1/2/3 UV selection).
        /// 返回一个属性在材质上使用的UV通道（考虑liltoon风格的_UVMode选择0/1/2/3 UV）。
        /// </summary>
        public static int GetUVChannel(Material mat, TexturePropertyDescriptor prop)
        {
            int ch = prop.DefaultUVChannel;
            if (!string.IsNullOrEmpty(prop.UVModePropertyName) && mat.HasProperty(prop.UVModePropertyName))
            {
                int mode = Mathf.RoundToInt(mat.GetFloat(prop.UVModePropertyName));
                // liltoon UVMode: 0=UV0, 1=UV1, 2=UV2, 3=UV3, 4=UV4... (when Pan is 0)
                if (mode >= 0 && mode <= 7) ch = mode;
            }
            return ch;
        }

        /// <summary>
        /// Returns true if the property has an ST (Scale/Offset) with non-default values or animated, or if the UVMode involves non-zero ScrollRotate etc.
        /// 返回属性是否存在非默认ST（缩放/偏移）或动画ST，或UVMode包含ScrollRotate等。
        /// </summary>
        public static bool HasNonDefaultST(Material mat, TexturePropertyDescriptor prop)
        {
            string stName = string.IsNullOrEmpty(prop.STPropertyName) ? prop.PropertyName + "_ST" : prop.STPropertyName;
            if (!string.IsNullOrEmpty(stName) && mat.HasProperty(stName))
            {
                Vector4 st = mat.GetVector(stName);
                if (Mathf.Abs(st.x - 1f) > 0.001f || Mathf.Abs(st.y - 1f) > 0.001f || Mathf.Abs(st.z) > 0.001f || Mathf.Abs(st.w) > 0.001f)
                    return true;
            }
            // liltoon ScrollRotate adds animation; if the property is non-zero, UV is rotated/scrolled
            string srName = prop.PropertyName + "_ScrollRotate";
            if (mat.HasProperty(srName))
            {
                Vector4 sr = mat.GetVector(srName);
                if (Mathf.Abs(sr.x) > 0.001f || Mathf.Abs(sr.y) > 0.001f || Mathf.Abs(sr.z) > 0.001f)
                    return true;
            }
            return false;
        }
    }
}
