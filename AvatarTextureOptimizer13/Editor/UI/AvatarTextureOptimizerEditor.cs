// ATO — Avatar Texture Optimizer
// Custom inspector for the per-avatar component. Shows the essential options, folds
// advanced quality parameters and per-platform overrides, and lists the whitelist.
// 组件自定义检视器：显示基础选项，折叠高级质量参数与各平台覆盖，列出白名单。

using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using net.fosa.ato;

namespace net.fosa.ato.editor
{
    [CustomEditor(typeof(AvatarTextureOptimizer))]
    public class AvatarTextureOptimizerEditor : Editor
    {
        private bool _advancedFoldout;
        private bool _platformFoldout;
        private bool _compressionFoldout;

        public override void OnInspectorGUI()
        {
            var t = (AvatarTextureOptimizer)target;

            EditorGUILayout.LabelField(ATOI18n.T(ATOI18nKeys.Name), EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(ATOI18n.T(ATOI18nKeys.Description), MessageType.Info);

            serializedObject.Update();

            EditorGUILayout.PropertyField(serializedObject.FindProperty("enable"),
                new GUIContent(ATOI18n.T(ATOI18nKeys.Enable)));

            DrawQualityPreset(serializedObject.FindProperty("qualityPreset"));

            EditorGUILayout.PropertyField(serializedObject.FindProperty("generateAtlas"),
                new GUIContent(ATOI18n.T(ATOI18nKeys.GenerateAtlas), ATOI18n.T(ATOI18nKeys.GenerateAtlasTooltip)));

            EditorGUILayout.Space();
            EditorGUILayout.PropertyField(serializedObject.FindProperty("minPixelDensity"),
                new GUIContent(ATOI18n.T(ATOI18nKeys.DensityMin), ATOI18n.T(ATOI18nKeys.DensityTooltip)));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("maxPixelDensity"),
                new GUIContent(ATOI18n.T(ATOI18nKeys.DensityMax)));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("islandPadding"),
                new GUIContent(ATOI18n.T(ATOI18nKeys.Padding), ATOI18n.T(ATOI18nKeys.PaddingTooltip)));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("npotAtlas"),
                new GUIContent(ATOI18n.T(ATOI18nKeys.Npot), ATOI18n.T(ATOI18nKeys.NpotTooltip)));

            EditorGUILayout.Space();
            EditorGUILayout.PropertyField(serializedObject.FindProperty("dedupMaterials"),
                new GUIContent(ATOI18n.T(ATOI18nKeys.DedupMaterials)));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("dedupTextures"),
                new GUIContent(ATOI18n.T(ATOI18nKeys.DedupTextures)));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("mipmapsEnabled"),
                new GUIContent(ATOI18n.T(ATOI18nKeys.Mipstreaming), ATOI18n.T(ATOI18nKeys.MipstreamingTooltip)));

            // Advanced quality parameters. 高级质量参数。
            _advancedFoldout = EditorGUILayout.Foldout(_advancedFoldout, ATOI18n.T(ATOI18nKeys.Advanced), true);
            if (_advancedFoldout)
            {
                EditorGUI.indentLevel++;
                var custom = serializedObject.FindProperty("customParameters");
                EditorGUILayout.PropertyField(custom.FindPropertyRelative("msSsim"),
                    new GUIContent(ATOI18n.T(ATOI18nKeys.AdvancedMsSsim)));
                EditorGUILayout.PropertyField(custom.FindPropertyRelative("deltaE"),
                    new GUIContent(ATOI18n.T(ATOI18nKeys.AdvancedDeltaE)));
                EditorGUILayout.PropertyField(custom.FindPropertyRelative("normalAngleDeg"),
                    new GUIContent(ATOI18n.T(ATOI18nKeys.AdvancedNormalAngle)));
                EditorGUILayout.PropertyField(custom.FindPropertyRelative("normalAngleP95Deg"),
                    new GUIContent(ATOI18n.T(ATOI18nKeys.AdvancedNormalAngleP95)));
                EditorGUILayout.PropertyField(custom.FindPropertyRelative("alphaRmse"),
                    new GUIContent(ATOI18n.T(ATOI18nKeys.AdvancedAlphaRmse)));
                EditorGUILayout.PropertyField(custom.FindPropertyRelative("alphaIou"),
                    new GUIContent(ATOI18n.T(ATOI18nKeys.AdvancedAlphaIou)));
                EditorGUILayout.PropertyField(custom.FindPropertyRelative("grayRmse"),
                    new GUIContent(ATOI18n.T(ATOI18nKeys.AdvancedGrayRmse)));
                EditorGUI.indentLevel--;
            }

            // Compression. 压缩。
            _compressionFoldout = EditorGUILayout.Foldout(_compressionFoldout, ATOI18n.T(ATOI18nKeys.Compression), true);
            if (_compressionFoldout)
            {
                EditorGUI.indentLevel++;
                var comp = serializedObject.FindProperty("compression");
                DrawCompressionPopup(comp.FindPropertyRelative("color"),
                    new GUIContent(ATOI18n.T(ATOI18nKeys.CompressionColor)));
                DrawCompressionPopup(comp.FindPropertyRelative("colorTransparent"),
                    new GUIContent(ATOI18n.T(ATOI18nKeys.CompressionColorTransparent)));
                DrawCompressionPopup(comp.FindPropertyRelative("normal"),
                    new GUIContent(ATOI18n.T(ATOI18nKeys.CompressionNormal)));
                DrawCompressionPopup(comp.FindPropertyRelative("grayscale"),
                    new GUIContent(ATOI18n.T(ATOI18nKeys.CompressionGrayscale)));
                EditorGUILayout.PropertyField(comp.FindPropertyRelative("grayscaleForceSingleChannel"),
                    new GUIContent(ATOI18n.T(ATOI18nKeys.CompressionGraySingleChannel)));
                EditorGUI.indentLevel--;
            }

            // Platform overrides. 平台覆盖。
            _platformFoldout = EditorGUILayout.Foldout(_platformFoldout, ATOI18n.T(ATOI18nKeys.PlatformOverride), true);
            if (_platformFoldout)
            {
                EditorGUILayout.HelpBox(ATOI18n.T(ATOI18nKeys.PlatformOverrideTooltip), MessageType.None);
                var overrides = serializedObject.FindProperty("platformOverrides");
                for (int i = 0; i < overrides.arraySize; i++)
                {
                    var entry = overrides.GetArrayElementAtIndex(i);
                    var platform = (ATOPlatform)entry.FindPropertyRelative("platform").enumValueIndex;
                    var label = ATOI18n.T(ATOI18nKeys.Platform + "." + platform);
                    EditorGUILayout.PropertyField(entry.FindPropertyRelative("overrideEnabled"), new GUIContent(label));
                }
            }

            // Whitelist. 白名单。
            EditorGUILayout.Space();
            var whitelist = serializedObject.FindProperty("whitelist");
            EditorGUILayout.PropertyField(whitelist, new GUIContent(ATOI18n.T(ATOI18nKeys.Whitelist), ATOI18n.T(ATOI18nKeys.WhitelistTooltip)), true);

            // Language + verbosity. 语言与详细度。
            EditorGUILayout.Space();
            DrawLanguage(serializedObject.FindProperty("language"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("verboseLogging"),
                new GUIContent(ATOI18n.T(ATOI18nKeys.Verbosity), ATOI18n.T(ATOI18nKeys.VerbosityTooltip)));

            serializedObject.ApplyModifiedProperties();
        }

        private static readonly string[] QualityLabels =
        {
            "Balanced", // ATOQualityPreset.Balanced = 0
            "High",     // High = 1
            "Low",      // Low = 2
            "Lossless", // Lossless = 3
            "Custom",   // Custom = 4
        };

        private void DrawQualityPreset(SerializedProperty prop)
        {
            var labels = new string[QualityLabels.Length];
            for (int i = 0; i < QualityLabels.Length; i++)
                labels[i] = ATOI18n.T(ATOI18nKeys.QualityPrefix + QualityLabels[i]);
            prop.enumValueIndex = EditorGUILayout.Popup(
                ATOI18n.T(ATOI18nKeys.Quality), prop.enumValueIndex, labels);
        }

        private static readonly string[] CompressionLabels =
        {
            "Auto", "NoCompression", "LowCompression", "NormalCompression", "HighCompression", "NormalMapCompression",
        };

        private void DrawCompressionPopup(SerializedProperty prop, GUIContent label)
        {
            var labels = new string[CompressionLabels.Length];
            for (int i = 0; i < CompressionLabels.Length; i++)
                labels[i] = ATOI18n.T(ATOI18nKeys.CompressionPrefix + CompressionLabels[i]);
            prop.enumValueIndex = EditorGUILayout.Popup(label, prop.enumValueIndex, labels);
        }

        private void DrawLanguage(SerializedProperty language)
        {
            var options = new List<string> { ATOI18n.T(ATOI18nKeys.LanguageAuto) };
            foreach (var lang in ATOI18n.AvailableLanguages) options.Add(lang);

            int current = 0;
            if (!string.IsNullOrEmpty(language.stringValue) && language.stringValue != "auto")
            {
                int idx = IndexOf(ATOI18n.AvailableLanguages, language.stringValue);
                if (idx >= 0) current = idx + 1;
            }

            int chosen = EditorGUILayout.Popup(ATOI18n.T(ATOI18nKeys.Language), current, options.ToArray());
            if (chosen == 0) language.stringValue = "auto";
            else if (chosen - 1 < ATOI18n.AvailableLanguages.Count)
                language.stringValue = ATOI18n.AvailableLanguages[chosen - 1];
        }

        private static int IndexOf(System.Collections.Generic.IReadOnlyList<string> list, string value)
        {
            for (int i = 0; i < list.Count; i++)
                if (list[i] == value) return i;
            return -1;
        }
    }
}
