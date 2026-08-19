// ============================================================================
// AvatarTextureOptimizer (net.fosa.avatar-texture-optimizer)
// UI/ATOComponentEditor.cs — 组件检查器 / Component inspector
//
// 需求:
//  - 面向小白（默认折叠高级选项）+ 支持高级用户。
//  - 全平台贴图/图集参数默认折叠；platform override 勾选对应平台才显示。
//  - 质量挡位变化时具体参数值同步变化；自定义挡位参数用户可改（不被其他挡位覆盖）。
//  - 语言选项手动切换；默认 Auto 读 ndmf 语言。
// ============================================================================
using UnityEditor;
using UnityEngine;

namespace net.fosa.avatar_texture_optimizer.editor
{
    /// <summary>
    /// ATO 组件检查器 / ATO component inspector.
    /// </summary>
    [CustomEditor(typeof(ATOComponent))]
    [CanEditMultipleObjects]
    public sealed class ATOComponentEditor : Editor
    {
        private static readonly int[] DensityOptions = { 512, 1024, 2048, 4096, 8192 };
        private static readonly int[] PaddingOptions = { 4, 8, 16, 32, 64 };

        private bool _showAdvanced;
        private bool _showAtlas;
        private bool _showImport;
        private bool _showPlatform;
        private bool _showQuality;
        private bool _showWhitelist;

        private SerializedProperty _generateAtlases;
        private SerializedProperty _qualityPreset;
        private SerializedProperty _customQuality;
        private SerializedProperty _minDensity;
        private SerializedProperty _maxDensity;
        private SerializedProperty _padding;
        private SerializedProperty _npot;
        private SerializedProperty _crunch;
        private SerializedProperty _platformOverride;
        private SerializedProperty _language;
        private SerializedProperty _verbose;
        private SerializedProperty _whitelist;
        private SerializedProperty _opaqueImport;
        private SerializedProperty _transparentImport;
        private SerializedProperty _normalImport;
        private SerializedProperty _grayscaleImport;
        private SerializedProperty _pc, _android, _ios;

        private void OnEnable()
        {
            _generateAtlases = serializedObject.FindProperty("generateAtlases");
            _qualityPreset = serializedObject.FindProperty("qualityPreset");
            _customQuality = serializedObject.FindProperty("customQuality");
            _minDensity = serializedObject.FindProperty("minPixelDensity");
            _maxDensity = serializedObject.FindProperty("maxPixelDensity");
            _padding = serializedObject.FindProperty("paddingOption");
            _npot = serializedObject.FindProperty("experimentalNpot");
            _crunch = serializedObject.FindProperty("crunch");
            _platformOverride = serializedObject.FindProperty("platformOverrideEnabled");
            _language = serializedObject.FindProperty("language");
            _verbose = serializedObject.FindProperty("verboseLogging");
            _whitelist = serializedObject.FindProperty("whitelist");
            _opaqueImport = serializedObject.FindProperty("opaqueImport");
            _transparentImport = serializedObject.FindProperty("transparentImport");
            _normalImport = serializedObject.FindProperty("normalImport");
            _grayscaleImport = serializedObject.FindProperty("grayscaleImport");
            _pc = serializedObject.FindProperty("pc");
            _android = serializedObject.FindProperty("android");
            _ios = serializedObject.FindProperty("ios");
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            // 标题 / header
            EditorGUILayout.LabelField("AvatarTextureOptimizer", EditorStyles.boldLabel);
            EditorGUILayout.Space();

            // 语言 / language
            EditorGUILayout.PropertyField(_language, new GUIContent(I18n.T("general.language")));
            if (_language.enumValueIndex == (int)ATOLanguage.Auto)
            {
                EditorGUILayout.HelpBox(I18n.T("general.language.desc", "") + " (Auto: " + I18n.CurrentLocale + ")", MessageType.None);
            }

            // 主开关 / main toggles
            EditorGUILayout.PropertyField(_generateAtlases, new GUIContent(I18n.T("general.generateAtlases")));
            if (_generateAtlases.boolValue)
            {
                EditorGUILayout.HelpBox(I18n.T("general.generateAtlases.desc"), MessageType.None);
            }

            // 质量挡位 / quality preset
            EditorGUILayout.PropertyField(_qualityPreset, new GUIContent(I18n.T("general.qualityPreset")));
            if (_qualityPreset.enumValueIndex == (int)QualityPreset.Custom)
            {
                _showQuality = EditorGUILayout.Foldout(_showQuality, I18n.T("quality.foldout"), true);
                if (_showQuality)
                {
                    EditorGUI.indentLevel++;
                    var q = serializedObject.FindProperty("customQuality");
                    EditorGUILayout.PropertyField(q.FindPropertyRelative("msSsim"), new GUIContent(I18n.T("quality.msSsim")));
                    EditorGUILayout.PropertyField(q.FindPropertyRelative("maxDeltaE"), new GUIContent(I18n.T("quality.maxDeltaE")));
                    EditorGUILayout.PropertyField(q.FindPropertyRelative("minAlphaCutoutIoU"), new GUIContent(I18n.T("quality.minAlphaCutoutIoU")));
                    EditorGUILayout.PropertyField(q.FindPropertyRelative("maxAlphaBlendRmse"), new GUIContent(I18n.T("quality.maxAlphaBlendRmse")));
                    EditorGUILayout.PropertyField(q.FindPropertyRelative("maxNormalAngleDeg"), new GUIContent(I18n.T("quality.maxNormalAngleDeg")));
                    EditorGUILayout.PropertyField(q.FindPropertyRelative("maxGrayRmse"), new GUIContent(I18n.T("quality.maxGrayRmse")));
                    EditorGUI.indentLevel--;
                }
            }

            // 像素密度 / density
            EditorGUILayout.IntPopup(_minDensity, DensityLabels(), DensityOptions, new GUIContent(I18n.T("general.minPixelDensity")));
            EditorGUILayout.IntPopup(_maxDensity, DensityLabels(), DensityOptions, new GUIContent(I18n.T("general.maxPixelDensity")));
            if (_minDensity.intValue > _maxDensity.intValue) _maxDensity.intValue = _minDensity.intValue;

            // 图集高级 / atlas advanced (folded)
            _showAtlas = EditorGUILayout.Foldout(_showAtlas, I18n.T("general.atlas.foldout"), true);
            if (_showAtlas)
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.IntPopup(_padding, PaddingLabels(), PaddingOptions, new GUIContent(I18n.T("general.paddingOption")));
                EditorGUILayout.PropertyField(_npot, new GUIContent(I18n.T("general.experimentalNpot")));
                if (_npot.boolValue)
                {
                    EditorGUILayout.HelpBox(I18n.T("general.npot.desc"), MessageType.Warning);
                }
                EditorGUILayout.PropertyField(_crunch, new GUIContent(I18n.T("general.crunch")));
                EditorGUI.indentLevel--;
            }

            // 导入设置（折叠） / import settings (folded)
            _showImport = EditorGUILayout.Foldout(_showImport, I18n.T("general.import.foldout"), true);
            if (_showImport)
            {
                EditorGUI.indentLevel++;
                DrawCategory(_opaqueImport, I18n.T("category.opaque"));
                DrawCategory(_transparentImport, I18n.T("category.transparent"));
                DrawCategory(_normalImport, I18n.T("category.normal"));
                DrawCategory(_grayscaleImport, I18n.T("category.grayscale"));
                EditorGUI.indentLevel--;
            }

            // 平台覆盖（勾选才显示） / platform overrides
            EditorGUILayout.PropertyField(_platformOverride, new GUIContent(I18n.T("general.platformOverrideEnabled")));
            if (_platformOverride.boolValue)
            {
                _showPlatform = EditorGUILayout.Foldout(_showPlatform, I18n.T("general.platform.foldout"), true);
                if (_showPlatform)
                {
                    EditorGUI.indentLevel++;
                    DrawPlatform(_pc, I18n.T("platform.PC"));
                    DrawPlatform(_android, I18n.T("platform.Android"));
                    DrawPlatform(_ios, I18n.T("platform.iOS"));
                    EditorGUI.indentLevel--;
                }
            }

            // 白名单 / whitelist
            _showWhitelist = EditorGUILayout.Foldout(_showWhitelist, I18n.T("general.whitelist"), true);
            if (_showWhitelist)
            {
                EditorGUILayout.HelpBox(I18n.T("general.whitelist.desc"), MessageType.Info);
                EditorGUILayout.PropertyField(_whitelist, new GUIContent(I18n.T("general.whitelist")), true);
            }

            // 其他 / misc
            EditorGUILayout.PropertyField(_verbose, new GUIContent(I18n.T("general.verboseLogging")));

            serializedObject.ApplyModifiedProperties();
        }

        private GUIContent[] DensityLabels()
        {
            var labels = new GUIContent[DensityOptions.Length];
            for (int i = 0; i < labels.Length; i++) labels[i] = new GUIContent(DensityOptions[i].ToString());
            return labels;
        }

        private GUIContent[] PaddingLabels()
        {
            var labels = new GUIContent[PaddingOptions.Length];
            for (int i = 0; i < labels.Length; i++) labels[i] = new GUIContent(PaddingOptions[i].ToString());
            return labels;
        }

        private void DrawCategory(SerializedProperty prop, string title)
        {
            EditorGUILayout.LabelField(title, EditorStyles.boldLabel);
            EditorGUI.indentLevel++;
            var format = prop.FindPropertyRelative("format");
            var mipmaps = prop.FindPropertyRelative("mipmaps");
            var maxSize = prop.FindPropertyRelative("maxSize");

            var names = System.Enum.GetNames(typeof(ATOCompressionFormat));
            var labels = new GUIContent[names.Length];
            for (int i = 0; i < names.Length; i++)
            {
                labels[i] = new GUIContent(I18n.T("format." + names[i]));
            }
            EditorGUILayout.IntPopup(format, labels, System.Array.ConvertAll(names, n => (int)System.Enum.Parse(typeof(ATOCompressionFormat), n)),
                new GUIContent(I18n.T("import.format")));
            EditorGUILayout.PropertyField(mipmaps, new GUIContent(I18n.T("import.mipmaps")));
            EditorGUILayout.PropertyField(maxSize, new GUIContent(I18n.T("import.maxSize")));
            EditorGUI.indentLevel--;
        }

        private void DrawPlatform(SerializedProperty prop, string title)
        {
            EditorGUILayout.LabelField(title, EditorStyles.boldLabel);
            EditorGUI.indentLevel++;
            EditorGUILayout.PropertyField(prop.FindPropertyRelative("maxAtlasSize"), new GUIContent(I18n.T("platform.maxAtlasSize")));
            EditorGUILayout.PropertyField(prop.FindPropertyRelative("allowNpot"), new GUIContent(I18n.T("platform.allowNpot")));
            DrawCategory(prop.FindPropertyRelative("opaque"), I18n.T("category.opaque"));
            DrawCategory(prop.FindPropertyRelative("transparent"), I18n.T("category.transparent"));
            DrawCategory(prop.FindPropertyRelative("normal"), I18n.T("category.normal"));
            DrawCategory(prop.FindPropertyRelative("grayscale"), I18n.T("category.grayscale"));
            EditorGUI.indentLevel--;
        }
    }
}
