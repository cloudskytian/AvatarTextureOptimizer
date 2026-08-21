using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

// Custom inspector for ATOSettings. All labels come from the i18n system (extensible JSON files).
// ATOSettings 的自定义 Inspector。所有标签来自 i18n 系统（可扩展 JSON 文件）。

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    [CustomEditor(typeof(ATOSettings))]
    [CanEditMultipleObjects]
    public sealed class ATOSettingsEditor : UnityEditor.Editor
    {
        private SerializedProperty _data;
        private bool _showQuality = true, _showAtlas, _showCompression, _showPlatform, _showWhitelist, _showLocalization;

        private void OnEnable()
        {
            _data = serializedObject.FindProperty("data");
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            ATOLocalizer.Select((target as ATOSettings).Data.languageMode, (target as ATOSettings).Data.manualLanguage);
            var T = ATOLocalizer.T;

            EditorGUILayout.HelpBox(T("component.description"), MessageType.Info);

            _showQuality = EditorGUILayout.Foldout(_showQuality, T("ui.header.quality"), true);
            if (_showQuality) QualitySection(T);

            _showAtlas = EditorGUILayout.Foldout(_showAtlas, T("ui.header.atlas"), true);
            if (_showAtlas) AtlasSection(T);

            _showCompression = EditorGUILayout.Foldout(_showCompression, T("ui.header.compression"), true);
            if (_showCompression) CompressionSection(T);

            _showPlatform = EditorGUILayout.Foldout(_showPlatform, T("ui.header.platform"), true);
            if (_showPlatform) PlatformSection(T);

            _showWhitelist = EditorGUILayout.Foldout(_showWhitelist, T("ui.header.whitelist"), true);
            if (_showWhitelist) WhitelistSection(T);

            _showLocalization = EditorGUILayout.Foldout(_showLocalization, T("ui.header.localization"), true);
            if (_showLocalization) LocalizationSection(T);

            serializedObject.ApplyModifiedProperties();
        }

        private void QualitySection(Func<string, string> T)
        {
            var tierProp = _data.FindPropertyRelative("qualityTier");
            var tier = (QualityTierId)tierProp.intValue;
            var names = new[] { QualityTierId.Custom, QualityTierId.Ultra, QualityTierId.High, QualityTierId.Medium, QualityTierId.Low, QualityTierId.Minimum };
            var labels = names.Select(n => T("ui.qualityTier." + n.ToString().ToLowerInvariant())).ToArray();
            int idx = Array.IndexOf(names, tier);
            int newIdx = EditorGUILayout.Popup(T("ui.qualityTier"), idx, labels);
            if (newIdx != idx) tierProp.intValue = (int)names[newIdx];

            var custom = _data.FindPropertyRelative("customTier");
            EditorGUILayout.PropertyField(custom.FindPropertyRelative("targetQuality"), new GUIContent(T("ui.customTier.targetQuality")));
            EditorGUILayout.PropertyField(custom.FindPropertyRelative("minSSIM"), new GUIContent(T("ui.customTier.minSSIM")));
            EditorGUILayout.PropertyField(custom.FindPropertyRelative("maxDeltaE"), new GUIContent(T("ui.customTier.maxDeltaE")));
            EditorGUILayout.PropertyField(custom.FindPropertyRelative("maxAlphaRMSE"), new GUIContent(T("ui.customTier.maxAlphaRMSE")));
            EditorGUILayout.PropertyField(custom.FindPropertyRelative("minCutoutIoU"), new GUIContent(T("ui.customTier.minCutoutIoU")));
            EditorGUILayout.PropertyField(custom.FindPropertyRelative("maxNormalAngleDeg"), new GUIContent(T("ui.customTier.maxNormalAngle")));
            EditorGUILayout.PropertyField(custom.FindPropertyRelative("maxGrayRMSE"), new GUIContent(T("ui.customTier.maxGrayRMSE")));

            EditorGUILayout.PropertyField(_data.FindPropertyRelative("densityMinPxPerMeter"), new GUIContent(T("ui.densityMin")));
            EditorGUILayout.PropertyField(_data.FindPropertyRelative("densityMaxPxPerMeter"), new GUIContent(T("ui.densityMax")));
        }

        private void AtlasSection(Func<string, string> T)
        {
            EditorGUILayout.PropertyField(_data.FindPropertyRelative("generateAtlas"), new GUIContent(T("ui.generateAtlas")));
            var mode = _data.FindPropertyRelative("atlasSizeMode");
            var names = new[] { AtlasSizeMode.PowerOfTwo, AtlasSizeMode.NonPowerOfTwo };
            var labels = names.Select(n => T("ui.atlasSizeMode." + (n == AtlasSizeMode.PowerOfTwo ? "pot" : "npot"))).ToArray();
            int idx = Array.IndexOf(names, (AtlasSizeMode)mode.intValue);
            int newIdx = EditorGUILayout.Popup(T("ui.atlasSizeMode"), idx, labels);
            if (newIdx != idx) mode.intValue = (int)names[newIdx];
            var pad = _data.FindPropertyRelative("minPadding");
            int[] padOptions = { 4, 8, 16, 32, 64 };
            int newPad = EditorGUILayout.IntPopup(T("ui.minPadding"), pad.intValue, padOptions.Select(p => p + "px").ToArray(), padOptions);
            if (newPad != pad.intValue) pad.intValue = newPad;
        }

        private void CompressionSection(Func<string, string> T)
        {
            MipField(_data.FindPropertyRelative("mipColor"), T("ui.mip.color"), T);
            MipField(_data.FindPropertyRelative("mipNormal"), T("ui.mip.normal"), T);
            MipField(_data.FindPropertyRelative("mipMask"), T("ui.mip.mask"), T);
            EditorGUILayout.PropertyField(_data.FindPropertyRelative("compressionColorOpaque"), new GUIContent(T("ui.compression.colorOpaque")));
            EditorGUILayout.PropertyField(_data.FindPropertyRelative("compressionColorAlpha"), new GUIContent(T("ui.compression.colorAlpha")));
            EditorGUILayout.PropertyField(_data.FindPropertyRelative("compressionNormal"), new GUIContent(T("ui.compression.normal")));
            EditorGUILayout.PropertyField(_data.FindPropertyRelative("compressionMask"), new GUIContent(T("ui.compression.mask")));
        }

        private static void MipField(SerializedProperty prop, string label, Func<string, string> T)
        {
            var names = new[] { MipMode.Off, MipMode.On };
            var labels = new[] { T("ui.mip.off"), T("ui.mip.on") };
            int idx = Array.IndexOf(names, (MipMode)prop.intValue);
            int newIdx = EditorGUILayout.Popup(label, idx, labels);
            if (newIdx != idx) prop.intValue = (int)names[newIdx];
        }

        private void PlatformSection(Func<string, string> T)
        {
            var overrides = _data.FindPropertyRelative("platformOverrides");
            for (int i = 0; i < overrides.arraySize; i++)
            {
                var ov = overrides.GetArrayElementAtIndex(i);
                var platform = (ATOPlatform)ov.FindPropertyRelative("platform").intValue;
                string title = platform switch
                {
                    ATOPlatform.PC => T("ui.platform.pc"),
                    ATOPlatform.Android => T("ui.platform.android"),
                    _ => T("ui.platform.ios"),
                };
                EditorGUILayout.BeginVertical("box");
                var enabled = ov.FindPropertyRelative("enabled");
                EditorGUILayout.PropertyField(enabled, new GUIContent(title + " — " + T("ui.platform.override")));
                if (enabled.boolValue)
                {
                    EditorGUILayout.PropertyField(ov.FindPropertyRelative("qualityTier"), new GUIContent(T("ui.qualityTier")));
                    var ovCustom = ov.FindPropertyRelative("overrideCustomTier");
                    EditorGUILayout.PropertyField(ovCustom, new GUIContent(T("ui.customTier")));
                    if (ovCustom.boolValue)
                    {
                        var ct = ov.FindPropertyRelative("customTier");
                        EditorGUILayout.PropertyField(ct.FindPropertyRelative("targetQuality"), new GUIContent(T("ui.customTier.targetQuality")));
                        EditorGUILayout.PropertyField(ct.FindPropertyRelative("minSSIM"), new GUIContent(T("ui.customTier.minSSIM")));
                        EditorGUILayout.PropertyField(ct.FindPropertyRelative("maxDeltaE"), new GUIContent(T("ui.customTier.maxDeltaE")));
                        EditorGUILayout.PropertyField(ct.FindPropertyRelative("maxAlphaRMSE"), new GUIContent(T("ui.customTier.maxAlphaRMSE")));
                        EditorGUILayout.PropertyField(ct.FindPropertyRelative("minCutoutIoU"), new GUIContent(T("ui.customTier.minCutoutIoU")));
                        EditorGUILayout.PropertyField(ct.FindPropertyRelative("maxNormalAngleDeg"), new GUIContent(T("ui.customTier.maxNormalAngle")));
                        EditorGUILayout.PropertyField(ct.FindPropertyRelative("maxGrayRMSE"), new GUIContent(T("ui.customTier.maxGrayRMSE")));
                    }
                    EditorGUILayout.PropertyField(ov.FindPropertyRelative("densityMinPxPerMeter"), new GUIContent(T("ui.densityMin")));
                    EditorGUILayout.PropertyField(ov.FindPropertyRelative("densityMaxPxPerMeter"), new GUIContent(T("ui.densityMax")));
                    EditorGUILayout.PropertyField(ov.FindPropertyRelative("generateAtlas"), new GUIContent(T("ui.generateAtlas")));
                    EditorGUILayout.PropertyField(ov.FindPropertyRelative("atlasSizeMode"), new GUIContent(T("ui.atlasSizeMode")));
                    EditorGUILayout.PropertyField(ov.FindPropertyRelative("minPadding"), new GUIContent(T("ui.minPadding")));
                    MipField(ov.FindPropertyRelative("mipColor"), T("ui.mip.color"), T);
                    MipField(ov.FindPropertyRelative("mipNormal"), T("ui.mip.normal"), T);
                    MipField(ov.FindPropertyRelative("mipMask"), T("ui.mip.mask"), T);
                    EditorGUILayout.PropertyField(ov.FindPropertyRelative("compressionColorOpaque"), new GUIContent(T("ui.compression.colorOpaque")));
                    EditorGUILayout.PropertyField(ov.FindPropertyRelative("compressionColorAlpha"), new GUIContent(T("ui.compression.colorAlpha")));
                    EditorGUILayout.PropertyField(ov.FindPropertyRelative("compressionNormal"), new GUIContent(T("ui.compression.normal")));
                    EditorGUILayout.PropertyField(ov.FindPropertyRelative("compressionMask"), new GUIContent(T("ui.compression.mask")));
                }
                EditorGUILayout.EndVertical();
            }
        }

        private void WhitelistSection(Func<string, string> T)
        {
            EditorGUILayout.HelpBox(T("ui.whitelist.title"), MessageType.None);
            var list = _data.FindPropertyRelative("whitelist");
            if (list.arraySize == 0) EditorGUILayout.LabelField(T("ui.whitelist.empty"));
            for (int i = 0; i < list.arraySize; i++)
            {
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.PropertyField(list.GetArrayElementAtIndex(i), GUIContent.none);
                if (GUILayout.Button("×", GUILayout.Width(20)))
                {
                    list.DeleteArrayElementAtIndex(i);
                    break;
                }
                EditorGUILayout.EndHorizontal();
            }
            if (GUILayout.Button("+ " + T("ui.whitelist.title").Split('(')[0].Trim()))
                list.InsertArrayElementAtIndex(list.arraySize);
        }

        private void LocalizationSection(Func<string, string> T)
        {
            var mode = _data.FindPropertyRelative("languageMode");
            var names = new[] { ATOLanguageMode.Auto, ATOLanguageMode.Manual };
            var labels = new[] { T("ui.language.auto"), T("ui.language.manual") };
            int idx = Array.IndexOf(names, (ATOLanguageMode)mode.intValue);
            int newIdx = EditorGUILayout.Popup(T("ui.language.mode"), idx, labels);
            if (newIdx != idx) mode.intValue = (int)names[newIdx];

            if ((ATOLanguageMode)mode.intValue == ATOLanguageMode.Manual)
            {
                var langs = ATOLocalizer.AvailableLanguages;
                var langIds = langs.Select(l => l.LanguageId).ToArray();
                var manual = _data.FindPropertyRelative("manualLanguage");
                int cur = Array.IndexOf(langIds, manual.stringValue);
                if (cur < 0) cur = 0;
                int n = EditorGUILayout.Popup(T("ui.language.mode"), cur, langIds);
                if (n >= 0 && n < langIds.Length) manual.stringValue = langIds[n];
            }
        }
    }
}
