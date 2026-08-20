// SPDX-License-Identifier: MIT
// EN: Inspector for the Avatar Texture Optimizer component. Simple by default, everything advanced is
//     folded away.
// ZH: Avatar Texture Optimizer 组件的检视面板。默认保持简单，所有高级内容都折叠收起。

using System;
using System.Collections.Generic;
using nadena.dev.ndmf.localization;
using UnityEditor;
using UnityEngine;

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    [CustomEditor(typeof(AvatarTextureOptimizer))]
    public sealed class ATOComponentEditor : UnityEditor.Editor
    {
        private ReorderableWhitelist _whitelist;

        public override void OnInspectorGUI()
        {
            var component = (AvatarTextureOptimizer)target;
            var settings = component.settings ??= new ATOSettings();

            EditorGUI.BeginChangeCheck();

            DrawHeader(component);
            DrawGeneral(settings);
            DrawAdvanced(component, settings);
            DrawOutput(component, settings);
            DrawWhitelist(component, settings);
            DrawDebug(component, settings);

            if (EditorGUI.EndChangeCheck())
            {
                EditorUtility.SetDirty(component);
            }
        }

        private static void DrawHeader(AvatarTextureOptimizer component)
        {
            EditorGUILayout.LabelField(ATOL10n.Tr("ato:component:title"), EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(ATOL10n.Tr("ato:component:desc"), MessageType.None);

            if (!ATOValidationPass.HasAvatarDescriptor(component.gameObject))
            {
                EditorGUILayout.HelpBox(ATOL10n.Tr("ato:error:noDescriptor:description"), MessageType.Error);
            }

            EditorGUILayout.Space();
        }

        private static void DrawGeneral(ATOSettings settings)
        {
            settings.generateAtlas = EditorGUILayout.ToggleLeft(
                ATOL10n.G("ato:generateAtlas", "ato:generateAtlas:tip"), settings.generateAtlas);

            var tier = (ATOQualityTier)EditorGUILayout.EnumPopup(
                ATOL10n.G("ato:qualityTier", "ato:qualityTier:tip"), settings.qualityTier);

            if (tier != settings.qualityTier)
            {
                settings.qualityTier = tier;
                // EN: Changing the tier refreshes the parameters, except for the custom tier.
                // ZH: 切换挡位会刷新参数，自定义挡位除外。
                if (tier != ATOQualityTier.Custom) settings.quality = ATOQualityParameters.ForTier(tier);
            }
        }

        private static void DrawAdvanced(AvatarTextureOptimizer component, ATOSettings settings)
        {
            component.advancedFoldout = EditorGUILayout.Foldout(component.advancedFoldout,
                ATOL10n.Tr("ato:section:advanced"), true);
            if (!component.advancedFoldout) return;

            using (new EditorGUI.IndentLevelScope())
            {
                var q = settings.EffectiveQuality();
                using (new EditorGUI.DisabledScope(settings.qualityTier == ATOQualityTier.Lossless))
                {
                    q.minStructuralSimilarity = EditorGUILayout.Slider(ATOL10n.G("ato:quality:ssim"),
                        q.minStructuralSimilarity, 0.5f, 1f);
                    q.maxDeltaE2000Mean = EditorGUILayout.Slider(ATOL10n.G("ato:quality:deltaEMean"),
                        q.maxDeltaE2000Mean, 0f, 20f);
                    q.maxDeltaE2000P95 = EditorGUILayout.Slider(ATOL10n.G("ato:quality:deltaEP95"),
                        q.maxDeltaE2000P95, 0f, 40f);
                    q.minAlphaIoU = EditorGUILayout.Slider(ATOL10n.G("ato:quality:alphaIoU"),
                        q.minAlphaIoU, 0.5f, 1f);
                    q.maxAlphaRmse = EditorGUILayout.Slider(ATOL10n.G("ato:quality:alphaRmse"),
                        q.maxAlphaRmse, 0f, 0.5f);
                    q.maxNormalAngleMeanDeg = EditorGUILayout.Slider(ATOL10n.G("ato:quality:normalMean"),
                        q.maxNormalAngleMeanDeg, 0f, 45f);
                    q.maxNormalAngleP95Deg = EditorGUILayout.Slider(ATOL10n.G("ato:quality:normalP95"),
                        q.maxNormalAngleP95Deg, 0f, 60f);
                    q.maxGrayscaleRmse = EditorGUILayout.Slider(ATOL10n.G("ato:quality:grayRmse"),
                        q.maxGrayscaleRmse, 0f, 0.5f);

                    q.minPixelDensity = DensityPopup(ATOL10n.G("ato:quality:minDensity"), q.minPixelDensity);
                    q.maxPixelDensity = DensityPopup(ATOL10n.G("ato:quality:maxDensity"), q.maxPixelDensity);
                }

                EditorGUILayout.Space();

                using (new EditorGUI.DisabledScope(!settings.generateAtlas))
                {
                    settings.minPadding = PaddingPopup(ATOL10n.G("ato:atlas:padding"), settings.minPadding);
                    settings.allowNPOT = EditorGUILayout.Toggle(ATOL10n.G("ato:atlas:npot"), settings.allowNPOT);
                    settings.allowIslandRotation =
                        EditorGUILayout.Toggle(ATOL10n.G("ato:atlas:rotation"), settings.allowIslandRotation);
                    settings.mergeOverlappingIslands =
                        EditorGUILayout.Toggle(ATOL10n.G("ato:atlas:mergeOverlap"), settings.mergeOverlappingIslands);
                }

                EditorGUILayout.Space();
                settings.deduplicateMaterials =
                    EditorGUILayout.Toggle(ATOL10n.G("ato:dedup:materials"), settings.deduplicateMaterials);
                settings.deduplicateTextures =
                    EditorGUILayout.Toggle(ATOL10n.G("ato:dedup:textures"), settings.deduplicateTextures);
            }
        }

        private static readonly int[] DensityOptions = { 512, 1024, 2048, 4096, 8192 };

        private static int DensityPopup(GUIContent label, int value)
        {
            var names = new string[DensityOptions.Length];
            var index = 2;
            for (var i = 0; i < DensityOptions.Length; i++)
            {
                names[i] = DensityOptions[i].ToString();
                if (DensityOptions[i] == value) index = i;
            }

            index = EditorGUILayout.Popup(label, index, names);
            return DensityOptions[Mathf.Clamp(index, 0, DensityOptions.Length - 1)];
        }

        private static readonly int[] PaddingOptions = { 4, 8, 16, 32, 64 };

        private static int PaddingPopup(GUIContent label, int value)
        {
            var names = new string[PaddingOptions.Length];
            var index = 0;
            for (var i = 0; i < PaddingOptions.Length; i++)
            {
                names[i] = PaddingOptions[i].ToString();
                if (PaddingOptions[i] == value) index = i;
            }

            index = EditorGUILayout.Popup(label, index, names);
            return PaddingOptions[Mathf.Clamp(index, 0, PaddingOptions.Length - 1)];
        }

        private static void DrawOutput(AvatarTextureOptimizer component, ATOSettings settings)
        {
            component.outputFoldout = EditorGUILayout.Foldout(component.outputFoldout,
                ATOL10n.Tr("ato:section:output"), true);
            if (!component.outputFoldout) return;

            using (new EditorGUI.IndentLevelScope())
            {
                EditorGUILayout.LabelField(ATOL10n.Tr("ato:output:shared"), EditorStyles.boldLabel);
                DrawProfile(settings.sharedProfile, false);

                EditorGUILayout.Space();

                foreach (var profile in settings.platformProfiles)
                {
                    profile.enabled = EditorGUILayout.ToggleLeft(
                        ATOL10n.Tr("ato:output:override", profile.platform.ToString()), profile.enabled);
                    if (!profile.enabled) continue;

                    using (new EditorGUI.IndentLevelScope())
                    {
                        DrawProfile(profile, true);
                    }
                }
            }
        }

        private static void DrawProfile(ATOPlatformProfile profile, bool platformSpecific)
        {
            var maxAllowed = profile.platform == ATOPlatform.PC || !platformSpecific ? 8192 : 4096;
            profile.maxAtlasSize = Mathf.Clamp(
                EditorGUILayout.IntPopup(ATOL10n.G("ato:atlas:maxSize"), profile.maxAtlasSize,
                    new[] { "512", "1024", "2048", "4096", "8192" },
                    new[] { 512, 1024, 2048, 4096, 8192 }), 64, maxAllowed);

            var mobile = platformSpecific && profile.platform != ATOPlatform.PC;

            if (mobile)
            {
                // EN: Mobile platforms only get ASTC / uncompressed choices. ZH: 移动端只提供 ASTC / 未压缩选项。
                profile.formatColorAlpha = (ATOFormatColorAlpha)FilteredEnumPopup(
                    ATOL10n.G("ato:output:formatColorAlpha"), (int)profile.formatColorAlpha,
                    typeof(ATOFormatColorAlpha), MobileAllowedColorAlpha);
                profile.formatColorOpaque = (ATOFormatColorOpaque)FilteredEnumPopup(
                    ATOL10n.G("ato:output:formatColorOpaque"), (int)profile.formatColorOpaque,
                    typeof(ATOFormatColorOpaque), MobileAllowedColorOpaque);
                profile.formatNormal = (ATOFormatNormal)FilteredEnumPopup(
                    ATOL10n.G("ato:output:formatNormal"), (int)profile.formatNormal,
                    typeof(ATOFormatNormal), MobileAllowedNormal);
                profile.formatGrayscale = (ATOFormatGrayscale)FilteredEnumPopup(
                    ATOL10n.G("ato:output:formatGrayscale"), (int)profile.formatGrayscale,
                    typeof(ATOFormatGrayscale), MobileAllowedGrayscale);
            }
            else
            {
                profile.formatColorAlpha = (ATOFormatColorAlpha)EditorGUILayout.EnumPopup(
                    ATOL10n.G("ato:output:formatColorAlpha"), profile.formatColorAlpha);
                profile.formatColorOpaque = (ATOFormatColorOpaque)EditorGUILayout.EnumPopup(
                    ATOL10n.G("ato:output:formatColorOpaque"), profile.formatColorOpaque);
                profile.formatNormal = (ATOFormatNormal)EditorGUILayout.EnumPopup(
                    ATOL10n.G("ato:output:formatNormal"), profile.formatNormal);
                profile.formatGrayscale = (ATOFormatGrayscale)EditorGUILayout.EnumPopup(
                    ATOL10n.G("ato:output:formatGrayscale"), profile.formatGrayscale);
            }

            profile.mipmapColor = EditorGUILayout.Toggle(ATOL10n.G("ato:output:mipColor"), profile.mipmapColor);
            profile.mipmapNormal = EditorGUILayout.Toggle(ATOL10n.G("ato:output:mipNormal"), profile.mipmapNormal);
            profile.mipmapGrayscale =
                EditorGUILayout.Toggle(ATOL10n.G("ato:output:mipGrayscale"), profile.mipmapGrayscale);
            profile.compressionQuality =
                EditorGUILayout.IntSlider(ATOL10n.G("ato:output:compressionQuality"), profile.compressionQuality, 0,
                    100);
        }

        private static readonly int[] MobileAllowedColorAlpha =
        {
            (int)ATOFormatColorAlpha.Automatic, (int)ATOFormatColorAlpha.ASTC_4x4, (int)ATOFormatColorAlpha.ASTC_5x5,
            (int)ATOFormatColorAlpha.ASTC_6x6, (int)ATOFormatColorAlpha.ASTC_8x8,
            (int)ATOFormatColorAlpha.Uncompressed_RGBA32,
        };

        private static readonly int[] MobileAllowedColorOpaque =
        {
            (int)ATOFormatColorOpaque.Automatic, (int)ATOFormatColorOpaque.ASTC_4x4,
            (int)ATOFormatColorOpaque.ASTC_5x5, (int)ATOFormatColorOpaque.ASTC_6x6,
            (int)ATOFormatColorOpaque.ASTC_8x8, (int)ATOFormatColorOpaque.Uncompressed_RGB24,
        };

        private static readonly int[] MobileAllowedNormal =
        {
            (int)ATOFormatNormal.Automatic, (int)ATOFormatNormal.ASTC_4x4, (int)ATOFormatNormal.ASTC_5x5,
            (int)ATOFormatNormal.ASTC_6x6, (int)ATOFormatNormal.Uncompressed_RGBA32,
        };

        private static readonly int[] MobileAllowedGrayscale =
        {
            (int)ATOFormatGrayscale.Automatic, (int)ATOFormatGrayscale.ASTC_4x4, (int)ATOFormatGrayscale.ASTC_6x6,
            (int)ATOFormatGrayscale.Uncompressed_R8, (int)ATOFormatGrayscale.Uncompressed_RGBA32,
        };

        private static int FilteredEnumPopup(GUIContent label, int value, Type enumType, int[] allowed)
        {
            var names = new string[allowed.Length];
            for (var i = 0; i < allowed.Length; i++) names[i] = Enum.GetName(enumType, allowed[i]);
            return EditorGUILayout.IntPopup(label, value, names, allowed);
        }

        private void DrawWhitelist(AvatarTextureOptimizer component, ATOSettings settings)
        {
            component.whitelistFoldout = EditorGUILayout.Foldout(component.whitelistFoldout,
                ATOL10n.Tr("ato:section:whitelist"), true);
            if (!component.whitelistFoldout) return;

            _whitelist ??= new ReorderableWhitelist();
            using (new EditorGUI.IndentLevelScope())
            {
                _whitelist.Draw(settings.whitelist);
            }
        }

        private static void DrawDebug(AvatarTextureOptimizer component, ATOSettings settings)
        {
            component.debugFoldout = EditorGUILayout.Foldout(component.debugFoldout,
                ATOL10n.Tr("ato:section:debug"), true);
            if (!component.debugFoldout) return;

            using (new EditorGUI.IndentLevelScope())
            {
                settings.verboseLogging = EditorGUILayout.Toggle(ATOL10n.G("ato:debug:verbose"),
                    settings.verboseLogging);
                settings.timingProfile = EditorGUILayout.Toggle(ATOL10n.G("ato:debug:timing"), settings.timingProfile);

                var languages = new List<string> { ATOL10n.Tr("ato:debug:languageAuto") };
                languages.AddRange(ATOL10n.AvailableLanguages);

                var current = 0;
                for (var i = 1; i < languages.Count; i++)
                    if (languages[i] == settings.languageOverride)
                        current = i;

                var selected = EditorGUILayout.Popup(ATOL10n.G("ato:debug:language"), current, languages.ToArray());
                settings.languageOverride = selected == 0 ? "" : languages[selected];

                if (selected != 0 && LanguagePrefs.Language != settings.languageOverride)
                    LanguagePrefs.Language = settings.languageOverride;
            }
        }

        /// <summary>
        /// EN: Minimal object list editor for the whitelist (accepts any object type).
        /// ZH: 白名单用的极简对象列表编辑器（接受任意对象类型）。
        /// </summary>
        private sealed class ReorderableWhitelist
        {
            public void Draw(List<UnityEngine.Object> list)
            {
                var remove = -1;
                for (var i = 0; i < list.Count; i++)
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        list[i] = EditorGUILayout.ObjectField(list[i], typeof(UnityEngine.Object), true);
                        if (GUILayout.Button("-", GUILayout.Width(24))) remove = i;
                    }
                }

                if (remove >= 0) list.RemoveAt(remove);

                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.FlexibleSpace();
                    if (GUILayout.Button("+", GUILayout.Width(24))) list.Add(null);
                }
            }
        }
    }
}
