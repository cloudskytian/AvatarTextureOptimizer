// SPDX-License-Identifier: MIT
// AvatarTextureOptimizer (ATO) - Component inspector.
// AvatarTextureOptimizer (ATO) - 组件面板。

using System.Collections.Generic;
using System.Linq;
using Net.Fosa.AvatarTextureOptimizer.Editor.Localization;
using UnityEditor;
using UnityEngine;
#if ATO_VRCSDK3_AVATARS
using VRC.SDK3.Avatars.Components;
#endif

namespace Net.Fosa.AvatarTextureOptimizer.Editor.UI
{
    /// <summary>
    /// EN: Inspector for <see cref="AvatarTextureOptimizer"/>. Designed so a complete beginner can attach the
    ///     component and press upload, while every knob remains reachable behind foldouts.
    /// ZH: <see cref="AvatarTextureOptimizer"/> 的面板。设计目标是让完全的新手挂上组件直接上传即可，
    ///     同时所有高级参数都收纳在折叠区中随时可用。
    /// </summary>
    [CustomEditor(typeof(AvatarTextureOptimizer))]
    public sealed class ATOComponentEditor : UnityEditor.Editor
    {
        private static bool _advancedFoldout;
        private static bool _outputFoldout;
        private static bool _whitelistFoldout;
        private static bool _debugFoldout;
        private static readonly Dictionary<ATOPlatform, bool> _platformFoldout = new Dictionary<ATOPlatform, bool>();

        private ReorderableListDrawer _whitelistDrawer;

        public override void OnInspectorGUI()
        {
            var component = (AvatarTextureOptimizer)target;
            var settings = component.settings;

            serializedObject.Update();
            EditorGUI.BeginChangeCheck();

            DrawHeaderBox(component);
            DrawLanguageSelector(settings);

            EditorGUILayout.Space(4);
            DrawCommon(settings.common, settings, isOverride: false);

            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField(ATOL.Tr("ATO:ui:platform_overrides"), EditorStyles.boldLabel);
            foreach (var over in settings.platformOverrides)
            {
                DrawPlatformOverride(settings, over);
            }

            EditorGUILayout.Space(6);
            _whitelistFoldout = EditorGUILayout.Foldout(_whitelistFoldout, ATOL.Tr("ATO:ui:whitelist"), true);
            if (_whitelistFoldout)
            {
                EditorGUILayout.HelpBox(ATOL.Tr("ATO:ui:whitelist_help"), MessageType.Info);
                DrawWhitelist(settings);
            }

            EditorGUILayout.Space(6);
            _debugFoldout = EditorGUILayout.Foldout(_debugFoldout, ATOL.Tr("ATO:ui:debug"), true);
            if (_debugFoldout)
            {
                settings.verboseLogging = EditorGUILayout.Toggle(ATOL.G("ATO:ui:verbose"), settings.verboseLogging);
                using (new EditorGUI.DisabledScope(!settings.verboseLogging))
                {
                    settings.traceIslandMetrics =
                        EditorGUILayout.Toggle(ATOL.G("ATO:ui:trace"), settings.traceIslandMetrics);
                }
            }

            if (EditorGUI.EndChangeCheck())
            {
                EditorUtility.SetDirty(component);
            }
            serializedObject.ApplyModifiedProperties();
        }

        private void DrawHeaderBox(AvatarTextureOptimizer component)
        {
            EditorGUILayout.LabelField("Avatar Texture Optimizer", EditorStyles.largeLabel);
            EditorGUILayout.LabelField(ATOL.Tr("ATO:ui:tagline"), EditorStyles.miniLabel);

#if ATO_VRCSDK3_AVATARS
            if (component.GetComponent<VRCAvatarDescriptor>() == null)
            {
                EditorGUILayout.HelpBox(ATOL.Tr("ATO:ui:err_no_descriptor"), MessageType.Error);
            }
#endif
            var root = component.transform.root;
            var all = root.GetComponentsInChildren<AvatarTextureOptimizer>(true);
            if (all.Length > 1)
            {
                EditorGUILayout.HelpBox(ATOL.Tr("ATO:ui:err_multiple"), MessageType.Error);
            }
        }

        private void DrawLanguageSelector(ATOSettings settings)
        {
            var languages = new List<string> { "Auto" };
            languages.AddRange(ATOL.AvailableLanguages);

            int current = settings.languageMode == ATOLanguageMode.Auto
                ? 0
                : Mathf.Max(0, languages.IndexOf(settings.explicitLanguage));

            int picked = EditorGUILayout.Popup(ATOL.Tr("ATO:ui:language"), current, languages.ToArray());
            if (picked == 0)
            {
                settings.languageMode = ATOLanguageMode.Auto;
                ATOL.ExplicitLanguage = null;
            }
            else
            {
                settings.languageMode = ATOLanguageMode.Explicit;
                settings.explicitLanguage = languages[picked];
                ATOL.ExplicitLanguage = settings.explicitLanguage;
            }
        }

        private void DrawCommon(ATOPlatformSettings o, ATOSettings settings, bool isOverride)
        {
            EditorGUILayout.LabelField(
                isOverride ? ATOL.Tr("ATO:ui:override_params") : ATOL.Tr("ATO:ui:common_params"),
                EditorStyles.boldLabel);

            EditorGUI.BeginChangeCheck();
            o.qualityTier = (ATOQualityTier)EditorGUILayout.EnumPopup(ATOL.G("ATO:ui:quality_tier"), o.qualityTier);
            if (EditorGUI.EndChangeCheck() && o.qualityTier != ATOQualityTier.Custom)
            {
                // EN: Switching tiers refreshes the concrete parameters, but never the custom set.
                // ZH: 切换挡位会刷新具体参数，但绝不会覆盖自定义参数集。
                o.quality = ATOQualityParams.ForTier(o.qualityTier);
            }

            o.generateAtlas = EditorGUILayout.Toggle(ATOL.G("ATO:ui:generate_atlas"), o.generateAtlas);

            _advancedFoldout = EditorGUILayout.Foldout(_advancedFoldout, ATOL.Tr("ATO:ui:advanced"), true);
            if (_advancedFoldout)
            {
                EditorGUI.indentLevel++;
                var q = o.qualityTier == ATOQualityTier.Custom ? o.customQuality : o.quality;
                using (new EditorGUI.DisabledScope(o.qualityTier != ATOQualityTier.Custom))
                {
                    q.msSsimMin = EditorGUILayout.Slider(ATOL.G("ATO:ui:msssim"), q.msSsimMin, 0.5f, 1f);
                    q.deltaE2000Mean = EditorGUILayout.FloatField(ATOL.G("ATO:ui:de_mean"), q.deltaE2000Mean);
                    q.deltaE2000P95 = EditorGUILayout.FloatField(ATOL.G("ATO:ui:de_p95"), q.deltaE2000P95);
                    q.alphaCutoutIoUMin = EditorGUILayout.Slider(ATOL.G("ATO:ui:alpha_iou"),
                        q.alphaCutoutIoUMin, 0.5f, 1f);
                    q.alphaBlendRmseMax = EditorGUILayout.FloatField(ATOL.G("ATO:ui:alpha_rmse"),
                        q.alphaBlendRmseMax);
                    q.normalAngleMeanMaxDeg = EditorGUILayout.FloatField(ATOL.G("ATO:ui:normal_mean"),
                        q.normalAngleMeanMaxDeg);
                    q.normalAngleP95MaxDeg = EditorGUILayout.FloatField(ATOL.G("ATO:ui:normal_p95"),
                        q.normalAngleP95MaxDeg);
                    q.grayscaleRmseMax = EditorGUILayout.FloatField(ATOL.G("ATO:ui:gray_rmse"),
                        q.grayscaleRmseMax);
                    q.minPixelDensity = (ATOPixelDensity)EditorGUILayout.EnumPopup(
                        ATOL.G("ATO:ui:min_density"), q.minPixelDensity);
                    q.maxPixelDensity = (ATOPixelDensity)EditorGUILayout.EnumPopup(
                        ATOL.G("ATO:ui:max_density"), q.maxPixelDensity);
                    q.lossless = EditorGUILayout.Toggle(ATOL.G("ATO:ui:lossless"), q.lossless);
                }

                EditorGUILayout.Space(4);
                o.minPadding = (ATOMinPadding)EditorGUILayout.EnumPopup(ATOL.G("ATO:ui:padding"), o.minPadding);
                o.maxAtlasSize = EditorGUILayout.IntPopup(ATOL.Tr("ATO:ui:max_atlas"), o.maxAtlasSize,
                    new[] { "512", "1024", "2048", "4096", "8192" }, new[] { 512, 1024, 2048, 4096, 8192 });
                o.experimentalNpot = EditorGUILayout.Toggle(ATOL.G("ATO:ui:npot"), o.experimentalNpot);
                if (o.experimentalNpot) EditorGUILayout.HelpBox(ATOL.Tr("ATO:ui:npot_help"), MessageType.Warning);

                o.dedupMaterials = EditorGUILayout.Toggle(ATOL.G("ATO:ui:dedup_materials"), o.dedupMaterials);
                o.dedupTextures = EditorGUILayout.Toggle(ATOL.G("ATO:ui:dedup_textures"), o.dedupTextures);
                EditorGUI.indentLevel--;
            }

            _outputFoldout = EditorGUILayout.Foldout(_outputFoldout, ATOL.Tr("ATO:ui:output"), true);
            if (_outputFoldout)
            {
                EditorGUI.indentLevel++;
                DrawClass(ATOL.Tr("ATO:ui:cls_opaque"), o.opaqueColor, o.platform);
                DrawClass(ATOL.Tr("ATO:ui:cls_transparent"), o.transparentColor, o.platform);
                DrawClass(ATOL.Tr("ATO:ui:cls_normal"), o.normalMap, o.platform);
                DrawClass(ATOL.Tr("ATO:ui:cls_grayscale"), o.grayscale, o.platform);
                EditorGUI.indentLevel--;
            }
        }

        private static void DrawClass(string label, ATOTextureClassSettings s, ATOPlatform platform)
        {
            EditorGUILayout.LabelField(label, EditorStyles.miniBoldLabel);
            EditorGUI.indentLevel++;

            var allowed = AllowedFormats(platform);
            var names = allowed.Select(f => f.ToString()).ToArray();
            int index = System.Array.IndexOf(allowed, s.format);
            if (index < 0) index = 0;
            index = EditorGUILayout.Popup(ATOL.Tr("ATO:ui:format"), index, names);
            s.format = allowed[index];

            s.compressionQuality = EditorGUILayout.IntSlider(ATOL.Tr("ATO:ui:comp_quality"),
                s.compressionQuality, 0, 100);
            s.mipmapAndStreaming = EditorGUILayout.Toggle(ATOL.G("ATO:ui:mipmaps"), s.mipmapAndStreaming);

            EditorGUI.indentLevel--;
        }

        /// <summary>
        /// EN: Only formats that are valid on the selected platform are ever offered, which is half of the
        ///     "no option combination can break a material" guarantee (the other half is the build-time
        ///     validator in TextureOutput).
        /// ZH: 只提供在所选平台上有效的格式，这是“任何选项组合都不会让材质出错”承诺的一半
        ///     （另一半是构建时 TextureOutput 中的校验器）。
        /// </summary>
        private static ATOCompressionFormat[] AllowedFormats(ATOPlatform platform)
        {
            switch (platform)
            {
                case ATOPlatform.Android:
                    return new[]
                    {
                        ATOCompressionFormat.Auto,
                        ATOCompressionFormat.ASTC_4x4, ATOCompressionFormat.ASTC_5x5,
                        ATOCompressionFormat.ASTC_6x6, ATOCompressionFormat.ASTC_8x8,
                        ATOCompressionFormat.ASTC_10x10, ATOCompressionFormat.ASTC_12x12,
                        ATOCompressionFormat.ETC2_RGB4, ATOCompressionFormat.ETC2_RGBA8,
                        ATOCompressionFormat.ETC2_RGB4Crunched, ATOCompressionFormat.ETC2_RGBA8Crunched,
                        ATOCompressionFormat.RGBA32,
                    };
                case ATOPlatform.iOS:
                    // EN: PVRTC is deliberately excluded - it requires square power-of-two textures and is
                    //     incompatible with the NPOT option; ASTC is universally available on iOS anyway.
                    // ZH: 刻意剔除 PVRTC——它要求正方形且 2 的幂尺寸，与 NPOT 选项不兼容；
                    //     而 ASTC 在 iOS 上本来就全平台可用。
                    return new[]
                    {
                        ATOCompressionFormat.Auto,
                        ATOCompressionFormat.ASTC_4x4, ATOCompressionFormat.ASTC_5x5,
                        ATOCompressionFormat.ASTC_6x6, ATOCompressionFormat.ASTC_8x8,
                        ATOCompressionFormat.ASTC_10x10, ATOCompressionFormat.ASTC_12x12,
                        ATOCompressionFormat.RGBA32,
                    };
                default:
                    return new[]
                    {
                        ATOCompressionFormat.Auto,
                        ATOCompressionFormat.BC7, ATOCompressionFormat.BC5, ATOCompressionFormat.BC4,
                        ATOCompressionFormat.DXT1, ATOCompressionFormat.DXT5,
                        ATOCompressionFormat.DXT1Crunched, ATOCompressionFormat.DXT5Crunched,
                        ATOCompressionFormat.RGBA32, ATOCompressionFormat.RGB24,
                        ATOCompressionFormat.RG16, ATOCompressionFormat.R8,
                    };
            }
        }

        private void DrawPlatformOverride(ATOSettings settings, ATOPlatformSettings over)
        {
            EditorGUILayout.BeginHorizontal();
            over.enabled = EditorGUILayout.ToggleLeft($"{over.platform}", over.enabled, GUILayout.Width(120));
            EditorGUILayout.EndHorizontal();

            if (!over.enabled) return;

            _platformFoldout.TryGetValue(over.platform, out var open);
            open = EditorGUILayout.Foldout(open, $"{over.platform} {ATOL.Tr("ATO:ui:settings")}", true);
            _platformFoldout[over.platform] = open;
            if (!open) return;

            EditorGUI.indentLevel++;
            DrawCommon(over, settings, isOverride: true);
            EditorGUI.indentLevel--;
        }

        private void DrawWhitelist(ATOSettings settings)
        {
            for (int i = 0; i < settings.whitelist.Count; i++)
            {
                EditorGUILayout.BeginHorizontal();
                settings.whitelist[i] = EditorGUILayout.ObjectField(settings.whitelist[i],
                    typeof(UnityEngine.Object), true);
                if (GUILayout.Button("-", GUILayout.Width(24)))
                {
                    settings.whitelist.RemoveAt(i);
                    i--;
                }
                EditorGUILayout.EndHorizontal();
            }

            if (GUILayout.Button(ATOL.Tr("ATO:ui:whitelist_add")))
            {
                settings.whitelist.Add(null);
            }
        }
    }

    /// <summary>EN: Placeholder for future list UI enhancements. ZH: 为将来的列表 UI 增强预留。</summary>
    internal sealed class ReorderableListDrawer { }
}
