// Custom inspector for AvatarTextureOptimizer with i18n labels.
// / AvatarTextureOptimizer 的自定义检查器，带 i18n 标签。

using UnityEditor;
using UnityEditorInternal;
using UnityEngine;
using net.fosa.avatar_texture_optimizer.editor.localization;
using net.fosa.avatar_texture_optimizer.runtime;

namespace net.fosa.avatar_texture_optimizer.editor.inspector
{
    /// <summary>
    /// Inspector UI. / 检查器 UI。
    /// </summary>
    [CustomEditor(typeof(AvatarTextureOptimizer))]
    public sealed class AvatarTextureOptimizerEditor : Editor
    {
        private bool _showBasic = true;
        private bool _showQuality = true;
        private bool _showPacking = false;
        private bool _showOutput = false;
        private bool _showPlatform = false;
        private bool _showWhitelist = true;
        private bool _showAdvanced = false;
        private ReorderableList _whitelistList;

        private SerializedProperty _language;

        private void OnEnable()
        {
            _language = serializedObject.FindProperty("language");
            Localization.SetLanguage((AtoLanguage)_language.enumValueIndex);
            BuildWhitelistList();
        }

        private void BuildWhitelistList()
        {
            var prop = serializedObject.FindProperty("whitelist");
            _whitelistList = new ReorderableList(serializedObject, prop, true, true, true, true)
            {
                drawHeaderCallback = rect =>
                {
                    EditorGUI.LabelField(rect, Localization.T("component.header.whitelist") +
                        "  (" + Localization.T("component.whitelist.tip") + ")");
                },
                drawElementCallback = (rect, index, active, focused) =>
                {
                    var element = prop.GetArrayElementAtIndex(index);
                    var target = element.FindPropertyRelative("target");
                    var note = element.FindPropertyRelative("note");
                    float h = rect.height;
                    rect.height = h;
                    EditorGUI.PropertyField(new Rect(rect.x, rect.y, rect.width * 0.6f, h), target, GUIContent.none);
                    EditorGUI.PropertyField(new Rect(rect.x + rect.width * 0.62f, rect.y, rect.width * 0.38f, h), note, GUIContent.none);
                },
                onAddCallback = list =>
                {
                    list.serializedProperty.arraySize++;
                },
            };
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            var lang = (AtoLanguage)_language.enumValueIndex;
            Localization.SetLanguage(lang);

            EditorGUILayout.LabelField(Localization.T("component.title"), EditorStyles.boldLabel);
            EditorGUILayout.Space();

            _showBasic = EditorGUILayout.Foldout(_showBasic, Localization.T("component.header.basic"), true);
            if (_showBasic)
            {
                EditorGUILayout.PropertyField(serializedObject.FindProperty("generateAtlas"),
                    new GUIContent(Localization.T("component.generateAtlas"), Localization.T("component.generateAtlas.tip")));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("processAllUvChannels"),
                    new GUIContent(Localization.T("component.processAllUvChannels"), Localization.T("component.processAllUvChannels.tip")));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("deduplicateMaterials"),
                    new GUIContent(Localization.T("component.deduplicateMaterials")));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("deduplicateTextures"),
                    new GUIContent(Localization.T("component.deduplicateTextures")));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("verboseLogs"),
                    new GUIContent(Localization.T("component.verboseLogs")));
                EditorGUILayout.PropertyField(_language, new GUIContent(Localization.T("component.language")));
                if (lang != (AtoLanguage)_language.enumValueIndex) Localization.SetLanguage((AtoLanguage)_language.enumValueIndex);
            }

            _showQuality = EditorGUILayout.Foldout(_showQuality, Localization.T("component.header.quality"), true);
            if (_showQuality)
            {
                var preset = serializedObject.FindProperty("quality.preset");
                EditorGUILayout.PropertyField(preset, new GUIContent(Localization.T("component.quality.preset")));
                if (preset.enumValueIndex == (int)AvatarTextureOptimizer.QualityPreset.Custom)
                {
                    EditorGUI.indentLevel++;
                    EditorGUILayout.PropertyField(serializedObject.FindProperty("quality.custom.ssim"), new GUIContent(Localization.T("component.quality.ssim")));
                    EditorGUILayout.PropertyField(serializedObject.FindProperty("quality.custom.deltaE"), new GUIContent(Localization.T("component.quality.deltaE")));
                    EditorGUILayout.PropertyField(serializedObject.FindProperty("quality.custom.alpha"), new GUIContent(Localization.T("component.quality.alpha")));
                    EditorGUILayout.PropertyField(serializedObject.FindProperty("quality.custom.normalAngle"), new GUIContent(Localization.T("component.quality.normalAngle")));
                    EditorGUILayout.PropertyField(serializedObject.FindProperty("quality.custom.grayRms"), new GUIContent(Localization.T("component.quality.grayRms")));
                    EditorGUI.indentLevel--;
                }
                EditorGUILayout.PropertyField(serializedObject.FindProperty("quality.minPixelsPerMeter"), new GUIContent(Localization.T("component.quality.minPpm")));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("quality.maxPixelsPerMeter"), new GUIContent(Localization.T("component.quality.maxPpm")));
                DrawDensityPresets(serializedObject.FindProperty("quality.minPixelsPerMeter"),
                    serializedObject.FindProperty("quality.maxPixelsPerMeter"),
                    serializedObject.FindProperty("quality.densityOptions"));
            }

            _showPacking = EditorGUILayout.Foldout(_showPacking, Localization.T("component.header.packing"), true);
            if (_showPacking)
            {
                EditorGUILayout.PropertyField(serializedObject.FindProperty("packing.allowNPOT"), new GUIContent(Localization.T("component.packing.allowNPOT")));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("packing.minPadding"), new GUIContent(Localization.T("component.packing.minPadding")));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("packing.pullPush"), new GUIContent(Localization.T("component.packing.pullPush")));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("packing.minAtlasSize"), new GUIContent(Localization.T("component.packing.minAtlasSize")));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("packing.maxAtlasSize"), new GUIContent(Localization.T("component.packing.maxAtlasSize")));
            }

            _showOutput = EditorGUILayout.Foldout(_showOutput, Localization.T("component.header.output"), true);
            if (_showOutput)
            {
                EditorGUILayout.PropertyField(serializedObject.FindProperty("output.mipmap"), new GUIContent(Localization.T("component.output.mipmap")));
                DrawCompression(serializedObject.FindProperty("output.compression.opaque"), Localization.T("component.output.compression.opaque"));
                DrawCompression(serializedObject.FindProperty("output.compression.transparent"), Localization.T("component.output.compression.transparent"));
                DrawCompression(serializedObject.FindProperty("output.compression.normal"), Localization.T("component.output.compression.normal"));
                DrawCompression(serializedObject.FindProperty("output.compression.grayscale"), Localization.T("component.output.compression.grayscale"));
            }

            _showPlatform = EditorGUILayout.Foldout(_showPlatform, Localization.T("component.header.platform"), true);
            if (_showPlatform)
            {
                var enable = serializedObject.FindProperty("platform.enableOverrides");
                EditorGUILayout.PropertyField(enable, new GUIContent(Localization.T("component.platform.enableOverrides")));
                if (enable.boolValue)
                {
                    DrawPlatform("platform.pc", Localization.T("component.platform.pc"));
                    DrawPlatform("platform.android", Localization.T("component.platform.android"));
                    DrawPlatform("platform.ios", Localization.T("component.platform.ios"));
                }
            }

            _showWhitelist = EditorGUILayout.Foldout(_showWhitelist, Localization.T("component.header.whitelist"), true);
            if (_showWhitelist)
            {
                _whitelistList.DoLayoutList();
            }

            _showAdvanced = EditorGUILayout.Foldout(_showAdvanced, Localization.T("component.header.advanced"), true);
            if (_showAdvanced)
            {
                EditorGUILayout.PropertyField(serializedObject.FindProperty("maxTextureSizeOverride"),
                    new GUIContent(Localization.T("component.advanced.maxTextureSize")));
            }

            EditorGUILayout.HelpBox(
                "This component optimizes the avatar during NDMF build (after Modular Avatar, before Avatar Optimizer). " +
                "Attach it to the avatar root with VRCAvatarDescriptor. / 该组件在 NDMF 构建时（MA 之后、AAO 之前）优化 Avatar。" +
                "请挂载在带 VRCAvatarDescriptor 的 Avatar 根物体上。",
                MessageType.Info);

            serializedObject.ApplyModifiedProperties();
        }

        private void DrawDensityPresets(SerializedProperty minProp, SerializedProperty maxProp, SerializedProperty optionsProp)
        {
            EditorGUILayout.BeginHorizontal();
            var opts = new int[optionsProp.arraySize];
            var labels = new string[optionsProp.arraySize];
            for (int i = 0; i < optionsProp.arraySize; i++)
            {
                opts[i] = optionsProp.GetArrayElementAtIndex(i).intValue;
                labels[i] = opts[i].ToString();
            }
            int sel = EditorGUILayout.Popup(new GUIContent("px/m preset"), -1, labels);
            if (sel >= 0 && sel < opts.Length)
            {
                minProp.intValue = opts[sel];
                maxProp.intValue = opts[sel] * 2;
            }
            EditorGUILayout.EndHorizontal();
        }

        private void DrawCompression(SerializedProperty prop, string label)
        {
            EditorGUILayout.PropertyField(prop, new GUIContent(label));
        }

        private void DrawPlatform(string path, string title)
        {
            EditorGUILayout.LabelField(title, EditorStyles.boldLabel);
            EditorGUI.indentLevel++;
            var root = serializedObject.FindProperty(path);
            EditorGUILayout.PropertyField(root.FindPropertyRelative("enabled"), new GUIContent(Localization.T("component.platform.enabled")));
            if (root.FindPropertyRelative("enabled").boolValue)
            {
                EditorGUILayout.PropertyField(root.FindPropertyRelative("maxAtlasSize"), new GUIContent(Localization.T("component.platform.maxAtlasSize")));
                DrawCompression(root.FindPropertyRelative("opaque"), Localization.T("component.output.compression.opaque"));
                DrawCompression(root.FindPropertyRelative("transparent"), Localization.T("component.output.compression.transparent"));
                DrawCompression(root.FindPropertyRelative("normal"), Localization.T("component.output.compression.normal"));
                DrawCompression(root.FindPropertyRelative("grayscale"), Localization.T("component.output.compression.grayscale"));
            }
            EditorGUI.indentLevel--;
        }
    }
}
