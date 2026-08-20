// ============================================================================
// ATO - import stage (stage 5, compute only) + format safety
// ATO - 导入阶段（阶段5，仅计算）+ 格式安全
//
// Safe format enumeration  安全格式枚举：
//   per category x platform x NPOT x channel-requirement; any unsafe user
//   choice falls back to a safe format with a console warning (never a
//   broken texture).
//   按 类别 x 平台 x NPOT x 通道需求；任何不安全的用户选择回退到安全格式并
//   控制台警告（绝不产出坏贴图）。
// Mipmaps + MipStreaming are bound (VRChat requires mip streaming whenever
// mipmaps are on).  Mipmap 与 MipStreaming 绑定（VRChat 要求开 Mipmap 必开
// MipStreaming）。
// ============================================================================

#region

using System.Collections.Generic;
using nadena.dev.ndmf;
using net.fosa.AvatarTextureOptimizer.Editor.Analysis;
using net.fosa.AvatarTextureOptimizer.Editor.Core;
using net.fosa.AvatarTextureOptimizer.Editor.Packing;
using UnityEditor;
using UnityEngine;

#endregion

namespace net.fosa.AvatarTextureOptimizer.Editor.Import
{
    public static class ImportStageImpl
    {
        public static void Execute(ATOContext ctx, BuildContext context)
        {
            var c = ctx.Component;
            var an = ctx.Analysis;
            var log = ctx.Log;
            if (an == null) return;

            var platform = CurrentPlatform();
            bool npot = c.UseNPOT;
            if (c.PlatformOverride)
            {
                var ov = platform == ATOPlatform.Android ? c.AndroidOverride :
                         platform == ATOPlatform.iOS ? c.IOSOverride : c.PCOVERRIDE;
                npot = ov.useNPOT;
            }

            int planCount = 0;

            // atlas pages  图集页
            if (an.PackedResult != null)
            {
                foreach (var page in an.PackedResult.Pages)
                {
                    if (page.Texture == null) continue;
                    ctx.Session.Check("Import 导入参数");
                    var tg = an.TypeGroups[page.TypeGroupId];
                    var cat = PageCategory(page, an);
                    var (fmt, fb, reason) = ResolveFormat(c, platform, npot, cat, page.HasAlpha, out _);
                    var plan = new ATOImportPlan
                    {
                        Texture = page.Texture,
                        Category = cat,
                        HasAlpha = page.HasAlpha,
                        Format = fmt,
                        Mipmaps = MipsFor(c, platform, cat),
                        NpotAllowed = npot,
                        FallbackUsed = fb,
                        FallbackReason = reason,
                    };
                    an.ImportPlans[page.Texture] = plan;
                    if (fb) log.Warn(ATOLogMask.Import,
                        $"atlas {page.Texture.name}: format fallback ({reason}). 图集格式回退。");
                    planCount++;
                }
            }

            // whole-image scaled textures  整图缩放贴图
            foreach (var (tid, scaled) in an.ScaledTextures)
            {
                if (scaled == null) continue;
                ctx.Session.Check("Import 导入参数");
                var tref = an.Textures[tid];
                var cat = TextureCategory(an, tref);
                bool hasAlpha = SampleHasAlpha(scaled);
                var (fmt, fb, reason) = ResolveFormat(c, platform, npot, cat, hasAlpha, out _);
                var plan = new ATOImportPlan
                {
                    Texture = scaled,
                    Category = cat,
                    HasAlpha = hasAlpha,
                    Format = fmt,
                    Mipmaps = MipsFor(c, platform, cat),
                    NpotAllowed = npot,
                    FallbackUsed = fb,
                    FallbackReason = reason,
                };
                an.ImportPlans[scaled] = plan;
                if (fb) log.Warn(ATOLogMask.Import,
                    $"scaled texture {scaled.name}: format fallback ({reason}). 缩放贴图格式回退。");
                planCount++;
            }

            // existing non-whitelisted textures: import-parameter optimization only
            // 既有非白名单贴图：仅导入参数优化
            foreach (var (tid, tref) in an.Textures)
            {
                if (tref.Whitelisted) continue;
                var path = AssetDatabase.GetAssetPath(tref.Texture);
                if (string.IsNullOrEmpty(path)) continue;
                ctx.Session.Check("Import 导入参数");
                var cat = TextureCategory(an, tref);
                bool hasAlpha = SampleHasAlpha(tref.Texture);
                var (fmt, fb, reason) = ResolveFormat(c, platform, npot, cat, hasAlpha, out _);
                var plan = new ATOImportPlan
                {
                    Texture = tref.Texture,
                    Category = cat,
                    HasAlpha = hasAlpha,
                    Format = fmt,
                    Mipmaps = MipsFor(c, platform, cat),
                    NpotAllowed = npot,
                    FallbackUsed = fb,
                    FallbackReason = reason,
                };
                an.ImportPlans[tref.Texture] = plan;
                if (fb) log.Warn(ATOLogMask.Import,
                    $"texture {tref.Texture.name}: format fallback ({reason}). 贴图格式回退。");
                planCount++;
            }

            log.Info(ATOLogMask.Import,
                $"import plans: {planCount} textures (platform={platform}, npot={npot}). 导入计划完成。");
        }

        // ------------------------------------------------------------------
        public static ATOPlatform CurrentPlatform()
        {
            var t = EditorUserBuildSettings.activeBuildTarget;
            if (t == BuildTarget.Android) return ATOPlatform.Android;
            if (t == BuildTarget.iOS) return ATOPlatform.iOS;
            return ATOPlatform.PC;
        }

        private static bool MipsFor(ATOComponent c, ATOPlatform platform, ATOTextureCategory cat)
        {
            if (c.PlatformOverride)
            {
                var ov = platform == ATOPlatform.Android ? c.AndroidOverride :
                         platform == ATOPlatform.iOS ? c.IOSOverride : c.PCOVERRIDE;
                switch (cat)
                {
                    case ATOTextureCategory.Transparent: return ov.mipsTransparent;
                    case ATOTextureCategory.Normal: return ov.mipsNormal;
                    case ATOTextureCategory.Gray: return ov.mipsGray;
                    default: return ov.mipsOpaque;
                }
            }
            switch (cat)
            {
                case ATOTextureCategory.Transparent: return c.MipsTransparent;
                case ATOTextureCategory.Normal: return c.MipsNormal;
                case ATOTextureCategory.Gray: return c.MipsGray;
                default: return c.MipsOpaque;
            }
        }

        private static ATOTextureCategory PageCategory(ATOPackedPage page, ATOAnalysis an)
        {
            if (page.IsMirrorRole >= 0)
            {
                var role = (Api.ATOTextureRole) page.IsMirrorRole;
                if (role == Api.ATOTextureRole.Normal) return ATOTextureCategory.Normal;
                if (role == Api.ATOTextureRole.Mask) return ATOTextureCategory.Gray;
                if (role == Api.ATOTextureRole.Emission)
                {
                    return an.TypeGroups[page.TypeGroupId].TextureIds.Count > 0 &&
                           IsAnyTransparent(an, page.TypeGroupId)
                        ? ATOTextureCategory.Transparent
                        : ATOTextureCategory.Opaque;
                }
                return ATOTextureCategory.Opaque;
            }
            return IsAnyTransparent(an, page.TypeGroupId)
                ? ATOTextureCategory.Transparent
                : ATOTextureCategory.Opaque;
        }

        private static bool IsAnyTransparent(ATOAnalysis an, int typeGroupId)
        {
            var tg = an.TypeGroups[typeGroupId];
            foreach (var tid in tg.TextureIds)
            {
                foreach (var mat in an.Textures[tid].ReferringMaterials)
                {
                    if (an.Materials.TryGetValue(mat, out var info) && info.AlphaMode != 0) return true;
                }
            }
            return false;
        }

        private static ATOTextureCategory TextureCategory(ATOAnalysis an, ATOTextureRef tref)
        {
            // strictest role across referring materials  引用材质中最严角色
            bool isNormal = false, isMask = false, isAlbedo = false;
            bool transparent = false;
            foreach (var mat in tref.ReferringMaterials)
            {
                if (!an.Materials.TryGetValue(mat, out var info)) continue;
                if (info.AlphaMode != 0) transparent = true;
                foreach (var (prop, pref) in info.PropertyRefs)
                {
                    if (!info.Textures.TryGetValue(prop, out var tex)) continue;
                    if (!(tex is Texture2D t2d)) continue;
                    if (!an.TextureDedupMap.TryGetValue(t2d, out var did)) continue;
                    if (did != tref.Id) continue;
                    switch (pref.Role)
                    {
                        case Api.ATOTextureRole.Normal: isNormal = true; break;
                        case Api.ATOTextureRole.Mask: isMask = true; break;
                        case Api.ATOTextureRole.Albedo: isAlbedo = true; break;
                    }
                }
            }
            if (isNormal) return ATOTextureCategory.Normal;
            if (isMask) return ATOTextureCategory.Gray;
            if (isAlbedo) return transparent ? ATOTextureCategory.Transparent : ATOTextureCategory.Opaque;
            return ATOTextureCategory.Opaque;
        }

        // ------------------------------------------------------------------
        /// <summary>Resolves a safe format: user choice if safe, otherwise a
        /// safe fallback (with reason). 解析安全格式：用户选择不安全则安全
        /// 回退（附原因）。</summary>
        public static (TextureImporterFormat fmt, bool fallback, string reason) ResolveFormat(
            ATOComponent c, ATOPlatform platform, bool npot, ATOTextureCategory cat,
            bool hasAlpha, out bool usedChannelsMultiple)
        {
            usedChannelsMultiple = false;
            ATOFormatChoice choice = c.PlatformOverride
                ? PlatformChoice(c, platform, cat)
                : GenericChoice(c, cat);

            var allowed = SafeFormats(platform, npot, cat, hasAlpha);
            if (choice != ATOFormatChoice.Auto && allowed.Contains((TextureImporterFormat) (int) choice))
            {
                // gray single-channel check  灰度单通道检查
                return ((TextureImporterFormat) (int) choice, false, null);
            }

            // fallback: auto-pick  回退：自动选择
            var auto = AutoFormat(platform, cat, hasAlpha, npot);
            if (!allowed.Contains(auto)) auto = TextureImporterFormat.RGBA32;
            return (auto, true,
                $"requested {(int) choice} unsafe for {cat}@{platform}{(npot ? " (NPOT)" : "")}");
        }

        private static ATOFormatChoice GenericChoice(ATOComponent c, ATOTextureCategory cat)
        {
            switch (cat)
            {
                case ATOTextureCategory.Transparent: return c.FormatTransparent;
                case ATOTextureCategory.Normal: return c.FormatNormal;
                case ATOTextureCategory.Gray: return c.FormatGray;
                default: return c.FormatOpaque;
            }
        }

        private static ATOFormatChoice PlatformChoice(ATOComponent c, ATOPlatform platform, ATOTextureCategory cat)
        {
            var ov = platform == ATOPlatform.Android ? c.AndroidOverride :
                     platform == ATOPlatform.iOS ? c.IOSOverride : c.PCOVERRIDE;
            switch (cat)
            {
                case ATOTextureCategory.Transparent: return ov.formatTransparent;
                case ATOTextureCategory.Normal: return ov.formatNormal;
                case ATOTextureCategory.Gray: return ov.formatGray;
                default: return ov.formatOpaque;
            }
        }

        /// <summary>Formats safe for (platform, npot, category, alpha).
        /// 对 (平台, npot, 类别, alpha) 安全的格式集合。</summary>
        public static HashSet<TextureImporterFormat> SafeFormats(
            ATOPlatform platform, bool npot, ATOTextureCategory cat, bool hasAlpha)
        {
            var set = new HashSet<TextureImporterFormat>();
            // platform support  平台支持
            var platformOK = new HashSet<TextureImporterFormat>();
            if (platform == ATOPlatform.PC)
            {
                AddAll(platformOK, TextureImporterFormat.DXT1, TextureImporterFormat.DXT5,
                    TextureImporterFormat.BC4, TextureImporterFormat.BC5,
                    TextureImporterFormat.BC7, TextureImporterFormat.RGB24,
                    TextureImporterFormat.RGBA32, TextureImporterFormat.Alpha8);
            }
            else if (platform == ATOPlatform.Android)
            {
                AddAll(platformOK, TextureImporterFormat.ETC2, TextureImporterFormat.ETC2A,
                    TextureImporterFormat.ETC2A8, TextureImporterFormat.EACR,
                    TextureImporterFormat.EACRG, TextureImporterFormat.ASTC_4x4,
                    TextureImporterFormat.ASTC_5x5, TextureImporterFormat.ASTC_6x6,
                    TextureImporterFormat.ASTC_8x8, TextureImporterFormat.RGBA32,
                    TextureImporterFormat.Alpha8);
            }
            else // iOS
            {
                AddAll(platformOK, TextureImporterFormat.ASTC_4x4, TextureImporterFormat.ASTC_5x5,
                    TextureImporterFormat.ASTC_6x8, TextureImporterFormat.ASTC_6x6,
                    TextureImporterFormat.ASTC_8x8, TextureImporterFormat.RGBA32,
                    TextureImporterFormat.Alpha8);
                if (!npot)
                {
                    platformOK.Add(TextureImporterFormat.PVRTC_2_BPP);
                    platformOK.Add(TextureImporterFormat.PVRTC_4_BPP);
                }
            }

            foreach (var f in platformOK)
            {
                bool ok = true;
                if (npot && (f == TextureImporterFormat.PVRTC_2_BPP || f == TextureImporterFormat.PVRTC_4_BPP))
                {
                    ok = false;
                }
                // channel requirements  通道需求
                switch (cat)
                {
                    case ATOTextureCategory.Transparent:
                        ok &= f == TextureImporterFormat.DXT5 || f == TextureImporterFormat.BC7 ||
                              f == TextureImporterFormat.ETC2A8 || f == TextureImporterFormat.ASTC_4x4 ||
                              f == TextureImporterFormat.ASTC_5x5 || f == TextureImporterFormat.ASTC_6x6 ||
                              f == TextureImporterFormat.ASTC_8x8 || f == TextureImporterFormat.ASTC_6x8 ||
                              f == TextureImporterFormat.RGBA32;
                        break;
                    case ATOTextureCategory.Normal:
                        ok &= f == TextureImporterFormat.BC5 || f == TextureImporterFormat.BC7 ||
                              f == TextureImporterFormat.EACRG || f == TextureImporterFormat.ASTC_4x4 ||
                              f == TextureImporterFormat.ASTC_5x5 || f == TextureImporterFormat.ASTC_6x6 ||
                              f == TextureImporterFormat.ASTC_8x8 || f == TextureImporterFormat.ASTC_6x8 ||
                              f == TextureImporterFormat.RGBA32;
                        break;
                    case ATOTextureCategory.Gray:
                        // all formats except 1-bit alpha  全部格式（除 1bit alpha）
                        ok &= f != TextureImporterFormat.ETC2A && f != TextureImporterFormat.DXT1;
                        break;
                    default: // Opaque 不透明
                        if (hasAlpha)
                        {
                            // needs a real alpha channel  需要真正的 alpha 通道
                            ok &= f == TextureImporterFormat.DXT5 || f == TextureImporterFormat.BC7 ||
                                  f == TextureImporterFormat.ETC2A8 || f == TextureImporterFormat.ASTC_4x4 ||
                                  f == TextureImporterFormat.ASTC_5x5 || f == TextureImporterFormat.ASTC_6x6 ||
                                  f == TextureImporterFormat.ASTC_8x8 || f == TextureImporterFormat.ASTC_6x8 ||
                                  f == TextureImporterFormat.RGBA32;
                        }
                        break;
                }
                if (ok) set.Add(f);
            }
            return set;
        }

        private static void AddAll(HashSet<TextureImporterFormat> set, params TextureImporterFormat[] fs)
        {
            foreach (var f in fs) set.Add(f);
        }

        private static TextureImporterFormat AutoFormat(
            ATOPlatform platform, ATOTextureCategory cat, bool hasAlpha, bool npot)
        {
            if (platform == ATOPlatform.PC)
            {
                switch (cat)
                {
                    case ATOTextureCategory.Normal: return TextureImporterFormat.BC5;
                    case ATOTextureCategory.Transparent: return TextureImporterFormat.BC7;
                    case ATOTextureCategory.Gray: return TextureImporterFormat.BC7;
                    default: return hasAlpha ? TextureImporterFormat.DXT5 : TextureImporterFormat.DXT1;
                }
            }
            if (platform == ATOPlatform.Android)
            {
                switch (cat)
                {
                    case ATOTextureCategory.Normal: return TextureImporterFormat.EACRG;
                    case ATOTextureCategory.Transparent: return TextureImporterFormat.ASTC_6x6;
                    case ATOTextureCategory.Gray: return TextureImporterFormat.ASTC_6x6;
                    default: return hasAlpha ? TextureImporterFormat.ETC2A8 : TextureImporterFormat.ETC2;
                }
            }
            // iOS  iOS
            switch (cat)
            {
                case ATOTextureCategory.Normal: return TextureImporterFormat.ASTC_6x6;
                case ATOTextureCategory.Transparent: return TextureImporterFormat.ASTC_6x6;
                case ATOTextureCategory.Gray: return TextureImporterFormat.ASTC_6x6;
                default: return hasAlpha ? TextureImporterFormat.ASTC_6x6 : TextureImporterFormat.ASTC_8x8;
            }
        }

        /// <summary>Unity platform settings name for the build target.
        /// 构建目标对应的 Unity 平台设置名。</summary>
        public static string PlatformSettingsName(BuildTarget target)
        {
            if (target == BuildTarget.Android) return "Android";
            if (target == BuildTarget.iOS) return "iOS";
            return "Standalone";
        }

        // ------------------------------------------------------------------
        private static bool SampleHasAlpha(Texture2D tex)
        {
            try
            {
                // strip-based sampling (memory friendly)  按条带采样（省内存）
                int w = tex.width, h = tex.height;
                int stripH = Mathf.Max(1, h / 4);
                for (int y = 0; y < h; y += stripH)
                {
                    int ch = Mathf.Min(stripH, h - y);
                    var colors = tex.GetPixels(0, y, w, ch);
                    int step = Mathf.Max(1, colors.Length / 512);
                    for (int i = 0; i < colors.Length; i += step)
                    {
                        if (colors[i].a < 0.999f) return true;
                    }
                }
                return false;
            }
            catch (System.Exception)
            {
                return true; // fail safe: assume alpha needed  失败安全：认为需要 alpha
            }
        }
    }
}
