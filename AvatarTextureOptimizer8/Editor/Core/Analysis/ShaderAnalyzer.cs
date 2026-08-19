// ShaderAnalyzer.cs
// Maps a Material → list of TextureUsage (role / uv channel / transform / channels / alpha).
// lilToon uses a precise hardcoded table (extracted from lilToon 2.3.4 sources);
// other shaders are analyzed via ShaderUtil + standard-keyword conventions;
// unanalyzable shaders/properties are whitelisted with a warning.
// 将材质映射为贴图用途列表(角色/UV通道/变换/通道/alpha)。lilToon 用精确硬编码表(提炼自
// lilToon 2.3.4 源码);其余着色器用 ShaderUtil+标准关键字约定分析;无法分析的按白名单+警告处理。
// Copyright (c) 2026 fosa. Licensed under the MIT License.

using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace net.fosa.ato
{
    internal static partial class ShaderAnalyzer
    {
        // ------------------------------------------------------------------ //
        // lilToon property table / lilToon 属性表
        // Sources: lilToon 2.3.4 shader includes + AAO ShaderInformation.Liltoon semantics.
        // 来源:lilToon 2.3.4 着色器源码 + AAO ShaderInformation.Liltoon 语义。
        // ------------------------------------------------------------------ //

        // Textures sampled with uvMain (UV0 ∘ _MainTex_ST ∘ ScrollRotate), no own ST. / 用 uvMain 采样且无独立 ST 的贴图。
        private static readonly string[] LilUvMainColor = { "_MainTex", "_BaseMap", "_BaseColorMap" };
        private static readonly string[] LilUvMainColorTex =
        {
            "_BacklightColorTex", "_ReflectionColorTex", "_RimColorTex", "_GlitterColorTex",
            "_ShadowColorTex", "_Shadow2ndColorTex", "_Shadow3rdColorTex",
        };
        // uvMain + own ST. / uvMain + 自身 ST。
        private static readonly string[] LilUvMainStColor = { "_OutlineTex" };
        private static readonly string[] LilUvMainStMask =
        {
            "_AlphaMask", "_MainColorAdjustMask", "_Bump2ndScaleMask", "_AnisotropyScaleMask",
            "_AnisotropyShiftNoiseMask", "_Main2ndBlendMask", "_Main3rdBlendMask", "_RimShadeMask",
            "_SmoothnessTex", "_MetallicGlossMap", "_FurMask", "_FurLengthMask", "_FurVectorTex",
            "_OutlineWidthMask",
        };
        private static readonly string[] LilUvMainNoStMask = { "_EmissionBlendMask", "_Emission2ndBlendMask", "_MatCapBlendMask", "_MatCap2ndBlendMask" };
        private static readonly string[] LilUvStNormal = { "_BumpMap", "_Bump2ndMap", "_MatCapBumpMap", "_MatCap2ndBumpMap" };
        private static readonly string[] LilShadowGrad = { "_ShadowStrengthMask", "_ShadowBorderMask", "_ShadowBlurMask" };

        /// <summary>Texture with a _UVMode int selector. / 带 _UVMode 整型选择器的贴图描述。</summary>
        internal sealed class LilUvModeEntry
        {
            internal string Prop, UvModeProp, Gate;
            internal TexRole Role; internal bool Decal;
            internal LilUvModeEntry(string prop, string uvModeProp, string gate, TexRole role, bool decal)
            { Prop = prop; UvModeProp = uvModeProp; Gate = gate; Role = role; Decal = decal; }
        }

        private static readonly LilUvModeEntry[] LilUvMode =
        {
            new LilUvModeEntry("_Main2ndTex", "_Main2ndTex_UVMode", "_UseMain2ndTex", TexRole.Color, true),
            new LilUvModeEntry("_Main3rdTex", "_Main3rdTex_UVMode", "_UseMain3rdTex", TexRole.Color, true),
            new LilUvModeEntry("_Bump2ndMap", "_Bump2ndMap_UVMode", "_UseBump2ndMap", TexRole.Normal, false),
            new LilUvModeEntry("_EmissionMap", "_EmissionMap_UVMode", "_UseEmission", TexRole.Color, false),
            new LilUvModeEntry("_Emission2ndMap", "_Emission2ndMap_UVMode", "_UseEmission2nd", TexRole.Color, false),
            new LilUvModeEntry("_AudioLinkMask", "_AudioLinkMask_UVMode", null, TexRole.Mask, false),
        };

        // Not sampled with mesh UVs at all → never atlasable, but not an error. / 非网格UV采样→不可装箱(非错误)。
        private static readonly HashSet<string> LilNonMesh =
        {
            "_DitherTex", "_MainGradationTex", "_MatCapTex", "_MatCap2ndTex", "_GlitterShapeTex",
            "_EmissionGradTex", "_Emission2ndGradTex", "_AudioLinkLocalMap",
        };

        // Mask channel usage: property → used channel bits. / 蒙版通道使用表。
        private const int CH_R = 1, CH_G = 2, CH_B = 4, CH_A = 8;
        private static readonly Dictionary<string, byte> LilMaskChannels = new Dictionary<string, byte>
        {
            { "_AlphaMask", CH_R }, { "_MainColorAdjustMask", CH_R }, { "_Bump2ndScaleMask", CH_R },
            { "_AnisotropyScaleMask", CH_R }, { "_AnisotropyShiftNoiseMask", CH_R },
            { "_Main2ndBlendMask", CH_R }, { "_Main3rdBlendMask", CH_R }, { "_RimShadeMask", CH_R },
            { "_SmoothnessTex", CH_R }, { "_MetallicGlossMap", CH_R }, { "_FurMask", CH_R },
            { "_FurLengthMask", CH_R }, { "_OutlineWidthMask", CH_R },
            { "_ShadowStrengthMask", CH_R | CH_G | CH_A }, { "_ShadowBorderMask", CH_R | CH_G },
            { "_ShadowBlurMask", CH_R | CH_G }, { "_AudioLinkMask", CH_R | CH_G },
            { "_FurNoiseMask", CH_R }, { "_DissolveMask", CH_R }, { "_DissolveNoiseMask", CH_R },
            { "_MatCapBlendMask", CH_R | CH_G | CH_B }, { "_EmissionBlendMask", CH_R | CH_G | CH_B | CH_A },
            { "_Emission2ndBlendMask", CH_R | CH_G | CH_B | CH_A },
        };

        // Properties whose non-default value means "unsupported transform" (decal family). / 非默认值即"不支持变换"的属性(decal 族)。
        private static readonly string[] LilSubTexDecalFlags = { "IsDecal", "IsLeftOnly", "IsRightOnly", "ShouldCopy", "ShouldFlipMirror", "ShouldFlipCopy", "IsMSDF" };

        /// <summary>Result of analyzing one material. / 单个材质的分析结果。</summary>
        internal sealed class MaterialAnalysis
        {
            internal List<TextureUsage> Usages = new List<TextureUsage>();
            /// <summary>Textures that are sampled but not analyzable → whitelist. / 被采样但无法分析的贴图→白名单。</summary>
            internal List<(Texture2D tex, string reason)> WhitelistCandidates = new List<(Texture2D, string)>();
            /// <summary>Textures with transforms etc. / 存在变换等的贴图。</summary>
            internal bool ShaderUnsupported;
            internal string ShaderName;
        }

        /// <summary>Analyze one material. / 分析一个材质。</summary>
        internal static MaterialAnalysis Analyze(Material mat, AnimationDatabase anim, string rendererPath, int slot)
        {
            var res = new MaterialAnalysis { ShaderName = mat.shader != null ? mat.shader.name : "<null>" };
            if (mat.shader == null) { res.ShaderUnsupported = true; return res; }

            // Animated material floats for this slot / 该槽位的动画浮点关键帧
            anim.MaterialFloatKeyframes.TryGetValue(rendererPath, out var slotFloats);
            Dictionary<string, float[]> pathFloats = null;
            if (slotFloats != null && slotFloats.TryGetValue(0, out var f0)) pathFloats = f0;
            if (slotFloats != null && slotFloats.TryGetValue(slot, out var fs)) pathFloats = fs;

            if (IsLilToon(mat.shader)) AnalyzeLilToon(mat, anim, rendererPath, slot, res);
            else AnalyzeGeneric(mat, res, pathFloats);

            ResolveAlphaRequirements(mat, anim, rendererPath, slot, res);
            return res;
        }

        private static bool IsLilToon(Shader shader)
        {
            var n = shader.name;
            return n.StartsWith("Hidden/lilToon", StringComparison.Ordinal) ||
                   n.StartsWith("_lil/", StringComparison.Ordinal) ||
                   n.Contains("lilToon", StringComparison.Ordinal);
        }

        // ------------------------------------------------------------------ //
        // lilToon / lilToon 分析
        // ------------------------------------------------------------------ //
        private static void AnalyzeLilToon(Material mat, AnimationDatabase anim, string rendererPath, int slot, MaterialAnalysis res)
        {
            var sh = mat.shader;

            // uvMain matrix preconditions / uvMain 矩阵前提
            var mainSt = mat.GetVector("_MainTex_ST");
            bool mainStIdentity = mainSt == new Vector4(1, 1, 0, 0);
            bool mainScrollZero = !mat.HasProperty("_MainTex_ScrollRotate") || mat.GetVector("_MainTex_ScrollRotate") == Vector4.zero;
            bool shiftBackface = mat.HasProperty("_ShiftBackfaceUV") && mat.GetFloat("_ShiftBackfaceUV") != 0;
            bool uvMainIdentity = mainStIdentity && mainScrollZero && !shiftBackface;

            // Animated ST → transform / 动画 ST → 变换
            bool animSt = HasAnimatedSt(anim, rendererPath);

            foreach (var p in LilUvMainColor)
                RegisterUvMainTex(mat, p, TexRole.Color, uvMainIdentity, animSt, res);
            foreach (var p in LilUvMainColorTex)
                if (Gate(mat, "_UseBacklight", p)) RegisterUvMainTex(mat, p, TexRole.Color, uvMainIdentity, animSt, res);
            foreach (var p in LilUvMainStColor)
                RegisterStTex(mat, p, TexRole.Color, uvMainIdentity, animSt, res);
            foreach (var p in LilUvMainStMask)
                RegisterStTex(mat, p, TexRole.Mask, uvMainIdentity, animSt, res);
            foreach (var p in LilUvMainNoStMask)
                if (Gate(mat, "_UseEmission", p) || Gate(mat, "_UseMatCap", p)) RegisterUvMainTex(mat, p, TexRole.Mask, uvMainIdentity, animSt, res);
            foreach (var p in LilUvStNormal)
            {
                var gate = p == "_BumpMap" ? "_UseBumpMap" : p == "_Bump2ndMap" ? "_UseBump2ndMap"
                    : p == "_MatCapBumpMap" ? "_MatCapCustomNormal" : "_MatCap2ndCustomNormal";
                if (Gate(mat, gate, p)) RegisterStTex(mat, p, TexRole.Normal, uvMainIdentity, animSt, res);
            }

            // Shadow LUT vs uvMain / 阴影 LUT 与 uvMain
            bool shadowLut = mat.HasProperty("_ShadowColorType") && (int)mat.GetFloat("_ShadowColorType") == 1;
            foreach (var p in new[] { "_ShadowStrengthMask", "_ShadowBorderMask", "_ShadowBlurMask" })
                if (Gate(mat, "_UseShadow", p)) RegisterStTex(mat, p, TexRole.Mask, uvMainIdentity, animSt, res);
            if (!shadowLut)
                foreach (var p in new[] { "_ShadowColorTex", "_Shadow2ndColorTex", "_Shadow3rdColorTex" })
                    if (Gate(mat, "_UseShadow", p)) RegisterUvMainTex(mat, p, TexRole.Color, uvMainIdentity, animSt, res);

            // UVMode-selected textures / UVMode 选择型贴图
            foreach (var e in LilUvMode)
            {
                var prop = e.Prop; var uvModeProp = e.UvModeProp; var role = e.Role; var decal = e.Decal;
                if (e.Gate != null && !GateOn(mat, e.Gate)) continue;
                var tex = mat.GetTexture(prop) as Texture2D;
                if (tex == null) continue;
                if (LilNonMesh.Contains(prop)) continue; // unreachable for these props / 这些属性不会到这
                int mode = mat.HasProperty(uvModeProp) ? (int)mat.GetFloat(uvModeProp) : -1;
                if (mode == 4) continue; // matcap/uvRim based → non-mesh / 非网格UV
                if (mode == -1 || mode > 3)
                {
                    res.WhitelistCandidates.Add((tex, $"lilToon {prop}: ambiguous UV mode {mode}"));
                    continue;
                }
                bool transformed = animSt || (decal && HasDecalFlags(mat, prop));
                var usage = new TextureUsage
                {
                    Texture = tex, Role = role, UvChannel = mode, PropertyName = prop, Material = mat,
                    HasTransform = transformed || !uvMainIdentity, NonMeshUv = false,
                    UsedChannels = role == TexRole.Mask ? ChannelBits(prop) : AllChannels(),
                    Srgb = ImportSrgb(tex), Filter = tex.filterMode,
                };
                if (prop == "_EmissionMap" || prop == "_Emission2ndMap")
                {
                    // emission parallax → deform / 自发光视差 → 形变用途
                    var depthProp = prop + "ParallaxDepth";
                    if (mat.HasProperty(depthProp) && mat.GetFloat(depthProp) != 0)
                        usage.HasTransform = true;
                }
                res.Usages.Add(usage);
            }

            // UV0 fixed textures / 固定 UV0 的贴图
            foreach (var p in new[] { "_ParallaxMap", "_FurNoiseMask", "_DissolveMask", "_DissolveNoiseMask", "_OutlineVectorTex" })
            {
                var tex = mat.GetTexture(p) as Texture2D;
                if (tex == null) continue;
                var gate = p == "_ParallaxMap" ? "_UseParallax" : null;
                if (gate != null && !GateOn(mat, gate)) continue;
                var role = p == "_OutlineVectorTex" ? TexRole.Mask : p == "_ParallaxMap" ? TexRole.Color : TexRole.Mask;
                res.Usages.Add(new TextureUsage
                {
                    Texture = tex, Role = role, UvChannel = 0, PropertyName = p, Material = mat,
                    HasTransform = animSt, UsedChannels = p == "_OutlineVectorTex" ? AllChannels() : ChannelBits(p),
                    Srgb = ImportSrgb(tex), Filter = tex.filterMode,
                });
            }

            // Any other texture property unknown to the table → whitelist + warn / 表外属性→白名单+警告
            for (int i = 0; i < ShaderUtil.GetPropertyCount(sh); i++)
            {
                if (ShaderUtil.GetPropertyType(sh, i) != ShaderPropertyType.Texture) continue;
                var p = ShaderUtil.GetPropertyName(sh, i);
                if (IsKnownLilProperty(p)) continue;
                var tex = mat.GetTexture(p) as Texture2D;
                if (tex == null) continue;
                res.WhitelistCandidates.Add((tex, $"unrecognized lilToon property {p}"));
            }
        }

        private static void RegisterUvMainTex(Material mat, string p, TexRole role, bool uvMainIdentity, bool animSt, MaterialAnalysis res)
        {
            var tex = mat.GetTexture(p) as Texture2D;
            if (tex == null) return;
            res.Usages.Add(new TextureUsage
            {
                Texture = tex, Role = role, UvChannel = 0, PropertyName = p, Material = mat,
                HasTransform = !uvMainIdentity || animSt,
                UsedChannels = role == TexRole.Mask ? ChannelBits(p) : AllChannels(),
                Srgb = ImportSrgb(tex), Filter = tex.filterMode,
            });
        }

        private static void RegisterStTex(Material mat, string p, TexRole role, bool uvMainIdentity, bool animSt, MaterialAnalysis res)
        {
            var tex = mat.GetTexture(p) as Texture2D;
            if (tex == null) return;
            bool stIdentity = true;
            if (mat.HasProperty(p + "_ST"))
            {
                var st = mat.GetVector(p + "_ST");
                stIdentity = st == new Vector4(1, 1, 0, 0);
            }
            res.Usages.Add(new TextureUsage
            {
                Texture = tex, Role = role, UvChannel = 0, PropertyName = p, Material = mat,
                HasTransform = !uvMainIdentity || !stIdentity || animSt,
                UsedChannels = role == TexRole.Mask ? ChannelBits(p) : AllChannels(),
                Srgb = ImportSrgb(tex), Filter = tex.filterMode,
            });
        }

        private static void AnalyzeGeneric(Material mat, MaterialAnalysis res, Dictionary<string, float[]> pathFloats)
        {
            var sh = mat.shader;
            bool recognized = false;
            for (int i = 0; i < ShaderUtil.GetPropertyCount(sh); i++)
            {
                if (ShaderUtil.GetPropertyType(sh, i) != ShaderPropertyType.Texture) continue;
                var p = ShaderUtil.GetPropertyName(sh, i);
                var flags = ShaderUtil.GetPropertyFlags(sh, i);
                var tex = mat.GetTexture(p) as Texture2D;
                if (tex == null) continue;
                bool isNormal = (flags & ShaderPropertyFlags.Normal) != 0 || p.EndsWith("BumpMap") || p.EndsWith("NormalMap");

                if (StandardTextureProps.TryGetValue(p, out var sem))
                {
                    recognized = true;
                    if (sem.nonMesh) continue;
                    if (sem.uvChannel < 0)
                    {
                        res.WhitelistCandidates.Add((tex, $"{p}: detail/secondary UV semantics"));
                        continue;
                    }
                    var st = GetTextureSt(mat, p, flags);
                    res.Usages.Add(new TextureUsage
                    {
                        Texture = tex, Role = sem.role, UvChannel = sem.uvChannel, PropertyName = p, Material = mat,
                        HasTransform = !st.identity || (pathFloats != null && pathFloats.ContainsKey($"material.{p}_ST.x")),
                        UsedChannels = sem.channels, Srgb = ImportSrgb(tex), Filter = tex.filterMode,
                        NonMeshUv = false,
                    });
                }
                else
                {
                    // Unknown property on a partially recognized shader: whitelist + warn. / 半识别着色器上的未知属性:白名单+警告。
                    res.WhitelistCandidates.Add((tex, $"unrecognized shader property {p} on '{mat.shader.name}'"));
                }
            }
            res.ShaderUnsupported = false;
        }

        private static readonly Dictionary<string, (TexRole role, int uvChannel, byte channels, bool nonMesh)> StandardTextureProps =
            new Dictionary<string, (TexRole, int, byte, bool)>
            {
                { "_MainTex", (TexRole.Color, 0, 0xF, false) },
                { "_BumpMap", (TexRole.Normal, 0, 0xF, false) },
                { "_EmissionMap", (TexRole.Color, 0, 0xF, false) },
                { "_MetallicGlossMap", (TexRole.Mask, 0, CH_R, false) },
                { "_OcclusionMap", (TexRole.Mask, 0, CH_G, false) },
                { "_ParallaxMap", (TexRole.Color, 0, 0xF, true) }, // parallax deform → whitelist / 视差形变→白名单
                { "_DetailAlbedoMap", (TexRole.Color, -1, 0xF, false) }, // UV1/ST detail → whitelist / 细节UV→白名单
                { "_DetailNormalMap", (TexRole.Normal, -1, 0xF, false) },
                { "_DetailMask", (TexRole.Mask, 0, 0xF, false) },
            };

        private static (bool identity, Vector4 st) GetTextureSt(Material mat, string p, ShaderPropertyFlags flags)
        {
            if ((flags & ShaderPropertyFlags.NoScaleOffset) != 0) return (true, new Vector4(1, 1, 0, 0));
            if (!mat.HasProperty(p + "_ST")) return (true, new Vector4(1, 1, 0, 0));
            var st = mat.GetVector(p + "_ST");
            return (st == new Vector4(1, 1, 0, 0), st);
        }

        private static byte ChannelBits(string prop) =>
            LilMaskChannels.TryGetValue(prop, out var b) ? b : (byte)0xF;

        private static byte AllChannels() => 0xF;

        private static bool Gate(Material mat, string gate, string prop)
        {
            if (!mat.HasProperty(gate)) return true; // no gate → consider enabled / 无开关→视为启用
            if (mat.GetFloat(gate) != 0) return true;
            return false; // gated off → texture unused / 关闭→贴图未被使用
        }

        private static bool GateOn(Material mat, string gate)
        {
            return !mat.HasProperty(gate) || mat.GetFloat(gate) != 0;
        }

        private static bool HasDecalFlags(Material mat, string prop)
        {
            foreach (var suffix in LilSubTexDecalFlags)
            {
                var p = prop + suffix;
                if (mat.HasProperty(p) && mat.GetFloat(p) != 0) return true;
            }
            // decal animation default (1,1,1,30) / decal 动画默认值
            var da = prop + "DecalAnimation";
            if (mat.HasProperty(da) && mat.GetVector(da) != new Vector4(1, 1, 1, 30)) return true;
            return false;
        }

        private static bool HasAnimatedSt(AnimationDatabase anim, string rendererPath)
        {
            if (anim.MaterialFloatKeyframes.TryGetValue(rendererPath, out var bySlot))
                foreach (var byProp in bySlot.Values)
                    foreach (var prop in byProp.Keys)
                        if (prop.Contains("_ST.", StringComparison.Ordinal) || prop.EndsWith("_ScrollRotate", StringComparison.Ordinal))
                            return true;
            return false;
        }

        private static bool IsKnownLilProperty(string p) =>
            LilKnownProps.Contains(p) || p.EndsWith("_ST") || p.EndsWith("_UVMode") || p.EndsWith("_ScrollRotate");

        private static readonly HashSet<string> LilKnownProps = new HashSet<string>
        {
            "_MainTex", "_BaseMap", "_BaseColorMap", "_BacklightColorTex", "_ReflectionColorTex", "_RimColorTex",
            "_GlitterColorTex", "_ShadowColorTex", "_Shadow2ndColorTex", "_Shadow3rdColorTex", "_OutlineTex",
            "_AlphaMask", "_MainColorAdjustMask", "_Bump2ndScaleMask", "_AnisotropyScaleMask",
            "_AnisotropyShiftNoiseMask", "_Main2ndBlendMask", "_Main3rdBlendMask", "_RimShadeMask",
            "_SmoothnessTex", "_MetallicGlossMap", "_FurMask", "_FurLengthMask", "_FurVectorTex", "_OutlineWidthMask",
            "_EmissionBlendMask", "_Emission2ndBlendMask", "_MatCapBlendMask", "_MatCap2ndBlendMask",
            "_BumpMap", "_Bump2ndMap", "_MatCapBumpMap", "_MatCap2ndBumpMap",
            "_ShadowStrengthMask", "_ShadowBorderMask", "_ShadowBlurMask",
            "_Main2ndTex", "_Main3rdTex", "_EmissionMap", "_Emission2ndMap", "_AudioLinkMask",
            "_ParallaxMap", "_FurNoiseMask", "_DissolveMask", "_DissolveNoiseMask", "_OutlineVectorTex",
            "_DitherTex", "_MainGradationTex", "_MatCapTex", "_MatCap2ndTex", "_GlitterShapeTex",
            "_EmissionGradTex", "_Emission2ndGradTex", "_AudioLinkLocalMap",
        };

        // ------------------------------------------------------------------ //
        // Alpha requirements / alpha 要求
        // ------------------------------------------------------------------ //
        private static void ResolveAlphaRequirements(Material mat, AnimationDatabase anim, string rendererPath, int slot, MaterialAnalysis res)
        {
            var mode = DetectAlphaMode(mat);
            var cutoffs = new List<float> { mode == AlphaMode.Cutout ? GetCutoff(mat) : 0.5f };

            // Animated cutoff/mode → strictest across all keyframes / 动画阈值/模式→全部关键帧取最严
            if (anim.MaterialFloatKeyframes.TryGetValue(rendererPath, out var bySlot) && bySlot.TryGetValue(slot, out var byProp2))
                foreach (var byProp in bySlot.Values)
                {
                    if (byProp.TryGetValue("material._Cutoff", out var cut))
                        for (int i = 0; i < cut.Length; i++) cutoffs.Add(cut[i]);
                    if (byProp.ContainsKey("material._Mode") || byProp.ContainsKey("material._SrcBlend") ||
                        byProp.ContainsKey("material._ZWrite"))
                        cutoffs.Add(-1f); // marker: mode animated → also require Blend metrics / 模式被动画→同时要求 Blend 指标
                }

            foreach (var u in res.Usages)
            {
                if (u.Role != TexRole.Color) continue;
                if (mode == AlphaMode.Opaque && !cutoffs.Contains(-1f)) continue;
                u.Alpha = mode;
                u.Cutoff = cutoffs[0];
                u.BlendAlsoRequired = cutoffs.Contains(-1f);
                var mc = new List<float>();
                foreach (var c in cutoffs) if (c >= 0f && !mc.Contains(c)) mc.Add(c);
                u.MultiCutoffs = mc.Count > 0 ? mc.ToArray() : new[] { cutoffs[0] };
            }
        }

        private static AlphaMode DetectAlphaMode(Material mat)
        {
            var q = mat.renderQueue;
            if (q >= 3000) return AlphaMode.Blend;
            if (q >= 2450 && q < 3000) return AlphaMode.Cutout;
            return AlphaMode.Opaque;
        }

        private static float GetCutoff(Material mat) =>
            mat.HasProperty("_Cutoff") ? mat.GetFloat("_Cutoff") : 0.5f;

        internal static bool ImportSrgb(Texture2D tex)
        {
            var path = AssetDatabase.GetAssetPath(tex);
            if (!string.IsNullOrEmpty(path) && AssetImporter.GetAtPath(path) is TextureImporter importer)
                return importer.sRGBTexture;
            return true; // generated textures default sRGB / 生成的贴图默认 sRGB
        }
    }

}
