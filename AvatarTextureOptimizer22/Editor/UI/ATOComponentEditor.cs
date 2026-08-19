// AvatarTextureOptimizer
// File: Editor/UI/ATOComponentEditor.cs
//
// Custom inspector (IMGUI). Beginner-friendly by default: sane presets, the
// language dropdown follows the i18n config files (Auto = NDMF language), and
// advanced options are folded away.
//
// 自定义检查器（IMGUI）。默认对新手友好：合理预设、语言下拉框跟随 i18n
// 配置文件（Auto = NDMF 语言）、高级选项折叠起来。

using System.Collections.Generic;
using net.fosa.avatar_texture_optimizer.editor.localization;
using UnityEditor;
using UnityEngine;

namespace net.fosa.avatar_texture_optimizer.editor.ui
{
    [CustomEditor(typeof(AvatarTextureOptimizer))]
    public sealed class ATOComponentEditor : Editor
    {
        private AvatarTextureOptimizer _target;
        private ATOLocale _locale;
        private bool _showAdvancedQuality;
        private bool _showAtlas;
        private bool _showImport;
        private bool[] _showPlatformOverride = { false, false, false };
        private string[] _localeOptions;
        private string[] _localeValues;

        private void OnEnable()
        {
            _target = (AvatarTextureOptimizer)target;
            ReloadLocaleOptions();
        }

        private void ReloadLocaleOptions()
        {
            var locales = ATOI18n.Locales;
            var options = new List<string> { ATOI18n.T("i18n.auto") };
            var values = new List<string> { "Auto" };
            foreach (var l in locales)
            {
                options.Add($"{l.DisplayName} ({l.Locale})");
                values.Add(l.Locale);
            }
            _localeOptions = options.ToArray();
            _localeValues = values.ToArray();
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            _locale = ATOI18n.ActiveLocale(_target.Locale);

            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField(ATOI18n.T("component.name", _locale), EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(ATOI18n.T("component.description", _locale), MessageType.Info);

            // ---- Language / 语言 ----
            var localeIndex = System.Array.IndexOf(_localeValues, _target.Locale);
            if (localeIndex < 0) localeIndex = 0;
            int newIndex = EditorGUILayout.Popup(ATOI18n.T("i18n.locale", _locale), localeIndex, _localeOptions);
            if (newIndex != localeIndex)
            {
                _target.Locale = _localeValues[newIndex];
                ReloadLocaleOptions();
                EditorUtility.SetDirty(_target);
            }
            _locale = ATOI18n.ActiveLocale(_target.Locale);

            EditorGUILayout.Space(4);

            // ---- Basic switches / 基础开关 ----
            EditorGUILayout.PropertyField(serializedObject.FindProperty("Enabled"),
                new GUIContent(ATOI18n.T("settings.enabled", _locale)));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("GenerateAtlas"),
                new GUIContent(ATOI18n.T("settings.generateAtlas", _locale), ATOI18n.T("settings.generateAtlas.tip", _locale)));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("OptimizeMaterials"),
                new GUIContent(ATOI18n.T("settings.optimizeMaterials", _locale)));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("OptimizeTextures"),
                new GUIContent(ATOI18n.T("settings.optimizeTextures", _locale)));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("VerboseLogging"),
                new GUIContent(ATOI18n.T("settings.verboseLogging", _locale)));

            if (!_target.Enabled) return;

            // ---- Quality / 质量 ----
            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField(ATOI18n.T("quality.header", _locale), EditorStyles.boldLabel);

            var qualityProp = serializedObject.FindProperty("Quality");
            var tierProp = qualityProp.FindPropertyRelative("Tier");
            var oldTier = (QualityTier)tierProp.intValue;
            EditorGUI.BeginChangeCheck();
            tierProp.intValue = (int)(QualityTier)EditorGUILayout.EnumPopup(ATOI18n.T("quality.tier", _locale), (QualityTier)tierProp.intValue);
            if (EditorGUI.EndChangeCheck())
            {
                // Re-apply the preset thresholds unless Custom is selected.
                // Custom 参数由用户自己修改，永不被覆盖。
                // 除非选择 Custom，否则重新应用预设阈值。
                var newTier = (QualityTier)tierProp.intValue;
                if (newTier != QualityTier.Custom)
                {
                    var th = QualitySettings.DefaultThresholds(newTier);
                    WriteThresholds(qualityProp, th);
                }
            }
            _ = oldTier;

            EditorGUILayout.PropertyField(qualityProp.FindPropertyRelative("MinPixelsPerMeter"),
                new GUIContent(ATOI18n.T("quality.minPixelsPerMeter", _locale)));
            EditorGUILayout.PropertyField(qualityProp.FindPropertyRelative("MaxPixelsPerMeter"),
                new GUIContent(ATOI18n.T("quality.maxPixelsPerMeter", _locale)));

            _showAdvancedQuality = EditorGUILayout.Foldout(_showAdvancedQuality,
                new GUIContent(ATOI18n.T("quality.advanced", _locale), ATOI18n.T("quality.advanced.tip", _locale)));
            if (_showAdvancedQuality)
            {
                EditorGUI.indentLevel++;
                var thresholds = qualityProp.FindPropertyRelative("Thresholds");
                var editable = (QualityTier)tierProp.intValue == QualityTier.Custom;
                EditorGUI.BeginDisabledGroup(!editable);
                EditorGUILayout.PropertyField(thresholds.FindPropertyRelative("TargetQuality"),
                    new GUIContent(ATOI18n.T("quality.targetQuality", _locale), ATOI18n.T("quality.targetQuality.tip", _locale)));
                EditorGUILayout.PropertyField(thresholds.FindPropertyRelative("MinMsSsim"),
                    new GUIContent(ATOI18n.T("quality.minMsSsim", _locale)));
                EditorGUILayout.PropertyField(thresholds.FindPropertyRelative("MaxDeltaE"),
                    new GUIContent(ATOI18n.T("quality.maxDeltaE", _locale)));
                EditorGUILayout.PropertyField(thresholds.FindPropertyRelative("MaxAlphaRmse"),
                    new GUIContent(ATOI18n.T("quality.maxAlphaRmse", _locale)));
                EditorGUILayout.PropertyField(thresholds.FindPropertyRelative("MinCutoutIoU"),
                    new GUIContent(ATOI18n.T("quality.minCutoutIoU", _locale)));
                EditorGUILayout.PropertyField(thresholds.FindPropertyRelative("MaxNormalAngleDeg"),
                    new GUIContent(ATOI18n.T("quality.maxNormalAngleDeg", _locale)));
                EditorGUILayout.PropertyField(thresholds.FindPropertyRelative("MaxGrayRmse"),
                    new GUIContent(ATOI18n.T("quality.maxGrayRmse", _locale)));
                EditorGUILayout.PropertyField(thresholds.FindPropertyRelative("SolidColorShortcut"),
                    new GUIContent(ATOI18n.T("quality.solidColorShortcut", _locale)));
                EditorGUI.EndDisabledGroup();
                EditorGUI.indentLevel--;
            }

            // ---- Atlas / 图集 ----
            EditorGUILayout.Space(6);
            _showAtlas = EditorGUILayout.Foldout(_showAtlas, ATOI18n.T("atlas.header", _locale));
            if (_showAtlas && _target.GenerateAtlas)
            {
                EditorGUI.indentLevel++;
                var atlas = serializedObject.FindProperty("Atlas");
                EditorGUILayout.PropertyField(atlas.FindPropertyRelative("MinPadding"),
                    new GUIContent(ATOI18n.T("atlas.minPadding", _locale)));
                EditorGUILayout.PropertyField(atlas.FindPropertyRelative("EnableNPOT"),
                    new GUIContent(ATOI18n.T("atlas.enableNPOT", _locale), ATOI18n.T("atlas.enableNPOT.tip", _locale)));
                EditorGUILayout.PropertyField(atlas.FindPropertyRelative("PullPushFill"),
                    new GUIContent(ATOI18n.T("atlas.pullPushFill", _locale)));
                EditorGUILayout.PropertyField(atlas.FindPropertyRelative("MaxCandidates"),
                    new GUIContent(ATOI18n.T("atlas.maxCandidates", _locale)));
                EditorGUILayout.PropertyField(atlas.FindPropertyRelative("RasterGranularity"),
                    new GUIContent(ATOI18n.T("atlas.rasterGranularity", _locale)));
                EditorGUI.indentLevel--;
            }

            // ---- Import / 导入参数 ----
            EditorGUILayout.Space(6);
            _showImport = EditorGUILayout.Foldout(_showImport, ATOI18n.T("import.header", _locale));
            if (_showImport)
            {
                EditorGUI.indentLevel++;
                var import = serializedObject.FindProperty("Import");
                DrawImportCategory(import, "Transparent", ATOI18n.T("import.category.transparent", _locale));
                DrawImportCategory(import, "Opaque", ATOI18n.T("import.category.opaque", _locale));
                DrawImportCategory(import, "NormalMap", ATOI18n.T("import.category.normalMap", _locale));
                DrawImportCategory(import, "Grayscale", ATOI18n.T("import.category.grayscale", _locale));
                EditorGUILayout.Space(2);
                EditorGUI.BeginDisabledGroup(true);
                EditorGUILayout.PropertyField(import.FindPropertyRelative("ReadWrite"),
                    new GUIContent(ATOI18n.T("import.locked.readWrite", _locale)));
                EditorGUILayout.PropertyField(import.FindPropertyRelative("WrapMode"),
                    new GUIContent(ATOI18n.T("import.locked.wrapMode", _locale)));
                EditorGUI.EndDisabledGroup();
                EditorGUI.indentLevel--;
            }

            // ---- Platform overrides / 平台覆写 ----
            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField(ATOI18n.T("platform.header", _locale), EditorStyles.boldLabel);
            var platforms = serializedObject.FindProperty("Platforms");
            var overrides = platforms.FindPropertyRelative("Overrides");
            for (int i = 0; i < overrides.arraySize && i < 3; i++)
            {
                var ov = overrides.GetArrayElementAtIndex(i);
                var platformName = ov.FindPropertyRelative("Platform").enumValueIndex switch
                {
                    0 => ATOI18n.T("platform.pc", _locale),
                    1 => ATOI18n.T("platform.android", _locale),
                    _ => ATOI18n.T("platform.ios", _locale),
                };
                var enabledProp = ov.FindPropertyRelative("Enabled");
                EditorGUILayout.PropertyField(enabledProp, new GUIContent($"{ATOI18n.T("platform.enabled", _locale)}: {platformName}"));
                if (enabledProp.boolValue)
                {
                    EditorGUI.indentLevel++;
                    _showPlatformOverride[i] = EditorGUILayout.Foldout(_showPlatformOverride[i], platformName);
                    if (_showPlatformOverride[i])
                    {
                        EditorGUILayout.PropertyField(ov.FindPropertyRelative("Compression"),
                            new GUIContent(ATOI18n.T("import.compression", _locale)));
                        EditorGUILayout.PropertyField(ov.FindPropertyRelative("MaxTextureSize"),
                            new GUIContent(ATOI18n.T("import.maxTextureSize", _locale)));
                    }
                    EditorGUI.indentLevel--;
                }
            }

            // ---- Whitelist / 白名单 ----
            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField(ATOI18n.T("whitelist.header", _locale), EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(ATOI18n.T("whitelist.tip", _locale), MessageType.None);
            var whitelist = serializedObject.FindProperty("Whitelist");
            EditorGUILayout.PropertyField(whitelist.FindPropertyRelative("Objects"), true);

            serializedObject.ApplyModifiedProperties();
        }

        private void DrawImportCategory(SerializedProperty import, string fieldName, string label)
        {
            var cat = import.FindPropertyRelative(fieldName);
            EditorGUILayout.LabelField(label, EditorStyles.boldLabel);
            EditorGUI.indentLevel++;
            EditorGUILayout.PropertyField(cat.FindPropertyRelative("Compression"),
                new GUIContent(ATOI18n.T("import.compression", _locale)));
            EditorGUILayout.PropertyField(cat.FindPropertyRelative("CompressionQuality"),
                new GUIContent(ATOI18n.T("import.compressionQuality", _locale)));
            EditorGUILayout.PropertyField(cat.FindPropertyRelative("EnableMipmap"),
                new GUIContent(ATOI18n.T("import.enableMipmap", _locale)));
            EditorGUILayout.PropertyField(cat.FindPropertyRelative("MaxTextureSize"),
                new GUIContent(ATOI18n.T("import.maxTextureSize", _locale)));
            EditorGUILayout.PropertyField(cat.FindPropertyRelative("UseCrunchCompression"),
                new GUIContent(ATOI18n.T("import.useCrunch", _locale)));
            EditorGUI.indentLevel--;
        }

        private void WriteThresholds(SerializedProperty qualityProp, QualityThresholds th)
        {
            var t = qualityProp.FindPropertyRelative("Thresholds");
            t.FindPropertyRelative("TargetQuality").floatValue = th.TargetQuality;
            t.FindPropertyRelative("MinMsSsim").floatValue = th.MinMsSsim;
            t.FindPropertyRelative("MaxDeltaE").floatValue = th.MaxDeltaE;
            t.FindPropertyRelative("MaxAlphaRmse").floatValue = th.MaxAlphaRmse;
            t.FindPropertyRelative("MinCutoutIoU").floatValue = th.MinCutoutIoU;
            t.FindPropertyRelative("MaxNormalAngleDeg").floatValue = th.MaxNormalAngleDeg;
            t.FindPropertyRelative("MaxGrayRmse").floatValue = th.MaxGrayRmse;
            t.FindPropertyRelative("SolidColorShortcut").boolValue = th.SolidColorShortcut;
        }
    }
}
