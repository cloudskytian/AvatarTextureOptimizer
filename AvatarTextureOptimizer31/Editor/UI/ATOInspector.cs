// ATOInspector.cs
// Custom inspector for the ATOComponent. Shows quality preset, atlas options,
// platform overrides (folded by default), advanced settings (folded), and
// texture format settings. Uses i18n for all labels.
// ATOComponent 的自定义 Inspector。
//
// Copyright (c) 2024 fosa. Licensed under the MIT License.

using UnityEditor;
using UnityEngine;

namespace Fosa.AvatarTextureOptimizer
{
    [CustomEditor(typeof(ATOComponent))]
    [CanEditMultipleObjects]
    internal sealed class ATOInspector : Editor
    {
        private SerializedProperty _enabled;
        private SerializedProperty _qualityPreset;
        private SerializedProperty _generateAtlas;
        private SerializedProperty _deduplicateMaterials;
        private SerializedProperty _deduplicateTextures;
        private SerializedProperty _padding;
        private SerializedProperty _useNPOT;
        private SerializedProperty _verboseLogging;
        private SerializedProperty _maxPixelDensity;
        private SerializedProperty _minPixelDensity;
        private SerializedProperty _whitelist;
        private SerializedProperty _advanced;
        private SerializedProperty _platformSettings;
        private SerializedProperty _textureFormats;

        private bool _showAdvanced = false;
        private bool _showPlatform = false;
        private bool _showFormats = false;
        private bool _showHelp = false;
        private int _selectedLanguageIndex = 0;

        private void OnEnable()
        {
            _enabled = serializedObject.FindProperty("_enabled");
            _qualityPreset = serializedObject.FindProperty("_qualityPreset");
            _generateAtlas = serializedObject.FindProperty("_generateAtlas");
            _deduplicateMaterials = serializedObject.FindProperty("_deduplicateMaterials");
            _deduplicateTextures = serializedObject.FindProperty("_deduplicateTextures");
            _padding = serializedObject.FindProperty("_padding");
            _useNPOT = serializedObject.FindProperty("_useNPOT");
            _verboseLogging = serializedObject.FindProperty("_verboseLogging");
            _maxPixelDensity = serializedObject.FindProperty("_maxPixelDensity");
            _minPixelDensity = serializedObject.FindProperty("_minPixelDensity");
            _whitelist = serializedObject.FindProperty("_whitelist");
            _advanced = serializedObject.FindProperty("_advanced");
            _platformSettings = serializedObject.FindProperty("_platformSettings");
            _textureFormats = serializedObject.FindProperty("_textureFormats");
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            // Language selector
            DrawLanguageSelector();

            // Help box
            _showHelp = EditorGUILayout.Foldout(_showHelp, ATOI18n.T("ato.help").Substring(0, Mathf.Min(40, ATOI18n.T("ato.help").Length)) + "...");
            if (_showHelp)
            {
                EditorGUILayout.HelpBox(ATOI18n.T("ato.help"), MessageType.Info);
            }

            EditorGUILayout.Space();

            // Main settings
            EditorGUILayout.PropertyField(_enabled, new GUIContent(ATOI18n.T("ato.enabled")));
            EditorGUILayout.PropertyField(_qualityPreset, new GUIContent(ATOI18n.T("ato.qualityPreset")));
            EditorGUILayout.PropertyField(_generateAtlas, new GUIContent(ATOI18n.T("ato.generateAtlas")));
            EditorGUILayout.PropertyField(_deduplicateMaterials, new GUIContent(ATOI18n.T("ato.deduplicateMaterials")));
            EditorGUILayout.PropertyField(_deduplicateTextures, new GUIContent(ATOI18n.T("ato.deduplicateTextures")));

            EditorGUILayout.Space();

            // Atlas settings
            EditorGUILayout.LabelField("Atlas / 图集", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(_padding, new GUIContent(ATOI18n.T("ato.padding")));
            EditorGUILayout.PropertyField(_useNPOT, new GUIContent(ATOI18n.T("ato.useNPOT")));

            EditorGUILayout.Space();

            // Pixel density
            EditorGUILayout.LabelField("Pixel Density / 像素密度", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(_maxPixelDensity, new GUIContent(ATOI18n.T("ato.maxPixelDensity")));
            EditorGUILayout.PropertyField(_minPixelDensity, new GUIContent(ATOI18n.T("ato.minPixelDensity")));

            EditorGUILayout.Space();

            // Whitelist
            EditorGUILayout.PropertyField(_whitelist, new GUIContent(ATOI18n.T("ato.whitelist")));

            EditorGUILayout.Space();

            // Debug
            EditorGUILayout.PropertyField(_verboseLogging, new GUIContent(ATOI18n.T("ato.verboseLogging")));

            // Advanced settings (folded)
            EditorGUILayout.Space();
            _showAdvanced = EditorGUILayout.Foldout(_showAdvanced, ATOI18n.T("ato.advanced"), true);
            if (_showAdvanced)
            {
                EditorGUI.indentLevel++;
                DrawAdvancedSettings();
                EditorGUI.indentLevel--;
            }

            // Platform overrides (folded, check per-platform to show)
            EditorGUILayout.Space();
            _showPlatform = EditorGUILayout.Foldout(_showPlatform, ATOI18n.T("ato.platformSettings"), true);
            if (_showPlatform)
            {
                EditorGUI.indentLevel++;
                DrawPlatformSettings();
                EditorGUI.indentLevel--;
            }

            // Texture format settings (folded)
            EditorGUILayout.Space();
            _showFormats = EditorGUILayout.Foldout(_showFormats, ATOI18n.T("ato.textureFormats"), true);
            if (_showFormats)
            {
                EditorGUI.indentLevel++;
                DrawTextureFormatSettings();
                EditorGUI.indentLevel--;
            }

            serializedObject.ApplyModifiedProperties();
        }

        private void DrawLanguageSelector()
        {
            var langs = ATOI18n.AvailableLanguages;
            var displayNames = new string[langs.Count + 1];
            displayNames[0] = ATOI18n.GetLanguageDisplayName("auto");
            for (int i = 0; i < langs.Count; i++)
                displayNames[i + 1] = ATOI18n.GetLanguageDisplayName(langs[i]);

            int current = ATOI18n.CurrentLanguage == "auto" ? 0 : langs.IndexOf(ATOI18n.CurrentLanguage) + 1;
            int selected = EditorGUILayout.Popup(ATOI18n.T("ato.language"), current, displayNames);
            if (selected != current)
            {
                ATOI18n.CurrentLanguage = selected == 0 ? "auto" : langs[selected - 1];
            }
        }

        private void DrawAdvancedSettings()
        {
            // Only show if preset is Custom or if user wants to see them
            var presetProp = (QualityPreset)_qualityPreset.enumValueIndex;

            if (presetProp != QualityPreset.Custom)
            {
                EditorGUILayout.HelpBox(
                    "Advanced settings are overridden by the selected preset. Switch to Custom to edit directly.\n" +
                    "高级设置由当前挡位覆盖。切换到自定义挡位可直接编辑。",
                    MessageType.Info);
            }

            EditorGUI.BeginDisabledGroup(presetProp != QualityPreset.Custom);
            EditorGUILayout.PropertyField(_advanced.FindPropertyRelative("mSSSIMThreshold"),
                new GUIContent(ATOI18n.T("ato.msssimThreshold")));
            EditorGUILayout.PropertyField(_advanced.FindPropertyRelative("deltaEThreshold"),
                new GUIContent(ATOI18n.T("ato.deltaEThreshold")));
            EditorGUILayout.PropertyField(_advanced.FindPropertyRelative("alphaRMSEThreshold"),
                new GUIContent(ATOI18n.T("ato.alphaRMSEThreshold")));
            EditorGUILayout.PropertyField(_advanced.FindPropertyRelative("alphaIoUThreshold"),
                new GUIContent(ATOI18n.T("ato.alphaIoUThreshold")));
            EditorGUILayout.PropertyField(_advanced.FindPropertyRelative("normalAngleThreshold"),
                new GUIContent(ATOI18n.T("ato.normalAngleThreshold")));
            EditorGUILayout.PropertyField(_advanced.FindPropertyRelative("grayscaleRMSEThreshold"),
                new GUIContent(ATOI18n.T("ato.grayscaleRMSEThreshold")));
            EditorGUILayout.PropertyField(_advanced.FindPropertyRelative("useGPUAcceleration"));
            EditorGUILayout.PropertyField(_advanced.FindPropertyRelative("useBurstParallelism"));
            EditorGUILayout.PropertyField(_advanced.FindPropertyRelative("rasterGranularity"));
            EditorGUI.EndDisabledGroup();
        }

        private void DrawPlatformSettings()
        {
            EditorGUILayout.PropertyField(_platformSettings.FindPropertyRelative("overridePC"),
                new GUIContent(ATOI18n.T("ato.platform.pc")));
            if (_platformSettings.FindPropertyRelative("overridePC").boolValue)
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.PropertyField(_platformSettings.FindPropertyRelative("maxAtlasSizePC"));
                EditorGUI.indentLevel--;
            }

            EditorGUILayout.PropertyField(_platformSettings.FindPropertyRelative("overrideAndroid"),
                new GUIContent(ATOI18n.T("ato.platform.android")));
            if (_platformSettings.FindPropertyRelative("overrideAndroid").boolValue)
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.PropertyField(_platformSettings.FindPropertyRelative("maxAtlasSizeAndroid"));
                EditorGUI.indentLevel--;
            }

            EditorGUILayout.PropertyField(_platformSettings.FindPropertyRelative("overrideIOS"),
                new GUIContent(ATOI18n.T("ato.platform.ios")));
            if (_platformSettings.FindPropertyRelative("overrideIOS").boolValue)
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.PropertyField(_platformSettings.FindPropertyRelative("maxAtlasSizeIOS"));
                EditorGUI.indentLevel--;
            }
        }

        private void DrawTextureFormatSettings()
        {
            EditorGUILayout.PropertyField(_textureFormats.FindPropertyRelative("enableMipStreaming"));

            EditorGUILayout.LabelField("PC / Windows", EditorStyles.boldLabel);
            EditorGUI.indentLevel++;
            EditorGUILayout.PropertyField(_textureFormats.FindPropertyRelative("transparentFormatPC"));
            EditorGUILayout.PropertyField(_textureFormats.FindPropertyRelative("opaqueFormatPC"));
            EditorGUILayout.PropertyField(_textureFormats.FindPropertyRelative("normalFormatPC"));
            EditorGUILayout.PropertyField(_textureFormats.FindPropertyRelative("maskFormatPC"));
            EditorGUI.indentLevel--;

            EditorGUILayout.LabelField("Android / Quest", EditorStyles.boldLabel);
            EditorGUI.indentLevel++;
            EditorGUILayout.PropertyField(_textureFormats.FindPropertyRelative("transparentFormatAndroid"));
            EditorGUILayout.PropertyField(_textureFormats.FindPropertyRelative("opaqueFormatAndroid"));
            EditorGUILayout.PropertyField(_textureFormats.FindPropertyRelative("normalFormatAndroid"));
            EditorGUILayout.PropertyField(_textureFormats.FindPropertyRelative("maskFormatAndroid"));
            EditorGUI.indentLevel--;

            EditorGUILayout.LabelField("iOS", EditorStyles.boldLabel);
            EditorGUI.indentLevel++;
            EditorGUILayout.PropertyField(_textureFormats.FindPropertyRelative("transparentFormatIOS"));
            EditorGUILayout.PropertyField(_textureFormats.FindPropertyRelative("opaqueFormatIOS"));
            EditorGUILayout.PropertyField(_textureFormats.FindPropertyRelative("normalFormatIOS"));
            EditorGUILayout.PropertyField(_textureFormats.FindPropertyRelative("maskFormatIOS"));
            EditorGUI.indentLevel--;
        }
    }
}
