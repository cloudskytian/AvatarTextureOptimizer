// AvatarTextureOptimizer - TextureClassifier
// EN: Auto-analyzes the shader property table (works for liltoon, standard, and unknown shaders), classifies each
// texture property by name patterns + [Normal] attributes + special-UV exclusions, and checks ST transforms.
// CN: 自动分析着色器属性表（兼容 liltoon、标准与未知着色器），按名称模式 + [Normal] 属性 + 特殊 UV 排除规则
//     分类每个贴图属性，并检查 ST 变换。
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace net.fosa.avatar_texture_optimizer
{
    /// <summary>EN: Cached per-shader property analysis. / CN: 每着色器属性分析缓存。</summary>
    public sealed class ShaderPropertyInfo
    {
        public string name;
        public int id;
        public bool isTexture;
        public bool isNormal;          // [Normal] 属性
        public bool noScaleOffset;     // [NoScaleOffset]
        public PropertyCategory category;
        public bool hasUvMode;         // 存在同名 _UVMode 伴随属性
        public int uvModeId;
    }

    public enum PropertyCategory
    {
        Albedo,        // 主色（sRGB 彩色）
        Normal,        // 法线
        GrayMask,      // 灰度/蒙版
        SpecialUv,     // 非网格 UV（matcap/反射/全景/cubemap）→ 不可优化
        Unsupported    // 无法判断 → 白名单
    }

    /// <summary>
    /// EN: Classifies materials & textures for optimization eligibility.
    /// CN: 分类材质与贴图，判定可优化性。
    /// </summary>
    public static class TextureClassifier
    {
        private static readonly Dictionary<Shader, ShaderPropertyInfo[]> Cache =
            new Dictionary<Shader, ShaderPropertyInfo[]>();

        // EN: Name-pattern rules (order matters). / CN: 名称模式规则（顺序敏感）。
        private static readonly (string pattern, PropertyCategory cat, bool contains)[] Patterns =
        {
            // Normal maps: [Normal] flag is the primary signal; name patterns as fallback.
            ("_Normal", PropertyCategory.Normal, true),
            ("_Bump", PropertyCategory.Normal, true),
            ("DetailNormal", PropertyCategory.Normal, true),
            ("_NMap", PropertyCategory.Normal, true),
            ("_Height", PropertyCategory.GrayMask, true),
            ("_Parallax", PropertyCategory.GrayMask, true),
            ("_Occlusion", PropertyCategory.GrayMask, true),
            ("_AO", PropertyCategory.GrayMask, true),
            ("_Metallic", PropertyCategory.GrayMask, true),
            ("_Smoothness", PropertyCategory.GrayMask, true),
            ("_Roughness", PropertyCategory.GrayMask, true),
            ("Mask", PropertyCategory.GrayMask, true),
            ("_DetailMask", PropertyCategory.GrayMask, true),
            ("_MainTex", PropertyCategory.Albedo, false),
            ("_BaseMap", PropertyCategory.Albedo, false),
            ("_BaseColorMap", PropertyCategory.Albedo, false),
            ("_DetailAlbedoMap", PropertyCategory.Albedo, false),
            ("_EmissionMap", PropertyCategory.Albedo, false),
            ("_Emission2ndMap", PropertyCategory.Albedo, false),
            ("_EmissionGradTex", PropertyCategory.Albedo, false),
            ("_Emission2ndGradTex", PropertyCategory.Albedo, false),
            ("_MainGradationTex", PropertyCategory.Albedo, false),
            ("_Main2ndTex", PropertyCategory.Albedo, false),
            ("_Main3rdTex", PropertyCategory.Albedo, false),
            ("_Gradation", PropertyCategory.Albedo, true),
            ("_BacklightColorTex", PropertyCategory.Albedo, false),
            ("_Emission", PropertyCategory.Albedo, true),
        };

        // EN: Special-UV properties that never sample mesh UVs — must never be atlased.
        // CN: 永不采样网格 UV 的特殊属性 —— 绝不能进图集。
        private static readonly string[] SpecialUvTokens =
        {
            "MatCap", "Reflection", "Panorama", "Cubemap", "SkyReflection", "ScreenSpace", "SSR", "GrabPass",
            "Lightmap", "LightMask", "RampTex", "AudioLinkMask", "ShadowColor", "RimColor",
            "DissolveNoiseMask", "GradationTex2D"
        };

        /// <summary>EN: Analyzes a shader's texture properties (cached per shader). / CN: 分析着色器的贴图属性（按着色器缓存）。</summary>
        public static ShaderPropertyInfo[] AnalyzeShader(Shader shader)
        {
            if (shader == null) return Array.Empty<ShaderPropertyInfo>();
            if (Cache.TryGetValue(shader, out var cached)) return cached;

            var list = new List<ShaderPropertyInfo>();
            try
            {
                int count = shader.GetPropertyCount();
                var allNames = new HashSet<string>();
                for (int i = 0; i < count; i++) allNames.Add(shader.GetPropertyName(i));

                for (int i = 0; i < count; i++)
                {
                    var name = shader.GetPropertyName(i);
                    var type = shader.GetPropertyType(i);
                    if (type != ShaderPropertyType.Texture) continue;

                    var info = new ShaderPropertyInfo
                    {
                        name = name,
                        id = Shader.PropertyToID(name),
                        isTexture = true
                    };
                    var flags = shader.GetPropertyFlags(i);
                    var attrs = shader.GetPropertyAttributes(i);

                    info.isNormal = (flags & ShaderPropertyFlags.Normal) != 0 ||
                                    Array.IndexOf(attrs, "Normal") >= 0;
                    info.noScaleOffset = (flags & ShaderPropertyFlags.NoScaleOffset) != 0 ||
                                         Array.IndexOf(attrs, "NoScaleOffset") >= 0;
                    // EN: Companion _UVMode property (liltoon style) — existence & id.
                    // CN: 伴随 _UVMode 属性（liltoon 风格）——存在性与 id。
                    int uvModeId = Shader.PropertyToID(name + "_UVMode");
                    info.hasUvMode = allNames.Contains(name + "_UVMode");
                    info.uvModeId = uvModeId;
                    info.category = ClassifyProperty(name, info);
                    list.Add(info);
                }
            }
            catch (Exception e)
            {
                AtoLog.Warn($"Shader analysis failed for {shader.name}: {e.Message}");
            }
            Cache[shader] = list.ToArray();
            return Cache[shader];
        }

        private static PropertyCategory ClassifyProperty(string name, ShaderPropertyInfo info)
        {
            if (info.isNormal) return PropertyCategory.Normal;
            foreach (var tok in SpecialUvTokens)
            {
                if (name.IndexOf(tok, StringComparison.OrdinalIgnoreCase) >= 0)
                    return PropertyCategory.SpecialUv;
            }
            foreach (var (pattern, cat, contains) in Patterns)
            {
                if (contains ? name.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0
                             : name.Equals(pattern, StringComparison.OrdinalIgnoreCase))
                {
                    // EN: *_ST / *_ScrollRotate companions are vector props, not textures; ignore here.
                    // CN: *_ST / *_ScrollRotate 伴随属性是向量属性，非贴图；此处忽略。
                    return cat;
                }
            }
            // EN: Unknown texture property: unsafe to assume mesh-UV sampling → treat as unsupported.
            // CN: 未知贴图属性：不能假定其为网格 UV 采样 → 视为不支持。
            return PropertyCategory.Unsupported;
        }

        /// <summary>EN: Effective render mode for a material (keywords → _Mode → RenderType → queue). / CN: 材质有效渲染模式。</summary>
        public static RenderMode GetRenderMode(Material mat)
        {
            if (mat == null) return RenderMode.Opaque;
            try
            {
                if (mat.IsKeywordEnabled("LIL_RENDER_MODE_TRANSPARENT") ||
                    mat.IsKeywordEnabled("LIL_RENDER_MODE_TRANSCUT") && mat.IsKeywordEnabled("LIL_RENDER_MODE_TRANSPARENT"))
                    return RenderMode.Blend;
                if (mat.IsKeywordEnabled("LIL_RENDER_MODE_CUTOUT") ||
                    mat.IsKeywordEnabled("LIL_RENDER_MODE_TRANSCUT"))
                    return RenderMode.Cutout;
                if (mat.HasProperty("_Mode"))
                {
                    float mode = mat.GetFloat("_Mode");
                    if (mode <= 0.5f) return RenderMode.Opaque;
                    if (mode <= 1.5f) return RenderMode.Cutout;
                    return RenderMode.Blend;
                }
                string rt = mat.GetTag("RenderType", true, "");
                if (rt == "TransparentCutout") return RenderMode.Cutout;
                if (rt == "Transparent" || rt == "Fade") return RenderMode.Blend;
                if (string.IsNullOrEmpty(rt) && mat.renderQueue >= 3000) return RenderMode.Blend;
                if (string.IsNullOrEmpty(rt) && mat.renderQueue >= 2500) return RenderMode.Cutout;
            }
            catch (Exception) { /* fall through */ }
            return RenderMode.Opaque;
        }

        /// <summary>EN: Cutoff threshold (largest across materials/animation is handled by the evaluator). / CN: Cutoff 阈值。</summary>
        public static float GetCutoff(Material mat)
        {
            if (mat == null) return 0.5f;
            if (mat.HasProperty("_Cutoff")) return mat.GetFloat("_Cutoff");
            return 0.5f;
        }
    }
}

    // =====================================================================
    // 材质/贴图扫描：生成 TextureRef 列表
    // EN: Material/texture scanning: produces the TextureRef list.
    // =====================================================================

    /// <summary>
    /// EN: Scans all renderer materials (+ animated slot materials/textures), classifies each texture property,
    /// deduplicates, and produces TextureRefs.
    /// CN: 扫描全部渲染器材质（+ 动画切换的槽位材质/贴图），分类每个贴图属性，去重并生成 TextureRef。
    /// </summary>
    public static List<TextureRef> BuildTextureRefs(AtoBuildState state, List<Renderer> renderers,
        AnimationData anim)
    {
        var refs = new List<TextureRef>();
        state.Registry = new TextureRegistry();
        state.TextureRemap.Clear();
        state.MaterialUsages.Clear();

        foreach (var renderer in renderers)
        {
            bool rendererWhitelisted = AvatarScanner.IsWhitelisted(renderer, state.WhitelistObjects);
            var mats = renderer.sharedMaterials;
            for (int slot = 0; slot < mats.Length; slot++)
            {
                var mat = mats[slot];
                if (mat == null || mat.shader == null) continue;
                bool matWhitelisted = rendererWhitelisted || AvatarScanner.IsWhitelisted(mat, state.WhitelistObjects);

                // EN: Include materials that animations swap into this slot.
                // CN: 包含动画切换进该槽位的材质。
                var matList = new List<Material> { mat };
                if (anim != null && anim.animatedMaterials.TryGetValue((renderer, slot), out var animMats))
                    matList.AddRange(animMats);

                foreach (var m in matList)
                {
                    var mu = GetMaterialUsage(state, anim, m);
                    // EN: Static render mode & cutoff (merged with animation-derived ones).
                    // CN: 静态渲染模式与 Cutoff（与动画推导结果合并）。
                    mu.AddMode(GetRenderMode(m));
                    if (m.HasProperty("_Cutoff")) mu.AddCutoff(m.GetFloat("_Cutoff"));

                    var props = AnalyzeShader(m.shader);
                    // EN: Detect normal/mask references on this material (for type groups).
                    // CN: 检测该材质的法线/蒙版引用（用于类型组）。
                    foreach (var info in props)
                    {
                        if (info.category == PropertyCategory.Normal) mu.hasNormalRef = true;
                        if (info.category == PropertyCategory.GrayMask) mu.hasMaskRef = true;
                    }

                    foreach (var info in props)
                    {
                        var tex = m.GetTexture(info.id) as Texture2D;
                        if (tex == null) continue;

                        bool skipTex = matWhitelisted || AvatarScanner.IsWhitelisted(tex, state.WhitelistObjects);

                        // EN: uv-mode companion != 0 means the property does not use uv0 (liltoon) → skip.
                        // CN: uv-mode 伴随属性 != 0 表示该属性不使用 uv0（liltoon）→ 跳过。
                        if (!skipTex && info.hasUvMode && m.HasProperty(info.uvModeId) &&
                            Mathf.Abs(m.GetFloat(info.uvModeId)) > 0.01f)
                        {
                            AtoLog.Warn(string.Format(I18n.T("warn.whitelisted.specialuv"), tex.name));
                            skipTex = true;
                        }

                        // EN: ST transform (scale/offset) or animated ST/scroll → whitelist.
                        // CN: ST 变换（缩放/偏移）或动画 ST/滚动 → 白名单。
                        if (!skipTex && !info.noScaleOffset)
                        {
                            var st = m.GetTextureScale(info.id);
                            var off = m.GetTextureOffset(info.id);
                            bool stAnimated = anim != null && (anim.stAnimated.Contains((renderer, slot, info.name + "_ST")) ||
                                                               anim.stAnimated.Contains((renderer, slot, info.name + "_ScrollRotate")));
                            if (Mathf.Abs(st.x - 1f) > 1e-4f || Mathf.Abs(st.y - 1f) > 1e-4f ||
                                Mathf.Abs(off.x) > 1e-4f || Mathf.Abs(off.y) > 1e-4f || stAnimated)
                            {
                                AtoLog.Warn(string.Format(I18n.T("warn.whitelisted.st"), tex.name));
                                skipTex = true;
                            }
                        }

                        // EN: Usage classification.
                        // CN: 用途分类。
                        PropertyCategory cat = info.category;
                        if (skipTex)
                        {
                            state.WhitelistedTextures.Add(tex);
                        }
                        else if (cat == PropertyCategory.SpecialUv)
                        {
                            AtoLog.Warn(string.Format(I18n.T("warn.whitelisted.specialuv"), tex.name));
                            state.WhitelistedTextures.Add(tex);
                        }
                        else if (cat == PropertyCategory.Unsupported)
                        {
                            AtoLog.Warn(string.Format(I18n.T("warn.whitelisted.unknown"), tex.name));
                            state.WhitelistedTextures.Add(tex);
                        }

                        TextureUsage usage = cat switch
                        {
                            PropertyCategory.Normal => TextureUsage.Normal,
                            PropertyCategory.GrayMask => TextureUsage.GrayMask,
                            _ => TextureUsage.Albedo
                        };

                        // EN: Dedup (identical pixels + import settings collapse; whitelist survives dedup).
                        // CN: 去重（像素 + 导入设置相同则合并；白名单随去重保留）。
                        var canonical = state.Registry.Register(tex, state);
                        state.TextureRemap[tex] = canonical;
                        bool canonicalWhitelisted = state.WhitelistedTextures.Contains(tex) ||
                                                    state.Registry.IsWhitelistedResult(canonical);

                        int uvChannel = 0;
                        var tref = FindOrCreate(state, refs, canonical, uvChannel, usage, mu, info, m);
                        tref.meshUsages.Add(new MeshUsage { mesh = GetRendererMesh(renderer), renderer = renderer, slot = slot });
                        if (canonicalWhitelisted) tref.whitelisted = true;
                        if (info.category == PropertyCategory.SpecialUv || info.category == PropertyCategory.Unsupported)
                            tref.specialUv = true;
                        tref.usageByMaterial[mu] = usage;

                        // EN: Animated texture switches on this property join the same UV group (handled in GroupBuilder).
                        // CN: 该属性上的动画贴图切换并入同一 UV 组（GroupBuilder 处理）。
                    }
                }
            }
        }

        // EN: Animated texture swaps (e.g. m_Materials.Array.data[i]._MainTex) create/join TextureRefs too.
        // CN: 动画贴图切换（如 m_Materials.Array.data[i]._MainTex）同样生成/并入 TextureRef。
        if (anim != null)
        {
            foreach (var kv in anim.animatedTextureProps)
            {
                var (renderer, slot, prop) = kv.Key;
                var mats = renderer != null ? renderer.sharedMaterials : null;
                if (mats == null || slot < 0 || slot >= mats.Length) continue;
                var mat = mats[slot];
                if (mat == null) continue;
                var mu = GetMaterialUsage(state, anim, mat);
                var baseTex = mat.GetTexture(prop) as Texture2D;
                var props = AnalyzeShader(mat.shader);
                ShaderPropertyInfo info = null;
                foreach (var p in props) if (p.name == prop) { info = p; break; }
                if (info == null) continue; // 无法分类的属性不处理

                bool special = info.category == PropertyCategory.SpecialUv ||
                               info.category == PropertyCategory.Unsupported;

                foreach (var tex in kv.Value)
                {
                    if (tex == null) continue;
                    var canonical = state.Registry.Register(tex, state);
                    state.TextureRemap[tex] = canonical;
                    var tref = FindOrCreate(state, refs, canonical, 0, UsageFor(info.category), mu, info, mat);
                    tref.animated = true;
                    tref.meshUsages.Add(new MeshUsage { mesh = GetRendererMesh(renderer), renderer = renderer, slot = slot });
                    tref.usageByMaterial[mu] = UsageFor(info.category);
                    // EN: Special-UV / unclassifiable animated textures are whitelisted but STILL join the UV group
                    // so its partners skip the atlas (whitelisted textures cannot be sampled with remapped UVs).
                    // CN: 特殊 UV / 无法分类的动画贴图白名单化，但仍加入 UV 组，使同组贴图跳过图集化
                    //     （白名单贴图不能用重映射后的 UV 采样）。
                    if (special) { state.WhitelistedTextures.Add(tex); tref.specialUv = true; tref.whitelisted = true; }
                    if (state.WhitelistedTextures.Contains(tex) || state.Registry.IsWhitelistedResult(canonical))
                        tref.whitelisted = true;
                }
            }
        }

        return refs;
    }

    private static TextureUsage UsageFor(PropertyCategory cat) => cat switch
    {
        PropertyCategory.Normal => TextureUsage.Normal,
        PropertyCategory.GrayMask => TextureUsage.GrayMask,
        _ => TextureUsage.Albedo
    };

    private static Mesh GetRendererMesh(Renderer r)
    {
        if (r is SkinnedMeshRenderer smr) return smr.sharedMesh;
        if (r is MeshRenderer mr)
        {
            var mf = r.GetComponent<MeshFilter>();
            return mf != null ? mf.sharedMesh : null;
        }
        return null;
    }

    private static MaterialUsage GetMaterialUsage(AtoBuildState state, AnimationData anim, Material m)
    {
        if (state.MaterialUsages.TryGetValue(m, out var existing)) return existing;
        var mu = new MaterialUsage { material = m };
        if (anim != null && anim.materialUsage.TryGetValue(m, out var amu))
        {
            foreach (var mode in amu.modes) mu.modes.Add(mode);
            mu.cutoffs.AddRange(amu.cutoffs);
            mu.animated = amu.animated;
            foreach (var p in amu.animatedProperties) mu.animatedProperties.Add(p);
        }
        state.MaterialUsages[m] = mu;
        return mu;
    }

    private static TextureRef FindOrCreate(AtoBuildState state, List<TextureRef> refs, Texture2D canonical,
        int uvChannel, TextureUsage usage, MaterialUsage mu, ShaderPropertyInfo info, Material mat)
    {
        foreach (var r in refs)
        {
            if (r.texture == canonical && r.uvChannel == uvChannel)
            {
                if (!r.materials.Contains(mu)) r.materials.Add(mu);
                return r;
            }
        }
        var tref = new TextureRef
        {
            texture = canonical,
            propertyName = info.name,
            usage = usage,
            sRGB = canonical.isDataSRGB() || usage == TextureUsage.Albedo,
            filterMode = canonical.filterMode,
            width = canonical.width,
            height = canonical.height,
            uvChannel = uvChannel,
            originalBytes = canonical.width * canonical.height * 4L
        };
        if (usage == TextureUsage.Normal || usage == TextureUsage.GrayMask) tref.sRGB = false;
        tref.materials.Add(mu);
        refs.Add(tref);
        return tref;
    }
